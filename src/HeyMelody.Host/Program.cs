using HeyMelody.Platform;
using System.Runtime.CompilerServices;
using Avalonia;
using HeyMelodyWindows.Ui;
using HeyMelody.Diagnostics;

namespace HeyMelodyWindows.Host;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args is ["--performance-benchmark-worker", var benchmarkOutput])
            return PerformanceBenchmarkHost.RunWorkerAsync(benchmarkOutput).GetAwaiter().GetResult();
        if (args.FirstOrDefault() == "--performance-benchmark")
        {
            if (!PerformanceBenchmarkHost.TryConfigure(args, out var benchmarkExit)) return benchmarkExit;
            using var diagnostic = PerformanceBenchmarkHost.Options!.Diagnostics
                ? IsolatedBenchmarkDiagnostics.Start(DiagnosticComponents.Host, "benchmark",
                    PerformanceBenchmarkHost.Options.RunId, PerformanceBenchmarkHost.Options.Output) : null;
            using var isolated = DesktopRuntime.CreateMutex(PerformanceBenchmarkHost.Options!.HostMutex, out _);
            if (!isolated.WaitOne(0)) return 2;
            try { return RunTray(args); }
            finally { isolated.ReleaseMutex(); }
        }
        if (InstallationCheck.TryRun(args, out var installationResult)) return installationResult;
        // Workers never initialize Avalonia or acquire the tray singleton.
        if (args is ["--worker"])
        {
            using var diagnostics = DiagnosticRuntime.InitializeWorker("worker");
            return HostPlatformFactory.RunWorker(NativeWorker.RunAsync);
        }
        if (args is ["--catalog-worker"])
        {
            using var diagnostics = DiagnosticRuntime.InitializeWorker("catalog-worker");
            return HostPlatformFactory.RunWorker(NativeWorker.EnumerateAsync);
        }
        // Explicit development check: use this executable's real UI-thread identity without starting
        // Bluetooth, acquiring the shared Host mutex, initializing diagnostics, or saving preferences.
        if (args is ["--check-location-permission"])
            return HostPlatformFactory.IsSupported ? RunTray(args) : 2;
        if (args.Length != 0 || !HostPlatformFactory.IsSupported) return 2;
        using var singleton = DesktopRuntime.CreateMutex(BackendEndpoint.HostMutexName, out _);
        bool owner;
        try { owner = singleton.WaitOne(0); }
        catch (AbandonedMutexException) { owner = true; }
        if (!owner) return 0;
        try
        {
            using var diagnostics = DiagnosticRuntime.InitializeHost();
            try { return RunTray(args); }
            catch (Exception error) { DiagnosticLog.Error("Host", "process.failed", "Tray host failed", error); return 1; }
        }
        finally { singleton.ReleaseMutex(); }
    }

    // Kept out of Main so the JIT never loads Avalonia for the worker entry points.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int RunTray(string[] args) => AppBuilder.Configure<TrayApplication>().UsePlatformDetect()
        // The tray draws one small menu: software rendering avoids ANGLE/D3D and its driver memory. Skia keeps its
        // default caches because nothing here scrolls or animates.
        .With(new Win32PlatformOptions
        {
            RenderingMode = [Win32RenderingMode.Software],
            CompositionMode = [Win32CompositionMode.RedirectionSurface]
        })
        // The tray is a menu-bar accessory: LSUIElement alone is overridden by Avalonia's default Dock policy.
        .With(new MacOSPlatformOptions { ShowInDock = false })
        .UseHeyMelodyDiagnostics().UseHeyMelodyTypography().StartWithClassicDesktopLifetime(args);
}
