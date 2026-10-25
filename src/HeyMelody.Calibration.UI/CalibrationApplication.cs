using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Threading;
using FluentAvalonia.Styling;
using FluentAvalonia.UI.Windowing;
using HeyMelodyWindows.App;
using HeyMelodyWindows.Ui;
using HeyMelody.Diagnostics;

namespace HeyMelodyWindows.Calibration.UI;

public sealed class CalibrationApplication : Application
{
    public static string Kind { get; private set; } = "fit";
    public static string Handle { get; private set; } = "";
    public static int Run(string kind, string[] args)
    {
        if (HeyMelody.Platform.InstallationCheck.TryRun(args, out var installationExit)) return installationExit;
        if (args is not ["--handle", var handle] || string.IsNullOrWhiteSpace(handle) || handle.Length > 128
            || !HeyMelody.Platform.DesktopCapabilities.SupportsHeadsetControl) return 2;
        Kind = kind; Handle = handle;
        if (DiagnosticRuntime.IsBoundLaunchExpired)
        {
            // Diagnostics are not initialized yet, so leave a trace on stderr.
            Console.Error.WriteLine($"Calibration.UI ({kind}): the bound launch has already expired; exiting without starting the window.");
            return 1;
        }
        using var diagnostics = DiagnosticRuntime.InitializeClient(kind == "fit" ? DiagnosticComponents.FitTest : DiagnosticComponents.HearingSetup);
        if (DiagnosticRuntime.IsBoundLaunchExpired) return 1;
        try { return AppBuilder.Configure<CalibrationApplication>().UsePlatformDetect().UseHeyMelodyDiagnostics()
            .With(new MacOSPlatformOptions { ShowInDock = false })
            .UseHeyMelodyRendering().UseHeyMelodyTypography().StartWithClassicDesktopLifetime(args); }
        catch (Exception error) { DiagnosticLog.Error("Calibration.UI", "process.failed", "Calibration application failed", error); return 2; }
    }
    public override void Initialize()
    {
        Styles.Add(new FluentAvaloniaTheme { PreferSystemTheme = true, PreferUserAccentColor = true });
        Styles.Add(SharedTheme.Create());
        Typography.ApplyThemeResources(this);
    }
    public override void OnFrameworkInitializationCompleted()
    {
        RequestedThemeVariant = ThemePreferenceReader.Load() switch { "light" => ThemeVariant.Light, "dark" => ThemeVariant.Dark, _ => ThemeVariant.Default };
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        { desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown; desktop.MainWindow = new CalibrationWindow(new ClientBridge(), Handle, Kind); }
        base.OnFrameworkInitializationCompleted();
    }
}

/// <summary>The design tokens shared with the main window and device manager, compiled into this assembly.</summary>
public static class SharedTheme
{
    public static Avalonia.Markup.Xaml.Styling.StyleInclude Create() => new(new Uri("avares://HeyMelody.Calibration.UI/"))
    { Source = new Uri("avares://HeyMelody.Calibration.UI/Styles/HeyMelodyTheme.axaml") };
}

public sealed class CalibrationWindow : FAAppWindow
{
    private readonly CalibrationViewModel _model;
    private readonly ICalibrationService _service;
    private readonly IHeadsetEventService? _events;
    private readonly DesktopWindowBackdrop _backdrop;
    private bool _closing, _closed, _completed, _allowLocalClose;
    private Task? _exitVote;
    private string? _exitRequestId;
    private readonly TaskCompletionSource _backendStop = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal bool ShutdownApplicationOnClose { get; init; } = true;
    private readonly object _spectrumGate = new();
    private UiCalibrationSpectrum? _pendingSpectrum;
    private bool _spectrumPosted;
    public CalibrationWindow(ICalibrationService service, string handle, string kind)
    {
        // Register the backdrop's window-style callback before any window property is assigned, as DeviceManager does.
        _backdrop = new DesktopWindowBackdrop(this);
        Typography.ApplyTextOptions(this);
        RenderDiagnostics.Attach(this);
        _service = service; _events = service as IHeadsetEventService;
        DesktopWindowIntegration.Attach(this, _events);
        _model = new CalibrationViewModel(service, handle, kind);
        Title = kind == "fit" ? "耳塞贴合度测试" : "设置黄金听感";
        Icon = new Avalonia.Media.Imaging.Bitmap(AssetLoader.Open(new Uri("avares://HeyMelody.Calibration.UI/Assets/HeyMelody.ico")));
        Width = 520; Height = 720; MinWidth = 400; MinHeight = 520; CanResize = true; ShowAsDialog = true;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        var view = new CalibrationView(_model); Content = view;
        UiDiagnosticLogging.AttachDiagnosticNotice(this);
        view.CloseRequested += () => { Close(); return Task.CompletedTask; };
        if (_events is not null) _events.EventReceived += OnEvent;
        Opened += async (_, _) =>
        {
            try { if (_events is not null) await _events.SubscribeAsync("calibration", CancellationToken.None); await _model.InitializeAsync(); }
            catch (Exception error)
            {
                DiagnosticLog.Warning("ui.lifetime", "backendConnectFailed", "Calibration window could not connect to the backend.", error);
                _allowLocalClose = true; _model.BackendUnavailable("无法连接蓝牙后台，请关闭此窗口后重试。");
            }
        };
        Closing += ClosingAsync;
        Closed += (_, _) =>
        {
            if (_events is not null) _events.EventReceived -= OnEvent;
            _backdrop.Dispose();
            if (ShutdownApplicationOnClose && Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
                desktop.Shutdown(_completed && _model.StopConfirmed ? 0 : 1);
        };
    }
    private void OnEvent(BackendEvent notification)
    {
        if (notification.CalibrationSpectrum is { } spectrum)
        {
            lock (_spectrumGate)
            {
                _pendingSpectrum = spectrum;
                if (_spectrumPosted) return;
                _spectrumPosted = true;
            }
            Dispatcher.UIThread.Post(() =>
            {
                UiCalibrationSpectrum? latest;
                lock (_spectrumGate) { latest = _pendingSpectrum; _pendingSpectrum = null; _spectrumPosted = false; }
                if (!_closed && latest is not null) _model.AdoptSpectrum(latest);
            });
            return;
        }
        Dispatcher.UIThread.Post(async () =>
    {
        if (_closed) return;
        if (notification.Kind is "unavailable" or "stopping")
        {
            _allowLocalClose = true; _backendStop.TrySetResult();
            try { _model.BackendUnavailable(notification.Message ?? ""); }
            catch (Exception error)
            { DiagnosticLog.Warning("ui.lifetime", "terminalNotificationFailed", "Calibration model could not publish its terminal state.", error); }
            finally { Close(); }
            return;
        }
        if (notification.Calibration is { } state) _model.Adopt(state);
        if (notification.Kind == "requestExit" && notification.ExitRequestId is { } requestId && _events is not null)
        {
            if (_exitRequestId == requestId) return;
            var previous = _exitVote;
            _exitRequestId = requestId;
            var vote = VoteForExitAsync(previous, requestId);
            _exitVote = vote;
            try { await vote; }
            finally { if (ReferenceEquals(_exitVote, vote)) _exitVote = null; }
        }
        else if (notification.Kind == "exitCancelled") _exitRequestId = null;
        });
    }

    private async Task VoteForExitAsync(Task? previous, string requestId)
    {
        if (previous is not null)
        {
            try { await previous; }
            catch (Exception error) { DiagnosticLog.Debug("ui.lifetime", "previousVoteFailed", "The previous exit vote had failed", new { error = error.Message, requestId }); }
        }
        if (_exitRequestId != requestId || _closed || _allowLocalClose) return;
        _completed |= _model.State.Stage == "Completed";
        bool confirmed;
        try { confirmed = await _model.CancelAsync(); }
        catch (Exception error)
        {
            // Never let this escape the UI-thread post, and never leave the Host waiting for a vote.
            DiagnosticLog.Warning("ui.lifetime", "exitCancelFailed", "Calibration cancel failed while preparing for backend exit; voting no.", error, new { requestId });
            confirmed = false;
        }
        if (_exitRequestId != requestId || _closed || _allowLocalClose) return;
        try { await _events!.AcknowledgeExitAsync(requestId, confirmed, CancellationToken.None); }
        catch (Exception error)
        {
            HeyMelody.Diagnostics.DiagnosticLog.Warning("ui.lifetime", "exitVoteFailed", "Calibration window could not deliver the backend exit vote.", error);
            if (confirmed && (error is IOException or ObjectDisposedException) &&
                (_events is not ClientBridge bridge || bridge.IsStopping) && _exitRequestId == requestId)
            { _allowLocalClose = true; _backendStop.TrySetResult(); Close(); }
        }
        // An approved vote is preparation only. Another window can still veto the backend exit.
    }
    private async void ClosingAsync(object? sender, WindowClosingEventArgs e)
    {
        if (_closed) return;
        e.Cancel = true; if (_closing) return; _closing = true; _completed |= _model.State.Stage == "Completed";
        var approved = _allowLocalClose;
        try
        {
            if (!approved)
            {
                var cancel = _model.CancelAsync();
                if (await Task.WhenAny(cancel, _backendStop.Task) == cancel) approved = await cancel;
                else _ = cancel.ContinueWith(task => _ = task.Exception, CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
                approved |= _allowLocalClose;
            }
            if (!approved) return;
            try { await _model.DisposeAsync(); }
            catch (Exception error)
            { HeyMelody.Diagnostics.DiagnosticLog.Warning("ui.lifetime", "closeCleanupFailed", "Calibration model cleanup failed after STOP or local close was approved.", error); }
            if (_service is IAsyncDisposable disposable)
            {
                try
                {
                    var cleanup = disposable.DisposeAsync().AsTask();
                    if (await Task.WhenAny(cleanup, _backendStop.Task) != cleanup)
                    {
                        _ = cleanup.ContinueWith(task => _ = task.Exception, CancellationToken.None,
                            TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
                        await cleanup.WaitAsync(TimeSpan.FromSeconds(2));
                    }
                    else await cleanup;
                }
                catch (Exception error)
                { HeyMelody.Diagnostics.DiagnosticLog.Warning("ui.lifetime", "closeCleanupFailed", "Calibration client cleanup failed after STOP or local close was approved.", error); }
            }
        }
        catch (Exception error)
        { HeyMelody.Diagnostics.DiagnosticLog.Warning("ui.lifetime", "closePreparationFailed", "Calibration close preparation failed.", error); }
        finally
        {
            if (approved || _allowLocalClose) { _closed = true; Dispatcher.UIThread.Post(Close); }
            else _closing = false;
        }
    }
}
