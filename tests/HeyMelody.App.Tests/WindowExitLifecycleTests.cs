using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using HeyMelody.Rendering.Filament;
using HeyMelody.TestSupport;
using HeyMelodyWindows.App;
using static HeyMelody.TestSupport.NativeWindowLifetimeHarness;

internal static class WindowExitLifecycleTests
{
    private static void Check(bool condition, string message) => NativeWindowLifetimeHarness.Check(condition, message);
    internal static int Run()
    {
        DeviceModelView.DefaultFactoryOverride = new NoNativeRenderer();
        return NativeWindowLifetimeHarness.Run<ExitApplication>(VerifyAsync, "main");
    }

    public sealed class ExitApplication : App
    {
        public override void OnFrameworkInitializationCompleted() => Ready(this);
    }

    private static async Task VerifyAsync()
    {
        if (OperatingSystem.IsMacOS())
        {
            var quitService = new ExitService();
            var quitWindow = await OpenAsync(quitService);
            await VerifyBackdropAsync(quitWindow);
            var desktop = (IClassicDesktopStyleApplicationLifetime)Application.Current!.ApplicationLifetime!;
            Check(!desktop.TryShutdown(), "Native Quit bypassed the coordinated exit request.");
            await UntilAsync(() => quitService.ExitRequests == 1, "Native Quit did not request product exit.");
            quitService.Push("requestExit", "native-quit-veto");
            await UntilAsync(() => quitService.Votes.Count == 1, "Native Quit was not prepared.");
            quitService.Push("exitCancelled", "native-quit-veto");
            Check(quitWindow.IsVisible, "A cancelled native Quit closed the window.");
            Check(!desktop.TryShutdown(), "A second native Quit bypassed coordination.");
            await UntilAsync(() => quitService.ExitRequests == 2, "A retry of native Quit was not delivered.");
            await StopAsync(quitWindow, quitService);
            Pass("macOS native Quit requests global exit, a veto keeps the window, and commit closes it");
        }
        var service = new ExitService();
        var window = await OpenAsync(service);
        var model = (MainViewModel)window.FindControl<MainView>("Page")!.DataContext!;
        service.Push("requestExit", "prepared");
        await UntilAsync(() => service.Votes.Count == 1, "Main exit vote was not delivered.");
        Check(service.Votes[0].Approved && window.IsVisible && service.Disposals == 0,
            "A prepared main window closed before the Host committed.");
        service.Push("exitCancelled", "prepared");
        await UntilAsync(() => !model.Closing, "Main actions did not recover after a veto.");
        Check(window.IsVisible && !model.CloseCommitted, "Cancelling the global exit must preserve the main window.");
        await StopAsync(window, service);
        Check(service.Disposals == 1 && service.ResumesAfterStop == 0, "Terminal main close must dispose once without resuming a connection.");
        Pass("main vote waits for commit, veto restores actions, duplicate stopping closes once");

        var confirmation = new Confirmation(false);
        service = new ExitService();
        window = await OpenAsync(service, confirmation, preview: false);
        model = (MainViewModel)window.FindControl<MainView>("Page")!.DataContext!;
        await model.ConnectCommand.ExecuteAsync(null);
        model.EqName = "退出时保留草稿";
        Check(model.EqDirty, "The fixture did not establish an unsaved EQ draft.");
        service.Push("requestExit", "veto");
        await UntilAsync(() => service.Votes.Count == 1, "Draft veto was not delivered.");
        Check(!service.Votes[0].Approved && model.EqDirty && model.EqName == "退出时保留草稿" && window.IsVisible,
            "A veto must retain the EQ draft and its window.");
        service.Push("exitCancelled", "veto");
        await UntilAsync(() => !model.Closing, "Vetoed draft controls remained suspended.");
        await StopAsync(window, service);
        Check(confirmation.Calls == 1 && service.Submissions == 0, "Final stopping repeated the draft question or submitted the draft.");
        Pass("main veto keeps the unsaved draft and terminal close does not ask twice");

        confirmation = new Confirmation(true) { Deferred = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        service = new ExitService();
        window = await OpenAsync(service, confirmation, preview: false);
        model = (MainViewModel)window.FindControl<MainView>("Page")!.DataContext!;
        await model.ConnectCommand.ExecuteAsync(null);
        model.EqName = "超时后保留草稿";
        service.Push("requestExit", "cancelled-vote");
        await UntilAsync(() => confirmation.Calls == 1, "The deferred exit confirmation did not start.");
        service.Push("exitCancelled", "cancelled-vote");
        confirmation.Deferred.TrySetResult(true);
        await UntilAsync(() => !model.Closing, "A late answer re-suspended a cancelled exit.");
        await Task.Delay(50);
        Check(service.Votes.Count == 0 && model.EqDirty && window.IsVisible,
            "A late answer must not acknowledge or close an already cancelled request.");
        await StopAsync(window, service);
        Pass("a late confirmation after exitCancelled cannot close or acknowledge the old request");

        service = new ExitService { VoteFailure = new IOException("fixture original pipe closed") };
        window = await OpenAsync(service);
        service.Push("requestExit", "broken-pipe");
        await UntilAsync(() => !window.IsVisible, "An explicitly approved vote trapped its window after the original pipe disappeared.");
        Check(service.Votes.Count == 1 && service.Disposals == 1 && service.ResumesAfterStop == 0,
            "Approved transport failure must close locally without another vote or resume.");
        Pass("approved exit with a broken original pipe closes locally without reconnecting");

        service = new ExitService { VoteFailure = new OperationCanceledException("fixture request was already cancelled in the client") };
        window = await OpenAsync(service);
        model = (MainViewModel)window.FindControl<MainView>("Page")!.DataContext!;
        service.Push("requestExit", "cancelled-in-client");
        await UntilAsync(() => service.Votes.Count == 1 && !model.Closing, "Cancelled client vote did not restore main actions.");
        Check(window.IsVisible && service.Disposals == 0, "A cancelled vote closed the main window before its delayed UI cancellation event.");
        service.Push("exitCancelled", "cancelled-in-client");
        await StopAsync(window, service);
        Pass("client-side vote cancellation cannot be mistaken for terminal transport failure");

        service = new ExitService { DisposeFailure = true };
        window = await OpenAsync(service);
        await StopAsync(window, service);
        Check(service.Disposals == 1, "Disposal failure retried or trapped main close.");
        Pass("main cleanup faults cannot leave a disabled residual window");

        confirmation = new Confirmation(true) { Failure = new IOException("fixture confirmation failed") };
        service = new ExitService();
        window = await OpenAsync(service, confirmation, preview: false);
        model = (MainViewModel)window.FindControl<MainView>("Page")!.DataContext!;
        await model.ConnectCommand.ExecuteAsync(null);
        model.EqName = "确认失败仍保留草稿";
        window.Close();
        await UntilAsync(() => confirmation.Calls == 1 && !model.Closing, "Failed local confirmation did not restore the main window.");
        Check(window.IsVisible && model.EqDirty && service.Disposals == 0,
            "An unapproved local close failure must preserve the window, controls and draft.");
        await StopAsync(window, service);
        Pass("an unapproved local close fault restores the window and preserves its draft");

        service = new ExitService { PendingDispose = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        window = await OpenAsync(service);
        window.Close();
        await UntilAsync(() => service.Disposals == 1, "Local main close did not enter client disposal.");
        await StopAsync(window, service);
        Check(service.Disposals == 1, "Late terminal stopping retried main client disposal.");
        service.PendingDispose.TrySetResult();
        Pass("stopping arriving during an approved local cleanup still finalizes the main window");

        confirmation = new Confirmation(true) { Deferred = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        service = new ExitService();
        window = await OpenAsync(service, confirmation, preview: false);
        model = (MainViewModel)window.FindControl<MainView>("Page")!.DataContext!;
        await model.ConnectCommand.ExecuteAsync(null);
        model.EqName = "本地关闭尚待确认";
        window.Close();
        await UntilAsync(() => confirmation.Calls == 1, "The local close confirmation did not start.");
        await StopAsync(window, service);
        Check(service.Disposals == 1 && service.Submissions == 0, "A terminal close waited forever for the superseded local confirmation.");
        Pass("stopping completes a close already waiting for a local confirmation");
    }

    private static async Task<MainWindow> OpenAsync(ExitService service, IConfirmationService? confirmation = null, bool preview = true)
    {
        var window = new MainWindow(service, preview, manager: null, confirmations: confirmation);
        Track(window, () => service.Push("stopping"));
        window.Show();
        await UntilAsync(() => service.Subscriptions != 0 && ((MainViewModel)window.FindControl<MainView>("Page")!.DataContext!).SelectedDevice is not null,
            "Main fixture initialization did not complete.");
        return window;
    }

    private static async Task StopAsync(MainWindow window, ExitService service)
    {
        var closed = 0;
        window.Closed += (_, _) => closed++;
        service.Push("stopping"); service.Push("stopping");
        await UntilAsync(() => closed == 1 && !window.IsVisible, "Main window did not finish its terminal close.");
        await Task.Delay(30);
        Check(closed == 1, "Duplicate stopping closed the same main window more than once.");
    }

    private sealed class Confirmation(bool answer) : IConfirmationService
    {
        internal int Calls;
        internal TaskCompletionSource<bool>? Deferred;
        internal Exception? Failure;
        public Task<bool> ConfirmAsync(string title, string message, CancellationToken token)
        { Calls++; return Failure is not null ? Task.FromException<bool>(Failure) : Deferred is null ? Task.FromResult(answer) : Deferred.Task.WaitAsync(token); }
    }

    private sealed class ExitService : InputTestService, IHeadsetService, IHeadsetEventService
    {
        public event Action<BackendEvent>? EventReceived;
        internal readonly List<(string Request, bool Approved)> Votes = [];
        internal int Subscriptions, Disposals, ResumesAfterStop;
        internal int ExitRequests;
        public Task RequestApplicationExitAsync(CancellationToken token) { ExitRequests++; return Task.CompletedTask; }
        internal bool DisposeFailure, Stopping;
        internal Exception? VoteFailure;
        internal TaskCompletionSource? PendingDispose;
        internal void Push(string kind, string? request = null)
        {
            if (kind == "stopping") Stopping = true;
            EventReceived?.Invoke(new BackendEvent { Kind = kind, ExitRequestId = request, BackendInstanceId = "exit-fixture" });
        }
        public Task<BackendSubscription> SubscribeAsync(string role, CancellationToken token)
        { Subscriptions++; return Task.FromResult(new BackendSubscription { BackendInstanceId = "exit-fixture" }); }
        public new async Task<UiSnapshot> ConnectAsync(string identity, CancellationToken token)
        { var snapshot = await base.ConnectAsync(identity, token); snapshot.BackendInstanceId = "exit-fixture"; return snapshot; }
        public Task AcknowledgeExitAsync(string request, bool approved, CancellationToken token)
        { Votes.Add((request, approved)); return VoteFailure is null ? Task.CompletedTask : Task.FromException(VoteFailure); }
        public Task<UiSnapshot> SelectAsync(string identity, CancellationToken token) => ConnectAsync(identity, token);
        public Task<UiSnapshot> PauseAsync(string identity, CancellationToken token) => RefreshAsync(token);
        public Task<UiSnapshot> ResumeAsync(string identity, CancellationToken token)
        { if (Stopping) ResumesAfterStop++; return ConnectAsync(identity, token); }
        public Task<UiSnapshot> RefreshAsync(string identity, RefreshScope scope, CancellationToken token) => RefreshAsync(token);
        public Task<UiApplyResult> ApplyAsync(string identity, long generation, UiAction action, CancellationToken token) => ApplyAsync(action, token);
        public Task ActivateMainUiAsync(CancellationToken token) => Task.CompletedTask;
        public new ValueTask DisposeAsync()
        { Disposals++; return DisposeFailure ? ValueTask.FromException(new IOException("fixture cleanup failed")) : new(PendingDispose?.Task ?? Task.CompletedTask); }
    }

    private sealed class NoNativeRenderer : IFilamentRendererFactory
    {
        public IFilamentRenderer Create(FilamentBackend backend, CancellationToken token = default)
            => throw new NotSupportedException("The lifecycle fixture does not initialize native rendering.");
    }
}
