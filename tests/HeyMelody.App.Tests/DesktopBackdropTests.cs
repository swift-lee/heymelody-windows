using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using FluentAvalonia.UI.Windowing;
using HeyMelodyWindows.Ui;

internal static class DesktopBackdropTests
{
    internal static int Run()
    {
        int checks = 0;
        foreach (var dark in new[] { false, true })
        {
            var standard = dark ? Color.FromArgb(64, 24, 24, 24) : Color.FromArgb(40, 248, 248, 248);
            var dense = dark ? Color.FromArgb(240, 32, 32, 32) : Color.FromArgb(244, 243, 243, 243);
            var opaque = dark ? Color.FromRgb(32, 32, 32) : Color.FromRgb(243, 243, 243);
            foreach (var level in new[] { WindowTransparencyLevel.Blur, WindowTransparencyLevel.AcrylicBlur })
            foreach (var isMacOS in new[] { false, true })
            foreach (var tint in new[] { MacBackdropTint.Standard, MacBackdropTint.Dense })
            {
                var result = DesktopWindowBackdrop.ResolveBackground(DesktopBackdropEffect.NativeBlur, level,
                    isMacOS, dark, tint);
                var expected = isMacOS && tint == MacBackdropTint.Dense ? dense : standard;
                Check(result.Applied && result.Background == expected,
                    $"Native blur uses only its platform's tint: macOS={isMacOS}, dark={dark}, tint={tint}, level={level}.");
            }
            foreach (var isMacOS in new[] { false, true })
            foreach (var tint in new[] { MacBackdropTint.Standard, MacBackdropTint.Dense })
            {
                foreach (var level in new[] { WindowTransparencyLevel.None, WindowTransparencyLevel.Transparent,
                    WindowTransparencyLevel.Blur, WindowTransparencyLevel.AcrylicBlur })
                {
                    var result = DesktopWindowBackdrop.ResolveBackground(DesktopBackdropEffect.Opaque, level,
                        isMacOS, dark, tint);
                    Check(!result.Applied && result.Background == opaque,
                        "An unnegotiated provider remains opaque regardless of the actual transparency level.");
                }
                foreach (var level in new[] { WindowTransparencyLevel.None, WindowTransparencyLevel.Transparent })
                {
                    var result = DesktopWindowBackdrop.ResolveBackground(DesktopBackdropEffect.NativeBlur, level,
                        isMacOS, dark, tint);
                    Check(!result.Applied && result.Background == opaque,
                        "A negotiated provider without actual native blur uses the legible opaque fallback.");
                }
                var before = DesktopWindowBackdrop.ResolveBackground(DesktopBackdropEffect.NativeBlur,
                    WindowTransparencyLevel.Blur, isMacOS, dark, tint);
                var fallback = DesktopWindowBackdrop.ResolveBackground(DesktopBackdropEffect.NativeBlur,
                    WindowTransparencyLevel.None, isMacOS, dark, tint);
                var recovered = DesktopWindowBackdrop.ResolveBackground(DesktopBackdropEffect.NativeBlur,
                    WindowTransparencyLevel.Blur, isMacOS, dark, tint);
                Check(before == recovered && before.Applied && !fallback.Applied,
                    "Removing and restoring native blur restores exactly the platform's original tint.");
            }
        }
        Console.WriteLine($"PASS backdrop platform policy ({checks} checks; no windows, native effects or Bluetooth).");
        return checks;

        void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
            checks++;
        }
    }

    internal static void VerifyWindowsWindow()
    {
        if (!OperatingSystem.IsWindows()) return;
        // FAAppWindow requires real Win32 handles, so this case belongs to the native runner.
        foreach (var tint in new[] { MacBackdropTint.Standard, MacBackdropTint.Dense })
        {
            var background = new SolidColorBrush(Color.FromArgb(173, 21, 43, 65));
            var fallback = new SolidColorBrush(Color.FromRgb(87, 109, 131));
            var hints = new[] { WindowTransparencyLevel.Transparent };
            var window = new FAAppWindow
            {
                Background = background,
                TransparencyBackgroundFallback = fallback,
                TransparencyLevelHint = hints
            };
            try
            {
                using var backdrop = new DesktopWindowBackdrop(window, useWindowsMicaAlt: false, macTint: tint);
                int changes = 0;
                backdrop.Changed += () => changes++;
                VerifyUnchanged();
                window.Show();
                foreach (var theme in new[] { ThemeVariant.Dark, ThemeVariant.Light })
                {
                    window.RequestedThemeVariant = theme;
                    Dispatcher.UIThread.RunJobs();
                    VerifyUnchanged();
                }
                Check(!backdrop.Applied && changes == 0,
                    "The shared non-Windows backdrop never applies or publishes changes on Windows.");

                void VerifyUnchanged() => Check(ReferenceEquals(window.Background, background)
                    && ReferenceEquals(window.TransparencyBackgroundFallback, fallback)
                    && window.TransparencyLevelHint.SequenceEqual(hints),
                    "Mac tint leaves the Windows background, fallback and transparency hints unchanged.");
            }
            finally { window.Close(); }
        }

        static void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
