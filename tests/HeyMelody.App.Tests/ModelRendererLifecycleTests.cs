using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;
using FluentAvalonia.UI.Controls;
using HeyMelody.Rendering.Filament;
using HeyMelody.Rendering.Tests;
using HeyMelodyWindows.App;
using HeyMelodyWindows.Ui;

internal static class ModelRendererLifecycleTests
{
    internal static async Task RunAsync(string output)
    {
        var old = ModelRenderingPreferences.CompatibilityMode;
        var oldDiagnostics = RenderDiagnostics.Enabled;
        try
        {
            ModelRenderingPreferences.Apply(false);
            RenderDiagnostics.Set(true);
            using (var ringBarrier = new ManualResetEventSlim(false))
            {
                var ringFactory = new RecordingFilamentRendererFactory { CreateBarrier = ringBarrier };
                var ringView = View(ringFactory);
                int measurements = 0;
                ModelLoadTimings measured = default;
                void OnMeasured(DeviceModelView view, ModelLoadTimings timings)
                {
                    if (!ReferenceEquals(view, ringView)) return;
                    ++measurements;
                    measured = timings;
                }
                DeviceModelView.LoadMeasured += OnMeasured;
                var ringWindow = new Window { Content = ringView, Width = 280, Height = 240 };
                try
                {
                    ringWindow.Show(); Dispatcher.UIThread.RunJobs();
                    Check(ringView.LoadingIndicatorVisible && ringView.VisibleStatus is null
                        && ringView.GetVisualDescendants().OfType<FAProgressRing>().Single() is { IsVisible: true, IsActive: true },
                        "A loading model shows the progress ring and no loading text.");
                    ringBarrier.Set();
                    await UntilAsync(() => ringView.CaptureLatestFrame(out _) is not null && !ringView.LoadingIndicatorVisible,
                        "The first presented frame hides the progress ring.");
                    Check(ringView.VisibleStatus is null, "A ready model shows no status text.");
                    Check(measurements == 1 && measured.TotalMilliseconds >= measured.FirstFrameMilliseconds
                        && measured.ReadVerifyMilliseconds >= 0 && measured.ModelLoadMilliseconds >= 0,
                        "The first presented frame completes one load measurement using elapsed time.");
                    _ = ringView.CaptureLatestFrame(out _);
                    Check(measurements == 1, "Subsequent frames do not report another model load.");
                    await TickAsync();
                    var overlay = ringView.GetVisualDescendants().OfType<TextBlock>()
                        .Single(text => text.Text?.StartsWith("3D ", StringComparison.Ordinal) == true);
                    Check(overlay.Text!.Contains("\n加载 ", StringComparison.Ordinal)
                        && overlay.TextWrapping == Avalonia.Media.TextWrapping.Wrap
                        && overlay.Bounds.Width <= ringView.Bounds.Width - 16 + 1,
                        "Load diagnostics wrap inside a narrow model viewport instead of clipping the new row.");
                }
                finally { DeviceModelView.LoadMeasured -= OnMeasured; ringBarrier.Set(); ringWindow.Close(); }
            }
            RenderDiagnostics.Set(oldDiagnostics);
            int leases = 0;
            var invalidFactory = new RecordingFilamentRendererFactory();
            var invalidRead = new DeviceModelView(Visual(), invalidFactory,
                _ => Task.FromException<FilamentModelData>(new InvalidDataException("Deliberate invalid model read.")));
            var invalidWindow = new Window { Content = invalidRead, Width = 280, Height = 240 };
            try
            {
                invalidWindow.Show();
                await UntilAsync(() => invalidRead.LoadError is not null, "An already failed read reaches the view's error presentation.");
                Check(invalidFactory.CreateCalls == 0 && invalidRead.VisibleStatus == "三维模型资源无效",
                    "An already failed model read never creates an engine and keeps its invalid-resource classification.");
            }
            finally { invalidWindow.Close(); }

            var combinedRead = new TaskCompletionSource<FilamentModelData>(TaskCreationOptions.RunContinuationsAsynchronously);
            var combinedFactory = new RecordingFilamentRendererFactory { FailCreate = true };
            var combined = new DeviceModelView(Visual(), combinedFactory, _ => combinedRead.Task);
            var combinedWindow = new Window { Content = combined, Width = 280, Height = 240 };
            try
            {
                combinedWindow.Show();
                await UntilAsync(() => combinedFactory.CreateCalls == 1, "The backend can fail while model validation is still pending.");
                combinedRead.SetException(new InvalidDataException("The pending model was also invalid."));
                await UntilAsync(() => combined.LoadError is not null, "Combined read/backend failures reach the view.");
                Check(combined.VisibleStatus == "三维模型资源无效" && combinedFactory.Instances.Count == 0,
                    "A later invalid read retains the original read-before-backend error priority.");
            }
            finally { combinedRead.TrySetCanceled(); combinedWindow.Close(); }

            var pendingRead = new TaskCompletionSource<FilamentModelData>(TaskCreationOptions.RunContinuationsAsynchronously);
            var readFactory = new RecordingFilamentRendererFactory();
            var reading = new DeviceModelView(Visual(() =>
            {
                Interlocked.Increment(ref leases);
                return new Lease(() => Interlocked.Decrement(ref leases));
            }), readFactory, _ => pendingRead.Task);
            var readingWindow = new Window { Content = reading, Width = 280, Height = 240 };
            try
            {
                readingWindow.Show();
                await UntilAsync(() => readFactory.CreateCalls == 1, "Engine creation overlaps an outstanding model read.");
                readingWindow.Content = null;
                Check(leases == 1, "Detaching keeps the loading lease until the outstanding read really finishes.");
                pendingRead.SetException(new InvalidDataException("A cancelled read can still fail."));
                await UntilAsync(() => leases == 0 && readFactory.Instances.Single().Disposed,
                    "A late read failure is observed, the cancelled engine is disposed and all leases reach zero.");
                Check(!reading.ModelReady && reading.CaptureLatestFrame(out _) is null,
                    "A cancelled model read cannot publish a renderer or stale pixels.");
            }
            finally { pendingRead.TrySetCanceled(); readingWindow.Close(); }

            var cacheFactory = new RecordingFilamentRendererFactory();
            var cacheView = View(cacheFactory, () => { Interlocked.Increment(ref leases); return new Lease(() => Interlocked.Decrement(ref leases)); });
            var cacheWindow = new Window { Content = cacheView, Width = 280, Height = 240 };
            try
            {
                cacheWindow.Show();
                await UntilAsync(() => cacheView.CaptureLatestFrame(out _) is not null, "A visible model presents a completed fake frame.");
                var snapshot = cacheView.CaptureLatestFrame(out var firstFrame)!;
                byte firstByte = snapshot[0]; snapshot[0] ^= 255;
                var repeated = cacheView.CaptureLatestFrame(out var repeatedFrame)!;
                Check(repeated[0] == firstByte && repeatedFrame == firstFrame,
                    "Repeated snapshots copy cached pixels without consuming a new native frame or exposing mutable storage.");
                cacheWindow.Width = 190; cacheWindow.Height = 340;
                await UntilAsync(() =>
                {
                    _ = cacheView.CaptureLatestFrame(out var frame);
                    var expected = cacheView.RenderingPolicy.GetPixelSize(cacheView.Viewport.Bounds.Size, cacheWindow.RenderScaling);
                    return frame.Width == expected.Width && frame.Height == expected.Height
                        && frame.Width != firstFrame.Width;
                }, "A portrait resize presents only a frame matching the current policy's DPI-aware backing dimensions.");
                Check(Math.Abs(cacheFactory.Instances.Single().Configuration.FitMargin - 1.08f) < .0001,
                    "Portrait views retain the common fit margin; native camera fitting owns the aspect ratio.");
                cacheWindow.Content = null;
                await UntilAsync(() => cacheFactory.Instances.Single().Disposed && leases == 0,
                    "Detaching releases the renderer and every loading/display artwork lease.");
                Check(cacheView.CaptureLatestFrame(out _) is null && cacheView.Viewport.Source is null,
                    "Detached views cannot expose a stale cached frame or bitmap.");
            }
            finally { cacheWindow.Close(); }

            foreach (bool compatibility in new[] { false, true })
            {
                ModelRenderingPreferences.Apply(compatibility);
                var factory = new RecordingFilamentRendererFactory { FailBackend = true };
                var failed = View(factory);
                var window = new Window { Content = failed, Width = 280, Height = 240 };
                try
                {
                    window.Show();
                    await UntilAsync(() => failed.LoadError is not null, "A backend initialization failure reaches static artwork.");
                    Check(!failed.LoadingIndicatorVisible && failed.VisibleStatus is not null,
                        "A failure replaces the progress ring with its guidance text.");
                    bool hint = failed.GetVisualDescendants().OfType<TextBlock>().Any(text => text.Text?.Contains("“3D模型渲染API”中选择 OpenGL") == true);
                    Check(hint == (!compatibility && !OperatingSystem.IsMacOS()),
                        "Only an unavailable Vulkan backend recommends manually enabling compatibility.");
                    await TickAsync();
                    Check(ModelRenderingPreferences.CompatibilityMode == compatibility && factory.CreateCalls == 1,
                        "Backend failure does not change the preference or automatically switch backend.");
                    if (!compatibility && !OperatingSystem.IsMacOS())
                    {
                        using var frame = window.CaptureRenderedFrame() ?? throw new InvalidOperationException("Missing Vulkan error region.");
                        using var stream = new FileStream(Path.Combine(output, "model-vulkan-unavailable.png"), FileMode.CreateNew);
                        frame.Save(stream, Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
                    }
                }
                finally { window.Close(); }
            }
            ModelRenderingPreferences.Apply(false);
            foreach (string failure in new[] { "create", "load", "native-model", "frame", "backend-frame" })
            {
                var factory = new RecordingFilamentRendererFactory
                { FailCreate = failure == "create", FailLoad = failure == "load", FailFrame = failure == "frame",
                    FailBackendFrame = failure == "backend-frame", FailNativeModel = failure == "native-model" };
                var model = View(factory);
                var window = new Window { Content = model, Width = 280, Height = 240 };
                try
                {
                    window.Show();
                    try { await UntilAsync(() => model.LoadError is not null, "Renderer " + failure + " failure is reported."); }
                    catch (Exception exception)
                    {
                        throw new InvalidOperationException($"{failure}: ready={model.ModelReady}, active={model.AnimationActive}, create={factory.CreateCalls}, "
                            + $"instances={factory.Instances.Count}, bounds={model.Viewport.Bounds}, frames={factory.Instances.LastOrDefault()?.FrameRequests}, "
                            + $"error={model.LoadError}", exception);
                    }
                    await UntilAsync(() => factory.Instances.All(instance => instance.Disposed), "Failed native resources are disposed.");
                    Check(!model.ModelReady && !model.AnimationActive && model.CaptureLatestFrame(out _) is null,
                        "Failed rendering stops the frame loop and clears the image rather than keeping stale pixels.");
                    Check(model.GetVisualDescendants().OfType<FAIconSourceElement>()
                        .Any(icon => IconTests.Matches(icon.IconSource, AppIcon.Audio, 96)),
                        "Renderer failure reaches the bundled Fluent artwork fallback without recursing into GLB.");
                    Check(model.GetVisualDescendants().OfType<TextBlock>().Any(text => text.Text?.Contains("“3D模型渲染API”中选择 OpenGL") == true)
                        == (failure == "backend-frame" && !OperatingSystem.IsMacOS()),
                        "Vulkan rendering failures suggest compatibility, while component, model and unclassified errors do not.");
                    if (failure == "native-model")
                        Check(model.GetVisualDescendants().OfType<TextBlock>().Any(text => text.Text == "三维模型资源加载失败"),
                            "Native model parsing failure is identified as a resource load failure.");
                }
                finally { window.Close(); }
            }

            using var barrier = new ManualResetEventSlim();
            var delayedFactory = new RecordingFilamentRendererFactory { CreateBarrier = barrier, ObserveCancellation = false };
            var delayed = View(delayedFactory);
            var delayedWindow = new Window { Content = delayed, Width = 280, Height = 240 };
            try
            {
                delayedWindow.Show();
                await UntilAsync(() => delayedFactory.CreateCalls == 1, "A background model load reaches the factory.");
                delayedWindow.Content = null;
                Dispatcher.UIThread.RunJobs();
                barrier.Set();
                await UntilAsync(() => delayedFactory.Instances.Count == 1 && delayedFactory.Instances[0].Disposed,
                    "A renderer whose creation completes after detach is disposed instead of attached.");
                Check(!delayed.ModelReady && !delayed.AnimationActive && delayed.CaptureLatestFrame(out _) is null,
                    "Stale model completion cannot revive a detached viewport or present its pixels.");
            }
            finally { barrier.Set(); delayedWindow.Close(); }

            if (!OperatingSystem.IsMacOS())
            {
                using var cancellationBarrier = new ManualResetEventSlim();
                var cancellableFactory = new RecordingFilamentRendererFactory { CreateBarrier = cancellationBarrier };
                var switching = View(cancellableFactory);
                var switchingWindow = new Window { Content = switching, Width = 280, Height = 240 };
                try
                {
                    switchingWindow.Show();
                    await UntilAsync(() => cancellableFactory.CreateCalls == 1, "Creation reaches a cancellable factory.");
                    ModelRenderingPreferences.Apply(true);
                    await UntilAsync(() => cancellableFactory.CreateCalls == 2, "Switching cancels old creation and starts the selected backend.");
                    ModelRenderingPreferences.Apply(false);
                    await UntilAsync(() => cancellableFactory.CreateCalls == 3, "A second switch cancels the superseded backend.");
                    cancellationBarrier.Set();
                    await UntilAsync(() => switching.ModelReady, "Only the latest selected backend completes loading.");
                    Check(cancellableFactory.Instances.Count == 1
                        && switching.Diagnostics.ActualBackend == FilamentBackend.Vulkan,
                        "Cancelled loads cannot publish a stale renderer or change the current preference.");
                }
                finally { cancellationBarrier.Set(); switchingWindow.Close(); }
            }
        }
        finally { RenderDiagnostics.Set(oldDiagnostics); ModelRenderingPreferences.Apply(old); }
    }

    private static DeviceModelView View(RecordingFilamentRendererFactory factory, Func<IDisposable>? lease = null) => new(Visual(lease), factory);

    private static DeviceModelVisual Visual(Func<IDisposable>? lease = null) => new(
        new ArtworkFile(new Uri("avares://HeyMelody/Assets/Devices/EncoFree4Dynaudio/model.glb"), "", GlbSource.DynaudioSha256, lease),
        DeviceVisuals.GenericProduct);

    private sealed class Lease(Action release) : IDisposable
    {
        private Action? _release = release;
        public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
    }

    internal static async Task UntilAsync(Func<bool> condition, string reason)
    {
        for (int attempt = 0; attempt < 300 && !condition(); attempt++) await TickAsync();
        Check(condition(), reason);
    }
    private static async Task TickAsync()
    {
        await Task.Delay(20);
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }
    private static void Check(bool condition, string reason)
    { if (!condition) throw new InvalidOperationException(reason); }
}
