using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Input;
using HeyMelodyWindows.Ui;

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
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsMacOS())
            throw new PlatformNotSupportedException("The window lifecycle fixture needs a supported desktop.");
        if (OperatingSystem.IsWindows()) SetErrorMode(3);
        _tests = tests;
        _output = Path.Combine(Environment.CurrentDirectory, "artifacts", "window-exit-" + component + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_output);
        var builder = AppBuilder.Configure<TApplication>().UsePlatformDetect().UseHeyMelodyTypography();
        if (OperatingSystem.IsWindows()) builder = builder.With(new Win32PlatformOptions
            {
                RenderingMode = [Win32RenderingMode.Software],
                CompositionMode = [Win32CompositionMode.RedirectionSurface]
            });
        builder.LogToTrace().StartWithClassicDesktopLifetime([], ShutdownMode.OnExplicitShutdown);
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

    internal static void CloseThroughNativeMenu(Window window)
    {
        if (!OperatingSystem.IsMacOS()) { window.Close(); return; }
        var file = NativeMenu.GetMenu(window)!.Items.OfType<NativeMenuItem>().Single().Menu!;
        var close = file.Items.OfType<NativeMenuItem>().Single();
        Check(close.Gesture?.Key == Key.W && close.Gesture.KeyModifiers == KeyModifiers.Meta,
            "The macOS close menu did not register Command-W.");
        // Exercise the same managed bridge invoked by Avalonia's native menu exporter.
        var dispatch = typeof(NativeMenuItem).GetMethod("Avalonia.Controls.INativeMenuItemExporterEventsImplBridge.RaiseClicked",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("The native menu click bridge was unavailable.");
        dispatch.Invoke(close, null);
    }

    internal static async Task VerifyBackdropAsync(Window window)
    {
        if (!OperatingSystem.IsMacOS()) return;
        Check(Typography.Source == TypographySource.MacOSSystem, "The native window did not select macOS PingFang SC.");
        foreach (var weight in new[] { FontWeight.Normal, FontWeight.SemiBold, FontWeight.Bold })
        {
            var glyphs = new Typeface(Typography.ThemeFontFamily, FontStyle.Normal, weight).GlyphTypeface;
            Check(glyphs.FamilyName == Typography.MacOSFamily && glyphs.FontSimulations == FontSimulations.None,
                $"The native window did not use a real PingFang SC face for {weight}: {glyphs.FamilyName}/{glyphs.FontSimulations}.");
        }
        Pass("macOS native windows use real PingFang SC regular and semibold faces without synthetic bold");
        Console.WriteLine(JsonSerializer.Serialize(new { backdropWindow = window.Title, actual = window.ActualTransparencyLevel.ToString(),
            hints = window.TransparencyLevelHint.Select(x => x.ToString()).ToArray(), background = window.Background?.ToString() }));
        var initialTheme = window.RequestedThemeVariant;
        foreach (var theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
        {
            window.RequestedThemeVariant = theme;
            await UntilAsync(() => (window.ActualTransparencyLevel == WindowTransparencyLevel.Blur || window.ActualTransparencyLevel == WindowTransparencyLevel.AcrylicBlur)
                && window.Background is ISolidColorBrush brush && brush.Color.A < 255,
                "macOS did not negotiate native blur with a translucent readable window background.");
            window.TransparencyLevelHint = [WindowTransparencyLevel.None];
            await UntilAsync(() => window.ActualTransparencyLevel == WindowTransparencyLevel.None
                && window.Background is ISolidColorBrush brush && brush.Color.A == 255,
                "The window did not restore an opaque background when the native effect was removed.");
            window.TransparencyLevelHint = [WindowTransparencyLevel.AcrylicBlur, WindowTransparencyLevel.Blur, WindowTransparencyLevel.None];
            await UntilAsync(() => (window.ActualTransparencyLevel == WindowTransparencyLevel.Blur || window.ActualTransparencyLevel == WindowTransparencyLevel.AcrylicBlur)
                && window.Background is ISolidColorBrush brush && brush.Color.A < 255,
                "The native blur background did not recover after fallback.");
        }
        window.RequestedThemeVariant = initialTheme;
        Pass("macOS native blur remains translucent in both themes and restores a legible opaque fallback");
    }

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
            nativeBluetoothCalls = 0, hostProcessesLaunched = 0, screenshots = MacBackdropCapture.WindowCaptures, output = _output,
            operatingSystem = OperatingSystem.IsMacOS() ? "macOS" : "Windows",
            scope = "Isolated native desktop windows with fake services; exit votes, veto recovery, terminal close and cleanup faults; no real Host or headset."
        };
        var json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(Path.Combine(_output, "receipt.json"), json);
        Console.WriteLine(JsonSerializer.Serialize(report));
    }
}
