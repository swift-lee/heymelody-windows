using System.Collections;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Fonts;
using SkiaSharp;

namespace HeyMelodyWindows.Ui;

/// <summary>Simplified Chinese typography shared by every HeyMelody UI process (compiled into each by source link).</summary>
/// <remarks>
/// Without a configured family, Han characters reach Avalonia's fallback, which scores candidate faces by the
/// text's culture or, when absent, <see cref="CultureInfo.CurrentUICulture"/>. Under an en-US Windows UI that
/// can pick a Japanese face, so the same characters show Japanese glyph shapes. The UI only supports Simplified
/// Chinese: it uses PingFang SC on macOS and hinted Microsoft YaHei UI on Windows, pinning Han fallback before culture scoring.
/// Noto Sans SC (OFL-1.1) ships beside the executables for systems without YaHei; YaHei itself may not be bundled.
/// </remarks>
internal static class Typography
{
    internal const string SystemFamily = "Microsoft YaHei UI";
    internal const string SystemLegacyFamily = "Microsoft YaHei";
    internal const string MacOSFamily = "PingFang SC";
    internal const string BundledFamily = "Noto Sans SC";
    /// <summary>Key of <see cref="WeightMappedFontCollection"/>, which serves every family the UI names.</summary>
    internal const string CollectionKey = "fonts:HeyMelody";
    internal const string ThemeFontResourceKey = "ContentControlThemeFontFamily";
    internal static readonly CultureInfo Culture = CultureInfo.GetCultureInfo("zh-CN");

    /// <summary>Han ideographs, their radicals and strokes, CJK punctuation and full-width forms.</summary>
    internal static readonly UnicodeRange HanRange = new([
        new(0x2E80, 0x2FDF), new(0x3000, 0x303F), new(0x3100, 0x312F), new(0x31A0, 0x31EF),
        new(0x3200, 0x33FF), new(0x3400, 0x4DBF), new(0x4E00, 0x9FFF), new(0xF900, 0xFAFF),
        new(0xFE30, 0xFE4F), new(0xFF00, 0xFFEF), new(0x20000, 0x2FA1F)
    ]);

    /// <summary>The bundled font files, copied beside every UI executable that references HeyMelody.Artwork.</summary>
    internal static string BundledFontDirectory => Path.Combine(AppContext.BaseDirectory, "fonts");

    internal static TypographySource Source { get; private set; }

    /// <summary>The theme font: the system family where present, then the bundled family.</summary>
    internal static FontFamily ThemeFontFamily { get; private set; } = FontFamily.Default;

    /// <param name="bundledFallback">
    /// Allows the bundled Noto Sans SC files when Microsoft YaHei UI is missing. The Windows-only Host passes
    /// false: Windows ships YaHei, and the backend's tray menu should not load extra fonts.
    /// </param>
    internal static AppBuilder UseHeyMelodyTypography(this AppBuilder builder, bool bundledFallback = true)
    {
        ApplyCulture();
        var files = bundledFallback && Directory.Exists(BundledFontDirectory)
            && Directory.EnumerateFiles(BundledFontDirectory, "*.otf").Any();
        // Fontconfig must see the bundled directory before Skia first matches a family.
        if (files && OperatingSystem.IsLinux()) Fontconfig.TryAddApplicationFonts(BundledFontDirectory);
        string[] names;
        if (OperatingSystem.IsMacOS() && HasSystemFamily(MacOSFamily))
        {
            Source = TypographySource.MacOSSystem;
            names = [MacOSFamily];
        }
        else if (HasSystemFamily(SystemFamily))
        {
            Source = TypographySource.System;
            names = [SystemFamily, SystemLegacyFamily];
        }
        else if (files && HasSystemFamily(BundledFamily))
        {
            // Registered with the platform font manager, so Skia opens the files by path like installed fonts.
            Source = TypographySource.BundledSystem;
            names = [BundledFamily];
        }
        else if (files)
        {
            // Last resort on a Windows installation without YaHei. Avalonia 12.1 loads collection fonts from
            // streams, which retains native memory for every window that draws them, so the system paths above win.
            Source = TypographySource.BundledCollection;
            names = [BundledFamily];
        }
        else
        {
            Source = TypographySource.None;
            return builder;
        }
        var bundledCollection = Source == TypographySource.BundledCollection;
        var primary = CollectionKey + "#" + names[0];
        ThemeFontFamily = new FontFamily(string.Join(", ", names.Select(name => CollectionKey + "#" + name)));
        return builder
            .ConfigureFonts(fonts => fonts.AddFontCollection(new WeightMappedFontCollection(bundledCollection
                ? new EmbeddedFontCollection(new Uri(CollectionKey + "-files", UriKind.Absolute),
                    new Uri(BundledFontDirectory + Path.DirectorySeparatorChar))
                : null)))
            .With(new FontManagerOptions
            {
                DefaultFamilyName = primary,
                FontFallbacks = [new FontFallback { FontFamily = new FontFamily(primary), UnicodeRange = HanRange }]
            });
    }

    /// <summary>Overrides the FluentAvalonia/Fluent theme font so templates use the same family.</summary>
    internal static void ApplyThemeResources(Application application)
    {
        if (Source != TypographySource.None) application.Resources[ThemeFontResourceKey] = ThemeFontFamily;
    }

    /// <summary>States the text rendering options explicitly for a window and everything it renders.</summary>
    /// <remarks>
    /// Strong hinting is Skia's full hinting, which keeps YaHei's hand-tuned hints through DirectWrite; baselines snap
    /// to whole pixels. The rendering mode stays unspecified: Avalonia uses subpixel text on opaque targets and
    /// switches to greyscale inside layers and on transparent Mica surfaces, where subpixel text would fringe.
    /// These match Avalonia 12.1's defaults and are pinned so a future default change cannot soften the text.
    /// </remarks>
    internal static void ApplyTextOptions(Visual visual)
    {
        TextOptions.SetTextHintingMode(visual, TextHintingMode.Strong);
        TextOptions.SetBaselinePixelAlignment(visual, BaselinePixelAlignment.Aligned);
    }

    internal static void ApplyCulture()
    {
        // Only the UI culture: number and date formatting keep the user's culture and existing behaviour.
        CultureInfo.DefaultThreadCurrentUICulture = Culture;
        CultureInfo.CurrentUICulture = Culture;
    }

    internal static bool HasSystemFamily(string name)
    {
        try
        {
            using var typeface = SKFontManager.Default.MatchFamily(name);
            // Font configuration on other systems substitutes a different family instead of returning null.
            return typeface is not null && string.Equals(typeface.FamilyName, name, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception) { return false; }
    }

    /// <summary>Makes the bundled fonts system fonts for fontconfig (Linux previews and tests).</summary>
    /// <remarks>
    /// Skia's fontconfig manager loads its own configuration on first use instead of the process's current one, so the
    /// directory is added through a configuration file that includes the system one. .NET does not pass environment
    /// changes to native code, so the variable is set with libc before Skia reads it.
    /// </remarks>
    private static class Fontconfig
    {
        [DllImport("libc", EntryPoint = "setenv")]
        private static extern int SetNativeEnvironment([MarshalAs(UnmanagedType.LPUTF8Str)] string name,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string value, int overwrite);

        public static bool TryAddApplicationFonts(string directory)
        {
            try
            {
                var configured = Environment.GetEnvironmentVariable("FONTCONFIG_FILE");
                var system = string.IsNullOrEmpty(configured) || configured.EndsWith("heymelody-fonts.conf", StringComparison.Ordinal)
                    ? "/etc/fonts/fonts.conf" : configured;
                var folder = Path.Combine(Path.GetTempPath(), "heymelody-fontconfig-" + Environment.UserName);
                Directory.CreateDirectory(folder);
                var file = Path.Combine(folder, "heymelody-fonts.conf");
                var content = "<?xml version=\"1.0\"?>\n<!DOCTYPE fontconfig SYSTEM \"urn:fontconfig:fonts.dtd\">\n<fontconfig>\n"
                    + $"  <include ignore_missing=\"yes\">{System.Security.SecurityElement.Escape(system)}</include>\n"
                    + $"  <dir>{System.Security.SecurityElement.Escape(directory)}</dir>\n</fontconfig>\n";
                if (!File.Exists(file) || File.ReadAllText(file) != content)
                {
                    var temporary = file + "." + Environment.ProcessId + ".tmp";
                    File.WriteAllText(temporary, content);
                    File.Move(temporary, file, overwrite: true);
                }
                return SetNativeEnvironment("FONTCONFIG_FILE", file, 1) == 0;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException
                or DllNotFoundException or EntryPointNotFoundException) { return false; }
        }
    }
}

/// <summary>Serves the UI families from the platform fonts (or the bundled files) with real weight faces.</summary>
/// <remarks>
/// PingFang SC provides Regular, Medium and Semibold, while Microsoft YaHei UI has Light, Regular and Bold;
/// Noto Sans SC here has Regular, Medium and Bold. A semibold request
/// would otherwise match the lighter face and be emboldened synthetically, which smears Han strokes at UI sizes, so
/// weights from 550 up to bold use the real bold face. Implemented on the interface because the font manager calls
/// an internal overload of <see cref="FontCollectionBase"/> that an override would not see.
/// </remarks>
internal sealed class WeightMappedFontCollection(IFontCollection? files) : IFontCollection
{
    public Uri Key { get; } = new(Typography.CollectionKey, UriKind.Absolute);
    private IFontCollection Target => files ?? FontManager.Current.SystemFonts;

    internal static FontWeight Map(FontWeight weight, string? familyName = null)
    {
        // PingFang's heaviest installed face is Semibold. Request that real face for UI bold text,
        // rather than asking CoreText to synthesize a missing 700-weight face.
        if ((familyName == Typography.MacOSFamily || familyName is null && Typography.Source == TypographySource.MacOSSystem)
            && weight >= FontWeight.SemiBold) return FontWeight.SemiBold;
        return (int)weight is >= 550 and < 700 ? FontWeight.Bold : weight;
    }

    public bool TryGetGlyphTypeface(string familyName, FontStyle style, FontWeight weight, FontStretch stretch,
        [NotNullWhen(true)] out GlyphTypeface? glyphTypeface) => Target.TryGetGlyphTypeface(familyName, style, Map(weight, familyName), stretch, out glyphTypeface);

    public bool TryMatchCharacter(int codepoint, FontStyle fontStyle, FontWeight fontWeight, FontStretch fontStretch,
        string? familyName, CultureInfo? culture, out Typeface typeface)
        => Target.TryMatchCharacter(codepoint, fontStyle, Map(fontWeight, familyName), fontStretch, familyName, culture, out typeface);

    public bool TryGetFamilyTypefaces(string familyName, [NotNullWhen(true)] out IReadOnlyList<Typeface>? familyTypefaces)
        => Target.TryGetFamilyTypefaces(familyName, out familyTypefaces);

    public bool TryCreateSyntheticGlyphTypeface(GlyphTypeface glyphTypeface, FontStyle style, FontWeight weight, FontStretch stretch,
        [NotNullWhen(true)] out GlyphTypeface? syntheticGlyphTypeface)
        => Target.TryCreateSyntheticGlyphTypeface(glyphTypeface, style, Map(weight, glyphTypeface.FamilyName), stretch, out syntheticGlyphTypeface);

    public bool TryGetNearestMatch(string familyName, FontStyle style, FontWeight weight, FontStretch stretch,
        [NotNullWhen(true)] out GlyphTypeface? glyphTypeface) => Target.TryGetNearestMatch(familyName, style, Map(weight, familyName), stretch, out glyphTypeface);

    public int Count => Target.Count;
    public FontFamily this[int index] => Target[index];
    public IEnumerator<FontFamily> GetEnumerator() => Target.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    public void Dispose() => files?.Dispose();
}

internal enum TypographySource
{
    None,
    /// <summary>Microsoft YaHei UI from the system.</summary>
    System,
    /// <summary>PingFang SC from macOS, using its real Regular, Medium and Semibold faces.</summary>
    MacOSSystem,
    /// <summary>Bundled Noto Sans SC files registered with the platform font manager.</summary>
    BundledSystem,
    /// <summary>Bundled Noto Sans SC files loaded as an Avalonia font collection.</summary>
    BundledCollection
}
