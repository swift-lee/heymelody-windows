using System.Security.Cryptography;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using FluentAvalonia.UI.Controls;
using HeyMelodyWindows.App;

internal static class DemoDeviceTests
{
    public static int Run(HeadlessUnitTestSession session, string output, List<object> receipts)
    {
        var passed = 0;
        if (HeyMelody.Platform.DesktopCapabilities.SupportsHeadsetControl)
        {
            Run("add-device release before two seconds keeps the normal short click", ShortClickAsync);
            Run("two-second hold adds one demo and releases without opening settings", HoldAsync);
            Run("release at the threshold handles a delayed timer callback exactly once", LateTimerAsync);
            Run("keyboard input during a hold keeps its mouse release suppressed", KeyboardDuringHoldAsync);
            Run("moving away, capture loss, disabling and detaching cancel the pending hold", CancellationAsync);
        }
        else Console.WriteLine("SKIP 5 add-device gesture tests: headset control is unavailable on this platform.");
        Run("demo loads the exact Dynaudio asset and corrects only its runtime material", DynaudioArtworkAsync);
        Run("demo fit and hearing buttons never launch calibration delegates", CalibrationButtonsAsync);
        foreach (var dark in new[] { false, true })
        foreach (var size in new[] { new Size(1280, 900), new Size(900, 640) })
            Run($"demo complete layout {(dark ? "dark" : "light")} {size.Width}x{size.Height}",
                () => LayoutAsync(dark, size, output, receipts));
        return passed;

        void Run(string label, Func<Task> test)
        {
            session.Dispatch<bool>(async () => { await test(); return true; }, CancellationToken.None).GetAwaiter().GetResult();
            passed++; Console.WriteLine("PASS " + label);
        }
    }

    private static async Task ShortClickAsync()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Press(); fixture.Time.Advance(TimeSpan.FromMilliseconds(1999));
        fixture.View.EvaluateAddDeviceHold();
        Check(!fixture.Vm.IsDemoSelected, "A hold shorter than two seconds must not add a demo.");
        fixture.Release();
        await UntilAsync(() => fixture.Launcher.Uris.Count == 1);
        var expectedUri = OperatingSystem.IsMacOS()
            ? "x-apple.systempreferences:com.apple.BluetoothSettings" : "ms-settings:bluetooth";
        Check(fixture.Launcher.Uris.Single().OriginalString == expectedUri
            && fixture.Vm.Devices.Count == 0 && fixture.Service.Calls == 1,
            "The original short click still opens Bluetooth settings once and does not query devices.");
    }

    private static async Task HoldAsync()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Press(); fixture.Time.Advance(TimeSpan.FromSeconds(2));
        fixture.View.EvaluateAddDeviceHold();
        await UntilAsync(() => fixture.Vm.IsDemoSelected && fixture.Vm.CanManageDevices);
        Check(fixture.Vm.Devices.Count(MainViewModel.IsDemoDevice) == 1 && fixture.Launcher.Uris.Count == 0,
            "The demo appears while the pointer is still held.");
        fixture.Time.Advance(TimeSpan.FromSeconds(5)); fixture.View.EvaluateAddDeviceHold(); fixture.Release();
        Check(fixture.Launcher.Uris.Count == 0 && fixture.Vm.Devices.Count == 1, "The same gesture cannot add or click twice.");
        var demo = fixture.Vm.SelectedDevice;
        fixture.Press(); fixture.Time.Advance(TimeSpan.FromSeconds(2)); fixture.View.EvaluateAddDeviceHold(); fixture.Release();
        await UntilAsync(() => fixture.Vm.CanManageDevices);
        Check(fixture.Vm.Devices.Count == 1 && fixture.Vm.SelectedDevice == demo && fixture.Service.Calls == 1,
            "A repeated hold selects the same demo without a backend call.");
        fixture.Press(); fixture.Release();
        await UntilAsync(() => fixture.Launcher.Uris.Count == 1);
        Check(fixture.Vm.IsDemoSelected, "Click suppression ends with its gesture; a later short click still works.");
    }

    private static async Task LateTimerAsync()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Press(); fixture.Time.Advance(TimeSpan.FromSeconds(2));
        // Deliberately do not dispatch the timer: releasing must evaluate monotonic elapsed time.
        fixture.Release(); await UntilAsync(() => fixture.Vm.IsDemoSelected && fixture.Vm.CanManageDevices);
        fixture.View.EvaluateAddDeviceHold(); Dispatcher.UIThread.RunJobs();
        Check(fixture.Vm.Devices.Count == 1 && fixture.Launcher.Uris.Count == 0 && fixture.Service.Calls == 1,
            "The delayed callback cannot duplicate the demo or allow the release to launch settings.");
    }

    private static async Task CancellationAsync()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Press(); fixture.Time.Advance(TimeSpan.FromSeconds(1));
        fixture.Window.MouseMove(new Point(5, 5));
        fixture.Time.Advance(TimeSpan.FromSeconds(3)); fixture.View.EvaluateAddDeviceHold();
        fixture.Window.MouseUp(new Point(5, 5), MouseButton.Left); Dispatcher.UIThread.RunJobs();
        Check(fixture.Vm.Devices.Count == 0, "Moving outside the button cancels a pending hold.");
        fixture.Press(); fixture.View.CancelAddDeviceHold();
        fixture.Time.Advance(TimeSpan.FromSeconds(3)); fixture.View.EvaluateAddDeviceHold();
        fixture.Window.MouseUp(new Point(5, 5), MouseButton.Left); Dispatcher.UIThread.RunJobs();
        Check(fixture.Vm.Devices.Count == 0, "The shared capture-loss/deactivation cancellation path stops the hold.");
        fixture.Press(); fixture.View.FindControl<Border>("AddDeviceHost")!.IsEnabled = false;
        fixture.Time.Advance(TimeSpan.FromSeconds(3)); fixture.View.EvaluateAddDeviceHold(); fixture.Release();
        Check(fixture.Vm.Devices.Count == 0, "Disabling the button cancels a pending hold.");
        fixture.View.FindControl<Border>("AddDeviceHost")!.IsEnabled = true; Dispatcher.UIThread.RunJobs();
        fixture.Press(); fixture.Window.Content = null;
        fixture.Time.Advance(TimeSpan.FromSeconds(3)); fixture.View.EvaluateAddDeviceHold();
        fixture.Window.MouseUp(new Point(5, 5), MouseButton.Left); Dispatcher.UIThread.RunJobs();
        Check(fixture.Vm.Devices.Count == 0 && fixture.Launcher.Uris.Count == 0,
            "Detaching cancels the pending gesture and none of the cancellation paths launch settings.");
    }

    private static async Task KeyboardDuringHoldAsync()
    {
        await using var fixture = await Fixture.CreateAsync();
        var button = fixture.View.FindControl<Button>("AddDeviceButton")!;
        foreach (bool completed in new[] { false, true })
        {
            fixture.Press();
            fixture.Time.Advance(TimeSpan.FromSeconds(completed ? 2 : 1));
            if (completed)
            {
                fixture.View.EvaluateAddDeviceHold();
                await UntilAsync(() => fixture.Vm.IsDemoSelected && fixture.Vm.CanManageDevices);
            }
            button.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.LeftShift });
            fixture.Time.Advance(TimeSpan.FromSeconds(3));
            fixture.View.EvaluateAddDeviceHold(); fixture.Release();
            Check(fixture.Launcher.Uris.Count == 0 && fixture.Vm.Devices.Count == (completed ? 1 : 0),
                "Keyboard cancellation retains the mouse-release suppression marker.");
        }
        // Independent keyboard activation remains a normal click.
        button.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });
        await UntilAsync(() => fixture.Launcher.Uris.Count == 1);
    }

    private static async Task DynaudioArtworkAsync()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.Vm.AddDemoDeviceAsync(); Dispatcher.UIThread.RunJobs();
        CheckDynaudioPresentation(fixture.View, fixture.Vm);
        using var catalog = new DeviceVisualCatalog();
        catalog.Update(fixture.Vm.Devices, fixture.Vm.SelectedDevice, fixture.Vm.ProductId,
            DemoDeviceData.Color, artwork: fixture.Service);
        Check(catalog.ProductFor(fixture.Vm.SelectedDevice) is EncoFree4DynaudioModelVisual,
            "The demo catalog uses the Dynaudio model without acquiring backend artwork.");
        Check(DeviceVisuals.DefaultTemplate.Build(new EncoFree4DynaudioModelVisual()) is EncoFree4DynaudioModelView,
            "The model template selects the Dynaudio renderer rather than ordinary Free4.");
        await DynaudioModelTests.VerifyAsync();
        Check(fixture.Service.Calls == 1, "Model selection and parsing do not contact the headset or artwork backend.");
    }

    private static void CheckDynaudioPresentation(MainView view, MainViewModel vm)
    {
        Check(vm.ProductId == "06C010" && DemoDeviceData.Color == 3 && vm.Title == "Demo · Enco Free4 丹拿版",
            "The demo identifies the model and color used to obtain the Dynaudio resource.");
        Check(view.FindControl<ProductArtwork>("HeroArt")!.Visual is EncoFree4DynaudioModelVisual
            && view.FindControl<Grid>("HeroPanel")!.Children.Count == 1,
            "The demo selects the bundled Dynaudio model without duplicate captions beneath it.");
        Check(view.FindControl<FAInfoBar>("DemoNotice")!.Message == "控件仅模拟交互，关闭窗口后自动移除。"
            && vm.Subtitle == "演示设备" && view.FindControl<Avalonia.Controls.Shapes.Ellipse>("ConnectionDot")!.Classes.Contains("online"),
            "The temporary-device notice and header stay brief while marking the demo as connected.");
    }

    private static async Task CalibrationButtonsAsync()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.Vm.AddDemoDeviceAsync(); Dispatcher.UIThread.RunJobs();
        int launches = 0;
        fixture.View.RequestFitTest = () => { launches++; return Task.CompletedTask; };
        fixture.View.RequestHearingSetup = () => { launches++; return Task.CompletedTask; };
        fixture.View.FindControl<FASettingsExpander>("HearingPanel")!.IsExpanded = true;
        Dispatcher.UIThread.RunJobs();
        foreach (var label in new[] { "开始测试", "设置黄金听感" })
        {
            var button = fixture.View.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, label));
            var point = await MainUiTestLayout.ClickPointAsync(fixture.Window, button);
            fixture.Window.MouseDown(point, MouseButton.Left); fixture.Window.MouseUp(point, MouseButton.Left);
            Dispatcher.UIThread.RunJobs();
            var action = label == "开始测试" ? "耳塞贴合度测试" : label;
            Check(launches == 0 && fixture.Vm.Status.Contains(action, StringComparison.Ordinal)
                && fixture.Vm.Status.Contains(DemoDeviceData.Notice, StringComparison.Ordinal),
                $"Demo action '{label}' provides feedback without invoking a calibration process; status: {fixture.Vm.Status}.");
        }
        Check(fixture.Service.Calls == 1 && fixture.Vm.CanSetupHearing && fixture.Vm.CanFitTest,
            "Demo test controls are enabled without contacting the headset service.");
    }

    private static async Task LayoutAsync(bool dark, Size size, string output, List<object> receipts)
    {
        Application.Current!.RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light;
        await using var fixture = await Fixture.CreateAsync(size);
        await fixture.Vm.AddDemoDeviceAsync(); Dispatcher.UIThread.RunJobs();
        var view = fixture.View; var window = fixture.Window;
        Check(view.IsCompact == (size.Width < 1080), "The demo uses the existing responsive breakpoint.");
        Check(view.FindControl<FAInfoBar>("DemoNotice") is { IsOpen: true, IsVisible: true }, "The temporary-device notice is visible.");
        CheckDynaudioPresentation(view, fixture.Vm);
        var anc = view.FindControl<FASettingsExpander>("AncPanel")!;
        Check(anc.IsExpanded && view.GetVisualDescendants().OfType<FASettingsExpander>()
                .Where(expander => !ReferenceEquals(expander, anc)).All(expander => !expander.IsExpanded)
            && !view.FindControl<FASettingsExpander>("OperationLogPanel")!.IsExpanded,
            "Demo ANC starts expanded while optional settings and operation history start collapsed like real-device settings.");
        await MainUiTestLayout.ExpandSettingsAsync(view);
        Check(view.FindControl<ItemsControl>("BandEditor")!.Items.Count == 10, "The complete ten-band EQ editor is present.");
        DeviceBarTests.CheckBottomBar(view, window);
        var picker = view.FindControl<ListBox>("DevicePicker")!;
        Check(Equals(picker.SelectedItem, fixture.Vm.SelectedDevice)
            && picker.GetVisualDescendants().OfType<TextBlock>().Any(text => text.Text == "演示设备"),
            "The selected card identifies the device as a demo.");
        CheckFeatureEditorLayout(view);
        var scroll = view.FindControl<ScrollViewer>("ControlScroll")!;
        var log = view.GetVisualDescendants().OfType<FASettingsExpander>().Single(expander => Equals(expander.Header, "操作记录"));
        log.IsExpanded = true; Dispatcher.UIThread.RunJobs();
        var maximum = Math.Max(0, scroll.Extent.Height - scroll.Viewport.Height);
        var step = Math.Max(1, scroll.Viewport.Height * 0.8);
        var offsets = Enumerable.Range(0, (int)Math.Ceiling(maximum / step)).Select(index => index * step).Append(maximum).Distinct().ToArray();
        for (var index = 0; index < offsets.Length; index++)
        {
            scroll.Offset = new Vector(0, offsets[index]); Dispatcher.UIThread.RunJobs();
            using var frame = window.CaptureRenderedFrame() ?? throw new InvalidOperationException("Missing demo Skia pixels.");
            Check(frame.PixelSize == new PixelSize((int)size.Width, (int)size.Height), "Demo screenshot dimensions match the viewport.");
            var name = $"demo-{(dark ? "dark" : "light")}-{(int)size.Width}x{(int)size.Height}-page{index + 1:00}.png";
            var path = Path.Combine(output, name);
            using (var stream = new FileStream(path, FileMode.CreateNew)) frame.Save(stream, Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
            receipts.Add(new { scenario = "demo", theme = dark ? "dark" : "light", width = size.Width, height = size.Height,
                scrollY = scroll.Offset.Y, file = name, sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant() });
        }
        Check(scroll.Offset.Y >= maximum - 1 && fixture.Service.Calls == 1,
            "The screenshots cover the entire controls column and rendering never queries demo artwork or a headset.");
    }

    private static async Task UntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(3);
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline) throw new TimeoutException("Demo UI interaction did not finish.");
            await Task.Delay(10); Dispatcher.UIThread.RunJobs();
        }
        Dispatcher.UIThread.RunJobs();
    }
    internal static IReadOnlyList<object> CheckFeatureEditorLayout(MainView view)
    {
        var vm = (MainViewModel)view.DataContext!;
        var scroll = view.FindControl<ScrollViewer>("ControlScroll")!;
        var controls = view.GetVisualDescendants().OfType<FeatureControlView>().ToArray();
        Check(controls.Length == vm.FeatureSections.Sum(section => section.Controls.Count) + vm.EqualizerFeatures.Count,
            "Every dynamic control has an instantiated editor.");
        var sliderLayouts = new List<object>();
        foreach (var control in controls)
        {
            var model = (FeatureControlViewModel)control.DataContext!;
            var editor = control.Footer as Control ?? throw new InvalidOperationException("A demo control has no editor.");
            var origin = editor.TranslatePoint(default, scroll)!.Value;
            Check(editor.IsEffectivelyEnabled && editor.Bounds.Width > 0 && editor.Bounds.Height > 0
                && origin.X >= -1 && origin.X + editor.Bounds.Width <= scroll.Bounds.Width + 1,
                $"Demo editor {model.Id} fits within the control column: " +
                $"enabled={editor.IsEffectivelyEnabled}, bounds={editor.Bounds}, origin={origin}, scroll={scroll.Bounds}");
            if (model.Id is not ("bass" or "prompt-volume")) continue;
            var row = editor as StackPanel ?? throw new InvalidOperationException($"The {model.Id} slider and value must share a layout row.");
            var slider = row.Children.OfType<Slider>().Single();
            var value = row.Children.OfType<TextBlock>().Single();
            var tickBars = slider.GetVisualDescendants().OfType<TickBar>()
                .Where(ticks => ticks.IsEffectivelyVisible && ticks.Bounds.Width > 0 && ticks.Bounds.Height > 0).ToArray();
            var sliderOrigin = slider.TranslatePoint(default, row)!.Value;
            var valueOrigin = value.TranslatePoint(default, row)!.Value;
            Check(tickBars.Length > 0 && slider.IsSnapToTickEnabled && slider.TickFrequency == model.Step,
                $"The native {model.Id} editor lays out visible ticks and preserves the device's discrete step.");
            var expectedValue = model.Definition.Value is null ? "—"
                : model.NumberValue.ToString("0", System.Globalization.CultureInfo.CurrentCulture);
            Check(value.Text == expectedValue
                && value.Bounds.Width > 0 && value.DesiredSize.Width <= value.Bounds.Width + 1
                && valueOrigin.X >= sliderOrigin.X + slider.Bounds.Width
                && valueOrigin.X + value.Bounds.Width <= row.Bounds.Width + 1,
                $"The {model.Id} integer readout stays to the right of the slider without clipping or overlap.");
            sliderLayouts.Add(new
            {
                id = model.Id, model.Minimum, model.Maximum, model.Step, value = value.Text,
                editorWidth = row.Bounds.Width, sliderWidth = slider.Bounds.Width,
                valueX = valueOrigin.X, valueWidth = value.Bounds.Width, visibleTickBars = tickBars.Length
            });
        }
        Check(sliderLayouts.Count == 2, "Both bass and prompt-volume editors have verified native slider/readout geometry.");
        return sliderLayouts;
    }

    private static void Check(bool condition, string reason)
    { if (!condition) throw new InvalidOperationException(reason); }
    private sealed class ManualTimeProvider : TimeProvider
    {
        private long _timestamp;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _timestamp;
        public void Advance(TimeSpan amount) => _timestamp += amount.Ticks;
    }
    private sealed class Launcher : ILauncher
    {
        public List<Uri> Uris { get; } = [];
        public Task<bool> LaunchUriAsync(Uri uri) { Uris.Add(uri); return Task.FromResult(true); }
        public Task<bool> LaunchFileAsync(IStorageItem file) => throw new InvalidOperationException("Unexpected file launch.");
    }
    private sealed class Service : IHeadsetService, IArtworkService
    {
        public int Calls { get; private set; }
        public Task<IReadOnlyList<UiDevice>> EnumerateAsync(CancellationToken token)
        { Calls++; return Task.FromResult<IReadOnlyList<UiDevice>>([]); }
        public Task<UiSnapshot> ConnectAsync(string identity, CancellationToken token)
        { Calls++; throw new InvalidOperationException("Demo must not connect to hardware."); }
        public Task DisconnectAsync(CancellationToken token)
        { Calls++; throw new InvalidOperationException("Demo must not disconnect hardware."); }
        public Task<UiSnapshot> RefreshAsync(CancellationToken token)
        { Calls++; throw new InvalidOperationException("Demo must not read hardware."); }
        public Task<UiApplyResult> ApplyAsync(UiAction action, CancellationToken token)
        { Calls++; throw new InvalidOperationException("Demo must not submit a setter."); }
        public Task<UiArtwork?> AcquireArtworkAsync(string productId, int? color, string role, CancellationToken token)
        { Calls++; throw new InvalidOperationException("Demo artwork must be bundled."); }
        public Task ReleaseArtworkAsync(string leaseId, CancellationToken token)
        { Calls++; throw new InvalidOperationException("Demo must not own a backend artwork lease."); }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed class Fixture : IAsyncDisposable
    {
        public Service Service { get; } = new();
        public Launcher Launcher { get; } = new();
        public ManualTimeProvider Time { get; } = new();
        public MainViewModel Vm { get; }
        public MainView View { get; }
        public Window Window { get; }
        private Fixture(Size size)
        {
            Vm = new MainViewModel(Service, new NoConfirm());
            View = new MainView { DataContext = Vm, LauncherOverride = Launcher, DemoHoldTimeProvider = Time };
            Window = new Window { Content = View, Width = size.Width, Height = size.Height, WindowDecorations = WindowDecorations.None };
        }
        public static async Task<Fixture> CreateAsync(Size? size = null)
        {
            var fixture = new Fixture(size ?? new Size(1280, 900));
            await fixture.Vm.InitializeAsync(); fixture.Window.Show(); Dispatcher.UIThread.RunJobs();
            return fixture;
        }
        private Point ButtonPoint => View.FindControl<Button>("AddDeviceButton")!
            .TranslatePoint(new Point(24, 24), Window)!.Value;
        public void Press() { Window.MouseDown(ButtonPoint, MouseButton.Left); Dispatcher.UIThread.RunJobs(); }
        public void Release() { Window.MouseUp(ButtonPoint, MouseButton.Left); Dispatcher.UIThread.RunJobs(); }
        public async ValueTask DisposeAsync()
        {
            await View.DismissSettingsDialogAsync(); Window.Close(); await Vm.DisposeAsync();
        }
    }
}
