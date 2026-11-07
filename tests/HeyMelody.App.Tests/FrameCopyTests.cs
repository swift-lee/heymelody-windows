using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Styling;
using Avalonia.Threading;
using FluentAvalonia.UI.Controls;
using HeyMelodyWindows.App;
using HeyMelodyWindows.Artwork;

/// <summary>The renderer-to-bitmap copy path and event-driven GPU cleanup that need no GPU.</summary>
internal static class FrameCopyTests
{
    internal static int Run(HeadlessUnitTestSession session)
    {
        Run("model frames copy straight into the locked bitmap and match the expected pixels", DirectAsync);
        Run("the managed copy still presents frames when the direct path is unavailable", FallbackAsync);
        Run("an idle model does not lock its bitmap or poll the renderer", NoRequestAsync);
        Run("pending frames are probed without locking or consuming the bitmap", PendingFrameAsync);
        Run("the audit baseline keeps its original no-probe copy path", ProbeDisabledAsync);
        Run("a frame lost after probing and a later resize recover without stale presentation", ProbeRaceAsync);
        Run("a frozen audit pose produces one observable frame and stays unchanged", FrozenPoseAsync);
        Run("artwork cleanup events share one deadline and do not create an idle loop", CleanupAsync);
        Run("settings expanders schedule cleanup without an animation gate", ExpanderAsync);
        Run("window cleanup hooks deactivate once and detach on close", WindowCleanupAsync);
        Run("GPU cleanup requests are rate limited", () => { Gate(); return Task.CompletedTask; });
        return 11;

        void Run(string name, Func<Task> action)
        {
            session.Dispatch<bool>(async () => { await action(); return true; }, CancellationToken.None).GetAwaiter().GetResult();
            Console.WriteLine("PASS " + name);
        }
    }

    private static async Task<(DeviceModelView Model, Window Window, RecordingFilamentRendererFactory Factory)> OpenAsync(
        ModelRenderPolicy? renderingPolicy = null)
    {
        var factory = new RecordingFilamentRendererFactory();
        var model = new DeviceModelView(new DeviceModelVisual(new ArtworkFile(
            new Uri("avares://HeyMelody/Assets/Devices/EncoFree4Dynaudio/model.glb"), "", EncoFree4DynaudioModelView.ModelSha256),
            DeviceVisuals.GenericProduct), factory) { RenderingPolicyOverride = renderingPolicy };
        var window = new Window { Content = model, Width = 280, Height = 240 };
        window.Show(); Dispatcher.UIThread.RunJobs();
        await Until(() => model.CaptureLatestFrame(out _) is not null, "The model presents its first frame.");
        return (model, window, factory);
    }

    private static async Task DirectAsync()
    {
        var (model, window, _) = await OpenAsync();
        try
        {
            model.Motion.SetActive(false);
            var fallbacks = model.FallbackFrameCopyCount;
            foreach (var (theme, clear) in new[] { (ThemeVariant.Dark, (byte)32), (ThemeVariant.Light, (byte)243) })
            {
                var direct = model.DirectFrameCopyCount;
                window.RequestedThemeVariant = theme;
                await Until(() => model.DirectFrameCopyCount > direct, "A theme change requests a frame that is copied directly.");
                var pixels = model.CaptureLatestFrame(out var frame)!;
                int center = (frame.Height / 2 * frame.Width + frame.Width / 2) * 4;
                Check(pixels[0] == clear && pixels[1] == clear && pixels[2] == clear && pixels[3] == 255,
                    "The directly copied frame carries the new clear colour: " + pixels[0]);
                Check(pixels[center] == 130 && pixels[center + 1] == 150 && pixels[center + 2] == 180 && pixels[center + 3] == 255,
                    "The directly copied frame carries the model rectangle.");
            }
            Check(model.FallbackFrameCopyCount == fallbacks, "Frames of the bitmap's size never use the managed copy.");
        }
        finally { window.Close(); }
    }

    private static async Task FallbackAsync()
    {
        var (model, window, _) = await OpenAsync();
        try
        {
            model.Motion.SetActive(false);
            model.DirectCopyDisabled = true;
            int directBefore = model.DirectFrameCopyCount;
            var fallbacks = model.FallbackFrameCopyCount;
            window.RequestedThemeVariant = ThemeVariant.Dark;
            await Until(() => model.FallbackFrameCopyCount > fallbacks, "A frame is copied through the managed buffer.");
            var pixels = model.CaptureLatestFrame(out var frame)!;
            Check(model.DirectFrameCopyCount == directBefore, "No frame used the direct path.");
            Check(pixels[0] == 32 && pixels[3] == 255, "The fallback path still produces the right pixels.");
        }
        finally { window.Close(); }
    }

    private static async Task NoRequestAsync()
    {
        var (model, window, factory) = await OpenAsync();
        try
        {
            model.Motion.SetActive(false);
            for (var i = 0; i < 4; i++) await Tick();
            // Polling continues briefly after the last request, in case a later frame is still in flight.
            await Task.Delay(TimeSpan.FromSeconds(.4));
            for (var i = 0; i < 2; i++) await Tick();
            int locks = model.BitmapLockCount, copies = factory.Instances.Single().CopyCalls;
            int probes = model.FrameProbeCount;
            for (var i = 0; i < 6; i++) await Tick();
            Check(model.BitmapLockCount == locks && factory.Instances.Single().CopyCalls == copies && model.FrameProbeCount == probes,
                "Without a requested frame the bitmap is not locked and the renderer is not polled.");
        }
        finally { window.Close(); }
    }

    private static async Task PendingFrameAsync()
    {
        var (model, window, factory) = await OpenAsync(new(800, true));
        try
        {
            model.Motion.SetActive(false);
            var renderer = factory.Instances.Single();
            renderer.HoldFrameCompletion = true;
            int locks = model.BitmapLockCount, copies = renderer.CopyCalls, misses = model.FrameProbeNoFrameCount;
            long presented = model.PresentedFrameCount;
            var previousFrame = model.LastPresentedFrame;
            var observed = new List<ulong>();
            model.FrameCopiedObserver = observed.Add;
            ChangeTheme(window);
            await Until(() => model.FrameProbeNoFrameCount > misses, "The pending request is checked with metadata only.");
            for (var i = 0; i < 2; i++) await Tick();
            Check(model.BitmapLockCount == locks && renderer.CopyCalls == copies && model.PresentedFrameCount == presented,
                "A pending frame neither locks nor copies the bitmap nor advances presentation.");

            renderer.CompletePendingFrame();
            Check(renderer.TryGetLatestFrameInfo(previousFrame.FrameId, out var first)
                && renderer.TryGetLatestFrameInfo(previousFrame.FrameId, out var second) && first == second,
                "Repeated metadata probes do not consume the completed frame.");
            Check(renderer.CopyCalls == copies, "Metadata probes do not count as pixel copies.");
            await Until(() => model.PresentedFrameCount > presented, "The completed frame is copied after its metadata probe.");
            Check(model.BitmapLockCount == locks + 1 && renderer.CopyCalls == copies + 1,
                "Exactly one bitmap lock and pixel copy present the completed frame.");
            Check(observed.SequenceEqual(new[] { model.LastPresentedFrame.FrameId }),
                "The observer receives only the successfully copied frame.");
            Check(model.FrameProbeReadyCount > 0
                && model.FrameProbeCount == model.FrameProbeReadyCount + model.FrameProbeNoFrameCount,
                "Probe counters distinguish ready frames from missing frames.");
            for (var i = 0; i < 2; i++) await Tick();
            Check(model.BitmapLockCount == locks + 1 && renderer.CopyCalls == copies + 1 && observed.Count == 1,
                "The same completed frame is not locked, copied or observed again.");
        }
        finally { model.FrameCopiedObserver = null; window.Close(); }
    }

    private static async Task ProbeDisabledAsync()
    {
        var (model, window, factory) = await OpenAsync(new(0, false));
        try
        {
            model.Motion.SetActive(false);
            var renderer = factory.Instances.Single();
            renderer.HoldFrameCompletion = true;
            int locks = model.BitmapLockCount, copies = renderer.CopyCalls;
            ChangeTheme(window);
            await Until(() => renderer.CopyCalls > copies, "The uncapped no-probe baseline still attempts the original direct copy.");
            Check(model.BitmapLockCount > locks && model.FrameProbeCount == 0 && renderer.PeekCalls == 0,
                "Disabling metadata probes retains the legacy lock-and-copy path.");
        }
        finally { window.Close(); }
    }

    private static async Task ProbeRaceAsync()
    {
        var (model, window, factory) = await OpenAsync(new(800, true));
        try
        {
            model.Motion.SetActive(false);
            var renderer = factory.Instances.Single();
            renderer.HoldFrameCompletion = true;
            var previousFrame = model.LastPresentedFrame;
            long presented = model.PresentedFrameCount;
            ChangeTheme(window);
            renderer.CompletePendingFrame();
            renderer.DropFrameBeforeNextCopy = true;
            await Until(() => !renderer.DropFrameBeforeNextCopy, "The completed frame disappears between its probe and copy.");
            Check(model.ModelReady && model.LoadError is null && model.PresentedFrameCount == presented
                && model.LastPresentedFrame.FrameId == previousFrame.FrameId,
                "A positive probe followed by no copy does not consume a frame or fail the renderer.");

            var logicalBounds = model.Viewport.Bounds;
            model.RenderingPolicyOverride = new(160, true);
            Check(Math.Max(renderer.Configuration.Width, renderer.Configuration.Height) == 160,
                "A backing resize applies the new pixel budget.");
            Check(model.Viewport.Bounds == logicalBounds && model.LastPresentedFrame == default,
                "A backing resize keeps the logical viewport and invalidates the previous frame metadata.");
            renderer.CompletePendingFrame();
            await Until(() => model.LastPresentedFrame.Width == renderer.Configuration.Width
                && model.LastPresentedFrame.Height == renderer.Configuration.Height,
                "The resized frame is presented through the existing fallback path.");
            Check(model.LastPresentedFrame.Generation > previousFrame.Generation && model.LoadError is null,
                "The replacement backing frame comes from the new generation without a renderer failure.");
        }
        finally { window.Close(); }
    }

    private static async Task FrozenPoseAsync()
    {
        var (model, window, factory) = await OpenAsync(new(800, true));
        try
        {
            var observed = new List<ulong>();
            model.FrameCopiedObserver = observed.Add;
            model.FreezePoseForAudit(.35, .2);
            long updates = model.CameraUpdateCount;
            await Until(() => observed.Count > 0, "The frozen pose requests a final observable frame.");
            var renderer = factory.Instances.Single();
            Check(!model.Motion.IsActive && model.Motion.Yaw == .35 && model.Motion.Pitch == .2
                && Math.Abs(renderer.Configuration.YawRadians - .35) < .000001
                && Math.Abs(renderer.Configuration.PitchRadians - .2) < .000001,
                "The motion and render configuration use the same exact frozen pose.");
            long presented = model.PresentedFrameCount;
            for (var i = 0; i < 3; i++) await Tick();
            Check(model.CameraUpdateCount == updates && model.PresentedFrameCount == presented && observed.Count == 1,
                "A frozen pose does not keep submitting or observing duplicate frames.");
            model.FrameCopiedObserver = null;
            ChangeTheme(window);
            await Until(() => model.PresentedFrameCount > presented, "A later explicit background change still requests a frame.");
            Check(observed.Count == 1, "Removing the audit observer stops callbacks without changing rendering.");
        }
        finally { model.FrameCopiedObserver = null; window.Close(); }
    }

    private static void ChangeTheme(Window window) =>
        window.RequestedThemeVariant = window.ActualThemeVariant == ThemeVariant.Dark ? ThemeVariant.Light : ThemeVariant.Dark;

    private static Task CleanupAsync()
    {
        long now = 1_000;
        int requests = 0;
        using (ArtworkCleanupTriggers.BeginTestScope(() => now, () => requests++))
        {
            ArtworkCleanupTriggers.ScheduleCleanup(TimeSpan.Zero);
            Check(!ArtworkCleanupTriggers.HasPendingCleanup && requests == 0, "An empty delay does not schedule work.");
            ArtworkCleanupTriggers.ScheduleCleanup(ArtworkCleanupTriggers.ExpanderDelay);
            now = 1_100;
            ArtworkCleanupTriggers.ScheduleCleanup(ArtworkCleanupTriggers.ScrollDelay);
            Check(ArtworkCleanupTriggers.PendingDeadlineMilliseconds == 1_600,
                "A shorter scroll delay cannot shorten pending expander cleanup.");
            now = 1_300;
            ArtworkCleanupTriggers.ScheduleCleanup(ArtworkCleanupTriggers.ExpanderDelay);
            Check(ArtworkCleanupTriggers.PendingDeadlineMilliseconds == 1_900,
                "Later events extend the same cleanup deadline.");
            now = 1_899; ArtworkCleanupTriggers.FlushPendingForTests();
            Check(requests == 0 && ArtworkCleanupTriggers.HasPendingCleanup, "Cleanup waits until the exact deadline.");
            now++; ArtworkCleanupTriggers.FlushPendingForTests();
            Check(requests == 1 && !ArtworkCleanupTriggers.HasPendingCleanup, "One merged cleanup runs at its deadline.");
            now = 50_000; ArtworkCleanupTriggers.FlushPendingForTests(); ArtworkCleanupTriggers.FlushPendingForTests();
            Check(requests == 1 && !ArtworkCleanupTriggers.HasPendingCleanup, "An idle UI does not request recurring cleanup.");
            ArtworkCleanupTriggers.ScheduleCleanup(ArtworkCleanupTriggers.ScrollDelay);
        }
        Check(!ArtworkCleanupTriggers.HasPendingCleanup, "A finished test scope drops pending callbacks and delegates.");
        return Task.CompletedTask;
    }

    private static Task ExpanderAsync()
    {
        long now = 1_000;
        int requests = 0;
        using var cleanup = ArtworkCleanupTriggers.BeginTestScope(() => now, () => requests++);
        ArtworkCleanupTriggers.RegisterExpanders();
        var expander = new FASettingsExpander { Header = "x" };
        var window = new Window { Content = expander, Width = 280, Height = 240 };
        try
        {
            window.Show(); Dispatcher.UIThread.RunJobs();
            Check(!ArtworkCleanupTriggers.HasPendingCleanup, "Showing an expander does not schedule cleanup.");
            expander.IsExpanded = true;
            Check(ArtworkCleanupTriggers.PendingDeadlineMilliseconds == 1_600 && requests == 0,
                "Expanding schedules delayed cleanup without requesting it immediately.");
            now = 1_200;
            expander.IsExpanded = false;
            Check(ArtworkCleanupTriggers.PendingDeadlineMilliseconds == 1_800, "Collapsing extends the same cleanup deadline.");
            now = 1_800; ArtworkCleanupTriggers.FlushPendingForTests();
            Check(requests == 1 && !ArtworkCleanupTriggers.HasPendingCleanup, "The expander changes produce one cleanup request.");
        }
        finally { window.Close(); }
        return Task.CompletedTask;
    }

    private static Task WindowCleanupAsync()
    {
        int requests = 0, observedDeactivations = 0;
        using var cleanup = ArtworkCleanupTriggers.BeginTestScope(() => 1_000, () => requests++);
        var window = new Window { Width = 100, Height = 100 };
        EventHandler deactivated = (_, _) => observedDeactivations++;
        window.Deactivated += deactivated;
        ArtworkCleanupTriggers.AttachWindow(window);
        try
        {
            window.Show(); Dispatcher.UIThread.RunJobs();
            int beforeActivation = requests;
            HeadlessWindowActivity.SetActive(window, true);
            Check(requests == beforeActivation, "Activation does not request cleanup or install an animation hold.");
            var deactivate = HeadlessWindowActivity.GetCallback(window, false);
            deactivate();
            Check(!window.IsActive && requests == beforeActivation + 1 && !ArtworkCleanupTriggers.HasPendingCleanup,
                "Actual deactivation requests cleanup immediately without scheduling animation state.");
            ArtworkCleanupTriggers.AttachWindow(window);
            HeadlessWindowActivity.SetActive(window, true);
            deactivate();
            Check(requests == beforeActivation + 2, "Repeated hookup does not duplicate the window cleanup handler.");
            window.Close(); Dispatcher.UIThread.RunJobs();
            int afterClose = requests, observedAfterClose = observedDeactivations;
            deactivate();
            Check(observedDeactivations == observedAfterClose + 1 && requests == afterClose,
                "A captured platform callback still reaches Deactivated after close, but the cleanup handler is unhooked.");
        }
        finally { window.Deactivated -= deactivated; window.Close(); }
        return Task.CompletedTask;
    }

    private static void Gate()
    {
        var gate = new CleanupGate(TimeSpan.FromSeconds(10));
        Check(gate.TryAcquire(1_000), "The first request is allowed.");
        Check(!gate.TryAcquire(1_001) && !gate.TryAcquire(10_999), "Requests inside the interval are dropped.");
        Check(gate.TryAcquire(11_000), "A request after the interval is allowed.");
        Check(!gate.TryAcquire(15_000), "The interval restarts at the last allowed request.");
    }

    private static async Task Tick()
    {
        await Task.Delay(40);
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }

    private static async Task Until(Func<bool> condition, string reason)
    {
        for (var attempt = 0; attempt < 200 && !condition(); attempt++) { await Tick(); await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background); }
        Check(condition(), reason);
    }

    private static void Check(bool condition, string reason) { if (!condition) throw new InvalidOperationException(reason); }
}
