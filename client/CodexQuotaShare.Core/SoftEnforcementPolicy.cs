namespace CodexQuotaShare.Core;

public static class SoftEnforcementPolicy
{
    // Exact executable allowlist; names alone never authorize process termination.
    public static bool ShouldStop(bool enabled, bool limited, int processId, int observerId,
        string? executable, IEnumerable<string> knownExecutables) =>
        enabled && limited && processId != observerId && executable is not null &&
        knownExecutables.Any(path => string.Equals(Path.GetFullPath(path), Path.GetFullPath(executable), StringComparison.OrdinalIgnoreCase));
}
