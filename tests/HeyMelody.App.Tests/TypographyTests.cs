using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media;
using HeyMelodyWindows.Ui;

/// <summary>Simplified Chinese typography that must hold on any Windows display language.</summary>
/// <remarks>Linux and other machines without Microsoft YaHei exercise the bundled Noto Sans SC branch.</remarks>
internal static class TypographyTests
{
    // Characters whose Japanese and Simplified Chinese glyph shapes differ under Han unification.
    private const string ProbeText = "直骨角写画";
    private static readonly string[] SimplifiedFamilies = [Typography.SystemFamily, Typography.SystemLegacyFamily, Typography.MacOSFamily, Typography.BundledFamily];

    internal static int Run(HeadlessUnitTestSession session)
    {
        Run("typography pins the zh-CN UI culture, the theme font and a Simplified Chinese default family", Defaults);
        Run("Han characters keep Simplified Chinese faces under an en-US culture", EnglishCulture);
        Run("semibold and bold Chinese text use real font faces without synthetic emboldening", Weights);
        return 3;

        void Run(string name, Action action)
        {
            session.Dispatch<bool>(() => { action(); return Task.FromResult(true); }, CancellationToken.None).GetAwaiter().GetResult();
            Console.WriteLine("PASS " + name);
        }
    }

    private static void Defaults()
    {
        Check(CultureInfo.CurrentUICulture.Name == "zh-CN" && CultureInfo.DefaultThreadCurrentUICulture?.Name == "zh-CN",
            "The UI culture is Simplified Chinese regardless of the system display language.");
        Check(Typography.Source is TypographySource.System or TypographySource.MacOSSystem or TypographySource.BundledSystem,
            "The platform's Simplified Chinese system family or bundled files provide the UI font, not "
            + Typography.Source + ".");
        var expected = Typography.Source switch
        {
            TypographySource.System => Typography.SystemFamily,
            TypographySource.MacOSSystem => Typography.MacOSFamily,
            _ => Typography.BundledFamily
        };
        if (OperatingSystem.IsMacOS()) Check(Typography.Source == TypographySource.MacOSSystem,
            "macOS uses its installed PingFang SC family before Windows or bundled fallback families.");
        Check(FontManager.Current.DefaultFontFamily.Name == expected, "The default family is " + expected
            + ", not " + FontManager.Current.DefaultFontFamily.Name + ".");
        Check(Application.Current!.TryFindResource(Typography.ThemeFontResourceKey, out var resource) && resource is FontFamily theme
            && theme.Name == expected,
            "The FluentAvalonia theme font starts with " + expected + ".");
        var window = new Window();
        Typography.ApplyTextOptions(window);
        Check(TextOptions.GetTextHintingMode(window) == TextHintingMode.Strong
            && TextOptions.GetBaselinePixelAlignment(window) == BaselinePixelAlignment.Aligned
            && TextOptions.GetTextRenderingMode(window) == TextRenderingMode.Unspecified,
            "Windows pin full hinting and pixel-aligned baselines while Avalonia chooses subpixel or greyscale edges.");
    }

    private static void EnglishCulture()
    {
        var english = CultureInfo.GetCultureInfo("en-US");
        foreach (var rune in ProbeText.EnumerateRunes())
        {
            Check(FontManager.Current.TryMatchCharacter(rune.Value, FontStyle.Normal, FontWeight.Normal, FontStretch.Normal,
                    null, english, out var typeface), "A Simplified Chinese face covers " + rune + ".");
            var glyphs = typeface.GlyphTypeface;
            Check(SimplifiedFamilies.Contains(typeface.FontFamily.Name) || SimplifiedFamilies.Contains(glyphs.TypographicFamilyName)
                || SimplifiedFamilies.Contains(glyphs.FamilyName),
                rune + " fell back to " + typeface.FontFamily.Name + " / " + glyphs.FamilyName + " under en-US.");
        }
    }

    private static void Weights()
    {
        foreach (var weight in new[] { FontWeight.Normal, FontWeight.SemiBold, FontWeight.Bold })
        {
            var glyphs = new Typeface(Typography.ThemeFontFamily, FontStyle.Normal, weight).GlyphTypeface;
            Check(glyphs.FontSimulations == FontSimulations.None, weight + " Chinese text must not be synthesised; got "
                + glyphs.FontSimulations + " from " + glyphs.FamilyName + ".");
            Check(weight == FontWeight.Normal || glyphs.Weight >= FontWeight.SemiBold,
                weight + " resolves to a real heavier face, not " + glyphs.Weight + ".");
        }
    }

    private static void Check(bool condition, string reason) { if (!condition) throw new InvalidOperationException(reason); }
}
