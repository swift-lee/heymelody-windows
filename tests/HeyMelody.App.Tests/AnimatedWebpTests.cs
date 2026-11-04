using System.Security.Cryptography;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;
using HeyMelodyWindows.App;
using HeyMelodyWindows.Artwork;
using SkiaSharp;

internal static class AnimatedWebpTests
{
    public static int Run(HeadlessUnitTestSession session, string output, List<object> receipts)
    {
        Run("animated WebP decodes changing pixels at its own frame durations and replays safely", TimelineAsync);
        Run("X4 partial WebP frames match independent image references and change visible pixels", () => OriginalPixelsAsync(output, receipts));
        Run("animated product artwork changes actual compositor pixels, pauses and releases its lease", () => CompositorAsync(output, receipts));
        return 3;
        void Run(string label, Func<Task> test)
        { session.Dispatch<bool>(async () => { await test(); return true; }, CancellationToken.None).GetAwaiter().GetResult(); Console.WriteLine("PASS " + label); }
    }

    private static Task TimelineAsync()
    {
        int acquired = 0, released = 0;
        var source = Source("two-frame.webp") with { AcquireLease = () => { acquired++; return new Lease(() => released++); } };
        using (var loaded = AnimatedWebpPlayback.Load(source))
        {
            Check(loaded.FrameCount == 2 && Math.Abs(loaded.Duration - .4) < 1e-6 && loaded.RepetitionCount == -1,
                "WebP's 100/300 ms frame durations and infinite loop flag are retained.");
            Check(loaded.FrameAt(.099, out _) == 0 && loaded.FrameAt(.1, out _) == 1
                && loaded.FrameAt(.399, out _) == 1 && loaded.FrameAt(.4, out bool settled) == 0 && !settled,
                "Time chooses the displayed frame, including its exact boundary and wrap.");
            var first = loaded.Frame.GetPixelSpan().ToArray();
            Check(loaded.Frame.GetPixel(10, 10).Red > 200 && loaded.Frame.GetPixel(0, 0).Alpha == 0, "The first frame has its red rectangle and transparent margin.");
            loaded.SeekFrame(1);
            var second = loaded.Frame.GetPixelSpan().ToArray();
            Check(loaded.Frame.GetPixel(10, 10).Blue > 150 && loaded.Frame.GetPixel(18, 18).Red > 200,
                "The partial second frame adds blue while retaining the prior red pixels.");
            Check(ChangedPixels(first, second) >= 36, "Decoding later frames changes pixels instead of displaying frame zero repeatedly.");
            loaded.SeekFrame(0); Check(first.AsSpan().SequenceEqual(loaded.Frame.GetPixelSpan()), "Replay reconstructs the original first canvas.");
            loaded.SeekFrame(1); Check(second.AsSpan().SequenceEqual(loaded.Frame.GetPixelSpan()), "Replay reconstructs the same composited second canvas.");
        }
        Check(acquired == 1 && released == 1, "The source lease survives playback and is released once.");
        var invalid = Source("two-frame.webp") with { Sha256 = new string('0', 64), AcquireLease = () => new Lease(() => released++) };
        try { using var ignored = AnimatedWebpPlayback.Load(invalid); throw new Exception("A mismatched image digest was accepted."); }
        catch (InvalidDataException) { Check(released == 2, "A failed digest releases its source lease."); }
        return Task.CompletedTask;
    }

    private static Task OriginalPixelsAsync(string output, List<object> receipts)
    {
        using var loaded = AnimatedWebpPlayback.Load(Source("x4-first-two-frames.webp"));
        Check(loaded.Frame.Width == 780 && loaded.Frame.Height == 780 && loaded.FrameCount == 2 && Math.Abs(loaded.Duration - .066) < 1e-6,
            "The real X4 container has partial 780 × 780 frames at 33 ms.");
        byte[]? first = null, second = null;
        for (int index = 0; index < 2; index++)
        {
            loaded.SeekFrame(index);
            using var referenceData = SKData.CreateCopy(File.ReadAllBytes(Fixture("x4-frame-00" + index + ".png")));
            using var referenceCodec = SKCodec.Create(referenceData);
            using var reference = new SKBitmap(loaded.Frame.Info);
            Check(referenceCodec.GetPixels(reference.Info, reference.GetPixels()) == SKCodecResult.Success, "The independently generated reference image decodes.");
            var pixels = loaded.Frame.GetPixelSpan(); var expected = reference.GetPixelSpan();
            int maximum = 0; long sum = 0;
            for (int channel = 0; channel < pixels.Length; channel++)
            { int difference = Math.Abs(pixels[channel] - expected[channel]); maximum = Math.Max(maximum, difference); sum += difference; }
            Check(maximum <= 3 && sum / (double)pixels.Length < .05,
                $"The composited X4 frame {index} matches independent pixels, including transparent margins: max={maximum}, average={sum / (double)pixels.Length}.");
            var copy = pixels.ToArray();
            if (index == 0) first = copy; else second = copy;
            using var encoded = loaded.Frame.Encode(SKEncodedImageFormat.Png, 100);
            string name = "x4-webp-frame-00" + index + ".png";
            File.WriteAllBytes(Path.Combine(output, name), encoded.ToArray());
            receipts.Add(new { scenario = "X4 WebP composed frame " + index, file = name,
                rgbaSha256 = Convert.ToHexString(SHA256.HashData(copy)).ToLowerInvariant(), referenceMaximumChannelDifference = maximum });
        }
        int changed = ChangedPixels(first!, second!);
        Check(changed > 1000, $"The original rotation changes visible pixels: {changed} changed pixels.");
        loaded.SeekFrame(0); Check(first!.AsSpan().SequenceEqual(loaded.Frame.GetPixelSpan()), "A loop clears the preceding frame's disposal region correctly.");
        loaded.SeekFrame(1); Check(second!.AsSpan().SequenceEqual(loaded.Frame.GetPixelSpan()), "The next loop composes the same real second frame.");
        Console.WriteLine("X4 WebP changed pixels " + changed);
        return Task.CompletedTask;
    }

    private static async Task CompositorAsync(string output, List<object> receipts)
    {
        int acquired = 0, released = 0;
        var source = Source("two-frame.webp") with { AcquireLease = () => { Interlocked.Increment(ref acquired); return new Lease(() => Interlocked.Increment(ref released)); } };
        var visual = new AnimatedWebpVisual(source, DeviceVisuals.GenericProduct);
        var artwork = new ProductArtwork { Visual = visual, Width = 160, Height = 160 };
        var container = new Border { Child = artwork };
        var window = new Window { Content = container, Width = 160, Height = 160 };
        try
        {
            window.Show();
            var animation = artwork.GetVisualDescendants().OfType<AnimatedWebpView>().Single();
            Check(animation.Content is HeroLoadingIndicator, "A loading product animation shows the progress ring.");
            Dispatcher.UIThread.RunJobs();
            await PumpAsync(() => animation.AnimationLoaded || animation.LoadError is not null);
            Check(animation.AnimationLoaded && animation.AnimationActive, "The animated-webp visual creates an active product renderer.");
            Check(animation.Content is null, "The loaded animation replaces the progress ring.");
            var captures = new List<byte[]>();
            int redFrame = -1, blueFrame = -1;
            for (int index = 0; index < 10; index++)
            {
                using var capture = window.CaptureRenderedFrame() ?? throw new Exception("Missing compositor pixels.");
                using var memory = new MemoryStream(); capture.Save(memory, Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
                captures.Add(memory.ToArray());
                using var pixels = SKBitmap.Decode(captures[^1]);
                int red = pixels.Pixels.Count(pixel => pixel.Red > 200 && pixel.Green < 50 && pixel.Blue < 50);
                int blue = pixels.Pixels.Count(pixel => pixel.Blue > 150 && pixel.Red < 80);
                if (red > 100 && blue == 0) redFrame = index;
                if (red > 100 && blue > 100) blueFrame = index;
                await Task.Delay(60); Dispatcher.UIThread.RunJobs();
            }
            Check(redFrame >= 0 && blueFrame >= 0 && !captures[redFrame].AsSpan().SequenceEqual(captures[blueFrame]),
                "Actual compositor pixels display both the red first frame and the blue addition of the timed second frame.");
            foreach (int index in new[] { redFrame, blueFrame })
            {
                string name = "animated-webp-compositor-" + index + ".png";
                File.WriteAllBytes(Path.Combine(output, name), captures[index]);
                receipts.Add(new { scenario = "animated WebP actual compositor time", file = name, sha256 = Convert.ToHexString(SHA256.HashData(captures[index])).ToLowerInvariant() });
            }
            container.IsVisible = false; Dispatcher.UIThread.RunJobs();
            Check(!animation.AnimationActive, "Hidden ancestors pause the product animation.");
            await CheckPausedAsync("a hidden ancestor", () => container.IsVisible = true);
            window.WindowState = WindowState.Minimized; Dispatcher.UIThread.RunJobs();
            Check(!animation.AnimationActive, "Minimizing the window pauses animation.");
            await CheckPausedAsync("a minimized window", () => window.WindowState = WindowState.Normal);
            window.Hide(); Dispatcher.UIThread.RunJobs();
            await CheckPausedAsync("a hidden window", window.Show);
            artwork.Width = artwork.Height = 0; Dispatcher.UIThread.RunJobs();
            await DrawForAsync(60);
            Check(!animation.AnimationActive, "A zero-sized WebP viewport pauses its timeline and decoding.");
            long emptyFrames = animation.RenderedFrameCount;
            await DrawForAsync(160);
            Check(animation.RenderedFrameCount == emptyFrames, "A zero-sized WebP viewport does not draw frames.");
            artwork.Width = artwork.Height = 160; Dispatcher.UIThread.RunJobs();
            await PumpAsync(() =>
            {
                using var frame = window.CaptureRenderedFrame();
                return animation.RenderedFrameCount > emptyFrames;
            });
            Check(acquired == 1 && released == 0, "Restoring WebP visibility and viewport retains its original source lease.");
            window.Content = null; Dispatcher.UIThread.RunJobs();
            await PumpAsync(() => { using var frame = window.CaptureRenderedFrame(); return released == acquired; });
            Check(acquired == 1 && released == 1 && !animation.AnimationActive && !animation.AnimationLoaded,
                "Detaching releases the codec and its package lease once.");
            long detachedFrames = animation.RenderedFrameCount;
            await DrawForAsync(160);
            Check(animation.RenderedFrameCount == detachedFrames, "An unloaded WebP stops actual compositor draws.");

            async Task DrawForAsync(int milliseconds)
            {
                long until = Environment.TickCount64 + milliseconds;
                do
                {
                    using var frame = window.CaptureRenderedFrame();
                    await Task.Delay(10); Dispatcher.UIThread.RunJobs();
                } while (Environment.TickCount64 < until);
            }
            async Task CheckPausedAsync(string reason, Action resume)
            {
                Check(!animation.AnimationActive, $"WebP pauses for {reason}.");
                // A decode already in flight may publish its final frame once after the pause.
                await DrawForAsync(60);
                long pausedFrames = animation.RenderedFrameCount;
                await DrawForAsync(160);
                Check(animation.RenderedFrameCount == pausedFrames, $"WebP stops actual draws for {reason}.");
                resume(); Dispatcher.UIThread.RunJobs();
                await PumpAsync(() =>
                {
                    using var frame = window.CaptureRenderedFrame();
                    return animation.RenderedFrameCount > pausedFrames;
                });
                Check(animation.AnimationActive && acquired == 1 && released == 0,
                    $"WebP resumes from {reason} without loading a second source.");
            }
        }
        finally { window.Close(); }
    }

    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", name);
    private static ArtworkFile Source(string name)
    {
        string path = Fixture(name);
        return new(new Uri(path), Path.GetDirectoryName(path)!, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))));
    }
    private static int ChangedPixels(byte[] first, byte[] second)
    {
        int count = 0;
        for (int offset = 0; offset < first.Length; offset += 4)
            if (!first.AsSpan(offset, 4).SequenceEqual(second.AsSpan(offset, 4))) count++;
        return count;
    }
    private static async Task PumpAsync(Func<bool> condition)
    {
        for (int attempt = 0; attempt < 300; attempt++)
        {
            Dispatcher.UIThread.RunJobs();
            if (condition()) return;
            await Task.Delay(10);
        }
        throw new InvalidOperationException("Animation condition was not reached.");
    }
    private static void Check(bool condition, string reason) { if (!condition) throw new InvalidOperationException(reason); }
    private sealed class Lease(Action release) : IDisposable
    {
        private Action? _release = release;
        public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
    }
}
