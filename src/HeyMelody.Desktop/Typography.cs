using System.Collections;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Fonts;

namespace HeyMelodyWindows.Ui;

/// <summary>The same unmodified static MiSans faces serve every self-drawn desktop UI.</summary>
internal static class Typography
{
    internal const string Family = "MiSans";
    internal const string CollectionKey = "fonts:HeyMelody";
    internal const string ThemeFontResourceKey = "ContentControlThemeFontFamily";
    internal static readonly CultureInfo Culture = CultureInfo.GetCultureInfo("zh-CN");
    internal static readonly string[] BundledFontNames = ["MiSans-Regular.otf", "MiSans-Semibold.otf", "MiSans-Bold.otf"];
    internal static readonly UnicodeRange HanRange = new([
        new(0x2E80, 0x2FDF), new(0x3000, 0x303F), new(0x3100, 0x312F), new(0x31A0, 0x31EF),
        new(0x3200, 0x33FF), new(0x3400, 0x4DBF), new(0x4E00, 0x9FFF), new(0xF900, 0xFAFF),
        new(0xFE30, 0xFE4F), new(0xFF00, 0xFFEF), new(0x20000, 0x2FA1F)
    ]);

    private static readonly object CollectionLock = new();
    private static readonly ConditionalWeakTable<FontManager, MiSansFontCollection> Collections = new();
    internal static int CollectionInitializationCount => Collections.TryGetValue(FontManager.Current, out _) ? 1 : 0;
    internal static string BundledFontDirectory => Path.Combine(AppContext.BaseDirectory, "fonts");
    internal static FontFamily ThemeFontFamily { get; } = new(CollectionKey + "#" + Family);

    internal static AppBuilder UseHeyMelodyTypography(this AppBuilder builder)
    {
        ApplyCulture();
        foreach (var name in BundledFontNames)
        {
            var path = Path.Combine(BundledFontDirectory, name);
            if (!File.Exists(path))
                throw new FileNotFoundException("The bundled MiSans font is required by the desktop UI.", path);
        }
        return builder
            .ConfigureFonts(RegisterCollection)
            .With(new FontManagerOptions
            {
                DefaultFamilyName = CollectionKey + "#" + Family,
                FontFallbacks = [new FontFallback { FontFamily = ThemeFontFamily, UnicodeRange = HanRange }]
            });
    }

    private static void RegisterCollection(FontManager fonts)
    {
        lock (CollectionLock)
        {
            if (Collections.TryGetValue(fonts, out _)) return;
            // FontManager owns the native faces. Production has one manager per process; isolated headless
            // dispatches dispose theirs, so a later manager must not reuse that disposed collection.
            var collection = new MiSansFontCollection(new EmbeddedFontCollection(
                new Uri(CollectionKey + "-files", UriKind.Absolute),
                new Uri(BundledFontDirectory + Path.DirectorySeparatorChar)));
            fonts.AddFontCollection(collection);
            Collections.Add(fonts, collection);
        }
    }

    internal static void ApplyThemeResources(Application application) =>
        application.Resources[ThemeFontResourceKey] = ThemeFontFamily;

    internal static void ApplyTextOptions(Visual visual)
    {
        TextOptions.SetTextHintingMode(visual, TextHintingMode.Strong);
        TextOptions.SetBaselinePixelAlignment(visual, BaselinePixelAlignment.Aligned);
    }

    internal static void ApplyCulture()
    {
        // Only UI culture: number and date formatting retain the user's existing preferences.
        CultureInfo.DefaultThreadCurrentUICulture = Culture;
        CultureInfo.CurrentUICulture = Culture;
    }
}

/// <summary>Maps UI weight semantics to the original MiSans 4.009 static faces, without synthetic bold.</summary>
internal sealed class MiSansFontCollection : IFontCollection
{
    internal const FontWeight RegularWeight = (FontWeight)330;
    internal const FontWeight SemiboldWeight = (FontWeight)520;
    internal const FontWeight BoldWeight = (FontWeight)630;
    private readonly IFontCollection _files;
    private readonly Dictionary<FontWeight, GlyphTypeface> _faces = [];
    private readonly FontFamily[] _families = [Typography.ThemeFontFamily];

    internal MiSansFontCollection(IFontCollection files)
    {
        _files = files;
        foreach (var weight in new[] { RegularWeight, SemiboldWeight, BoldWeight })
        {
            // Semibold's legacy family is "MiSans Semibold", but its typographic family is "MiSans".
            if (!TryLoad(Typography.Family, weight, out var face)
                && !(weight == SemiboldWeight && TryLoad("MiSans Semibold", weight, out face)))
                throw new InvalidOperationException("The bundled MiSans face could not be loaded at weight " + (int)weight + ".");
            _faces.Add(weight, face!);
        }
    }

    private bool TryLoad(string family, FontWeight weight, [NotNullWhen(true)] out GlyphTypeface? face)
    {
        if (_files.TryGetGlyphTypeface(family, FontStyle.Normal, weight, FontStretch.Normal, out face)
            && face.Weight == weight && face.FontSimulations == FontSimulations.None
            && (face.TypographicFamilyName == Typography.Family || face.FamilyName == Typography.Family
                || face.FamilyName == "MiSans Semibold"))
            return true;
        face = null;
        return false;
    }

    internal static FontWeight Map(FontWeight weight) => weight switch
    {
        RegularWeight => RegularWeight,
        SemiboldWeight => SemiboldWeight,
        BoldWeight => BoldWeight,
        >= FontWeight.Bold => BoldWeight,
        >= FontWeight.SemiBold => SemiboldWeight,
        _ => RegularWeight
    };

    private static bool IsFamily(string? family) =>
        family is null || string.Equals(family, Typography.Family, StringComparison.OrdinalIgnoreCase);
    public Uri Key { get; } = new(Typography.CollectionKey, UriKind.Absolute);

    public bool TryGetGlyphTypeface(string familyName, FontStyle style, FontWeight weight, FontStretch stretch,
        [NotNullWhen(true)] out GlyphTypeface? glyphTypeface)
    {
        if (IsFamily(familyName))
        {
            glyphTypeface = _faces[Map(weight)];
            return true;
        }
        glyphTypeface = null;
        return false;
    }

    public bool TryMatchCharacter(int codepoint, FontStyle fontStyle, FontWeight fontWeight, FontStretch fontStretch,
        string? familyName, CultureInfo? culture, out Typeface typeface)
    {
        if (IsFamily(familyName) && _faces[Map(fontWeight)].CharacterToGlyphMap.ContainsGlyph(codepoint))
        {
            typeface = new Typeface(Typography.ThemeFontFamily, FontStyle.Normal, fontWeight, FontStretch.Normal);
            return true;
        }
        typeface = default;
        return false;
    }

    public bool TryGetFamilyTypefaces(string familyName, [NotNullWhen(true)] out IReadOnlyList<Typeface>? familyTypefaces)
    {
        if (IsFamily(familyName))
        {
            familyTypefaces = [new(Typography.ThemeFontFamily, FontStyle.Normal, RegularWeight),
                new(Typography.ThemeFontFamily, FontStyle.Normal, SemiboldWeight),
                new(Typography.ThemeFontFamily, FontStyle.Normal, BoldWeight)];
            return true;
        }
        familyTypefaces = null;
        return false;
    }

    public bool TryCreateSyntheticGlyphTypeface(GlyphTypeface glyphTypeface, FontStyle style, FontWeight weight,
        FontStretch stretch, [NotNullWhen(true)] out GlyphTypeface? syntheticGlyphTypeface)
    {
        // All UI weights resolve to original faces above. A missing italic or heavier variant must not
        // cause the renderer to invent another font face or synthesize heavier Chinese strokes.
        syntheticGlyphTypeface = null;
        return false;
    }

    public bool TryGetNearestMatch(string familyName, FontStyle style, FontWeight weight, FontStretch stretch,
        [NotNullWhen(true)] out GlyphTypeface? glyphTypeface) =>
        TryGetGlyphTypeface(familyName, style, weight, stretch, out glyphTypeface);

    public int Count => _families.Length;
    public FontFamily this[int index] => _families[index];
    public IEnumerator<FontFamily> GetEnumerator() => ((IEnumerable<FontFamily>)_families).GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    public void Dispose() => _files.Dispose();
}
