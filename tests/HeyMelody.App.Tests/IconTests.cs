using System.Security.Cryptography;
using System.Text.Json;
using System.Xml.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using FluentAvalonia.UI.Controls;
using HeyMelodyWindows.App;

internal static class IconTests
{
    private const string AssetRoot = "avares://HeyMelody.Desktop/Assets/Icons/";
    private const string Revision = "9bfae3d782fda4c8764f2128cec4d0dda160b283";

    internal static int Run(HeadlessUnitTestSession session)
    {
        Run("Fluent icon catalogue covers every supplied icon and preserves pinned source hashes", Catalogue);
        Run("all icon sizes preserve their viewport and create independent vector sources", Sources);
        Run("Fluent icons inherit foreground in both light and dark themes", Foreground);
        return 3;

        void Run(string name, Action action)
        {
            session.Dispatch<bool>(() => { action(); return Task.FromResult(true); }, CancellationToken.None).GetAwaiter().GetResult();
            Console.WriteLine("PASS " + name);
        }
    }

    internal static bool Matches(FAIconSource? source, AppIcon role, double size = 20)
    {
        if (source is not FAPathIconSource { Data: { } actual }
            || AppIcons.Source(role, size) is not FAPathIconSource { Data: { } expected }
            || actual.Bounds != expected.Bounds)
            return false;
        for (var row = 0; row < 24; row++)
        for (var column = 0; column < 24; column++)
        {
            var point = new Point((column + 0.5) * size / 24, (row + 0.5) * size / 24);
            if (actual.FillContains(point) != expected.FillContains(point))
                return false;
        }
        return true;
    }

    private static void Catalogue()
    {
        using var stream = AssetLoader.Open(new Uri(AssetRoot + "fluent-icons.json"));
        using var document = JsonDocument.Parse(stream);
        var root = document.RootElement;
        Check(root.GetProperty("revision").GetString() == Revision && root.GetProperty("style").GetString() == "regular",
            "The icon subset pins the reviewed upstream revision and Regular style.");
        var roles = root.GetProperty("roles");
        var expectedRoles = Enum.GetValues<AppIcon>().Where(role =>
            role is not (AppIcon.BatteryLeft or AppIcon.BatteryRight or AppIcon.BatteryCase)).ToArray();
        Check(roles.EnumerateObject().Count() == expectedRoles.Length
            && expectedRoles.All(role => roles.TryGetProperty(role.ToString(), out _)),
            "Every supplied icon has an explicit mapping; original HeyMelody device shapes stay outside the catalogue.");
        foreach (var (role, slug) in new[]
        {
            (AppIcon.NoiseControl, "sound_wave_circle"), (AppIcon.Hearing, "headphones_sound_wave"),
            (AppIcon.FitTest, "surface_earbuds"), (AppIcon.Equalizer, "options"), (AppIcon.SelectAll, "select_all_on")
        })
            Check(roles.GetProperty(role.ToString()).GetString() == slug, role + " retains its reviewed semantic mapping.");
        var assets = root.GetProperty("assets").EnumerateArray().ToArray();
        foreach (var asset in assets)
        {
            var file = asset.GetProperty("file").GetString()!;
            using var svg = AssetLoader.Open(new Uri(AssetRoot + file));
            var hash = Convert.ToHexStringLower(SHA256.HashData(svg));
            Check(hash == asset.GetProperty("sha256").GetString(),
                file + " is byte-for-byte identical to the recorded upstream source.");
            Check(asset.GetProperty("upstreamPath").GetString()!.EndsWith("/ic_fluent_" + file, StringComparison.Ordinal),
                file + " keeps its exact upstream path.");
            using var original = AssetLoader.Open(new Uri(AssetRoot + file));
            var svgRoot = XDocument.Load(original).Root!;
            var path = svgRoot.Elements(svgRoot.Name.Namespace + "path").Single();
            var evenOdd = (path.Attribute("fill-rule") ?? svgRoot.Attribute("fill-rule"))?.Value == "evenodd";
            var expected = Geometry.Parse((evenOdd ? "F0 " : "F1 ") + path.Attribute("d")!.Value);
            var size = asset.GetProperty("size").GetInt32();
            var slug = file[..file.LastIndexOf($"_{size}_regular.svg", StringComparison.Ordinal)];
            var role = Enum.Parse<AppIcon>(roles.EnumerateObject().First(item => item.Value.GetString() == slug).Name);
            var actual = ((FAPathIconSource)AppIcons.Source(role, size)).Data!;
            for (var row = 0; row < 32; row++)
            for (var column = 0; column < 32; column++)
            {
                var point = new Point((column + 0.5) * size / 32, (row + 0.5) * size / 32);
                Check(actual.FillContains(point) == expected.FillContains(point),
                    file + " preserves the original fill; its viewport contours never add painted pixels.");
            }
        }
        Check(roles.EnumerateObject().All(role => assets.Any(asset =>
            asset.GetProperty("file").GetString() == role.Value.GetString() + "_20_regular.svg")),
            "Every role has its normal 20-unit resource; missing 16-unit variants never cause font fallback.");
    }

    private static void Sources()
    {
        foreach (var role in Enum.GetValues<AppIcon>())
        foreach (var size in new double[] { 12, 16, 20, 24, 96 })
        {
            var first = AppIcons.Source(role, size);
            var second = AppIcons.Source(role, size);
            Check(first is FAPathIconSource { Data: { } } && second is FAPathIconSource { Data: { } },
                role + " uses a bundled vector, not a platform symbol font.");
            var data = ((FAPathIconSource)first).Data!;
            Check(!ReferenceEquals(first, second) && !ReferenceEquals(data, ((FAPathIconSource)second).Data),
                role + " sources and mutable geometries are independent.");
            if (role is not (AppIcon.BatteryLeft or AppIcon.BatteryRight or AppIcon.BatteryCase))
            {
                var bounds = data.Bounds;
                Check(Math.Abs(bounds.X) < 0.001 && Math.Abs(bounds.Y) < 0.001
                    && Math.Abs(bounds.Width - size) < 0.001 && Math.Abs(bounds.Height - size) < 0.001,
                    $"{role} preserves its original SVG canvas at {size}: {bounds}.");
            }
        }
    }

    private static void Foreground()
    {
        foreach (var theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
        {
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
                var paths = window.GetVisualDescendants().OfType<FAPathIcon>().ToArray();
                Check(paths.Length == 3 && paths.All(path =>
                    path.Foreground is ISolidColorBrush brush && brush.Color == expected
                    && path.Bounds.Width > 0 && path.Bounds.Height > 0),
                    theme + " preserves inherited foreground and nonzero layout at small, normal and product-fallback sizes.");
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
