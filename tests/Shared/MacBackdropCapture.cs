using System.Diagnostics;
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.VisualTree;

namespace HeyMelody.TestSupport;

/// <summary>Opt-in native compositor capture. Only small changed background crops are reviewed.</summary>
internal static class MacBackdropCapture
{
    private const string ObjC = "/usr/lib/libobjc.A.dylib";
    [DllImport(ObjC)] private static extern IntPtr objc_getClass(string name);
    [DllImport(ObjC)] private static extern IntPtr sel_registerName(string name);
    [DllImport(ObjC)] private static extern IntPtr object_getClassName(IntPtr value);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr Send(IntPtr value, IntPtr selector);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr At(IntPtr value, IntPtr selector, ulong index);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern long Number(IntPtr value, IntPtr selector);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool IsKind(IntPtr value, IntPtr selector, IntPtr type);
    internal static int WindowCaptures { get; private set; }
    internal static async Task CapturePickerAsync(Window window, Control picker)
    {
        if (!OperatingSystem.IsMacOS() || Environment.GetEnvironmentVariable("HEYMELODY_CAPTURE_BACKDROP") != "1") return;
        var handle = window.TryGetPlatformHandle()!.Handle;
        if (!IsKind(handle, sel_registerName("isKindOfClass:"), objc_getClass("NSWindow")))
            handle = Send(handle, sel_registerName("window"));
        long number = Number(handle, sel_registerName("windowNumber"));
        var directory = Path.Combine(Environment.CurrentDirectory, "artifacts", "macos", "backdrop");
        Directory.CreateDirectory(directory);
        var initialTheme = window.RequestedThemeVariant;
        try
        {
            foreach (var theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
            {
                window.RequestedThemeVariant = theme; await Task.Delay(150);
                // Theme changes rebuild the calibration body; resolve the current selector after that rebuild.
                var currentPicker = window.GetVisualDescendants().OfType<Control>().Single(x => x.Name == picker.Name);
                currentPicker.BringIntoView(); await Task.Delay(150);
                string name = "preview-output-" + theme.Key!.ToString()!.ToLowerInvariant();
                string whole = Path.Combine(directory, name + "-window.png"), crop = Path.Combine(directory, name + "-crop.png");
                await ExecuteAsync("/usr/sbin/screencapture", ["-x", "-o", "-l", number.ToString(), whole]);
                WindowCaptures++;
                using var source = File.OpenRead(whole); var header = new byte[24]; source.ReadExactly(header);
                int imageWidth = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(16, 4));
                int imageHeight = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(20, 4));
                double scale = imageWidth / window.ClientSize.Width;
                var point = currentPicker.TranslatePoint(default, window)!.Value;
                int left = Math.Max(0, (int)((point.X - 8) * scale));
                int top = Math.Max(0, imageHeight - (int)(window.ClientSize.Height * scale) + (int)((point.Y - 34) * scale));
                int width = Math.Min(imageWidth - left, (int)((currentPicker.Bounds.Width + 96) * scale));
                int height = Math.Min(imageHeight - top, (int)(180 * scale));
                await ExecuteAsync("/usr/bin/sips", ["-c", height.ToString(), width.ToString(), "--cropOffset", top.ToString(), left.ToString(), whole, "--out", crop]);
            }
        }
        finally { window.RequestedThemeVariant = initialTheme; }
    }
    internal static async Task CaptureAsync(Window window, string label)
    {
        if (!OperatingSystem.IsMacOS() || Environment.GetEnvironmentVariable("HEYMELODY_CAPTURE_BACKDROP") != "1") return;
        var platform = window.TryGetPlatformHandle() ?? throw new InvalidOperationException("A native window is required.");
        if (platform.HandleDescriptor is not ("NSWindow" or "NSView"))
            throw new InvalidOperationException("Unexpected native window handle: " + platform.HandleDescriptor);
        var nativeWindow = platform.Handle;
        if (!IsKind(nativeWindow, sel_registerName("isKindOfClass:"), objc_getClass("NSWindow")))
            nativeWindow = Send(nativeWindow, sel_registerName("window"));
        long number = Number(nativeWindow, sel_registerName("windowNumber"));
        if (number <= 0) throw new InvalidOperationException("The native window does not have a compositor identifier.");
        var directory = Path.Combine(Environment.CurrentDirectory, "artifacts", "macos", "backdrop");
        Directory.CreateDirectory(directory);
        var position = window.Position;
        var theme = window.RequestedThemeVariant;
        var pattern = new Grid { ColumnDefinitions = new("*,*,*,*,*,*"), RowDefinitions = new("*,*,*,*,*,*") };
        for (int y = 0; y < 6; y++) for (int x = 0; x < 6; x++)
        {
            var tile = new Border { Background = (x + y) % 2 == 0 ? Brushes.CornflowerBlue : Brushes.IndianRed };
            Grid.SetRow(tile, y); Grid.SetColumn(tile, x); pattern.Children.Add(tile);
        }
        var behind = new Window { Width = window.Width + 80, Height = window.Height + 80, Content = pattern,
            WindowStartupLocation = WindowStartupLocation.Manual, Position = new PixelPoint(100, 100), Title = "Backdrop verification pattern" };
        try
        {
            behind.Show(); window.Position = new PixelPoint(120, 120); window.Activate();
            foreach (var variant in new[] { ThemeVariant.Light, ThemeVariant.Dark })
            {
                window.RequestedThemeVariant = variant; await Task.Delay(300);
                var effects = new List<object>();
                Inspect(Send(nativeWindow, sel_registerName("contentView")), effects, 0);
                if (effects.Count == 0) throw new InvalidOperationException("No native NSVisualEffectView was installed for the requested blur.");
                string name = label + "-" + variant.Key!.ToString()!.ToLowerInvariant();
                string whole = Path.Combine(directory, name + "-window.png"), crop = Path.Combine(directory, name + "-backdrop-crop.png");
                await ExecuteAsync("/usr/sbin/screencapture", ["-x", "-o", "-l", number.ToString(), whole]);
                WindowCaptures++;
                await ExecuteAsync("/usr/bin/sips", ["-c", "180", "440", "--cropOffset", "120", "24", whole, "--out", crop]);
                File.WriteAllText(Path.Combine(directory, name + ".json"), JsonSerializer.Serialize(new
                {
                    actualTransparency = window.ActualTransparencyLevel.ToString(), effects,
                    tint = (window.Background as ISolidColorBrush)?.Color.ToString(), crop,
                    scope = "Native macOS compositor background and title/header region only; fake device catalog, no Bluetooth or audio."
                }));
            }
        }
        finally { behind.Close(); window.Position = position; window.RequestedThemeVariant = theme; }
    }
    private static void Inspect(IntPtr view, List<object> result, int depth)
    {
        if (view == IntPtr.Zero || depth > 12) return;
        if (IsKind(view, sel_registerName("isKindOfClass:"), objc_getClass("NSVisualEffectView")))
            result.Add(new { type = Marshal.PtrToStringUTF8(object_getClassName(view)), material = Number(view, sel_registerName("material")),
                state = Number(view, sel_registerName("state")), blendingMode = Number(view, sel_registerName("blendingMode")) });
        var children = Send(view, sel_registerName("subviews"));
        long count = Number(children, sel_registerName("count"));
        for (ulong index = 0; index < (ulong)Math.Min(count, 128); index++) Inspect(At(children, sel_registerName("objectAtIndex:"), index), result, depth + 1);
    }
    private static async Task ExecuteAsync(string executable, string[] arguments)
    {
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new IOException("Native capture tool did not start.");
        var errors = process.StandardError.ReadToEndAsync(); var output = process.StandardOutput.ReadToEndAsync();
        try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)); }
        catch { if (!process.HasExited) process.Kill(); throw; }
        await output;
        if (process.ExitCode != 0) throw new IOException("Native background capture failed: " + (await errors)[..Math.Min((await errors).Length, 500)]);
    }
}
