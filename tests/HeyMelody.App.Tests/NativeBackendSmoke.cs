using System.Text.Json;
using System.Threading.Channels;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using FluentAvalonia.UI.Controls;
using HeyMelodyWindows.App;
using HeyMelodyWindows.Ui;

/// <summary>Exercises the real main-window event dispatcher and tray exit dialogs using only an in-memory backend.</summary>
internal static class NativeBackendSmoke
{
    private static State _state = new();
    public static int Run()
    {
        if (!OperatingSystem.IsWindows()) return 2;
        _state = new();
        var thread = new Thread(() =>
        {
            try { AppBuilder.Configure<SmokeApplication>().UsePlatformDetect().UseHeyMelodyTypography()
                    .StartWithClassicDesktopLifetime([], ShutdownMode.OnExplicitShutdown); }
            catch (Exception error) { _state.Failure = error.ToString(); }
        }) { IsBackground = true, Name = "HeyMelody event window smoke" };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(20))) _state.Failure ??= "Native event window did not stop in twenty seconds.";
        var success = _state.Failure is null && _state.Closed && _state.Service.Disposals == 1 && _state.Checks >= 8;
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            success, passed = _state.Checks, nativeBluetoothCalls = 0, applicationSetSubmissions = 0,
            serviceDisposals = _state.Service.Disposals, windowClosed = _state.Closed,
            scope = "real Win32 main window; cross-thread backend push, activation, cancelled and approved EQ exit votes; in-memory backend only",
            failure = _state.Failure
        }));
        return success ? 0 : 1;
    }
    private sealed class State
    {
        public PushService Service { get; } = new();
        public int Checks;
        public bool Closed;
        public string? Failure;
        public TaskCompletionSource ClosedSignal { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    public sealed class SmokeApplication : App
    {
        public override void OnFrameworkInitializationCompleted()
        {
            var desktop = (IClassicDesktopStyleApplicationLifetime)ApplicationLifetime!;
            var window = new MainWindow(_state.Service, preview: false)
            { ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual, Position = new(-10000, -10000) };
            desktop.MainWindow = window;
            window.Closed += (_, _) => { _state.Closed = true; _state.ClosedSignal.TrySetResult(); };
            window.Opened += (_, _) => _ = VerifyAsync(window, desktop);
            _ = WatchdogAsync(desktop);
        }
    }
    private static async Task VerifyAsync(MainWindow window, IClassicDesktopStyleApplicationLifetime desktop)
    {
        MainViewModel? vm = null;
        try
        {
            var page = window.FindControl<MainView>("Page")!;
            vm = (MainViewModel)page.DataContext!;
            await UntilAsync(() => vm.HasVerifiedSnapshot, "The registered event backend initializes the window without Connect.");
            Check(_state.Service.Subscriptions == 1 && _state.Service.Selections == 1, "The main window subscribes and attaches exactly once.");
            var update = _state.Service.Next(); update.LeftBattery = 44; update.GameMode = true;
            await Task.Run(() => _state.Service.Publish(new() { Kind = "snapshot", BackendInstanceId = update.BackendInstanceId, Snapshot = update }));
            await UntilAsync(() => vm.LeftBatteryText == "44%" && vm.GameMode == true, "A background-thread event is adopted on the native UI thread.");
            window.WindowState = WindowState.Minimized;
            await Task.Run(() => _state.Service.Publish(new() { Kind = "activate" }));
            await UntilAsync(() => window.WindowState == WindowState.Normal, "Backend activation restores the minimized main window.");
            vm.EqName = "托盘关闭草稿";
            await Task.Run(() => _state.Service.Publish(new() { Kind = "requestExit", ExitRequestId = "cancelled-vote" }));
            var dialog = await FindDraftDialogAsync(window); dialog.Hide();
            var cancelled = await _state.Service.Votes.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));
            Check(cancelled == ("cancelled-vote", false) && vm.EqDirty && !vm.Closing && vm.CanEditEq,
                "Cancelling the existing EQ dialog denies tray exit and restores the draft editor.");
            await Task.Run(() => _state.Service.Publish(new() { Kind = "requestExit", ExitRequestId = "approved-vote" }));
            dialog = await FindDraftDialogAsync(window);
            var approve = dialog.GetVisualDescendants().OfType<Button>().First(button => Equals(button.Content, dialog.PrimaryButtonText));
            approve.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var approved = await _state.Service.Votes.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));
            Check(approved == ("approved-vote", true) && vm.Closing && _state.Service.Disposals == 0,
                "Approving the draft vote leaves the UI subscribed until the backend commits stopping.");
            await Task.Run(() => _state.Service.Publish(new() { Kind = "stopping" }));
            await _state.ClosedSignal.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Check(_state.Service.Disposals == 1 && _state.Service.Pauses == 0,
                "Stopping closes the main window once without requesting a device disconnect.");
            Check(!window.GetVisualDescendants().OfType<FAContentDialog>().Any(dialog => dialog.IsVisible),
                "An approved backend exit does not open a second draft confirmation.");
        }
        catch (Exception error) { _state.Failure = error.ToString(); }
        finally
        {
            if (!_state.Closed)
            {
                foreach (var dialog in window.GetVisualDescendants().OfType<FAContentDialog>().ToArray()) dialog.Hide();
                if (vm is not null) await vm.DisposeAsync();
                window.Close();
            }
            desktop.Shutdown(_state.Failure is null ? 0 : 1);
        }
    }
    private static async Task<FAContentDialog> FindDraftDialogAsync(MainWindow window)
    {
        for (var attempt = 0; attempt < 80; attempt++)
        {
            var dialog = window.GetVisualDescendants().OfType<FAContentDialog>()
                .FirstOrDefault(dialog => dialog.IsVisible && Equals(dialog.Title, "关闭客户端"));
            if (dialog is not null) return dialog;
            await Task.Delay(25);
        }
        throw new TimeoutException("The native tray exit draft dialog did not open.");
    }
    private static async Task UntilAsync(Func<bool> ready, string message)
    {
        for (var attempt = 0; !ready() && attempt < 80; attempt++) await Task.Delay(25);
        Check(ready(), message);
    }
    private static void Check(bool ready, string message)
    { if (!ready) throw new InvalidOperationException(message); _state.Checks++; }
    private static async Task WatchdogAsync(IClassicDesktopStyleApplicationLifetime desktop)
    {
        await Task.Delay(TimeSpan.FromSeconds(12));
        if (_state.Closed) return;
        _state.Failure ??= "Native backend smoke watchdog expired.";
        desktop.Shutdown(1);
    }
    private sealed class PushService : IHeadsetEventService
    {
        private readonly PreviewService _inner = new();
        private UiSnapshot? _snapshot;
        private IReadOnlyList<UiDevice> _devices = [];
        public event Action<BackendEvent>? EventReceived;
        public Channel<(string, bool)> Votes { get; } = Channel.CreateUnbounded<(string, bool)>();
        public int Subscriptions, Selections, Disposals, Pauses;
        public UiSnapshot Next()
        {
            var snapshot = JsonSerializer.Deserialize<UiSnapshot>(JsonSerializer.Serialize(_snapshot))!;
            snapshot.Revision++; _snapshot = snapshot; return snapshot;
        }
        public void Publish(BackendEvent notification) => EventReceived?.Invoke(notification);
        public async Task<BackendSubscription> SubscribeAsync(string role, CancellationToken token)
        {
            Subscriptions++; _devices = (await _inner.EnumerateAsync(token)).Take(1).ToArray();
            _snapshot = await _inner.ConnectAsync(_devices[0].Identity, token);
            _snapshot.BackendInstanceId = "native-test-backend"; _snapshot.DeviceGeneration = 1; _snapshot.Revision = 1;
            _snapshot.ControlReady = true; _snapshot.MonitoringState = MonitoringState.Monitoring;
            return new() { BackendInstanceId = _snapshot.BackendInstanceId, Devices = _devices, Snapshots = [_snapshot] };
        }
        public Task<IReadOnlyList<UiDevice>> EnumerateAsync(CancellationToken token) => Task.FromResult(_devices);
        public Task<UiSnapshot> SelectAsync(string identity, CancellationToken token) { Selections++; return Task.FromResult(_snapshot!); }
        public Task<UiSnapshot> PauseAsync(string identity, CancellationToken token) { Pauses++; throw new InvalidOperationException(); }
        public Task<UiSnapshot> ResumeAsync(string identity, CancellationToken token) => throw new InvalidOperationException();
        public Task<UiSnapshot> RefreshAsync(string identity, RefreshScope scope, CancellationToken token) => Task.FromResult(_snapshot!);
        public Task<UiApplyResult> ApplyAsync(string identity, long generation, UiAction action, CancellationToken token) => throw new InvalidOperationException();
        public Task AcknowledgeExitAsync(string requestId, bool approved, CancellationToken token) { Votes.Writer.TryWrite((requestId, approved)); return Task.CompletedTask; }
        public Task ActivateMainUiAsync(CancellationToken token) => Task.CompletedTask;
        public Task<UiSnapshot> ConnectAsync(string identity, CancellationToken token) => throw new InvalidOperationException();
        public Task DisconnectAsync(CancellationToken token) => throw new InvalidOperationException();
        public Task<UiSnapshot> RefreshAsync(CancellationToken token) => throw new InvalidOperationException();
        public Task<UiApplyResult> ApplyAsync(UiAction action, CancellationToken token) => throw new InvalidOperationException();
        public ValueTask DisposeAsync() { Disposals++; return _inner.DisposeAsync(); }
    }
}
