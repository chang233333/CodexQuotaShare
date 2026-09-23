using System.Text.Json;
using CodexQuotaShare.Core;

namespace CodexQuotaShare.Windows;

internal sealed record LocalSettings(string? CodexExecutable = null, bool DebugLogging = false, string? RelayUrl = null,
    bool LaunchAtStartup = false, bool DesktopNotifications = true, bool SoftEnforcement = true, string? DeviceName = null);

internal static class SettingsStore
{
    private static string DirectoryPath => Path.Combine(AppContext.BaseDirectory, "data");
    private static string SettingsPath => Path.Combine(DirectoryPath, "settings.json");
    public static LocalSettings Load()
    {
        try { var value = JsonSerializer.Deserialize<LocalSettings>(File.ReadAllText(SettingsPath)) ?? new(); return value with { RelayUrl = value.RelayUrl ?? ProductConfiguration.DefaultRelayUrl }; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return new(RelayUrl: ProductConfiguration.DefaultRelayUrl); }
    }
    public static void Save(LocalSettings settings)
    {
        Directory.CreateDirectory(DirectoryPath);
        var temp = SettingsPath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(settings));
        File.Move(temp, SettingsPath, true);
    }
    public static QuotaSnapshot? LoadQuota()
    {
        try { var q = JsonSerializer.Deserialize<QuotaSnapshot>(File.ReadAllText(Path.Combine(DirectoryPath, "quota-cache.json"))); return q is { WeeklyUsedPercent: >= 0 and <= 100 } ? q : null; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }
    public static void SaveQuota(QuotaSnapshot quota)
    {
        try
        {
            Directory.CreateDirectory(DirectoryPath);
            var path = Path.Combine(DirectoryPath, "quota-cache.json");
            File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(quota)); File.Move(path + ".tmp", path, true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
    public static void Log(bool enabled, string code)
    {
        if (!enabled) return;
        // Callers supply only fixed event codes, never exceptions, RPC data or executable paths.
        try
        {
            Directory.CreateDirectory(DirectoryPath);
            var path = Path.Combine(DirectoryPath, "diagnostics.jsonl");
            if (File.Exists(path) && new FileInfo(path).Length > 256_000) File.Move(path, path + ".old", true);
            File.AppendAllText(path, JsonSerializer.Serialize(new { at = DateTimeOffset.UtcNow, code }) + Environment.NewLine);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
