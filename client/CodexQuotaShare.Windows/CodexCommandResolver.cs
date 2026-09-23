using System.Diagnostics;
using CodexQuotaShare.Core;

namespace CodexQuotaShare.Windows;

internal static class CodexCommandResolver
{
    public static ProcessStartInfo Resolve(string? configured)
    {
        if (!string.IsNullOrWhiteSpace(configured)) return Command(configured);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var bin = Path.Combine(local, "OpenAI", "Codex", "bin");
        try
        {
            if (Directory.Exists(bin))
            {
                var candidates = Directory.EnumerateDirectories(bin).Select(p => Path.Combine(p, "codex.exe"))
                    .Append(Path.Combine(bin, "codex.exe")).Where(File.Exists)
                    .OrderByDescending(File.GetLastWriteTimeUtc).ToList();
                if (candidates.Count > 0) return Command(candidates[0]);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        try
        {
            foreach (var package in new global::Windows.Management.Deployment.PackageManager().FindPackagesForUser(""))
            {
                if (!package.Id.Name.Equals("OpenAI.Codex", StringComparison.OrdinalIgnoreCase)) continue;
                foreach (var relative in new[] { "app/resources/codex.exe", "app/resources/bin/codex.exe", "app/resources/codex/codex.exe" })
                {
                    var candidate = Path.Combine(package.InstalledLocation.Path, relative);
                    if (File.Exists(candidate)) return Command(candidate);
                }
            }
        }
        catch (Exception e) when (e is UnauthorizedAccessException or System.Runtime.InteropServices.COMException) { }
        foreach (var entry in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            var directory = entry.Trim().Trim('"');
            if (string.IsNullOrWhiteSpace(directory) || !Path.IsPathFullyQualified(directory)) continue;
            var candidate = Path.Combine(directory, "codex.exe");
            if (File.Exists(candidate)) return Command(candidate);
        }
        throw new QuotaUnavailableException("CODEX_NOT_FOUND");
    }

    private static ProcessStartInfo Command(string executable)
    {
        // No cmd.exe or command text: paths with spaces are safe and shell metacharacters are not executed.
        if (!Path.IsPathFullyQualified(executable) || !executable.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            || !File.Exists(executable)) throw new QuotaUnavailableException("CODEX_NOT_FOUND");
        var command = new ProcessStartInfo(executable);
        command.ArgumentList.Add("app-server");
        return command;
    }
}
