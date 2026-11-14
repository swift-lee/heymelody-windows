using System.Globalization;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using FluentAvalonia.UI.Controls;
using HeyMelodyWindows.App;

internal static partial class FindHeadphonesTests
{
    internal static int Run(HeadlessUnitTestSession session, string output)
    {
        var directory = Path.Combine(output, "find-headphones-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var previous = ThemePreferences.SettingsPathOverride;
        try
        {
            ThemePreferences.SettingsPathOverride = Path.Combine(directory, "preferences.json");
            Preferences();
            session.Dispatch<bool>(async () =>
            {
                await StateAsync();
                await CopyFeedbackAsync();
                await MainPageAsync(directory);
                await SettingsAsync();
                return true;
            }, CancellationToken.None).GetAwaiter().GetResult();
        }
        finally { ThemePreferences.SettingsPathOverride = previous; }
        Console.WriteLine("PASS find headphones map preference preserves unrelated settings and defaults safely");
        Console.WriteLine("PASS find headphones offline device permissions and backend revisions remain independent of protocol controls");
        Console.WriteLine("PASS find headphones copy toggle confirms for five seconds and retires late clipboard results");
        Console.WriteLine("PASS find headphones coordinates, map confirmation and cancellation use only the selected device");
        Console.WriteLine("PASS find headphones permission drafts and asynchronous save failures retain settings");
        return 5;
    }

    private static void Preferences()
    {
        var path = ThemePreferences.SettingsPathOverride!;
        foreach (var text in new[] { "{}", "{\"mapProvider\":null}", "{\"mapProvider\":false}", "{\"mapProvider\":\"unknown\"}", "invalid" })
        {
            File.WriteAllText(path, text);
            Check(ThemePreferences.LoadMapProvider() == "openstreetmap", "Absent or invalid map choices use OpenStreetMap.");
        }
        File.WriteAllText(path, "{\"theme\":\"system\",\"otherPreference\":{\"value\":42}}");
        foreach (var provider in new[] { "openstreetmap", "apple", "google", "amap" })
        {
            Check(ThemePreferences.SaveMapProvider(provider) && ThemePreferences.LoadMapProvider() == provider,
                "Confirmed map providers round trip.");
            Check(JsonNode.Parse(File.ReadAllText(path))!["otherPreference"]!["value"]!.GetValue<int>() == 42,
                "Map saves preserve unrelated nested values.");
        }
        Check(ThemePreferences.Save("light") && ThemePreferences.LoadMapProvider() == "amap",
            "Ordinary application settings preserve the map provider.");
        var blocker = Path.Combine(Path.GetDirectoryName(path)!, "blocked");
        File.WriteAllText(blocker, "original");
        ThemePreferences.SettingsPathOverride = Path.Combine(blocker, "preferences.json");
        Check(!ThemePreferences.SaveMapProvider("apple") && File.ReadAllText(blocker) == "original",
            "Map preference write failures are reported without altering other files.");
        ThemePreferences.SettingsPathOverride = path;
    }

    private static async Task StateAsync()
    {
        var service = new LocationService();
        await using var vm = new MainViewModel(service, new NoConfirm());
        await vm.InitializeAsync();
        Check(!vm.FindHeadphonesEnabled && !vm.DeviceLocationRecordingEnabled && !vm.CanSetDeviceLocationRecording,
            "Both recording choices start disabled.");
        service.State = service.State with { Enabled = true, Revision = 2 };
        await vm.RefreshFindHeadphonesAsync();
        Check(vm.CanSetDeviceLocationRecording && !vm.HasVerifiedSnapshot,
            "Offline unknown models can configure recording without a verified protocol snapshot.");
        Check(vm.FindHeadphonesMessage == "将在耳机断开时核实定位权限。", "Unknown permission waits for a disconnect instead of being reported as paused.");
        await vm.SetDeviceLocationRecordingAsync(true);
        Check(vm.DeviceLocationRecordingEnabled && service.DeviceWrites == 1, "The independent device choice is saved.");
        var record = Location(22.3, 114.2);
        var recordedState = service.State with
        {
            Permission = new("allowed", ""),
            Devices = [new("unknown-a", true, record, "recorded", ""), new("unknown-b", false, null, "idle", "")],
            Revision = 10
        };
        vm.AdoptBackendEvent(Event(recordedState));
        Check(vm.HasRecordedLocation, "The latest successful record is presented.");
        vm.AdoptBackendEvent(Event(service.State with { Enabled = false, Revision = 9 }));
        Check(vm.FindHeadphonesEnabled && vm.HasRecordedLocation, "Out-of-order state cannot overwrite newer location data.");
        vm.AdoptBackendEvent(Event(service.State with { Enabled = false, Revision = 10 }));
        Check(vm.FindHeadphonesEnabled && vm.HasRecordedLocation, "A reused revision cannot change accepted state contents.");
        vm.AdoptBackendEvent(Event(recordedState with { Permission = new("denied", "系统定位权限已撤回。"), Revision = 11 }));
        Check(vm.HasRecordedLocation && vm.FindHeadphonesMessage.Contains("位置记录已暂停")
            && vm.FindHeadphonesMessage.Contains("权限已撤回"), "Revoked permission pauses recording while retaining old coordinates.");
        vm.AdoptBackendEvent(Event(recordedState with { Permission = new("unknown", "将在耳机断开时核实定位权限。"), Revision = 12 }));
        Check(vm.HasRecordedLocation && vm.FindHeadphonesMessage == "将在耳机断开时核实定位权限。",
            "A cold-start permission cache shows its pending explanation and preserves the last recorded location.");
        vm.AdoptBackendEvent(Event(recordedState with { Permission = null!, Revision = 13 }));
        Check(vm.HasRecordedLocation && vm.FindHeadphonesMessage == "将在耳机断开时核实定位权限。",
            "A sparse permission object falls back to the unknown explanation without losing recorded coordinates.");
        await vm.SelectDeviceAsync(vm.Devices.Single(device => device.Identity == "unknown-b"));
        Check(!vm.HasRecordedLocation && !vm.DeviceLocationRecordingEnabled, "Another device never inherits the first record.");
        await vm.SelectDeviceAsync(vm.Devices.Single(device => device.Identity == "unknown-a"));
        vm.AdoptBackendEvent(new BackendEvent { Kind = "unavailable", BackendInstanceId = "location-backend" });
        Check(vm.HasRecordedLocation && !vm.CanSetDeviceLocationRecording,
            "A backend outage preserves recorded coordinates and disables writes.");
        vm.AdoptBackendEvent(Event(service.State with { Enabled = false, Revision = 0 }, "replacement-backend"));
        Check(!vm.FindHeadphonesEnabled && !vm.HasRecordedLocation, "A new backend accepts its own revision sequence.");
        vm.AdoptBackendEvent(Event(service.State with { Enabled = true, Revision = 100 }));
        Check(!vm.FindHeadphonesEnabled, "Events from retired backends are ignored.");
        Check(service.HeadsetWrites == 0, "Location work never sends headset SET operations.");
    }

    private static async Task MainPageAsync(string output)
    {
        var service = new LocationService();
        service.State = service.State with
        {
            Devices = [new("unknown-a", true, Location(22.30123456, 114.20123456), "recorded", "")],
            Revision = 2
        };
        await using var vm = new MainViewModel(service, new NoConfirm());
        await vm.InitializeAsync();
        var view = new MainView { DataContext = vm };
        var window = new Window { Content = view, Width = 900, Height = 640 };
        var launcher = new MapLauncher();
        view.LauncherOverride = launcher;
        string? copied = null;
        view.WriteClipboardOverride = text => { copied = text; return Task.CompletedTask; };
        try
        {
            window.Show(); Dispatcher.UIThread.RunJobs();
            var panel = view.FindControl<FASettingsExpander>("FindHeadphonesPanel")!;
            Check(panel.IsVisible && !panel.IsExpanded && vm.DeviceLocationRecordingEnabled,
                $"The panel remains visible with a saved per-device choice while the global switch is off: visible={panel.IsVisible}, expanded={panel.IsExpanded}, enabled={vm.DeviceLocationRecordingEnabled}.");
            var stack = view.FindControl<StackPanel>("ControlStack")!;
            Check(stack.Children[^3] == panel && stack.Children[^2] == view.FindControl<FASettingsExpander>("DeviceInfoPanel")
                && IconTests.Matches(panel.IconSource, AppIcon.Location) && !view.FindControl<Button>("PlayFindHeadphonesButton")!.IsEnabled,
                "Find headphones precedes device information, uses a location icon and keeps playback disabled.");
            panel.IsExpanded = true; Dispatcher.UIThread.RunJobs();
            Check(!view.FindControl<ToggleSwitch>("DeviceLocationRecordingSwitch")!.IsEnabled,
                "The device switch remains disabled while global recording is off.");
            window.Width = 1280; window.Height = 900;
            view.FindControl<FASettingsExpander>("AncPanel")!.IsExpanded = false;
            foreach (var dark in new[] { false, true })
            {
                Avalonia.Application.Current!.RequestedThemeVariant = dark
                    ? Avalonia.Styling.ThemeVariant.Dark : Avalonia.Styling.ThemeVariant.Light;
                panel.BringIntoView(); Dispatcher.UIThread.RunJobs();
                await Task.Delay(30); Dispatcher.UIThread.RunJobs();
                using var frame = window.CaptureRenderedFrame() ?? throw new InvalidOperationException("Missing find-headphones pixels.");
                using var stream = File.Create(Path.Combine(output, "find-headphones-" + (dark ? "dark" : "light") + ".png"));
                frame.Save(stream, Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
            }
            var culture = CultureInfo.CurrentCulture;
            try
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
                await view.CopyLocationAsync();
                Check(copied == "geo:22.30123456,114.20123456", "geo coordinates always use latitude, longitude and decimal points.");
                Check(MainView.CreateLocationMapUri("amap", vm.RecordedLocation!).AbsoluteUri
                    .Contains("position=114.20123456,22.30123456&coordinate=wgs84"), "Amap receives WGS-84 longitude first.");
                Check(MainView.CreateLocationMapUri("apple", vm.RecordedLocation!).AbsoluteUri.Contains("&q="),
                    "Apple Maps receives a label that creates a pin at the recorded coordinates.");
            }
            finally { CultureInfo.CurrentCulture = culture; }
            ThemePreferences.SaveMapProvider("apple");
            var original = File.ReadAllText(ThemePreferences.SettingsPathOverride!);
            var mapOperation = view.OpenLocationMapDialogAsync();
            var mapDialog = await MapDialogAsync(window);
            var mapPanel = (LocationMapPanel)mapDialog.Content!;
            Check(mapPanel.FindControl<RadioButton>("LocationMapApple")!.IsChecked == true, "The map dialog restores the confirmed provider.");
            mapPanel.FindControl<RadioButton>("LocationMapGoogle")!.IsChecked = true;
            Check(launcher.Uris.Count == 0 && File.ReadAllText(ThemePreferences.SettingsPathOverride!) == original,
                "Changing a radio button has no external or persistent side effect.");
            ClickDialogButton(window, mapDialog, "取消"); await mapOperation.WaitAsync(TimeSpan.FromSeconds(3));
            Check(ThemePreferences.LoadMapProvider() == "apple", "Cancel discards the map dialog draft.");
            mapOperation = view.OpenLocationMapDialogAsync(); mapDialog = await MapDialogAsync(window);
            mapPanel = (LocationMapPanel)mapDialog.Content!;
            mapPanel.FindControl<RadioButton>("LocationMapGoogle")!.IsChecked = true;
            mapDialog.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, "取消")).Focus();
            window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
            window.KeyRelease(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null); Dispatcher.UIThread.RunJobs();
            await mapOperation.WaitAsync(TimeSpan.FromSeconds(3));
            Check(ThemePreferences.LoadMapProvider() == "apple" && launcher.Uris.Count == 0,
                "Escape discards the map dialog draft without saving or launching.");
            mapOperation = view.OpenLocationMapDialogAsync(); mapDialog = await MapDialogAsync(window);
            mapPanel = (LocationMapPanel)mapDialog.Content!;
            var googleChoice = mapPanel.FindControl<RadioButton>("LocationMapGoogle")!;
            googleChoice.BringIntoView(); Dispatcher.UIThread.RunJobs();
            var choicePoint = googleChoice.TranslatePoint(new Point(12, googleChoice.Bounds.Height / 2), window)!.Value;
            window.MouseDown(choicePoint, MouseButton.Left); window.MouseUp(choicePoint, MouseButton.Left); Dispatcher.UIThread.RunJobs();
            Check(!mapOperation.IsCompleted && mapPanel.SelectedProvider == "google", "Clicks inside the map dialog select without dismissing it.");
            var outsidePresses = 0;
            var coveredPage = view.FindControl<StackPanel>("ControlStack")!;
            EventHandler<PointerPressedEventArgs> coveredPress = (_, _) => outsidePresses++;
            coveredPage.AddHandler(InputElement.PointerPressedEvent, coveredPress, RoutingStrategies.Bubble);
            var outsideHandled = false;
            EventHandler<PointerPressedEventArgs> observeOutside = (_, args) => outsideHandled = args.Handled;
            window.AddHandler(InputElement.PointerPressedEvent, observeOutside, RoutingStrategies.Bubble, handledEventsToo: true);
            var scroll = view.FindControl<ScrollViewer>("ControlScroll")!;
            var outsidePoint = scroll.TranslatePoint(new Point(scroll.Bounds.Width - 40, 24), window)!.Value;
            window.MouseDown(outsidePoint, MouseButton.Left); window.MouseUp(outsidePoint, MouseButton.Left); Dispatcher.UIThread.RunJobs();
            await mapOperation.WaitAsync(TimeSpan.FromSeconds(3));
            coveredPage.RemoveHandler(InputElement.PointerPressedEvent, coveredPress);
            window.RemoveHandler(InputElement.PointerPressedEvent, observeOutside);
            Check(ThemePreferences.LoadMapProvider() == "apple" && launcher.Uris.Count == 0 && outsidePresses == 0 && outsideHandled,
                "An outside click dismisses the draft and is consumed before the covered page receives it.");
            mapOperation = view.OpenLocationMapDialogAsync(); mapDialog = await MapDialogAsync(window);
            mapPanel = (LocationMapPanel)mapDialog.Content!;
            mapPanel.FindControl<RadioButton>("LocationMapGoogle")!.IsChecked = true;
            ClickDialogButton(window, mapDialog, "打开"); await mapOperation.WaitAsync(TimeSpan.FromSeconds(3));
            Check(launcher.Uris.Count == 1 && launcher.Uris[0].AbsoluteUri.Contains("query=22.30123456,114.20123456")
                && ThemePreferences.LoadMapProvider() == "google", "Only Open confirms the choice and launches the map.");
            launcher.Success = false;
            mapOperation = view.OpenLocationMapDialogAsync(); mapDialog = await MapDialogAsync(window);
            mapPanel = (LocationMapPanel)mapDialog.Content!;
            mapPanel.FindControl<RadioButton>("LocationMapAmap")!.IsChecked = true;
            ClickDialogButton(window, mapDialog, "打开");
            await PumpUntilAsync(() => launcher.Uris.Count == 2 && mapDialog.IsPrimaryButtonEnabled);
            Check(ThemePreferences.LoadMapProvider() == "amap"
                && window.GetVisualDescendants().Contains(mapDialog) && mapPanel.SelectedProvider == "amap"
                && mapPanel.FindControl<FAInfoBar>("LocationMapError")!.IsOpen && !mapOperation.IsCompleted,
                "A failed launcher retains the dialog, selection and confirmed provider with recovery guidance.");
            ClickDialogButton(window, mapDialog, "取消"); await mapOperation.WaitAsync(TimeSpan.FromSeconds(3));
            mapOperation = view.OpenLocationMapDialogAsync(); await MapDialogAsync(window);
            await vm.SelectDeviceAsync(vm.Devices.Single(device => device.Identity == "unknown-b"));
            await mapOperation.WaitAsync(TimeSpan.FromSeconds(3));
            Check(!window.GetVisualDescendants().OfType<FAContentDialog>().Any() && !vm.HasRecordedLocation,
                "Device changes cancel the map dialog before another device can use its coordinates.");
            await vm.SelectDeviceAsync(vm.Devices.Single(device => device.Identity == "unknown-a"));
            mapOperation = view.OpenLocationMapDialogAsync(); await MapDialogAsync(window);
            await view.DismissSettingsDialogAsync(); await mapOperation.WaitAsync(TimeSpan.FromSeconds(3));
            view.ResumeSettingsDialogs();
            var settingsOperation = view.OpenSettingsDialogAsync();
            var settingsDialog = await SettingsDialogAsync(window);
            var leftoverMapHandler = false;
            EventHandler<PointerPressedEventArgs> observeSettingsPress = (_, args) => leftoverMapHandler = args.Handled;
            window.AddHandler(InputElement.PointerPressedEvent, observeSettingsPress, RoutingStrategies.Tunnel, handledEventsToo: true);
            window.MouseDown(outsidePoint, MouseButton.Left); window.MouseUp(outsidePoint, MouseButton.Left); Dispatcher.UIThread.RunJobs();
            window.RemoveHandler(InputElement.PointerPressedEvent, observeSettingsPress);
            Check(!settingsOperation.IsCompleted && window.GetVisualDescendants().Contains(settingsDialog) && !leftoverMapHandler,
                "Closing the page removes the map-only outside handler; application settings retain their own dismissal behavior.");
            ClickDialogButton(window, settingsDialog, "取消"); await settingsOperation.WaitAsync(TimeSpan.FromSeconds(3));
        }
        finally { await view.DismissSettingsDialogAsync(); window.Close(); }
        Check(service.HeadsetWrites == 0, "Map actions never write headset settings.");
    }

    private static async Task SettingsAsync()
    {
        var service = new LocationService();
        await using var vm = new MainViewModel(service, new NoConfirm());
        await vm.InitializeAsync();
        var view = new MainView { DataContext = vm };
        var window = new Window { Content = view, Width = 1280, Height = 900 };
        try
        {
            window.Show(); Dispatcher.UIThread.RunJobs();
            var operation = view.OpenSettingsDialogAsync();
            var dialog = await SettingsDialogAsync(window);
            var panel = (AppSettingsPanel)dialog.Content!;
            service.Permission = new("denied", "请开启系统定位权限。");
            await panel.CheckFindHeadphonesPermissionAsync();
            Check(!panel.FindHeadphonesDraft && service.GlobalWrites == 0
                && panel.FindControl<Button>("LocationSystemSettingsButton")!.IsVisible,
                "Permission denial keeps the draft off and exposes system settings guidance.");
            var pending = new TaskCompletionSource<UiLocationPermission>(TaskCreationOptions.RunContinuationsAsynchronously);
            service.PermissionTask = pending.Task;
            var checking = panel.CheckFindHeadphonesPermissionAsync();
            Check(!dialog.IsPrimaryButtonEnabled && !panel.FindControl<ToggleSwitch>("FindHeadphonesSwitch")!.IsEnabled,
                "Permission checks disable Save and repeated toggling.");
            pending.SetResult(new("allowed", ""));
            await checking;
            Check(panel.FindHeadphonesDraft && !service.State.Enabled && service.GlobalWrites == 0,
                "Granted system permission only changes the local draft.");
            dialog.Hide(); await operation.WaitAsync(TimeSpan.FromSeconds(3));
            Check(!service.State.Enabled && service.GlobalWrites == 0, "Cancel discards the granted draft without revoking OS permission.");

            service.PermissionTask = null; service.Permission = new("allowed", "");
            operation = view.OpenSettingsDialogAsync(); dialog = await SettingsDialogAsync(window);
            panel = (AppSettingsPanel)dialog.Content!;
            await panel.CheckFindHeadphonesPermissionAsync();
            service.SaveFailure = true;
            ClickSave(window, dialog);
            await PumpUntilAsync(() => service.GlobalWrites == 1 && !panel.FindHeadphonesBusy);
            Check(window.GetVisualDescendants().Contains(dialog) && panel.FindHeadphonesDraft
                && panel.FindControl<FAInfoBar>("LocationPermissionResult")!.IsOpen && !service.State.Enabled,
                "A failed asynchronous Host save keeps the settings dialog and validated draft.");
            service.SaveFailure = false;
            ClickSave(window, dialog);
            await operation.WaitAsync(TimeSpan.FromSeconds(3));
            Check(service.State.Enabled && service.GlobalWrites == 2, "Retry saves the draft and closes normally.");
        }
        finally { await view.DismissSettingsDialogAsync(); window.Close(); }
        Check(service.HeadsetWrites == 0, "Permission checks never submit headset commands.");
    }

    private static BackendEvent Event(UiFindHeadphonesState state, string backend = "location-backend") =>
        new() { Kind = "findHeadphones", BackendInstanceId = backend, FindHeadphones = state };
    private static UiRecordedLocation Location(double latitude, double longitude) =>
        new(latitude, longitude, 35, DateTimeOffset.Parse("2026-11-14T10:00:00Z"), DateTimeOffset.Parse("2026-11-14T10:00:02Z"));
    private static void Check(bool condition, string reason) { if (!condition) throw new InvalidOperationException(reason); }
    private static async Task PumpUntilAsync(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++) { Dispatcher.UIThread.RunJobs(); await Task.Delay(10); }
        Check(condition(), "Timed out waiting for UI operation.");
    }
    private static async Task<FAContentDialog> SettingsDialogAsync(Window window)
    {
        await PumpUntilAsync(() => window.GetVisualDescendants().OfType<FAContentDialog>().Any(dialog => Equals(dialog.Title, "应用设置")));
        return window.GetVisualDescendants().OfType<FAContentDialog>().Single(dialog => Equals(dialog.Title, "应用设置"));
    }
    private static async Task<FAContentDialog> MapDialogAsync(Window window)
    {
        await PumpUntilAsync(() => window.GetVisualDescendants().OfType<FAContentDialog>().Any(dialog => Equals(dialog.Title, "在地图上查看")));
        return window.GetVisualDescendants().OfType<FAContentDialog>().Single(dialog => Equals(dialog.Title, "在地图上查看"));
    }
    private static void ClickSave(Window window, FAContentDialog dialog) => ClickDialogButton(window, dialog, "保存");
    private static void ClickDialogButton(Window window, FAContentDialog dialog, string text)
    {
        var button = dialog.GetVisualDescendants().OfType<Button>().Single(value => Equals(value.Content, text));
        button.BringIntoView(); Dispatcher.UIThread.RunJobs();
        var point = button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), window)!.Value;
        window.MouseDown(point, MouseButton.Left); window.MouseUp(point, MouseButton.Left); Dispatcher.UIThread.RunJobs();
    }
    private sealed class NoConfirm : IConfirmationService
    { public Task<bool> ConfirmAsync(string title, string message, CancellationToken token) => Task.FromResult(false); }
    private sealed class MapLauncher : ILauncher
    {
        internal List<Uri> Uris { get; } = [];
        internal bool Success { get; set; } = true;
        public Task<bool> LaunchUriAsync(Uri uri) { Uris.Add(uri); return Task.FromResult(Success); }
        public Task<bool> LaunchFileAsync(IStorageItem file) => throw new InvalidOperationException("No file launch expected.");
    }
    private sealed class LocationService : IHeadsetEventService, IFindHeadphonesService
    {
        internal UiFindHeadphonesState State { get; set; } = new(false, new("unknown", ""),
            [new("unknown-a", false, null, "idle", ""), new("unknown-b", false, null, "idle", "")], 1);
        internal UiLocationPermission Permission { get; set; } = new("allowed", "");
        internal Task<UiLocationPermission>? PermissionTask { get; set; }
        internal bool SaveFailure { get; set; }
        internal int HeadsetWrites, GlobalWrites, DeviceWrites;
        public event Action<BackendEvent>? EventReceived { add { } remove { } }
        private static readonly UiDevice[] Devices = [new("unknown-a", "Unknown Headset A", false), new("unknown-b", "Unknown Headset B", false)];
        public Task<IReadOnlyList<UiDevice>> EnumerateAsync(CancellationToken token) => Task.FromResult<IReadOnlyList<UiDevice>>(Devices);
        public Task<BackendSubscription> SubscribeAsync(string role, CancellationToken token) =>
            Task.FromResult(new BackendSubscription { BackendInstanceId = "location-backend", Devices = Devices, FindHeadphones = State });
        public Task<UiSnapshot> ConnectAsync(string identity, CancellationToken token) => Task.FromResult(new UiSnapshot
            { Identity = identity, DeviceName = identity, BackendInstanceId = "location-backend", SupportedModel = false, Connected = false });
        public Task<UiSnapshot> SelectAsync(string identity, CancellationToken token) => ConnectAsync(identity, token);
        public Task<UiSnapshot> PauseAsync(string identity, CancellationToken token) => ConnectAsync(identity, token);
        public Task<UiSnapshot> ResumeAsync(string identity, CancellationToken token) => ConnectAsync(identity, token);
        public Task<UiSnapshot> RefreshAsync(string identity, RefreshScope scope, CancellationToken token) => ConnectAsync(identity, token);
        public Task<UiSnapshot> RefreshAsync(CancellationToken token) => ConnectAsync("unknown-a", token);
        public Task DisconnectAsync(CancellationToken token) => Task.CompletedTask;
        public Task AcknowledgeExitAsync(string request, bool approved, CancellationToken token) => Task.CompletedTask;
        public Task ActivateMainUiAsync(CancellationToken token) => Task.CompletedTask;
        public Task<UiApplyResult> ApplyAsync(string identity, long generation, UiAction action, CancellationToken token) => ApplyAsync(action, token);
        public Task<UiApplyResult> ApplyAsync(UiAction action, CancellationToken token)
        { HeadsetWrites++; throw new InvalidOperationException("Location operations cannot submit headset SET commands."); }
        public Task<UiFindHeadphonesState> GetFindHeadphonesAsync(CancellationToken token) => Task.FromResult(State);
        public Task<UiLocationPermission> RequestLocationPermissionAsync(CancellationToken token) =>
            PermissionTask?.WaitAsync(token) ?? Task.FromResult(Permission);
        public Task<UiFindHeadphonesState> SetFindHeadphonesEnabledAsync(bool enabled, CancellationToken token)
        {
            GlobalWrites++;
            if (SaveFailure) throw new IOException("Expected save failure.");
            State = State with { Enabled = enabled, Permission = Permission, Revision = State.Revision + 1 };
            return Task.FromResult(State);
        }
        public Task<UiFindHeadphonesState> SetDeviceLocationRecordingAsync(string identity, bool enabled, CancellationToken token)
        {
            DeviceWrites++;
            State = State with { Devices = State.Devices.Select(device => device.Identity == identity ? device with { Enabled = enabled } : device).ToArray(),
                Revision = State.Revision + 1 };
            return Task.FromResult(State);
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
