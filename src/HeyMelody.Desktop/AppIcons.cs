using Avalonia;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using FluentAvalonia.UI.Controls;

namespace HeyMelodyWindows.App;

public enum AppIcon
{
    Settings, Refresh, Copy, Charging, NoiseControl, Equalizer, Sound, Earbuds, Gestures,
    Hearing, FitTest, Info, History, Battery, BatteryLeft, BatteryRight, BatteryCase
}

/// <summary>Resolves interface icons from system or FluentAvalonia fonts, bundled HeyMelody device shapes and independent fallbacks.</summary>
public static class AppIcons
{
    private const string SegoeFluentName = "Segoe Fluent Icons", BundledSymbolsName = "Symbols";
    private static readonly FontFamily SegoeFluent = new(SegoeFluentName);
    // FluentAvalonia's bundled symbol font maps the same code points, so Windows 10 and previews keep the glyphs.
    private static readonly FontFamily BundledSymbols = new("avares://FluentAvalonia/Fonts#Symbols");
    private static readonly Dictionary<(string Family, int CodePoint), bool> Coverage = [];
    // Original 24-unit outline for platforms without the system battery glyph.
    private const string BatteryOutline = "M2,6 H20 V18 H2 Z M4,8 V16 H18 V8 Z M20,10 H23 V14 H20 Z";

    // Exact pathData from HeyMelody 17.6.5 battery device vectors; see docs/research/battery-device-icons.md.
    // F0 preserves evenOdd letter/indicator cutouts; the desktop inherits its themed foreground.
    private const string BatteryLeftShape =
        "F0 M7,14C10.866,14 14,10.866 14,7C14,3.134 10.866,0 7,0C3.134,0 0,3.134 0,7C0,10.866 3.134,14 7,14ZM4.621,11.133H10.035V9.726H6.366V2.871H4.621V11.133Z";
    private const string BatteryRightShape =
        "F0 M7,14C10.866,14 14,10.866 14,7C14,3.134 10.866,0 7,0C3.134,0 0,3.134 0,7C0,10.866 3.134,14 7,14ZM3.992,2.871V11.133H5.726V8.026H6.907L8.934,11.133H11.016L8.708,7.79C9.219,7.602 9.613,7.306 9.89,6.901C10.168,6.488 10.307,6.004 10.307,5.449C10.307,4.931 10.187,4.481 9.947,4.098C9.714,3.708 9.369,3.408 8.911,3.198C8.461,2.98 7.924,2.871 7.301,2.871H3.992ZM8.472,5.449C8.472,6.282 7.969,6.698 6.964,6.698H5.726V4.199H6.964C7.969,4.199 8.472,4.616 8.472,5.449Z";
    private const string BatteryCaseShape =
        "F0 M5.667,1.389H5.68H8.32H8.334H8.334C8.627,1.389 8.788,1.389 8.927,1.397C11.148,1.513 13.002,2.995 13.667,5.021H12.967C12.967,5.021 12.967,5.021 12.967,5.02H10.278C10.189,4.642 9.849,4.36 9.443,4.36H4.559C4.153,4.36 3.813,4.642 3.724,5.02H1.034C1.034,5.021 1.033,5.021 1.033,5.021H0.333C0.998,2.995 2.852,1.513 5.074,1.397C5.212,1.389 5.373,1.389 5.666,1.389H5.667ZM13.537,5.879H13.871C13.896,6.048 13.914,6.219 13.923,6.393C13.93,6.531 13.93,6.692 13.93,6.986V6.999V7.013C13.93,7.306 13.93,7.468 13.923,7.606C13.781,10.304 11.625,12.461 8.927,12.602C8.788,12.609 8.627,12.609 8.334,12.609H8.32H5.68H5.666C5.373,12.609 5.212,12.609 5.074,12.602C2.375,12.461 0.219,10.304 0.077,7.606C0.07,7.468 0.07,7.307 0.07,7.013V7.013V6.999V6.986V6.985C0.07,6.692 0.07,6.531 0.077,6.393C0.087,6.219 0.104,6.048 0.129,5.879H0.464C0.464,5.879 0.464,5.879 0.464,5.878H3.798C3.941,6.153 4.228,6.34 4.559,6.34H9.443C9.774,6.34 10.061,6.153 10.204,5.878H13.537C13.537,5.879 13.537,5.879 13.537,5.879ZM7,8.98C7.364,8.98 7.66,8.684 7.66,8.32C7.66,7.955 7.364,7.66 7,7.66C6.635,7.66 6.34,7.955 6.34,8.32C6.34,8.684 6.635,8.98 7,8.98Z";

    private abstract record Candidate;
    private sealed record Glyph(int CodePoint) : Candidate;
    private sealed record Vector(string Data, double Viewport) : Candidate;

    private static Candidate[] Candidates(AppIcon icon) => icon switch
    {
        AppIcon.Settings => [new Glyph(0xE713)],
        AppIcon.Refresh => [new Glyph(0xE72C)],
        AppIcon.Copy => [new Glyph(0xE8C8)],
        AppIcon.Charging => [new Glyph(0xE945)],
        AppIcon.NoiseControl => [new Glyph(0xF61F)],
        AppIcon.Equalizer => [new Glyph(0xE9E9)],
        AppIcon.Sound => [new Glyph(0xE995)],
        AppIcon.Earbuds => [new Glyph(0xF4C0)],
        AppIcon.Gestures => [new Glyph(0xE815)],
        AppIcon.Hearing => [new Glyph(0xF270)],
        AppIcon.FitTest => [new Glyph(0xF27C)],
        AppIcon.Info => [new Glyph(0xE946)],
        AppIcon.History => [new Glyph(0xE81C)],
        AppIcon.Battery => [new Glyph(0xE850), new Vector(BatteryOutline, 24)],
        AppIcon.BatteryLeft => [new Vector(BatteryLeftShape, 14)],
        AppIcon.BatteryRight => [new Vector(BatteryRightShape, 14)],
        AppIcon.BatteryCase => [new Vector(BatteryCaseShape, 14)],
        _ => throw new ArgumentOutOfRangeException(nameof(icon))
    };

    /// <summary>Creates a new icon source; icon sources are never shared between controls.</summary>
    public static FAIconSource Source(AppIcon icon, double size = 20)
    {
        foreach (var candidate in Candidates(icon))
        {
            switch (candidate)
            {
                case Glyph glyph when Covers(SegoeFluent, SegoeFluentName, glyph.CodePoint):
                    return Font(SegoeFluent, glyph.CodePoint, size);
                case Glyph glyph when Covers(BundledSymbols, BundledSymbolsName, glyph.CodePoint):
                    return Font(BundledSymbols, glyph.CodePoint, size);
                case Vector vector:
                    // Keep the fallback's padding at the requested icon size.
                    var data = Geometry.Parse(vector.Data);
                    data.Transform = new ScaleTransform(size / vector.Viewport, size / vector.Viewport);
                    return new FAPathIconSource { Data = data, Stretch = Stretch.None };
            }
        }
        return new FASymbolIconSource { Symbol = icon is AppIcon.NoiseControl or AppIcon.Hearing or AppIcon.FitTest or AppIcon.Earbuds
            ? FASymbol.Audio : FASymbol.Help, FontSize = size };
    }

    public static FAIconSourceElement Element(AppIcon icon, double size = 16) =>
        new() { IconSource = Source(icon, size), Width = size, Height = size };

    private static FAFontIconSource Font(FontFamily family, int codePoint, double size) =>
        new() { FontFamily = family, Glyph = char.ConvertFromUtf32(codePoint), FontSize = size };

    private static bool Covers(FontFamily family, string familyName, int codePoint)
    {
        if (Coverage.TryGetValue((familyName, codePoint), out var covered)) return covered;
        // A missing family resolves to the default text font, so compare the family that was found.
        covered = FontManager.Current.TryGetGlyphTypeface(new Typeface(family), out var typeface)
            && string.Equals(typeface.FamilyName, familyName, StringComparison.OrdinalIgnoreCase)
            && typeface.CharacterToGlyphMap.ContainsGlyph(codePoint);
        Coverage[(familyName, codePoint)] = covered;
        return covered;
    }
}

/// <summary>XAML access to <see cref="AppIcons"/>, for example <c>{local:AppIcon Equalizer}</c>.</summary>
public sealed class AppIconExtension : MarkupExtension
{
    public AppIconExtension() { }
    public AppIconExtension(AppIcon icon) => Icon = icon;
    public AppIcon Icon { get; set; }
    public double Size { get; set; } = 20;
    public override object ProvideValue(IServiceProvider serviceProvider) => AppIcons.Source(Icon, Size);
}
