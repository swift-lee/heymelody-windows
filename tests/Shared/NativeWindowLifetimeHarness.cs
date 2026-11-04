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
    private static readonly List<object> FontMemory = [];
    private static int _fontWindowsOpened;
    private static int _fontWindowsCollected;
    private static Func<Task>? _tests;
    private static string _output = "";
    private static bool _done;

    [DllImport("kernel32.dll")] private static extern uint SetErrorMode(uint mode);

    internal static int Run<TApplication>(Func<Task> tests, string component, bool showInDock = true) where TApplication : Application, new()
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
        if (OperatingSystem.IsMacOS()) builder = builder.With(new MacOSPlatformOptions { ShowInDock = showInDock });
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

    internal static async Task VerifyBackdropAsync(Window window, Control? alwaysVisible = null, byte minimumTintAlpha = 1)
    {
        Check(Typography.CollectionInitializationCount == 1, "Native windows share one MiSans collection per process.");
        foreach (var weight in new[] { FontWeight.Normal, FontWeight.SemiBold, FontWeight.Bold })
        {
            var glyphs = new Typeface(Typography.ThemeFontFamily, FontStyle.Normal, weight).GlyphTypeface;
            Check((glyphs.TypographicFamilyName == Typography.Family || glyphs.FamilyName == Typography.Family)
                && glyphs.Weight == MiSansFontCollection.Map(weight) && glyphs.FontSimulations == FontSimulations.None,
                $"The native window did not use the real MiSans face for {weight}: {glyphs.FamilyName}/{glyphs.Weight}/{glyphs.FontSimulations}.");
        }
        Pass("native windows use the bundled MiSans regular, semibold and bold faces without synthesis");
        if (!OperatingSystem.IsMacOS()) return;
        Console.WriteLine(JsonSerializer.Serialize(new { backdropWindow = window.Title, actual = window.ActualTransparencyLevel.ToString(),
            hints = window.TransparencyLevelHint.Select(x => x.ToString()).ToArray(), background = window.Background?.ToString() }));
        var initialTheme = window.RequestedThemeVariant;
        foreach (var theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
        {
            window.RequestedThemeVariant = theme;
            await UntilAsync(() => (window.ActualTransparencyLevel == WindowTransparencyLevel.Blur || window.ActualTransparencyLevel == WindowTransparencyLevel.AcrylicBlur)
                && window.Background is ISolidColorBrush brush && brush.Color.A < 255,
                "macOS did not negotiate native blur with a translucent readable window background.");
            Check((window.Background as ISolidColorBrush)?.Color.A >= minimumTintAlpha,
                $"The macOS material tint is more transparent than expected: expected at least {minimumTintAlpha}, "
                + $"actual {(window.Background as ISolidColorBrush)?.Color.A}.");
            Check(alwaysVisible?.IsVisible ?? true, "The page surface was hidden while the native material applied.");
            window.TransparencyLevelHint = [WindowTransparencyLevel.None];
            await UntilAsync(() => window.ActualTransparencyLevel == WindowTransparencyLevel.None
                && window.Background is ISolidColorBrush brush && brush.Color.A == 255,
                "The window did not restore an opaque background when the native effect was removed.");
            Check(alwaysVisible?.IsVisible ?? true, "The page surface was hidden in the opaque fallback.");
            window.TransparencyLevelHint = [WindowTransparencyLevel.AcrylicBlur, WindowTransparencyLevel.Blur, WindowTransparencyLevel.None];
            await UntilAsync(() => (window.ActualTransparencyLevel == WindowTransparencyLevel.Blur || window.ActualTransparencyLevel == WindowTransparencyLevel.AcrylicBlur)
                && window.Background is ISolidColorBrush brush && brush.Color.A < 255,
                "The native blur background did not recover after fallback.");
            Check((window.Background as ISolidColorBrush)?.Color.A >= minimumTintAlpha,
                "The recovered macOS material tint did not restore the expected density.");
            Check(alwaysVisible?.IsVisible ?? true, "The page surface was hidden after the native material recovered.");
        }
        window.RequestedThemeVariant = initialTheme;
        Pass("macOS native blur keeps the expected density and page surface, then restores a legible opaque fallback");
    }

    internal static void VerifyActivationPolicy(MacActivationPolicy expected, string context)
    {
        if (!OperatingSystem.IsMacOS()) return;
        var actual = MacApplicationPolicy.Get();
        Check(actual == expected, $"{context}: expected activation policy {expected}, actual {actual?.ToString() ?? "unavailable"}.");
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
        try
        {
            await VerifyFontWindowLifetimeAsync();
            await _tests!();
        }
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
            var exitCode = Errors.Count == 0 ? 0 : 1;
            desktop.Shutdown(exitCode);
            // The native loop can occasionally keep the test host alive after shutdown, leaving a Dock icon
            // behind. The receipt is already written, so bound the wait and terminate the fixture explicitly.
            ExitLater(exitCode);
        }
    }

    private static void ExitLater(int exitCode) => _ = Task.Run(async () =>
    {
        await Task.Delay(TimeSpan.FromSeconds(2));
        Environment.Exit(exitCode);
    });

    private static async Task VerifyFontWindowLifetimeAsync()
    {
        var faces = new[] { FontWeight.Normal, FontWeight.SemiBold, FontWeight.Bold }
            .Select(weight => new Typeface(Typography.ThemeFontFamily, FontStyle.Normal, weight).GlyphTypeface).ToArray();
        var windows = new List<WeakReference>();
        for (var i = 0; i < 4; i++) windows.Add(await DrawFontWindowAsync());
        var baseline = SampleFontMemory("after-four-warmup-windows");
        for (var batch = 0; batch < 2; batch++)
        {
            for (var i = 0; i < 10; i++) windows.Add(await DrawFontWindowAsync());
            var sample = SampleFontMemory("after-" + (14 + batch * 10) + "-windows");
            // Windows software rendering gives comparable private bytes. macOS working set is recorded only:
            // compositor/cache residency is not a private-allocation measurement.
            if (OperatingSystem.IsWindows())
                Check(sample - baseline < 64L * 1024 * 1024,
                    "Repeated fixed-text windows retained more than 64 MiB after MiSans warmup.");
        }
        for (var i = 0; i < faces.Length; i++)
        {
            var weight = new[] { FontWeight.Normal, FontWeight.SemiBold, FontWeight.Bold }[i];
            Check(ReferenceEquals(faces[i], new Typeface(Typography.ThemeFontFamily, FontStyle.Normal, weight).GlyphTypeface),
                "Window churn must reuse the process-cached MiSans face for " + weight + ".");
        }
        Check(Typography.CollectionInitializationCount == 1, "Window churn must not reload the MiSans collection.");
        _fontWindowsCollected = windows.Count(reference => !reference.IsAlive);
        Check(_fontWindowsCollected == windows.Count, "Closed font fixture windows must be collectible.");
        Pass("24 fixed-text windows close and collect while sharing one MiSans collection and its three faces");
    }

    private static async Task<WeakReference> DrawFontWindowAsync()
    {
        Window? window = new()
        {
            Width = 320, Height = 160, ShowInTaskbar = false, Title = "MiSans lifetime fixture",
            Content = new StackPanel
            {
                Margin = new Thickness(12), Spacing = 4,
                Children =
                {
                    new TextBlock { Text = "MiSans 012345 \u76f4\u9aa8\u89d2\u5199\u753b", FontWeight = FontWeight.Normal },
                    new TextBlock { Text = "MiSans 012345 \u76f4\u9aa8\u89d2\u5199\u753b", FontWeight = FontWeight.SemiBold },
                    new TextBlock { Text = "MiSans 012345 \u76f4\u9aa8\u89d2\u5199\u753b", FontWeight = FontWeight.Bold }
                }
            }
        };
        Typography.ApplyTextOptions(window);
        var reference = new WeakReference(window);
        window.Show();
        await Task.Delay(30);
        window.Close();
        window = null;
        _fontWindowsOpened++;
        await Task.Delay(10);
        return reference;
    }

    private static long SampleFontMemory(string phase)
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        using var process = Process.GetCurrentProcess();
        process.Refresh();
        var bytes = OperatingSystem.IsWindows() ? process.PrivateMemorySize64 : process.WorkingSet64;
        FontMemory.Add(new { phase, bytes, metric = OperatingSystem.IsWindows() ? "privateBytes" : "workingSetBytes" });
        return bytes;
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
        ExitLater(1);
    }

    private static void WriteReceipt()
    {
        var report = new
        {
            success = Errors.Count == 0, passed = Checks.Count, checks = Checks, errors = Errors,
            nativeBluetoothCalls = 0, hostProcessesLaunched = 0, screenshots = MacBackdropCapture.WindowCaptures, output = _output,
            operatingSystem = OperatingSystem.IsMacOS() ? "macOS" : "Windows",
            fontWindowChurn = new { opened = _fontWindowsOpened, collected = _fontWindowsCollected,
                collectionInitializations = Typography.CollectionInitializationCount, memory = FontMemory },
            scope = "Isolated native desktop windows with fake services; exit votes, veto recovery, terminal close and cleanup faults; no real Host or headset."
        };
        var json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(Path.Combine(_output, "receipt.json"), json);
        Console.WriteLine(JsonSerializer.Serialize(report));
    }
}
