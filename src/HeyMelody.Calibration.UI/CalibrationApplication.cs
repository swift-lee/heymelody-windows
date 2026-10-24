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
using HeyMelodyWindows.Windowing;
using HeyMelody.Diagnostics;

namespace HeyMelodyWindows.Calibration.UI;

public sealed class CalibrationApplication : Application
{
    public static string Kind { get; private set; } = "fit";
    public static string Handle { get; private set; } = "";
    public static int Run(string kind, string[] args)
    {
        if (args is not ["--handle", var handle] || string.IsNullOrWhiteSpace(handle) || handle.Length > 128 || !OperatingSystem.IsWindows()) return 2;
        Kind = kind; Handle = handle;
        using var diagnostics = DiagnosticRuntime.InitializeClient(kind == "fit" ? DiagnosticComponents.FitTest : DiagnosticComponents.HearingSetup);
        try { return AppBuilder.Configure<CalibrationApplication>().UsePlatformDetect().UseHeyMelodyDiagnostics()
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
    private readonly MicaAltBackdrop _backdrop;
    private bool _closing, _closed, _completed, _allowLocalClose;
    private readonly object _spectrumGate = new();
    private UiCalibrationSpectrum? _pendingSpectrum;
    private bool _spectrumPosted;
    public CalibrationWindow(ICalibrationService service, string handle, string kind)
    {
        // Register the backdrop's window-style callback before any window property is assigned, as DeviceManager does.
        _backdrop = new MicaAltBackdrop(this);
        Typography.ApplyTextOptions(this);
        RenderDiagnostics.Attach(this);
        _service = service; _events = service as IHeadsetEventService;
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
            catch { _allowLocalClose = true; _model.BackendUnavailable("无法连接蓝牙后台，请关闭此窗口后重试。"); }
        };
        Closing += ClosingAsync;
        Closed += (_, _) =>
        {
            if (_events is not null) _events.EventReceived -= OnEvent;
            _backdrop.Dispose();
            if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
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
        if (notification.Calibration is { } state) _model.Adopt(state);
        if (notification.Kind is "unavailable" or "stopping")
        { _allowLocalClose = true; _model.BackendUnavailable(notification.Message ?? ""); Close(); }
        else if (notification.Kind == "requestExit" && notification.ExitRequestId is { } requestId && _events is not null)
        {
            var confirmed = false;
            try
            {
                confirmed = await _model.CancelAsync();
                await _events.AcknowledgeExitAsync(requestId, confirmed, CancellationToken.None);
            }
            catch { /* Connection events permit a local close if the backend has disappeared. */ }
            if (confirmed) Close();
        }
        });
    }
    private async void ClosingAsync(object? sender, WindowClosingEventArgs e)
    {
        if (_closed) return;
        e.Cancel = true; if (_closing) return; _closing = true; _completed |= _model.State.Stage == "Completed";
        try
        {
            if (!_allowLocalClose && !await _model.CancelAsync() && !_allowLocalClose) return;
            await _model.DisposeAsync();
            if (_service is IAsyncDisposable disposable)
            {
                try { await disposable.DisposeAsync(); }
                catch { /* STOP was confirmed, or the backend disappeared; an IPC cleanup failure must not trap the window. */ }
            }
            _closed = true; Dispatcher.UIThread.Post(Close);
        }
        finally { _closing = false; }
    }
}
