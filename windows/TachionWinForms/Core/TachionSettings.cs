using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Tachion.Core;

public sealed class TachionSettings
{
    public string SyncDir { get; set; } = @"D:\tachion";
    public string SyncUrl { get; set; } = "wss://tachion.example.com/ws";
    public string SyncName { get; set; } = Environment.MachineName;

    [JsonIgnore]
    public string SyncToken { get; set; } = "";

    // Stored in JSON, encrypted with Windows DPAPI for the current Windows user.
    public string? ProtectedSyncToken { get; set; }

    // Backward compatibility: old config files had plaintext "SyncToken".
    // We read it once, then Save() removes it and writes ProtectedSyncToken instead.
    [JsonPropertyName("SyncToken")]
    public string? LegacyPlainTextSyncToken { get; set; }

    public static string ConfigPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "tachion",
        "tachion.config.json");

    public static TachionSettings Load()
    {
        try
        {
            if (!File.Exists(ConfigPath)) return new TachionSettings();
            var json = File.ReadAllText(ConfigPath);
            var settings = JsonSerializer.Deserialize<TachionSettings>(json) ?? new TachionSettings();

            if (!string.IsNullOrWhiteSpace(settings.ProtectedSyncToken))
            {
                settings.SyncToken = SecretProtector.Unprotect(settings.ProtectedSyncToken);
            }
            else if (!string.IsNullOrWhiteSpace(settings.LegacyPlainTextSyncToken))
            {
                settings.SyncToken = settings.LegacyPlainTextSyncToken;
                settings.LegacyPlainTextSyncToken = null;
                settings.Save(); // migrate old plaintext config immediately
            }

            return settings;
        }
        catch
        {
            return new TachionSettings();
        }
    }

    public void Save()
    {
        var dir = Path.GetDirectoryName(ConfigPath)!;
        Directory.CreateDirectory(dir);

        ProtectedSyncToken = SecretProtector.Protect(SyncToken);
        LegacyPlainTextSyncToken = null;

        var json = JsonSerializer.Serialize(this, new JsonSerializerOptions
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        });
        File.WriteAllText(ConfigPath, json);
    }
}
