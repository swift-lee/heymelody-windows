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
        // Workers never initialize Avalonia or acquire the tray singleton.
        if (args is ["--worker"])
        {
            using var diagnostics = DiagnosticRuntime.InitializeWorker("worker");
            return NativeWorker.RunAsync().GetAwaiter().GetResult();
        }
        if (args is ["--catalog-worker"])
        {
            using var diagnostics = DiagnosticRuntime.InitializeWorker("catalog-worker");
            return NativeWorker.EnumerateAsync().GetAwaiter().GetResult();
        }
        if (args.Length != 0 || !OperatingSystem.IsWindows()) return 2;
        using var singleton = new Mutex(false, BackendEndpoint.HostMutexName);
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
        .UseHeyMelodyDiagnostics().UseHeyMelodyTypography(bundledFallback: false).StartWithClassicDesktopLifetime(args);
}
