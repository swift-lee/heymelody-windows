using Avalonia;

namespace HeyMelodyWindows.App;

internal readonly record struct ModelRenderPolicy(int MaximumLongEdgePixels, bool ProbeBeforeBitmapLock)
{
    internal static ModelRenderPolicy PlatformDefault => new(800, true);

    internal void Validate()
    {
        if (MaximumLongEdgePixels < 0)
            throw new ArgumentOutOfRangeException(nameof(MaximumLongEdgePixels));
    }

    internal PixelSize GetPixelSize(Size logicalSize, double renderScaling)
    {
        Validate();
        if (!double.IsFinite(logicalSize.Width) || !double.IsFinite(logicalSize.Height)
            || logicalSize.Width <= 0 || logicalSize.Height <= 0)
            throw new ArgumentOutOfRangeException(nameof(logicalSize));
        if (!double.IsFinite(renderScaling) || renderScaling <= 0)
            throw new ArgumentOutOfRangeException(nameof(renderScaling));

        var width = checked((int)Math.Ceiling(logicalSize.Width * renderScaling));
        var height = checked((int)Math.Ceiling(logicalSize.Height * renderScaling));
        var longest = Math.Max(width, height);
        if (MaximumLongEdgePixels == 0 || longest <= MaximumLongEdgePixels)
            return new PixelSize(width, height);

        // Only the backing pixels change; the Image keeps its logical layout and pointer coordinates.
        var scale = (double)MaximumLongEdgePixels / longest;
        return new PixelSize(
            Math.Max(1, (int)Math.Round(width * scale, MidpointRounding.AwayFromZero)),
            Math.Max(1, (int)Math.Round(height * scale, MidpointRounding.AwayFromZero)));
    }
}
