using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Threading;
using HeyMelodyWindows.Ui;

/// <summary>Bundled static MiSans must be identical on every supported desktop and display language.</summary>
internal static class TypographyTests
{
    // Characters whose Japanese and Simplified Chinese glyph shapes differ under Han unification.
    private const string ProbeText = "直骨角写画";

    internal static int Run(HeadlessUnitTestSession session)
    {
        Run("typography pins zh-CN UI culture and bundled MiSans for the default and theme", Defaults);
        Run("Han characters keep bundled MiSans under an en-US culture", EnglishCulture);
        Run("logical UI weights reuse the three real MiSans faces without synthetic emboldening", Weights);
        try
        {
            session.Dispatch<bool>(() => Task.FromException<bool>(new InvalidOperationException("font-lifetime-probe")),
                CancellationToken.None).GetAwaiter().GetResult();
            throw new InvalidOperationException("The font lifetime probe did not propagate its expected failure.");
        }
        catch (InvalidOperationException error) when (error.Message == "font-lifetime-probe") { }
        Run("a new isolated font manager can draw after a previous dispatch was disposed", DrawAfterIsolatedDispatch);
        return 4;

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
        const string expected = Typography.Family;
        Check(Typography.CollectionInitializationCount == 1, "The active font manager owns one bundled font collection.");
        foreach (var name in Typography.BundledFontNames)
            Check(File.Exists(Path.Combine(Typography.BundledFontDirectory, name)), "The shared output contains " + name + ".");
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
            Check(typeface.FontFamily.Name == Typography.Family
                && (glyphs.TypographicFamilyName == Typography.Family || glyphs.FamilyName == Typography.Family),
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
            Check(glyphs.Weight == MiSansFontCollection.Map(weight),
                weight + " resolves to original MiSans weight " + (int)MiSansFontCollection.Map(weight) + ", not " + (int)glyphs.Weight + ".");
            Check(ReferenceEquals(glyphs, new Typeface(Typography.ThemeFontFamily, FontStyle.Normal, weight).GlyphTypeface),
                weight + " reuses the face cached for the owning font manager.");
            Check(glyphs.TypographicFamilyName == Typography.Family || glyphs.FamilyName == Typography.Family,
                "The physical face belongs to MiSans, not a platform replacement: " + glyphs.FamilyName + ".");
        }
    }

    private static void DrawAfterIsolatedDispatch()
    {
        var window = new Window
        {
            Width = 320, Height = 140,
            Content = new StackPanel
            {
                Children =
                {
                    new TextBlock { Text = ProbeText, FontWeight = FontWeight.Normal },
                    new TextBlock { Text = ProbeText, FontWeight = FontWeight.SemiBold },
                    new TextBlock { Text = ProbeText, FontWeight = FontWeight.Bold }
                }
            }
        };
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            Check(window.Bounds.Width > 0, "Text layout uses live native typefaces after isolated font-manager cleanup.");
            Weights();
        }
        finally { window.Close(); }
    }

    private static void Check(bool condition, string reason) { if (!condition) throw new InvalidOperationException(reason); }
}
