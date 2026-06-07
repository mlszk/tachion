using System.Collections.Concurrent;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace Tachion.Core;

public sealed class SyncClient : IDisposable
{
    private readonly TachionSettings _settings;
    private readonly Action<string> _log;
    private CancellationTokenSource? _cts;
    private FileSystemWatcher? _watcher;
    private ClientWebSocket? _ws;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly ConcurrentDictionary<string, DateTime> _recentRemoteWrites = new(StringComparer.OrdinalIgnoreCase);

    public bool IsRunning => _cts is { IsCancellationRequested: false };

    public SyncClient(TachionSettings settings, Action<string> log)
    {
        _settings = settings;
        _log = log;
    }

    public void Start()
    {
        if (IsRunning) return;
        Directory.CreateDirectory(_settings.SyncDir);
        _cts = new CancellationTokenSource();
        StartWatcher();
        _ = Task.Run(() => RunLoopAsync(_cts.Token));
    }

    public void Stop()
    {
        try { _cts?.Cancel(); } catch { }
        try { _watcher?.Dispose(); } catch { }
        try { _ws?.Abort(); _ws?.Dispose(); } catch { }
        _watcher = null;
        _ws = null;
        _log("Stopped.");
    }

    private void StartWatcher()
    {
        _watcher = new FileSystemWatcher(_settings.SyncDir)
        {
            IncludeSubdirectories = true,
            EnableRaisingEvents = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.CreationTime
        };
        _watcher.Created += (_, e) => QueueUpload(e.FullPath);
        _watcher.Changed += (_, e) => QueueUpload(e.FullPath);
        _watcher.Renamed += (_, e) => QueueUpload(e.FullPath);
    }

    private void QueueUpload(string fullPath)
    {
        if (_cts == null || _cts.IsCancellationRequested) return;
        if (Directory.Exists(fullPath)) return;
        if (Path.GetFileName(fullPath).Contains(".tmp.", StringComparison.OrdinalIgnoreCase)) return;

        var rel = PathUtil.RelativeUnixPath(_settings.SyncDir, fullPath);
        if (_recentRemoteWrites.TryGetValue(rel, out var until) && DateTime.UtcNow < until)
            return;

        _ = Task.Run(async () =>
        {
            await Task.Delay(300);
            try
            {
                if (File.Exists(fullPath)) await SendPutAsync(rel, _cts.Token);
            }
            catch (Exception ex)
            {
                _log($"Upload skipped {rel}: {ex.Message}");
            }
        });
    }

    private async Task RunLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                using var ws = new ClientWebSocket();
                _ws = ws;
                _log("Connecting...");
                await ws.ConnectAsync(new Uri(_settings.SyncUrl), token);
                _log("Connected.");

                await SendHelloAsync(token);
                await ReceiveLoopAsync(token);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _log("Connection error: " + ex.Message);
                try { await Task.Delay(3000, token); } catch { break; }
            }
            finally
            {
                try { _ws?.Dispose(); } catch { }
                _ws = null;
            }
        }
    }

    private Dictionary<string, long> BuildManifest()
    {
        var result = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(_settings.SyncDir)) return result;
        foreach (var file in Directory.EnumerateFiles(_settings.SyncDir, "*", SearchOption.AllDirectories))
        {
            if (Path.GetFileName(file).Contains(".tmp.", StringComparison.OrdinalIgnoreCase)) continue;
            var rel = PathUtil.RelativeUnixPath(_settings.SyncDir, file);
            result[rel] = TimeUtil.FileMtimeNs(file);
        }
        return result;
    }

    private async Task SendHelloAsync(CancellationToken token)
    {
        var hello = new Dictionary<string, object?>
        {
            ["type"] = "hello",
            ["token"] = _settings.SyncToken,
            ["name"] = _settings.SyncName,
            ["manifest"] = BuildManifest()
        };
        await SendJsonAsync(hello, token);
    }

    private async Task ReceiveLoopAsync(CancellationToken token)
    {
        var buffer = new byte[1024 * 1024];
        while (!token.IsCancellationRequested && _ws is { State: WebSocketState.Open })
        {
            using var ms = new MemoryStream();
            WebSocketReceiveResult result;
            do
            {
                result = await _ws.ReceiveAsync(buffer, token);
                if (result.MessageType == WebSocketMessageType.Close) return;
                ms.Write(buffer, 0, result.Count);
            } while (!result.EndOfMessage);

            var json = Encoding.UTF8.GetString(ms.ToArray());
            await HandleMessageAsync(json, token);
        }
    }

    private async Task HandleMessageAsync(string json, CancellationToken token)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var type = root.GetProperty("type").GetString();
        if (type == "put")
        {
            await ApplyPutAsync(root);
        }
        else if (type == "request")
        {
            foreach (var p in root.GetProperty("paths").EnumerateArray())
            {
                var rel = p.GetString();
                if (!string.IsNullOrWhiteSpace(rel)) await SendPutAsync(rel, token);
            }
        }
    }

    private async Task ApplyPutAsync(JsonElement m)
    {
        var rel = m.GetProperty("path").GetString() ?? "";
        var mtimeNs = m.GetProperty("mtime_ns").GetInt64();
        var expectedHash = m.TryGetProperty("hash", out var h) ? h.GetString() : null;
        var data64 = m.GetProperty("data").GetString() ?? "";
        var data = Convert.FromBase64String(data64);
        if (expectedHash != null && HashUtil.Sha256Hex(data) != expectedHash)
        {
            _log($"Hash mismatch, dropped {rel}");
            return;
        }

        var full = PathUtil.SafeFullPath(_settings.SyncDir, rel);
        if (File.Exists(full) && TimeUtil.FileMtimeNs(full) >= mtimeNs) return;

        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        var tmp = full + ".tmp." + Environment.ProcessId;
        await File.WriteAllBytesAsync(tmp, data);
        if (File.Exists(full)) File.Delete(full);
        File.Move(tmp, full);
        TimeUtil.SetMtimeNs(full, mtimeNs);
        _recentRemoteWrites[rel] = DateTime.UtcNow.AddSeconds(2);
        _log("Received " + rel);
    }

    private async Task SendPutAsync(string rel, CancellationToken token)
    {
        var full = PathUtil.SafeFullPath(_settings.SyncDir, rel);
        if (!File.Exists(full)) return;
        byte[] data;
        for (var i = 0; ; i++)
        {
            try { data = await File.ReadAllBytesAsync(full, token); break; }
            catch (IOException) when (i < 5) { await Task.Delay(200, token); }
        }
        var mtimeNs = TimeUtil.FileMtimeNs(full);
        var msg = new Dictionary<string, object?>
        {
            ["type"] = "put",
            ["path"] = rel,
            ["mtime_ns"] = mtimeNs,
            ["hash"] = HashUtil.Sha256Hex(data),
            ["data"] = Convert.ToBase64String(data)
        };
        await SendJsonAsync(msg, token);
        _log("Sent " + rel);
    }

    private async Task SendJsonAsync(object msg, CancellationToken token)
    {
        if (_ws is not { State: WebSocketState.Open }) return;
        var json = JsonSerializer.Serialize(msg);
        var bytes = Encoding.UTF8.GetBytes(json);
        await _sendLock.WaitAsync(token);
        try
        {
            await _ws.SendAsync(bytes, WebSocketMessageType.Text, true, token);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    public void Dispose()
    {
        Stop();
        _sendLock.Dispose();
        _cts?.Dispose();
    }
}
