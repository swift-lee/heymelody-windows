#if HEYMELODY_WINDOWS
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using FluentAvalonia.UI.Windowing;

namespace HeyMelodyWindows.Windowing;

/// <summary>Uses the documented Windows 11 Mica Alt backdrop; older systems remain opaque.</summary>
/// <remarks>DeviceManager owns this source; Calibration.UI compiles the same file so both windows share one backdrop.</remarks>
internal sealed class MicaAltBackdrop : IDisposable
{
    private readonly FAAppWindow _window;
    private bool _opened, _disposed, _queued;
    private readonly Win32Properties.CustomWndProcHookCallback _hook;
    private readonly Win32Properties.CustomWindowStylesCallback _styles;
    internal bool Applied { get; private set; }
    public MicaAltBackdrop(FAAppWindow window)
    {
        _window = window; _hook = WindowMessage; _styles = RemoveNativeCaptionButtons;
        SetFallback();
        window.Opened += OnOpened;
        window.ActualThemeVariantChanged += OnThemeChanged;
        if (OperatingSystem.IsWindows())
        {
            Win32Properties.AddWndProcHookCallback(window, _hook);
            Win32Properties.AddWindowStylesCallback(window, _styles);
        }
    }
    private static (uint style, uint exStyle) RemoveNativeCaptionButtons(uint style, uint exStyle) =>
        // Avalonia supplies the Close button. Remove the native caption buttons
        // underneath it while retaining the DWM frame, backdrop and resize border.
        (style & ~0x00080000u, exStyle); // WS_SYSMENU
    private void OnOpened(object? sender, EventArgs e) { _opened = true; ScheduleApply(); }
    private void OnThemeChanged(object? sender, EventArgs e) => ScheduleApply();
    private IntPtr WindowMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message is 0x031E or 0x031A or 0x001A) ScheduleApply(); // composition, theme, settings changes
        return IntPtr.Zero;
    }
    private void ScheduleApply()
    {
        if (!_opened || _disposed || _queued) return;
        _queued = true;
        Dispatcher.UIThread.Post(() => { _queued = false; if (!_disposed) Apply(); });
    }
    private void Apply()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22621)
            || _window.TryGetPlatformHandle() is not { Handle: var hwnd } || hwnd == IntPtr.Zero)
        { SetFallback(); return; }
        try
        {
            // Transparent content leaves DWM visible. Avalonia's ordinary Mica brush
            // would cover the native Alt material, so do not request it here.
            _window.TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
            var margins = new Margins(-1);
            var kind = 4; // DWMSBT_TABBEDWINDOW (Mica Alt)
            var dark = _window.ActualThemeVariant == ThemeVariant.Dark ? 1 : 0;
            _ = DwmSetWindowAttribute(hwnd, 20, ref dark, sizeof(int));
            if (DwmExtendFrameIntoClientArea(hwnd, ref margins) < 0
                || DwmSetWindowAttribute(hwnd, 38, ref kind, sizeof(int)) < 0)
            { SetFallback(); return; }
            _window.Background = Brushes.Transparent;
            _window.TitleBar.BackgroundColor = Colors.Transparent;
            _window.TitleBar.InactiveBackgroundColor = Colors.Transparent;
            Applied = true;
        }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException)
        { SetFallback(); }
    }
    private void SetFallback()
    {
        Applied = false;
        _window.TransparencyLevelHint = [WindowTransparencyLevel.None];
        var brush = _window.TryFindResource("ApplicationPageBackgroundThemeBrush", out var value)
            && value is IBrush found ? found : new SolidColorBrush(_window.ActualThemeVariant == ThemeVariant.Dark
                ? Color.FromRgb(32, 32, 32) : Color.FromRgb(243, 243, 243));
        _window.Background = brush;
        _window.TitleBar.BackgroundColor = brush is ISolidColorBrush solid ? solid.Color : null;
        _window.TitleBar.InactiveBackgroundColor = _window.TitleBar.BackgroundColor;
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _window.Opened -= OnOpened; _window.ActualThemeVariantChanged -= OnThemeChanged;
        if (OperatingSystem.IsWindows())
        {
            Win32Properties.RemoveWndProcHookCallback(_window, _hook);
            Win32Properties.RemoveWindowStylesCallback(_window, _styles);
        }
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct Margins(int value) { public int Left = value, Right = value, Top = value, Bottom = value; }
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
    [DllImport("dwmapi.dll")]
    private static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref Margins margins);
}
#endif
