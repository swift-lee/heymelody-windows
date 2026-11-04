using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using HeyMelodyWindows.App;
using HeyMelodyWindows.Ui;

/// <summary>
/// Explicit --native-startup-smoke verification of five main-window recovery and
/// cancellation flows. Uses real Win32 windows off screen and fake headset/editor
/// services; never starts the Bluetooth worker, device manager or Windows settings.
/// </summary>
internal static class NativeStartupSmoke
{
    private static SmokeState _state = new();

    public static int Run()
    {
        if (!OperatingSystem.IsWindows())
        {
            Console.WriteLine(JsonSerializer.Serialize(new { success = false, reason = "Windows required", nativeBluetoothCalls = 0 }));
            return 2;
        }

        _state = new();
        var uiThread = new Thread(() =>
        {
            try
            {
                AppBuilder.Configure<SmokeApplication>().UsePlatformDetect().UseHeyMelodyTypography().LogToTrace()
                    .StartWithClassicDesktopLifetime([], ShutdownMode.OnExplicitShutdown);
            }
            catch (Exception error) { _state.Failure = error.ToString(); }
        }) { IsBackground = true, Name = "HeyMelody native startup smoke UI" };
        uiThread.SetApartmentState(ApartmentState.STA);
        uiThread.Start();
        if (!uiThread.Join(TimeSpan.FromSeconds(30))) _state.Failure ??= "Native startup UI thread did not stop in thirty seconds.";

        var success = _state.Failure is null && _state.Passed == 5
            && _state.Windows.Count == 5 && _state.ClosedWindows == 5 && _state.Completed;
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            schemaVersion = 1, success, passed = _state.Passed,
            createdWindows = _state.Windows.Count, closedWindows = _state.ClosedWindows,
            completed = _state.Completed, nativeBluetoothCalls = 0,
            editorProcessLaunches = 0, systemSettingsLaunchRequested = false,
            scope = "five real Win32 FAAppWindow startup and manual-management recovery/cancellation flows; in-memory headset service and fake editor process; no system settings, Bluetooth hardware or real editor launch",
            cases = _state.Cases, failure = _state.Failure
        }));
        return success ? 0 : 1;
    }

    private sealed class SmokeState
    {
        public List<MainWindow> Windows { get; } = [];
        public List<string> Cases { get; } = [];
        public int Passed { get; set; }
        public int ClosedWindows { get; set; }
        public bool Completed { get; set; }
        public string? Failure { get; set; }
    }

    // App.Initialize populates the normal compiled XAML and theme resources. Avoid
    // its framework completion override, which would create the live backend.
    public sealed class SmokeApplication : App
    {
        public override void OnFrameworkInitializationCompleted()
        {
            var desktop = ApplicationLifetime as IClassicDesktopStyleApplicationLifetime
                ?? throw new InvalidOperationException("Desktop lifetime required.");
            Dispatcher.UIThread.Post(() => _ = VerifyAndShutdownAsync(desktop));
            _ = StopOnTimeoutAsync(desktop);
        }
    }

    private static async Task VerifyAndShutdownAsync(IClassicDesktopStyleApplicationLifetime desktop)
    {
        try
        {
            await StartupTests.RunNativeWindowCasesAsync(window =>
            {
                window.ShowActivated = false;
                window.WindowStartupLocation = WindowStartupLocation.Manual;
                window.Position = new PixelPoint(-10000, -10000);
                desktop.MainWindow = window;
                _state.Windows.Add(window);
                window.Closed += (_, _) => _state.ClosedWindows++;
            }, name =>
            {
                _state.Passed++;
                _state.Cases.Add(name);
                Console.WriteLine("PASS " + name);
            });
            _state.Completed = true;
        }
        catch (Exception error) { _state.Failure = error.ToString(); }
        finally
        {
            foreach (var window in _state.Windows.Where(window => window.IsVisible).ToArray()) window.Close();
            var deadline = DateTime.UtcNow.AddSeconds(3);
            while (_state.ClosedWindows < _state.Windows.Count && DateTime.UtcNow < deadline) await Task.Delay(10);
            if (_state.ClosedWindows != _state.Windows.Count) _state.Failure ??= "An owned native startup window did not close.";
            desktop.Shutdown(_state.Failure is null ? 0 : 1);
        }
    }

    private static async Task StopOnTimeoutAsync(IClassicDesktopStyleApplicationLifetime desktop)
    {
        await Task.Delay(TimeSpan.FromSeconds(25));
        if (_state.Completed) return;
        _state.Failure ??= "Native startup smoke exceeded twenty-five seconds.";
        foreach (var window in _state.Windows.Where(window => window.IsVisible).ToArray()) window.Close();
        desktop.Shutdown(1);
    }
}
