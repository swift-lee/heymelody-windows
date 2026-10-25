using Avalonia.Controls;
using Avalonia.Threading;
using FluentAvalonia.UI.Controls;
using FluentAvalonia.UI.Windowing;
using HeyMelody.Diagnostics;

namespace HeyMelodyWindows.App;

public partial class MainWindow : FAAppWindow
{
    private readonly MainViewModel _viewModel;
    private readonly IHeadsetService _service;
    private readonly TrackedDeviceManager? _manager;
    private readonly CancellationTokenSource _managerLifetime = new();
    private readonly WindowConfirmationService _confirmations;
    private readonly IHeadsetEventService? _events;
    private Task? _exitVote;
    private string? _exitRequestId;
    private readonly TaskCompletionSource _backendStop = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task? _calibrationOperation;
    private Task? _startupVerification;
    private bool _closing, _closed, _opened, _managing, _backendStopping, _localCloseApproved;
    internal bool StartWithDemo { get; init; }

    public MainWindow() : this(HeadsetServiceFactory.Create(), false,
        OperatingSystem.IsWindows() ? new DeviceManagerProcess() : null) { }

    internal MainWindow(IHeadsetService service) : this(service, false,
        OperatingSystem.IsWindows() ? new DeviceManagerProcess() : null) { }

    internal MainWindow(IHeadsetService service, bool preview) : this(service, preview, null) { }

    internal MainWindow(IHeadsetService service, bool preview, IDeviceManagerProcess? manager,
        IConfirmationService? confirmations = null)
    {
        InitializeComponent();
        Ui.Typography.ApplyTextOptions(this);
        Ui.RenderDiagnostics.Attach(this);
        AnimationYieldTriggers.AttachWindow(this);
        _service = service;
        _events = service as IHeadsetEventService;
        _manager = manager is null ? null : new TrackedDeviceManager(manager);
        _confirmations = new WindowConfirmationService(this);
        _viewModel = new MainViewModel(service, confirmations ?? _confirmations, preview);
        Page.DataContext = _viewModel;
        Page.SetPlatformNotice(!OperatingSystem.IsWindows());
        Page.ManageDevicesRequested = manager is not null ? ManageDevicesAsync : null;
        if (!preview && service is ICalibrationService calibration)
        {
            Page.RequestFitTest = () => LaunchCalibrationAsync("fit", calibration);
            Page.RequestHearingSetup = () => LaunchCalibrationAsync("hearing", calibration);
        }
        Page.IsEnabled = manager is null;
        StartupOverlay.IsVisible = manager is not null;
        Opened += async (_, _) => await InitializeOnceAsync();
        Closing += OnClosing;
        if (_events is not null) _events.EventReceived += OnBackendEvent;
        Closed += (_, _) => { if (_events is not null) _events.EventReceived -= OnBackendEvent; };
    }

    private async Task InitializeOnceAsync()
    {
        if (_opened) return;
        _opened = true;
        try
        {
            await _viewModel.AttachBackendAsync();
            if (_manager is null) await _viewModel.InitializeAsync();
            else if (_viewModel.BackendStartupDevices is { } known)
            {
                // The tray already holds an effective list: show it now and confirm it with a fresh lookup behind
                // the page instead of holding the overlay for a catalog-worker process.
                await _viewModel.InitializeAsync(known);
                if (!_closing) _startupVerification = VerifyStartupDevicesAsync();
            }
            else
            {
                var devices = await ResolveDevicesAsync();
                if (devices is null) { if (!_closing) Close(); return; }
                await _viewModel.InitializeAsync(devices);
            }
            if (!_closing && StartWithDemo) await _viewModel.AddDemoDeviceAsync();
            if (!_closing) { StartupOverlay.IsVisible = false; Page.IsEnabled = true; }
        }
        catch (OperationCanceledException) when (_managerLifetime.IsCancellationRequested) { }
        catch (Exception error)
        {
            if (!_closing) await _viewModel.Dialogs.RunAsync(token => _confirmations.AlertAsync(
                "无法连接蓝牙后台", error is InvalidDataException && error.Message.Contains("版本", StringComparison.Ordinal)
                    ? "蓝牙后台版本不一致，请完整更新客户端后重新启动后台。"
                    : "蓝牙后台未能启动或连接。请尝试重启软件或重新安装。", token), _managerLifetime.Token);
            if (!_closing) Close();
        }
    }

    private async Task VerifyStartupDevicesAsync()
    {
        try
        {
            // A non-empty result reaches the page as the backend's catalog event; a failure keeps the presented
            // selection and is reported by the backend's health event.
            if (!await new DeviceStartupCoordinator(_service, _manager!).ConfirmPresentedAsync(_managerLifetime.Token)) return;
            if (_closing || _managing || _viewModel.ManagingDevices || _viewModel.Closing) return;
            _managing = true;
            try
            {
                Page.IsEnabled = false;
                StartupOverlay.IsVisible = true;
                var devices = await ResolveDevicesAsync();
                if (devices is null) { if (!_closing) Close(); return; }
                // A select or reconnect still in flight would make InitializeAsync a silent no-op.
                while (!_closing && !_viewModel.CanManageDevices) await Task.Delay(50, _managerLifetime.Token);
                if (!_closing) await _viewModel.InitializeAsync(devices);
            }
            finally
            {
                _managing = false;
                if (!_closing) { StartupOverlay.IsVisible = false; Page.IsEnabled = true; }
            }
        }
        catch (OperationCanceledException) when (_managerLifetime.IsCancellationRequested) { }
    }

    private void OnBackendEvent(BackendEvent notification) => Dispatcher.UIThread.Post(async () =>
    {
        if (_closed) return;
        try
        {
            if (notification.Kind == "stopping")
            {
                MarkBackendStopping();
                try { _viewModel.AdoptBackendEvent(notification); }
                finally { _confirmations.CancelActiveDialog(); Close(); }
                return;
            }
            _viewModel.AdoptBackendEvent(notification);
            switch (notification.Kind)
            {
                case "activate": ActivateMainUi(); break;
                case "requestExit" when notification.ExitRequestId is { } requestId && _events is not null:
                    if (_exitRequestId != requestId)
                    {
                        var previous = _exitVote;
                        _exitRequestId = requestId;
                        var vote = VoteAfterAsync(previous, requestId);
                        _exitVote = vote;
                        try { await vote; }
                        finally { if (ReferenceEquals(_exitVote, vote)) _exitVote = null; }
                    }
                    break;
                case "exitCancelled":
                    _exitRequestId = null;
                    if (!_closing && !_backendStopping)
                    {
                        _confirmations.CancelActiveDialog();
                        _viewModel.CancelBackendExit();
                        Page.ResumeSettingsDialogs();
                    }
                    break;
            }
        }
        catch (OperationCanceledException) when (_closing) { }
        catch (Exception error)
        {
            DiagnosticLog.Warning("ui.lifetime", "backendEventFailed", "Main window could not handle a backend event", error, new { notification.Kind, notification.ExitRequestId });
            if (!_closing && !_backendStopping)
            {
                _viewModel.CancelBackendExit();
                Page.ResumeSettingsDialogs();
            }
        }
    });

    private async Task VoteAfterAsync(Task? previous, string requestId)
    {
        if (previous is not null)
        {
            try { await previous; }
            catch (Exception error) { DiagnosticLog.Debug("ui.lifetime", "previousVoteFailed", "The previous exit vote had failed", new { error = error.Message, requestId }); }
        }
        if (_exitRequestId == requestId && !_closed && !_backendStopping)
            await VoteForBackendExitAsync(requestId);
    }

    internal void ActivateMainUi()
    {
        if (_closing || _closed) return;
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Show();
        Activate();
    }

    private async Task VoteForBackendExitAsync(string requestId)
    {
        if (_closing)
        {
            // A local confirmation already in progress must not be replaced by a second dialog.
            await _events!.AcknowledgeExitAsync(requestId, _localCloseApproved || _viewModel.CloseCommitted, CancellationToken.None);
            return;
        }
        ActivateMainUi();
        _viewModel.SuspendActionsForClose();
        _confirmations.CancelActiveDialog();
        await Page.DismissSettingsDialogAsync();
        var approved = await _viewModel.ConfirmBackendExitAsync();
        if (_exitRequestId != requestId || _closed || _backendStopping)
        {
            if (!_closed && !_closing && !_backendStopping)
            { _viewModel.CancelBackendExit(); Page.ResumeSettingsDialogs(); }
            return;
        }
        try { await _events!.AcknowledgeExitAsync(requestId, approved, CancellationToken.None); }
        catch (Exception error)
        {
            DiagnosticLog.Warning("ui.lifetime", "exitVoteFailed", "The backend exit vote could not be delivered on its original connection.", error);
            if (approved && (error is IOException or ObjectDisposedException) &&
                (_events is not ClientBridge bridge || bridge.IsStopping) && _exitRequestId == requestId)
            {
                MarkBackendStopping();
                Close();
                return;
            }
            if (!_closing && !_backendStopping && _exitRequestId == requestId)
            { _viewModel.CancelBackendExit(); Page.ResumeSettingsDialogs(); }
            return;
        }
        if (!approved && _exitRequestId == requestId) Page.ResumeSettingsDialogs();
    }

    private async Task<IReadOnlyList<UiDevice>?> ResolveDevicesAsync()
    {
        var resolver = new DeviceStartupCoordinator(_service, _manager!);
        while (!_closing)
        {
            try { return await resolver.ResolveAsync(_managerLifetime.Token); }
            catch (OperationCanceledException) when (_managerLifetime.IsCancellationRequested) { throw; }
            catch (Exception error)
            {
                DiagnosticLog.Warning("ui.lifetime", "deviceResolveFailed", "Device startup resolution failed; offering recovery", error);
                if (_closing) return null;
                var recovery = await _viewModel.Dialogs.RunAsync(token => _confirmations.RecoverDevicesAsync(token),
                    _managerLifetime.Token);
                if (recovery == FAContentDialogResult.Primary) continue;
                if (recovery != FAContentDialogResult.Secondary) return null;
                // Explicit recovery also allows repairing a damaged selection file.
                try
                {
                    if (await _manager!.RunAsync(_managerLifetime.Token) == DeviceManagerOutcome.Cancelled) return null;
                }
                catch (OperationCanceledException) when (_managerLifetime.IsCancellationRequested) { throw; }
                catch (Exception managerError) { DiagnosticLog.Warning("ui.lifetime", "recoveryManagerFailed", "The device manager failed during explicit recovery", managerError); }
            }
        }
        return null;
    }

    internal async Task ManageDevicesAsync()
    {
        if (_manager is null || _closing || _managing || !_viewModel.CanManageDevices) return;
        _managing = true;
        var originalIdentity = _viewModel.SelectedDevice?.Identity;
        var prepared = false;
        try
        {
            prepared = await _viewModel.PrepareDeviceManagementAsync();
            if (!prepared || _closing) return;
            Page.IsEnabled = false;
            ManagerOverlay.IsVisible = true;
            DeviceManagerOutcome result;
            while (true)
            {
                try { result = await _manager.RunAsync(_managerLifetime.Token); break; }
                catch (OperationCanceledException) when (_managerLifetime.IsCancellationRequested) { throw; }
                catch (Exception error)
                {
                    DiagnosticLog.Warning("ui.lifetime", "deviceManagerFailed", "The device manager did not complete; offering retry", error);
                    if (_closing) return;
                    var retry = await _viewModel.Dialogs.RunAsync(token => _confirmations.RetryManagerAsync(token),
                        _managerLifetime.Token);
                    if (retry) continue;
                    result = DeviceManagerOutcome.Cancelled;
                    break;
                }
            }
            if (_closing) return;
            var completed = await _viewModel.CompleteDeviceManagementAsync(result == DeviceManagerOutcome.Saved);
            prepared = false;
            if (completed) return;
            if (result == DeviceManagerOutcome.Cancelled) { Close(); return; }
            // Retry only after an actual save race or enumeration failure.
            var devices = await ResolveDevicesAsync();
            if (devices is null) { if (!_closing) Close(); return; }
            await _viewModel.InitializeAsync(devices);
            if (devices.FirstOrDefault(device => device.Identity == originalIdentity) is { } original)
                await _viewModel.SelectDeviceAsync(original);
        }
        catch (OperationCanceledException) when (_managerLifetime.IsCancellationRequested) { }
        finally
        {
            if (prepared && !_closing) await _viewModel.CompleteDeviceManagementAsync(saved: false);
            _managing = false;
            ManagerOverlay.IsVisible = false;
            if (!_closing) Page.IsEnabled = true;
        }
    }

    private async Task LaunchCalibrationAsync(string kind, ICalibrationService service)
    {
        if (_closing || _calibrationOperation is not null) return;
        if (_viewModel.IsDemoSelected)
        {
            _viewModel.ShowDemoFeedback(kind == "fit" ? "耳塞贴合度测试" : "设置黄金听感");
            return;
        }
        var operation = RunCalibrationAsync(kind, service);
        _calibrationOperation = operation;
        try { await operation; }
        finally { if (ReferenceEquals(_calibrationOperation, operation)) _calibrationOperation = null; }
    }
    private async Task RunCalibrationAsync(string kind, ICalibrationService service)
    {
        UiCalibrationState? state = null;
        try
        {
            state = await _viewModel.CreateCalibrationAsync(kind);
            await new CalibrationProcess().RunAsync(kind, state.Handle, service, _managerLifetime.Token);
            if (!_closing) await _viewModel.RefreshStateCommand.ExecuteAsync(null);
        }
        catch (OperationCanceledException) when (_managerLifetime.IsCancellationRequested) { }
        catch (Exception error)
        {
            DiagnosticLog.Warning("ui.lifetime", "calibrationLaunchFailed", "The calibration window did not start or complete", error, new { kind });
            if (!_closing) await _viewModel.Dialogs.RunAsync(token => _confirmations.AlertAsync("无法打开测试", "测试组件未能正常启动或完成，请检查耳机连接及客户端文件后重试。", token), _managerLifetime.Token);
        }
        finally
        {
            if (state is not null) { try { await service.CancelCalibrationAsync(state.Handle, CancellationToken.None); } catch (Exception error) { DiagnosticLog.Debug("ui.lifetime", "calibrationCancelFailed", "Final calibration cancel failed", new { error = error.Message }); } }
        }
    }

    private async void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_closed) return;
        e.Cancel = true;
        if (_closing) return;
        _closing = true;
        var approved = _backendStopping;
        try
        {
            _viewModel.SuspendActionsForClose();
            _confirmations.CancelActiveDialog();
            var dismiss = Page.DismissSettingsDialogAsync();
            if (await Task.WhenAny(dismiss, _backendStop.Task) == dismiss) await dismiss;
            else ObserveLateFailure(dismiss);
            if (!approved && !_backendStopping)
            {
                var decision = _viewModel.RequestCloseAsync();
                if (await Task.WhenAny(decision, _backendStop.Task) == decision)
                    _localCloseApproved = approved = await decision;
                else { ObserveLateFailure(decision); approved = true; }
            }
        }
        catch (Exception error)
        {
            DiagnosticLog.Error("ui.lifetime", "closePreparationFailed", "Main window close preparation failed.", error);
            approved |= _backendStopping || _localCloseApproved || _viewModel.CloseCommitted;
        }
        if (!approved && !_backendStopping)
        {
            _closing = false;
            _viewModel.CancelBackendExit();
            Page.ResumeSettingsDialogs();
            return;
        }
        try
        {
            _managerLifetime.Cancel();
            await CleanupAsync(() => _viewModel.DisposeAsync().AsTask(), "viewModel");
            var pending = new[] { _startupVerification, _calibrationOperation, _manager?.CurrentOperation }
                .OfType<Task>().ToArray();
            if (pending.Length != 0)
                await CleanupAsync(async () =>
                {
                    var work = Task.WhenAll(pending);
                    if (_backendStopping)
                    {
                        _ = work.ContinueWith(task => _ = task.Exception, CancellationToken.None,
                            TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
                        await work.WaitAsync(TimeSpan.FromSeconds(2));
                    }
                    else await work;
                }, "windowOperations");
            try { Page.VisualCatalog.Dispose(); }
            catch (Exception error) { DiagnosticLog.Warning("ui.lifetime", "artworkCleanupFailed", "Artwork cleanup failed after close was approved.", error); }
        }
        catch (Exception error)
        {
            DiagnosticLog.Warning("ui.lifetime", "closeCleanupFailed", "Main window cleanup failed after close was approved.", error);
        }
        finally
        {
            _closed = true;
            Dispatcher.UIThread.Post(Close);
        }
    }

    private async Task CleanupAsync(Func<Task> cleanup, string stage)
    {
        try
        {
            var work = cleanup();
            if (await Task.WhenAny(work, _backendStop.Task) != work)
            {
                ObserveLateFailure(work);
                await work.WaitAsync(TimeSpan.FromSeconds(2));
            }
            else await work;
        }
        catch (Exception error) { DiagnosticLog.Warning("ui.lifetime", "closeCleanupFailed", "Main window cleanup failed after close was approved.", error, new { stage }); }
    }

    private void MarkBackendStopping()
    {
        _backendStopping = true;
        _backendStop.TrySetResult();
    }

    private static void ObserveLateFailure(Task task) => _ = task.ContinueWith(completed => _ = completed.Exception,
        CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
}

internal sealed class WindowConfirmationService(TopLevel owner) : IConfirmationService
{
    private FAContentDialog? _activeDialog;
    public void CancelActiveDialog() => _activeDialog?.Hide();
    public Task<bool> ConfirmAsync(string title, string message, CancellationToken token) =>
        ShowConfirmationAsync(title, message, "继续", token);

    public Task<bool> ConfirmRetryAsync(string title, string message, CancellationToken token) =>
        ShowConfirmationAsync(title, message, "确认", token);

    public Task<FAContentDialogResult> RecoverDevicesAsync(CancellationToken token) => ShowDialogAsync(new FAContentDialog
    {
        Title = "无法读取设备列表",
        Content = "无法检查蓝牙配对记录或设备选择。请确认蓝牙可用，并重启软件或重新安装。原来的设备选择不会被清空。",
        PrimaryButtonText = "重试", SecondaryButtonText = "重新选择设备", CloseButtonText = "退出", DefaultButton = FAContentDialogButton.Primary
    }, token);

    public Task<bool> RetryManagerAsync(CancellationToken token) => ShowConfirmationAsync("无法打开设备管理",
        "设备管理组件缺失或未正常完成。请确认已完整安装客户端后重试。", "重试", token);

    public async Task AlertAsync(string title, string message, CancellationToken token) =>
        _ = await ShowDialogAsync(CreateAlert(title, message), token);

    private async Task<bool> ShowConfirmationAsync(string title, string message, string primaryText, CancellationToken token) =>
        await ShowDialogAsync(CreateConfirmation(title, message, primaryText), token) == FAContentDialogResult.Primary
            && !token.IsCancellationRequested;

    internal static FAContentDialog CreateConfirmation(string title, string message, string primaryText) =>
        new()
        {
            Title = title,
            Content = new TextBlock { Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap },
            PrimaryButtonText = primaryText,
            CloseButtonText = "取消",
            DefaultButton = FAContentDialogButton.Close
        };

    internal static FAContentDialog CreateAlert(string title, string message) =>
        new()
        {
            Title = title,
            Content = new TextBlock { Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap },
            CloseButtonText = "确定",
            DefaultButton = FAContentDialogButton.Close
        };

    private async Task<FAContentDialogResult> ShowDialogAsync(FAContentDialog dialog, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        _activeDialog = dialog;
        using var registration = token.Register(() => Dispatcher.UIThread.Post(dialog.Hide));
        try { return await dialog.ShowAsync(owner); }
        finally { _activeDialog = null; }
    }
}
