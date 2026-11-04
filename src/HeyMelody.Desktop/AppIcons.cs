using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Xml.Linq;
using Avalonia;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Platform;
using FluentAvalonia.UI.Controls;

namespace HeyMelodyWindows.App;

public enum AppIcon
{
    Settings, Refresh, Copy, Charging, NoiseControl, Equalizer, Sound, Earbuds, Gestures,
    Hearing, FitTest, Info, History, Battery, BatteryLeft, BatteryRight, BatteryCase,
    Add, DarkTheme, Sync, Rotate, Globe, Permissions, CloudDownload, SpeakerBluetooth, Repair, OpenFolder, Help,
    Play, Pause, Speaker0, Speaker2, SpeakerMute, Rename, Checkmark, Audio, Cancel,
    ChevronDown, Subtract, Warning, Error, Success, Dismiss, Cut, Paste, Undo, Redo, SelectAll
}

/// <summary>Resolves bundled Fluent System Icons and the original HeyMelody battery device shapes.</summary>
public static class AppIcons
{
    private const string AssetRoot = "avares://HeyMelody.Desktop/Assets/Icons/";
    private static readonly Lazy<IconCatalog> Catalog = new(LoadCatalog);
    private static readonly ConcurrentDictionary<string, SvgPath> Paths = new();

    // Exact pathData from HeyMelody 17.6.5 battery device vectors; see docs/research/battery-device-icons.md.
    // F0 preserves evenOdd letter/indicator cutouts; the desktop inherits its themed foreground.
    private const string BatteryLeftShape =
        "F0 M7,14C10.866,14 14,10.866 14,7C14,3.134 10.866,0 7,0C3.134,0 0,3.134 0,7C0,10.866 3.134,14 7,14ZM4.621,11.133H10.035V9.726H6.366V2.871H4.621V11.133Z";
    private const string BatteryRightShape =
        "F0 M7,14C10.866,14 14,10.866 14,7C14,3.134 10.866,0 7,0C3.134,0 0,3.134 0,7C0,10.866 3.134,14 7,14ZM3.992,2.871V11.133H5.726V8.026H6.907L8.934,11.133H11.016L8.708,7.79C9.219,7.602 9.613,7.306 9.89,6.901C10.168,6.488 10.307,6.004 10.307,5.449C10.307,4.931 10.187,4.481 9.947,4.098C9.714,3.708 9.369,3.408 8.911,3.198C8.461,2.98 7.924,2.871 7.301,2.871H3.992ZM8.472,5.449C8.472,6.282 7.969,6.698 6.964,6.698H5.726V4.199H6.964C7.969,4.199 8.472,4.616 8.472,5.449Z";
    private const string BatteryCaseShape =
        "F0 M5.667,1.389H5.68H8.32H8.334H8.334C8.627,1.389 8.788,1.389 8.927,1.397C11.148,1.513 13.002,2.995 13.667,5.021H12.967C12.967,5.021 12.967,5.021 12.967,5.02H10.278C10.189,4.642 9.849,4.36 9.443,4.36H4.559C4.153,4.36 3.813,4.642 3.724,5.02H1.034C1.034,5.021 1.033,5.021 1.033,5.021H0.333C0.998,2.995 2.852,1.513 5.074,1.397C5.212,1.389 5.373,1.389 5.666,1.389H5.667ZM13.537,5.879H13.871C13.896,6.048 13.914,6.219 13.923,6.393C13.93,6.531 13.93,6.692 13.93,6.986V6.999V7.013C13.93,7.306 13.93,7.468 13.923,7.606C13.781,10.304 11.625,12.461 8.927,12.602C8.788,12.609 8.627,12.609 8.334,12.609H8.32H5.68H5.666C5.373,12.609 5.212,12.609 5.074,12.602C2.375,12.461 0.219,10.304 0.077,7.606C0.07,7.468 0.07,7.307 0.07,7.013V7.013V6.999V6.986V6.985C0.07,6.692 0.07,6.531 0.077,6.393C0.087,6.219 0.104,6.048 0.129,5.879H0.464C0.464,5.879 0.464,5.879 0.464,5.878H3.798C3.941,6.153 4.228,6.34 4.559,6.34H9.443C9.774,6.34 10.061,6.153 10.204,5.878H13.537C13.537,5.879 13.537,5.879 13.537,5.879ZM7,8.98C7.364,8.98 7.66,8.684 7.66,8.32C7.66,7.955 7.364,7.66 7,7.66C6.635,7.66 6.34,7.955 6.34,8.32C6.34,8.684 6.635,8.98 7,8.98Z";

    /// <summary>Creates a new icon source; icon sources are never shared between controls.</summary>
    public static FAIconSource Source(AppIcon icon, double size = 20)
    {
        if (!double.IsFinite(size) || size <= 0)
            throw new ArgumentOutOfRangeException(nameof(size));

        var batteryShape = icon switch
        {
            AppIcon.BatteryLeft => BatteryLeftShape,
            AppIcon.BatteryRight => BatteryRightShape,
            AppIcon.BatteryCase => BatteryCaseShape,
            _ => null
        };
        if (batteryShape is not null)
        {
            var battery = Geometry.Parse(batteryShape);
            battery.Transform = new ScaleTransform(size / 14, size / 14);
            return new FAPathIconSource { Data = battery, Stretch = Stretch.None };
        }

        if (!Catalog.Value.Roles.TryGetValue(icon, out var slug))
            throw new ArgumentOutOfRangeException(nameof(icon));
        var file = $"{slug}_{(size <= 16 && Catalog.Value.Files.Contains($"{slug}_16_regular.svg") ? 16 : 20)}_regular.svg";
        var svg = Paths.GetOrAdd(file, LoadPath);
        // Opposite closed contours cancel their fill but retain the SVG canvas in Skia's path bounds.
        var viewport = FormattableString.Invariant(
            $"M0 0 H{svg.Viewport} V{svg.Viewport} H0 Z M0 0 V{svg.Viewport} H{svg.Viewport} V0 Z");
        var geometry = Geometry.Parse((svg.FillRule == FillRule.EvenOdd ? "F0 " : "F1 ") + svg.Data + " " + viewport);
        geometry.Transform = new ScaleTransform(size / svg.Viewport, size / svg.Viewport);
        return new FAPathIconSource { Data = geometry, Stretch = Stretch.None };
    }

    public static FAIconSourceElement Element(AppIcon icon, double size = 16) =>
        new() { IconSource = Source(icon, size), Width = size, Height = size };

    private static IconCatalog LoadCatalog()
    {
        using var stream = AssetLoader.Open(new Uri(AssetRoot + "fluent-icons.json"));
        using var document = JsonDocument.Parse(stream);
        var roles = document.RootElement.GetProperty("roles").EnumerateObject()
            .ToDictionary(x => Enum.Parse<AppIcon>(x.Name), x => x.Value.GetString()!);
        var files = document.RootElement.GetProperty("assets").EnumerateArray()
            .Select(x => x.GetProperty("file").GetString()!).ToHashSet(StringComparer.Ordinal);
        return new IconCatalog(roles, files);
    }

    private static SvgPath LoadPath(string file)
    {
        using var stream = AssetLoader.Open(new Uri(AssetRoot + file));
        var root = XDocument.Load(stream).Root ?? throw new InvalidDataException("The bundled Fluent SVG has no root.");
        var viewport = root.Attribute("viewBox")!.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(x => double.Parse(x, CultureInfo.InvariantCulture)).ToArray();
        var path = root.Elements(root.Name.Namespace + "path").Single();
        if (viewport.Length != 4 || viewport[0] != 0 || viewport[1] != 0 || viewport[2] <= 0
            || viewport[2] != viewport[3] || path.Attribute("transform") is not null)
            throw new InvalidDataException("The bundled Fluent SVG does not use the curated square path format.");
        var fillRule = (path.Attribute("fill-rule") ?? root.Attribute("fill-rule"))?.Value == "evenodd"
            ? FillRule.EvenOdd : FillRule.NonZero;
        return new SvgPath(path.Attribute("d")!.Value, viewport[2], fillRule);
    }

    private sealed record IconCatalog(IReadOnlyDictionary<AppIcon, string> Roles, IReadOnlySet<string> Files);
    private sealed record SvgPath(string Data, double Viewport, FillRule FillRule);
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
