using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using FluentAvalonia.UI.Controls;
using HeyMelodyWindows.Resources;

namespace HeyMelodyWindows.App;

/// <summary>A bitmap resource description; loading is deferred until it is displayed.</summary>
public sealed record BitmapVisual(Uri Source);
public sealed record EncoFree4ModelVisual;
public sealed record EncoFree4DynaudioModelVisual;
public sealed record GenericProductVisual;
/// <summary>The device's own artwork is still being looked up or downloaded for the first time.</summary>
public sealed record LoadingProductVisual;

/// <summary>Built-in presentation data and its default template, independent of device identification.</summary>
public static class DeviceVisuals
{
    public static BitmapVisual X4Product { get; } = new(new Uri(
        "avares://HeyMelody/Assets/Devices/EncoX4/x4-pods-with-earphones.png"));
    public static GenericProductVisual GenericProduct { get; } = new();
    public static LoadingProductVisual LoadingProduct { get; } = new();
    public static IDataTemplate DefaultTemplate { get; } = new DeviceVisualTemplate();

    private static readonly object BitmapLock = new();
    private static readonly Dictionary<Uri, Bitmap> Bitmaps = new();
    private static readonly string[] AncGlyphNames = ["melody_ui_reduction_noise_close", "melody_ui_reduction_noise_transparent",
        "melody_ui_reduction_noise_adaptive", "melody_ui_reduction_noise_weak", "melody_ui_reduction_noise_strong"];

    // The bundled glyph index is fixed for the process. Only the resolved paths are kept: the bundled artwork
    // index (about 224 KB of JSON) is parsed once and released instead of living for the whole process.
    private static readonly Lazy<IReadOnlyDictionary<string, string?>> AncGlyphPaths = new(() =>
    {
        try
        {
            var bundled = new BundledResourceStore();
            return AncGlyphNames.ToDictionary(name => name,
                name => bundled.FindNamedAsset(name) is { } found ? bundled.FullPath(found) : null);
        }
        catch (Exception) { return new Dictionary<string, string?>(); }
    }, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>
    /// Decodes the bundled default artwork and resolves the noise-control glyph paths on a worker thread, so the
    /// first window and the first noise-control panel do not pause the UI thread for a decode or the index parse.
    /// </summary>
    internal static Task PrewarmBundledArtworkAsync() => Task.Run(() =>
    {
        _ = AncGlyphPaths.Value;
        try { GetBitmap(X4Product.Source); }
        catch (Exception) { }
    });

    private static Bitmap GetBitmap(Uri source)
    {
        lock (BitmapLock)
        {
            if (Bitmaps.TryGetValue(source, out var bitmap)) return bitmap;
            using var stream = source.IsFile ? File.OpenRead(source.LocalPath) : AssetLoader.Open(source);
            bitmap = new Bitmap(stream);
            Bitmaps.Add(source, bitmap);
            return bitmap;
        }
    }

    internal static Control CreateAncGlyph(string key, string brushKey)
    {
        var name = key switch { "off" => "melody_ui_reduction_noise_close", "transparency" => "melody_ui_reduction_noise_transparent",
            "adaptive" or "smart" => "melody_ui_reduction_noise_adaptive", "weak" => "melody_ui_reduction_noise_weak", _ => "melody_ui_reduction_noise_strong" };
        try
        {
            if (AncGlyphPaths.Value.GetValueOrDefault(name) is { } path)
            {
                var glyph = new Border { Width = 26, Height = 26, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                    OpacityMask = new ImageBrush(GetBitmap(new Uri(path))) { Stretch = Stretch.Uniform } };
                glyph.Bind(Border.BackgroundProperty, new DynamicResourceExtension(brushKey));
                return glyph;
            }
        }
        catch (IOException) { }
        var fallback = AppIcons.Element(key switch { "off" => AppIcon.Cancel, "transparency" => AppIcon.Speaker2, "adaptive" or "smart" => AppIcon.Sync, _ => AppIcon.Audio }, 24);
        fallback.Bind(FAIconElement.ForegroundProperty, new DynamicResourceExtension(brushKey));
        return fallback;
    }

    /// <summary>The shared loading state of the left-hand product area; never a text prompt.</summary>
    internal static Control CreateLoadingIndicator() => new HeroLoadingIndicator();

    /// <summary>Releases shared bitmaps after all application windows have closed.</summary>
    public static void DisposeResources()
    {
        lock (BitmapLock)
        {
            foreach (var bitmap in Bitmaps.Values) bitmap.Dispose();
            Bitmaps.Clear();
        }
    }

    private sealed class DeviceVisualTemplate : IDataTemplate
    {
        public bool Match(object? data) => data is BitmapVisual or FileBitmapVisual or DeviceModelVisual or LottieVisual or AnimatedWebpVisual or EncoFree4ModelVisual or EncoFree4DynaudioModelVisual or GenericProductVisual or LoadingProductVisual;

        public Control? Build(object? data) => data switch
        {
            BitmapVisual image => new Image { Source = GetBitmap(image.Source), Stretch = Stretch.Uniform },
            FileBitmapVisual image => new ArtworkBitmapView(image),
            DeviceModelVisual model => new DeviceModelView(model),
            LottieVisual animation => new DeviceLottieView(animation),
            AnimatedWebpVisual animation => new DeviceAnimatedWebpView(animation),
            EncoFree4ModelVisual => new EncoFree4ModelView(),
            EncoFree4DynaudioModelVisual => new EncoFree4DynaudioModelView(),
            GenericProductVisual => CreateGenericProduct(),
            LoadingProductVisual => CreateLoadingIndicator(),
            _ => null
        };

        private static Control CreateGenericProduct()
        {
            var icon = AppIcons.Element(AppIcon.Audio, 96);
            icon.Bind(FAIconElement.ForegroundProperty, new DynamicResourceExtension("TextFillColorSecondaryBrush"));
            var shell = new Border
            {
                Width = 220,
                Height = 220,
                CornerRadius = new CornerRadius(110),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Child = icon
            };
            shell.Bind(Border.BackgroundProperty, new DynamicResourceExtension("ControlFillColorDefaultBrush"));
            return new Viewbox { Stretch = Stretch.Uniform, MaxWidth = 220, MaxHeight = 220, Child = shell };
        }
    }
}
