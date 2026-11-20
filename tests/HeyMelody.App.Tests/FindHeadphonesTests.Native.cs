using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Styling;
using Avalonia.Threading;
using FluentAvalonia.UI.Controls;
using HeyMelodyWindows.App;
using HeyMelodyWindows.Ui;

internal static partial class FindHeadphonesTests
{
    private static NativeLocationState _nativeLocation = new();

    internal static int RunNative(string output)
    {
        if (!OperatingSystem.IsWindows()) return 2;
        Directory.CreateDirectory(output);
        _nativeLocation = new() { Output = output };
        var previous = ThemePreferences.SettingsPathOverride;
        var thread = new Thread(() =>
        {
            ThemePreferences.SettingsPathOverride = Path.Combine(output, "isolated-preferences.json");
            try
            {
                AppBuilder.Configure<NativeLocationApplication>().UsePlatformDetect().UseHeyMelodyTypography()
                    .StartWithClassicDesktopLifetime([], ShutdownMode.OnExplicitShutdown);
            }
            catch (Exception error) { _nativeLocation.Error = error.ToString(); }
            finally { ThemePreferences.SettingsPathOverride = previous; }
        }) { IsBackground = true, Name = "HeyMelody find headphones native smoke" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(45))) _nativeLocation.Error ??= "Native location smoke timed out.";
        var success = _nativeLocation.Error is null && _nativeLocation.Closed && _nativeLocation.Screenshots.Count == 12;
        var report = JsonSerializer.Serialize(new
        {
            success, nativeBluetoothCalls = 0, headsetSetCalls = _nativeLocation.Service.HeadsetWrites,
            systemPermissionRequests = 0, fakeToneCommands = _nativeLocation.Service.ToneCalls.Count, windowClosed = _nativeLocation.Closed,
            screenshots = _nativeLocation.Screenshots, failure = _nativeLocation.Error,
            scope = "Real Win32 window, compositor screenshots and light/dark layout with fake location and tone services; no Host, Bluetooth, device writes, real position, permission calls or external maps."
        }, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(Path.Combine(output, "report.json"), report);
        Console.WriteLine(report);
        return success ? 0 : 1;
    }

    private sealed class NativeLocationState
    {
        internal string Output = "";
        internal readonly LocationService Service = new();
        internal string? Error;
        internal bool Closed;
        internal readonly List<string> Screenshots = [];
    }

    public sealed class NativeLocationApplication : App
    {
        public override void OnFrameworkInitializationCompleted()
        {
            var desktop = ApplicationLifetime as IClassicDesktopStyleApplicationLifetime
                ?? throw new InvalidOperationException("Desktop lifetime required.");
            _nativeLocation.Service.State = new(false, new("allowed", ""),
                [new("unknown-a", true, Location(22.30123456, 114.20123456), "recorded", "")], 1);
            var window = new MainWindow(_nativeLocation.Service, preview: false)
            {
                Width = 1280, Height = 900, Topmost = true, ShowActivated = true,
                WindowStartupLocation = WindowStartupLocation.CenterScreen
            };
            desktop.MainWindow = window;
            window.Closed += (_, _) => _nativeLocation.Closed = true;
            window.Opened += (_, _) => _ = VerifyNativeLocationAsync(window, desktop);
            _ = NativeLocationWatchdogAsync(window, desktop);
        }
    }

    private static async Task VerifyNativeLocationAsync(MainWindow window, IClassicDesktopStyleApplicationLifetime desktop)
    {
        try
        {
            var view = window.FindControl<MainView>("Page")!;
            var vm = (MainViewModel)view.DataContext!;
            for (var i = 0; i < 100 && !vm.HasRecordedLocation; i++) await Task.Delay(30);
            Check(vm.HasRecordedLocation && !vm.HasVerifiedSnapshot, "The native UI uses offline fake location data.");
            Check(window.TryGetPlatformHandle()?.HandleDescriptor == "HWND", "A real Win32 window is required.");
            var panel = view.FindControl<FASettingsExpander>("FindHeadphonesPanel")!;
            view.FindControl<FASettingsExpander>("AncPanel")!.IsExpanded = false;
            panel.IsExpanded = true;
            foreach (var dark in new[] { false, true })
            {
                Application.Current!.RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light;
                var theme = dark ? "dark" : "light";
                foreach (var size in new[] { new Size(1280, 900), new Size(900, 640) })
                {
                    window.Width = size.Width; window.Height = size.Height;
                    await Task.Delay(250);
                    panel.BringIntoView();
                    await Task.Delay(200);
                    CaptureNativeLocation(window, $"location-{theme}-{(int)size.Width}x{(int)size.Height}.png");
                    var mapOperation = view.OpenLocationMapDialogAsync();
                    var mapDialog = await MapDialogAsync(window);
                    await Task.Delay(450);
                    CaptureNativeLocation(window, $"maps-{theme}-{(int)size.Width}x{(int)size.Height}.png");
                    mapDialog.Hide();
                    await mapOperation;
                }
                window.Width = 1280; window.Height = 900;
                await Task.Delay(200);
                var operation = view.OpenSettingsDialogAsync();
                var dialog = await SettingsDialogAsync(window);
                ((AppSettingsPanel)dialog.Content!).FindControl<FASettingsExpanderItem>("FindHeadphonesSettingsCard")!.BringIntoView();
                await Task.Delay(250);
                CaptureNativeLocation(window, $"settings-{theme}.png");
                dialog.Hide();
                await operation;
            }
            _nativeLocation.Service.ToneEnabled = true;
            foreach (var worn in new[] { false, true })
            {
                Application.Current!.RequestedThemeVariant = worn ? ThemeVariant.Dark : ThemeVariant.Light;
                vm.AdoptBackendEvent(ToneEvent(_nativeLocation.Service.ToneSnapshot("unknown-a", worn)));
                var tone = vm.ToggleFindHeadphonesToneAsync();
                var warning = await CapturedToneDialogAsync(window);
                await Task.Delay(300);
                CaptureNativeLocation(window, worn ? "tone-confirmation-worn.png" : "tone-confirmation.png");
                warning.Hide();
                await tone;
            }
            Check(_nativeLocation.Service.HeadsetWrites == 0 && _nativeLocation.Service.GlobalWrites == 0 && _nativeLocation.Service.ToneCalls.Count == 0,
                "Native layout verification performs no location or headset writes.");
        }
        catch (Exception error) { _nativeLocation.Error = error.ToString(); }
        finally
        {
            window.Close();
            await Task.Delay(100);
            desktop.Shutdown(_nativeLocation.Error is null ? 0 : 1);
        }
    }

    private static void CaptureNativeLocation(Window window, string name)
    {
        var path = Path.Combine(_nativeLocation.Output, name);
        NativeWindowCapture.Save(window.TryGetPlatformHandle()!.Handle, path);
        _nativeLocation.Screenshots.Add(path);
    }

    private static async Task NativeLocationWatchdogAsync(Window window, IClassicDesktopStyleApplicationLifetime desktop)
    {
        await Task.Delay(TimeSpan.FromSeconds(30));
        if (_nativeLocation.Closed) return;
        _nativeLocation.Error ??= "Native location verification exceeded its timeout.";
        await Dispatcher.UIThread.InvokeAsync(() => { window.Close(); desktop.Shutdown(1); });
    }
}
