using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using HeyMelodyWindows.App;
using FluentAvalonia.UI.Controls;
using SkiaSharp;
using HeyMelodyWindows.Artwork;
using HeyMelodyWindows.Resources;
using System.Security.Cryptography;

internal static class DeviceVisualTests
{
    public static int Run(HeadlessUnitTestSession session)
    {
        Run("artwork requires actual PID and does not infer models from Bluetooth names", IdentityAsync);
        Run("backend replacement rejects old artwork requests and releases their leases", BackendAsync);
        Run("colour changes cancel stale requests and resolve the replacement artwork", ColorAsync);
        var preparationTests = RunSelectedModelPreparation(session);
        Run("X4 product PNG uses independent presentation controls and shared bitmap data", PngAsync);
        Run("active renderer leases outlive catalog replacement and release exactly once", LeaseAsync);
        var animationTests = RunAnimations(session);
        Run("the product area shows a progress ring instead of text while artwork resolves or decodes", LoadingRingAsync);
        return 6 + preparationTests + animationTests;
        void Run(string label, Func<Task> test)
        { session.Dispatch<bool>(async () => { await test(); return true; }, CancellationToken.None).GetAwaiter().GetResult(); Console.WriteLine("PASS " + label); }
    }
    internal static int RunAnimations(HeadlessUnitTestSession session)
    {
        Run("Skottie renders embedded and package images and rejects escaped paths", LottieAsync);
        Run("original hearing Lottie renders a frame, pauses while hidden, and releases on detach", OriginalLottieAsync);
        Run("Lottie keeps drawing every callback during focus, scroll and expander activity and pauses safely", LottieSchedulingAsync);
        return 3;
        void Run(string label, Func<Task> test)
        { session.Dispatch<bool>(async () => { await test(); return true; }, CancellationToken.None).GetAwaiter().GetResult(); Console.WriteLine("PASS " + label); }
    }
    internal static int RunSelectedModelPreparation(HeadlessUnitTestSession session)
    {
        Run("only the selected hero model is prepared and cached selection is deduplicated", SelectedModelPreparationAsync);
        Run("late artwork completion prepares the current selection before notifying views", PreparationSelectionAsync);
        Run("cancelled and failed artwork generations do not consume model preparation", PreparationGenerationAsync);
        return 3;
        void Run(string label, Func<Task> test)
        { session.Dispatch<bool>(async () => { await test(); return true; }, CancellationToken.None).GetAwaiter().GetResult(); Console.WriteLine("PASS " + label); }
    }
    internal static int RunCatalog(HeadlessUnitTestSession session)
    {
        Run("artwork requires actual PID and does not infer models from Bluetooth names", IdentityAsync);
        Run("backend replacement rejects old artwork requests and releases their leases", BackendAsync);
        Run("colour changes cancel stale requests and resolve the replacement artwork", ColorAsync);
        return 3 + RunSelectedModelPreparation(session);
        void Run(string label, Func<Task> test)
        { session.Dispatch<bool>(async () => { await test(); return true; }, CancellationToken.None).GetAwaiter().GetResult(); Console.WriteLine("PASS " + label); }
    }
    public static void CheckScenario(MainView view, string scenario)
    {
        var hero = view.FindControl<ProductArtwork>("HeroArt")!;
        if (scenario is "connected" or "partial")
            Check(Equals(hero.Visual, DeviceVisuals.X4Product), "Verified X4 keeps its own artwork.");
        else Check(Equals(hero.Visual, DeviceVisuals.GenericProduct), "Unknown and absent devices use generic artwork.");
    }
    private static Task IdentityAsync()
    {
        using var catalog = new DeviceVisualCatalog();
        var device = new UiDevice("earbuds", "OPPO Enco X4", false);
        catalog.Update([device], device, null);
        Check(Equals(catalog.ProductFor(device), DeviceVisuals.GenericProduct), "A model name does not provide an actual product ID.");
        catalog.Update([device], device, "06EC10");
        Check(Equals(catalog.ProductFor(device), DeviceVisuals.X4Product), "X4 PID selects X4 artwork.");
        catalog.Update([device], device, "068C10");
        Check(Equals(catalog.ProductFor(device), DeviceVisuals.GenericProduct), "Free4 never borrows X4 artwork.");
        catalog.Update([], null, null);
        Check(Equals(catalog.ProductFor(device), DeviceVisuals.GenericProduct), "Removed devices lose temporary product knowledge.");
        return Task.CompletedTask;
    }
    private static async Task BackendAsync()
    {
        var service = new ArtworkService(); var replacement = new ArtworkService();
        using var catalog = new DeviceVisualCatalog();
        var device = new UiDevice("earbuds", "耳机", false);
        catalog.Update([device], device, "06EC10", 1, artwork: service);
        catalog.Update([device], device, "06EC10", 1, artwork: replacement);
        foreach (var request in service.Requests) request.Completion.TrySetResult(Art(request.Role));
        await Task.Delay(10); Dispatcher.UIThread.RunJobs();
        Check(Equals(catalog.ProductFor(device), DeviceVisuals.LoadingProduct) && service.Releases.Count == 1,
            "A stale backend response is released before it can be displayed; the current backend's lookup is still loading.");
        foreach (var request in replacement.Requests) request.Completion.TrySetResult(Art(request.Role));
        await Task.Delay(10); Dispatcher.UIThread.RunJobs();
        Check(catalog.ProductFor(device) is FileBitmapVisual && replacement.Requests.Count == 1,
            "The current backend's hero replaces the placeholder.");
    }
    private static Task PngAsync()
    {
        var hero = new ProductArtwork { Visual = DeviceVisuals.X4Product, Width = 240, Height = 180 };
        var second = new ProductArtwork { Visual = new BitmapVisual(DeviceVisuals.X4Product.Source), Width = 240, Height = 180 };
        var window = new Window { Content = new StackPanel { Children = { hero, second } }, Width = 300, Height = 420 };
        try
        {
            window.Show(); Dispatcher.UIThread.RunJobs();
            var image = hero.GetVisualDescendants().OfType<Image>().Single();
            var another = second.GetVisualDescendants().OfType<Image>().Single();
            Check(!ReferenceEquals(image, another) && ReferenceEquals(image.Source, another.Source), "Hosts own their image controls and share decoded bundled bitmap data.");
            Check(image.Stretch == Stretch.Uniform && DeviceVisuals.X4Product.Source.AbsolutePath.EndsWith("x4-pods-with-earphones.png"),
                "The main product view keeps its original X4 artwork.");
            Check(!hero.GetVisualDescendants().OfType<DeviceModelView>().Any(), "X4 PNG does not display Free4's model.");
        }
        finally { window.Close(); }
        return Task.CompletedTask;
    }
    private static async Task ColorAsync()
    {
        var service = new ArtworkService();
        using var catalog = new DeviceVisualCatalog();
        var device = new UiDevice("earbuds", "耳机", false);
        catalog.Update([device], device, "068C10", 1, artwork: service);
        var stale = service.Requests.ToArray();
        catalog.Update([device], device, "068C10", 2, artwork: service);
        Check(service.Requests.Count == 2 && service.Requests.All(request => request.Role == "hero"),
            "Changing colour starts fresh hero requests without waiting for old requests or fetching device icons.");
        foreach (var request in stale) request.Completion.TrySetResult(Art(request.Role));
        foreach (var request in service.Requests.Except(stale)) request.Completion.TrySetResult(Art(request.Role));
        await Task.Delay(10); Dispatcher.UIThread.RunJobs();
        Check(catalog.ProductFor(device) is FileBitmapVisual && service.Releases.Count == 1,
            "Only the replacement colour is accepted; the stale hero package lease is released.");
    }
    private static async Task SelectedModelPreparationAsync()
    {
        var service = new ArtworkService();
        var prepared = new List<DeviceModelVisual>();
        using var catalog = new DeviceVisualCatalog { PrefetchModel = prepared.Add };
        var image = new UiDevice("image", "静态美术", false) { ProductId = "IMAGE" };
        var model = new UiDevice("model", "模型美术", false) { ProductId = "MODEL" };
        catalog.Update([image, model], image, "IMAGE", artwork: service);
        var modelArt = ModelArt("unselected", 'a');
        await CompleteModelAsync(service, service.Requests.Single(request => request.ProductId == "MODEL"), modelArt);
        await PumpUntilAsync(() => catalog.ProductFor(model) is DeviceModelVisual, "Unselected model metadata resolves normally.");
        service.Requests.Single(request => request.ProductId == "IMAGE").Completion.TrySetResult(Art("image"));
        await PumpUntilAsync(() => catalog.ProductFor(image) is FileBitmapVisual, "The selected image resolves normally.");
        Check(prepared.Count == 0, "Selecting a non-model never reads or probes another device's model.");

        catalog.Update([image, model], model, "MODEL", artwork: service);
        Check(prepared.Count == 1 && prepared[0].File.Sha256 == modelArt.Sha256,
            "Selecting an already completed model prepares it before Update returns.");
        for (var attempt = 0; attempt < 5; attempt++)
        {
            _ = catalog.ProductFor(model);
            catalog.Update([image, model], model, "MODEL", artwork: service);
        }
        catalog.Update([image, model], image, "IMAGE", artwork: service);
        catalog.Update([image, model], model, "MODEL", artwork: service);
        Check(prepared.Count == 1 && service.Requests.Count == 3,
            "Getters, unchanged snapshots and selecting the same completed model again do not repeat preparation or artwork requests.");
    }

    private static async Task PreparationSelectionAsync()
    {
        var service = new ArtworkService();
        var prepared = new List<DeviceModelVisual>();
        using var catalog = new DeviceVisualCatalog { PrefetchModel = prepared.Add };
        var first = new UiDevice("first", "首个模型", false) { ProductId = "FIRST" };
        var second = new UiDevice("second", "第二个模型", false) { ProductId = "SECOND" };
        catalog.Update([first, second], first, "FIRST", artwork: service);
        catalog.Update([first, second], second, "SECOND", artwork: service);
        var firstArt = ModelArt("first", 'b'); var secondArt = ModelArt("second", 'c');
        bool notifiedBeforePreparation = false;
        catalog.PropertyChanged += (_, _) =>
        {
            if (catalog.ProductFor(second) is DeviceModelVisual visual && !prepared.Contains(visual))
                notifiedBeforePreparation = true;
        };
        await CompleteModelAsync(service, service.Requests.Single(request => request.ProductId == "FIRST"), firstArt);
        await PumpUntilAsync(() => catalog.ProductFor(first) is DeviceModelVisual, "A model completing after selection changes remains available.");
        Check(prepared.Count == 0, "The old selection's late completion does not prepare an unused model.");
        await CompleteModelAsync(service, service.Requests.Single(request => request.ProductId == "SECOND"), secondArt);
        await PumpUntilAsync(() => catalog.ProductFor(second) is DeviceModelVisual, "The current selected model resolves.");
        Check(prepared.Count == 1 && prepared[0].File.Sha256 == secondArt.Sha256 && !notifiedBeforePreparation,
            "Only the current selection is prepared, after acquiring its slot leases and before its ready notification.");
        catalog.Update([first, second], first, "FIRST", artwork: service);
        catalog.Update([first, second], second, "SECOND", artwork: service);
        Check(prepared.Count == 2 && prepared[1].File.Sha256 == firstArt.Sha256,
            "Switching to the previously completed model prepares it once without repeating the other model's preparation.");
    }

    private static async Task PreparationGenerationAsync()
    {
        var service = new ArtworkService();
        var prepared = new List<DeviceModelVisual>();
        var readingLeases = new List<IDisposable>();
        using var catalog = new DeviceVisualCatalog
        {
            PrefetchModel = visual =>
            {
                readingLeases.Add(visual.File.Acquire() ?? throw new InvalidOperationException("Prepared model has no package lease."));
                prepared.Add(visual);
            }
        };
        var device = new UiDevice("generation", "模型代次", false) { ProductId = "MODEL" };
        try
        {
            catalog.Update([device], device, "MODEL", 1, artwork: service);
            var cancelled = service.Requests.Single();
            catalog.Update([device], device, "MODEL", 2, artwork: service);
            Check(cancelled.Token.IsCancellationRequested, "Replacing an unresolved colour cancels its old slot.");
            int revision = catalog.Revision;
            service.Requests.Last().Completion.TrySetException(new IOException("Synthetic artwork failure."));
            await PumpUntilAsync(() => catalog.Revision > revision, "A failed replacement ends its loading state.");
            var cancelledArt = ModelArt("cancelled", 'd');
            await CompleteModelAsync(service, cancelled, cancelledArt);
            await PumpUntilAsync(() => service.Releases.Contains(cancelledArt.LeaseId), "A late cancelled response releases its lease.");
            Check(prepared.Count == 0, "Neither failed nor cancelled artwork consumes preparation for a later generation.");

            catalog.Update([device], device, "MODEL", 2, epoch: "retry", artwork: service);
            var firstArt = ModelArt("recovered", 'e');
            await CompleteModelAsync(service, service.Requests.Last(), firstArt);
            await PumpUntilAsync(() => prepared.Count == 1, "Retrying the same slot can prepare its first valid model.");
            var firstVisual = catalog.ProductFor(device);
            catalog.Update([device], device, "MODEL", 2, epoch: "republished", artwork: service);
            revision = catalog.Revision;
            service.Requests.Last().Completion.TrySetException(new IOException("Synthetic refresh failure."));
            await PumpUntilAsync(() => catalog.Revision > revision, "A failed refresh retains its prior model.");
            Check(prepared.Count == 1 && ReferenceEquals(catalog.ProductFor(device), firstVisual),
                "A failed new generation does not reprepare or discard its valid previous model.");

            catalog.Update([device], device, "MODEL", 2, epoch: "recovered", artwork: service);
            var replacementArt = ModelArt("replacement", 'f');
            await CompleteModelAsync(service, service.Requests.Last(), replacementArt);
            await PumpUntilAsync(() => prepared.Count == 2, "A successful new model generation is prepared once.");
            Check(prepared[1].File.Sha256 == replacementArt.Sha256 && !service.Releases.Contains(firstArt.LeaseId),
                "Replacement preparation uses the new model while an in-progress old read retains its own lease.");
            readingLeases[0].Dispose();
            await PumpUntilAsync(() => service.Releases.Contains(firstArt.LeaseId), "The last old reader releases its package.");
            Check(service.Releases.Count(id => id == firstArt.LeaseId) == 1,
                "Replacing a prepared model preserves exactly-once release of its retired package.");
        }
        finally { foreach (var lease in readingLeases) lease.Dispose(); }
    }

    private static async Task CompleteModelAsync(ArtworkService service, ArtworkService.Request request, UiArtwork artwork)
    {
        int previousRequests = service.Requests.Count;
        request.Completion.TrySetResult(artwork);
        await PumpUntilAsync(() => service.Requests.Skip(previousRequests).Any(next => next.Role == "detail"),
            "A model resolves its independent static fallback.");
        service.Requests.Skip(previousRequests).Single(next => next.Role == "detail").Completion.TrySetResult(null);
    }

    private static async Task PumpUntilAsync(Func<bool> condition, string reason)
    {
        for (var attempt = 0; attempt < 200 && !condition(); attempt++)
        { await Task.Delay(1); Dispatcher.UIThread.RunJobs(); }
        Check(condition(), reason);
    }
    private static async Task LoadingRingAsync()
    {
        var service = new ArtworkService();
        using var catalog = new DeviceVisualCatalog();
        var device = new UiDevice("earbuds", "耳机", false);
        catalog.Update([device], device, "068C10", 1, artwork: service);
        Check(Equals(catalog.ProductFor(device), DeviceVisuals.LoadingProduct), "The first artwork lookup shows the loading state.");
        int revision = catalog.Revision;
        foreach (var request in service.Requests) request.Completion.TrySetException(new IOException());
        await Task.Delay(10); Dispatcher.UIThread.RunJobs();
        Check(Equals(catalog.ProductFor(device), DeviceVisuals.GenericProduct) && catalog.Revision > revision,
            "A failed first lookup announces the change and falls back to the generic icon, not a ring during retries.");

        var art = Path.Combine(Path.GetTempPath(), "hm-loading-" + Guid.NewGuid().ToString("N") + ".png");
        using (var bitmap = new SKBitmap(64, 48))
        using (var image = SKImage.FromBitmap(bitmap))
        using (var data = image.Encode(SKEncodedImageFormat.Png, 100))
        using (var file = File.Create(art)) data.SaveTo(file);
        var loading = new ProductArtwork { Visual = DeviceVisuals.LoadingProduct, Width = 240, Height = 180 };
        var decoded = new ProductArtwork { Visual = new FileBitmapVisual(new ArtworkFile(new Uri(art), Path.GetDirectoryName(art)!, ""),
            DeviceVisuals.GenericProduct), Width = 240, Height = 180 };
        var window = new Window { Content = new StackPanel { Children = { loading, decoded } }, Width = 300, Height = 420 };
        try
        {
            window.Show();
            var pending = decoded.GetVisualDescendants().OfType<ArtworkBitmapView>().Single();
            Check(pending.Content is HeroLoadingIndicator, "A decoding bitmap shows the loading indicator, not an empty area.");
            Dispatcher.UIThread.RunJobs();
            var ring = loading.GetVisualDescendants().OfType<FAProgressRing>().Single();
            Check(ring.IsVisible && ring.IsActive && ring.IsIndeterminate
                && !loading.GetVisualDescendants().OfType<TextBlock>().Any(text => !string.IsNullOrEmpty(text.Text)),
                "The loading state is an active indeterminate progress ring without a text prompt.");
            for (int attempt = 0; attempt < 100 && pending.Content is not Image; attempt++)
            { await Task.Delay(10); Dispatcher.UIThread.RunJobs(); }
            Check(pending.Content is Image && !decoded.GetVisualDescendants().OfType<FAProgressRing>().Any(),
                "The decoded bitmap replaces the ring.");
            window.WindowState = WindowState.Minimized; Dispatcher.UIThread.RunJobs();
            Check(!ring.IsActive && !ring.IsVisible, "A minimized window does not keep the ring spinning.");
            window.WindowState = WindowState.Normal; Dispatcher.UIThread.RunJobs();
            Check(ring.IsActive && ring.IsVisible, "Restoring the window resumes the ring while still loading.");
            window.Content = null; Dispatcher.UIThread.RunJobs();
            Check(!ring.IsActive && !ring.IsVisible, "A detached indicator stops animating.");
        }
        finally { window.Close(); File.Delete(art); }
    }

    private static Task LeaseAsync()
    {
        var releases = 0;
        var shared = new SharedArtworkLease(new TestLease(() => ++releases));
        var first = shared.Acquire(); var second = shared.Acquire();
        shared.Dispose(); Check(releases == 0, "Replaced catalog data cannot delete resources used by renderers.");
        first.Dispose(); first.Dispose(); Check(releases == 0, "A renderer lease is idempotent.");
        second.Dispose(); Check(releases == 1, "The last renderer releases the package exactly once.");
        return Task.CompletedTask;
    }
    private static Task LottieAsync()
    {
        var folder = Path.Combine(Path.GetTempPath(), "heymelody-lottie-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(folder, "images"));
        try
        {
            using var bitmap = new SKBitmap(32, 32); using var painter = new SKCanvas(bitmap); painter.Clear(SKColors.Red);
            using var encoded = bitmap.Encode(SKEncodedImageFormat.Png, 100);
            var png = encoded.ToArray(); File.WriteAllBytes(Path.Combine(folder, "images", "sample.png"), png);
            foreach (var embedded in new[] { false, true })
            {
                var image = embedded ? "data:image/png;base64," + Convert.ToBase64String(png) : "sample.png";
                var prefix = embedded ? "" : "images/";
                var json = System.Text.Json.JsonSerializer.Serialize(new
                {
                    v = "5.7.0", fr = 30, ip = 0, op = 60, w = 64, h = 64,
                    assets = new[] { new { id = "pic", w = 32, h = 32, u = prefix, p = image, e = embedded ? 1 : 0 } },
                    layers = new[] { new { ddd = 0, ind = 1, ty = 2, refId = "pic",
                        ks = new { o = new { a = 0, k = 100 }, r = new { a = 0, k = 0 },
                            p = new { a = 0, k = new[] {32,32,0} }, a = new { a = 0, k = new[] {16,16,0} }, s = new { a = 0, k = new[] {100,100,100} } },
                        ip = 0, op = 60, st = 0, bm = 0 } }
                });
                var path = Path.Combine(folder, "animation.json"); File.WriteAllText(path, json);
                using var loaded = LottiePlayback.Load(new ArtworkFile(new Uri(path), folder, ""));
                using var frame = new SKBitmap(64, 64); using var canvas = new SKCanvas(frame); canvas.Clear(SKColors.Transparent);
                loaded.Animation.SeekFrameTime(.1); loaded.Animation.Render(canvas, new SKRect(0, 0, 64, 64));
                Check(frame.GetPixel(32, 32).Red > 200 && frame.GetPixel(32, 32).Alpha > 200, "Skottie decoded and rendered the image provider's pixels.");
            }
            var rejected = false;
            try { LottiePlayback.ValidateImages(Encoding.UTF8.GetBytes("{\"assets\":[{\"p\":\"../../outside.png\"}]}"), folder, folder); }
            catch (InvalidDataException) { rejected = true; }
            Check(rejected, "Package image traversal is rejected before native parsing.");
        }
        finally { Directory.Delete(folder, true); }
        return Task.CompletedTask;
    }
    private static async Task OriginalLottieAsync()
    {
        var bundled = new BundledResourceStore();
        var asset = bundled.FindNamedAsset("melody_ui_gold_hearing_detecting_guide")
            ?? throw new InvalidOperationException("Original hearing Lottie is missing from the installed resource bundle.");
        var path = bundled.FullPath(asset);
        var file = new ArtworkFile(new Uri(path), Path.GetDirectoryName(path)!, asset.Sha256);
        var output = Path.GetFullPath(Path.Combine("artifacts", "lottie-original-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(output);
        using (var loaded = LottiePlayback.Load(file))
        {
            using var frame = new SKBitmap(256, 256); using var canvas = new SKCanvas(frame);
            canvas.Clear(SKColors.Transparent); loaded.Animation.SeekFrameTime(loaded.Animation.Duration.TotalSeconds / 2);
            loaded.Animation.Render(canvas, new SKRect(0, 0, 256, 256));
            Check(frame.Pixels.Any(pixel => pixel.Alpha > 0), "Original APK animation produces visible pixels through native Skottie.");
            using var png = frame.Encode(SKEncodedImageFormat.Png, 100);
            var framePath = Path.Combine(output, "hearing-guide.png"); File.WriteAllBytes(framePath, png.ToArray());
            Console.WriteLine("Original Lottie frame SHA256 " + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(framePath))).ToLowerInvariant());
        }
        var acquired = 0; var released = 0;
        var animation = new LottieView(file with { AcquireLease = () => { Interlocked.Increment(ref acquired); return new TestLease(() => Interlocked.Increment(ref released)); } });
        var container = new Border { Child = animation };
        var window = new Window { Content = container, Width = 280, Height = 280 };
        try
        {
            window.Show(); Dispatcher.UIThread.RunJobs();
            for (var attempt = 0; !animation.AnimationLoaded && animation.LoadError is null && attempt < 300; attempt++)
            { await Task.Delay(10); Dispatcher.UIThread.RunJobs(); }
            Check(animation.AnimationLoaded && animation.AnimationActive, "The Avalonia compositor starts a loaded animation.");
            container.IsVisible = false; Dispatcher.UIThread.RunJobs();
            Check(!animation.AnimationActive, "A hidden ancestor pauses the animation.");
            container.IsVisible = true; Dispatcher.UIThread.RunJobs();
            Check(animation.AnimationActive, "Making the parent visible resumes the animation.");
            window.Hide(); Dispatcher.UIThread.RunJobs(); Check(!animation.AnimationActive, "A hidden window pauses animation frames.");
            window.Show(); Dispatcher.UIThread.RunJobs(); Check(animation.AnimationActive, "A shown window resumes without rereading its resource.");
            window.Content = null; Dispatcher.UIThread.RunJobs();
            for (var attempt = 0; released < acquired && attempt < 100; attempt++)
            { using var frame = window.CaptureRenderedFrame(); await Task.Delay(10); Dispatcher.UIThread.RunJobs(); }
            Check(!animation.AnimationActive && !animation.AnimationLoaded && acquired == 1 && released == 1,
                "Detaching disposes native Skottie objects and releases the active package lease once.");
        }
        finally { window.Close(); }
    }
    private static async Task LottieSchedulingAsync()
    {
        var bundled = new BundledResourceStore();
        var asset = bundled.FindNamedAsset("melody_ui_gold_hearing_detecting_guide")
            ?? throw new InvalidOperationException("Original hearing Lottie is missing from the installed resource bundle.");
        string path = bundled.FullPath(asset);
        var animationClock = new ManualAnimationClock();
        long cleanupMilliseconds = 0;
        int cleanupCalls = 0, acquired = 0, released = 0;
        var source = new ArtworkFile(new Uri(path), Path.GetDirectoryName(path)!, asset.Sha256,
            () => { Interlocked.Increment(ref acquired); return new TestLease(() => Interlocked.Increment(ref released)); });
        var animation = new LottieView(source)
        {
            Width = 128, Height = 128,
            AnimationClockForTests = animationClock.Read
        };
        var container = new Border { Child = animation };
        var scroll = new ScrollViewer { Height = 48, Content = new Border { Height = 400 } };
        var expander = new FASettingsExpander { Header = "Animation activity" };
        expander.Items.Add(new FASettingsExpanderItem { Content = new Border { Height = 16 } });
        var window = new Window
        {
            Content = new StackPanel { Children = { container, scroll, expander } }, Width = 240, Height = 300
        };
        using var cleanup = ArtworkCleanupTriggers.BeginTestScope(() => cleanupMilliseconds, () => cleanupCalls++);
        ArtworkCleanupTriggers.RegisterExpanders();
        ArtworkCleanupTriggers.AttachScroll(scroll);
        ArtworkCleanupTriggers.AttachWindow(window);
        try
        {
            window.Show(); Dispatcher.UIThread.RunJobs();
            await PumpUntilAsync(() => animation.AnimationLoaded || animation.LoadError is not null, "The scheduling animation loads.");
            Check(animation.AnimationLoaded && animation.AnimationActive, "The scheduling animation starts with its original resource.");
            await DrawStepsAsync(4);
            await CheckCadenceAsync("an active window");

            HeadlessWindowActivity.SetActive(window, false);
            Check(cleanupCalls > 0, "The deactivation event still requests cleanup without a playback hold.");
            await CheckCadenceAsync("an inactive window");
            HeadlessWindowActivity.SetActive(window, true);
            await CheckCadenceAsync("a reactivated window");

            scroll.Offset = new Vector(0, 80); Dispatcher.UIThread.RunJobs();
            Check(scroll.Offset.Y > 0 && ArtworkCleanupTriggers.HasPendingCleanup, "Real scrolling schedules cleanup only.");
            await CheckCadenceAsync("scroll activity");
            expander.IsExpanded = true; Dispatcher.UIThread.RunJobs();
            Check(ArtworkCleanupTriggers.HasPendingCleanup, "A real expander schedules cleanup only.");
            await CheckCadenceAsync("an opening expander");
            expander.IsExpanded = false; Dispatcher.UIThread.RunJobs();
            await CheckCadenceAsync("a closing expander");
            cleanupMilliseconds += 1000;
            ArtworkCleanupTriggers.FlushPendingForTests();
            await CheckCadenceAsync("completion of scheduled cleanup");

            container.IsVisible = false; Dispatcher.UIThread.RunJobs();
            await CheckPausedAsync("a hidden ancestor", () => container.IsVisible = true);
            window.WindowState = WindowState.Minimized; Dispatcher.UIThread.RunJobs();
            await CheckPausedAsync("a minimized window", () => window.WindowState = WindowState.Normal);
            window.Hide(); Dispatcher.UIThread.RunJobs();
            await CheckPausedAsync("a hidden window", window.Show);

            animation.Width = animation.Height = 0; Dispatcher.UIThread.RunJobs();
            await DrawStepsAsync(3);
            Check(!animation.AnimationActive, "A zero-sized viewport pauses the Lottie timeline.");
            long emptyFrames = animation.RenderedFrameCount;
            await DrawStepsAsync(8);
            Check(animation.RenderedFrameCount == emptyFrames, "A zero-sized viewport does not draw Lottie frames.");
            animation.Width = animation.Height = 128; Dispatcher.UIThread.RunJobs();
            await CheckCadenceAsync("a restored viewport");

            window.Content = null; Dispatcher.UIThread.RunJobs();
            await DrawStepsAsync(4);
            await PumpUntilAsync(() => released == acquired, "The detached animation releases its native source.");
            long detachedFrames = animation.RenderedFrameCount;
            await DrawStepsAsync(8);
            Check(animation.RenderedFrameCount == detachedFrames && !animation.AnimationActive && !animation.AnimationLoaded
                && acquired == 1 && released == 1, "Detaching stops actual draws and releases the unchanged source exactly once.");
        }
        finally { animation.AnimationClockForTests = null; window.Close(); }

        async Task DrawStepsAsync(int count)
        {
            for (int index = 0; index < count; index++)
            {
                animationClock.Advance(TimeSpan.TicksPerSecond / 120);
                AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                Dispatcher.UIThread.RunJobs();
                using var capture = window.CaptureRenderedFrame();
                await Task.Yield();
                Dispatcher.UIThread.RunJobs();
            }
        }
        async Task CheckCadenceAsync(string reason)
        {
            const int steps = 24;
            long before = animation.RenderedFrameCount;
            await DrawStepsAsync(steps);
            long drawn = animation.RenderedFrameCount - before;
            Check(drawn >= steps - 2, $"Lottie draws on the controlled 120 Hz callbacks during {reason}: {drawn} actual draws.");
        }
        async Task CheckPausedAsync(string reason, Action resume)
        {
            Check(!animation.AnimationActive, $"Lottie stops playback for {reason}.");
            await DrawStepsAsync(3);
            long frames = animation.RenderedFrameCount;
            double elapsed = animation.PresentedElapsedSeconds;
            animationClock.Advance(TimeSpan.TicksPerSecond * 20);
            await DrawStepsAsync(8);
            Check(animation.RenderedFrameCount == frames, $"Lottie produces no actual draws for {reason}.");
            resume(); Dispatcher.UIThread.RunJobs();
            await DrawStepsAsync(2);
            Check(animation.AnimationActive && animation.RenderedFrameCount > frames
                && animation.PresentedElapsedSeconds - elapsed < .1,
                $"Lottie resumes from {reason} without advancing through the hidden 20 seconds.");
            Check(acquired == 1 && released == 0, "Visibility changes retain the same loaded source lease.");
        }
    }
    private sealed class ManualAnimationClock
    {
        private long _ticks;
        public TimeSpan Read() => TimeSpan.FromTicks(Interlocked.Read(ref _ticks));
        public void Advance(long ticks) => Interlocked.Add(ref _ticks, ticks);
    }
    private static UiArtwork Art(string role) => new(Guid.NewGuid().ToString("N"), Path.GetFullPath("artwork-" + role + ".png"), "image", Path.GetFullPath("."), "", 1, 0);
    private static UiArtwork ModelArt(string name, char digest) => Art(name) with
    { FullPath = Path.GetFullPath("artwork-" + name + ".glb"), Kind = "model", Sha256 = new string(digest, 64) };
    private sealed class ArtworkService : IArtworkService
    {
        public sealed record Request(string ProductId, int? Color, string Role, CancellationToken Token, TaskCompletionSource<UiArtwork?> Completion);
        public List<Request> Requests { get; } = [];
        public List<string> Releases { get; } = [];
        public Task<UiArtwork?> AcquireArtworkAsync(string productId, int? color, string role, CancellationToken token)
        { var completion = new TaskCompletionSource<UiArtwork?>(); Requests.Add(new(productId, color, role, token, completion)); return completion.Task; }
        public Task ReleaseArtworkAsync(string id, CancellationToken token) { Releases.Add(id); return Task.CompletedTask; }
    }
    private sealed class TestLease(Action release) : IDisposable { public void Dispose() => release(); }
    private static void Check(bool condition, string reason) { if (!condition) throw new InvalidOperationException(reason); }
}
