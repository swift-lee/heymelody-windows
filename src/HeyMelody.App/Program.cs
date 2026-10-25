using Avalonia;
using Avalonia.Headless;
using HeyMelodyWindows.Host;
using HeyMelodyWindows.Ui;
using HeyMelody.Diagnostics;
using HeyMelody.Platform;

namespace HeyMelodyWindows.App;

internal static class Program
{
    internal static bool IsModelDemo { get; private set; }
    private const string Usage = PreviewOptions.Usage + "\n--3d-demo";

    [STAThread]
    public static int Main(string[] args)
    {
        if (InstallationCheck.TryRun(args, out var installationExit)) return installationExit;
        using var loadTrace = args.Length == 0 && DesktopCapabilities.SupportsHeadsetControl ? null : ModelLoadTrace.Start();
        if (args.FirstOrDefault() == "--model-loading-benchmark") return ModelLoadingBenchmark.Run(args);
        if (args.Length > 0 && args[0] == "--3d-demo")
        {
            if (args.Length != 1) { Console.Error.WriteLine(Usage); return 2; }
            IsModelDemo = true;
            // The demo deliberately enters before the production single-instance/backend path.
            return BuildAvaloniaApp().StartWithClassicDesktopLifetime([]);
        }
        if (PreviewOptions.TryParse(args, out var preview, out var error))
        {
            if (error is not null) { Console.Error.WriteLine(error); return 2; }
            if (preview!.Live && !DesktopCapabilities.SupportsHeadsetControl)
            { Console.Error.WriteLine("当前平台不支持蓝牙后台。"); return 2; }
            return PreviewRenderer.Run(preview);
        }
        if (args.Length != 0) { Console.Error.WriteLine(Usage); return 2; }
        if (DesktopCapabilities.SupportsHeadsetControl)
        {
            using var diagnostics = DiagnosticRuntime.InitializeClient(DiagnosticComponents.Main);
            using var normalLoadTrace = ModelLoadTrace.Start();
            using var mainInstance = DesktopRuntime.CreateMutex(BackendEndpoint.MainMutexName, out _);
            var ownsMain = false;
            try
            {
                try { ownsMain = mainInstance.WaitOne(0); }
                catch (AbandonedMutexException) { ownsMain = true; }
                if (!ownsMain)
                {
                    try { ActivateExistingAsync().GetAwaiter().GetResult(); return 0; }
                    catch (Exception activationError)
                    {
                        DiagnosticLog.Error("App", "activation.failed", "Failed to activate the existing main window", activationError);
                        return 2;
                    }
                }
                // Connecting (and launching a stopped tray) overlaps Avalonia's start-up instead of waiting for Opened.
                var service = new ClientBridge();
                service.BeginConnect();
                _startupService = service;
                try { return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args); }
                catch (Exception startupError)
                {
                    DiagnosticLog.Error("App", "process.failed", "Main application failed", startupError);
                    throw;
                }
            }
            finally { if (ownsMain) mainInstance.ReleaseMutex(); }
        }
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    private static IHeadsetService? _startupService;

    /// <summary>The main window takes the early-connected service once; the window and its view model own it.</summary>
    internal static IHeadsetService? TakeStartupService() => Interlocked.Exchange(ref _startupService, null);

    private static async Task ActivateExistingAsync()
    {
        await using var service = HeadsetServiceFactory.Create();
        if (service is not IHeadsetEventService events) return;
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await events.ActivateMainUiAsync(budget.Token);
    }

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UsePlatformDetect().With(new MacOSPlatformOptions { ShowInDock = true })
        .UseHeyMelodyDiagnostics().UseHeyMelodyRendering().UseHeyMelodyTypography();

    public static AppBuilder BuildHeadlessApp()
    {
        // Previews and tests capture single frames; cosmetic fades would be caught half way.
        ProductArtwork.TransitionsEnabled = false;
        // Their dispatcher clock does not run on its own either: show the loading ring at once.
        HeroLoadingIndicator.Delay = TimeSpan.Zero;
        return AppBuilder.Configure<App>()
            .UseSkia().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .UseHeyMelodyRendering().UseHeyMelodyTypography();
    }
}
