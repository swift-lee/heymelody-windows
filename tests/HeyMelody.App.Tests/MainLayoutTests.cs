using System.Security.Cryptography;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using FluentAvalonia.UI.Controls;
using HeyMelodyWindows.App;

internal static class MainLayoutTests
{
    internal static int Run(HeadlessUnitTestSession session, string output, List<object> receipts)
    {
        Run("real-device layout keeps ANC visible and other panels collapsed", () => LayoutAsync(false));
        Run("demo layout uses the same expanded ANC default and eight-pixel spacing", () => LayoutAsync(true));
        Run("device information copies current fields and handles pending, failed and stale requests", CopyAsync);
        Run("pinned header keeps its status dot and activity slot stable during reads and writes", HeaderActivityAsync);
        return 4 + EqSliderGeometryTests.Run(session);

        void Run(string name, Func<Task> action)
        {
            session.Dispatch<bool>(async () => { await action(); return true; }, CancellationToken.None).GetAwaiter().GetResult();
            Console.WriteLine("PASS " + name);
        }

        async Task LayoutAsync(bool demo)
        {
            var oldTheme = Application.Current!.RequestedThemeVariant;
            try
            {
                foreach (var dark in new[] { false, true })
                foreach (var size in new[] { new Size(1280, 900), new Size(900, 640) })
                {
                    Application.Current.RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light;
                    await using var vm = new MainViewModel(new ChargingPreviewService(), new NoConfirm(), preview: true);
                    await vm.InitializeAsync();
                    if (demo) await vm.AddDemoDeviceAsync();
                    var view = new MainView { DataContext = vm };
                    var window = new Window { Content = view, Width = size.Width, Height = size.Height,
                        WindowDecorations = WindowDecorations.None };
                    try
                    {
                        window.Show(); Dispatcher.UIThread.RunJobs();
                        var hero = view.FindControl<Grid>("HeroPanel")!;
                        Check(hero.Children.Count == 1 && hero.Children[0] == view.FindControl<ProductArtwork>("HeroArt")
                            && view.FindControl<TextBlock>("HeroCaption") is null,
                            "Product artwork occupies its own panel without repeated title or caption rows.");
                        var anc = view.FindControl<FASettingsExpander>("AncPanel")!;
                        var selector = anc.GetVisualDescendants().OfType<AncModeSelector>().Single();
                        Check(anc.IsVisible && anc.IsExpanded && Equals(anc.Header, "降噪") && selector.IsVisible
                            && selector.Bounds.Width > 0 && selector.Bounds.Height > 0
                            && anc.IconSource is not null && anc.Description == "调整你听见的环境声音",
                            "ANC starts expanded with its header icon, description and functional selector.");
                        Check(view.GetVisualDescendants().OfType<FASettingsExpander>().Any()
                            && view.GetVisualDescendants().OfType<FASettingsExpander>()
                                .Where(expander => !ReferenceEquals(expander, anc)).All(expander => !expander.IsExpanded)
                            && !view.FindControl<FASettingsExpander>("OperationLogPanel")!.IsExpanded,
                            "All optional main-interface groups start collapsed, including demo groups and the operation log.");
                        CheckHeader(view, vm, window);
                        CheckIcons(view);
                        if (!demo)
                        {
                            var charging = view.FindControl<Border>("BatteryPanel")!.GetVisualDescendants()
                                .OfType<FAIconSourceElement>().Single(icon => icon.GetValue(AutomationProperties.NameProperty) == "左耳正在充电");
                            Check(charging.IsVisible && view.FindControl<FAIconSourceElement>("LeftBatteryDeviceIcon")!.IsVisible,
                                $"The device shape and charging bolt remain visible together (charging={charging.IsVisible}, value={vm.LeftCharging}).");
                            Console.WriteLine($"Battery charging {view.ActualThemeVariant}: source={charging.IconSource?.GetType().Name}, "
                                + $"glyph={(charging.IconSource is FAFontIconSource font ? $"U+{char.ConvertToUtf32(font.Glyph!, 0):X4}" : "none")}, foreground={charging.Foreground}");
                            Check(charging.IconSource is FAFontIconSource { Glyph: "\uE945" },
                                "The charging marker retains the existing lightning glyph.");
                            var expected = view.FindResource(view.ActualThemeVariant, "SystemFillColorSuccessBrush");
                            Check(charging.Foreground is ISolidColorBrush actual && expected is ISolidColorBrush success
                                && actual.Color == success.Color,
                                $"The charging bolt retains its success colour (actual={charging.Foreground}, expected={expected}).");
                        }
                        var fit = view.FindControl<FASettingsExpander>("FitTestPanel")!;
                        var hearing = view.FindControl<FASettingsExpander>("HearingPanel")!;
                        var information = view.FindControl<FASettingsExpander>("DeviceInfoPanel")!;
                        var scroll = view.FindControl<ScrollViewer>("ControlScroll")!;
                        var fitTop = fit.TranslatePoint(default, scroll)!.Value.Y;
                        var informationTop = information.TranslatePoint(default, scroll)!.Value.Y;
                        var hearingTop = hearing.TranslatePoint(default, scroll)!.Value.Y;
                        var groups = new[] { "SoundGroupHeader", "HearingGroupHeader", "DeviceGroupHeader" }
                            .Select(name => view.FindControl<TextBlock>(name)!).ToArray();
                        var groupTops = groups.Select(header => header.TranslatePoint(default, scroll)!.Value.Y).ToArray();
                        Check(Math.Abs(fitTop - hearingTop - hearing.Bounds.Height - 8) < .6
                            && fit.Items.Count == 0 && fit.Footer is Button && fit.IconSource is not null
                            && groups.Select(header => header.Text).SequenceEqual(["声音", "听感与佩戴", "设备"])
                            && groupTops[0] < view.FindControl<FASettingsExpander>("AncPanel")!.TranslatePoint(default, scroll)!.Value.Y
                            && groupTops[1] < hearingTop && groupTops[2] > fitTop + fit.Bounds.Height && informationTop > groupTops[2],
                            "Settings are grouped as sound, hearing and fit, then device; the fit card sits eight pixels below Golden Sound.");
                        var copy = view.FindControl<Button>("CopyDeviceInfoButton")!;
                        var copyContent = (StackPanel)copy.Content!;
                        Check(copy.IsEnabled && information.Footer is null && !information.IsExpanded
                            && copyContent.Children[0] is FAIconSourceElement { IconSource: FAFontIconSource { Glyph: "\uE8C8" } }
                            && copyContent.Children[1] is TextBlock { Text: "复制" },
                            "Copy keeps its icon and text in the expanded information content, not in the header.");
                        var picker = view.FindControl<ListBox>("DevicePicker")!;
                        var cards = picker.GetVisualDescendants().OfType<Grid>().Where(grid => grid.Name == "DeviceCardContent").ToArray();
                        Check(cards.Length > 0 && cards.All(card => card.ColumnDefinitions.Count == 2
                                && card.GetVisualDescendants().OfType<Viewbox>().Any(icon => icon.Name == "DeviceIcon"
                                    && DeviceBarTests.HasEarbudIcon(icon)))
                            && !picker.GetVisualDescendants().OfType<Image>().Any(),
                            "Real and demo cards share the settings earbud glyph without product images.");
                        Check(view.IsCompact == (size.Width < 1080), "The simplified hero respects the responsive breakpoint.");

                        using var frame = window.CaptureRenderedFrame() ?? throw new InvalidOperationException("Missing main-layout pixels.");
                        var batteryPanel = view.FindControl<Border>("BatteryPanel")!;
                        var batteryOrigin = batteryPanel.TranslatePoint(default, window)!.Value;
                        var name = $"main-collapsed-{(demo ? "demo" : "real")}-{(dark ? "dark" : "light")}-{(int)size.Width}x{(int)size.Height}.png";
                        var path = Path.Combine(output, name);
                        using (var stream = new FileStream(path, FileMode.CreateNew)) frame.Save(stream, Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
                        receipts.Add(new { scenario = "main-collapsed", demo, theme = dark ? "dark" : "light",
                            width = size.Width, height = size.Height, file = name,
                            battery = new { x = batteryOrigin.X, y = batteryOrigin.Y,
                                width = batteryPanel.Bounds.Width, height = batteryPanel.Bounds.Height },
                            sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant() });

                        // Keep UI review limited to the changed device card. These raster scales
                        // exercise glyph scaling; they do not claim physical monitor DPI coverage.
                        foreach (var scale in new[] { 1d, 1.5d, 2d })
                        {
                            var card = cards[0];
                            var origin = card.TranslatePoint(default, window)
                                ?? throw new InvalidOperationException("Device card is detached from the captured window.");
                            using var fullWindow = new Avalonia.Media.Imaging.RenderTargetBitmap(
                                new PixelSize((int)Math.Ceiling(window.Bounds.Width * scale),
                                    (int)Math.Ceiling(window.Bounds.Height * scale)), new Vector(96 * scale, 96 * scale));
                            fullWindow.Render(window);
                            // Crop the composed window so the card keeps its real background,
                            // inherited foreground and its translated position at each scale.
                            var left = Math.Max(0, (int)Math.Floor(origin.X * scale));
                            var top = Math.Max(0, (int)Math.Floor(origin.Y * scale));
                            var right = Math.Min(fullWindow.PixelSize.Width, (int)Math.Ceiling((origin.X + card.Bounds.Width) * scale));
                            var bottom = Math.Min(fullWindow.PixelSize.Height, (int)Math.Ceiling((origin.Y + card.Bounds.Height) * scale));
                            var pixels = new PixelSize(right - left, bottom - top);
                            using var crop = new Avalonia.Media.Imaging.WriteableBitmap(pixels, fullWindow.Dpi,
                                fullWindow.Format, fullWindow.AlphaFormat);
                            using (var buffer = crop.Lock())
                                fullWindow.CopyPixels(new PixelRect(left, top, pixels.Width, pixels.Height),
                                    buffer.Address, checked(buffer.RowBytes * pixels.Height), buffer.RowBytes);
                            var cropName = $"device-card-{(demo ? "demo" : "real")}-{(dark ? "dark" : "light")}-{(int)size.Width}-{(int)(scale * 100)}pct.png";
                            var cropPath = Path.Combine(output, cropName);
                            using (var stream = new FileStream(cropPath, FileMode.CreateNew))
                                crop.Save(stream, Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
                            receipts.Add(new { scenario = "device-earbud-crop", demo, theme = dark ? "dark" : "light",
                                rasterScale = scale, width = pixels.Width, height = pixels.Height, file = cropName,
                                scope = "headless glyph raster scaling; no physical monitor DPI assertion",
                                sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(cropPath))).ToLowerInvariant() });
                        }

                        // Inspect the controls below the initial viewport without opening
                        // any panel. Center the adjacent fit-test and information cards
                        // and verify that copying is unavailable until information is expanded.
                        information.BringIntoView(); Dispatcher.UIThread.RunJobs();
                        AvaloniaHeadlessPlatform.ForceRenderTimerTick(); Dispatcher.UIThread.RunJobs();
                        fitTop = fit.TranslatePoint(default, scroll)!.Value.Y;
                        informationTop = information.TranslatePoint(default, scroll)!.Value.Y;
                        var groupHeight = informationTop + information.Bounds.Height - fitTop;
                        var desiredOffset = scroll.Offset.Y + fitTop - Math.Max(0, (scroll.Viewport.Height - groupHeight) / 2);
                        scroll.Offset = new Vector(scroll.Offset.X,
                            Math.Clamp(desiredOffset, 0, Math.Max(0, scroll.Extent.Height - scroll.Viewport.Height)));
                        AvaloniaHeadlessPlatform.ForceRenderTimerTick(); Dispatcher.UIThread.RunJobs();
                        await Task.Delay(20);
                        AvaloniaHeadlessPlatform.ForceRenderTimerTick(); Dispatcher.UIThread.RunJobs();
                        fitTop = fit.TranslatePoint(default, scroll)!.Value.Y;
                        informationTop = information.TranslatePoint(default, scroll)!.Value.Y;
                        Check(fitTop >= -.6 && informationTop + information.Bounds.Height <= scroll.Viewport.Height + .6
                            && !information.IsExpanded
                            && !CanHit(window, copy)
                            && view.GetVisualDescendants().OfType<FASettingsExpander>()
                                .Where(expander => !ReferenceEquals(expander, anc)).All(expander => !expander.IsExpanded),
                            "The collapsed information card remains beside fit-test with no clickable Copy action.");
                        await ExpandInformationAsync(information);
                        information.BringIntoView(); Dispatcher.UIThread.RunJobs();
                        await MainUiTestLayout.ClickPointAsync(window, copy, requireFullVisibility: true);
                        AvaloniaHeadlessPlatform.ForceRenderTimerTick(); Dispatcher.UIThread.RunJobs();
                        fitTop = fit.TranslatePoint(default, scroll)!.Value.Y;
                        informationTop = information.TranslatePoint(default, scroll)!.Value.Y;
                        var copyTop = copy.TranslatePoint(default, scroll)!.Value.Y;
                        var fields = view.FindControl<Grid>("DeviceInfoFields")!;
                        var fieldsTop = fields.TranslatePoint(default, information)!.Value.Y;
                        var copyInInformation = copy.TranslatePoint(default, information)!.Value.Y;
                        Check(information.IsExpanded && CanHit(window, copy) && copy.Bounds.Width > 0 && copy.Bounds.Height > 0
                            && copyInInformation >= fieldsTop + fields.Bounds.Height + 15.4
                            && copyInInformation + copy.Bounds.Height <= information.Bounds.Height + .6
                            && copyTop >= -.6 && copyTop + copy.Bounds.Height <= scroll.Viewport.Height + .6
                            && view.GetVisualDescendants().OfType<FASettingsExpander>()
                                .Where(expander => !ReferenceEquals(expander, information) && !ReferenceEquals(expander, anc))
                                .All(expander => !expander.IsExpanded),
                            $"Expanding information reveals Copy below every field while optional settings stay collapsed: "
                            + $"expanded={information.IsExpanded}, hit={CanHit(window, copy)}, copy={copy.Bounds}, "
                            + $"copyInInformation={copyInInformation}, fieldsTop={fieldsTop}, fields={fields.Bounds}, "
                            + $"information={information.Bounds}, copyTop={copyTop}, viewport={scroll.Viewport}, "
                            + $"otherExpanded={string.Join(',', view.GetVisualDescendants().OfType<FASettingsExpander>()
                                .Where(expander => !ReferenceEquals(expander, information) && !ReferenceEquals(expander, anc) && expander.IsExpanded)
                                .Select(expander => expander.Name))}.");
                        using var informationFrame = window.CaptureRenderedFrame()
                            ?? throw new InvalidOperationException("Missing device-information layout pixels.");
                        var informationName = $"main-information-{(demo ? "demo" : "real")}-{(dark ? "dark" : "light")}-{(int)size.Width}x{(int)size.Height}.png";
                        var informationPath = Path.Combine(output, informationName);
                        using (var stream = new FileStream(informationPath, FileMode.CreateNew))
                            informationFrame.Save(stream, Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
                        receipts.Add(new { scenario = "main-information", demo, theme = dark ? "dark" : "light",
                            width = size.Width, height = size.Height, file = informationName, informationExpanded = true,
                            scrollY = scroll.Offset.Y, viewportHeight = scroll.Viewport.Height,
                            fitTop, informationTop, informationHeight = information.Bounds.Height,
                            copyTop, copyHeight = copy.Bounds.Height,
                            sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(informationPath))).ToLowerInvariant() });
                    }
                    finally { window.Close(); }
                }
            }
            finally { Application.Current.RequestedThemeVariant = oldTheme; }
        }
    }

    private static async Task CopyAsync()
    {
        await using var vm = new MainViewModel(new PreviewService(), new NoConfirm(), preview: true);
        await vm.InitializeAsync();
        var view = new MainView();
        var window = new Window { Content = view, Width = 1280, Height = 900, WindowDecorations = WindowDecorations.None };
        var copied = new List<string>();
        view.WriteClipboardOverride = text => { copied.Add(text); return Task.CompletedTask; };
        try
        {
            window.Show(); Dispatcher.UIThread.RunJobs();
            var button = view.FindControl<Button>("CopyDeviceInfoButton")!;
            Check(!button.IsEnabled, "Copy remains disabled when no device is selected.");
            await view.CopyDeviceInfoAsync();
            Check(copied.Count == 0, "An unavailable copy action does not access the clipboard.");
            view.DataContext = vm; Dispatcher.UIThread.RunJobs();
            var information = view.FindControl<FASettingsExpander>("DeviceInfoPanel")!;
            information.BringIntoView(); Dispatcher.UIThread.RunJobs();
            Check(!information.IsExpanded && !CanHit(window, button), "Collapsed information cannot expose a clickable Copy button.");
            await ExpandInformationAsync(information);
            await ClickAsync(window, button);
            Check(copied.SequenceEqual([Expected(vm)]) && view.FindControl<TextBlock>("DeviceInfoCopyStatus")!.Text == "已复制"
                && information.IsExpanded,
                "Clicking Copy below the expanded fields copies the displayed information and keeps the panel open.");

            await vm.AddDemoDeviceAsync(); Dispatcher.UIThread.RunJobs();
            await view.CopyDeviceInfoAsync();
            Check(copied.Count == 2 && copied[1] == Expected(vm) && copied[1] != copied[0],
                "A subsequent copy reads the currently selected device rather than cached metadata.");
            view.WriteClipboardOverride = _ => Task.FromException(new IOException("clipboard locked"));
            await view.CopyDeviceInfoAsync();
            Check(button.IsEnabled && view.FindControl<TextBlock>("DeviceInfoCopyStatus")!.Text == "复制失败，请重试"
                && !window.GetVisualDescendants().OfType<FAContentDialog>().Any(),
                "Clipboard failure keeps retry available and reports locally without opening a modal dialog.");

            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            view.WriteClipboardOverride = text => { copied.Add(text); return completion.Task; };
            var pending = view.CopyDeviceInfoAsync();
            Check(!button.IsEnabled && !pending.IsCompleted, "A pending clipboard write disables duplicate requests.");
            await view.CopyDeviceInfoAsync();
            Check(copied.Count == 3, "Another request cannot race the pending clipboard write.");
            view.DataContext = null; Dispatcher.UIThread.RunJobs();
            completion.SetResult(); await pending;
            Check(!button.IsEnabled && string.IsNullOrEmpty(view.FindControl<TextBlock>("DeviceInfoCopyStatus")!.Text),
                "A late clipboard result cannot report success against a different device context.");
            view.DataContext = vm; Dispatcher.UIThread.RunJobs();
            await view.DismissSettingsDialogAsync();
            Check(!button.IsEnabled, "Closing the view disables device-information copying.");
        }
        finally { window.Close(); }
    }

    private static async Task HeaderActivityAsync()
    {
        var service = new InteractionTests.BlockingService();
        await using var vm = new MainViewModel(service, new NoConfirm());
        await vm.InitializeAsync(); await vm.ConnectCommand.ExecuteAsync(null);
        var view = new MainView { DataContext = vm };
        var window = new Window { Content = view, Width = 1280, Height = 900, WindowDecorations = WindowDecorations.None };
        try
        {
            window.Show(); Dispatcher.UIThread.RunJobs();
            var bar = view.FindControl<ProgressBar>("ActivityBar")!;
            var dot = view.FindControl<Avalonia.Controls.Shapes.Ellipse>("ConnectionDot")!;
            var scroll = view.FindControl<ScrollViewer>("ControlScroll")!;
            var battery = view.FindControl<Border>("BatteryPanel")!;
            (double Scroll, double Battery, Rect Bar) Positions() => (scroll.TranslatePoint(default, window)!.Value.Y,
                battery.TranslatePoint(default, window)!.Value.Y, new Rect(bar.TranslatePoint(default, window)!.Value, bar.Bounds.Size));
            var idle = Positions();
            Check(!vm.HasActivity && bar.IsVisible && !bar.IsIndeterminate && !bar.Classes.Contains("active")
                && Math.Abs(bar.Bounds.Height - 3) < .01 && vm.Indicator == ConnectionIndicator.Online && dot.Classes.Contains("online"),
                "An idle activity bar keeps its three-pixel slot while the connected dot is green.");
            service.BlockNextRead = true;
            var read = vm.PollAsync();
            await service.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(3)); Dispatcher.UIThread.RunJobs();
            Check(vm.IsRefreshing && bar.IsIndeterminate && bar.Classes.Contains("active") && Positions() == idle
                && vm.Indicator == ConnectionIndicator.Online,
                "A background read shows activity without moving content or implying a connection change.");
            var action = vm.SelectAncModeAsync(vm.AncModes.Single(mode => mode.Key == 0)); Dispatcher.UIThread.RunJobs();
            Check(!action.IsCompleted && vm.Indicator == ConnectionIndicator.Pending && dot.Classes.Contains("pending")
                && !dot.Classes.Contains("online") && Positions() == idle,
                "A queued write turns the dot yellow while the layout stays fixed.");
            service.ReleaseRead.TrySetResult();
            await action.WaitAsync(TimeSpan.FromSeconds(3)); await read.WaitAsync(TimeSpan.FromSeconds(3)); Dispatcher.UIThread.RunJobs();
            Check(!vm.HasActivity && !bar.IsIndeterminate && !bar.Classes.Contains("active") && Positions() == idle
                && vm.Indicator == ConnectionIndicator.Online && dot.Classes.Contains("online") && !dot.Classes.Contains("pending"),
                "Finishing the read and write hides activity in place and restores the green dot.");
        }
        finally { service.ReleaseRead.TrySetResult(); window.Close(); }
    }

    private static void CheckHeader(MainView view, MainViewModel vm, Window window)
    {
        var header = view.FindControl<Grid>("DeviceHeader")!;
        var dot = view.FindControl<Avalonia.Controls.Shapes.Ellipse>("ConnectionDot")!;
        var title = view.FindControl<TextBlock>("DeviceTitle")!;
        var subtitle = view.FindControl<TextBlock>("DeviceSubtitle")!;
        var bar = view.FindControl<ProgressBar>("ActivityBar")!;
        var scroll = view.FindControl<ScrollViewer>("ControlScroll")!;
        var dotCentre = dot.TranslatePoint(new Point(dot.Bounds.Width / 2, dot.Bounds.Height / 2), window)!.Value;
        var titleTop = title.TranslatePoint(default, window)!.Value;
        var subtitleTop = subtitle.TranslatePoint(default, window)!.Value;
        var barTop = bar.TranslatePoint(default, window)!.Value.Y;
        var scrollTop = scroll.TranslatePoint(default, window)!.Value.Y;
        Check(dotCentre.X < titleTop.X && Math.Abs(dotCentre.Y - (titleTop.Y + title.Bounds.Height / 2)) < 1
            && Math.Abs(subtitleTop.X - titleTop.X) < .6 && subtitleTop.Y >= titleTop.Y + title.Bounds.Height - .6
            && subtitle.Text == vm.Subtitle && !vm.Subtitle.Contains("固件", StringComparison.Ordinal)
            && dot.Classes.Contains("online") == (vm.Indicator == ConnectionIndicator.Online),
            "The status dot sits left of the name, and the caption below it omits firmware.");
        Check(!scroll.GetVisualDescendants().Contains(header) && barTop >= header.TranslatePoint(default, window)!.Value.Y + header.Bounds.Height
            && scrollTop >= barTop + bar.Bounds.Height && bar.IsVisible && view.FindControl<Control>("CachedSettingsNotice") is null,
            "The header and activity slot stay pinned above the scrolling settings.");
    }

    private static void CheckIcons(MainView view)
    {
        var expanders = view.GetVisualDescendants().OfType<FASettingsExpander>().ToArray();
        var sections = view.GetVisualDescendants().OfType<FeatureSectionView>().Select(section => (FASettingsExpander)section.Content!).ToArray();
        Check(expanders.All(expander => expander.IconSource is not null && !string.IsNullOrEmpty(expander.Description))
            && view.FindControl<FASettingsExpander>("EqualizerPanel")!.IconSource is FAFontIconSource { Glyph: "\uE9E9" }
            && view.FindControl<FASettingsExpander>("FitTestPanel")!.IconSource is not null
            && sections.Length == ((MainViewModel)view.DataContext!).FeatureSections.Count
            && view.FindControl<Border>("BatteryPanel")!.GetVisualDescendants().OfType<TextBlock>()
                .Count(label => label.Text is "左耳" or "右耳" or "充电盒") == 3,
            "Every settings card, dynamic section and battery cell has an icon, and every card has a description.");
        CheckBatteryDeviceIcons(view);
    }

    private static void CheckBatteryDeviceIcons(MainView view)
    {
        Geometry GeometryFor(string name)
        {
            var icon = view.FindControl<FAIconSourceElement>(name)!;
            Check(icon.Width == 14 && icon.Height == 14 && icon.Bounds.Width == 14 && icon.Bounds.Height == 14
                && icon.Opacity == 1 && icon.IconSource is FAPathIconSource { Stretch: Stretch.None },
                "Battery device icons keep their native opacity and fourteen-pixel slots.");
            var geometry = ((FAPathIconSource)icon.IconSource!).Data!;
            Check(geometry.Bounds.Width > 0 && geometry.Bounds.Height > 0
                && geometry.Bounds.Left >= 0 && geometry.Bounds.Top >= 0
                && geometry.Bounds.Right <= 14 && geometry.Bounds.Bottom <= 14,
                "The complete battery icon fits inside its historical fourteen-pixel viewport.");
            return geometry;
        }
        var left = GeometryFor("LeftBatteryDeviceIcon");
        var right = GeometryFor("RightBatteryDeviceIcon");
        var box = GeometryFor("CaseBatteryDeviceIcon");
        foreach (var circle in new[] { left, right })
        {
            Check(circle.Bounds == new Rect(0, 0, 14, 14) && circle.Bounds.Center == new Point(7, 7)
                && circle.FillContains(new Point(7, .5)) && circle.FillContains(new Point(.5, 7))
                && circle.FillContains(new Point(13.5, 7)) && circle.FillContains(new Point(7, 13.5))
                && !circle.FillContains(new Point(1, 1)),
                "Left and right badges retain matching fourteen-pixel circles centered at seven pixels.");
        }
        Check(!left.FillContains(new Point(5, 5)) && !left.FillContains(new Point(8, 10.4))
            && left.FillContains(new Point(7, 7)) && !right.FillContains(new Point(7, 7)),
            "The L cutout keeps its vertical and bottom strokes and remains distinct from R.");
        Check(!right.FillContains(new Point(4.8, 5)) && right.FillContains(new Point(7, 5.4)),
            "The R cutout keeps its vertical stroke and filled inner counter under even-odd filling.");
        Check(box.Bounds.Width > box.Bounds.Height && box.Bounds.Top > 0 && box.Bounds.Bottom < 14
            && box.FillContains(new Point(7, 2.3)) && box.FillContains(new Point(7, 10.5))
            && !box.FillContains(new Point(7, 5.4)) && !box.FillContains(new Point(7, 8.32))
            && left.FillContains(new Point(7, 5.4)) && right.FillContains(new Point(7, 5.4)),
            "The matching case keeps its lid, body, open seam and indicator hole, distinct from both letter badges.");
    }

    private static string Expected(MainViewModel vm) => $"设备名称：{vm.Title}{Environment.NewLine}产品编号：{vm.ProductId ?? "—"}{Environment.NewLine}固件版本：{vm.Firmware ?? "—"}";
    private static async Task ExpandInformationAsync(FASettingsExpander information)
    {
        information.IsExpanded = true;
        var timer = System.Diagnostics.Stopwatch.StartNew();
        while (timer.Elapsed < TimeSpan.FromMilliseconds(350))
        {
            AvaloniaHeadlessPlatform.ForceRenderTimerTick(); Dispatcher.UIThread.RunJobs();
            await Task.Delay(20);
        }
        AvaloniaHeadlessPlatform.ForceRenderTimerTick(); Dispatcher.UIThread.RunJobs();
    }
    private static async Task ClickAsync(Window window, Control control)
    {
        var point = await MainUiTestLayout.ClickPointAsync(window, control);
        window.MouseDown(point, MouseButton.Left); window.MouseUp(point, MouseButton.Left); Dispatcher.UIThread.RunJobs();
    }
    private sealed class ChargingPreviewService : IHeadsetService
    {
        private readonly PreviewService _inner = new();
        public Task<IReadOnlyList<UiDevice>> EnumerateAsync(CancellationToken token) => _inner.EnumerateAsync(token);
        public async Task<UiSnapshot> ConnectAsync(string identity, CancellationToken token) => Charging(await _inner.ConnectAsync(identity, token));
        public Task DisconnectAsync(CancellationToken token) => _inner.DisconnectAsync(token);
        public async Task<UiSnapshot> RefreshAsync(CancellationToken token) => Charging(await _inner.RefreshAsync(token));
        public Task<UiSnapshot> RefreshAsync(RefreshScope scope, CancellationToken token) => RefreshAsync(token);
        public Task<UiApplyResult> ApplyAsync(UiAction action, CancellationToken token) => _inner.ApplyAsync(action, token);
        public ValueTask DisposeAsync() => _inner.DisposeAsync();
        private static UiSnapshot Charging(UiSnapshot snapshot) { snapshot.LeftCharging = true; return snapshot; }
    }

    private static bool CanHit(Window window, Control control)
    {
        if (control.Bounds.Width <= 0 || control.Bounds.Height <= 0) return false;
        var point = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window);
        return point is { } current && window.InputHitTest(current) is Visual hit
            && (ReferenceEquals(hit, control) || hit.GetVisualAncestors().Contains(control));
    }
    private static void Check(bool condition, string reason) { if (!condition) throw new InvalidOperationException(reason); }
}
