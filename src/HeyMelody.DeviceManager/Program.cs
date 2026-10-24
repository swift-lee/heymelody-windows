using Avalonia;
using HeyMelodyWindows.Ui;
using HeyMelody.Diagnostics;

namespace HeyMelodyWindows.DeviceManager;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args is ["--ui-preview"]) DeviceManagerApp.Preview = true;
        else if (args.Length != 0) return 2;
        if (!DeviceManagerApp.Preview && !OperatingSystem.IsWindows()) return 2;
        using var diagnostics = DeviceManagerApp.Preview ? null : DiagnosticRuntime.InitializeClient(DiagnosticComponents.DeviceManager);
        try { return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args); }
        catch (Exception error) { DiagnosticLog.Error("DeviceManager", "process.failed", "Device manager failed", error); return 2; }
    }

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<DeviceManagerApp>().UsePlatformDetect()
        .UseHeyMelodyDiagnostics().UseHeyMelodyRendering().UseHeyMelodyTypography();
}
