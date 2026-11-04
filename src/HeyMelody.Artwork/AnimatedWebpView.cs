using System.Numerics;
using System.Security.Cryptography;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Rendering.Composition;
using Avalonia.Skia;
using Avalonia.Threading;
using Avalonia.VisualTree;
using SkiaSharp;

namespace HeyMelodyWindows.Artwork;

/// <summary>CPU-decoded animated WebP artwork; each frame is composed by Skia's image codec.</summary>
/// <remarks>
/// Frames are decoded on a worker thread and published as immutable images; the render thread only draws the newest
/// one. Decoding inside OnRender used to cost 3-10 ms of the frame (a whole dependency chain when looping) plus a
/// full-frame copy on every draw.
/// </remarks>
public class AnimatedWebpView(ILottieSource source, Func<Control?>? fallback = null,
    Func<Control?>? poster = null, Func<Control?>? loading = null) : UserControl
{
    private CompositionCustomVisual? _composition;
    private Window? _window;
    private VisibilityTracker? _visibility;
    private DispatcherTimer? _poster;
    private int _generation;
    private bool _attached;
    private Size _sourceSize;
    private long _renderedFrames;
    internal long RenderedFrameCount => Interlocked.Read(ref _renderedFrames);
    public bool AnimationActive { get; private set; }
    public bool AnimationLoaded { get; private set; }
    public string? LoadError { get; private set; }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _attached = true;
        _window = TopLevel.GetTopLevel(this) as Window;
        _visibility = new VisibilityTracker(this, RefreshActivity);
        LoadError = null;
        int generation = ++_generation;
        // The caller's loading state shows until the animation replaces it; a poster that follows within the ring's
        // own reveal delay would only flash it, so a poster takes its place.
        if (loading is not null && poster is null) Content = loading();
        if (poster is not null)
        {
            _poster = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            _poster.Tick += (_, _) =>
            {
                StopPoster();
                if (_attached && generation == _generation && !AnimationLoaded && LoadError is null) Content = poster();
            };
            _poster.Start();
        }
        _ = LoadAsync(generation);
    }

    private async Task LoadAsync(int generation)
    {
        AnimatedWebpPlayback? playback = null;
        try
        {
            playback = await Task.Run(() => AnimatedWebpPlayback.Load(source));
            if (!_attached || generation != _generation) { playback.Dispose(); return; }
            var parent = ElementComposition.GetElementVisual(this);
            if (parent is null) { playback.Dispose(); StopPoster(); Content = null; return; }
            StopPoster(); Content = null;
            _sourceSize = new Size(playback.Frame.Width, playback.Frame.Height);
            _composition = parent.Compositor.CreateCustomVisual(new WebpHandler(playback,
                () => Interlocked.Increment(ref _renderedFrames), error => Dispatcher.UIThread.Post(() => Failed(generation, error))));
            playback = null;
            AnimationLoaded = true;
            ElementComposition.SetElementChildVisual(this, _composition);
            InvalidateMeasure(); InvalidateArrange(); Resize(); RefreshActivity();
        }
        catch (Exception error) { playback?.Dispose(); Failed(generation, error); }
    }

    private void Failed(int generation, Exception error)
    {
        if (!_attached || generation != _generation) return;
        StopPoster();
        _composition?.SendHandlerMessage(Command.Dispose);
        ElementComposition.SetElementChildVisual(this, null);
        _composition = null;
        AnimationLoaded = AnimationActive = false;
        LoadError = error.Message;
        Content = fallback?.Invoke();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _attached = false; ++_generation;
        StopPoster();
        _visibility?.Dispose();
        _visibility = null;
        _window = null;
        _composition?.SendHandlerMessage(Command.Dispose);
        ElementComposition.SetElementChildVisual(this, null);
        _composition = null;
        AnimationLoaded = AnimationActive = false;
        base.OnDetachedFromVisualTree(e);
    }
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.Property == BoundsProperty) { Resize(); RefreshActivity(); }
        if (e.Property == IsVisibleProperty) RefreshActivity();
    }
    protected override Size MeasureOverride(Size availableSize) => Content is not null
        ? base.MeasureOverride(availableSize) : Stretch.Uniform.CalculateSize(availableSize, _sourceSize);
    private void StopPoster() { _poster?.Stop(); _poster = null; }
    private void Resize() { if (_composition is not null) _composition.Size = new Vector2((float)Bounds.Width, (float)Bounds.Height); }
    private void RefreshActivity()
    {
        bool active = _attached && _composition is not null && Bounds.Width > 0 && Bounds.Height > 0
            && double.IsFinite(Bounds.Width) && double.IsFinite(Bounds.Height) && IsEffectivelyVisible
            && _window is { IsVisible: true, WindowState: not WindowState.Minimized };
        if (active == AnimationActive) return;
        AnimationActive = active;
        _composition?.SendHandlerMessage(AnimationActive ? Command.Play : Command.Pause);
    }

    private enum Command { Play, Pause, Dispose }
    private sealed class WebpHandler(AnimatedWebpPlayback playback, Action rendered, Action<Exception> failed) : CompositionCustomVisualHandler
    {
        // The worker owns the playback while decoding; the gate also orders disposal after the last decode.
        private readonly object _gate = new();
        private AnimatedWebpPlayback? _playback = playback;
        private SKImage? _current = playback.Snapshot(), _pending;
        private int _requested = -1, _decoded, _decoding;
        private volatile bool _disposed, _faulted;
        private bool _playing, _settled;
        private TimeSpan? _lastFrame;
        private double _elapsed;
        private int _frame;

        public override void OnMessage(object message)
        {
            if (message is not Command command) return;
            if (command == Command.Dispose) { Dispose(); return; }
            _playing = command == Command.Play;
            _lastFrame = null;
            Invalidate();
            if (_playing && !_settled) RegisterForNextAnimationFrameUpdate();
        }

        public override void OnAnimationFrameUpdate()
        {
            if (_disposed) return;
            // A frame decoded since the last composition replaces the drawn one here, on the render thread.
            if (Interlocked.Exchange(ref _pending, null) is { } ready)
            {
                _current?.Dispose();
                _current = ready;
                Invalidate();
            }
            if (!_playing || _playback is not { } playback || _settled) return;
            if (_lastFrame is { } last) _elapsed += Math.Max(0, (CompositionNow - last).TotalSeconds);
            _lastFrame = CompositionNow;
            int next = playback.FrameAt(_elapsed, out _settled);
            if (next != _frame) { _frame = next; RequestDecode(next); }
            // Keep ticking until the last requested frame has been shown.
            if (!_settled || Volatile.Read(ref _decoding) != 0 || Volatile.Read(ref _pending) is not null) RegisterForNextAnimationFrameUpdate();
        }

        public override void OnRender(ImmediateDrawingContext context)
        {
            if (_current is not { } image || context.TryGetFeature<ISkiaSharpApiLeaseFeature>() is not { } feature) return;
            var bounds = GetRenderBounds();
            if (bounds.Width <= 0 || bounds.Height <= 0) return;
            double scale = Math.Min(bounds.Width / image.Width, bounds.Height / image.Height);
            float width = (float)(image.Width * scale), height = (float)(image.Height * scale);
            float left = (float)(bounds.Width - width) / 2, top = (float)(bounds.Height - height) / 2;
            using var clip = context.PushClip(bounds);
            using var lease = feature.Lease();
            lease.SkCanvas.DrawImage(image, new SKRect(left, top, left + width, top + height), new SKSamplingOptions(SKFilterMode.Linear));
            rendered();
        }

        private void RequestDecode(int index)
        {
            Volatile.Write(ref _requested, index);
            if (Interlocked.CompareExchange(ref _decoding, 1, 0) == 0) _ = Task.Run(Decode);
        }

        private void Decode()
        {
            try
            {
                while (!_disposed && !_faulted)
                {
                    var target = Volatile.Read(ref _requested);
                    SKImage image;
                    lock (_gate)
                    {
                        if (_playback is not { } playback) return;
                        playback.SeekFrame(target);
                        image = playback.Snapshot();
                    }
                    // An unshown older frame is simply discarded.
                    Interlocked.Exchange(ref _pending, image)?.Dispose();
                    if (_disposed) Interlocked.Exchange(ref _pending, null)?.Dispose();
                    Volatile.Write(ref _decoded, target);
                    if (Volatile.Read(ref _requested) == target) return;
                }
            }
            catch (Exception error)
            {
                _faulted = true;
                if (!_disposed) failed(error);
            }
            finally
            {
                Volatile.Write(ref _decoding, 0);
                // A request that arrived between the last check and releasing the worker starts it again.
                if (!_disposed && !_faulted && Volatile.Read(ref _requested) != Volatile.Read(ref _decoded)
                    && Interlocked.CompareExchange(ref _decoding, 1, 0) == 0) _ = Task.Run(Decode);
            }
        }

        private void Dispose()
        {
            _disposed = true;
            _playing = false;
            lock (_gate) { _playback?.Dispose(); _playback = null; }
            Interlocked.Exchange(ref _pending, null)?.Dispose();
            _current?.Dispose();
            _current = null;
        }
    }
}

/// <summary>Keeps one decoded canvas, including prior-frame blending and disposal, rather than caching every frame.</summary>
internal sealed class AnimatedWebpPlayback : IDisposable
{
    private readonly SKData _data;
    private readonly SKCodec _codec;
    private readonly SKCodecFrameInfo[] _frames;
    private readonly double[] _frameEnds;
    private readonly IDisposable? _lease;
    private bool _disposed;
    private int _decodedFrame = -1;
    public SKBitmap Frame { get; }
    public int FrameCount => _frames.Length;
    public double Duration => _frameEnds[^1];
    public int RepetitionCount => _codec.RepetitionCount;
    private AnimatedWebpPlayback(SKData data, SKCodec codec, SKCodecFrameInfo[] frames, SKBitmap bitmap, IDisposable? lease)
    {
        _data = data; _codec = codec; _frames = frames; Frame = bitmap; _lease = lease;
        _frameEnds = new double[frames.Length];
        double end = 0;
        for (int i = 0; i < frames.Length; i++) _frameEnds[i] = end += (frames[i].Duration > 0 ? Math.Max(10, frames[i].Duration) : 100) / 1000d;
    }
    public static AnimatedWebpPlayback Load(ILottieSource source)
    {
        var lease = source.Acquire();
        SKData? data = null;
        SKCodec? codec = null;
        SKBitmap? bitmap = null;
        try
        {
            using var stream = source.Open();
            if (stream.Length is <= 0 or > 64 * 1024 * 1024) throw new InvalidDataException("Animated image exceeds its limit.");
            using var memory = new MemoryStream(); stream.CopyTo(memory);
            byte[] bytes = memory.ToArray();
            if (!string.IsNullOrEmpty(source.Sha256) && !Convert.ToHexString(SHA256.HashData(bytes)).Equals(source.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Animated image integrity check failed.");
            data = SKData.CreateCopy(bytes);
            codec = SKCodec.Create(data) ?? throw new InvalidDataException("Unsupported animated image.");
            var info = codec.Info;
            if (codec.EncodedFormat != SKEncodedImageFormat.Webp || info.Width is <= 0 or > 8192 || info.Height is <= 0 or > 8192
                || (long)info.Width * info.Height > 16 * 1024 * 1024 || codec.FrameCount is < 2 or > 4096)
                throw new InvalidDataException("Animated WebP bounds or frame count exceed their limit.");
            var frames = codec.FrameInfo;
            if (frames.Length != codec.FrameCount || frames.Any(frame => !frame.FullyRecieved)
                || frames.Sum(frame => (long)(frame.Duration > 0 ? Math.Max(10, frame.Duration) : 100)) > 60 * 60 * 1000)
                throw new InvalidDataException("Animated WebP timeline is incomplete or exceeds its limit.");
            bitmap = new SKBitmap(new SKImageInfo(info.Width, info.Height, SKColorType.Rgba8888, SKAlphaType.Premul));
            var playback = new AnimatedWebpPlayback(data, codec, frames, bitmap, lease);
            playback.SeekFrame(0);
            return playback;
        }
        catch { bitmap?.Dispose(); codec?.Dispose(); data?.Dispose(); lease?.Dispose(); throw; }
    }
    public int FrameAt(double elapsed, out bool settled)
    {
        elapsed = Math.Max(0, elapsed);
        settled = RepetitionCount >= 0 && elapsed >= Duration * (RepetitionCount + 1d);
        if (settled) return FrameCount - 1;
        double time = elapsed % Duration;
        int index = Array.BinarySearch(_frameEnds, time);
        return index >= 0 ? Math.Min(index + 1, FrameCount - 1) : ~index;
    }
    /// <summary>An immutable copy of the current frame, safe to draw on the render thread while the next one decodes.</summary>
    public SKImage Snapshot()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return SKImage.FromPixelCopy(Frame.Info, Frame.GetPixels(), Frame.RowBytes)
            ?? throw new InvalidDataException("Animated WebP frame could not be copied.");
    }

    public void SeekFrame(int index)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if ((uint)index >= (uint)FrameCount) throw new ArgumentOutOfRangeException(nameof(index));
        if (index == _decodedFrame) return;
        int required = _frames[index].RequiredFrame;
        int prior = required >= 0 && _decodedFrame >= required && _decodedFrame < index
            && _frames[_decodedFrame].DisposalMethod != SKCodecAnimationDisposalMethod.RestorePrevious ? _decodedFrame : -1;
        // With no reusable prior frame the codec decodes all dependencies and clears disposed areas.
        var result = _codec.GetPixels(Frame.Info, Frame.GetPixels(), Frame.RowBytes, new SKCodecOptions(index, prior));
        if (result != SKCodecResult.Success) throw new InvalidDataException("Animated WebP frame decoding failed: " + result);
        Frame.NotifyPixelsChanged();
        _decodedFrame = index;
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Frame.Dispose(); _codec.Dispose(); _data.Dispose(); _lease?.Dispose();
    }
}
