// Startup sequence informed by QuotaScope, Copyright (c) 2026 HexX, MIT. See THIRD_PARTY_NOTICES.md.
using Microsoft.UI.Dispatching;

namespace CodexQuotaShare.Windows;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        var smokeIndex = Array.IndexOf(args, "--smoke-test");
        var smokePath = smokeIndex >= 0 && smokeIndex + 1 < args.Length ? args[smokeIndex + 1] : null;
        if (smokePath is not null) File.WriteAllText(smokePath + ".trace.txt", "Main entered\n");
        var demo = args.Contains("--demo") || smokePath is not null;
        var suffix = smokePath is null ? "" : ".Smoke";
        using var mutex = new Mutex(true, @"Local\CodexQuotaShare" + suffix, out var first);
        using var toggle = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\CodexQuotaShare.Toggle" + suffix);
        if (!first) { toggle.Set(); return 0; }
        try
        {
        WinRT.ComWrappersSupport.InitializeComWrappers();
        if (smokePath is not null) File.AppendAllText(smokePath + ".trace.txt", "ComWrappers ready\n");
        Microsoft.UI.Xaml.Application.Start(callbackParams =>
        {
            if (smokePath is not null) File.AppendAllText(smokePath + ".trace.txt", "Application callback\n");
            SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));
            _ = new App(toggle, demo, smokePath);
            if (smokePath is not null) File.AppendAllText(smokePath + ".trace.txt", "App constructed\n");
        });
        return 0;
        }
        catch (Exception error) when (smokePath is not null)
        {
            File.WriteAllText(smokePath + ".error.json", System.Text.Json.JsonSerializer.Serialize(new { type = error.GetType().Name, hresult = error.HResult, message = error.Message, stack = error.StackTrace }));
            return 1;
        }
    }
}
