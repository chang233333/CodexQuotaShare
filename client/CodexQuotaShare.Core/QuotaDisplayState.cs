namespace CodexQuotaShare.Core;

public sealed class QuotaDisplayState
{
    public QuotaSnapshot? LastGood { get; private set; }
    public string? ErrorCode { get; private set; }
    public bool IsStale => ErrorCode is not null;
    public void Accept(QuotaSnapshot snapshot)
    {
        if (LastGood is not null && snapshot.ObservedAt < LastGood.ObservedAt) return;
        LastGood = snapshot;
        ErrorCode = null;
    }
    public void Fail(string code) => ErrorCode = code;
}
