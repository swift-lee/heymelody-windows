using System.Runtime.InteropServices;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using FluentAvalonia.UI.Controls;
using HeyMelodyWindows.App;
using HeyMelodyWindows.Resources;
using HeyMelodyWindows.Artwork;

/// <summary>
/// A real Win32 window with an in-memory headset service; never starts a Bluetooth worker.
/// Only --native-settings-smoke launches Windows settings. A true launcher result confirms
/// that Windows accepted the URI request, not that a particular settings page was displayed.
/// </summary>
internal static class NativeSmoke
{
    private static SmokeState _state = new();

    public static int Run(bool openSettings = false, bool useRenderingSetup = false,
        bool? basicRenderMode = null, string? outputDirectory = null, string? renderingFailure = null)
    {
        if (!OperatingSystem.IsWindows())
        {
            Console.WriteLine(JsonSerializer.Serialize(new { success = false, reason = "Windows required", nativeBluetoothCalls = 0 }));
            return 2;
        }
        if (renderingFailure is not (null or "software-fallback" or "warp-missing"))
            throw new ArgumentException("Unknown rendering failure injection.", nameof(renderingFailure));
        _state = new() { OpenSettings = openSettings, BasicRenderMode = basicRenderMode, RenderingFailure = renderingFailure,
            Output = outputDirectory is null ? null : Path.GetFullPath(outputDirectory) };
        var originalPreferencesPath = ThemePreferences.SettingsPathOverride;
        if (basicRenderMode is not null)
        {
            _state.Output ??= Path.GetFullPath(Path.Combine("artifacts", "ui-render-policy-native-" + Guid.NewGuid().ToString("N")));
            Directory.CreateDirectory(_state.Output);
            ThemePreferences.SettingsPathOverride = Path.Combine(_state.Output, "isolated-preferences.json");
            File.WriteAllText(ThemePreferences.SettingsPathOverride, "{\"theme\":\"light\",\"uiBasicRenderMode\":" + (basicRenderMode.Value ? "true" : "false") + "}");
        }
        var uiThread = new Thread(() =>
        {
            try
            {
                var builder = AppBuilder.Configure<SmokeApplication>().UsePlatformDetect();
                if (useRenderingSetup)
                {
                    System.Diagnostics.Trace.Listeners.Add(new System.Diagnostics.ConsoleTraceListener());
                    builder = HeyMelodyWindows.Ui.RenderingSetup.UseHeyMelodyRendering(builder, basicRenderMode ?? UiRenderingPreferenceReader.Load());
                    if (renderingFailure is not null)
                        builder = builder.With(HeyMelodyWindows.Ui.RenderingSetup.CreateWindowsOptions(true,
                            () => renderingFailure == "software-fallback" ? null : Enumerable.Repeat((byte)0x7f, 8).ToArray()));
                    builder = HeyMelodyWindows.Ui.Typography.UseHeyMelodyTypography(builder);
                }
                builder.LogToTrace(useRenderingSetup ? Avalonia.Logging.LogEventLevel.Information : Avalonia.Logging.LogEventLevel.Warning)
                    .StartWithClassicDesktopLifetime([], ShutdownMode.OnExplicitShutdown);
            }
            catch (Exception error) { _state.Failure = error.ToString(); }
        }) { IsBackground = true, Name = "HeyMelody native smoke UI" };
        uiThread.SetApartmentState(ApartmentState.STA);
        uiThread.Start();
        if (!uiThread.Join(TimeSpan.FromSeconds(basicRenderMode is null ? 20 : 40)))
            _state.Failure ??= "Native UI thread did not stop within its time limit.";
        ThemePreferences.SettingsPathOverride = originalPreferencesPath;
        var success = _state.Failure is null && _state.Closed && _state.Service.Disposals == 1
            && _state.Service.Submissions == 0 && _state.Checks > 0
            && (!_state.OpenSettings || _state.SystemSettingsLaunchAccepted == true);
        var report = JsonSerializer.Serialize(new
        {
            schemaVersion = 1, success, passed = _state.Checks, nativeBluetoothCalls = 0,
            applicationSetSubmissions = _state.Service.Submissions,
            serviceDisposals = _state.Service.Disposals, windowClosed = _state.Closed,
            settingsDialogCompleted = _state.SettingsDialogCompleted,
            failureDialogsCompleted = _state.FailureDialogsCompleted,
            systemSettingsLaunchRequested = _state.OpenSettings,
            systemSettingsLaunchAccepted = _state.SystemSettingsLaunchAccepted,
            systemSettingsLaunchUri = _state.SystemSettingsLaunchUri,
            requestedBasicRenderMode = _state.BasicRenderMode, renderingFailureInjection = _state.RenderingFailure,
            rendering = _state.Probe?.Receipt,
            screenshots = _state.Screenshots,
            scope = _state.BasicRenderMode is not null
                ? "Real visible Win32 main window and in-window dialogs; actual compositor GL renderer, screen-DC pixel captures for manual review, both themes, sizes, Mica/DWM/opaque fallback and fake-service lifecycle. Preferences are isolated. No production backend, native Bluetooth or headset SET."
                : _state.OpenSettings
                ? "real Win32 FAAppWindow geometry, themes, Avalonia Composition Mica, DWM properties, opaque fallback and lifecycle with an in-memory headset service; one real Windows settings launch; true means the OS accepted the URI request, not that page content was verified; no DWM pixel or hardware assertions"
                : "real Win32 FAAppWindow geometry, themes, Avalonia Composition Mica, DWM properties, opaque fallback and lifecycle with an in-memory service; no system settings launch, DWM pixel or hardware assertions",
            states = _state.Receipts, failure = _state.Failure
        }, new JsonSerializerOptions { WriteIndented = _state.Output is not null });
        if (_state.Output is not null) File.WriteAllText(Path.Combine(_state.Output, "report.json"), report);
        Console.WriteLine(report);
        return success ? 0 : 1;
    }

    private sealed class SmokeState
    {
        public SmokeService Service { get; } = new();
        public int Checks { get; set; }
        public bool Closed { get; set; }
        public bool SettingsDialogCompleted { get; set; }
        public bool FailureDialogsCompleted { get; set; }
        public bool OpenSettings { get; init; }
        public bool? BasicRenderMode { get; init; }
        public string? RenderingFailure { get; init; }
        public string? Output { get; set; }
        public NativeRenderingProbe? Probe { get; set; }
        public List<object> Screenshots { get; } = [];
        public bool? SystemSettingsLaunchAccepted { get; set; }
        public string? SystemSettingsLaunchUri { get; set; }
        public string? Failure { get; set; }
        public List<object> Receipts { get; } = [];
        public TaskCompletionSource ClosedSignal { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    // Base.Initialize is the App's compiled XAML population method; do not invoke the
    // App's framework completion override, which would create the live backend.
    public sealed class SmokeApplication : App
    {
        public override void OnFrameworkInitializationCompleted()
        {
            var desktop = ApplicationLifetime as IClassicDesktopStyleApplicationLifetime
                ?? throw new InvalidOperationException("Desktop lifetime required.");
            var window = new MainWindow(_state.Service, preview: true)
            {
                ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual,
                Position = new PixelPoint(-10000, -10000)
            };
            if (_state.BasicRenderMode is not null)
            {
                window.ShowActivated = true;
                window.Topmost = true;
                window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
                _state.Probe = NativeRenderingProbe.Attach(window);
            }
            desktop.MainWindow = window;
            window.Closed += (_, _) =>
            {
                _state.Closed = true;
                _state.ClosedSignal.TrySetResult();
            };
            window.Opened += (_, _) => _ = VerifyAndCloseAsync(window, desktop);
            // A separate watchdog also covers an initialization/close failure which
            // otherwise could leave a native test window or its dispatcher running.
            _ = StopOnTimeoutAsync(window, desktop);
        }
    }

    private static async Task VerifyAndCloseAsync(MainWindow window, IClassicDesktopStyleApplicationLifetime desktop)
    {
        Task? settingsTask = null;
        try
        {
            var page = window.FindControl<MainView>("Page") ?? throw new InvalidOperationException("MainView missing.");
            var vm = page.DataContext as MainViewModel ?? throw new InvalidOperationException("View model missing.");
            for (int attempt = 0; !vm.HasVerifiedSnapshot && attempt < 40; attempt++) await Task.Delay(50);
            Check(vm.HasVerifiedSnapshot, "Fake snapshot initializes the real window.");
            var handle = window.TryGetPlatformHandle();
            Check(handle is not null && handle.Handle != IntPtr.Zero && handle.HandleDescriptor == "HWND", "Real Win32 HWND.");
            Check(window.Icon is not null && window.Title == "HeyMelody", "Title and icon loaded.");
            if (_state.Probe is { } probe)
            {
                for (int attempt = 0; !probe.Queried && attempt < 80; attempt++) await Task.Delay(50);
                Check(probe.Queried && probe.Failure is null, "The actual compositor context was observed without an observer failure.");
                if (_state.RenderingFailure is not null)
                    Check(!probe.HasGpuContext && probe.Renderer is null,
                        "WARP query/matching failure reaches software Skia without silently returning to a hardware GPU.");
                else
                {
                    Check(probe.HasGpuContext && !string.IsNullOrWhiteSpace(probe.Renderer),
                        "The actual compositor render context exposes its ANGLE renderer.");
                    Check(_state.BasicRenderMode == probe.Renderer!.Contains("Microsoft Basic Render Driver", StringComparison.OrdinalIgnoreCase),
                        "The actual renderer matches the requested default GPU or Basic Render mode.");
                }
            }
            foreach (var dark in new[] { false, true })
            foreach (var size in new[] { new Size(1280, 900), new Size(900, 640) })
            {
                ((App)Application.Current!).ApplyTheme(dark ? "dark" : "light");
                window.Width = size.Width; window.Height = size.Height;
                await Task.Delay(150);
                Dispatcher.UIThread.RunJobs();
                Check(window.ActualThemeVariant == (dark ? ThemeVariant.Dark : ThemeVariant.Light), "Theme reaches native window.");
                var backdrop = await VerifyBackdropAsync(window, dark, probeFallback: size.Width == 1280);
                var titleBar = window.GetVisualDescendants().OfType<Panel>().FirstOrDefault(control => control.Name == "DefaultTitleBar");
                Check(titleBar is { IsVisible: true } && titleBar.Bounds.Height > 0, "Managed Windows titlebar is visible.");
                Check(window.TemplateSettings.IsTitleBarContentVisible && window.TitleBar.Height > 0, "Titlebar settings preserve content separation.");
                var pageOrigin = page.TranslatePoint(default, window) ?? throw new InvalidOperationException("View is detached.");
                Check(page.Bounds.Width > 0 && page.Bounds.Height > 0 && pageOrigin.Y >= window.TitleBar.Height - 1, "Content fits below titlebar.");
                Check(page.IsCompact == (page.Bounds.Width < 1080), "Native responsive breakpoint.");
                var anc = page.FindControl<FASettingsExpander>("AncPanel")!;
                Check(anc is { IsVisible: true, IsExpanded: true }
                    && page.GetVisualDescendants().OfType<FASettingsExpander>()
                        .Where(expander => !ReferenceEquals(expander, anc)).All(expander => !expander.IsExpanded),
                    "The native ANC panel starts expanded and optional settings start collapsed.");
                var hero = page.FindControl<ProductArtwork>("HeroArt") ?? throw new InvalidOperationException("Product artwork missing.");
                Check(Equals(hero.Visual, DeviceVisuals.X4Product)
                    && hero.GetVisualDescendants().OfType<Image>().SingleOrDefault() is not null,
                    "Connected X4 selects its own original PNG artwork.");
                var picker = page.FindControl<ListBox>("DevicePicker") ?? throw new InvalidOperationException("Picker missing.");
                var pickerOrigin = picker.TranslatePoint(default, page) ?? throw new InvalidOperationException("Picker is detached.");
                Check(picker.IsVisible && picker.Bounds.Height > 0 && pickerOrigin.Y >= 0
                    && pickerOrigin.Y + picker.Bounds.Height <= page.Bounds.Height + 1, "Picker remains inside native content viewport.");
                DeviceBarTests.CheckBottomBar(page, page);
                var scroll = page.FindControl<ScrollViewer>("DeviceBarScroll") ?? throw new InvalidOperationException("Device bar scroll missing.");
                var padding = page.FindControl<Border>("DeviceStripPadding") ?? throw new InvalidOperationException("Device strip padding missing.");
                var strip = page.FindControl<StackPanel>("DeviceStrip") ?? throw new InvalidOperationException("Device strip missing.");
                var add = page.FindControl<Button>("AddDeviceButton") ?? throw new InvalidOperationException("Add device button missing.");
                Check(picker.GetVisualAncestors().Contains(scroll) && add.GetVisualAncestors().Contains(scroll),
                    "The native device picker and trailing add button share one scroll viewport.");
                Check(scroll.Extent.Width > scroll.Viewport.Width && padding.Padding == default
                    && Math.Abs(scroll.Extent.Width - strip.Bounds.Width) <= 1,
                    "The native overflowing strip ends at its actual contents without artificial centering space.");
                var selectedCard = picker.ContainerFromIndex(picker.SelectedIndex) as ListBoxItem
                    ?? throw new InvalidOperationException("Selected native device card was not realized.");
                var card = selectedCard.GetVisualDescendants().OfType<Grid>().Single(control => control.Name == "DeviceCardContent");
                Check(card.ColumnDefinitions.Count == 2
                    && card.GetVisualDescendants().OfType<Viewbox>().Any(control => control.Name == "DeviceIcon"
                        && DeviceBarTests.HasEarbudIcon(control))
                    && !card.GetVisualDescendants().OfType<Image>().Any()
                    && selectedCard.GetVisualDescendants().OfType<TextBlock>().Any(control => control.Name == "DeviceNameText"
                        && control.Text == vm.SelectedDevice!.Name),
                    "The native device card shows the settings earbud glyph, name and status without a product image.");
                var selectedOrigin = selectedCard.TranslatePoint(default, scroll)
                    ?? throw new InvalidOperationException("Selected native device card is detached.");
                var selectedCenter = selectedOrigin.X + selectedCard.Bounds.Width / 2;
                Check(selectedCard.IsSelected && Equals(picker.SelectedItem, vm.SelectedDevice)
                    && picker.SelectedIndex == 0 && Math.Abs(scroll.Offset.X) <= 1
                    && selectedOrigin.X >= -1 && selectedOrigin.X + selectedCard.Bounds.Width <= scroll.Viewport.Width + 1,
                    "The first selected native device stays fully visible at the natural left boundary after startup and resizing.");
                _state.Receipts.Add(new
                {
                    theme = dark ? "dark" : "light", requestedWidth = size.Width, requestedHeight = size.Height,
                    clientWidth = window.ClientSize.Width, clientHeight = window.ClientSize.Height,
                    renderScaling = window.RenderScaling,
                    contentWidth = page.Bounds.Width, contentHeight = page.Bounds.Height,
                    titleBarHeight = titleBar!.Bounds.Height, compact = page.IsCompact,
                    deviceCount = picker.ItemCount, deviceViewportWidth = scroll.Viewport.Width,
                    deviceExtentWidth = scroll.Extent.Width, deviceOffset = scroll.Offset.X,
                    selectedDeviceCenter = selectedCenter, devicePadding = padding.Padding.Left,
                    backdrop
                });
                if (_state.Output is not null && size.Width == 900)
                {
                    await NativeRenderingProbe.WaitForPixelsAsync(window);
                    _state.Screenshots.Add(NativeRenderingProbe.Capture(window,
                        Path.Combine(_state.Output, dark ? "main-dark.png" : "main-light.png")));
                    Check(true, "A visible on-screen compositor screenshot was saved for manual review.");
                }
            }
            var bundle = new BundledResourceStore();
            var animationAsset = bundle.FindNamedAsset("melody_ui_gold_hearing_detecting_guide")
                ?? throw new InvalidOperationException("Original Lottie asset missing.");
            var animationPath = bundle.FullPath(animationAsset);
            var artwork = page.FindControl<ProductArtwork>("HeroArt")!;
            artwork.Visual = new LottieVisual(new ArtworkFile(new Uri(animationPath), Path.GetDirectoryName(animationPath)!, animationAsset.Sha256), DeviceVisuals.GenericProduct);
            var animation = artwork.GetVisualDescendants().OfType<LottieView>().Single();
            for (int attempt = 0; animation.RenderedFrameCount < 2 && attempt < 100; attempt++) await Task.Delay(30);
            Check(animation.AnimationLoaded && animation.RenderedFrameCount >= 2,
                $"The original APK Lottie is rendered repeatedly by the real Win32 Skia compositor: loaded={animation.AnimationLoaded}, active={animation.AnimationActive}, bounds={animation.Bounds}, frames={animation.RenderedFrameCount}, error={animation.LoadError}.");
            artwork.Visual = DeviceVisuals.X4Product;
            if (_state.OpenSettings) await VerifySystemSettingsLaunchAsync(page, window, vm);
            Check(_state.Service.Submissions == 0, "Native smoke never submits settings.");
            var dialogs = new WindowConfirmationService(window);
            var retryTask = vm.Dialogs.RunAsync(token => dialogs.ConfirmRetryAsync("重试设置", "请核对后决定是否重试。", token));
            var retryDialog = await FindDialogAsync(window, "重试设置");
            Check(retryDialog.PrimaryButtonText == "确认" && retryDialog.CloseButtonText == "取消"
                && retryDialog.DefaultButton == FAContentDialogButton.Close, "Native retry dialog defaults to cancel.");
            dialogs.CancelActiveDialog();
            Check(!await retryTask, "Dismissed native retry does not grant retry consent.");
            var alertTask = vm.Dialogs.RunAsync(token => dialogs.AlertAsync("设置未完成", "请检查当前状态。", token));
            var alertDialog = await FindDialogAsync(window, "设置未完成");
            Check(alertDialog.CloseButtonText == "确定" && string.IsNullOrEmpty(alertDialog.PrimaryButtonText)
                && string.IsNullOrEmpty(alertDialog.SecondaryButtonText), "Native failure alert only acknowledges the failure.");
            settingsTask = page.OpenSettingsDialogAsync();
            await Task.Delay(50);
            Check(!settingsTask.IsCompleted && !window.GetVisualDescendants().OfType<FAContentDialog>()
                .Any(dialog => Equals(dialog.Title, "应用设置")), "Settings waits behind the native failure dialog.");
            dialogs.CancelActiveDialog(); await alertTask;
            _state.FailureDialogsCompleted = true;
            var settingsDialog = await FindDialogAsync(window, "应用设置");
            Check(!settingsDialog.GetVisualDescendants().OfType<ComboBox>().Any(control =>
                    Avalonia.Automation.AutomationProperties.GetName(control) == "资源来源")
                && settingsDialog.GetVisualDescendants().OfType<TextBox>().Any(control =>
                    Avalonia.Automation.AutomationProperties.GetName(control) == "云端资源密钥"),
                "Native settings expose one HeyMelody key and no source selector.");
            Check(!settingsTask.IsCompleted, "Settings dialog opens after the failure dialog releases ownership.");
            if (_state.Output is not null)
            {
                await NativeRenderingProbe.WaitForPixelsAsync(window);
                _state.Screenshots.Add(NativeRenderingProbe.Capture(window, Path.Combine(_state.Output, "settings-popup.png")));
                Check(true, "The visible settings popup compositor screenshot was saved for manual review.");
            }
        }
        catch (Exception error) { _state.Failure = error.ToString(); }
        finally
        {
            window.Close();
            try { await _state.ClosedSignal.Task.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (TimeoutException) { _state.Failure ??= "Native window did not close in five seconds."; }
            if (settingsTask is not null)
            {
                try
                {
                    await settingsTask.WaitAsync(TimeSpan.FromSeconds(2));
                    _state.SettingsDialogCompleted = true;
                    Check(_state.Service.Submissions == 0, "Closing settings leaves no write or pending dialog.");
                }
                catch (Exception error) { _state.Failure ??= error.ToString(); }
            }
            if (_state.Service.Disposals != 1) _state.Failure ??= "Fake service was not disposed exactly once.";
            desktop.Shutdown(_state.Failure is null ? 0 : 1);
        }
    }

    private static async Task VerifySystemSettingsLaunchAsync(MainView page, MainWindow window, MainViewModel vm)
    {
        var owner = TopLevel.GetTopLevel(page) ?? throw new InvalidOperationException("Page has no native owner.");
        var launcher = new RecordingLauncher(owner.Launcher);
        page.LauncherOverride = launcher;
        var enumerations = _state.Service.Enumerations;
        var selected = vm.SelectedDevice;
        var button = page.FindControl<Button>("AddDeviceButton") ?? throw new InvalidOperationException("Add device button missing.");
        var scroll = page.FindControl<ScrollViewer>("DeviceBarScroll") ?? throw new InvalidOperationException("Device bar scroll missing.");
        scroll.Offset = new Vector(scroll.Extent.Width, 0);
        await Task.Delay(50); Dispatcher.UIThread.RunJobs();
        var buttonOrigin = button.TranslatePoint(default, scroll) ?? throw new InvalidOperationException("Add device button is detached.");
        Check(buttonOrigin.X >= -1 && buttonOrigin.X + button.Bounds.Width <= scroll.Viewport.Width + 1,
            "Scrolling to the native strip's end makes the add device button visible before activation.");
        Check(button.IsEffectivelyEnabled, "Native add device button is enabled before launch.");
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        _state.SystemSettingsLaunchUri = launcher.Uris.SingleOrDefault()?.OriginalString;
        _state.SystemSettingsLaunchAccepted = await launcher.Completed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Check(launcher.Uris is [{ OriginalString: "ms-settings:bluetooth" }],
            "The native add device button launches the Bluetooth settings URI exactly once.");
        Check(_state.SystemSettingsLaunchAccepted == true, "Windows accepts the Bluetooth settings URI request.");
        var host = page.FindControl<Border>("AddDeviceHost") ?? throw new InvalidOperationException("Add device host missing.");
        for (int attempt = 0; !host.IsEnabled && attempt < 40; attempt++) await Task.Delay(25);
        Check(host.IsEnabled, "Native add device control recovers after the accepted launch.");
        Check(!window.GetVisualDescendants().OfType<FAContentDialog>().Any(), "Accepted launch creates no application error dialog.");
        Check(_state.Service.Enumerations == enumerations && _state.Service.Submissions == 0
            && Equals(vm.SelectedDevice, selected) && vm.HasVerifiedSnapshot,
            "Opening Windows settings leaves device enumeration, selection and the verified session intact without a setter.");
        // Keep this explicit smoke mode alive briefly for an external UI Automation
        // observer; the launcher receipt alone does not verify the Settings page.
        await Task.Delay(TimeSpan.FromSeconds(3));
    }

    private static async Task StopOnTimeoutAsync(MainWindow window, IClassicDesktopStyleApplicationLifetime desktop)
    {
        await Task.Delay(TimeSpan.FromSeconds(_state.BasicRenderMode is null ? 12 : 30));
        if (_state.Closed) return;
        _state.Failure ??= "Native smoke exceeded twelve seconds.";
        window.Close();
        desktop.Shutdown(1);
    }

    private static async Task<object> VerifyBackdropAsync(MainWindow window, bool dark, bool probeFallback)
    {
        var hwnd = window.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        Check(hwnd != IntPtr.Zero, "Backdrop checks use the actual native window.");
        var level = window.ActualTransparencyLevel;
        Check(level == WindowTransparencyLevel.Mica || level == WindowTransparencyLevel.None,
            $"The native main window uses ordinary Mica or an opaque platform fallback; actual={level}.");
        var compositionResult = DwmIsCompositionEnabled(out var compositionEnabled);
        var backdropResult = DwmGetWindowAttribute(hwnd, 38, out var systemBackdrop, sizeof(int));
        var darkResult = DwmGetWindowAttribute(hwnd, 20, out var immersiveDark, sizeof(int));
        // Avalonia 12.1.4 supplies Mica through its WinUI Composition brush. Its
        // MicaLight/MicaDark enum is not the separate DWMSBT_* DWM enum.
        Check(backdropResult < 0 || systemBackdrop != 4,
            "The main window never requests the DWM Mica Alt backdrop.");
        if (compositionResult >= 0 && compositionEnabled && OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22621))
        {
            Check(backdropResult >= 0, "Modern Windows exposes the native system backdrop property.");
            Check(darkResult >= 0 && immersiveDark == (dark ? 1 : 0), "DWM dark mode follows both application themes.");
        }
        if (level == WindowTransparencyLevel.Mica)
            Check(window.Background is ISolidColorBrush { Color.A: 0 }, "The active Mica material is not covered by an opaque window background.");
        else VerifyOpaqueFallback(window);

        bool fallbackProbed = level == WindowTransparencyLevel.None;
        if (probeFallback && !fallbackProbed)
        {
            var hints = window.TransparencyLevelHint;
            try
            {
                window.TransparencyLevelHint = [WindowTransparencyLevel.None];
                await Task.Delay(75);
                Dispatcher.UIThread.RunJobs();
                Check(window.ActualTransparencyLevel == WindowTransparencyLevel.None, "The native platform enters the opaque fallback path.");
                VerifyOpaqueFallback(window);
                fallbackProbed = true;
            }
            finally
            {
                window.TransparencyLevelHint = hints;
                await Task.Delay(75);
                Dispatcher.UIThread.RunJobs();
            }
            Check(window.ActualTransparencyLevel == WindowTransparencyLevel.Mica
                || window.ActualTransparencyLevel == WindowTransparencyLevel.None,
                "Restoring backdrop preferences retains Mica or the platform's opaque fallback.");
            if (window.ActualTransparencyLevel == WindowTransparencyLevel.None) VerifyOpaqueFallback(window);
        }
        var fallback = (ISolidColorBrush)window.TransparencyBackgroundFallback;
        return new
        {
            actualTransparency = level.ToString(), restoredTransparency = window.ActualTransparencyLevel.ToString(),
            compositionResult, compositionEnabled, backdropResult,
            systemBackdrop = backdropResult >= 0 ? (int?)systemBackdrop : null,
            darkResult, immersiveDark = darkResult >= 0 ? (int?)immersiveDark : null,
            fallbackColor = fallback.Color.ToString(), fallbackProbed,
            materialApi = "Avalonia WinUI Composition Mica; DWMSBT_* is observed separately"
        };
    }

    private static void VerifyOpaqueFallback(MainWindow window)
    {
        Check(window.TransparencyBackgroundFallback is ISolidColorBrush { Color.A: 255, Opacity: 1 },
            "The platform fallback paints an opaque theme surface.");
        var fallback = (ISolidColorBrush)window.TransparencyBackgroundFallback;
        // The overload without ThemeVariant passes null rather than the host's
        // ActualThemeVariant, so it cannot verify a dark DynamicResource value.
        bool found = window.TryFindResource("ApplicationPageBackgroundThemeBrush", window.ActualThemeVariant, out var resource);
        var theme = resource as ISolidColorBrush;
        Check(found && theme is not null && fallback.Color == theme.Color,
            $"The effective fallback follows the current application theme; theme={window.ActualThemeVariant}, "
            + $"actual={fallback.Color}, expected={theme?.Color.ToString() ?? "unresolved"}.");
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int value, int size);
    [DllImport("dwmapi.dll")]
    private static extern int DwmIsCompositionEnabled([MarshalAs(UnmanagedType.Bool)] out bool enabled);

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
        _state.Checks++;
    }

    private static async Task<FAContentDialog> FindDialogAsync(Window window, string title)
    {
        for (int attempt = 0; attempt < 40; attempt++)
        {
            var dialog = window.GetVisualDescendants().OfType<FAContentDialog>()
                .FirstOrDefault(candidate => Equals(candidate.Title, title));
            if (dialog is not null) return dialog;
            await Task.Delay(25);
        }
        throw new InvalidOperationException($"Native dialog did not open: {title}");
    }

    private sealed class SmokeService : IHeadsetService
    {
        private readonly PreviewService _inner = new();
        public int Submissions { get; private set; }
        public int Disposals { get; private set; }
        public int Enumerations { get; private set; }
        public async Task<IReadOnlyList<UiDevice>> EnumerateAsync(CancellationToken token)
        {
            Enumerations++;
            var first = (await _inner.EnumerateAsync(token))[0];
            return new[] { first }.Concat(Enumerable.Range(1, 7)
                .Select(index => new UiDevice("native-smoke-device-" + index, "原生布局测试耳机 " + (index + 1), true))).ToArray();
        }
        public Task<UiSnapshot> ConnectAsync(string identity, CancellationToken token) => _inner.ConnectAsync(identity, token);
        public Task DisconnectAsync(CancellationToken token) => _inner.DisconnectAsync(token);
        public Task<UiSnapshot> RefreshAsync(CancellationToken token) => _inner.RefreshAsync(token);
        public Task<UiApplyResult> ApplyAsync(UiAction action, CancellationToken token)
        {
            Submissions++;
            throw new InvalidOperationException("Native smoke forbids settings.");
        }
        public ValueTask DisposeAsync() { Disposals++; return _inner.DisposeAsync(); }
    }

    private sealed class RecordingLauncher(ILauncher inner) : ILauncher
    {
        public List<Uri> Uris { get; } = [];
        public TaskCompletionSource<bool> Completed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<bool> LaunchUriAsync(Uri uri)
        {
            Uris.Add(uri);
            try
            {
                var accepted = await inner.LaunchUriAsync(uri);
                Completed.TrySetResult(accepted);
                return accepted;
            }
            catch (Exception error)
            {
                Completed.TrySetException(error);
                throw;
            }
        }
        public Task<bool> LaunchFileAsync(IStorageItem file) => inner.LaunchFileAsync(file);
    }
}
