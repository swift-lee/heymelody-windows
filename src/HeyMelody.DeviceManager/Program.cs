using Avalonia;
using HeyMelodyWindows.Ui;
using HeyMelody.Diagnostics;

namespace HeyMelodyWindows.DeviceManager;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (HeyMelody.Platform.InstallationCheck.TryRun(args, out var installationExit)) return installationExit;
        if (args is ["--ui-preview"]) DeviceManagerApp.Preview = true;
        else if (args.Length != 0) return 2;
        if (!DeviceManagerApp.Preview && !HeyMelody.Platform.DesktopCapabilities.SupportsHeadsetControl) return 2;
        if (!DeviceManagerApp.Preview && DiagnosticRuntime.IsBoundLaunchExpired)
        {
            // Diagnostics are not initialized yet, so leave a trace on stderr.
            Console.Error.WriteLine("DeviceManager: the bound launch has already expired; exiting without starting the window.");
            return 1;
        }
        using var diagnostics = DeviceManagerApp.Preview ? null : DiagnosticRuntime.InitializeClient(DiagnosticComponents.DeviceManager);
        if (!DeviceManagerApp.Preview && DiagnosticRuntime.IsBoundLaunchExpired) return 1;
        try { return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args); }
        catch (Exception error) { DiagnosticLog.Error("DeviceManager", "process.failed", "Device manager failed", error); return 2; }
    }

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<DeviceManagerApp>().UsePlatformDetect()
        .With(new MacOSPlatformOptions { ShowInDock = false })
        .UseHeyMelodyDiagnostics().UseHeyMelodyRendering().UseHeyMelodyTypography();
}
