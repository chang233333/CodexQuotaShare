using System.Text.Json;
using System.Net.Http.Json;
using Microsoft.Win32;
namespace CodexQuotaShare.Windows;

internal static class ProductConfiguration
{
    private sealed record ReleaseConfiguration(string? DefaultRelayUrl, string? GitHubRepository);
    private static readonly ReleaseConfiguration Configuration = Read();
    private static ReleaseConfiguration Read()
    {
        try { return JsonSerializer.Deserialize<ReleaseConfiguration>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "release-config.json"))) ?? new(null, null); }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { return new(null, null); }
    }
    public static string? DefaultRelayUrl => Configuration.DefaultRelayUrl;
    public static string? Repository => Configuration.GitHubRepository;
    public static void SetStartup(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
        if (enabled) key.SetValue("CodexQuotaShare", "\"" + Environment.ProcessPath + "\"");
        else key.DeleteValue("CodexQuotaShare", false);
    }
    public static async Task<string?> CheckReleaseAsync()
    {
        if (string.IsNullOrWhiteSpace(Repository)) return "此候选版本尚未配置 GitHub 发布仓库。";
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("CodexQuotaShare/0.1.0");
        try
        {
            using var document = await client.GetFromJsonAsync<JsonDocument>($"https://api.github.com/repos/{Repository}/releases/latest");
            var tag = document?.RootElement.GetProperty("tag_name").GetString();
            return "GitHub 最新版本：" + tag;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException) { return "暂时无法检查更新。"; }
    }
}
