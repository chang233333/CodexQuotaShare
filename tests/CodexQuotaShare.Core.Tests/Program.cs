using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using CodexQuotaShare.Core;
using CodexQuotaShare.Core.Tests;
using CodexQuotaShare.Windows;

internal static class Program
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;
    private static readonly List<(string Name, Func<Task> Run)> Tests = [];
    private static JsonObject Window(decimal used = 42.5m, int minutes = 10080) => new()
    {
        ["usedPercent"] = used, ["windowDurationMins"] = minutes,
        ["resetsAt"] = Now.AddDays(3).ToUnixTimeSeconds()
    };
    private static JsonObject Sample() => new()
    {
        ["rateLimits"] = new JsonObject { ["limitId"] = "codex", ["planType"] = "plus", ["primary"] = Window(), ["secondary"] = null }
    };
    private static QuotaSnapshot Parse(JsonObject data) => QuotaParser.Parse(JsonSerializer.SerializeToElement(data), Now);
    private static void Check(bool pass, string detail = "Unexpected result") { if (!pass) throw new Exception(detail); }
    private static void Test(string name, Action test) => Tests.Add((name, () => { test(); return Task.CompletedTask; }));
    private static void AsyncTest(string name, Func<Task> test) => Tests.Add((name, test));
    private static void Reject(string expected, Action<JsonObject> mutate)
    {
        var json = Sample(); mutate(json);
        try { Parse(json); throw new Exception("Expected rejection"); }
        catch (QuotaUnavailableException ex) { Check(ex.Code == expected, ex.Code); }
    }
    private static ProcessStartInfo FakeCommand(string mode)
    {
        var command = new ProcessStartInfo(Environment.ProcessPath!);
        if (Path.GetFileNameWithoutExtension(command.FileName).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            command.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
        command.ArgumentList.Add("--fake-server"); command.ArgumentList.Add(mode);
        return command;
    }
    private static Task<CodexAppServerClient> Connect(string mode = "normal", int timeout = 2000) =>
        CodexAppServerClient.StartAsync(FakeCommand(mode), requestTimeout: TimeSpan.FromMilliseconds(timeout));

    public static async Task<int> Main(string[] args)
    {
        if (args.Length == 3 && args[0] == "--relay-integration") return await RelayIntegration.RunAsync(args[1], args[2]);
        if (args.Length == 2 && args[0] == "--probe")
        {
            try
            {
                var command = new ProcessStartInfo(args[1]); command.ArgumentList.Add("app-server");
                await using var client = await CodexAppServerClient.StartAsync(command);
                var first = await client.ReadQuotaAsync();
                await Task.Delay(1000);
                var second = await client.ReadQuotaAsync();
                Console.WriteLine(JsonSerializer.Serialize(new { connected = true, source = second.Source, planType = second.PlanType, weeklyWindowRead = true, repeatRead = second.ObservedAt > first.ObservedAt }));
                return 0;
            }
            catch (QuotaUnavailableException error)
            {
                Console.WriteLine(JsonSerializer.Serialize(new { connected = false, code = error.Code })); return 1;
            }
        }
        if (args.Length == 2 && args[0] == "--fake-server") return await FakeServer(args[1]);
        Test("weekly primary, decimal and remaining", () => { var q = Parse(Sample()); Check(q.WeeklyUsedPercent == 42.5m && q.WeeklyRemainingPercent == 57.5m && q.PlanType == "Plus"); });
        Test("weekly secondary, not max of windows", () => { var x = Sample(); x["rateLimits"]!["primary"] = Window(99, 300); x["rateLimits"]!["secondary"] = Window(12.5m); Check(Parse(x).WeeklyUsedPercent == 12.5m); });
        Test("mapped codex fallback", () => { var x = Sample(); var s = x["rateLimits"]!.DeepClone(); x.Remove("rateLimits"); x["rateLimitsByLimitId"] = new JsonObject { ["codex"] = s }; Check(Parse(x).WeeklyUsedPercent == 42.5m); });
        Test("ignore Spark model quota", () => { var x = Sample(); x["rateLimitsByLimitId"] = new JsonObject { ["codex_bengalfox"] = new JsonObject { ["primary"] = Window(99) } }; Check(Parse(x).WeeklyUsedPercent == 42.5m); });
        Test("reject model-only payload", () => Reject("ACCOUNT_QUOTA_UNAVAILABLE", x => x["rateLimits"]!["limitId"] = "codex_bengalfox"));
        Test("missing percent not zero", () => Reject("INVALID_USED_PERCENT", x => x["rateLimits"]!["primary"]!.AsObject().Remove("usedPercent")));
        Test("null percent not zero", () => Reject("INVALID_USED_PERCENT", x => x["rateLimits"]!["primary"]!["usedPercent"] = null));
        Test("numeric string rejected", () => Reject("INVALID_USED_PERCENT", x => x["rateLimits"]!["primary"]!["usedPercent"] = "42"));
        Test("negative rejected", () => Reject("INVALID_USED_PERCENT", x => x["rateLimits"]!["primary"]!["usedPercent"] = -1));
        Test("over 100 rejected", () => Reject("INVALID_USED_PERCENT", x => x["rateLimits"]!["primary"]!["usedPercent"] = 101));
        Test("zero and full are valid", () => { foreach (var n in new[] { 0, 100 }) { var x = Sample(); x["rateLimits"]!["primary"]!["usedPercent"] = n; Check(Parse(x).WeeklyUsedPercent == n); } });
        Test("ambiguous weekly rejected", () => Reject("AMBIGUOUS_WEEKLY_QUOTA", x => x["rateLimits"]!["secondary"] = Window(10)));
        Test("no weekly rejected", () => Reject("WEEKLY_QUOTA_UNAVAILABLE", x => x["rateLimits"]!["primary"] = Window(10, 300)));
        Test("null weekly rejected", () => Reject("WEEKLY_QUOTA_UNAVAILABLE", x => x["rateLimits"]!["primary"] = null));
        Test("missing reset rejected", () => Reject("INVALID_RESET_TIME", x => x["rateLimits"]!["primary"]!.AsObject().Remove("resetsAt")));
        Test("millisecond timestamp rejected", () => Reject("INVALID_RESET_TIME", x => x["rateLimits"]!["primary"]!["resetsAt"] = Now.ToUnixTimeMilliseconds()));
        Test("out-of-range timestamp rejected", () => Reject("INVALID_RESET_TIME", x => x["rateLimits"]!["primary"]!["resetsAt"] = long.MaxValue));
        Test("obsolete reset rejected", () => Reject("INVALID_RESET_TIME", x => x["rateLimits"]!["primary"]!["resetsAt"] = Now.AddDays(-30).ToUnixTimeSeconds()));
        Test("unexpected plan text not displayed", () => { var x = Sample(); x["rateLimits"]!["planType"] = "private-content"; Check(Parse(x).PlanType is null); });
        Test("stale data preserved then recovered", () => { var state = new QuotaDisplayState(); var q = Parse(Sample()); state.Accept(q); state.Fail("OFFLINE"); Check(state.IsStale && state.LastGood == q); state.Accept(q with { ObservedAt = q.ObservedAt.AddSeconds(1) }); Check(!state.IsStale); });
        Test("older observation does not override", () => { var state = new QuotaDisplayState(); var q = Parse(Sample()); state.Accept(q); state.Accept(q with { ObservedAt = q.ObservedAt.AddSeconds(-1), WeeklyUsedPercent = 1 }); Check(state.LastGood == q); });
        Test("activity parser reads token_count total", () =>
        {
            var line = "{\"timestamp\":\"" + Now.ToString("O") + "\",\"type\":\"event_msg\",\"payload\":{\"type\":\"token_count\",\"info\":{\"total_token_usage\":{\"input_tokens\":10,\"output_tokens\":5,\"total_tokens\":15}}}}";
            Check(CodexActivityParser.TryParse(line, "session-a", Now, out var record));
            Check(record.SessionKey == "session-a" && record.TotalTokens == 15 && record.Timestamp == Now);
        });
        Test("activity parser ignores non token events and malformed lines", () =>
        {
            Check(!CodexActivityParser.TryParse("{\"type\":\"turn_context\"}", "session-a", Now, out _));
            Check(!CodexActivityParser.TryParse("{broken", "session-a", Now, out _));
        });
        Test("activity parser accepts unix milliseconds", () =>
        {
            var line = "{\"timestamp\":" + Now.ToUnixTimeMilliseconds() + ",\"payload\":{\"type\":\"token_count\",\"info\":{\"total_token_usage\":{\"total_tokens\":8}}}}";
            Check(CodexActivityParser.TryParse(line, "session-a", Now, out var record) && record.TotalTokens == 8);
            Check(Math.Abs((record.Timestamp - Now).TotalSeconds) < 1);
        });
        Test("utf8 line buffer retains half line", () =>
        {
            var buffer = new Utf8LineBuffer();
            var first = System.Text.Encoding.UTF8.GetBytes("{\"text\":\"中文");
            var second = System.Text.Encoding.UTF8.GetBytes("\"}\r\nnext\n");
            Check(buffer.Append(first).Count == 0);
            var lines = buffer.Append(second);
            Check(lines.Count == 2 && lines[0].Contains("中文") && lines[1] == "next");
        });
        Test("activity baseline and duplicate cumulative totals", () =>
        {
            var accumulator = new ActivityAccumulator();
            var first = new ActivityRecord("session-a", Now, 100);
            Check(accumulator.Apply([first], Now, establishBaseline: true).TokenDelta == 0);
            Check(accumulator.Apply([first with { TotalTokens = 120 }], Now.AddMinutes(1), establishBaseline: false).TokenDelta == 20);
            Check(accumulator.Apply([first with { TotalTokens = 120 }], Now.AddMinutes(2), establishBaseline: false).TokenDelta == 0);
            Check(accumulator.Snapshot(Now.AddMinutes(2)).TodayTokenCount == 20);
        });
        Test("activity late lower total cannot rewind", () =>
        {
            var accumulator = new ActivityAccumulator();
            accumulator.Apply([new ActivityRecord("session-a", Now, 100)], Now, true);
            accumulator.Apply([new ActivityRecord("session-a", Now.AddMinutes(1), 130)], Now.AddMinutes(1), false);
            Check(accumulator.Apply([new ActivityRecord("session-a", Now, 110)], Now.AddMinutes(2), false).TokenDelta == 0);
            Check(accumulator.Snapshot(Now.AddMinutes(2)).CurrentSessionTokenCount == 130);
        });
        Test("activity rolls today counter at local midnight", () =>
        {
            var accumulator = new ActivityAccumulator();
            var beforeMidnight = new DateTimeOffset(Now.Year, Now.Month, Now.Day, 23, 59, 0, Now.Offset).AddDays(-1);
            var afterMidnight = beforeMidnight.AddMinutes(2);
            accumulator.Apply([new ActivityRecord("session-a", beforeMidnight, 100)], beforeMidnight, true);
            accumulator.Apply([new ActivityRecord("session-a", afterMidnight, 110)], afterMidnight, false);
            Check(accumulator.Snapshot(afterMidnight).TodayTokenCount == 10);
        });
        AsyncTest("handshake and read", async () => { await using var c = await Connect(); Check((await c.ReadQuotaAsync()).WeeklyUsedPercent == 25); });
        AsyncTest("updated notification", async () => { await using var c = await Connect("updated"); var signal = new TaskCompletionSource<QuotaSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously); c.QuotaUpdated += q => signal.TrySetResult(q); await c.ReadQuotaAsync(); Check((await signal.Task.WaitAsync(TimeSpan.FromSeconds(2))).WeeklyUsedPercent == 26); });
        AsyncTest("wire order survives async response continuation", async () => { await using var c = await Connect("updated"); var signal = new TaskCompletionSource<QuotaSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously); c.QuotaUpdated += q => signal.TrySetResult(q); var read = await c.ReadQuotaAsync(); var pushed = await signal.Task.WaitAsync(TimeSpan.FromSeconds(2)); Check(read.ObservedAt < pushed.ObservedAt); var state = new QuotaDisplayState(); state.Accept(pushed); state.Accept(read); Check(state.LastGood!.WeeklyUsedPercent == 26); });
        AsyncTest("concurrent requests preserve JSON lines", async () => { await using var c = await Connect(); var all = await Task.WhenAll(Enumerable.Range(0, 24).Select(_ => c.ReadQuotaAsync())); Check(all.All(q => q.WeeklyUsedPercent == 25)); });
        AsyncTest("unrecognized response ID ignored", async () => { await using var c = await Connect("unknown-id"); Check((await c.ReadQuotaAsync()).WeeklyUsedPercent == 25); });
        AsyncTest("RPC errors are sanitized", async () => { await using var c = await Connect("error"); try { await c.ReadQuotaAsync(); throw new Exception("Expected error"); } catch (QuotaUnavailableException e) { Check(e.Message == "CODEX_REQUEST_REJECTED"); } });
        foreach (var mode in new[] { "eof", "malformed", "non-object" })
        {
            AsyncTest("bounded failure: " + mode, async () => { await using var c = await Connect(mode); var started = Stopwatch.StartNew(); try { await c.ReadQuotaAsync(); throw new Exception("Expected failure"); } catch (QuotaUnavailableException) { Check(started.Elapsed < TimeSpan.FromSeconds(2)); } });
        }
        AsyncTest("request timeout", async () => { await using var c = await Connect("silent", 300); try { await c.ReadQuotaAsync(); throw new Exception("Expected timeout"); } catch (QuotaUnavailableException e) { Check(e.Code == "CODEX_TIMEOUT"); } });
        AsyncTest("initialization timeout cleans child", async () => { try { await using var c = await Connect("silent-init", 300); throw new Exception("Expected timeout"); } catch (QuotaUnavailableException e) { Check(e.Code == "CODEX_TIMEOUT"); } });
        AsyncTest("caller cancellation and subsequent read", async () => { await using var c = await Connect("delayed", 3000); using var cancel = new CancellationTokenSource(80); try { await c.ReadQuotaAsync(cancel.Token); throw new Exception("Expected cancel"); } catch (OperationCanceledException) { } Check((await c.ReadQuotaAsync()).WeeklyUsedPercent == 25); });
        AsyncTest("dispose completes pending read", async () => { var c = await Connect("silent"); var pending = c.ReadQuotaAsync(); await Task.Delay(50); await c.DisposeAsync(); try { await pending.WaitAsync(TimeSpan.FromSeconds(1)); throw new Exception("Expected disposed"); } catch (QuotaUnavailableException) { } await c.DisposeAsync(); });
        AsyncTest("fresh connection after failure", async () => { await using (var c = await Connect("eof")) { try { await c.ReadQuotaAsync(); } catch (QuotaUnavailableException) { } } await using var next = await Connect(); Check((await next.ReadQuotaAsync()).WeeklyUsedPercent == 25); });
        AsyncTest("invalid update reports unavailable", async () => { await using var c = await Connect("bad-update"); var signal = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously); c.Unavailable += code => signal.TrySetResult(code); await c.ReadQuotaAsync(); Check(await signal.Task.WaitAsync(TimeSpan.FromSeconds(2)) == "WEEKLY_QUOTA_UNAVAILABLE"); });
        Test("relay HMAC golden vector", () =>
        {
            const string secret = "c2VjcmV0";
            const string method = "GET";
            const string path = "/v1/groups/11111111-1111-4111-8111-111111111111";
            const long timestamp = 1700000000000;
            const string nonce = "AAAAAAAAAAAAAAAAAAAAAA";
            const long sequence = 1;
            const string payload = "null";
            const string expected = "v0xlsjQ7kG9OElqQaSy1m4hmQa0LpNvjAFiFbBZh3ws";
            var signature = RelayAuth.Sign(secret, method, path, timestamp, nonce, sequence, payload);
            Check(signature == expected, signature);
            Check(RelayAuth.Verify(secret, method, path, timestamp, nonce, sequence, payload, expected));
            Check(!RelayAuth.Verify(secret, method, path, timestamp, nonce, sequence, payload, "A" + expected[1..]));
        });
        Test("relay canonical JSON and snapshot cache are deterministic", () =>
        {
            using var document = JsonDocument.Parse("{\"z\": [3, {\"b\": true, \"a\": 1}], \"a\": null}");
            Check(RelayAuth.CanonicalJson(document.RootElement) == "{\"a\":null,\"z\":[3,{\"a\":1,\"b\":true}]}");
            var directory = Path.Combine(Path.GetTempPath(), "relay-cache-" + Guid.NewGuid().ToString("N"));
            var path = Path.Combine(directory, "relay-snapshot.json");
            var snapshot = new RelaySnapshot("11111111-1111-4111-8111-111111111111", "11111111-1111-4111-8111-111111111111", 7, 1, 2,
                [new RelayDevice("11111111-1111-4111-8111-111111111111", "MAIN-PC", "OWNER", "ONLINE", 1, 2,
                    new RelayActivityReport(1, 2, 3, 1))]);
            try
            {
                RelaySnapshotCache.Save(path, snapshot);
                var loaded = RelaySnapshotCache.Load(path);
                Check(loaded is not null && loaded.GroupId == snapshot.GroupId && loaded.Version == snapshot.Version &&
                    loaded.Devices.Count == 1 && loaded.Devices[0].DisplayName == "MAIN-PC" &&
                    loaded.Devices[0].LastActivity?.TokenDelta == 3);
                var persisted = File.ReadAllText(path);
                Check(!persisted.Contains("deviceSecret", StringComparison.Ordinal) && !persisted.Contains("prompt", StringComparison.Ordinal));
                File.WriteAllText(path, "{broken");
                Check(RelaySnapshotCache.Load(path) is null);
            }
            finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
        });
        Test("dashboard separates official quota from device activity", () =>
        {
            var dashboard = new RelayDashboardState();
            var official = new QuotaSnapshot("Plus", 42.5m, Now.AddDays(3), Now);
            var deviceId = "11111111-1111-4111-8111-111111111111";
            var snapshot = new RelaySnapshot(deviceId, deviceId, 4, 1, 2,
                [new RelayDevice(deviceId, "MAIN-PC", "OWNER", "ONLINE", 1, 2, new RelayActivityReport(1, 2, 800, 1))]);
            Check(dashboard.AcceptOfficialAccountUsage(official) && dashboard.AcceptAuthoritativeSnapshot(snapshot));
            Check(dashboard.OfficialAccountUsage!.WeeklyUsedPercent == 42.5m);
            Check(dashboard.DeviceActivity.Count == 1 && dashboard.DeviceActivity[0].ActivityTokenDelta == 800 &&
                dashboard.DeviceActivity[0].Confidence == ActivityConfidence.High &&
                dashboard.DeviceActivity[0].Source == "local-activity-signal");
            var attributed = snapshot with { Version = 5, UsageLedger = new RelayUsageLedger(
                new Dictionary<string, decimal> { [deviceId] = 1.6m }, 0,
                new Dictionary<string, string> { [deviceId] = "HIGH" }) };
            Check(dashboard.AcceptAuthoritativeSnapshot(attributed) && dashboard.DeviceActivity[0].Source == "server-attribution" &&
                dashboard.DeviceActivity[0].EstimatedUsagePercent == 1.6m);
            Check(!dashboard.AcceptAuthoritativeSnapshot(snapshot with { Version = 3 }));
            dashboard.BeginReconnect();
            var repaired = snapshot with { Devices = [snapshot.Devices[0] with { DisplayName = "REPAIRED" }] };
            Check(dashboard.AcceptAuthoritativeSnapshot(repaired) && dashboard.DeviceActivity[0].DisplayName == "REPAIRED");
            Check(!dashboard.AcceptOfficialAccountUsage(official with { ObservedAt = Now.AddSeconds(-1), WeeklyUsedPercent = 1 }));
            var notification = new RelayNotification("event-1", "WEEKLY_RESET", 3);
            var notified = repaired with { Version = 6, LastNotification = notification };
            Check(dashboard.AcceptAuthoritativeSnapshot(notified) && dashboard.ConsumeNotification()?.EventId == "event-1");
            Check(!dashboard.AcceptAuthoritativeSnapshot(notified) && dashboard.ConsumeNotification() is null);
        });
        Test("relay launch gate is fail-safe across offline and reset", () =>
        {
            var deviceId = "11111111-1111-4111-8111-111111111111";
            var epoch = new RelayWeeklyEpoch("epoch-1", 1, 3, 30);
            var device = new RelayDevice(deviceId, "MAIN-PC", "OWNER", "LIMIT_REACHED", 1, 2, null) { LimitPercent = 30 };
            var snapshot = new RelaySnapshot(deviceId, deviceId, 4, 1, 2, [device]) with
            {
                WeeklyEpoch = epoch,
                UsageLedger = new RelayUsageLedger(new Dictionary<string, decimal> { [deviceId] = 30 }, 0,
                    new Dictionary<string, string> { [deviceId] = "HIGH" })
            };
            var gate = new RelayLaunchGate(deviceId);
            gate.AcceptSnapshot(snapshot);
            Check(!gate.State.CanStartNewCodex && gate.State.IsLimited && gate.State.EstimatedUsagePercent == 30);
            gate.MarkConnectionState(RelayConnectionState.Offline);
            Check(!gate.State.CanStartNewCodex && gate.State.Status == "LIMIT_REACHED_OFFLINE");

            var sameEpochOnline = snapshot with { Version = 5, Devices = [device with { Status = "OFFLINE" }] };
            gate.AcceptSnapshot(sameEpochOnline);
            Check(!gate.State.CanStartNewCodex);
            var reset = sameEpochOnline with
            {
                Version = 6,
                WeeklyEpoch = epoch with { EpochId = "epoch-2", ResetAt = 4 },
                UsageLedger = new RelayUsageLedger(new Dictionary<string, decimal> { [deviceId] = 0 }, 0, new Dictionary<string, string>()),
                Devices = [device with { Status = "OFFLINE" }]
            };
            gate.AcceptSnapshot(reset);
            Check(gate.State.CanStartNewCodex && !gate.State.IsLimited);

            gate.AcceptSnapshot(snapshot with { Version = 7, Devices = [device] });
            Check(!gate.State.CanStartNewCodex);
            gate.AcceptSnapshot(snapshot with { Version = 8, Devices = [device with { Status = "OFFLINE", LimitPercent = null }] });
            Check(gate.State.CanStartNewCodex && gate.State.LimitPercent is null);
        });
        Test("DPAPI identity envelope hides device secret", () =>
        {
            var directory = Path.Combine(Path.GetTempPath(), "dpapi-test-" + Guid.NewGuid().ToString("N"));
            var path = Path.Combine(directory, "device-identity.json");
            const string deviceId = "11111111-1111-4111-8111-111111111111";
            const string secret = "c2VjcmV0-device-secret";
            try
            {
                DeviceSecretStore.Save(path, deviceId, secret);
                var persisted = File.ReadAllText(path);
                Check(!persisted.Contains(secret, StringComparison.Ordinal));
                var loaded = DeviceSecretStore.Load(path);
                Check(loaded.DeviceId == deviceId && loaded.DeviceSecret == secret);
            }
            finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
        });
        Test("enforcement requires exact executable and exempts quota observer", () =>
        {
            var executable = Path.GetFullPath("codex.exe");
            Check(SoftEnforcementPolicy.ShouldStop(true, true, 5, 6, executable, [executable]));
            Check(!SoftEnforcementPolicy.ShouldStop(true, true, 6, 6, executable, [executable]));
            Check(!SoftEnforcementPolicy.ShouldStop(false, true, 5, 6, executable, [executable]));
            Check(!SoftEnforcementPolicy.ShouldStop(true, false, 5, 6, executable, [executable]));
            Check(!SoftEnforcementPolicy.ShouldStop(true, true, 5, 6, Path.GetFullPath("other/codex.exe"), [executable]));
        });
        Test("authoritative snapshot repairs inflated cache version", () =>
        {
            var d = new RelayDashboardState();
            var s = new RelaySnapshot("group", "owner", 900000, 1, 2, []);
            d.AcceptCachedSnapshot(s);
            Check(d.AcceptAuthoritativeSnapshot(s with { Version = 2 }));
            Check(d.Snapshot!.Version == 2 && !d.AcceptAuthoritativeSnapshot(s with { Version = 1 }));
        });
        Test("all simultaneous limit notifications are delivered once", () =>
        {
            var d = new RelayDashboardState();
            var s = new RelaySnapshot("group", "owner", 1, 1, 2, []) { Notifications = [new("a", "DEVICE_LIMIT_REACHED", 1), new("b", "DEVICE_LIMIT_REACHED", 1)] };
            d.AcceptAuthoritativeSnapshot(s);
            Check(d.ConsumeNotification()?.EventId == "a" && d.ConsumeNotification()?.EventId == "b" && d.ConsumeNotification() is null);
        });
        ActivityReaderTests.Register(AsyncTest);
        var failed = 0;
        foreach (var (name, test) in Tests)
        {
            try { await test(); Console.WriteLine("PASS " + name); }
            catch (Exception ex) { failed++; Console.WriteLine("FAIL " + name + ": " + ex.GetType().Name + " " + ex.Message); }
        }
        Console.WriteLine($"{Tests.Count - failed}/{Tests.Count} passed");
        return failed == 0 ? 0 : 1;
    }

    private static async Task<int> FakeServer(string mode)
    {
        var initialized = false; var reads = 0;
        while (await Console.In.ReadLineAsync() is { } line)
        {
            using var doc = JsonDocument.Parse(line); var request = doc.RootElement;
            var method = request.GetProperty("method").GetString();
            if (method == "initialized") { initialized = true; continue; }
            var id = request.GetProperty("id").GetInt64();
            if (method == "initialize")
            {
                if (mode != "silent-init") await Send(new { id, result = new { } });
                continue;
            }
            if (!initialized) return 2;
            reads++;
            if (mode == "silent") continue;
            if (mode == "eof") return 0;
            if (mode == "malformed") { Console.WriteLine("{broken private-content"); continue; }
            if (mode == "non-object") { Console.WriteLine("[]"); continue; }
            if (mode == "error") { await Send(new { id, error = new { code = 401, message = "private-content MUST NOT LEAK" } }); continue; }
            if (mode == "unknown-id") await Send(new { id = 99999, result = new { } });
            if (mode == "delayed" && reads == 1) await Task.Delay(250);
            var sample = Sample(); sample["rateLimits"]!["primary"]!["usedPercent"] = 25;
            await Send(new { id, result = sample });
            if (mode == "updated") { sample["rateLimits"]!["primary"]!["usedPercent"] = 26; await Send(new { method = "account/rateLimits/updated", @params = sample }); }
            if (mode == "bad-update") { sample["rateLimits"]!["primary"] = null; await Send(new { method = "account/rateLimits/updated", @params = sample }); }
        }
        return 0;
    }
    private static Task Send(object value) => Console.Out.WriteLineAsync(JsonSerializer.Serialize(value));
}
