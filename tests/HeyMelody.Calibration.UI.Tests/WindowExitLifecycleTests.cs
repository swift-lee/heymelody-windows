using Avalonia;
using Avalonia.Controls;
using FluentAvalonia.Styling;
using HeyMelody.TestSupport;
using HeyMelodyWindows.App;
using HeyMelodyWindows.Calibration.UI;
using HeyMelodyWindows.Ui;
using static HeyMelody.TestSupport.NativeWindowLifetimeHarness;

internal static class WindowExitLifecycleTests
{
    private static void Check(bool condition, string message) => NativeWindowLifetimeHarness.Check(condition, message);
    internal static int Run() => NativeWindowLifetimeHarness.Run<ExitApplication>(VerifyAsync, "calibration");
    public sealed class ExitApplication : Application
    {
        public override void Initialize()
        {
            Styles.Add(new FluentAvaloniaTheme()); Styles.Add(SharedTheme.Create());
            Typography.ApplyThemeResources(this);
        }
        public override void OnFrameworkInitializationCompleted() => Ready(this);
    }

    private static async Task VerifyAsync()
    {
        foreach (var kind in new[] { "fit", "hearing" })
        {
            var service = new ExitService();
            var window = await OpenAsync(service, kind);
            service.Push("requestExit", "prepared");
            await UntilAsync(() => service.Votes.Count == 1, "Calibration did not deliver its STOP vote.");
            Check(service.Votes[0].Approved && service.State.Stage == "Cancelled" && window.IsVisible && service.Disposals == 0,
                "A successful calibration STOP closed the window before global commit.");
            service.Push("exitCancelled", "prepared"); await Task.Delay(30);
            Check(window.IsVisible && service.Cancels == 1 && service.Inputs.Count == 0,
                "A veto must preserve the safely stopped calibration without resuming a test.");
            await StopAsync(window, service);
            Check(service.Disposals == 1 && service.Cancels == 1, "Final stopping repeated the calibration STOP or disposal.");
            Pass(kind + " waits for commit after STOP, veto leaves it stopped, duplicate stopping closes once");
        }

        var failing = new ExitService { StopFailuresRemaining = 2 };
        var active = await OpenAsync(failing, "hearing");
        active.Close();
        await UntilAsync(() => failing.Cancels == 1, "Local calibration close did not request STOP.");
        await Task.Delay(50);
        Check(active.IsVisible && failing.Disposals == 0, "Unconfirmed STOP must keep the calibration window open for retry.");
        failing.Push("requestExit", "unsafe-stop");
        await UntilAsync(() => failing.Votes.Count == 1, "Failed STOP did not veto the backend exit.");
        Check(!failing.Votes[0].Approved && active.IsVisible, "A failed STOP was incorrectly approved.");
        failing.Push("exitCancelled", "unsafe-stop");
        await StopAsync(active, failing);
        Check(failing.Cancels == 2 && failing.Disposals == 1, "Terminal stopping issued another business RPC.");
        Pass("unconfirmed STOP preserves the window and vetoes exit; terminal stopping does not issue another STOP");

        var deferred = new ExitService { PendingCancel = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        active = await OpenAsync(deferred, "fit");
        deferred.Push("requestExit", "late-stop");
        await UntilAsync(() => deferred.CancelAttempts == 1, "Deferred calibration STOP did not start.");
        deferred.Push("exitCancelled", "late-stop");
        deferred.PendingCancel.TrySetResult();
        await UntilAsync(() => deferred.Cancels == 1, "Deferred STOP did not complete.");
        await Task.Delay(30);
        Check(deferred.Votes.Count == 0 && active.IsVisible, "A late STOP acknowledged or closed an already cancelled request.");
        await StopAsync(active, deferred);
        Pass("late calibration STOP completion cannot vote for an already cancelled request");

        var early = new ExitService { ReplayStopping = true, PendingSubscription = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        active = new CalibrationWindow(early, "operation", "fit") { ShutdownApplicationOnClose = false };
        Track(active, () => early.Push("stopping")); active.Show();
        await UntilAsync(() => early.Disposals == 1 && !active.IsVisible, "Early stopping left a calibration window behind.");
        Check(early.Cancels == 0 && early.Reads == 0, "Early terminal close started a new calibration operation.");
        early.PendingSubscription.TrySetResult();
        await Task.Delay(30);
        Pass("calibration stopping before subscription completes still closes without a STOP RPC");

        var broken = new ExitService { VoteFailure = new IOException("fixture original pipe closed") };
        active = await OpenAsync(broken, "hearing");
        broken.Push("requestExit", "broken-pipe");
        await UntilAsync(() => broken.Disposals == 1 && !active.IsVisible, "Approved calibration exit was trapped by its missing original pipe.");
        Check(broken.Votes.Count == 1 && broken.Cancels == 1, "Broken-pipe fallback repeated STOP or vote.");
        Pass("approved calibration vote with a broken original pipe can close locally");

        var cancelled = new ExitService { VoteFailure = new OperationCanceledException("fixture request was already cancelled in the client") };
        active = await OpenAsync(cancelled, "hearing");
        cancelled.Push("requestExit", "cancelled-in-client");
        await UntilAsync(() => cancelled.Votes.Count == 1, "Cancelled calibration vote did not reach its client.");
        await Task.Delay(30);
        Check(active.IsVisible && cancelled.Disposals == 0, "A cancelled vote closed calibration before its delayed UI cancellation event.");
        cancelled.Push("exitCancelled", "cancelled-in-client");
        await StopAsync(active, cancelled);
        Pass("calibration client-side vote cancellation preserves the safely stopped window");

        var cleanup = new ExitService { DisposeFailure = true };
        active = await OpenAsync(cleanup, "fit");
        await StopAsync(active, cleanup);
        Check(cleanup.Disposals == 1, "Calibration cleanup failure retried disposal.");
        Pass("calibration client cleanup faults cannot retain a disabled window");

        var lateTerminal = new ExitService { PendingDispose = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        active = await OpenAsync(lateTerminal, "fit");
        active.Close();
        await UntilAsync(() => lateTerminal.Disposals == 1, "Local calibration close did not enter client disposal.");
        await StopAsync(active, lateTerminal);
        Check(lateTerminal.Cancels == 1 && lateTerminal.Disposals == 1, "Late stopping repeated calibration STOP or disposal.");
        lateTerminal.PendingDispose.TrySetResult();
        Pass("stopping arriving during an approved local calibration cleanup still finalizes the window");

        var hanging = new ExitService { PendingCancel = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        active = await OpenAsync(hanging, "hearing");
        active.Close();
        await UntilAsync(() => hanging.CancelAttempts == 1, "Local calibration close did not start the pending STOP.");
        await StopAsync(active, hanging);
        Check(hanging.Disposals == 1, "Terminal stopping remained blocked on the superseded local STOP.");
        hanging.PendingCancel.TrySetResult();
        Pass("terminal calibration stopping bypasses an already pending local STOP wait");
    }

    private static async Task<CalibrationWindow> OpenAsync(ExitService service, string kind)
    {
        var window = new CalibrationWindow(service, "operation", kind) { ShutdownApplicationOnClose = false };
        Track(window, () => service.Push("stopping")); window.Show();
        await UntilAsync(() => service.Subscriptions == 1 && service.Reads == 1, "Calibration fixture did not initialize.");
        return window;
    }
    private static async Task StopAsync(CalibrationWindow window, ExitService service)
    {
        var closed = 0; window.Closed += (_, _) => closed++;
        service.Push("stopping"); service.Push("stopping");
        await UntilAsync(() => closed == 1 && !window.IsVisible, "Calibration did not finalize terminal close.");
        await Task.Delay(30); Check(closed == 1, "Calibration closed more than once.");
    }

    private sealed class ExitService : TestService, ICalibrationService, IHeadsetEventService
    {
        private Action<BackendEvent>? _events;
        internal bool ReplayStopping, DisposeFailure;
        internal int Subscriptions, Disposals, Reads, CancelAttempts;
        internal Exception? VoteFailure;
        internal TaskCompletionSource? PendingCancel, PendingSubscription, PendingDispose;
        internal readonly List<(string Request, bool Approved)> Votes = [];
        public event Action<BackendEvent>? EventReceived
        { add { _events += value; if (ReplayStopping) value?.Invoke(new() { Kind = "stopping" }); } remove => _events -= value; }
        internal void Push(string kind, string? request = null) => _events?.Invoke(new() { Kind = kind, ExitRequestId = request });
        public async Task<BackendSubscription> SubscribeAsync(string role, CancellationToken token)
        { Subscriptions++; if (PendingSubscription is not null) await PendingSubscription.Task; return new(); }
        public new Task<UiCalibrationState> GetCalibrationAsync(string handle, CancellationToken token)
        { token.ThrowIfCancellationRequested(); Reads++; return base.GetCalibrationAsync(handle, token); }
        public new async Task<UiCalibrationState> CancelCalibrationAsync(string handle, CancellationToken token)
        { CancelAttempts++; if (PendingCancel is not null) await PendingCancel.Task; return await base.CancelCalibrationAsync(handle, token); }
        public Task AcknowledgeExitAsync(string request, bool approved, CancellationToken token)
        { Votes.Add((request, approved)); return VoteFailure is null ? Task.CompletedTask : Task.FromException(VoteFailure); }
        public ValueTask DisposeAsync()
        { Disposals++; return DisposeFailure ? ValueTask.FromException(new IOException("fixture cleanup failed")) : new(PendingDispose?.Task ?? Task.CompletedTask); }
        public Task<IReadOnlyList<UiDevice>> EnumerateAsync(CancellationToken token) => throw new NotSupportedException();
        public Task<UiSnapshot> ConnectAsync(string identity, CancellationToken token) => throw new NotSupportedException();
        public Task DisconnectAsync(CancellationToken token) => throw new NotSupportedException();
        public Task<UiSnapshot> RefreshAsync(CancellationToken token) => throw new NotSupportedException();
        public Task<UiApplyResult> ApplyAsync(UiAction action, CancellationToken token) => throw new NotSupportedException();
        public Task<UiSnapshot> SelectAsync(string identity, CancellationToken token) => throw new NotSupportedException();
        public Task<UiSnapshot> PauseAsync(string identity, CancellationToken token) => throw new NotSupportedException();
        public Task<UiSnapshot> ResumeAsync(string identity, CancellationToken token) => throw new NotSupportedException();
        public Task<UiSnapshot> RefreshAsync(string identity, RefreshScope scope, CancellationToken token) => throw new NotSupportedException();
        public Task<UiApplyResult> ApplyAsync(string identity, long generation, UiAction action, CancellationToken token) => throw new NotSupportedException();
        public Task ActivateMainUiAsync(CancellationToken token) => throw new NotSupportedException();
    }
}
