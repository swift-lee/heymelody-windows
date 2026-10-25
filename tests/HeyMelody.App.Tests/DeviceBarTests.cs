using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using FluentAvalonia.Styling;
using FluentAvalonia.UI.Controls;
using HeyMelodyWindows.App;

internal static class DeviceBarTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(3);

    public static int Run(HeadlessUnitTestSession session)
    {
        Run("device cards switch sessions and restore selection when EQ discard is cancelled", DeviceSelectionAsync);
        Run("long device names remain bounded and add device scrolls after every card", DeviceLayoutAsync);
        Run("device card contents and a short device strip are centered", DeviceCenteredLayoutAsync);
        Run("opening with overflowing devices centers the selection within natural strip boundaries", DeviceStartupCenteredAsync);
        Run("selection and resizing respect natural boundaries without overriding manual scrolling", DeviceCenteringInteractionsAsync);
        Run("selected device borders follow theme and accent changes", DeviceThemeAsync);
        if (HeyMelody.Platform.DesktopCapabilities.SupportsHeadsetControl)
        {
            Run("add device launches once without enumerating devices or changing an EQ draft", LaunchSuccessAsync);
            Run("failed system launches wait for dialog ownership and show one actionable alert", LaunchFailureAsync);
            Run("closing cancels a pending launch and suppresses its late failure", LaunchClosingAsync);
            return 9;
        }
        Console.WriteLine("SKIP 3 settings launcher tests: headset control is unavailable on this platform.");
        Run("unsupported platforms disable the add button and cannot launch system settings", UnsupportedLaunchAsync);
        return 7;

        void Run(string name, Func<Task> test)
        {
            session.Dispatch<bool>(async () => { await test(); return true; }, CancellationToken.None)
                .GetAwaiter().GetResult();
            Console.WriteLine("PASS " + name);
        }
    }

    public static void CheckBottomBar(MainView view, Control viewport)
    {
        var bar = view.FindControl<Border>("DeviceBar") ?? throw new InvalidOperationException("Device bar missing.");
        var button = view.FindControl<Button>("AddDeviceButton") ?? throw new InvalidOperationException("Add device button missing.");
        var scroll = DeviceScroll(view);
        CheckInside(bar, viewport, "The device bar is completely inside the viewport.");
        CheckInside(scroll, bar, "The horizontal device viewport is completely inside the device bar.");
        Check(button.IsEffectivelyVisible && button.Bounds.Width > 0 && button.Bounds.Height > 0
            && button.GetVisualAncestors().Contains(scroll), "The add device button is part of the same scrollable device strip.");
        var origin = bar.TranslatePoint(default, view)!.Value;
        Check(Math.Abs(origin.Y + bar.Bounds.Height - view.Bounds.Height) <= 1,
            "The device bar reaches the bottom without an extra status row.");
        Check(!view.GetVisualDescendants().OfType<TextBlock>().Any(text => text.Text == AppVersion.Footer),
            "The removed status footer is absent.");
    }

    internal static bool HasEarbudIcon(Viewbox icon)
    {
        if (!icon.IsEffectivelyVisible || icon.Bounds.Width != 28 || icon.Bounds.Height != 28
            || icon.Child is not FAIconSourceElement { Width: 24, Height: 24 } element)
            return false;
        return (element.IconSource, AppIcons.Source(AppIcon.Earbuds, 24)) switch
        {
            (FAFontIconSource actual, FAFontIconSource expected) => actual.Glyph == expected.Glyph
                && Equals(actual.FontFamily, expected.FontFamily) && actual.FontSize == expected.FontSize,
            (FASymbolIconSource actual, FASymbolIconSource expected) => actual.Symbol == expected.Symbol
                && actual.FontSize == expected.FontSize,
            _ => false
        };
    }

    private static async Task DeviceSelectionAsync()
    {
        var service = new DeviceService();
        var confirmations = new RejectConfirmations();
        await using var vm = new MainViewModel(service, confirmations);
        await vm.InitializeAsync(); await vm.ConnectCommand.ExecuteAsync(null);
        var view = new MainView { DataContext = vm };
        var window = CreateWindow(view);
        try
        {
            window.Show(); Dispatcher.UIThread.RunJobs();
            var picker = view.FindControl<ListBox>("DevicePicker")!;
            Click(window, Card(picker, 1));
            await UntilAsync(() => vm.SelectedDevice?.Identity == service.Devices[1].Identity && vm.CanRefreshDevices);
            Check(Equals(picker.SelectedItem, vm.SelectedDevice) && Card(picker, 1).IsSelected
                && !Card(picker, 0).IsSelected, "Card selection and the active device agree after switching.");
            Check(service.Disconnections == 1 && service.Connections == 1 && !vm.HasVerifiedSnapshot,
                "Switching disconnects the old session and requires an explicit new connection.");
            await vm.ConnectCommand.ExecuteAsync(null);
            vm.EqName = "切换取消后保留"; vm.Bands[0].Gain = 4;
            Click(window, Card(picker, 0));
            await UntilAsync(() => confirmations.Calls == 1 && vm.CanRefreshDevices);
            Check(Equals(picker.SelectedItem, vm.SelectedDevice) && Card(picker, 1).IsSelected
                && !Card(picker, 0).IsSelected, "Cancelling EQ discard restores the original card highlight.");
            Check(vm.SelectedDevice?.Identity == service.Devices[1].Identity && vm.HasVerifiedSnapshot
                && vm.EqDirty && vm.EqName == "切换取消后保留" && vm.Bands[0].Gain == 4
                && service.Disconnections == 1 && service.Submissions == 0,
                "Cancelled switching preserves the current session and EQ draft without a write.");
        }
        finally { window.Close(); }
    }

    private static async Task DeviceLayoutAsync()
    {
        var service = new DeviceService(count: 8, longName: true);
        await using var vm = new MainViewModel(service, new RejectConfirmations());
        await vm.InitializeAsync();
        var view = new MainView { DataContext = vm };
        var window = CreateWindow(view);
        try
        {
            window.Show(); Dispatcher.UIThread.RunJobs();
            foreach (var size in new[] { new Size(1280, 900), new Size(900, 640) })
            {
                window.Width = size.Width; window.Height = size.Height;
                Dispatcher.UIThread.RunJobs();
                var picker = view.FindControl<ListBox>("DevicePicker")!;
                var first = Card(picker, 0); var second = Card(picker, 1);
                var name = first.GetVisualDescendants().OfType<TextBlock>().Single(text => text.Name == "DeviceNameText");
                Check(name.Text == service.Devices[0].Name && first.Bounds.Width == second.Bounds.Width
                    && name.Bounds.Width <= first.Bounds.Width, "A long name stays inside a card of the same width as its neighbours.");
                CheckBottomBar(view, window);
                var add = view.FindControl<Button>("AddDeviceButton")!;
                var before = add.TranslatePoint(default, view)!.Value;
                var scroll = DeviceScroll(view);
                Check(scroll.Extent.Width > scroll.Viewport.Width, "Several device cards have a horizontal scrollable extent.");
                var firstBefore = first.TranslatePoint(default, view)!.Value;
                var previousOffset = scroll.Offset.X;
                scroll.Offset = new Vector(scroll.Extent.Width, 0);
                Dispatcher.UIThread.RunJobs();
                Check(scroll.Offset.X > 0, "Overflowing devices can be scrolled horizontally.");
                var offsetChange = scroll.Offset.X - previousOffset;
                Check(Math.Abs(add.TranslatePoint(default, view)!.Value.X - before.X + offsetChange) <= 1
                    && Math.Abs(first.TranslatePoint(default, view)!.Value.X - firstBefore.X + offsetChange) <= 1,
                    "The add button and device cards move together when the strip scrolls.");
                CheckInside(add, scroll, "Scrolling to the end makes the add button fully visible.");
                var last = Card(picker, service.Devices.Length - 1);
                var lastOrigin = last.TranslatePoint(default, scroll)!.Value;
                Check(lastOrigin.X + last.Bounds.Width <= add.TranslatePoint(default, scroll)!.Value.X,
                    "The add button follows the final device card.");
                CheckBottomBar(view, window);
                scroll.Offset = default;
                Dispatcher.UIThread.RunJobs();
            }
        }
        finally { window.Close(); }
    }

    private static async Task DeviceCenteredLayoutAsync()
    {
        foreach (var count in new[] { 1, 2 })
        {
            await using var vm = new MainViewModel(new DeviceService(count), new RejectConfirmations());
            await vm.InitializeAsync();
            var view = new MainView { DataContext = vm };
            var window = CreateWindow(view);
            try
            {
                window.Show(); Dispatcher.UIThread.RunJobs();
                foreach (var width in new[] { 1280d, 900d })
                {
                    window.Width = width; Dispatcher.UIThread.RunJobs();
                    var picker = view.FindControl<ListBox>("DevicePicker")!;
                    var first = Card(picker, 0);
                    var add = view.FindControl<Button>("AddDeviceButton")!;
                    var scroll = DeviceScroll(view);
                    Check(scroll.Extent.Width <= scroll.Viewport.Width + 1 && Math.Abs(scroll.Offset.X) <= 1,
                        "A short device strip has no horizontal overflow.");
                    var left = first.TranslatePoint(default, scroll)!.Value.X;
                    var right = add.TranslatePoint(default, scroll)!.Value.X + add.Bounds.Width;
                    var presenter = scroll.GetVisualDescendants().OfType<SmoothScroll.Avalonia.Controls.ScrollPresenter>().SingleOrDefault();
                    // These points are relative to ScrollViewer; a centered smooth presenter can have a smaller logical Viewport.
                    // Keep the original visible-region centering requirement and 1 px tolerance in that same coordinate space.
                    Check(Math.Abs((left + right) / 2 - scroll.Bounds.Width / 2) <= 1,
                        $"The entire strip, including the final add button, is centered in its viewport. count={count}, width={width}, scrollBounds={scroll.Bounds}, viewport={scroll.Viewport}, extent={scroll.Extent}, offset={scroll.Offset}, left={left}, right={right}, presenterBounds={presenter?.Bounds}, presenterOffset={presenter?.Offset}, paddingBounds={view.FindControl<Control>("DeviceStripPadding")?.Bounds}");
                    foreach (var index in Enumerable.Range(0, count))
                    {
                        var card = Card(picker, index);
                        var content = card.GetVisualDescendants().OfType<Control>().Single(control => control.Name == "DeviceCardContent");
                        var origin = content.TranslatePoint(default, card)!.Value;
                        Check(content.Bounds.Width < card.Bounds.Width - 32
                            && Math.Abs(origin.X + content.Bounds.Width / 2 - card.Bounds.Width / 2) <= 1
                            && Math.Abs(origin.Y + content.Bounds.Height / 2 - card.Bounds.Height / 2) <= 1,
                            "The earbud icon, name and status form a centered group inside each device card.");
                        var icon = content.GetVisualDescendants().OfType<Viewbox>().Single(control => control.Name == "DeviceIcon");
                        Check(HasEarbudIcon(icon)
                            && !content.GetVisualDescendants().OfType<Image>().Any(),
                            "Every device card uses the settings earbud glyph in its fixed icon slot without a product image.");
                    }
                    CheckBottomBar(view, window);
                }
            }
            finally { window.Close(); }
        }
    }

    private static async Task DeviceStartupCenteredAsync()
    {
        foreach (var index in new[] { 0, 1, 3, 6, 7 })
        foreach (var width in new[] { 1280d, 900d })
        {
            var service = new DeviceService(count: 8);
            await using var vm = new MainViewModel(service, new RejectConfirmations());
            await vm.InitializeAsync();
            await vm.SelectDeviceAsync(service.Devices[index]);
            var view = new MainView { DataContext = vm };
            var window = CreateWindow(view);
            window.Width = width;
            try
            {
                window.Show(); Dispatcher.UIThread.RunJobs();
                var picker = view.FindControl<ListBox>("DevicePicker")!;
                var scroll = DeviceScroll(view);
                var card = Card(picker, index);
                await UntilAsync(() => IsNaturallyPositioned(card, scroll));
                var strip = view.FindControl<StackPanel>("DeviceStrip")!;
                var padding = view.FindControl<Border>("DeviceStripPadding")!;
                Check(padding.Padding == default && Math.Abs(scroll.Extent.Width - strip.Bounds.Width) <= 1,
                    "An overflowing strip has no artificial space before or after its actual contents.");
                CheckInside(card, scroll, "The selected startup card is completely visible at its natural scroll position.");
                if (index == 0)
                    Check(Math.Abs(scroll.Offset.X) <= 1, "The first startup device stops at the strip's left boundary.");
                else if (index == service.Devices.Length - 1)
                {
                    Check(Math.Abs(scroll.Offset.X - scroll.Extent.Width + scroll.Viewport.Width) <= 1,
                        "The last startup device stops at the strip's right boundary.");
                    CheckInside(view.FindControl<Button>("AddDeviceButton")!, scroll,
                        "The trailing add button is completely visible when the last device is selected at startup.");
                }
                else if (index == 3)
                    Check(IsCentered(card, scroll), "A middle startup device is centered when neither boundary limits it.");
                if (index == 1 && width == 1280)
                    Check(Math.Abs(scroll.Offset.X) <= 1,
                        "A device close to the left edge stays at the left boundary instead of adding blank space.");
                if (index == 6 && width == 1280)
                    Check(Math.Abs(scroll.Offset.X - scroll.Extent.Width + scroll.Viewport.Width) <= 1,
                        "A device close to the right edge stays at the right boundary instead of adding blank space.");
                Check(Card(picker, index).IsSelected && Equals(picker.SelectedItem, vm.SelectedDevice),
                    "The naturally positioned startup card is the view model's selected device.");
                Check(service.Enumerations == 1 && service.Connections == 0 && service.Submissions == 0,
                    "Startup centering performs no extra enumeration, connection or settings write.");
            }
            finally { window.Close(); }
        }
    }

    private static async Task DeviceCenteringInteractionsAsync()
    {
        var service = new DeviceService(count: 8);
        await using var vm = new MainViewModel(service, new RejectConfirmations());
        await vm.InitializeAsync();
        var view = new MainView { DataContext = vm };
        var window = CreateWindow(view);
        try
        {
            window.Show(); Dispatcher.UIThread.RunJobs();
            var picker = view.FindControl<ListBox>("DevicePicker")!;
            var scroll = DeviceScroll(view);
            await UntilAsync(() => IsNaturallyPositioned(Card(picker, 0), scroll));
            await vm.SelectDeviceAsync(service.Devices[5]);
            await UntilAsync(() => IsCentered(Card(picker, 5), scroll));
            foreach (var width in new[] { 900d, 1280d })
            {
                window.Width = width; Dispatcher.UIThread.RunJobs();
                await UntilAsync(() => IsCentered(Card(picker, 5), scroll));
            }
            var wheelPoint = Card(picker, 5).TranslatePoint(new Point(150, 32), window)!.Value;
            var beforeWheel = scroll.Offset.X;
            window.MouseWheel(wheelPoint, new Vector(0, -1), RawInputModifiers.None);
            Dispatcher.UIThread.RunJobs();
            await UntilAsync(() => scroll.Offset.X > beforeWheel);
            Check(scroll.Offset.X > beforeWheel,
                "A vertical wheel gesture over the device strip scrolls toward later devices.");
            // Finish this gesture before the independent manual-layout scenario below.
            await UntilScrollSettlesAsync(window, scroll);
            var maximum = scroll.Extent.Width - scroll.Viewport.Width;
            scroll.Offset = new Vector(maximum / 2, 0); Dispatcher.UIThread.RunJobs();
            await UntilScrollSettlesAsync(window, scroll);
            var beforeFractional = scroll.Offset.X;
            // Reuse the card hit point already exercised by the whole-notch gesture above.
            var fractionalPoint = wheelPoint;
            int fractionalRouted = 0; bool fractionalHandled = false; Vector routedDelta = default; double offsetAtRoute = -1;
            void ObserveFractional(object? sender, PointerWheelEventArgs args)
            { fractionalRouted++; fractionalHandled = args.Handled; routedDelta = args.Delta; offsetAtRoute = scroll.Offset.X; }
            scroll.AddHandler(InputElement.PointerWheelChangedEvent, ObserveFractional, RoutingStrategies.Tunnel, handledEventsToo: true);
            var fractionalTarget = Card(picker, 4);
            var fractionalArgs = new PointerWheelEventArgs(fractionalTarget, new Pointer(1, PointerType.Mouse, true),
                window, fractionalPoint, (ulong)Environment.TickCount64,
                new PointerPointProperties(RawInputModifiers.None, PointerUpdateKind.Other), KeyModifiers.None, new Vector(0, -.5));
            try
            {
                // The raw half-notch did not route in either headless theme. Exercise the real card's routed handler
                // contract directly; this verifies axis mapping, not a physical precision-touchpad input stack.
                fractionalTarget.RaiseEvent(fractionalArgs);
                Dispatcher.UIThread.RunJobs();
            }
            finally { scroll.RemoveHandler(InputElement.PointerWheelChangedEvent, ObserveFractional); }
            await UntilScrollSettlesAsync(window, scroll);
            Check(fractionalRouted == 1 && fractionalArgs.Handled && routedDelta.Y == -.5
                && Math.Abs(scroll.Offset.X - beforeFractional - 24) <= 1,
                $"A routed fractional Y wheel uses the library's 48 px horizontal mapping without losing the input. before={beforeFractional}, after={scroll.Offset.X}, maximum={maximum}, routed={fractionalRouted}, handled={fractionalArgs.Handled}, handledAtObserver={fractionalHandled}, delta={routedDelta}, atRoute={offsetAtRoute}, point={fractionalPoint}");
            scroll.Offset = new Vector((scroll.Extent.Width - scroll.Viewport.Width) / 4, 0);
            Dispatcher.UIThread.RunJobs();
            var manualOffset = scroll.Offset.X;
            Check(!IsCentered(Card(picker, 5), scroll), "A manual scroll can move the selected card away from the center.");
            view.InvalidateMeasure(); Dispatcher.UIThread.RunJobs();
            await Task.Delay(30); Dispatcher.UIThread.RunJobs();
            Check(Math.Abs(scroll.Offset.X - manualOffset) <= 1,
                "An ordinary layout pass preserves the user's manual horizontal scroll position.");
            await vm.SelectDeviceAsync(service.Devices[7]);
            await UntilAsync(() => IsNaturallyPositioned(Card(picker, 7), scroll));
            CheckInside(view.FindControl<Button>("AddDeviceButton")!, scroll,
                "Selecting the last device also makes the trailing add button completely visible.");
            Check(service.Enumerations == 1 && service.Connections == 0 && service.Submissions == 0,
                "Selection, scrolling and resizing do not enumerate or connect devices or submit settings.");
        }
        finally { window.Close(); }
        await DeviceOverflowResizeAsync();
    }

    private static async Task DeviceOverflowResizeAsync()
    {
        var service = new DeviceService(count: 3);
        await using var vm = new MainViewModel(service, new RejectConfirmations());
        await vm.InitializeAsync();
        await vm.SelectDeviceAsync(service.Devices[2]);
        var view = new MainView { DataContext = vm };
        var window = CreateWindow(view);
        window.Width = 1280;
        try
        {
            window.Show(); Dispatcher.UIThread.RunJobs();
            var picker = view.FindControl<ListBox>("DevicePicker")!;
            var scroll = DeviceScroll(view);
            var add = view.FindControl<Button>("AddDeviceButton")!;
            foreach (var width in new[] { 1280d, 900d, 1280d, 900d })
            {
                window.Width = width; Dispatcher.UIThread.RunJobs();
                if (width == 900)
                {
                    await UntilAsync(() => scroll.Extent.Width > scroll.Viewport.Width
                        && IsNaturallyPositioned(Card(picker, 2), scroll));
                    Check(Math.Abs(scroll.Offset.X - scroll.Extent.Width + scroll.Viewport.Width) <= 1,
                        "Shrinking the window restores the last device's natural right boundary.");
                }
                else
                {
                    await UntilAsync(() => scroll.Extent.Width <= scroll.Viewport.Width + 1
                        && Math.Abs(scroll.Offset.X) <= 1);
                    var left = Card(picker, 0).TranslatePoint(default, scroll)!.Value.X;
                    var right = add.TranslatePoint(default, scroll)!.Value.X + add.Bounds.Width;
                    // The original smooth-layout regression centered at 492 instead of 620 in the 1280 px window.
                    // The corrected presenter centers at 620; compare visible viewer bounds, not its narrower logical Viewport.
                    if (Math.Abs((left + right) / 2 - scroll.Bounds.Width / 2) > 1)
                    {
                        var padding = view.FindControl<Control>("DeviceStripPadding")!;
                        var strip = view.FindControl<Control>("DeviceStrip")!;
                        var presenter = scroll.GetVisualDescendants().OfType<SmoothScroll.Avalonia.Controls.ScrollPresenter>().SingleOrDefault();
                        Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { phase = "device-strip-resize-before-wait", width,
                            viewport = scroll.Viewport.Width, extent = scroll.Extent.Width, offset = scroll.Offset.X, left, right,
                            paddingBounds = padding.Bounds.ToString(), paddingAlignment = padding.HorizontalAlignment.ToString(),
                            stripBounds = strip.Bounds.ToString(), presenterBounds = presenter?.Bounds.ToString(),
                            presenterAlignment = presenter?.HorizontalAlignment.ToString(),
                            contentAlignment = presenter?.HorizontalContentAlignment.ToString(), presenterOffset = presenter?.Offset.ToString() }));
                        await UntilAsync(() =>
                        {
                            left = Card(picker, 0).TranslatePoint(default, scroll)!.Value.X;
                            right = add.TranslatePoint(default, scroll)!.Value.X + add.Bounds.Width;
                            return Math.Abs((left + right) / 2 - scroll.Bounds.Width / 2) <= 1;
                        });
                    }
                    Check(Math.Abs((left + right) / 2 - scroll.Bounds.Width / 2) <= 1,
                        $"Growing an overflowing strip to fit centers all devices together with the add button. width={width}, visibleWidth={scroll.Bounds.Width}, viewport={scroll.Viewport.Width}, extent={scroll.Extent.Width}, offset={scroll.Offset.X}, left={left}, right={right}");
                }
                CheckInside(Card(picker, 2), scroll, "The selected last device remains fully visible across overflow changes.");
                CheckInside(add, scroll, "The trailing add button remains fully visible across overflow changes.");
            }
            Check(service.Enumerations == 1 && service.Connections == 0 && service.Submissions == 0,
                "Crossing the overflow boundary changes layout without enumeration, connection or writes.");
        }
        finally { window.Close(); }
    }

    private static async Task DeviceThemeAsync()
    {
        var theme = Application.Current!.Styles.OfType<FluentAvaloniaTheme>().Single();
        var previousAccent = theme.CustomAccentColor;
        var previousVariant = Application.Current.RequestedThemeVariant;
        await using var vm = new MainViewModel(new DeviceService(), new RejectConfirmations());
        await vm.InitializeAsync();
        var view = new MainView { DataContext = vm };
        var window = CreateWindow(view);
        try
        {
            window.Show(); Dispatcher.UIThread.RunJobs();
            foreach (var dark in new[] { false, true })
            {
                Application.Current.RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light;
                theme.CustomAccentColor = Color.FromRgb(0, 120, 212);
                Dispatcher.UIThread.RunJobs();
                var picker = view.FindControl<ListBox>("DevicePicker")!;
                Check(picker.GetVisualDescendants().OfType<Viewbox>()
                    .Where(icon => icon.Name == "DeviceIcon").All(HasEarbudIcon),
                    "Device cards retain the settings earbud glyph in both themes.");
                var selected = BorderColor(Card(picker, 0));
                var other = BorderColor(Card(picker, 1));
                Check(selected != other, "The selected device has a distinct border in each theme.");
                theme.CustomAccentColor = Color.FromRgb(190, 25, 112);
                Dispatcher.UIThread.RunJobs();
                Check(BorderColor(Card(picker, 0)) != selected && BorderColor(Card(picker, 1)) == other,
                    "Changing the system accent updates the selected border and preserves neutral unselected borders.");
            }
        }
        finally
        {
            window.Close();
            theme.CustomAccentColor = previousAccent;
            Application.Current.RequestedThemeVariant = previousVariant;
            Dispatcher.UIThread.RunJobs();
        }
    }

    private static async Task LaunchSuccessAsync()
    {
        var service = new DeviceService(count: 8);
        var confirmations = new RejectConfirmations();
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var launcher = new FakeLauncher(_ => release.Task);
        await using var vm = new MainViewModel(service, confirmations);
        await vm.InitializeAsync(); await vm.ConnectCommand.ExecuteAsync(null);
        vm.EqName = "打开设置后保留"; vm.Bands[0].Gain = 4;
        var view = new MainView { DataContext = vm, LauncherOverride = launcher };
        var window = CreateWindow(view);
        try
        {
            window.Show(); Dispatcher.UIThread.RunJobs();
            var button = view.FindControl<Button>("AddDeviceButton")!;
            var scroll = DeviceScroll(view);
            scroll.Offset = new Vector(scroll.Extent.Width, 0); Dispatcher.UIThread.RunJobs();
            CheckInside(button, scroll, "The trailing add button can be reached before it is activated.");
            Click(window, button);
            var expectedUri = OperatingSystem.IsMacOS()
                ? "x-apple.systempreferences:com.apple.BluetoothSettings" : "ms-settings:bluetooth";
            Check(launcher.Uris.Count == 1 && launcher.Uris[0].OriginalString == expectedUri,
                "The add button requests the documented Bluetooth settings URI.");
            Check(!view.FindControl<Border>("AddDeviceHost")!.IsEnabled, "A pending launch disables the add device control.");
            await view.OpenBluetoothSettingsAsync();
            Click(window, button);
            Check(launcher.Uris.Count == 1, "Repeated activation does not start another pending launch.");
            release.TrySetResult(true);
            await UntilAsync(() => view.FindControl<Border>("AddDeviceHost")!.IsEnabled);
            window.Hide(); window.Show(); window.Activate(); Dispatcher.UIThread.RunJobs();
            Check(service.Enumerations == 1 && service.Connections == 1 && service.Disconnections == 0
                && service.Submissions == 0 && confirmations.Calls == 0 && vm.HasVerifiedSnapshot
                && vm.SelectedDevice?.Identity == service.Devices[0].Identity
                && vm.EqDirty && vm.EqName == "打开设置后保留" && vm.Bands[0].Gain == 4,
                "Launching settings and reactivating the app preserve selection, connection and draft without enumeration or writes.");
            Check(!window.GetVisualDescendants().OfType<FAContentDialog>().Any(), "A successful launch needs no alert.");
        }
        finally
        {
            release.TrySetResult(true);
            await view.DismissSettingsDialogAsync();
            window.Close();
        }
    }

    private static async Task UnsupportedLaunchAsync()
    {
        var launcher = new FakeLauncher(_ => Task.FromResult(true));
        var service = new DeviceService();
        await using var vm = new MainViewModel(service, new RejectConfirmations());
        await vm.InitializeAsync();
        var view = new MainView { DataContext = vm, LauncherOverride = launcher };
        var window = CreateWindow(view);
        try
        {
            window.Show(); Dispatcher.UIThread.RunJobs();
            var button = view.FindControl<Button>("AddDeviceButton")!;
            Check(!view.FindControl<Border>("AddDeviceHost")!.IsEnabled && !button.IsEffectivelyEnabled,
                "The settings entry is disabled on unsupported platforms.");
            Click(window, button);
            await view.OpenBluetoothSettingsAsync();
            Check(launcher.Uris.Count == 0 && service.Enumerations == 1 && service.Submissions == 0,
                "Both pointer activation and direct entry leave the launcher and headset service untouched.");
            Check(!window.GetVisualDescendants().OfType<FAContentDialog>().Any(),
                "A disabled platform entry cannot open a failure dialog.");
        }
        finally { window.Close(); }
    }

    private static async Task LaunchFailureAsync()
    {
        foreach (var throwException in new[] { false, true })
        {
            var launcher = new FakeLauncher(_ => throwException
                ? Task.FromException<bool>(new InvalidOperationException("test-launch-failure")) : Task.FromResult(false));
            await using var vm = new MainViewModel(new DeviceService(), new RejectConfirmations());
            await vm.InitializeAsync();
            var view = new MainView { DataContext = vm, LauncherOverride = launcher };
            var window = CreateWindow(view);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Task? owner = null; Task? opening = null;
            try
            {
                window.Show(); Dispatcher.UIThread.RunJobs();
                owner = vm.Dialogs.RunAsync(token => release.Task.WaitAsync(token));
                opening = view.OpenBluetoothSettingsAsync();
                Dispatcher.UIThread.RunJobs();
                Check(!opening.IsCompleted && !window.GetVisualDescendants().OfType<FAContentDialog>().Any(),
                    "A failed launch waits for the current dialog owner before showing an alert.");
                await view.OpenBluetoothSettingsAsync();
                Check(launcher.Uris.Count == 1, "Repeated activation cannot queue duplicate launch failure dialogs.");
                release.TrySetResult(); await owner;
                await UntilAsync(() => window.GetVisualDescendants().OfType<FAContentDialog>()
                    .Any(dialog => Equals(dialog.Title, "无法打开蓝牙设置")));
                var dialog = window.GetVisualDescendants().OfType<FAContentDialog>().Single();
                Check(dialog.CloseButtonText == "确定" && string.IsNullOrEmpty(dialog.PrimaryButtonText)
                    && string.IsNullOrEmpty(dialog.SecondaryButtonText) && !opening.IsCompleted,
                    "Both a rejected URI and a launcher exception produce one acknowledgement alert.");
                Check(dialog.Content is string help && help.Contains(OperatingSystem.IsMacOS() ? "macOS 系统设置" : "Windows 设置"),
                    "Launch failures explain how to open the current platform's Bluetooth settings.");
                await view.DismissSettingsDialogAsync().WaitAsync(Deadline);
                await opening.WaitAsync(Deadline);
            }
            finally
            {
                release.TrySetResult();
                await view.DismissSettingsDialogAsync();
                if (owner is not null) await owner;
                if (opening is not null) await opening;
                window.Close();
            }
        }
    }

    private static async Task LaunchClosingAsync()
    {
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var launcher = new FakeLauncher(_ => release.Task);
        await using var vm = new MainViewModel(new DeviceService(), new RejectConfirmations());
        await vm.InitializeAsync();
        var view = new MainView { DataContext = vm, LauncherOverride = launcher };
        var window = CreateWindow(view);
        Task? opening = null;
        try
        {
            window.Show(); Dispatcher.UIThread.RunJobs();
            opening = view.OpenBluetoothSettingsAsync();
            Check(launcher.Uris.Count == 1 && !opening.IsCompleted, "The fake system launch is pending.");
            await view.DismissSettingsDialogAsync().WaitAsync(Deadline);
            await view.OpenBluetoothSettingsAsync();
            Check(launcher.Uris.Count == 1, "Window shutdown blocks another system launch.");
            release.TrySetResult(false);
            await opening.WaitAsync(Deadline);
            await Task.Delay(20); Dispatcher.UIThread.RunJobs();
            Check(!window.GetVisualDescendants().OfType<FAContentDialog>().Any(),
                "Late launch failure cannot open a dialog after shutdown begins.");
        }
        finally
        {
            release.TrySetResult(true);
            await view.DismissSettingsDialogAsync();
            if (opening is not null) await opening;
            window.Close();
        }
    }

    private static Color BorderColor(ListBoxItem card) => card.BorderBrush is ISolidColorBrush brush
        ? brush.Color : throw new InvalidOperationException("Device card border has no solid theme brush.");

    private static ListBoxItem Card(ListBox picker, int index) => picker.ContainerFromIndex(index) as ListBoxItem
        ?? throw new InvalidOperationException("Device card was not realized.");

    private static ScrollViewer DeviceScroll(MainView view) => view.FindControl<ScrollViewer>("DeviceBarScroll")
        ?? throw new InvalidOperationException("Shared device bar scroll viewer missing.");

    private static bool IsCentered(Control card, ScrollViewer scroll)
    {
        var origin = card.TranslatePoint(default, scroll);
        return origin is { } point && Math.Abs(point.X + card.Bounds.Width / 2 - scroll.Viewport.Width / 2) <= 1;
    }

    private static bool IsNaturallyPositioned(Control card, ScrollViewer scroll)
    {
        var origin = card.TranslatePoint(default, scroll);
        if (origin is not { } point || card.Bounds.Width <= 0) return false;
        var contentCenter = point.X + scroll.Offset.X + card.Bounds.Width / 2;
        var maximum = Math.Max(0, scroll.Extent.Width - scroll.Viewport.Width);
        var expectedOffset = Math.Clamp(contentCenter - scroll.Viewport.Width / 2, 0, maximum);
        return Math.Abs(scroll.Offset.X - expectedOffset) <= 1;
    }

    private static Window CreateWindow(MainView view) => new()
        { Width = 1280, Height = 900, Content = view, WindowDecorations = WindowDecorations.None };

    private static void Click(Window window, Control control)
    {
        var point = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;
        window.MouseDown(point, MouseButton.Left); window.MouseUp(point, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
    }

    private static void CheckInside(Control control, Control viewport, string message)
    {
        var origin = control.TranslatePoint(default, viewport) ?? throw new InvalidOperationException("Control is detached.");
        Check(control.IsEffectivelyVisible && control.Bounds.Width > 0 && control.Bounds.Height > 0
            && origin.X >= -1 && origin.Y >= -1
            && origin.X + control.Bounds.Width <= viewport.Bounds.Width + 1
            && origin.Y + control.Bounds.Height <= viewport.Bounds.Height + 1, message);
    }

    private static async Task UntilScrollSettlesAsync(Window window, ScrollViewer scroll)
    {
        var deadline = DateTime.UtcNow + Deadline;
        var began = DateTime.UtcNow;
        var previous = scroll.Offset;
        int stable = 0;
        while (DateTime.UtcNow < deadline)
        {
            using (var frame = window.CaptureRenderedFrame()) { }
            await Task.Delay(20); Dispatcher.UIThread.RunJobs();
            var current = scroll.Offset;
            stable = (current - previous).Length < .01 ? stable + 1 : 0;
            previous = current;
            if (stable >= 8 && DateTime.UtcNow - began >= TimeSpan.FromMilliseconds(180)) return;
        }
        throw new TimeoutException("The device bar wheel gesture did not settle.");
    }

    private static async Task UntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Deadline;
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline) throw new TimeoutException("Device bar interaction did not finish.");
            await Task.Delay(10); Dispatcher.UIThread.RunJobs();
        }
        Dispatcher.UIThread.RunJobs();
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class RejectConfirmations : IConfirmationService
    {
        public int Calls { get; private set; }
        public Task<bool> ConfirmAsync(string title, string message, CancellationToken token)
        { Calls++; return Task.FromResult(false); }
    }

    private sealed class FakeLauncher(Func<Uri, Task<bool>> launch) : ILauncher
    {
        public List<Uri> Uris { get; } = [];
        public Task<bool> LaunchUriAsync(Uri uri) { Uris.Add(uri); return launch(uri); }
        public Task<bool> LaunchFileAsync(IStorageItem file) => throw new InvalidOperationException("No file launch expected.");
    }

    private sealed class DeviceService : IHeadsetService
    {
        private readonly PreviewService _inner = new();
        private string? _identity;
        public DeviceService(int count = 2, bool longName = false) => Devices = Enumerable.Range(0, count)
            .Select(index => new UiDevice("card-device-" + index,
                index == 0 && longName ? string.Concat(Enumerable.Repeat("名称很长的已配对耳机", 12)) : "耳机 " + (index + 1), true)).ToArray();
        public UiDevice[] Devices { get; }
        public int Enumerations { get; private set; }
        public int Connections { get; private set; }
        public int Disconnections { get; private set; }
        public int Submissions { get; private set; }
        public Task<IReadOnlyList<UiDevice>> EnumerateAsync(CancellationToken token)
        { Enumerations++; return Task.FromResult<IReadOnlyList<UiDevice>>(Devices); }
        public async Task<UiSnapshot> ConnectAsync(string identity, CancellationToken token)
        {
            Connections++; _identity = identity;
            return Adjust(await _inner.ConnectAsync(identity, token));
        }
        public Task DisconnectAsync(CancellationToken token) { Disconnections++; return Task.CompletedTask; }
        public async Task<UiSnapshot> RefreshAsync(CancellationToken token) => Adjust(await _inner.RefreshAsync(token));
        private UiSnapshot Adjust(UiSnapshot snapshot)
        {
            snapshot.Identity = _identity ?? Devices[0].Identity;
            snapshot.DeviceName = Devices.Single(device => device.Identity == snapshot.Identity).Name;
            return snapshot;
        }
        public Task<UiApplyResult> ApplyAsync(UiAction action, CancellationToken token)
        { Submissions++; throw new InvalidOperationException("Device bar tests must not submit settings."); }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
