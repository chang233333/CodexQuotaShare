using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace CodexQuotaShare.Core;

/// <summary>
/// A local-only activity observation. SessionKey is deliberately never a Relay DTO.
/// </summary>
public sealed record ActivityRecord(
    string SessionKey,
    DateTimeOffset Timestamp,
    long TotalTokens);

public sealed record ActivitySessionState(
    long TotalTokens,
    DateTimeOffset? LastActivityAt);

public sealed record ActivityAccumulatorState(
    string? LocalDate,
    long TodayTokenCount,
    Dictionary<string, ActivitySessionState> Sessions);

public sealed record ActivitySnapshot(
    DateTimeOffset ObservedAt,
    long TodayTokenCount,
    long CurrentSessionTokenCount,
    long TokenDelta,
    int ActiveSessionCount,
    DateTimeOffset? LastActivityAt,
    bool BaselineEstablished,
    string? ErrorCode = null);

/// <summary>Parses only the token-count fields needed for local attribution.</summary>
public static class CodexActivityParser
{
    public static bool TryParse(string line, string sessionKey, DateTimeOffset fallbackTimestamp, out ActivityRecord record,
        bool requireTimestamp = false)
    {
        record = default!;
        if (string.IsNullOrWhiteSpace(sessionKey) || string.IsNullOrWhiteSpace(line)) return false;
        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;

            var payload = root.TryGetProperty("payload", out var payloadValue)
                && payloadValue.ValueKind == JsonValueKind.Object ? payloadValue : root;
            if (!TryFindTokenUsage(payload, root, out var totalTokens)) return false;
            if (!TryFindTimestamp(root, payload, fallbackTimestamp, out var timestamp, requireTimestamp)) return false;

            record = new ActivityRecord(sessionKey, timestamp, totalTokens);
            return true;
        }
        catch (JsonException)
        {
            // A partially written or malformed session line must not stop the reader.
            return false;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static bool TryFindTokenUsage(JsonElement payload, JsonElement root, out long totalTokens)
    {
        totalTokens = 0;
        if (payload.TryGetProperty("type", out var eventType)
            && eventType.ValueKind == JsonValueKind.String
            && !string.Equals(eventType.GetString(), "token_count", StringComparison.Ordinal)) return false;

        foreach (var container in new[] { payload, root })
        {
            if (container.TryGetProperty("info", out var info) && info.ValueKind == JsonValueKind.Object
                && TryReadTotal(info, out totalTokens)) return true;
            if (TryReadTotal(container, out totalTokens)) return true;
        }
        return false;
    }

    private static bool TryReadTotal(JsonElement container, out long totalTokens)
    {
        totalTokens = 0;
        if (!container.TryGetProperty("total_token_usage", out var usage)
            || usage.ValueKind != JsonValueKind.Object
            || !usage.TryGetProperty("total_tokens", out var total)) return false;
        if (total.ValueKind != JsonValueKind.Number || !total.TryGetInt64(out totalTokens) || totalTokens < 0)
        {
            totalTokens = 0;
            return false;
        }
        return true;
    }

    private static bool TryFindTimestamp(JsonElement root, JsonElement payload, DateTimeOffset fallback, out DateTimeOffset timestamp,
        bool requireTimestamp)
    {
        foreach (var container in new[] { root, payload })
        {
            if (!container.TryGetProperty("timestamp", out var value)) continue;
            if (value.ValueKind == JsonValueKind.String
                && DateTimeOffset.TryParse(value.GetString(), CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out timestamp))
                return IsReasonable(timestamp, fallback);
            if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var unix))
            {
                try
                {
                    timestamp = unix >= 100_000_000_000d
                        ? DateTimeOffset.FromUnixTimeMilliseconds(checked((long)unix))
                        : DateTimeOffset.FromUnixTimeSeconds(checked((long)unix));
                    return IsReasonable(timestamp, fallback);
                }
                catch (ArgumentOutOfRangeException) { }
                catch (OverflowException) { }
            }
        }
        timestamp = fallback;
        return !requireTimestamp && IsReasonable(timestamp, fallback);
    }

    private static bool IsReasonable(DateTimeOffset timestamp, DateTimeOffset now) =>
        timestamp >= new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero)
        && timestamp <= now.AddDays(2);
}

/// <summary>Maintains cumulative session totals and derives local attribution signals.</summary>
public sealed class ActivityAccumulator
{
    private readonly Dictionary<string, ActivitySessionState> _sessions = new(StringComparer.OrdinalIgnoreCase);
    private DateOnly? _localDate;
    private long _todayTokenCount;

    public void Restore(ActivityAccumulatorState state)
    {
        _sessions.Clear();
        foreach (var pair in state.Sessions ?? [])
        {
            if (!string.IsNullOrWhiteSpace(pair.Key) && pair.Value is not null && pair.Value.TotalTokens >= 0)
                _sessions[pair.Key] = pair.Value;
        }
        _todayTokenCount = Math.Max(0, state.TodayTokenCount);
        _localDate = DateOnly.TryParseExact(state.LocalDate, "yyyy-MM-dd", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var parsed) ? parsed : null;
    }

    public ActivityAccumulatorState ExportState() => new(
        _localDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        _todayTokenCount,
        new Dictionary<string, ActivitySessionState>(_sessions, StringComparer.OrdinalIgnoreCase));

    public void ForgetSession(string sessionKey) => _sessions.Remove(sessionKey);

    public ActivitySnapshot Apply(IEnumerable<ActivityRecord> records, DateTimeOffset now, bool establishBaseline)
    {
        EnsureDay(now);
        long delta = 0;
        foreach (var record in records.OrderBy(item => item.Timestamp))
        {
            if (record.TotalTokens < 0 || string.IsNullOrWhiteSpace(record.SessionKey)) continue;
            if (_sessions.TryGetValue(record.SessionKey, out var previous))
            {
                if (previous.LastActivityAt is { } previousAt && record.Timestamp < previousAt)
                    continue; // A late line cannot rewind a session's cumulative counter.

                if (record.TotalTokens < previous.TotalTokens)
                {
                    // A reset/truncated session gets a new baseline; the decrease is not usage.
                    _sessions[record.SessionKey] = new ActivitySessionState(record.TotalTokens, record.Timestamp);
                    continue;
                }

                var increment = record.TotalTokens - previous.TotalTokens;
                if (!establishBaseline && increment > 0)
                {
                    delta += increment;
                    if (record.Timestamp.ToLocalTime().Date == now.ToLocalTime().Date)
                        _todayTokenCount = checked(_todayTokenCount + increment);
                }
                _sessions[record.SessionKey] = new ActivitySessionState(record.TotalTokens,
                    Max(previous.LastActivityAt, record.Timestamp));
            }
            else
            {
                _sessions[record.SessionKey] = new ActivitySessionState(record.TotalTokens, record.Timestamp);
                if (!establishBaseline)
                {
                    delta = checked(delta + record.TotalTokens);
                    if (record.Timestamp.ToLocalTime().Date == now.ToLocalTime().Date)
                        _todayTokenCount = checked(_todayTokenCount + record.TotalTokens);
                }
            }
        }
        return Snapshot(now, delta, establishBaseline);
    }

    public ActivitySnapshot Snapshot(DateTimeOffset now, long tokenDelta = 0, bool baselineEstablished = false)
    {
        EnsureDay(now);
        var latest = _sessions
            .Where(pair => pair.Value.LastActivityAt is not null)
            .OrderByDescending(pair => pair.Value.LastActivityAt)
            .ThenBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .Select(pair => pair.Value)
            .FirstOrDefault();
        var cutoff = now - TimeSpan.FromMinutes(30);
        var active = _sessions.Values.Count(value => value.LastActivityAt is { } at && at >= cutoff && at <= now.AddMinutes(2));
        return new ActivitySnapshot(now, _todayTokenCount, latest?.TotalTokens ?? 0, tokenDelta,
            active, latest?.LastActivityAt, baselineEstablished);
    }

    private void EnsureDay(DateTimeOffset now)
    {
        var day = DateOnly.FromDateTime(now.ToLocalTime().DateTime);
        if (_localDate == day) return;
        _localDate = day;
        _todayTokenCount = 0;
    }

    private static DateTimeOffset? Max(DateTimeOffset? first, DateTimeOffset second) =>
        first is null || second > first.Value ? second : first;
}

/// <summary>Byte-safe line framing for append-only UTF-8 JSONL files.</summary>
public sealed class Utf8LineBuffer
{
    private const int MaximumPendingBytes = 1_048_576;
    private readonly List<byte> _pending;
    private bool _discarding;

    public Utf8LineBuffer(byte[]? pending = null) => _pending = pending is null ? [] : [.. pending];
    public byte[] PendingBytes => [.. _pending];

    public IReadOnlyList<string> Append(ReadOnlySpan<byte> bytes)
    {
        var lines = new List<string>();
        for (var index = 0; index < bytes.Length; index++)
        {
            if (_discarding)
            {
                if (bytes[index] == (byte)'\n') _discarding = false;
                continue;
            }
            _pending.Add(bytes[index]);
            if (bytes[index] != (byte)'\n')
            {
                if (_pending.Count > MaximumPendingBytes)
                {
                    _pending.Clear();
                    _discarding = true;
                }
                continue;
            }

            var lineLength = _pending.Count - 1;
            if (lineLength > 0 && _pending[lineLength - 1] == (byte)'\r') lineLength--;
            if (lineLength > 0)
            {
                try
                {
                    lines.Add(new UTF8Encoding(false, true).GetString(CollectionsMarshal.AsSpan(_pending)[..lineLength]));
                }
                catch (DecoderFallbackException) { }
            }
            _pending.Clear();
        }
        return lines;
    }
}
