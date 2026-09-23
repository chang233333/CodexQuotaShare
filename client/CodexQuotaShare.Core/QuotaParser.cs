using System.Text.Json;

namespace CodexQuotaShare.Core;

public static class QuotaParser
{
    public static QuotaSnapshot Parse(JsonElement result, DateTimeOffset observedAt)
    {
        if (result.ValueKind != JsonValueKind.Object) throw Unavailable("INVALID_QUOTA_RESPONSE");
        JsonElement snapshot;
        if (result.TryGetProperty("rateLimits", out var direct) && direct.ValueKind == JsonValueKind.Object
            && IsAccountLimit(direct)) snapshot = direct;
        else if (result.TryGetProperty("rateLimitsByLimitId", out var byId)
                 && byId.ValueKind == JsonValueKind.Object
                 && byId.TryGetProperty("codex", out var codex) && IsAccountLimit(codex)) snapshot = codex;
        else if (result.TryGetProperty("limitId", out var id) && id.ValueKind == JsonValueKind.String
                 && id.GetString() == "codex") snapshot = result;
        else throw Unavailable("ACCOUNT_QUOTA_UNAVAILABLE");

        var windows = new List<JsonElement>();
        foreach (var key in new[] { "primary", "secondary" })
        {
            if (!snapshot.TryGetProperty(key, out var window) || window.ValueKind != JsonValueKind.Object) continue;
            if (window.TryGetProperty("windowDurationMins", out var duration)
                && duration.ValueKind == JsonValueKind.Number && duration.TryGetInt32(out var minutes)
                && minutes == 10080) windows.Add(window);
        }
        if (windows.Count != 1) throw Unavailable(windows.Count == 0 ? "WEEKLY_QUOTA_UNAVAILABLE" : "AMBIGUOUS_WEEKLY_QUOTA");
        var weekly = windows[0];
        if (!weekly.TryGetProperty("usedPercent", out var value) || value.ValueKind != JsonValueKind.Number
            || !value.TryGetDecimal(out var used) || used < 0 || used > 100) throw Unavailable("INVALID_USED_PERCENT");
        if (!weekly.TryGetProperty("resetsAt", out var reset) || reset.ValueKind != JsonValueKind.Number
            || !reset.TryGetInt64(out var seconds)) throw Unavailable("INVALID_RESET_TIME");
        DateTimeOffset resetAt;
        try { resetAt = DateTimeOffset.FromUnixTimeSeconds(seconds); }
        catch (ArgumentOutOfRangeException) { throw Unavailable("INVALID_RESET_TIME"); }
        // Milliseconds, obsolete windows, and absurd future dates must not appear as a valid weekly reset.
        if (resetAt < observedAt.AddDays(-7) || resetAt > observedAt.AddDays(14)) throw Unavailable("INVALID_RESET_TIME");
        string? plan = null;
        if (snapshot.TryGetProperty("planType", out var planValue) && planValue.ValueKind == JsonValueKind.String)
            plan = planValue.GetString() switch { "plus" => "Plus", "pro" => "Pro", _ => null };
        return new QuotaSnapshot(plan, used, resetAt, observedAt);
    }

    private static bool IsAccountLimit(JsonElement element) => element.ValueKind == JsonValueKind.Object
        && (!element.TryGetProperty("limitId", out var id) || id.ValueKind == JsonValueKind.Null
            || (id.ValueKind == JsonValueKind.String && id.GetString() == "codex"));
    private static QuotaUnavailableException Unavailable(string code) => new(code);
}
