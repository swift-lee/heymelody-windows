using System.Text;
using System.Text.Json;
using HeyMelody.Diagnostics;

internal static class WriterFailureChecks
{
    internal static async Task RunAsync(string root, Action<bool, string> check)
    {
        DiagnosticSessionInfo Session(string name) => new()
        {
            Enabled = true, SessionId = Guid.NewGuid().ToString("N"), Directory = Path.Combine(root, name),
            Components = [DiagnosticComponents.Main]
        };
        DiagnosticLog.ResetForTests();
        DiagnosticLog.InitializeIdentity(DiagnosticComponents.Main, "main");
        var blockedStream = new ControlledStream();
        DiagnosticLog.Configure(Session("overflow"), capacity: 2, streamFactory: _ => blockedStream);
        await blockedStream.FirstWrite.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var start = System.Diagnostics.Stopwatch.StartNew();
        for (int index = 0; index < 100; index++) DiagnosticLog.Debug("Fixture", "burst", "queued", new { index });
        check(start.Elapsed < TimeSpan.FromSeconds(1) && DiagnosticLog.LastFailure is not null && DiagnosticLog.IsEnabled,
            "slow disk overflow is nonblocking and reports a sticky incomplete state");
        blockedStream.Release.TrySetResult();
        DiagnosticLog.Stop("overflow completed");
        var rows = Encoding.UTF8.GetString(blockedStream.ToArray()).Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => JsonDocument.Parse(line).RootElement.Clone()).ToArray();
        var dropped = rows.Where(row => row.GetProperty("eventName").GetString() == "logging.dropped")
            .Sum(row => row.GetProperty("data").GetProperty("lostEvents").GetInt32());
        var retained = rows.Count(row => row.GetProperty("eventName").GetString() != "logging.dropped");
        check(dropped > 0 && retained + dropped == 102, "overflow count survives a simultaneous exit flush");

        DiagnosticLog.ResetForTests();
        DiagnosticLog.InitializeIdentity(DiagnosticComponents.Main, "main");
        var failingStream = new ControlledStream { Failure = new IOException("fixture disk full") };
        failingStream.Release.TrySetResult();
        DiagnosticLog.Configure(Session("write-failure"), streamFactory: _ => failingStream);
        var wait = System.Diagnostics.Stopwatch.StartNew();
        while (DiagnosticLog.LastFailure is null && wait.Elapsed < TimeSpan.FromSeconds(3)) await Task.Delay(10);
        check(!DiagnosticLog.IsEnabled && DiagnosticLog.LastFailure?.Detail?.Contains("fixture disk full") == true,
            "midstream write failure disables only writer and preserves original exception");
        DiagnosticLog.Debug("Fixture", "ignoredAfterFailure", "application can continue");
        check(!DiagnosticLog.IsEnabled, "write failure never automatically re-enables logging");
        DiagnosticLog.ResetForTests();

        DiagnosticLog.InitializeIdentity(DiagnosticComponents.Main, "main");
        var delayedStream = new ControlledStream();
        DiagnosticLog.Configure(Session("flush-timeout"), streamFactory: _ => delayedStream);
        await delayedStream.FirstWrite.Task.WaitAsync(TimeSpan.FromSeconds(3));
        DiagnosticLog.Flush();
        check(DiagnosticLog.LastFailure?.Message.Contains("刷新超时") == true, "bounded flush timeout explicitly reports an incomplete tail");
        delayedStream.Release.TrySetResult();
        DiagnosticLog.ResetForTests();

        DiagnosticSettingsStore.Save([DiagnosticComponents.Host], DiagnosticArmIntent.ArmNew);
        using (DiagnosticRuntime.InitializeHost())
        {
            check(DiagnosticRuntime.ReadCurrentSession() is { Enabled: true } && DiagnosticSettingsStore.Load().Pending is null,
                "Host publishes enabled ready decision after consuming next-start request");
        }
        using (DiagnosticRuntime.InitializeHost())
            check(DiagnosticRuntime.ReadCurrentSession() is { Enabled: false } && !DiagnosticLog.IsEnabled,
                "next Host lifecycle publishes ready-off without another request");
        check(DiagnosticRuntime.ReadCurrentSession() is null, "normal Host teardown removes only its ready descriptor");
    }

    private sealed class ControlledStream : MemoryStream
    {
        internal TaskCompletionSource FirstWrite { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Exception? Failure { get; init; }
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            FirstWrite.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            if (Failure is not null) throw Failure;
            await base.WriteAsync(buffer, cancellationToken);
        }
    }
}
