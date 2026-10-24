using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using FluentAvalonia.UI.Windowing;
using HeyMelodyWindows.App;
using HeyMelodyWindows.Windowing;
using HeyMelody.Diagnostics;

namespace HeyMelodyWindows.DeviceManager;

public partial class DeviceManagerWindow : FAAppWindow
{
    private readonly DeviceManagerViewModel _viewModel;
    private readonly MicaAltBackdrop _backdrop;
    private readonly IHeadsetEventService? _events;
    private bool _closing, _closed, _backendStopping;
    private Task? _exitVote;
    private string? _exitRequestId;
    private readonly TaskCompletionSource _backendStop = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal bool ShutdownApplicationOnClose { get; init; } = true;

    public DeviceManagerWindow() : this(new ClientBridge(), false) { }
    internal DeviceManagerWindow(IDeviceManagementService service, bool preview)
    {
        _backdrop = new MicaAltBackdrop(this);
        InitializeComponent();
        Ui.Typography.ApplyTextOptions(this);
        Ui.RenderDiagnostics.Attach(this);
        if (preview) Title = "设备管理 · 界面预览";
        _viewModel = new DeviceManagerViewModel(service);
        _events = service as IHeadsetEventService;
        if (_events is not null) _events.EventReceived += OnBackendEvent;
        Page.DataContext = _viewModel;
        _viewModel.ExitRequested += () => Dispatcher.UIThread.Post(Close);
        Opened += async (_, _) => await _viewModel.RefreshAsync();
        Closing += OnClosing;
        Closed += (_, _) =>
        {
            if (_events is not null) _events.EventReceived -= OnBackendEvent;
            _backdrop.Dispose();
            if (ShutdownApplicationOnClose && Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
                desktop.Shutdown(_viewModel.ExitCode);
        };
    }

    private void OnBackendEvent(BackendEvent notification) => Dispatcher.UIThread.Post(async () =>
    {
        if (_closed) return;
        if (notification.Kind == "stopping")
        {
            _backendStopping = true;
            _backendStop.TrySetResult();
            Close();
            return;
        }
        if (_closing) return;
        try
        {
            _viewModel.AdoptBackendEvent(notification);
            if (notification.Kind == "requestExit" && notification.ExitRequestId is { } requestId)
            {
                if (_exitRequestId == requestId) return;
                var previous = _exitVote;
                _exitRequestId = requestId;
                var vote = VoteForExitAsync(previous, requestId);
                _exitVote = vote;
                try { await vote; }
                finally { if (ReferenceEquals(_exitVote, vote)) _exitVote = null; }
            }
            else if (notification.Kind == "exitCancelled")
            { _exitRequestId = null; _viewModel.CancelBackendExit(); }
        }
        catch { if (!_closing && !_backendStopping) _viewModel.CancelBackendExit(); }
    });

    private async Task VoteForExitAsync(Task? previous, string requestId)
    {
        if (previous is not null) { try { await previous; } catch { } }
        if (_exitRequestId != requestId || _closing || _closed || _backendStopping) return;
        var approved = false;
        try
        {
            await _viewModel.PrepareBackendExitAsync();
            if (_exitRequestId != requestId || _closing || _backendStopping) return;
            approved = true;
            await _events!.AcknowledgeExitAsync(requestId, true, CancellationToken.None);
        }
        catch (Exception error)
        {
            DiagnosticLog.Warning("ui.lifetime", "exitVoteFailed", "Device manager could not deliver the backend exit vote.", error);
            if (approved && (error is IOException or ObjectDisposedException) &&
                (_events is not ClientBridge bridge || bridge.IsStopping) && _exitRequestId == requestId)
            {
                _backendStopping = true; _backendStop.TrySetResult(); Close();
            }
            else if (!_closing && !_backendStopping && _exitRequestId == requestId) _viewModel.CancelBackendExit();
        }
    }
    private async void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_closed) return;
        e.Cancel = true;
        if (_closing) return;
        _closing = true;
        try
        {
            var cleanup = _viewModel.DisposeAsync().AsTask();
            if (await Task.WhenAny(cleanup, _backendStop.Task) != cleanup)
            {
                _ = cleanup.ContinueWith(task => _ = task.Exception, CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
                await cleanup.WaitAsync(TimeSpan.FromSeconds(2));
            }
            else await cleanup;
        }
        catch (Exception error)
        { DiagnosticLog.Warning("ui.lifetime", "closeCleanupFailed", "Device manager cleanup failed after close was approved.", error); }
        finally { _closed = true; Dispatcher.UIThread.Post(Close); }
    }
}
