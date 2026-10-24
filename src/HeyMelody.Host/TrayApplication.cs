using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform;
using Avalonia.Threading;
using FluentAvalonia.Styling;
using HeyMelodyWindows.App;
using HeyMelodyWindows.Ui;
using HeyMelody.Diagnostics;

namespace HeyMelodyWindows.Host;

internal sealed class TrayApplication : Application
{
    private BackendSupervisor? _supervisor;
    private BackendPipeServer? _server;
    private WindowsStateMonitor? _monitor;
    private EnergyPolicy? _energyPolicy;
    private EnergyController? _energy;
    private Process? _self;
    private IDisposable? _selfEnergy;
    private TrayIcon? _tray;
    private bool _exiting;

    public override void Initialize()
    {
        Styles.Add(new FluentAvaloniaTheme { PreferSystemTheme = true, PreferUserAccentColor = true });
        Typography.ApplyThemeResources(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            // Eco applies when no window is connected: this process and its workers drop to EcoQoS and idle priority.
            _energyPolicy = new(TimeProvider.System); _energy = new(_energyPolicy, new WindowsProcessEnergy());
            HostEnergy.Controller = _energy; _selfEnergy = _energy.Attach(_self = Process.GetCurrentProcess());
            _supervisor = new(); _server = new(_supervisor, energy: _energyPolicy); _monitor = new(_supervisor);
            _supervisor.AttachDiagnostics();
            var menu = new NativeMenu();
            var open = new NativeMenuItem("打开主界面");
            void OpenMain() { try { _server.ActivateMain(true); } catch { _supervisor.ReportHealth("无法打开主界面，请重启软件或重新安装。"); } }
            open.Click += (_, _) => OpenMain();
            var exit = new NativeMenuItem("退出");
            exit.Click += async (_, _) => await _server.RequestExitAsync();
            // The separator keeps 退出 away from the everyday command.
            menu.Add(open); menu.Add(new NativeMenuItemSeparator()); menu.Add(exit);
            _tray = new TrayIcon { Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://HeyMelody.Host/Assets/HeyMelody.ico"))),
                ToolTipText = "HeyMelody", Menu = menu, IsVisible = true };
            // A left click opens the main window, as the first menu command does.
            _tray.Clicked += (_, _) => OpenMain();
            TrayIcon.SetIcons(this, new TrayIcons { _tray });
            _supervisor.EventPublished += OnBackendEvent;
            _server.ExitApproved += async () =>
            {
                _exiting = true;
                await Dispatcher.UIThread.InvokeAsync(() => _monitor.Dispose());
                await _server.DisposeAsync();
                await Dispatcher.UIThread.InvokeAsync(() => { _tray.Dispose(); desktop.Shutdown(); });
            };
            desktop.ShutdownRequested += (_, args) =>
            {
                if (_exiting) return;
                args.Cancel = true; _ = _server.RequestExitAsync();
            };
            desktop.Exit += (_, _) =>
            {
                _monitor.Dispose(); _tray.Dispose();
                HostEnergy.Controller = null; _selfEnergy?.Dispose(); _energy?.Dispose(); _energyPolicy?.Dispose(); _self?.Dispose();
            };
            _server.Start();
            _ = StartAsync();
        }
        base.OnFrameworkInitializationCompleted();
    }
    private async Task StartAsync()
    {
        try { await _monitor!.StartAsync(); await _supervisor!.InitializeAsync(CancellationToken.None); }
        catch (Exception error)
        {
            DiagnosticLog.Error("Host", "backend.initializeFailed", "Bluetooth backend initialization failed", error);
            _supervisor!.ReportHealth("无法初始化蓝牙设备监听，请检查蓝牙状态。托盘继续运行。");
        }
        ShowDevices(_supervisor!.Devices());
    }

    // Only events that already carry the device list update the tooltip: no polling and no extra backend work.
    private void OnBackendEvent(BackendEvent notification)
    {
        if (notification.Devices is { } devices) ShowDevices(devices);
    }

    private void ShowDevices(IReadOnlyList<UiDevice> devices)
    {
        var text = ToolTipFor(devices);
        Dispatcher.UIThread.Post(() => { if (!_exiting && _tray is { } tray && tray.ToolTipText != text) tray.ToolTipText = text; });
    }

    /// <summary>The tray tooltip; Windows draws it with the system font and limits it to 127 characters.</summary>
    internal static string ToolTipFor(IReadOnlyList<UiDevice> devices)
    {
        var connected = devices.Where(device => device.Connected).ToArray();
        var text = connected.Length switch
        {
            0 => "HeyMelody · 未连接耳机",
            1 => "HeyMelody · " + connected[0].Name + " 已连接",
            _ => $"HeyMelody · {connected.Length} 台耳机已连接"
        };
        return text.Length <= 127 ? text : text[..126] + "…";
    }
}
