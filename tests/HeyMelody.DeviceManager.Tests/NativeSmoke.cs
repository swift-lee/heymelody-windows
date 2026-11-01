using System.Runtime.InteropServices;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Chrome;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using HeyMelodyWindows.App;
using HeyMelodyWindows.DeviceManager;

/// <summary>Real Win32/DWM verification with an in-memory catalog. Never creates a ClientBridge.</summary>
internal static class NativeSmoke
{
    private static SmokeState _state = new();

    public static int Run(bool closeWithAltF4 = false, bool? basicRenderMode = null, string? outputDirectory = null)
    {
        if (!OperatingSystem.IsWindows()) return 2;
        _state = new() { CloseWithAltF4 = closeWithAltF4, BasicRenderMode = basicRenderMode,
            OutputOverride = outputDirectory is null ? null : Path.GetFullPath(outputDirectory) };
        var uiThread = new Thread(() =>
        {
            try
            {
                var builder = AppBuilder.Configure<SmokeApplication>().UsePlatformDetect();
                if (basicRenderMode is not null)
                    builder = HeyMelodyWindows.Ui.RenderingSetup.UseHeyMelodyRendering(builder, basicRenderMode.Value);
                _state.ExitCode = builder.LogToTrace().StartWithClassicDesktopLifetime([], ShutdownMode.OnExplicitShutdown);
            }
            catch (Exception error) { _state.Failure = error.ToString(); }
        }) { IsBackground = true, Name = "Device manager native smoke UI" };
        uiThread.SetApartmentState(ApartmentState.STA);
        uiThread.Start();
        if (!uiThread.Join(TimeSpan.FromSeconds(25))) _state.Failure ??= "Native UI thread did not finish in twenty-five seconds.";
        var success = _state.Failure is null && _state.Closed && (_state.CloseButtonClicked || _state.AltF4Sent) && _state.ExitCode == 1
            && _state.Service.Disposals == 1 && _state.Service.Saves == 0 && _state.Receipts.Count == 4;
        var report = JsonSerializer.Serialize(new
        {
            schemaVersion = 1, success, passed = _state.Checks, nativeBluetoothCalls = 0,
            whitelistWrites = _state.Service.Saves, serviceDisposals = _state.Service.Disposals,
            windowClosed = _state.Closed, closeButtonClicked = _state.CloseButtonClicked, altF4Sent = _state.AltF4Sent,
            applicationExitCode = _state.ExitCode,
            requestedBasicRenderMode = _state.BasicRenderMode, rendering = _state.Probe?.Receipt,
            operatingSystem = Environment.OSVersion.Version.ToString(),
            scope = "Real Win32 window and DWM attributes, compositor screenshots, both themes and sizes; in-memory paired catalog only. No Bluetooth or user configuration writes.",
            states = _state.Receipts, failure = _state.Failure
        }, new JsonSerializerOptions { WriteIndented = true });
        Directory.CreateDirectory(_state.Output);
        File.WriteAllText(Path.Combine(_state.Output, "report.json"), report);
        Console.WriteLine(report);
        return success ? 0 : 1;
    }

    private sealed class SmokeState
    {
        public FakeService Service { get; } = new()
        {
            Catalog = new(Enumerable.Range(0, 12).Select(index => new UiDeviceCandidate("native-" + index,
                index == 0 ? "OPPO Enco X4" : index == 1 ? "OPPO Enco Air4 Pro" : index == 2 ? "OnePlus Buds Pro 3" : "已配对的耳机 " + index,
                index % 3 == 0, index == 0)).ToArray())
        };
        public bool? BasicRenderMode { get; init; }
        public NativeRenderingProbe? Probe { get; set; }
        public string? OutputOverride { get; init; }
        public string Output => OutputOverride ?? Path.Combine(Directory.GetCurrentDirectory(), "artifacts",
            CloseWithAltF4 ? "device-manager-native-063-alt-f4" : "device-manager-native-063");
        public List<object> Receipts { get; } = [];
        public int Checks { get; set; }
        public int ExitCode { get; set; } = -1;
        public bool Closed { get; set; }
        public bool CloseButtonClicked { get; set; }
        public bool CloseWithAltF4 { get; init; }
        public bool AltF4Sent { get; set; }
        public string? Failure { get; set; }
    }

    // Reuse the actual manager theme but replace its framework initialization so
    // this runner cannot create a Bluetooth worker or read the real selection.
    public sealed class SmokeApplication : DeviceManagerApp
    {
        public override void OnFrameworkInitializationCompleted()
        {
            RequestedThemeVariant = ThemeVariant.Light;
            var desktop = ApplicationLifetime as IClassicDesktopStyleApplicationLifetime
                ?? throw new InvalidOperationException("Desktop lifetime required.");
            var window = new DeviceManagerWindow(_state.Service, preview: true) { Topmost = true };
            if (_state.BasicRenderMode is not null) _state.Probe = NativeRenderingProbe.Attach(window);
            desktop.MainWindow = window;
            window.Closed += (_, _) => _state.Closed = true;
            window.Opened += (_, _) => _ = VerifyAndCloseAsync(window);
            _ = StopOnTimeoutAsync(window, desktop);
        }
    }

    private static async Task VerifyAndCloseAsync(DeviceManagerWindow window)
    {
        try
        {
            var view = window.FindControl<DeviceManagerView>("Page") ?? throw new InvalidOperationException("Manager view missing.");
            var vm = view.DataContext as DeviceManagerViewModel ?? throw new InvalidOperationException("View model missing.");
            for (int attempt = 0; vm.Devices.Count == 0 && attempt < 40; attempt++) await Task.Delay(50);
            Check(vm.Devices.Count == 12 && !vm.Busy && vm.ExitCode == 1, "The mock catalog initializes without a write.");
            var hwnd = window.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
            Check(hwnd != IntPtr.Zero, "Real native HWND exists.");
            if (_state.Probe is { } probe)
            {
                for (int attempt = 0; !probe.Queried && attempt < 80; attempt++) await Task.Delay(50);
                Check(probe.Queried && probe.HasGpuContext && !string.IsNullOrWhiteSpace(probe.Renderer),
                    "The manager compositor exposes its actual ANGLE renderer.");
                Check(_state.BasicRenderMode == probe.Renderer!.Contains("Microsoft Basic Render Driver", StringComparison.OrdinalIgnoreCase),
                    "The manager renderer matches the requested GPU policy.");
            }
            var style = IntPtr.Size == 8 ? GetWindowLongPtr(hwnd, -16).ToInt64() : GetWindowLong(hwnd, -16);
            Check(window.ShowAsDialog && window.CanResize && !window.CanMinimize && !window.CanMaximize,
                "The dialog exposes only Close while allowing resizing.");
            Check((style & 0x30000) == 0 && (style & 0x40000) != 0,
                "Native window styles remove minimize/maximize and retain the resizable frame.");
            Directory.CreateDirectory(_state.Output);
            foreach (var dark in new[] { false, true })
            foreach (var size in new[] { new Size(520, 720), new Size(400, 480) })
            {
                Application.Current!.RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light;
                window.Width = size.Width; window.Height = size.Height;
                window.Activate();
                await Task.Delay(600);
                Dispatcher.UIThread.RunJobs();
                Check(window.ActualThemeVariant == (dark ? ThemeVariant.Dark : ThemeVariant.Light), "Native window uses the requested theme.");
                var host = window.GetVisualParent() ?? throw new InvalidOperationException("The real window visual host is missing.");
                var clientCaptionButtons = host.GetVisualDescendants()
                    .Where(visual => WindowDecorationProperties.GetElementRole(visual) == WindowDecorationsElementRole.CloseButton
                        && IsRendered(visual)).ToArray();
                Check(clientCaptionButtons.Length == 1,
                    "Exactly one managed client Close button is visible in the window host. Buttons: " + string.Join(", ",
                        host.GetVisualDescendants().OfType<Button>().Select(button => $"{button.GetType().Name}/{button.Name}/{button.IsEffectivelyVisible}")));
                var clientOtherCaptionButtons = host.GetVisualDescendants()
                    .Where(visual => WindowDecorationProperties.GetElementRole(visual) is WindowDecorationsElementRole.MinimizeButton
                        or WindowDecorationsElementRole.MaximizeButton).ToArray();
                Check(clientOtherCaptionButtons.All(visual => !IsRendered(visual)),
                    "Managed client minimize/maximize buttons remain hidden. Buttons: " + string.Join(", ",
                        host.GetVisualDescendants().OfType<Button>().Select(button => $"{button.GetType().Name}/{button.Name}/{button.IsEffectivelyVisible}/{button.Bounds}")));
                var caption = ProbeCaption(hwnd);
                Check(caption.CloseRegions == 1 && caption.ClosePoint != IntPtr.Zero,
                    "The real Win32 titlebar has exactly one contiguous Close hit target.");
                Check(!caption.HasMinimizeOrMaximize,
                    "The real Win32 titlebar has no minimize/maximize hit targets.");
                Check(GetWindowRect(hwnd, out var nativeBounds), "The native window bounds remain readable.");
                var drag = new IntPtr((nativeBounds.Top + 16) << 16 | ((nativeBounds.Left + 140) & 0xFFFF));
                var resize = new IntPtr((nativeBounds.Bottom - 2) << 16 | ((nativeBounds.Right - 2) & 0xFFFF));
                Check(SendMessage(hwnd, 0x0084, IntPtr.Zero, drag).ToInt32() == 2,
                    "The custom titlebar remains a real Win32 dragging target.");
                Check(SendMessage(hwnd, 0x0084, IntPtr.Zero, resize).ToInt32() == 17,
                    "The bottom right corner remains a real Win32 resizing target.");
                int backdrop = -1, immersiveDark = -1, nativeNonClientRendering = -1;
                var backdropResult = DwmGetWindowAttribute(hwnd, 38, out backdrop, sizeof(int));
                var darkResult = DwmGetWindowAttribute(hwnd, 20, out immersiveDark, sizeof(int));
                var nonClientResult = DwmGetWindowAttribute(hwnd, 1, out nativeNonClientRendering, sizeof(int));
                if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22621))
                {
                    var stateStyle = IntPtr.Size == 8 ? GetWindowLongPtr(hwnd, -16).ToInt64() : GetWindowLong(hwnd, -16);
                    Check((stateStyle & 0x80000) == 0 && (stateStyle & 0x40000) != 0,
                        "Native system caption buttons remain suppressed without losing the resizable frame.");
                    Check((stateStyle & 0xC80000) != 0xC80000,
                        "Native caption button styles cannot draw a second Close over the managed button.");
                    Check(nonClientResult >= 0 && nativeNonClientRendering == 1,
                        "DWM non-client rendering remains enabled to preserve native material and window framing.");
                    Check(backdropResult >= 0 && backdrop == 4, "Windows accepts Mica Alt (DWMSBT_TABBEDWINDOW)." );
                    Check(darkResult >= 0 && immersiveDark == (dark ? 1 : 0), "DWM dark mode matches the application theme.");
                    Check(window.Background == Brushes.Transparent && window.TitleBar.BackgroundColor == Colors.Transparent,
                        "The client and titlebar allow the native material to show.");
                }
                var overlay = view.FindControl<Border>("ActionOverlay")!;
                var origin = overlay.TranslatePoint(default, view)!.Value;
                Check(overlay.Bounds.Height == 72 && Math.Abs(origin.Y + overlay.Bounds.Height - view.Bounds.Height) <= 1,
                    "Footer overlay remains 72 pixels tall and attached to the bottom.");
                var scroll = view.FindControl<ScrollViewer>("DeviceScroll")!;
                scroll.Offset = new Vector(0, scroll.Extent.Height);
                await Task.Delay(50); Dispatcher.UIThread.RunJobs();
                var rows = view.FindControl<ItemsControl>("DeviceList")!.GetVisualDescendants().OfType<CheckBox>().ToArray();
                Check(rows.Length == 12 && rows.All(row => row.Bounds.Height == 72), "Native rows retain their 72 pixel height.");
                var lastBottom = rows[^1].TranslatePoint(new Point(0, rows[^1].Bounds.Height), view)!.Value.Y;
                Check(lastBottom <= origin.Y + 1, "The final device can scroll fully above the overlay.");
                scroll.Offset = default;
                await Task.Delay(150);
                var screenshot = Path.Combine(_state.Output, $"{(dark ? "dark" : "light")}-{size.Width}x{size.Height}.png");
                CaptureCompositor(hwnd, screenshot);
                _state.Receipts.Add(new
                {
                    theme = dark ? "dark" : "light", requestedWidth = size.Width, requestedHeight = size.Height,
                    clientWidth = window.ClientSize.Width, clientHeight = window.ClientSize.Height,
                    contentWidth = view.Bounds.Width, contentHeight = view.Bounds.Height,
                    footerHeight = overlay.Bounds.Height, backdropType = backdrop, immersiveDarkMode = immersiveDark,
                    nativeNonClientRendering = nativeNonClientRendering != 0,
                    visibleClientCloseButtons = clientCaptionButtons.Length,
                    closeHitRegions = caption.CloseRegions,
                    onlyClose = true, canResize = true, screenshot
                });
            }
            Check(_state.Service.Enumerations == 1 && _state.Service.Saves == 0,
                "Theme changes and resizing never enumerate or save devices.");
            var close = ProbeCaption(hwnd);
            Check(close.CloseRegions == 1 && close.ClosePoint != IntPtr.Zero, "A native Close target is available for the final click.");
            if (_state.CloseWithAltF4)
            {
                _state.AltF4Sent = PostMessage(hwnd, 0x0104, new IntPtr(0x73), new IntPtr(1L | (1L << 29)))
                    && PostMessage(hwnd, 0x0105, new IntPtr(0x73), new IntPtr(1L | (1L << 29) | (1L << 30) | (1L << 31)));
                Check(_state.AltF4Sent, "Alt+F4 native key messages are delivered only to this mock window.");
            }
            else
            {
                _state.CloseButtonClicked = PostMessage(hwnd, 0x00A1, new IntPtr(20), close.ClosePoint)
                    && PostMessage(hwnd, 0x00A2, new IntPtr(20), close.ClosePoint);
                Check(_state.CloseButtonClicked, "Both native Close pointer messages are delivered to this mock window.");
            }
        }
        catch (Exception error) { _state.Failure = error.ToString(); }
        finally { if (!_state.CloseButtonClicked && !_state.AltF4Sent) window.Close(); }
    }

    private static async Task StopOnTimeoutAsync(Window window, IClassicDesktopStyleApplicationLifetime desktop)
    {
        await Task.Delay(TimeSpan.FromSeconds(18));
        if (_state.Closed) return;
        _state.Failure ??= "Native smoke exceeded eighteen seconds.";
        window.Close(); desktop.Shutdown(1);
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
        _state.Checks++;
    }

    private static bool IsRendered(Visual visual) => visual.IsEffectivelyVisible && visual.Bounds.Width > 0 && visual.Bounds.Height > 0;

    private static (int CloseRegions, bool HasMinimizeOrMaximize, IntPtr ClosePoint) ProbeCaption(IntPtr hwnd)
    {
        if (!GetWindowRect(hwnd, out var rect)) throw new InvalidOperationException("Cannot read native caption bounds.");
        var closeRegions = 0;
        var inCloseRegion = false;
        var hasMinimizeOrMaximize = false;
        var closePoint = IntPtr.Zero;
        // Real HWND hit testing avoids depending on chrome template names; Avalonia's
        // Windows decorations are not descendants of the app's content visual tree.
        for (int x = rect.Left + 12; x < rect.Right - 8; x += 2)
        {
            var point = new IntPtr((rect.Top + 16) << 16 | (x & 0xFFFF));
            var hit = SendMessage(hwnd, 0x0084, IntPtr.Zero, point).ToInt32();
            if (hit == 20)
            {
                if (!inCloseRegion) closeRegions++;
                inCloseRegion = true;
                closePoint = point;
            }
            else inCloseRegion = false;
            if (hit is 8 or 9) hasMinimizeOrMaximize = true;
        }
        return (closeRegions, hasMinimizeOrMaximize, closePoint);
    }

    // Capture the displayed compositor output instead of Avalonia's render target,
    // which cannot contain the system material behind a transparent client surface.
    private static void CaptureCompositor(IntPtr hwnd, string output)
    {
        if (!GetWindowRect(hwnd, out var rect)) throw new InvalidOperationException("Cannot read native window bounds.");
        var width = rect.Right - rect.Left; var height = rect.Bottom - rect.Top;
        var screen = GetDC(IntPtr.Zero);
        var memory = CreateCompatibleDC(screen);
        var info = new BitmapInfo
        {
            Header = new BitmapInfoHeader { Size = 40, Width = width, Height = -height, Planes = 1, BitCount = 32 }
        };
        var dib = CreateDIBSection(screen, ref info, 0, out var pixels, IntPtr.Zero, 0);
        if (screen == IntPtr.Zero || memory == IntPtr.Zero || dib == IntPtr.Zero || pixels == IntPtr.Zero)
            throw new InvalidOperationException("Cannot allocate native screenshot surface.");
        var previous = SelectObject(memory, dib);
        try
        {
            if (!BitBlt(memory, 0, 0, width, height, screen, rect.Left, rect.Top, 0x00CC0020))
                throw new InvalidOperationException("Cannot capture compositor output.");
            using var bitmap = new Bitmap(PixelFormat.Bgra8888, AlphaFormat.Opaque, pixels,
                new PixelSize(width, height), new Vector(96, 96), width * 4);
            bitmap.Save(output, PngBitmapEncoderOptions.Default);
        }
        finally { SelectObject(memory, previous); DeleteObject(dib); DeleteDC(memory); ReleaseDC(IntPtr.Zero, screen); }
    }

    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct BitmapInfoHeader
    {
        public uint Size; public int Width, Height; public ushort Planes, BitCount;
        public uint Compression, SizeImage; public int XPelsPerMeter, YPelsPerMeter; public uint ColorsUsed, ColorsImportant;
    }
    [StructLayout(LayoutKind.Sequential)] private struct BitmapInfo { public BitmapInfoHeader Header; public uint Colors; }
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int value, int size);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] private static extern int GetWindowLong(IntPtr hwnd, int index);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);
    [DllImport("user32.dll", EntryPoint = "SendMessageW")] private static extern IntPtr SendMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", EntryPoint = "PostMessageW")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool PostMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateDIBSection(IntPtr dc, ref BitmapInfo info, uint usage, out IntPtr bits, IntPtr section, uint offset);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool BitBlt(IntPtr dc, int x, int y, int width, int height, IntPtr source, int sourceX, int sourceY, uint operation);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeleteDC(IntPtr dc);
}
