using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using FluentAvalonia.UI.Windowing;
using HeyMelody.Diagnostics;

namespace HeyMelodyWindows.Ui;

internal enum DesktopBackdropEffect { Opaque, NativeBlur }
// macOS materials are vibrancy surfaces rather than Mica-style content backdrops. Standard leaves a light tint
// for window chrome; Dense keeps arbitrary component content on a nearly opaque page surface.
internal enum MacBackdropTint { Standard, Dense }
internal interface IDesktopBackdropProvider : IDisposable
{
    DesktopBackdropCapabilities Capabilities { get; }
    event Action? Changed;
}
internal sealed record DesktopBackdropCapabilities(DesktopBackdropEffect Effect, string Provider)
{
    // A Linux adapter may populate this only after negotiating its compositor's background-effect capability.
    // Being on Wayland, having a protocol XML file, or running a named desktop does not establish support.
    internal static Func<Window, IDesktopBackdropProvider>? LinuxProviderFactory { get; set; }
    internal static DesktopBackdropCapabilities Current => OperatingSystem.IsMacOS()
        ? new(DesktopBackdropEffect.NativeBlur, "Avalonia.Native/NSVisualEffectView")
        : new(DesktopBackdropEffect.Opaque, "No native background effect negotiated");
}

/// <summary>Platform window background; native macOS blur and negotiated future Linux effects share legible fallbacks.</summary>
internal sealed class DesktopWindowBackdrop : IDisposable
{
    private readonly FAAppWindow _window;
    private readonly MacBackdropTint _macTint;
    private DesktopBackdropCapabilities _capabilities;
    private readonly IDesktopBackdropProvider? _linuxProvider;
    private bool _disposed, _updating;
#if HEYMELODY_WINDOWS
    private readonly HeyMelodyWindows.Windowing.MicaAltBackdrop? _windows;
#endif
    internal bool Applied { get; private set; }
    internal event Action? Changed;
    internal DesktopWindowBackdrop(FAAppWindow window, bool useWindowsMicaAlt = true,
        MacBackdropTint macTint = MacBackdropTint.Standard)
    {
        _window = window;
        _macTint = macTint;
        _linuxProvider = OperatingSystem.IsLinux() ? DesktopBackdropCapabilities.LinuxProviderFactory?.Invoke(window) : null;
        _capabilities = _linuxProvider?.Capabilities ?? DesktopBackdropCapabilities.Current;
        if (_linuxProvider is not null) _linuxProvider.Changed += OnCapabilitiesChanged;
        if (OperatingSystem.IsWindows())
        {
#if HEYMELODY_WINDOWS
            if (useWindowsMicaAlt) _windows = new(window);
#endif
            return; // The main window retains its existing Avalonia Mica composition path.
        }
        window.Opened += OnOpened;
        window.ActualThemeVariantChanged += OnThemeChanged;
        window.PropertyChanged += OnWindowPropertyChanged;
        SetTransparencyHint();
        Apply();
    }
    private void SetTransparencyHint() => _window.TransparencyLevelHint = _capabilities.Effect == DesktopBackdropEffect.NativeBlur
        ? [WindowTransparencyLevel.AcrylicBlur, WindowTransparencyLevel.Blur, WindowTransparencyLevel.None] : [WindowTransparencyLevel.None];
    private void OnCapabilitiesChanged() => Dispatcher.UIThread.Post(() =>
    {
        if (_disposed || _linuxProvider is null) return;
        _capabilities = _linuxProvider.Capabilities; SetTransparencyHint(); Apply();
    });
    private void OnOpened(object? sender, EventArgs args) => Dispatcher.UIThread.Post(Apply, DispatcherPriority.Render);
    private void OnThemeChanged(object? sender, EventArgs args) => Apply();
    private void OnWindowPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs args)
    {
        if (args.Property.Name == nameof(TopLevel.ActualTransparencyLevel)) Apply();
    }
    private void Apply()
    {
        if (_disposed || _updating) return;
        _updating = true;
        try
        {
            var dark = _window.ActualThemeVariant == ThemeVariant.Dark;
            var fallback = new SolidColorBrush(dark ? Color.FromRgb(32, 32, 32) : Color.FromRgb(243, 243, 243));
            bool applied = _capabilities.Effect == DesktopBackdropEffect.NativeBlur
                && _window.ActualTransparencyLevel is var level && (level == WindowTransparencyLevel.Blur || level == WindowTransparencyLevel.AcrylicBlur);
            _window.TransparencyBackgroundFallback = fallback;
            // NSVisualEffectView supplies the material. The tint keeps text readable without covering it;
            // component windows use a dense page-colored tint so arbitrary content stays on a stable surface.
            _window.Background = applied ? new SolidColorBrush(MaterialTint(dark, _macTint)) : fallback;
            if (Applied != applied)
            {
                Applied = applied; Changed?.Invoke();
                DiagnosticLog.Info("ui.backdrop", "negotiated", "Window background effect negotiated",
                    new { provider = _capabilities.Provider, requested = _capabilities.Effect.ToString(), actual = _window.ActualTransparencyLevel.ToString(), applied, tint = _macTint.ToString() });
            }
        }
        finally { _updating = false; }
    }
    private static Color MaterialTint(bool dark, MacBackdropTint tint) => (dark, tint) switch
    {
        (true, MacBackdropTint.Dense) => Color.FromArgb(240, 32, 32, 32),
        (false, MacBackdropTint.Dense) => Color.FromArgb(244, 243, 243, 243),
        (true, _) => Color.FromArgb(64, 24, 24, 24),
        (false, _) => Color.FromArgb(40, 248, 248, 248)
    };
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
#if HEYMELODY_WINDOWS
        _windows?.Dispose();
#endif
        _window.Opened -= OnOpened;
        _window.ActualThemeVariantChanged -= OnThemeChanged;
        _window.PropertyChanged -= OnWindowPropertyChanged;
        if (_linuxProvider is not null) { _linuxProvider.Changed -= OnCapabilitiesChanged; _linuxProvider.Dispose(); }
    }
}
