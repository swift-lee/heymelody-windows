using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using FluentAvalonia.UI.Controls;
using HeyMelodyWindows.App;

internal static class IconTests
{
    internal static int Run(HeadlessUnitTestSession session)
    {
        Run("startup selects only a successfully loaded Segoe family, once per initialization", Selection);
        Run("system font selection agrees with the actual platform loader", SystemFont);
        Run("Segoe and bundled symbols retain independent fixed role mappings", Mappings);
        Run("all roles preserve icon sizes and create independent sources in both modes", Sources);
        Run("font icons inherit foreground in both light and dark themes", Foreground);
        return 5;

        void Run(string name, Action action)
        {
            session.Dispatch<bool>(() =>
            {
                try { action(); return Task.FromResult(true); }
                finally { AppIcons.Initialize(); }
            }, CancellationToken.None).GetAwaiter().GetResult();
            Console.WriteLine("PASS " + name);
        }
    }

    internal static bool Matches(FAIconSource? source, AppIcon role, double size = 20)
    {
        var expected = AppIcons.Source(role, size);
        if (source is FAFontIconSource font && expected is FAFontIconSource expectedFont)
            return font.FontFamily.Equals(expectedFont.FontFamily)
                && font.Glyph == expectedFont.Glyph && font.FontSize == expectedFont.FontSize;
        if (source is FASymbolIconSource symbol && expected is FASymbolIconSource expectedSymbol)
            return symbol.Symbol == expectedSymbol.Symbol && symbol.FontSize == expectedSymbol.FontSize;
        if (source is not FAPathIconSource { Data: { } actual }
            || expected is not FAPathIconSource { Data: { } expectedData }
            || actual.Bounds != expectedData.Bounds)
            return false;
        for (var row = 0; row < 24; row++)
        for (var column = 0; column < 24; column++)
        {
            var point = new Point((column + 0.5) * size / 24, (row + 0.5) * size / 24);
            if (actual.FillContains(point) != expectedData.FillContains(point))
                return false;
        }
        return true;
    }

    private static void Selection()
    {
        foreach (var (family, expected) in new (string?, bool)[]
        {
            ("Segoe Fluent Icons", true), ("segoe fluent icons", true),
            (null, false), ("MiSans", false), ("Segoe UI", false)
        })
        {
            var loads = 0;
            AppIcons.Initialize(() => { loads++; return family; });
            Check(AppIcons.UsesSegoeFluentIcons == expected,
                $"Only the loaded Segoe family counts as usable: {family ?? "(missing)"}.");
            foreach (var role in Enum.GetValues<AppIcon>())
                AppIcons.Source(role);
            Check(loads == 1, "Creating icons never reloads the font or probes individual glyphs.");
        }

        AppIcons.Initialize(() => "Segoe Fluent Icons");
        AppIcons.Initialize(() => throw new InvalidOperationException("simulated font load failure"));
        Check(!AppIcons.UsesSegoeFluentIcons, "A font load exception resets a previous successful selection.");
        AppIcons.Initialize(() => "Segoe Fluent Icons");
        Check(AppIcons.UsesSegoeFluentIcons, "A new application initialization replaces the previous selection.");
    }

    private static void SystemFont()
    {
        string? loadedFamily = null;
        try
        {
            if (FontManager.Current.SystemFonts.TryGetGlyphTypeface("Segoe Fluent Icons",
                FontStyle.Normal, FontWeight.Normal, FontStretch.Normal, out var face))
                loadedFamily = face.FamilyName;
        }
        catch { }
        AppIcons.Initialize();
        Check(AppIcons.UsesSegoeFluentIcons == string.Equals(loadedFamily, "Segoe Fluent Icons",
            StringComparison.OrdinalIgnoreCase), "Startup follows the actual system font loader on this platform.");
        Console.WriteLine("INFO actual Segoe family: " + (loadedFamily ?? "(unavailable)"));
    }

    private static void Mappings()
    {
        var original = new (AppIcon Role, int Segoe, FASymbol BuiltIn)[]
        {
            (AppIcon.Settings, 0xE713, (FASymbol)0xE713),
            (AppIcon.Refresh, 0xE72C, (FASymbol)0xE72C),
            (AppIcon.Copy, 0xE8C8, (FASymbol)0xE8C8),
            (AppIcon.Charging, 0xE945, (FASymbol)0xE945),
            (AppIcon.NoiseControl, 0xF61F, FASymbol.Audio),
            (AppIcon.Equalizer, 0xE9E9, (FASymbol)0xE9E9),
            (AppIcon.Sound, 0xE995, (FASymbol)0xE995),
            (AppIcon.Earbuds, 0xF4C0, (FASymbol)0xF4C0),
            (AppIcon.Gestures, 0xE815, (FASymbol)0xE815),
            (AppIcon.Hearing, 0xF270, FASymbol.Audio),
            (AppIcon.FitTest, 0xF27C, FASymbol.Audio),
            (AppIcon.Info, 0xE946, (FASymbol)0xE946),
            (AppIcon.History, 0xE81C, (FASymbol)0xE81C),
            (AppIcon.Battery, 0xE850, (FASymbol)0xE850)
        };
        var extended = new (AppIcon Role, int Segoe, FASymbol BuiltIn)[]
        {
            (AppIcon.Sync, 0xE895, FASymbol.Sync),
            (AppIcon.Rotate, 0xE7AD, FASymbol.Rotate),
            (AppIcon.Globe, 0xE774, FASymbol.Globe),
            (AppIcon.Permissions, 0xE8D7, FASymbol.Permissions),
            (AppIcon.DarkTheme, 0xE708, FASymbol.DarkTheme),
            (AppIcon.Subtract, 0xE738, FASymbol.Remove),
            (AppIcon.Warning, 0xE7BA, FASymbol.Important),
            (AppIcon.Error, 0xEA39, FASymbol.Cancel),
            (AppIcon.Success, 0xE930, FASymbol.Accept)
        };
        foreach (var useSegoe in new[] { false, true })
        {
            AppIcons.Initialize(() => useSegoe ? "Segoe Fluent Icons" : null);
            foreach (var (role, segoe, builtIn) in original.Concat(extended))
            {
                var source = AppIcons.Source(role);
                Check(useSegoe
                    ? source is FAFontIconSource font && font.FontFamily.Name == "Segoe Fluent Icons"
                        && font.Glyph == char.ConvertFromUtf32(segoe)
                    : source is FASymbolIconSource symbol && symbol.Symbol == builtIn,
                    $"{role} retains its fixed {(useSegoe ? "Segoe" : "built-in")} mapping.");
            }
        }
    }

    private static void Sources()
    {
        Check(Enum.GetValues<AppIcon>().Length == 48, "All existing icon roles remain available.");
        foreach (var useSegoe in new[] { false, true })
        {
            AppIcons.Initialize(() => useSegoe ? "Segoe Fluent Icons" : null);
            foreach (var role in Enum.GetValues<AppIcon>())
            foreach (var size in new double[] { 12, 16, 20, 24, 96 })
            {
                var first = AppIcons.Source(role, size);
                var second = AppIcons.Source(role, size);
                Check(!ReferenceEquals(first, second), role + " sources are independent.");
                if (role is AppIcon.BatteryLeft or AppIcon.BatteryRight or AppIcon.BatteryCase)
                {
                    Check(first is FAPathIconSource { Data: { } }
                        && second is FAPathIconSource { Data: { } }
                        && !ReferenceEquals(((FAPathIconSource)first).Data, ((FAPathIconSource)second).Data),
                        role + " retains an independent manufacturer vector in both font modes.");
                }
                else
                    Check(useSegoe
                        ? first is FAFontIconSource font && font.FontSize == size
                        : first is FASymbolIconSource symbol && symbol.FontSize == size,
                        role + " retains its requested font size and startup selection.");
                var element = AppIcons.Element(role, size);
                Check(element.Width == size && element.Height == size && Matches(element.IconSource, role, size),
                    role + " retains the element size contract.");
            }
        }
    }

    private static void Foreground()
    {
        foreach (var useSegoe in new[] { false, true })
        foreach (var theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
        {
            AppIcons.Initialize(() => useSegoe ? "Segoe Fluent Icons" : null);
            var expected = Colors.OrangeRed;
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Margin = new Thickness(12) };
            foreach (var (role, size) in new[] { (AppIcon.Settings, 12), (AppIcon.Equalizer, 20), (AppIcon.Audio, 96) })
            {
                var icon = AppIcons.Element(role, size);
                icon.Foreground = new SolidColorBrush(expected);
                row.Children.Add(icon);
            }
            var window = new Window
            {
                Width = 230, Height = 140, Content = row, RequestedThemeVariant = theme,
                WindowDecorations = WindowDecorations.None
            };
            try
            {
                window.Show();
                Dispatcher.UIThread.RunJobs();
                var icons = window.GetVisualDescendants().OfType<FAIconElement>()
                    .Where(icon => icon is FAFontIcon or FASymbolIcon).ToArray();
                Check(icons.Length == 3 && icons.All(icon =>
                    icon.Foreground is ISolidColorBrush brush && brush.Color == expected
                    && icon.Bounds.Width > 0 && icon.Bounds.Height > 0),
                    $"{theme}/{useSegoe} preserves foreground and nonzero layout at small, normal and product-fallback sizes.");
            }
            finally
            {
                window.Close();
                Dispatcher.UIThread.RunJobs();
            }
        }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
