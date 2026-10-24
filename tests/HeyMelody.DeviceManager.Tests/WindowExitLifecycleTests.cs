using Avalonia.Controls;
using HeyMelody.TestSupport;
using HeyMelodyWindows.App;
using HeyMelodyWindows.DeviceManager;
using static HeyMelody.TestSupport.NativeWindowLifetimeHarness;

internal static class WindowExitLifecycleTests
{
    private static void Check(bool condition, string message) => NativeWindowLifetimeHarness.Check(condition, message);
    internal static int Run() => NativeWindowLifetimeHarness.Run<ExitApplication>(VerifyAsync, "manager");
    public sealed class ExitApplication : DeviceManagerApp
    { public override void OnFrameworkInitializationCompleted() => Ready(this); }

    private static async Task VerifyAsync()
    {
        var service = new ExitService();
        var window = await OpenAsync(service);
        var model = (DeviceManagerViewModel)window.FindControl<DeviceManagerView>("Page")!.DataContext!;
        model.Devices[0].IsChecked = true;
        service.Push("requestExit", "prepared");
        await UntilAsync(() => service.Votes.Count == 1, "Manager did not vote.");
        Check(service.Votes[0].Approved && window.IsVisible && service.Disposals == 0 && !model.CanRefresh,
            "Manager closed before commit or failed to suspend its actions.");
        service.Push("exitCancelled", "prepared");
        await UntilAsync(() => model.CanRefresh && model.CanCommit, "Manager did not restore controls after a veto.");
        Check(model.Devices[0].IsChecked && service.Saves == 0, "A veto discarded or saved the user's selection.");
        await StopAsync(window, service);
        Check(service.Disposals == 1, "Manager client was not disposed exactly once.");
        Pass("manager votes without closing, veto keeps the selection, duplicate stopping closes once");

        service = new ExitService { PendingSave = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        window = await OpenAsync(service);
        model = (DeviceManagerViewModel)window.FindControl<DeviceManagerView>("Page")!.DataContext!;
        model.Devices[0].IsChecked = true;
        var save = model.SaveAsync();
        service.Push("requestExit", "cancelled-vote");
        await UntilAsync(() => !model.CanRefresh, "Pending manager operation was not suspended.");
        service.Push("exitCancelled", "cancelled-vote");
        service.PendingSave.TrySetResult(new(false, "fixture did not save"));
        await save;
        await UntilAsync(() => model.CanRefresh, "Manager remained suspended after its cancelled vote.");
        await Task.Delay(30);
        Check(service.Votes.Count == 0 && window.IsVisible && model.Devices[0].IsChecked,
            "A late preparation acknowledged or closed the cancelled manager request.");
        await StopAsync(window, service);
        Pass("manager ignores late preparation after exitCancelled");

        service = new ExitService { VoteFailure = new OperationCanceledException("fixture request was already cancelled in the client") };
        window = await OpenAsync(service);
        model = (DeviceManagerViewModel)window.FindControl<DeviceManagerView>("Page")!.DataContext!;
        service.Push("requestExit", "cancelled-in-client");
        await UntilAsync(() => service.Votes.Count == 1 && model.CanRefresh, "Cancelled client vote did not restore manager controls.");
        Check(window.IsVisible && service.Disposals == 0, "A cancelled vote closed the manager before its delayed UI cancellation event.");
        service.Push("exitCancelled", "cancelled-in-client");
        await StopAsync(window, service);
        Pass("manager client-side vote cancellation does not close its window");

        service = new ExitService { ReplayStopping = true };
        window = new DeviceManagerWindow(service, false) { ShutdownApplicationOnClose = false };
        Track(window, () => service.Push("stopping")); window.Show();
        await UntilAsync(() => service.Disposals == 1 && !window.IsVisible, "Early stopping left a manager window behind.");
        Pass("manager handles stopping before its startup subscription completes");

        service = new ExitService { DisposeFailure = true };
        window = await OpenAsync(service);
        await StopAsync(window, service);
        Check(service.Disposals == 1, "A manager cleanup fault caused a second disposal.");
        Pass("manager cleanup faults still finalize the window");

        service = new ExitService { PendingDispose = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        window = await OpenAsync(service);
        await StopAsync(window, service);
        Check(service.Disposals == 1, "Terminal manager close did not bound a stalled cleanup.");
        service.PendingDispose.TrySetResult();
        Pass("manager terminal cleanup has a bounded wait");
    }

    private static async Task<DeviceManagerWindow> OpenAsync(ExitService service)
    {
        var window = new DeviceManagerWindow(service, false) { ShutdownApplicationOnClose = false };
        Track(window, () => service.Push("stopping")); window.Show();
        await UntilAsync(() => service.Subscriptions == 1 &&
            ((DeviceManagerViewModel)window.FindControl<DeviceManagerView>("Page")!.DataContext!).HasDevices,
            "Manager fixture did not initialize.");
        return window;
    }
    private static async Task StopAsync(DeviceManagerWindow window, ExitService service)
    {
        var closed = 0; window.Closed += (_, _) => closed++;
        service.Push("stopping"); service.Push("stopping");
        await UntilAsync(() => closed == 1 && !window.IsVisible, "Manager did not finalize terminal close.");
        await Task.Delay(30); Check(closed == 1, "Manager closed more than once.");
    }

    private sealed class ExitService : IDeviceManagementService, IHeadsetEventService
    {
        private Action<BackendEvent>? _events;
        internal bool ReplayStopping, DisposeFailure;
        internal Exception? VoteFailure;
        internal int Subscriptions, Disposals, Saves;
        internal TaskCompletionSource<UiWhitelistSaveResult>? PendingSave;
        internal TaskCompletionSource? PendingDispose;
        internal readonly List<(string Request, bool Approved)> Votes = [];
        public event Action<BackendEvent>? EventReceived
        { add { _events += value; if (ReplayStopping) value?.Invoke(new() { Kind = "stopping" }); } remove => _events -= value; }
        internal void Push(string kind, string? request = null) => _events?.Invoke(new() { Kind = kind, ExitRequestId = request });
        public Task<UiDeviceCatalog> EnumerateCandidatesAsync(CancellationToken token)
            => Task.FromResult(new UiDeviceCatalog([new("fixture", "Fixture headset", false, true)]));
        public Task<UiWhitelistSaveResult> SaveWhitelistAsync(IReadOnlyList<string> identities, CancellationToken token)
        { Saves++; return PendingSave?.Task.WaitAsync(token) ?? Task.FromResult(new UiWhitelistSaveResult(false)); }
        public Task<BackendSubscription> SubscribeAsync(string role, CancellationToken token)
        { Subscriptions++; return Task.FromResult(new BackendSubscription()); }
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
