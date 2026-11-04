using System.Numerics;
using System.Text.Json;
using System.Security.Cryptography;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Rendering.Composition;
using Avalonia.Skia;
using Avalonia.Threading;
using Avalonia.VisualTree;
using SkiaSharp;
using SkiaSharp.Resources;
using SkiaSharp.Skottie;
using ResourceProvider = SkiaSharp.Resources.ResourceProvider;

namespace HeyMelodyWindows.Artwork;

/// <summary>
/// Loop repeats the whole timeline (guides and status animations). Product presents device artwork:
/// marker-defined intro/loop segments, otherwise one pass held on its last frame unless the timeline is
/// seamless, with transparent canvas margins cropped away.
/// </summary>
public enum LottiePresentation { Loop, Product }

/// <summary>Skottie artwork rendered by Avalonia's compositor without a UI frame timer.</summary>
public class LottieView(ILottieSource source, Func<Control?>? fallback = null,
    LottiePresentation presentation = LottiePresentation.Loop, Func<Control?>? poster = null,
    Func<Control?>? loading = null) : UserControl
{
    private static readonly TimeSpan PosterDelay = TimeSpan.FromMilliseconds(250);
    private CompositionCustomVisual? _composition;
    private Window? _window;
    private int _generation;
    private bool _attached;
    private VisibilityTracker? _visibility;
    private long _renderedFrames;
    private double _presentedElapsedSeconds;
    private Size _sourceSize;
    private DispatcherTimer? _poster;
    internal long RenderedFrameCount => Interlocked.Read(ref _renderedFrames);
    internal double PresentedElapsedSeconds => Volatile.Read(ref _presentedElapsedSeconds);
    internal Func<TimeSpan>? AnimationClockForTests { get; set; }
    public bool AnimationActive { get; private set; }
    public bool AnimationLoaded { get; private set; }
    public string? LoadError { get; private set; }
    internal LottiePlan? Plan { get; private set; }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _attached = true;
        _window = TopLevel.GetTopLevel(this) as Window;
        _visibility = new VisibilityTracker(this, RefreshActivity);
        int generation = ++_generation;
        // The caller's loading state shows until the animation replaces it; a poster that follows within the ring's
        // own reveal delay would only flash it, so a poster takes its place.
        if (loading is not null && poster is null && !AnimationLoaded) Content = loading();
        if (poster is not null && !AnimationLoaded)
        {
            // A slow parse shows the static image; a fast one never flashes it.
            _poster = new DispatcherTimer { Interval = PosterDelay };
            _poster.Tick += (_, _) => { StopPoster(); if (_attached && generation == _generation && !AnimationLoaded && LoadError is null) Content = poster(); };
            _poster.Start();
        }
        _ = LoadAsync(generation);
    }

    private async Task LoadAsync(int generation)
    {
        LottiePlayback? playback = null;
        try
        {
            playback = await Task.Run(() => LottiePlayback.Load(source, presentation));
            if (!_attached || generation != _generation) { playback.Dispose(); return; }
            var parent = ElementComposition.GetElementVisual(this);
            if (parent is null) { playback.Dispose(); StopPoster(); Content = null; return; }
            StopPoster(); Content = null;
            Plan = playback.Plan;
            _sourceSize = new Size(playback.Plan.Crop.Width, playback.Plan.Crop.Height);
            _composition = parent.Compositor.CreateCustomVisual(new LottieHandler(playback, elapsed =>
            {
                Volatile.Write(ref _presentedElapsedSeconds, elapsed);
                Interlocked.Increment(ref _renderedFrames);
            }, AnimationClockForTests));
            AnimationLoaded = true;
            playback = null; // The compositor owns all native Skia objects from here.
            ElementComposition.SetElementChildVisual(this, _composition);
            InvalidateMeasure(); InvalidateArrange(); Resize(); RefreshActivity();
        }
        catch (Exception error)
        {
            playback?.Dispose();
            if (!_attached || generation != _generation) return;
            StopPoster();
            LoadError = error.Message;
            Content = fallback?.Invoke();
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _attached = false; ++_generation;
        StopPoster();
        _visibility?.Dispose();
        _visibility = null;
        _window = null;
        _composition?.SendHandlerMessage(LottieCommand.Dispose);
        ElementComposition.SetElementChildVisual(this, null);
        _composition = null; AnimationActive = false; AnimationLoaded = false;
        base.OnDetachedFromVisualTree(e);
    }
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.Property == BoundsProperty) { Resize(); RefreshActivity(); }
        if (e.Property == IsVisibleProperty) RefreshActivity();
    }
    protected override Size MeasureOverride(Size availableSize)
        => Content is not null ? base.MeasureOverride(availableSize)
            : Stretch.Uniform.CalculateSize(availableSize, _sourceSize);
    private void StopPoster() { _poster?.Stop(); _poster = null; }
    private void Resize() { if (_composition is not null) _composition.Size = new Vector2((float)Bounds.Width, (float)Bounds.Height); }
    private void RefreshActivity()
    {
        bool active = _attached && _composition is not null && Bounds.Width > 0 && Bounds.Height > 0
            && double.IsFinite(Bounds.Width) && double.IsFinite(Bounds.Height) && IsEffectivelyVisible
            && _window is { IsVisible: true } && _window.WindowState != WindowState.Minimized;
        if (active == AnimationActive) return;
        AnimationActive = active;
        _composition?.SendHandlerMessage(AnimationActive ? LottieCommand.Play : LottieCommand.Pause);
    }

    private enum LottieCommand { Play, Pause, Dispose }
    private sealed class LottieHandler(LottiePlayback playback, Action<double> rendered, Func<TimeSpan>? clock) : CompositionCustomVisualHandler
    {
        private LottiePlayback? _playback = playback;
        private bool _playing, _settled;
        // A held final frame is rendered once into a GPU surface and redrawn as an image; looping frames never are.
        private SKImage? _settledImage;
        private GRContext? _settledContext;
        private SKRect _settledBounds;
        private SKSizeI _settledPixels;
        private TimeSpan? _lastFrame;
        private double _elapsed;
        public override void OnMessage(object message)
        {
            if (message is not LottieCommand command) return;
            ReleaseSettledImage();
            if (command == LottieCommand.Dispose)
            { _playing = false; _playback?.Dispose(); _playback = null; clock = null; return; }
            _playing = command == LottieCommand.Play;
            _lastFrame = null;
            Invalidate();
            if (_playing && !_settled) RegisterForNextAnimationFrameUpdate();
        }
        public override void OnAnimationFrameUpdate()
        {
            if (!_playing || _playback is null || _settled) return;
            var now = clock?.Invoke() ?? CompositionNow;
            if (_lastFrame is { } last) _elapsed += Math.Max(0, (now - last).TotalSeconds);
            _lastFrame = now;
            _playback.Plan.TimeAt(_elapsed, out _settled);
            Invalidate();
            // A held final frame needs no further frame callbacks.
            if (!_settled) RegisterForNextAnimationFrameUpdate();
        }
        public override void OnRender(ImmediateDrawingContext context)
        {
            if (_playback is not { } loaded || context.TryGetFeature<ISkiaSharpApiLeaseFeature>() is not { } feature) return;
            var bounds = GetRenderBounds();
            if (bounds.Width <= 0 || bounds.Height <= 0) return;
            var animation = loaded.Animation; var crop = loaded.Plan.Crop;
            animation.SeekFrameTime(loaded.Plan.TimeAt(_elapsed, out _));
            // Fit the content region, then draw the whole canvas positioned around it.
            var scale = Math.Min(bounds.Width / crop.Width, bounds.Height / crop.Height);
            double left = (bounds.Width - crop.Width * scale) / 2 - crop.Left * scale, top = (bounds.Height - crop.Height * scale) / 2 - crop.Top * scale;
            var destination = new SKRect((float)left, (float)top,
                (float)(left + animation.Size.Width * scale), (float)(top + animation.Size.Height * scale));
            using (context.PushClip(bounds))
            {
                using var lease = feature.Lease();
                if (!(_settled && lease.GrContext is { } gpu && DrawSettled(lease.SkCanvas, gpu, animation, destination, bounds)))
                { ReleaseSettledImage(); animation.Render(lease.SkCanvas, destination); }
            }
            rendered(_elapsed);
        }

        private bool DrawSettled(SKCanvas canvas, GRContext gpu, Animation animation, SKRect destination, Rect bounds)
        {
            var matrix = canvas.TotalMatrix;
            var pixels = new SKSizeI(Math.Max(1, (int)Math.Ceiling(bounds.Width * Math.Abs(matrix.ScaleX))), Math.Max(1, (int)Math.Ceiling(bounds.Height * Math.Abs(matrix.ScaleY))));
            var local = new SKRect(0, 0, (float)bounds.Width, (float)bounds.Height);
            if (_settledImage is null || _settledContext != gpu || _settledPixels != pixels || _settledBounds != local)
            {
                ReleaseSettledImage();
                using var surface = SKSurface.Create(gpu, false, new SKImageInfo(pixels.Width, pixels.Height));
                if (surface is null) return false;
                surface.Canvas.Scale(pixels.Width / local.Width, pixels.Height / local.Height);
                animation.Render(surface.Canvas, destination);
                _settledImage = surface.Snapshot(); _settledContext = gpu; _settledPixels = pixels; _settledBounds = local;
            }
            canvas.DrawImage(_settledImage, local);
            return true;
        }

        private void ReleaseSettledImage() { _settledImage?.Dispose(); _settledImage = null; _settledContext = null; }
    }
}

/// <summary>Times are seconds relative to the animation in-point; Crop is in canvas coordinates.</summary>
internal sealed record LottiePlan(double IntroStart, double IntroEnd, double LoopStart, double LoopEnd, bool Loops, double Hold, SKRect Crop)
{
    public double TimeAt(double elapsed, out bool settled)
    {
        settled = false;
        double intro = IntroEnd - IntroStart;
        if (elapsed < intro) return IntroStart + elapsed;
        if (Loops && LoopEnd > LoopStart) return LoopStart + (elapsed - intro) % (LoopEnd - LoopStart);
        settled = true;
        return Hold;
    }
}

/// <summary>Validates local image references before allowing Skottie's package provider to open them.</summary>
internal sealed class LottiePlayback : IDisposable
{
    public Animation Animation { get; }
    public LottiePlan Plan { get; }
    private readonly IDisposable? _lease;
    private readonly ResourceProvider _provider;
    private readonly ResourceProvider _files;
    private LottiePlayback(Animation animation, LottiePlan plan, IDisposable? lease, ResourceProvider provider, ResourceProvider files)
    { Animation = animation; Plan = plan; _lease = lease; _provider = provider; _files = files; }

    public static LottiePlayback Load(ILottieSource file, LottiePresentation presentation = LottiePresentation.Loop)
    {
        var lease = file.Acquire();
        FileResourceProvider? files = null;
        DataUriResourceProvider? provider = null;
        Animation? animation = null;
        try
        {
            using var stream = file.Open();
            if (stream.Length is <= 0 or > 24 * 1024 * 1024) throw new InvalidDataException("Animation file exceeds its limit.");
            using var memory = new MemoryStream(); stream.CopyTo(memory);
            var root = file.Source.IsFile ? Path.GetDirectoryName(file.Source.LocalPath)! : file.PackageRoot;
            var bytes = memory.ToArray();
            if (!string.IsNullOrEmpty(file.Sha256) && !Convert.ToHexString(SHA256.HashData(bytes)).Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Animation integrity check failed.");
            ValidateImages(bytes, root, file.PackageRoot);
            files = new FileResourceProvider(root, true);
            provider = new DataUriResourceProvider(files, true);
            using var builder = Animation.CreateBuilder();
            builder.SetResourceProvider(provider);
            memory.Position = 0;
            animation = builder.Build(memory) ?? throw new InvalidDataException("Unsupported animation.");
            if (animation.Size.Width is <= 0 or > 8192 || animation.Size.Height is <= 0 or > 8192
                || animation.Duration <= TimeSpan.Zero || animation.Duration > TimeSpan.FromHours(1))
                throw new InvalidDataException("Animation bounds exceed their limit.");
            var plan = presentation == LottiePresentation.Product ? ProductPlan(animation, bytes) : LoopPlan(animation);
            return new LottiePlayback(animation, plan, lease, provider, files);
        }
        catch { animation?.Dispose(); provider?.Dispose(); files?.Dispose(); lease?.Dispose(); throw; }
    }

    internal static LottiePlan LoopPlan(Animation animation)
    {
        double duration = animation.Duration.TotalSeconds;
        return new(0, 0, 0, duration, true, 0, new SKRect(0, 0, animation.Size.Width, animation.Size.Height));
    }

    internal static LottiePlan ProductPlan(Animation animation, byte[] json)
    {
        double duration = animation.Duration.TotalSeconds, fps = animation.Fps > 0 ? animation.Fps : 30;
        double last = Math.Max(0, duration - 0.5 / fps);
        var (segments, splits) = ReadMarkers(json, animation.InPoint, fps, duration);
        double introStart = 0, introEnd = duration, loopStart = 0, loopEnd = 0;
        bool loops;
        if (segments.Count >= 2) { (introStart, introEnd) = segments[0]; (loopStart, loopEnd) = segments[^1]; loops = true; }
        else if (segments.Count == 1) { introEnd = segments[0].Start; (loopStart, loopEnd) = segments[0]; loops = true; }
        else if (splits.Count != 0) { introEnd = splits[0]; loopStart = splits[0]; loopEnd = duration; loops = true; }
        else
        {
            // Without markers, repeat only a timeline whose last frame returns to its first.
            loops = Seamless(animation, last);
            if (loops) { introEnd = 0; loopEnd = duration; }
        }
        var plan = new LottiePlan(introStart, introEnd, loopStart, loopEnd, loops, last, new SKRect(0, 0, animation.Size.Width, animation.Size.Height));
        return plan with { Crop = ContentBounds(animation, plan) };
    }

    /// <summary>Segment markers have a duration; zero-length markers split the timeline.</summary>
    private static (List<(double Start, double End)> Segments, List<double> Splits) ReadMarkers(byte[] json, double inPoint, double fps, double duration)
    {
        var segments = new List<(double Start, double End)>(); var splits = new List<double>();
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 128 });
        if (!document.RootElement.TryGetProperty("markers", out var markers) || markers.ValueKind != JsonValueKind.Array) return (segments, splits);
        foreach (var marker in markers.EnumerateArray().Take(64))
        {
            if (marker.ValueKind != JsonValueKind.Object || !marker.TryGetProperty("tm", out var tm) || !tm.TryGetDouble(out double frame)) continue;
            double start = (frame - inPoint) / fps;
            double length = marker.TryGetProperty("dr", out var dr) && dr.TryGetDouble(out double frames) ? frames / fps : 0;
            if (start < 0 || start >= duration) continue;
            if (length > 0) { double end = Math.Min(duration, start + length); if (end - start >= 0.05) segments.Add((start, end)); }
            else if (start > 0) splits.Add(start);
        }
        segments.Sort((a, b) => a.Start.CompareTo(b.Start)); splits.Sort();
        return (segments, splits);
    }

    private static bool Seamless(Animation animation, double last)
    {
        const int size = 48;
        using var first = Render(animation, 0, size, size); using var final = Render(animation, last, size, size);
        var a = first.GetPixelSpan(); var b = final.GetPixelSpan();
        long difference = 0;
        for (int i = 0; i < a.Length; i++) difference += Math.Abs(a[i] - b[i]);
        return difference / (double)a.Length < 3;
    }

    /// <summary>Union of visible pixels over the played timeline, padded and clamped to the canvas.</summary>
    private static SKRect ContentBounds(Animation animation, LottiePlan plan)
    {
        var canvas = new SKRect(0, 0, animation.Size.Width, animation.Size.Height);
        double ratio = 160 / Math.Max(canvas.Width, canvas.Height);
        int width = Math.Max(1, (int)Math.Round(canvas.Width * ratio)), height = Math.Max(1, (int)Math.Round(canvas.Height * ratio));
        var times = new List<double>();
        void Sample(double start, double end) { for (int i = 0; i < 6 && end > start; i++) times.Add(start + (end - start) * i / 6); }
        Sample(plan.IntroStart, plan.IntroEnd);
        if (plan.Loops) Sample(plan.LoopStart, plan.LoopEnd); else times.Add(plan.Hold);
        int minX = width, minY = height, maxX = -1, maxY = -1;
        foreach (double time in times)
        {
            using var frame = Render(animation, time, width, height);
            var pixels = frame.GetPixelSpan();
            for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++)
                    if (pixels[(y * width + x) * 4 + 3] > 8)
                    { minX = Math.Min(minX, x); maxX = Math.Max(maxX, x); minY = Math.Min(minY, y); maxY = Math.Max(maxY, y); }
        }
        if (maxX < 0) return canvas;
        var visible = new SKRect((float)(minX / ratio), (float)(minY / ratio), (float)((maxX + 1) / ratio), (float)((maxY + 1) / ratio));
        float pad = Math.Max(visible.Width, visible.Height) * 0.04f;
        visible = new SKRect(Math.Max(0, visible.Left - pad), Math.Max(0, visible.Top - pad),
            Math.Min(canvas.Right, visible.Right + pad), Math.Min(canvas.Bottom, visible.Bottom + pad));
        return visible.Width * visible.Height < canvas.Width * canvas.Height * 0.005f ? canvas : visible;
    }

    private static SKBitmap Render(Animation animation, double time, int width, int height)
    {
        var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Transparent);
        animation.SeekFrameTime(time);
        animation.Render(canvas, new SKRect(0, 0, width, height));
        canvas.Flush();
        return bitmap;
    }

    internal static void ValidateImages(byte[] json, string relativeRoot, string packageRoot)
    {
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 128 });
        if (!document.RootElement.TryGetProperty("assets", out var assets)) return;
        if (assets.GetArrayLength() > 4096) throw new InvalidDataException("Too many animation assets.");
        foreach (var asset in assets.EnumerateArray())
        {
            if (!asset.TryGetProperty("p", out var p)) continue;
            var path = p.GetString() ?? "";
            if (path.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                if (!path.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase) || path.Length > 16 * 1024 * 1024)
                    throw new InvalidDataException("Unsupported embedded animation image.");
                continue;
            }
            var prefix = asset.TryGetProperty("u", out var u) ? u.GetString() ?? "" : "";
            if (string.IsNullOrEmpty(packageRoot) || Path.IsPathRooted(path) || Path.IsPathRooted(prefix)
                || path.Contains(':') || prefix.Contains(':')) throw new InvalidDataException("Animation images must be inside the resource package.");
            var full = Path.GetFullPath(Path.Combine(relativeRoot, prefix, path));
            var boundary = Path.GetFullPath(packageRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!full.StartsWith(boundary, StringComparison.OrdinalIgnoreCase) || !File.Exists(full))
                throw new InvalidDataException("Animation image is outside its resource package or missing.");
            if (new FileInfo(full).Length > 16 * 1024 * 1024) throw new InvalidDataException("Animation image exceeds its limit.");
        }
    }
    public void Dispose() { Animation.Dispose(); _provider.Dispose(); _files.Dispose(); _lease?.Dispose(); }
}
