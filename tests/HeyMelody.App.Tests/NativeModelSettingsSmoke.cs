using System.Runtime.InteropServices;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Primitives;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.VisualTree;
using FluentAvalonia.UI.Controls;
using HeyMelody.Rendering.Filament;
using HeyMelodyWindows.App;
using HeyMelodyWindows.Ui;
using SkiaSharp;

/// <summary>Focused Win32 settings checks with an isolated preference file and a managed recording renderer.</summary>
internal static class NativeModelSettingsSmoke
{
    private static State _state = new();

    internal static int Run(string? output = null)
    {
        if (!OperatingSystem.IsWindows()) return 2;
        output ??= Path.Combine(Directory.GetCurrentDirectory(), "artifacts", "native-model-settings-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(output);
        _state = new() { Output = output };
        var thread = new Thread(() =>
        {
            var oldPath = ThemePreferences.SettingsPathOverride;
            var oldBackend = ModelRenderingPreferences.CompatibilityMode;
            var oldRate = ModelRenderingPreferences.FrameRate;
            var oldFactory = DeviceModelView.DefaultFactoryOverride;
            try
            {
                ThemePreferences.SettingsPathOverride = Path.Combine(output, "preferences-" + Guid.NewGuid().ToString("N") + ".json");
                File.WriteAllText(ThemePreferences.SettingsPathOverride,
                    "{\"theme\":\"system\",\"modelFrameRate\":\"display\",\"unrelated\":17}");
                DeviceModelView.DefaultFactoryOverride = _state.Factory;
                ModelRenderingPreferences.Reload();
                Check(ModelRenderingPreferences.Backend == FilamentBackend.Vulkan
                    && ModelRenderingPreferences.FrameRate == ModelFrameRate.Unlimited, "Isolated preferences default to Vulkan and unlimited.");
                AppBuilder.Configure<SmokeApplication>().UsePlatformDetect().UseHeyMelodyRendering(false).UseHeyMelodyTypography()
                    .StartWithClassicDesktopLifetime([], ShutdownMode.OnExplicitShutdown);
            }
            catch (Exception error) { _state.Failure ??= error.ToString(); }
            finally
            {
                ThemePreferences.SettingsPathOverride = oldPath;
                ModelRenderingPreferences.Apply(oldBackend, oldRate);
                DeviceModelView.DefaultFactoryOverride = oldFactory;
            }
        }) { IsBackground = true, Name = "HeyMelody native model settings" };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(40))) _state.Failure ??= "Focused native settings check exceeded forty seconds.";
        var report = new
        {
            success = _state.Failure is null && _state.Completed && _state.Closed && _state.Service.Disposals == 1,
            passed = _state.Checks, windowClosed = _state.Closed, serviceDisposals = _state.Service.Disposals,
            nativeBluetoothCalls = 0, applicationSetSubmissions = _state.Service.SetSubmissions,
            systemSettingsLaunches = 0, nativeRendererLoads = 0, recordingRendererCreations = _state.Factory.CreateCalls,
            scope = "focused real Win32 settings UI, native mouse/keyboard messages, themes, saved preferences and earbud glyph; managed recording renderer only; DWM attributes are observations, not assertions",
            captureScope = "real Win32 window and input; Avalonia scene pixels at the window's actual render scaling",
            states = _state.Receipts, failure = _state.Failure
        };
        var json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(Path.Combine(output, "native-model-settings-receipt.json"), json);
        Console.WriteLine(json);
        return report.success ? 0 : 1;
    }

    private sealed class State
    {
        internal string Output = "";
        internal readonly GuardService Service = new();
        internal readonly RecordingFilamentRendererFactory Factory = new();
        internal readonly List<object> Receipts = [];
        internal int Checks;
        internal bool Completed, Closed;
        internal string? Failure;
    }

    public sealed class SmokeApplication : App
    {
        public override void OnFrameworkInitializationCompleted()
        {
            var desktop = (IClassicDesktopStyleApplicationLifetime)ApplicationLifetime!;
            var window = new MainWindow(_state.Service, preview: true)
            { Width = 1000, Height = 740, StartWithDemo = true, ShowActivated = true, Topmost = true };
            window.FindControl<MainView>("Page")!.SetModelDemo();
            desktop.MainWindow = window;
            window.Closed += (_, _) => _state.Closed = true;
            window.Opened += (_, _) => _ = VerifyAsync(window, desktop);
            _ = WatchdogAsync(window, desktop);
        }
    }

    private static async Task VerifyAsync(MainWindow window, IClassicDesktopStyleApplicationLifetime desktop)
    {
        var page = window.FindControl<MainView>("Page")!;
        var vm = (MainViewModel)page.DataContext!;
        try
        {
            await UntilAsync(() => vm.IsDemoSelected && vm.HasVerifiedSnapshot, "The local demo initializes without a backend.");
            var model = page.GetVisualDescendants().OfType<DeviceModelView>().Single();
            await UntilAsync(() => model.ModelReady, "The managed recording renderer becomes ready.");
            Check(model.Diagnostics.ActualBackend == FilamentBackend.Vulkan, "The first model requests Vulkan explicitly.");
            foreach (var dark in new[] { false, true })
            {
                ((App)Application.Current!).ApplyTheme(dark ? "dark" : "light");
                await Task.Delay(200);
                Check(window.ActualThemeVariant == (dark ? ThemeVariant.Dark : ThemeVariant.Light), "The native content uses the requested theme.");
                var hwnd = window.TryGetPlatformHandle()!.Handle;
                var result = DwmGetWindowAttribute(hwnd, 20, out var immersiveDark, sizeof(int));
                var theme = dark ? "dark" : "light";
                var icon = page.GetVisualDescendants().OfType<Viewbox>().First(control => control.Name == "DeviceIcon");
                Check(DeviceBarTests.HasEarbudIcon(icon), "The native device bar uses the same earbud glyph as headphone settings.");
                Capture(window, theme + "-device-icon", (icon, "earbud"));
                var initialRate = ModelRenderingPreferences.FrameRate;
                var initialBackend = ModelRenderingPreferences.CompatibilityMode;
                var initialFile = File.ReadAllText(ThemePreferences.SettingsPathOverride!);
                var operation = page.OpenSettingsDialogAsync();
                var dialog = await SettingsAsync(window);
                var panel = (AppSettingsPanel)dialog.Content!;
                var slider = panel.FindControl<Slider>("ModelFrameRateSlider")!;
                CheckRows(panel);
                Capture(window, theme + "-settings", (panel.FindControl<FASettingsExpanderItem>("ModelCompatibilityCard")!, "api"),
                    (panel.FindControl<FASettingsExpanderItem>("ModelFrameRateCard")!, "rate"));
                for (var attempts = 0; slider.Value != 0 && attempts < 3; attempts++)
                    await ClickAsync(window, slider, TrackPoint(slider, 0));
                Check(slider.Value == 0 && panel.ModelFrameRate == ModelFrameRate.Fps30, "Native clicks select the fixed 30 fps stop.");
                slider.Focus();
                await KeyAsync(window, 0x27);
                Check(slider.Value == 1 && panel.ModelFrameRate == ModelFrameRate.Fps60, "A native right-arrow key selects 60 fps.");
                panel.FindControl<ComboBox>("ModelBackendChoice")!.SelectedIndex = initialBackend ? 0 : 1;
                Check(ModelRenderingPreferences.FrameRate == initialRate && ModelRenderingPreferences.CompatibilityMode == initialBackend
                    && File.ReadAllText(ThemePreferences.SettingsPathOverride!) == initialFile, "Native settings inputs only change the draft.");
                await ClickAsync(window, dialog.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, "取消")));
                await operation.WaitAsync(TimeSpan.FromSeconds(3));
                Check(ModelRenderingPreferences.FrameRate == initialRate && ModelRenderingPreferences.CompatibilityMode == initialBackend
                    && File.ReadAllText(ThemePreferences.SettingsPathOverride!) == initialFile, "Cancel preserves running and persisted preferences.");

                operation = page.OpenSettingsDialogAsync(); dialog = await SettingsAsync(window);
                panel = (AppSettingsPanel)dialog.Content!;
                slider = panel.FindControl<Slider>("ModelFrameRateSlider")!;
                slider.Value = dark ? 2 : 1;
                panel.FindControl<ComboBox>("ModelBackendChoice")!.SelectedIndex = dark ? 0 : 1;
                await ClickAsync(window, dialog.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, "保存")));
                await operation.WaitAsync(TimeSpan.FromSeconds(3));
                await UntilAsync(() => model.ModelReady && model.Diagnostics.ActualBackend == ModelRenderingPreferences.Backend,
                    "Saved backend choices apply to the existing native-window model.");
                using var saved = JsonDocument.Parse(File.ReadAllText(ThemePreferences.SettingsPathOverride!));
                Check(ModelRenderingPreferences.FrameRate == (dark ? ModelFrameRate.Unlimited : ModelFrameRate.Fps60)
                    && ModelRenderingPreferences.CompatibilityMode == !dark
                    && saved.RootElement.GetProperty("modelFrameRate").GetString() == (dark ? "unlimited" : "60")
                    && saved.RootElement.GetProperty("unrelated").GetInt32() == 17, "Save immediately applies canonical values and preserves unrelated settings.");
                _state.Receipts.Add(new { theme, renderScaling = window.RenderScaling, clientWidth = window.ClientSize.Width,
                    clientHeight = window.ClientSize.Height, dwmDarkResult = result, dwmImmersiveDark = result >= 0 ? (int?)immersiveDark : null,
                    savedBackend = ModelRenderingPreferences.Backend.ToString(),
                    selectedRate = ThemePreferences.Serialize(ModelRenderingPreferences.FrameRate),
                    framesPerSecond = ModelRenderingPreferences.FrameRate == ModelFrameRate.Unlimited ? (int?)null
                        : (int)ModelRenderingPreferences.FrameRate });
            }
            Check(_state.Service.SetSubmissions == 0 && _state.Service.ForbiddenCalls == 0,
                "The focused UI check starts no real backend, protocol write or system-settings action.");
            _state.Completed = true;
        }
        catch (Exception error) { _state.Failure ??= error.ToString(); }
        finally
        {
            await page.DismissSettingsDialogAsync(); window.Close();
            await Task.Delay(100); desktop.Shutdown(_state.Failure is null ? 0 : 1);
        }
    }

    private static void CheckRows(AppSettingsPanel panel)
    {
        var api = panel.FindControl<FASettingsExpanderItem>("ModelCompatibilityCard")!;
        var rate = panel.FindControl<FASettingsExpanderItem>("ModelFrameRateCard")!;
        var slider = panel.FindControl<Slider>("ModelFrameRateSlider")!;
        Check(Equals(api.Content, "3D模型渲染API") && Equals(api.Description, "若存在渲染问题，请尝试切换到 OpenGL")
            && Equals(rate.Content, "3D模型渲染帧率") && rate.Description is null or "", "Both native settings rows use the exact requested text.");
        Check(slider.Minimum == 0 && slider.Maximum == 2 && slider.SmallChange == 1 && slider.LargeChange == 1
            && slider.IsSnapToTickEnabled && slider.Ticks is { } ticks && ticks.SequenceEqual(new[] { 0d, 1d, 2d })
            && slider.GetVisualDescendants().OfType<TickBar>().Any(control => control.IsEffectivelyVisible), "The native slider has three visible snapping ticks.");
        var labels = new[] { "30帧", "60帧", "无限制" };
        var selectedLabel = panel.ModelFrameRate switch
        { ModelFrameRate.Fps30 => "30帧", ModelFrameRate.Fps60 => "60帧", _ => "无限制" };
        Check(Equals(ToolTip.GetTip(slider), selectedLabel)
            && Avalonia.Automation.AutomationProperties.GetHelpText(slider) == selectedLabel,
            "The native slider tooltip and accessible current choice use the readable label.");
        foreach (var text in new[] { "3D模型渲染API", "若存在渲染问题，请尝试切换到 OpenGL", "3D模型渲染帧率" }.Concat(labels))
        {
            var block = panel.GetVisualDescendants().OfType<TextBlock>().First(control => control.Text == text);
            Check(block.IsEffectivelyVisible && !block.TextLayout.TextLines.Any(line => line.HasCollapsed)
                && block.TextLayout.Width <= block.Bounds.Width + .5 && block.TextLayout.Height <= block.Bounds.Height + .5,
                "Changed native settings text is visible without ellipsis or clipping.");
        }
        var editor = panel.FindControl<Grid>("ModelFrameRateEditor")!;
        for (var index = 0; index < labels.Length; index++)
        {
            var label = panel.GetVisualDescendants().OfType<TextBlock>().Single(control => control.Text == labels[index]);
            var at = label.TranslatePoint(default, editor)!.Value;
            var stop = slider.TranslatePoint(TrackPoint(slider, index), editor)!.Value;
            Check(Math.Abs(at.X + label.Bounds.Width / 2 - stop.X) <= 1 && at.X >= 0
                && at.X + label.Bounds.Width <= editor.Bounds.Width + .5, "Native tick labels align with their three slider stops.");
        }
    }

    private static Point TrackPoint(Slider slider, double index)
    {
        var track = slider.GetVisualDescendants().OfType<Track>().Single();
        var half = track.Thumb!.Bounds.Width / 2;
        return track.TranslatePoint(new Point(half + (track.Bounds.Width - half * 2) * index / 2, track.Bounds.Height / 2), slider)!.Value;
    }

    private static async Task ClickAsync(Window window, Control control, Point? local = null)
    {
        control.BringIntoView(); await Task.Delay(40);
        var point = NativeWindowCapture.ClientPoint(window.TryGetPlatformHandle()!.Handle,
            control.PointToScreen(local ?? new Point(control.Bounds.Width / 2, control.Bounds.Height / 2)));
        var hwnd = window.TryGetPlatformHandle()!.Handle;
        NativeWindowCapture.PointerMessage(hwnd, 0x0200, point.X, point.Y, false); await Task.Delay(30);
        NativeWindowCapture.PointerMessage(hwnd, 0x0201, point.X, point.Y, true); await Task.Delay(30);
        NativeWindowCapture.PointerMessage(hwnd, 0x0202, point.X, point.Y, false); await Task.Delay(50);
    }

    private static async Task KeyAsync(Window window, int key)
    {
        var hwnd = window.TryGetPlatformHandle()!.Handle;
        if (!PostMessage(hwnd, 0x0100, new IntPtr(key), new IntPtr(1))
            || !PostMessage(hwnd, 0x0101, new IntPtr(key), new IntPtr(0xC0000001L)))
            throw new InvalidOperationException("Native keyboard message failed.");
        await Task.Delay(60);
    }

    private static async Task<FAContentDialog> SettingsAsync(Window window)
    {
        await UntilAsync(() => window.GetVisualDescendants().OfType<FAContentDialog>().Any(dialog => Equals(dialog.Title, "应用设置")),
            "The real settings dialog opens.");
        await Task.Delay(300);
        return window.GetVisualDescendants().OfType<FAContentDialog>().Single(dialog => Equals(dialog.Title, "应用设置"));
    }

    private static void Capture(Window window, string name, params (Control Control, string Name)[] controls)
    {
        var path = Path.Combine(_state.Output, name + "-full.png");
        var scale = window.RenderScaling;
        using (var scene = new RenderTargetBitmap(new PixelSize((int)Math.Ceiling(window.Bounds.Width * scale),
                   (int)Math.Ceiling(window.Bounds.Height * scale)), new Vector(96 * scale, 96 * scale)))
        {
            scene.Render(window);
            using var full = new FileStream(path, FileMode.CreateNew);
            scene.Save(full, Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
        }
        using var bitmap = SKBitmap.Decode(path);
        foreach (var (control, suffix) in controls)
        {
            var at = control.TranslatePoint(default, window) ?? throw new InvalidOperationException("Affected control is detached.");
            var rect = new SKRectI((int)Math.Floor(at.X * scale), (int)Math.Floor(at.Y * scale),
                (int)Math.Ceiling((at.X + control.Bounds.Width) * scale),
                (int)Math.Ceiling((at.Y + control.Bounds.Height) * scale));
            using var crop = new SKBitmap();
            Check(rect.Left >= 0 && rect.Top >= 0 && rect.Right <= bitmap.Width && rect.Bottom <= bitmap.Height
                && bitmap.ExtractSubset(crop, rect), "The affected native control fits the captured Avalonia scene.");
            using var data = crop.Encode(SKEncodedImageFormat.Png, 100);
            using var file = File.Create(Path.Combine(_state.Output, name + "-" + suffix + ".png"));
            data.SaveTo(file);
        }
    }

    private static async Task UntilAsync(Func<bool> condition, string reason)
    {
        for (var i = 0; i < 150 && !condition(); i++) await Task.Delay(20);
        Check(condition(), reason);
    }
    private static async Task WatchdogAsync(Window window, IClassicDesktopStyleApplicationLifetime desktop)
    {
        await Task.Delay(TimeSpan.FromSeconds(30));
        if (_state.Closed) return;
        _state.Failure ??= "Focused native settings window exceeded thirty seconds.";
        window.Close(); desktop.Shutdown(1);
    }
    private static void Check(bool condition, string reason)
    { if (!condition) throw new InvalidOperationException(reason); _state.Checks++; }

    private sealed class GuardService : IHeadsetService
    {
        internal int Disposals, SetSubmissions, ForbiddenCalls;
        public Task<IReadOnlyList<UiDevice>> EnumerateAsync(CancellationToken token) => Task.FromResult<IReadOnlyList<UiDevice>>([]);
        public Task<UiSnapshot> ConnectAsync(string id, CancellationToken token) { ForbiddenCalls++; throw new InvalidOperationException("Real connections are disabled."); }
        public Task DisconnectAsync(CancellationToken token) => Task.CompletedTask;
        public Task<UiSnapshot> RefreshAsync(CancellationToken token) { ForbiddenCalls++; throw new InvalidOperationException("Real device reads are disabled."); }
        public Task<UiApplyResult> ApplyAsync(UiAction action, CancellationToken token) { SetSubmissions++; throw new InvalidOperationException("Protocol writes are disabled."); }
        public ValueTask DisposeAsync() { Disposals++; return ValueTask.CompletedTask; }
    }
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool PostMessage(IntPtr hwnd, uint message, IntPtr wparam, IntPtr lparam);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int value, int size);
}
