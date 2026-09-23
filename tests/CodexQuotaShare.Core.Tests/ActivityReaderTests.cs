using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CodexQuotaShare.Core;
using CodexQuotaShare.Windows;

internal static class ActivityReaderTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(4);
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    public static void Register(Action<string, Func<Task>> test)
    {
        test("reader consecutive and concurrent refreshes release the lock", async () =>
        {
            await using var f = new Fixture();
            await f.Write("s", f.Line(100, -10));
            var reader = f.Reader();
            Check((await f.Poll(reader)).TokenDelta == 0, "History must be baseline");
            await f.Append("s", f.Line(120, 1));
            f.Clock.Advance();
            var deltas = new List<long>();
            reader.Updated += s => deltas.Add(s.TokenDelta);
            await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => reader.RefreshAsync())).WaitAsync(Timeout);
            Check(deltas.Sum() == 20 && f.Latest!.TodayTokenCount == 20, "Concurrent polls duplicated or lost usage");
        });
        test("reader missing directory recovers and disposes", async () =>
        {
            await using var f = new Fixture();
            Directory.Delete(f.Sessions);
            var reader = f.Reader();
            Check((await f.Poll(reader)).ErrorCode == "ACTIVITY_DIRECTORY_NOT_FOUND", "Expected missing directory");
            Directory.CreateDirectory(f.Sessions);
            await f.Write("new", f.Line(50, 1));
            f.Clock.Advance();
            Check((await f.Poll(reader)).TokenDelta == 50, "Recovery lost new session");
        });
        test("reader IO failure releases lock and reports error once", async () =>
        {
            await using var f = new Fixture();
            await f.Write("s", f.Line(100, -10));
            var reader = f.Reader();
            await f.Poll(reader);
            var errors = 0;
            reader.Unavailable += _ => errors++;
            using (var held = new FileStream(f.PathFor("s"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                Check((await f.Poll(reader)).ErrorCode == "ACTIVITY_UNAVAILABLE", "Expected IO error");
            Check(errors == 1, "Duplicate error notification");
            await f.Append("s", f.Line(120, 1));
            f.Clock.Advance();
            Check((await f.Poll(reader)).TokenDelta == 20, "IO recovery lost cursor");
        });
        test("reader short file append and restart retain fixed prefix", async () =>
        {
            await using var f = new Fixture();
            await f.Write("s", f.Line(100, -10));
            var reader = f.Reader();
            await f.Poll(reader);
            await f.Append("s", f.Line(120, 1));
            f.Clock.Advance();
            Check((await f.Poll(reader)).TokenDelta == 20, "Short append was reset");
            await f.Close(reader);
            await f.Append("s", f.Line(140, 2));
            var restored = f.Reader();
            var snapshot = await f.Poll(restored);
            Check(snapshot.TokenDelta == 20 && snapshot.TodayTokenCount == 40, "Restart lost short-file increment");
            Check((await f.Poll(restored)).TokenDelta == 0, "Restart counted duplicates");
        });
        test("reader counts first batch of new session and empty-file growth", async () =>
        {
            await using var f = new Fixture();
            await f.Write("empty", "");
            var reader = f.Reader();
            await f.Poll(reader);
            await f.Append("empty", f.Line(20, 1));
            await f.Write("new", f.Line(50, 1));
            f.Clock.Advance();
            var snapshot = await f.Poll(reader);
            Check(snapshot.TokenDelta == 70 && snapshot.TodayTokenCount == 70, "First batch ignored");
        });
        test("reader imports history without counting it and counts subsequent activity", async () =>
        {
            await using var f = new Fixture();
            var reader = f.Reader();
            await f.Poll(reader);
            await f.Write("old", f.Line(100, -60) + f.Line(120, -30));
            Check((await f.Poll(reader)).TokenDelta == 0, "Imported history counted");
            f.Clock.Advance();
            await f.Append("old", f.Line(150, 1));
            Check((await f.Poll(reader)).TokenDelta == 30, "Imported session append ignored");
        });
        test("reader monitoring cutoff survives restart and offline new sessions", async () =>
        {
            await using var f = new Fixture();
            var reader = f.Reader();
            await f.Poll(reader);
            await f.Close(reader);
            await f.Write("offline", f.Line(50, 1));
            await f.Write("import", f.Line(500, -60));
            f.Clock.Advance();
            var snapshot = await f.Poll(f.Reader());
            Check(snapshot.TokenDelta == 50 && snapshot.TodayTokenCount == 50, "Cutoff was reset on restart");
        });
        test("reader partial UTF8 prompt stays in source across restart", async () =>
        {
            await using var f = new Fixture();
            const string secret = "SYNTHETIC_PRIVATE_PROMPT_中文";
            var partial = Encoding.UTF8.GetBytes("{\"type\":\"response_item\",\"payload\":{\"text\":\"" + secret + "\"}}\r\n");
            var split = Array.IndexOf(partial, (byte)0xe4) + 1; // Within the first Chinese UTF-8 character.
            await f.Write("s", f.Line(100, -10));
            var completeOffset = new FileInfo(f.PathFor("s")).Length;
            await f.AppendBytes("s", partial[..split]);
            var reader = f.Reader();
            await f.Poll(reader);
            var state = await File.ReadAllTextAsync(f.State);
            var json = JsonNode.Parse(state)!;
            var cursor = json["Cursors"]!.AsObject().First().Value!;
            Check(cursor["Offset"]!.GetValue<long>() == completeOffset, "Partial bytes committed");
            Check(!state.Contains("PendingBase64") && !state.Contains(secret)
                && !state.Contains(Convert.ToBase64String(partial[..split])), "Raw content persisted");
            await f.Close(reader);
            await f.AppendBytes("s", partial[split..]);
            await f.Append("s", f.Line(120, 1));
            f.Clock.Advance();
            var restored = f.Reader();
            Check((await f.Poll(restored)).TokenDelta == 20, "Partial UTF8 broke following event");
            Check((await f.Poll(restored)).TokenDelta == 0, "Completed line counted twice");
        });
        test("reader partial token line waits for newline, including historical line", async () =>
        {
            await using var f = new Fixture();
            var historical = f.Line(100, -10);
            await f.Write("s", historical[..^1]);
            var reader = f.Reader();
            await f.Poll(reader);
            await f.Append("s", "\n");
            Check((await f.Poll(reader)).TokenDelta == 0, "Historical half line counted as new session");
            var live = f.Line(120, 1);
            f.Clock.Advance();
            await f.Append("s", live[..^1]);
            Check((await f.Poll(reader)).TokenDelta == 0, "Incomplete token line counted");
            await f.Close(reader);
            await f.Append("s", "\n");
            Check((await f.Poll(f.Reader())).TokenDelta == 20, "Restart lost token half line");
        });
        test("reader actual truncation and same-size replacement rebuild baseline", async () =>
        {
            await using var f = new Fixture();
            await f.Write("s", f.Line(100, -10) + f.Line(120, -9));
            var reader = f.Reader();
            await f.Poll(reader);
            f.Clock.Advance();
            await f.Write("s", f.Line(200, 1));
            Check((await f.Poll(reader)).TokenDelta == 0, "Truncation counted whole cumulative value");
            await f.Append("s", f.Line(220, 2));
            Check((await f.Poll(reader)).TokenDelta == 20, "Append after truncation lost");
            await f.Write("s", f.Line(300, 1) + f.Line(320, 2));
            Check((await f.Poll(reader)).TokenDelta == 0, "Same-size replacement not detected");
            await f.Append("s", f.Line(330, 3));
            var snapshot = await f.Poll(reader);
            Check(snapshot.TokenDelta == 10 && snapshot.TodayTokenCount == 30, "Replacement damaged daily counter");
        });
        test("reader legacy migration scrubs raw caches and preserves daily totals", async () =>
        {
            await using var f = new Fixture();
            await f.Write("s", f.Line(100, -10));
            var old = new
            {
                Accumulator = new ActivityAccumulatorState(f.Clock.Now.ToLocalTime().ToString("yyyy-MM-dd"), 7,
                    new() { [f.PathFor("s")] = new(100, f.Clock.Now.AddSeconds(-10)) }),
                Cursors = new Dictionary<string, object> { [f.PathFor("s")] = new
                {
                    Offset = 9999, Initialized = true, PrefixHash = "legacy",
                    PendingBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes("SYNTHETIC_PRIVATE_LEGACY"))
                } }
            };
            var legacy = JsonSerializer.Serialize(old);
            await File.WriteAllTextAsync(f.State, legacy);
            await File.WriteAllTextAsync(f.State + ".tmp", legacy);
            var reader = f.Reader();
            Check(!File.Exists(f.State + ".tmp"), "Legacy temporary file remains");
            var migrated = await File.ReadAllTextAsync(f.State);
            Check(!migrated.Contains("PendingBase64") && !migrated.Contains("U1lOVEhFVElD"), "Legacy prompt retained");
            Check((await f.Poll(reader)).TodayTokenCount == 7, "Migration changed daily total");
            await f.Append("s", f.Line(140, 1));
            f.Clock.Advance();
            Check((await f.Poll(reader)).TodayTokenCount == 47, "Post-migration append lost or duplicated");
        });
        test("reader legacy migration runs even when session directory is absent", async () =>
        {
            await using var f = new Fixture();
            Directory.Delete(f.Sessions);
            await File.WriteAllTextAsync(f.State, "{\"Cursors\":{\"old\":{\"PendingBase64\":\"cHJpdmF0ZQ==\"}}}");
            var reader = f.Reader();
            Check(!(await File.ReadAllTextAsync(f.State)).Contains("PendingBase64"), "Scrub deferred until successful scan");
            Check((await f.Poll(reader)).ErrorCode == "ACTIVITY_DIRECTORY_NOT_FOUND", "Expected missing directory");
        });
        test("reader rejects timestamp-less records and oversized line suffixes", async () =>
        {
            await using var f = new Fixture();
            await f.Write("s", f.Line(100, -10));
            var reader = f.Reader();
            await f.Poll(reader);
            var missing = "{\"payload\":{\"type\":\"token_count\",\"info\":{\"total_token_usage\":{\"total_tokens\":999}}}}\n";
            await f.Append("s", missing + new string('x', 1_048_577) + f.Line(999, 1) + f.Line(120, 2));
            f.Clock.Advance();
            Check((await f.Poll(reader)).TokenDelta == 20, "Malformed/undated content counted");
        });
        test("reader periodic loop produces multiple updates and exits", async () =>
        {
            await using var f = new Fixture();
            var reader = f.Reader(TimeSpan.FromMilliseconds(15));
            var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var count = 0;
            reader.Updated += _ => { if (Interlocked.Increment(ref count) >= 3) ready.TrySetResult(); };
            reader.Start();
            await ready.Task.WaitAsync(Timeout);
            await f.Close(reader);
            var stopped = count;
            await Task.Delay(50);
            Check(count == stopped, "Updates continued after dispose");
            await reader.DisposeAsync();
        });
        test("reader dispose cancels queued refresh while active refresh completes", async () =>
        {
            await using var f = new Fixture();
            var reader = f.Reader();
            using var release = new ManualResetEventSlim();
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            reader.Updated += _ => { entered.TrySetResult(); Check(release.Wait(Timeout), "Test callback release timed out"); };
            var active = Task.Run(reader.RefreshAsync);
            await entered.Task.WaitAsync(Timeout);
            var queued = reader.RefreshAsync();
            var disposing = reader.DisposeAsync().AsTask();
            release.Set();
            await Task.WhenAll(active, queued, disposing).WaitAsync(Timeout);
            await reader.RefreshAsync(); // No work after shutdown.
        });
        test("reader corrupt and null state entries recover", async () =>
        {
            await using var f = new Fixture();
            await File.WriteAllTextAsync(f.State, "{broken");
            var reader = f.Reader();
            Check((await f.Poll(reader)).ErrorCode is null, "Corrupt state blocked scan");
            await f.Close(reader);
            await File.WriteAllTextAsync(f.State, "{\"Version\":2,\"Cursors\":{\"s\":null},\"Accumulator\":{\"Sessions\":{\"s\":null},\"TodayTokenCount\":0}}");
            Check((await f.Poll(f.Reader())).ErrorCode is null, "Null state entry crashed reader");
        });
        test("reader restart across local midnight resets today only", async () =>
        {
            await using var f = new Fixture();
            var local = f.Clock.Now.ToLocalTime();
            f.Clock.Now = new DateTimeOffset(local.Year, local.Month, local.Day, 12, 0, 0, local.Offset).ToUniversalTime();
            var reader = f.Reader();
            await f.Poll(reader);
            await f.Write("s", f.Line(100, 1));
            f.Clock.Advance();
            Check((await f.Poll(reader)).TodayTokenCount == 100, "Initial day incorrect");
            await f.Close(reader);
            f.Clock.Now = f.Clock.Now.AddDays(1);
            await f.Append("s", f.Line(130, -1));
            var snapshot = await f.Poll(f.Reader());
            Check(snapshot.TodayTokenCount == 30 && snapshot.CurrentSessionTokenCount == 130, "Midnight restart lost session total");
        });
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
        public void Advance() => Now = Now.AddSeconds(10);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "CodexQuotaShare.ActivityTests", Guid.NewGuid().ToString("N"));
        private readonly List<CodexActivityReader> _readers = [];
        public Clock Clock { get; } = new();
        public string Sessions => Path.Combine(_directory, "sessions");
        public string State => Path.Combine(_directory, "activity-state.json");
        public ActivitySnapshot? Latest { get; private set; }
        public Fixture() => Directory.CreateDirectory(Sessions);
        public string PathFor(string name) => Path.Combine(Sessions, name + ".jsonl");
        public string Line(long total, int seconds) => JsonSerializer.Serialize(new
        {
            timestamp = Clock.Now.AddSeconds(seconds), type = "event_msg",
            payload = new { type = "token_count", info = new { total_token_usage = new { total_tokens = total } } }
        }) + "\n";
        public Task Write(string name, string text) => File.WriteAllTextAsync(PathFor(name), text, new UTF8Encoding(false));
        public Task Append(string name, string text) => File.AppendAllTextAsync(PathFor(name), text, new UTF8Encoding(false));
        public async Task AppendBytes(string name, byte[] bytes)
        {
            await using var stream = new FileStream(PathFor(name), FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            await stream.WriteAsync(bytes);
        }
        public CodexActivityReader Reader(TimeSpan? interval = null)
        {
            var reader = new CodexActivityReader(Sessions, State, Clock, interval);
            reader.Updated += snapshot => Latest = snapshot;
            _readers.Add(reader);
            return reader;
        }
        public async Task<ActivitySnapshot> Poll(CodexActivityReader reader)
        {
            await reader.RefreshAsync().WaitAsync(Timeout);
            return Latest ?? throw new Exception("Missing activity snapshot");
        }
        public async Task Close(CodexActivityReader reader)
        {
            await reader.DisposeAsync().AsTask().WaitAsync(Timeout);
            _readers.Remove(reader);
        }
        public async ValueTask DisposeAsync()
        {
            foreach (var reader in _readers) await reader.DisposeAsync().AsTask().WaitAsync(Timeout);
            Directory.Delete(_directory, recursive: true);
        }
    }
}
