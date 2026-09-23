namespace CodexQuotaShare.Core;

// OpenAI account quota only. Never invent an account hash from quota values.
public sealed record QuotaSnapshot(
    string? PlanType,
    decimal WeeklyUsedPercent,
    DateTimeOffset WeeklyResetAt,
    DateTimeOffset ObservedAt)
{
    public decimal WeeklyRemainingPercent => 100m - WeeklyUsedPercent;
    public string Source => "codex-app-server";
}

public sealed class QuotaUnavailableException(string code) : Exception(code)
{
    public string Code { get; } = code;
}
