using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;

namespace HeyMelody.TestSupport;

internal static class NativeWindowLifetimeHarness
{
    private static readonly List<(Window Window, Action Stop)> Windows = [];
    private static readonly List<string> Checks = [];
    private static readonly List<string> Errors = [];
    private static Func<Task>? _tests;
    private static string _output = "";
    private static bool _done;

    [DllImport("kernel32.dll")] private static extern uint SetErrorMode(uint mode);

    internal static int Run<TApplication>(Func<Task> tests, string component) where TApplication : Application, new()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("The FAAppWindow lifecycle fixture needs Win32 handles.");
        SetErrorMode(3);
        _tests = tests;
        _output = Path.Combine(Environment.CurrentDirectory, "artifacts", "window-exit-" + component + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_output);
        AppBuilder.Configure<TApplication>().UsePlatformDetect()
            .With(new Win32PlatformOptions
            {
                RenderingMode = [Win32RenderingMode.Software],
                CompositionMode = [Win32CompositionMode.RedirectionSurface]
            }).LogToTrace().StartWithClassicDesktopLifetime([], ShutdownMode.OnExplicitShutdown);
        return Errors.Count == 0 && _done ? 0 : 1;
    }

    internal static void Ready(Application application)
    {
        var desktop = application.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime
            ?? throw new InvalidOperationException("Desktop lifetime required.");
        Dispatcher.UIThread.UnhandledException += (_, error) =>
        {
            Errors.Add(error.Exception.ToString());
            error.Handled = true;
        };
        Dispatcher.UIThread.Post(() => _ = ExecuteAsync(desktop));
        _ = WatchdogAsync(desktop);
    }

    internal static void Track(Window window, Action stop) => Windows.Add((window, stop));
    internal static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
    internal static void Pass(string message) => Checks.Add(message);

    internal static async Task UntilAsync(Func<bool> condition, string message, int milliseconds = 7000)
    {
        var clock = Stopwatch.StartNew();
        while (!condition())
        {
            if (clock.ElapsedMilliseconds > milliseconds) throw new TimeoutException(message);
            await Task.Delay(10);
        }
    }

    private static async Task ExecuteAsync(IClassicDesktopStyleApplicationLifetime desktop)
    {
        try { await _tests!(); }
        catch (Exception error) { Errors.Add(error.ToString()); }
        finally
        {
            foreach (var (window, stop) in Windows.Where(item => item.Window.IsVisible))
            {
                try { stop(); window.Close(); } catch (Exception error) { Errors.Add(error.ToString()); }
            }
            await Task.Delay(100);
            _done = true;
            WriteReceipt();
            desktop.Shutdown(Errors.Count == 0 ? 0 : 1);
        }
    }

    private static async Task WatchdogAsync(IClassicDesktopStyleApplicationLifetime desktop)
    {
        await Task.Delay(TimeSpan.FromSeconds(45));
        if (_done) return;
        Errors.Add("Window lifecycle fixture timed out.");
        foreach (var (window, stop) in Windows)
            try { stop(); window.Close(); } catch { }
        _done = true;
        WriteReceipt();
        desktop.Shutdown(1);
    }

    private static void WriteReceipt()
    {
        var report = new
        {
            success = Errors.Count == 0, passed = Checks.Count, checks = Checks, errors = Errors,
            nativeBluetoothCalls = 0, hostProcessesLaunched = 0, screenshots = 0, output = _output,
            scope = "Isolated real Win32 windows with fake services; exit votes, veto recovery, terminal close and cleanup faults; no real Host or headset."
        };
        var json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(Path.Combine(_output, "receipt.json"), json);
        Console.WriteLine(JsonSerializer.Serialize(report));
    }
}
