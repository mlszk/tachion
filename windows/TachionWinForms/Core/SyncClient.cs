using System.Collections.Concurrent;
using System.IO;
using System.Net.WebSockets;
using System.Net.Http.Headers;
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
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _pendingUploads = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _pendingDeletes = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DateTime> _recentRemoteDeletes = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DateTime> _activeFolderImports = new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan UnableToConnectStopAfter = TimeSpan.FromMinutes(30);
    private DateTime? _unableToConnectSinceUtc;

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
        CancelPendingUploads();
        CancelPendingDeletes();
        try { _watcher?.Dispose(); } catch { }
        try { _ws?.Abort(); _ws?.Dispose(); } catch { }
        _unableToConnectSinceUtc = null;
        _watcher = null;
        _ws = null;
        _log("Stopped.");
    }

    private void CancelPendingUploads()
    {
        foreach (var item in _pendingUploads)
        {
            try { item.Value.Cancel(); } catch { }
            try { item.Value.Dispose(); } catch { }
        }
        _pendingUploads.Clear();
    }

    private void CancelPendingDeletes()
    {
        foreach (var item in _pendingDeletes)
        {
            try { item.Value.Cancel(); } catch { }
            try { item.Value.Dispose(); } catch { }
        }
        _pendingDeletes.Clear();
    }

    private void StartWatcher()
    {
        _watcher = new FileSystemWatcher(_settings.SyncDir)
        {
            IncludeSubdirectories = true,
            EnableRaisingEvents = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.CreationTime,
            InternalBufferSize = 64 * 1024
        };
        _watcher.Created += (_, e) =>
        {
            if (Directory.Exists(e.FullPath)) QueueFolderUpload(e.FullPath);
            else QueueUpload(e.FullPath);
        };
        _watcher.Changed += (_, e) => QueueUpload(e.FullPath);
        _watcher.Deleted += (_, e) => QueueDelete(e.FullPath);
        _watcher.Renamed += (_, e) =>
        {
            QueueDelete(e.OldFullPath);
            if (Directory.Exists(e.FullPath)) QueueFolderUpload(e.FullPath);
            else QueueUpload(e.FullPath);
        };
        _watcher.Error += (_, e) =>
        {
            _log("File watcher missed events, doing full rescan: " + e.GetException().Message);
            QueueFullRescan();
        };
    }

    private static bool ShouldIgnoreFile(string pathOrRel)
    {
        var name = Path.GetFileName(pathOrRel);
        if (string.IsNullOrWhiteSpace(name)) return true;
        if (name.Contains(".tmp.", StringComparison.OrdinalIgnoreCase)) return true;
        if (name.StartsWith("~$", StringComparison.OrdinalIgnoreCase)) return true; // Office lock files

        var ext = Path.GetExtension(name);
        if (ext.Equals(".dwl", StringComparison.OrdinalIgnoreCase)) return true;   // AutoCAD/DraftSight lock files
        if (ext.Equals(".dwl2", StringComparison.OrdinalIgnoreCase)) return true;
        if (ext.Equals(".swp", StringComparison.OrdinalIgnoreCase)) return true;   // editor swap files
        return false;
    }

    private static bool IsSameOrChildRel(string rel, string parentRel)
    {
        rel = rel.Replace("\\", "/").Trim('/');
        parentRel = parentRel.Replace("\\", "/").Trim('/');
        return rel.Equals(parentRel, StringComparison.OrdinalIgnoreCase)
            || rel.StartsWith(parentRel + "/", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeFullPath(string path)
    {
        return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static bool IsSameOrChildFullPath(string fullPath, string parentFullPath)
    {
        fullPath = NormalizeFullPath(fullPath);
        parentFullPath = NormalizeFullPath(parentFullPath);
        return fullPath.Equals(parentFullPath, StringComparison.OrdinalIgnoreCase)
            || fullPath.StartsWith(parentFullPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || fullPath.StartsWith(parentFullPath + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private bool IsUnderActiveFolderImport(string fullPath)
    {
        if (_activeFolderImports.IsEmpty) return false;

        var now = DateTime.UtcNow;
        foreach (var item in _activeFolderImports.ToArray())
        {
            if (now >= item.Value)
            {
                _activeFolderImports.TryRemove(item.Key, out _);
                continue;
            }

            try
            {
                if (IsSameOrChildFullPath(fullPath, item.Key))
                    return true;
            }
            catch
            {
                // Ignore malformed/racing paths.
            }
        }

        return false;
    }

    private bool HasPendingUploadsForRelTree(string relRoot)
    {
        return _pendingUploads.Keys.Any(rel => IsSameOrChildRel(rel, relRoot));
    }

    private bool HasRecentRemoteDelete(string rel)
    {
        foreach (var item in _recentRemoteDeletes)
        {
            if (DateTime.UtcNow < item.Value && IsSameOrChildRel(rel, item.Key))
                return true;
        }
        return false;
    }

    private void CancelPendingUploadsForRelTree(string rel)
    {
        foreach (var item in _pendingUploads.Keys.ToList())
        {
            if (!IsSameOrChildRel(item, rel)) continue;
            if (_pendingUploads.TryRemove(item, out var pendingUpload))
            {
                try { pendingUpload.Cancel(); } catch { }
                try { pendingUpload.Dispose(); } catch { }
            }
        }
    }

    private void QueueFolderUpload(string folderPath)
    {
        if (_cts == null || _cts.IsCancellationRequested) return;

        string folderRoot;
        try
        {
            folderRoot = NormalizeFullPath(folderPath);
        }
        catch
        {
            return;
        }

        _activeFolderImports[folderRoot] = DateTime.UtcNow.AddMinutes(5);
        var linked = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);

        _ = Task.Run(async () =>
        {
            var snapshot = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string? previousTreeSignature = null;
            var stableRounds = 0;
            var folderName = Path.GetFileName(folderRoot);

            try
            {
                _log($"Bulk folder import detected {folderName}; waiting until folder tree is stable...");

                // A copied folder tree is not atomic on Windows. Treat the root folder
                // as one import job: suppress noisy child watcher events, wait until
                // the tree stops changing, then queue the complete file list.
                for (var round = 1; round <= 240; round++) // about 120 seconds max
                {
                    linked.Token.ThrowIfCancellationRequested();
                    if (!Directory.Exists(folderRoot)) return;

                    var files = Directory.EnumerateFiles(folderRoot, "*", SearchOption.AllDirectories)
                        .Where(file => !ShouldIgnoreFile(file))
                        .OrderBy(file => file, StringComparer.OrdinalIgnoreCase)
                        .ToList();

                    snapshot = BuildFolderSnapshot(files);
                    var treeSignature = BuildFolderSignature(snapshot);

                    if (treeSignature == previousTreeSignature)
                    {
                        stableRounds++;
                        if (stableRounds >= 4)
                            break;
                    }
                    else
                    {
                        stableRounds = 0;
                        previousTreeSignature = treeSignature;
                    }

                    if (round % 20 == 0)
                        _log($"Bulk import still watching {folderName}: {snapshot.Count} file(s) visible so far...");

                    await Task.Delay(500, linked.Token);
                }

                if (!Directory.Exists(folderRoot)) return;

                var finalFiles = snapshot.Keys
                    .Where(File.Exists)
                    .OrderBy(file => file, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                if (finalFiles.Count == 0)
                {
                    _log($"Bulk import complete {folderName}: no files found.");
                    return;
                }

                _log($"Bulk import ready {folderName}: {finalFiles.Count} file(s) found; queueing uploads...");

                var queued = 0;
                foreach (var file in finalFiles)
                {
                    linked.Token.ThrowIfCancellationRequested();
                    QueueUpload(file, fromBulkImport: true);
                    queued++;
                    if (queued % 50 == 0)
                        _log($"Bulk import queued {folderName}: {queued}/{finalFiles.Count} file(s)...");
                }

                _log($"Bulk import queued {folderName}: {queued}/{finalFiles.Count} file(s). Waiting for upload queue to settle...");
                await VerifyBulkImportAsync(folderRoot, finalFiles, linked.Token);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                _log("Bulk folder import skipped " + folderPath + ": " + ex.Message);
            }
            finally
            {
                _activeFolderImports.TryRemove(folderRoot, out _);
                try { linked.Dispose(); } catch { }
            }
        });
    }

    private static Dictionary<string, string> BuildFolderSnapshot(IEnumerable<string> files)
    {
        var snapshot = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
        {
            try
            {
                var info = new FileInfo(file);
                if (!info.Exists) continue;
                snapshot[file] = info.Length + "|" + info.LastWriteTimeUtc.Ticks;
            }
            catch
            {
                // File may be mid-copy/deleted/renamed between enumeration and stat.
                // That means the tree is not stable yet, so include a changing marker.
                snapshot[file] = "changing|" + DateTime.UtcNow.Ticks;
            }
        }
        return snapshot;
    }

    private static string BuildFolderSignature(Dictionary<string, string> snapshot)
    {
        return string.Join("\n", snapshot
            .OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase)
            .Select(item => item.Key + "|" + item.Value));
    }

    private sealed class ServerFileInfo
    {
        public long Size { get; init; }
        public long MtimeNs { get; init; }
    }

    private async Task VerifyBulkImportAsync(string folderRoot, List<string> originalFiles, CancellationToken token)
    {
        string folderName;
        string folderRel;
        try
        {
            folderName = Path.GetFileName(folderRoot);
            folderRel = PathUtil.RelativeUnixPath(_settings.SyncDir, folderRoot);
        }
        catch
        {
            return;
        }

        // Let the normal per-file upload queue finish first. This keeps the bulk
        // feature compatible with the existing send/retry logic.
        for (var i = 1; i <= 180; i++) // up to about 3 minutes
        {
            token.ThrowIfCancellationRequested();
            if (!HasPendingUploadsForRelTree(folderRel)) break;
            if (i % 15 == 0)
                _log($"Bulk import waiting {folderName}: upload queue still active...");
            await Task.Delay(1000, token);
        }

        Dictionary<string, ServerFileInfo>? serverFiles;
        try
        {
            serverFiles = await FetchServerFileListAsync(token);
        }
        catch (Exception ex)
        {
            _log($"Bulk import verify skipped {folderName}: {ex.Message}");
            return;
        }

        if (serverFiles is null)
        {
            _log($"Bulk import verify skipped {folderName}: server file list unavailable.");
            return;
        }

        var missingOrOld = new List<string>();
        foreach (var file in originalFiles)
        {
            token.ThrowIfCancellationRequested();
            if (!File.Exists(file)) continue;
            if (ShouldIgnoreFile(file)) continue;

            string rel;
            FileInfo info;
            long localMtimeNs;
            try
            {
                rel = PathUtil.RelativeUnixPath(_settings.SyncDir, file);
                info = new FileInfo(file);
                localMtimeNs = TimeUtil.FileMtimeNs(file);
            }
            catch
            {
                continue;
            }

            if (!serverFiles.TryGetValue(rel, out var remote)
                || remote.Size != info.Length
                || remote.MtimeNs < localMtimeNs)
            {
                missingOrOld.Add(file);
            }
        }

        if (missingOrOld.Count == 0)
        {
            _log($"Bulk import verified {folderName}: {originalFiles.Count} file(s) present on server.");
            return;
        }

        _log($"Bulk import verify found {missingOrOld.Count} missing/old file(s) in {folderName}; re-queueing...");
        foreach (var file in missingOrOld)
        {
            token.ThrowIfCancellationRequested();
            QueueUpload(file, fromBulkImport: true);
        }
    }

    private async Task<Dictionary<string, ServerFileInfo>?> FetchServerFileListAsync(CancellationToken token)
    {
        var uri = BuildRestFilesUri();
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _settings.SyncToken);

        using var response = await http.SendAsync(request, token);
        if (!response.IsSuccessStatusCode)
            throw new IOException($"server returned HTTP {(int)response.StatusCode}");

        await using var stream = await response.Content.ReadAsStreamAsync(token);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: token);

        var result = new Dictionary<string, ServerFileInfo>(StringComparer.OrdinalIgnoreCase);
        var root = doc.RootElement;

        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("files", out var filesElement))
        {
            foreach (var item in filesElement.EnumerateArray())
            {
                var path = item.TryGetProperty("path", out var p) ? p.GetString() : null;
                if (string.IsNullOrWhiteSpace(path)) continue;
                var size = item.TryGetProperty("size", out var s) && s.TryGetInt64(out var sv) ? sv : 0;
                var mtime = item.TryGetProperty("mtime_ns", out var m) && m.TryGetInt64(out var mv) ? mv : 0;
                result[path.Replace("\\", "/").Trim('/')] = new ServerFileInfo { Size = size, MtimeNs = mtime };
            }
            return result;
        }

        // Backward-compatible support for older manifest shape: { "path": mtime_ns }.
        if (root.ValueKind == JsonValueKind.Object)
        {
            foreach (var item in root.EnumerateObject())
            {
                if (item.Value.TryGetInt64(out var mtime))
                    result[item.Name.Replace("\\", "/").Trim('/')] = new ServerFileInfo { Size = -1, MtimeNs = mtime };
            }
            return result;
        }

        return null;
    }

    private Uri BuildRestFilesUri()
    {
        var wsUri = new Uri(_settings.SyncUrl);
        var builder = new UriBuilder(wsUri)
        {
            Scheme = wsUri.Scheme.Equals("wss", StringComparison.OrdinalIgnoreCase) ? "https" : "http",
            Query = ""
        };

        var path = wsUri.AbsolutePath;
        if (path.EndsWith("/ws", StringComparison.OrdinalIgnoreCase))
            path = path[..^3];
        path = path.TrimEnd('/') + "/files";
        builder.Path = path;
        return builder.Uri;
    }

    private void QueueFullRescan()
    {
        if (_cts == null || _cts.IsCancellationRequested) return;

        var linked = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(1500, linked.Token);
                if (!Directory.Exists(_settings.SyncDir)) return;

                foreach (var file in Directory.EnumerateFiles(_settings.SyncDir, "*", SearchOption.AllDirectories))
                {
                    linked.Token.ThrowIfCancellationRequested();
                    QueueUpload(file);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                _log("Full rescan skipped: " + ex.Message);
            }
            finally
            {
                try { linked.Dispose(); } catch { }
            }
        });
    }

    private void QueueUpload(string fullPath, bool fromBulkImport = false)
    {
        if (_cts == null || _cts.IsCancellationRequested) return;
        if (!fromBulkImport && IsUnderActiveFolderImport(fullPath)) return;
        if (Directory.Exists(fullPath)) return;
        if (ShouldIgnoreFile(fullPath)) return;

        var rel = PathUtil.RelativeUnixPath(_settings.SyncDir, fullPath);
        if (_recentRemoteWrites.TryGetValue(rel, out var until) && DateTime.UtcNow < until)
            return;
        if (HasRecentRemoteDelete(rel))
            return;

        var linked = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        if (_pendingUploads.TryRemove(rel, out var old))
        {
            try { old.Cancel(); } catch { }
            try { old.Dispose(); } catch { }
        }
        _pendingUploads[rel] = linked;

        _ = Task.Run(() => DelayedUploadAsync(rel, fullPath, linked));
    }

    private void QueueDelete(string fullPath)
    {
        if (_cts == null || _cts.IsCancellationRequested) return;
        if (ShouldIgnoreFile(fullPath)) return;

        var rel = PathUtil.RelativeUnixPath(_settings.SyncDir, fullPath);
        if (HasRecentRemoteDelete(rel))
            return;

        // If a whole folder is deleted, cancel queued uploads for all children too.
        CancelPendingUploadsForRelTree(rel);

        var linked = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        if (_pendingDeletes.TryRemove(rel, out var old))
        {
            try { old.Cancel(); } catch { }
            try { old.Dispose(); } catch { }
        }
        _pendingDeletes[rel] = linked;

        _ = Task.Run(() => DelayedDeleteAsync(rel, fullPath, linked));
    }

    private async Task DelayedDeleteAsync(string rel, string fullPath, CancellationTokenSource deleteCts)
    {
        var token = deleteCts.Token;
        try
        {
            // Debounce: some programs implement save as delete+create/rename.
            // If the file immediately appears again, treat it as an update, not deletion.
            await Task.Delay(500, token);

            if (File.Exists(fullPath) || Directory.Exists(fullPath)) return;
            if (HasRecentRemoteDelete(rel)) return;

            var deleteNs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1_000_000L;
            for (var attempt = 1; attempt <= 20; attempt++)
            {
                token.ThrowIfCancellationRequested();
                var sent = await SendDeleteAsync(rel, deleteNs, token);
                if (sent)
                {
                    _log("Deleted " + rel);
                    return;
                }
                await Task.Delay(500, token);
            }

            _log($"Delete queued but not sent {rel}: not connected");
        }
        catch (OperationCanceledException)
        {
            // Normal when another event for the same file arrives or sync stops.
        }
        catch (Exception ex)
        {
            _log($"Delete skipped {rel}: {ex.Message}");
        }
        finally
        {
            if (_pendingDeletes.TryGetValue(rel, out var current) && ReferenceEquals(current, deleteCts))
                _pendingDeletes.TryRemove(rel, out _);
            try { deleteCts.Dispose(); } catch { }
        }
    }

    private async Task DelayedUploadAsync(string rel, string fullPath, CancellationTokenSource uploadCts)
    {
        var token = uploadCts.Token;
        var loggedWaiting = false;
        try
        {
            // Debounce: many apps write the same file several times while saving.
            await Task.Delay(800, token);

            // Try for up to about two minutes. CAD/editors often keep files locked while saving.
            for (var attempt = 1; attempt <= 90; attempt++)
            {
                token.ThrowIfCancellationRequested();

                if (!File.Exists(fullPath)) return;
                if (_recentRemoteWrites.TryGetValue(rel, out var until) && DateTime.UtcNow < until) return;
                if (HasRecentRemoteDelete(rel)) return;

                try
                {
                    await SendPutAsync(rel, token);
                    return;
                }
                catch (IOException ex) when (attempt < 90)
                {
                    if (!loggedWaiting)
                    {
                        _log($"Waiting for file to unlock {rel}: {ex.Message}");
                        loggedWaiting = true;
                    }
                    await Task.Delay(Math.Min(250 + attempt * 100, 2000), token);
                }
                catch (UnauthorizedAccessException ex) when (attempt < 90)
                {
                    if (!loggedWaiting)
                    {
                        _log($"Waiting for file access {rel}: {ex.Message}");
                        loggedWaiting = true;
                    }
                    await Task.Delay(Math.Min(250 + attempt * 100, 2000), token);
                }
            }

            _log($"Upload skipped {rel}: file stayed locked too long");
        }
        catch (OperationCanceledException)
        {
            // Normal when another change for the same file arrives or sync stops.
        }
        catch (Exception ex)
        {
            _log($"Upload skipped {rel}: {ex.Message}");
        }
        finally
        {
            if (_pendingUploads.TryGetValue(rel, out var current) && ReferenceEquals(current, uploadCts))
                _pendingUploads.TryRemove(rel, out _);
            try { uploadCts.Dispose(); } catch { }
        }
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
                if (_unableToConnectSinceUtc.HasValue)
                    _log("Connection restored; offline stop timer reset.");
                _unableToConnectSinceUtc = null;
                _log("Connected.");

                await SendHelloAsync(token);
                await ReceiveLoopAsync(token);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _log("Connection error: " + ex.Message);

                if (ShouldStopAfterUnableToConnectTimeout(ex))
                {
                    _log("Unable to connect to the remote server for 30 minutes. Sync stopped to allow Windows sleep/hibernate.");
                    Stop();
                    break;
                }

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
            if (ShouldIgnoreFile(file)) continue;
            try
            {
                var rel = PathUtil.RelativeUnixPath(_settings.SyncDir, file);
                result[rel] = TimeUtil.FileMtimeNs(file);
            }
            catch (IOException)
            {
                // Ignore locked files in the hello manifest; watcher retry will send them later if needed.
            }
            catch (UnauthorizedAccessException)
            {
            }
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

    private bool ShouldStopAfterUnableToConnectTimeout(Exception ex)
    {
        if (!ExceptionMessageContains(ex, "Unable to connect to the remote server"))
        {
            _unableToConnectSinceUtc = null;
            return false;
        }

        var now = DateTime.UtcNow;
        if (!_unableToConnectSinceUtc.HasValue)
        {
            _unableToConnectSinceUtc = now;
            _log("Unable-to-connect timer started; sync will stop after 30 minutes if the server stays unreachable.");
            return false;
        }

        return now - _unableToConnectSinceUtc.Value >= UnableToConnectStopAfter;
    }

    private static bool ExceptionMessageContains(Exception ex, string text)
    {
        for (var current = ex; current != null; current = current.InnerException)
        {
            if (current.Message.Contains(text, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
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
        else if (type == "delete")
        {
            ApplyDelete(root);
        }
        else if (type == "request")
        {
            foreach (var p in root.GetProperty("paths").EnumerateArray())
            {
                var rel = p.GetString();
                if (string.IsNullOrWhiteSpace(rel) || ShouldIgnoreFile(rel)) continue;

                try
                {
                    await SendPutAsync(rel, token);
                }
                catch (IOException ex)
                {
                    _log($"Requested file is locked, queued {rel}: {ex.Message}");
                    QueueUpload(PathUtil.SafeFullPath(_settings.SyncDir, rel));
                }
                catch (UnauthorizedAccessException ex)
                {
                    _log($"Requested file inaccessible, queued {rel}: {ex.Message}");
                    QueueUpload(PathUtil.SafeFullPath(_settings.SyncDir, rel));
                }
            }
        }
    }

    private void ApplyDelete(JsonElement m)
    {
        var rel = m.GetProperty("path").GetString() ?? "";
        if (ShouldIgnoreFile(rel)) return;

        var full = PathUtil.SafeFullPath(_settings.SyncDir, rel);
        _recentRemoteDeletes[rel] = DateTime.UtcNow.AddSeconds(5);
        CancelPendingUploadsForRelTree(rel);

        try
        {
            if (Directory.Exists(full))
            {
                Directory.Delete(full, recursive: true);
                _log("Deleted remote folder " + rel);
            }
            else if (File.Exists(full))
            {
                File.Delete(full);
                _log("Deleted remote " + rel);
            }

            // Clean up empty parent folders, but never remove the sync root itself.
            var root = Path.GetFullPath(_settings.SyncDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var parent = Directory.GetParent(full);
            while (parent != null && !string.Equals(parent.FullName.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), root, StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    if (Directory.EnumerateFileSystemEntries(parent.FullName).Any()) break;
                    parent.Delete();
                    parent = parent.Parent;
                }
                catch
                {
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            _log($"Remote delete failed {rel}: {ex.Message}");
        }
    }

    private async Task ApplyPutAsync(JsonElement m)
    {
        var rel = m.GetProperty("path").GetString() ?? "";
        if (ShouldIgnoreFile(rel)) return;

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

    private async Task<byte[]> ReadAllBytesSharedAsync(string full, CancellationToken token)
    {
        await using var fs = new FileStream(
            full,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 64 * 1024,
            options: FileOptions.SequentialScan | FileOptions.Asynchronous);

        var length = fs.Length;
        if (length > int.MaxValue) throw new IOException("file is too large for this simple whole-file sync client");
        var data = new byte[length];
        var offset = 0;
        while (offset < data.Length)
        {
            var read = await fs.ReadAsync(data.AsMemory(offset), token);
            if (read == 0) break;
            offset += read;
        }
        if (offset != data.Length) throw new IOException("file changed while reading");
        return data;
    }

    private async Task<bool> SendDeleteAsync(string rel, long deleteNs, CancellationToken token)
    {
        if (ShouldIgnoreFile(rel)) return false;

        var msg = new Dictionary<string, object?>
        {
            ["type"] = "delete",
            ["path"] = rel,
            ["delete_ns"] = deleteNs,
            ["mtime_ns"] = deleteNs
        };
        return await SendJsonAsync(msg, token);
    }

    private async Task SendPutAsync(string rel, CancellationToken token)
    {
        if (ShouldIgnoreFile(rel)) return;
        var full = PathUtil.SafeFullPath(_settings.SyncDir, rel);
        if (!File.Exists(full)) return;

        byte[] data;
        long mtimeNs;

        for (var i = 0; ; i++)
        {
            try
            {
                var info1 = new FileInfo(full);
                var len1 = info1.Length;
                var mt1 = info1.LastWriteTimeUtc;

                // Make sure the file is not still being written.
                await Task.Delay(150, token);

                var info2 = new FileInfo(full);
                if (info2.Length != len1 || info2.LastWriteTimeUtc != mt1)
                    throw new IOException("file is still changing");

                data = await ReadAllBytesSharedAsync(full, token);

                var info3 = new FileInfo(full);
                if (info3.Length != info2.Length || info3.LastWriteTimeUtc != info2.LastWriteTimeUtc)
                    throw new IOException("file changed while reading");

                mtimeNs = TimeUtil.FileMtimeNs(full);
                break;
            }
            catch (IOException) when (i < 5)
            {
                await Task.Delay(200, token);
            }
        }

        var msg = new Dictionary<string, object?>
        {
            ["type"] = "put",
            ["path"] = rel,
            ["mtime_ns"] = mtimeNs,
            ["hash"] = HashUtil.Sha256Hex(data),
            ["data"] = Convert.ToBase64String(data)
        };
        var sent = await SendJsonAsync(msg, token);
        if (sent) _log("Sent " + rel);
    }

    private async Task<bool> SendJsonAsync(object msg, CancellationToken token)
    {
        if (_ws is not { State: WebSocketState.Open }) return false;
        var json = JsonSerializer.Serialize(msg);
        var bytes = Encoding.UTF8.GetBytes(json);
        await _sendLock.WaitAsync(token);
        try
        {
            if (_ws is not { State: WebSocketState.Open }) return false;
            await _ws.SendAsync(bytes, WebSocketMessageType.Text, true, token);
            return true;
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
