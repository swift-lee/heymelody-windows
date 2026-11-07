using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Templates;
using Avalonia.Styling;
using Avalonia.VisualTree;
using HeyMelody.Rendering.Filament;
using HeyMelodyWindows.App;
using HeyMelodyWindows.Ui;

/// <summary>Opt-in real-window measurements; never constructs a production backend.</summary>
internal static class NativePerformanceAudit
{
    internal const string Usage = "--native-performance-audit --scenario <static|static-idle|static-scroll|animated|model|model-scroll|model-frozen> "
        + "--backend <vulkan|opengl> --seconds <5..120> --output-dir <new-directory> "
        + "[--artwork-file <animated-webp>] [--basic-render <true|false>] "
        + "[--model-max-edge <0..4096>] [--frame-ready-probe <on|off>] [--theme <light|dark>]";
    private static AuditState _state = null!;
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static int Run(string[] args)
    {
        if (!OperatingSystem.IsWindows()) { Console.Error.WriteLine("Windows required."); return 2; }
        if (!TryParseOptions(args, out var options))
        { Console.Error.WriteLine(Usage); return 2; }
        var scenario = options["--scenario"];
        var backendName = options["--backend"];
        var seconds = int.Parse(options["--seconds"]);
        var output = Path.GetFullPath(options["--output-dir"]);
        if (Directory.Exists(output) || File.Exists(output)) { Console.Error.WriteLine("Output must be a new directory."); return 2; }
        Directory.CreateDirectory(output);
        using (new FileStream(Path.Combine(output, "audit-created"), FileMode.CreateNew, FileAccess.Write)) { }
        _state = new(scenario, backendName == "opengl" ? FilamentBackend.OpenGL : FilamentBackend.Vulkan, seconds, output,
            options.GetValueOrDefault("--artwork-file"), options.GetValueOrDefault("--basic-render") == "true",
            new(int.Parse(options.GetValueOrDefault("--model-max-edge", "800")),
                options.GetValueOrDefault("--frame-ready-probe", "on") == "on"),
            options.GetValueOrDefault("--theme", "light"));
        var thread = new Thread(() =>
        {
            var oldPath = ThemePreferences.SettingsPathOverride;
            ThemePreferences.SettingsPathOverride = Path.Combine(output, "unwritten-preferences.json");
            try
            {
                ModelRenderingPreferences.Reload();
                ModelRenderingPreferences.Apply(_state.Backend == FilamentBackend.OpenGL, ModelFrameRate.Unlimited);
                AppBuilder.Configure<AuditApplication>().UsePlatformDetect().UseHeyMelodyTypography()
                    .UseHeyMelodyRendering(basicRenderMode: _state.BasicRenderMode)
                    .StartWithClassicDesktopLifetime([], ShutdownMode.OnExplicitShutdown);
                if (File.Exists(ThemePreferences.SettingsPathOverride))
                    _state.Failure ??= "Audit unexpectedly wrote isolated preferences.";
            }
            catch (Exception error) { _state.Failure = error.ToString(); }
            finally
            {
                ThemePreferences.SettingsPathOverride = oldPath;
            }
        }) { IsBackground = true, Name = "HeyMelody performance audit" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(seconds + 90)))
        {
            WriteNew(Path.Combine(output, "summary.json"), JsonSerializer.Serialize(new
            { success = false, timedOut = true, scenario, seconds, failure = "UI thread join watchdog expired." }, Json));
            return 1;
        }
        using (var raw = new StreamWriter(new FileStream(Path.Combine(output, "samples.jsonl"), FileMode.CreateNew)))
        {
            foreach (var callback in _state.Callbacks) raw.WriteLine(JsonSerializer.Serialize(new { kind = "uiCallback", callback }));
            foreach (var sample in _state.Samples) raw.WriteLine(JsonSerializer.Serialize(new { kind = "processAndModel", sample }));
            foreach (var frame in _state.FrameCopies) raw.WriteLine(JsonSerializer.Serialize(new { kind = "bitmapCopy", frame }));
        }
        var intervals = _state.Callbacks.Where(callback => callback.IntervalMilliseconds is not null)
            .Select(callback => callback.IntervalMilliseconds!.Value).Order().ToArray();
        double? Percentile(double p) => intervals.Length == 0 ? null
            : intervals[Math.Clamp((int)Math.Ceiling(intervals.Length * p) - 1, 0, intervals.Length - 1)];
        var bitmapObservations = new List<CallbackSample>();
        foreach (var callback in _state.Callbacks)
            if (callback.ModelFrameId is > 0
                && (bitmapObservations.Count == 0 || callback.ModelFrameId != bitmapObservations[^1].ModelFrameId))
                bitmapObservations.Add(callback);
        var bitmapIntervals = bitmapObservations.Skip(1).Select((observation, index) =>
            (observation.Seconds - bitmapObservations[index].Seconds) * 1000).Order().ToArray();
        double? BitmapPercentile(double p) => bitmapIntervals.Length == 0 ? null
            : bitmapIntervals[Math.Clamp((int)Math.Ceiling(bitmapIntervals.Length * p) - 1, 0, bitmapIntervals.Length - 1)];
        var observedFrameChanges = Math.Max(0, bitmapObservations.Count - 1);
        var copyIntervals = _state.FrameCopies.Skip(1).Select((frame, index) =>
            (frame.Seconds - _state.FrameCopies[index].Seconds) * 1000).Order().ToArray();
        double? CopyPercentile(double p) => copyIntervals.Length == 0 ? null
            : copyIntervals[Math.Clamp((int)Math.Ceiling(copyIntervals.Length * p) - 1, 0, copyIntervals.Length - 1)];
        var first = _state.Samples.FirstOrDefault();
        var last = _state.Samples.LastOrDefault();
        var elapsed = last is null || first is null ? 0 : last.Seconds - first.Seconds;
        var cpu = last is null || first is null ? 0 : last.CpuSeconds - first.CpuSeconds;
        var report = new
        {
            schemaVersion = 3, harnessRevision = "v3-pixel-budget-ready-probe-20261107",
            success = _state.Completed && _state.Closed && _state.Failure is null
                && _state.Service.ForbiddenCalls == 0,
            scenario, requestedBackend = _state.Backend.ToString(), requestedBasicRenderMode = _state.BasicRenderMode,
            requestedTheme = _state.Theme,
            modelRenderPolicy = new { maximumLongEdgePixels = _state.ModelRenderPolicy.MaximumLongEdgePixels,
                probeBeforeBitmapLock = _state.ModelRenderPolicy.ProbeBeforeBitmapLock },
            requestedSeconds = seconds, warmupSeconds = 5,
            measurementSeconds = _state.MeasurementSeconds,
            measurementStartedUtc = _state.MeasurementStartedUtc, measurementEndedUtc = _state.MeasurementEndedUtc,
            process = new { architecture = RuntimeInformation.ProcessArchitecture.ToString(), Environment.ProcessorCount,
                processId = Environment.ProcessId, runtime = RuntimeInformation.FrameworkDescription,
                cpuSeconds = cpu, cpuPercentOneCore = elapsed > 0 ? cpu / elapsed * 100 : (double?)null,
                cpuPercentMachine = elapsed > 0 ? cpu / elapsed / Environment.ProcessorCount * 100 : (double?)null,
                allocatedBytes = first is null || last is null ? (long?)null : last.AllocatedBytes - first.AllocatedBytes,
                gcCollections = first is null || last is null ? null : Enumerable.Range(0, 3)
                    .Select(generation => last.GcCollections[generation] - first.GcCollections[generation]).ToArray(),
                peakWorkingSetBytes = _state.Samples.Select(sample => sample.WorkingSetBytes).DefaultIfEmpty().Max(),
                peakPrivateBytes = _state.Samples.Select(sample => sample.PrivateBytes).DefaultIfEmpty().Max() },
            uiCallbacks = new { count = _state.Callbacks.Count, intervalCount = intervals.Length,
                hertz = _state.MeasurementSeconds > 0 ? _state.Callbacks.Count / _state.MeasurementSeconds : 0,
                medianMilliseconds = Percentile(.5), p95Milliseconds = Percentile(.95), p99Milliseconds = Percentile(.99),
                maximumMilliseconds = intervals.Length == 0 ? (double?)null : intervals[^1],
                scope = scenario == "static-idle"
                    ? "No audit RAF requests; only 0.5 s process snapshots. This is the idle CPU baseline, not an FPS measurement."
                    : "Full measured TopLevel.RequestAnimationFrame callback distribution including the deadline-crossing callback; NOT displayed FPS or DWM Present timing." },
            bitmapObservation = _state.Scenario.StartsWith("model", StringComparison.Ordinal) ? new
            {
                observedFrameChanges, intervalCount = bitmapIntervals.Length,
                observedChangesPerSecond = _state.MeasurementSeconds > 0 ? observedFrameChanges / _state.MeasurementSeconds : 0,
                presentedCounterDelta = _state.ModelPresentedAtEnd - _state.ModelPresentedAtStart,
                presentedCounterDeltaPerSecond = _state.MeasurementSeconds > 0
                    ? (_state.ModelPresentedAtEnd - _state.ModelPresentedAtStart) / _state.MeasurementSeconds : null,
                medianMilliseconds = BitmapPercentile(.5), p95Milliseconds = BitmapPercentile(.95),
                p99Milliseconds = BitmapPercentile(.99),
                maximumMilliseconds = bitmapIntervals.Length == 0 ? (double?)null : bitmapIntervals[^1],
                firstObservationSeconds = bitmapObservations.FirstOrDefault()?.Seconds,
                lastNewBitmapObservationSeconds = bitmapObservations.LastOrDefault()?.Seconds,
                tailWithoutNewBitmapMilliseconds = bitmapObservations.Count == 0 ? (double?)null
                    : Math.Max(0, _state.MeasurementSeconds - bitmapObservations[^1].Seconds) * 1000,
                scope = "Full audit-RAF observation series of changes in cached LastPresentedFrame.FrameId. UI bitmap observations, NOT displayed FPS, GPU timestamps or native completion timestamps."
            } : null,
            bitmapCopies = _state.Scenario.StartsWith("model", StringComparison.Ordinal) ? new
            {
                count = _state.FrameCopies.Count, intervalCount = copyIntervals.Length,
                hertz = _state.MeasurementSeconds > 0 ? _state.FrameCopies.Count / _state.MeasurementSeconds : 0,
                medianMilliseconds = CopyPercentile(.5), p95Milliseconds = CopyPercentile(.95),
                p99Milliseconds = CopyPercentile(.99),
                maximumMilliseconds = copyIntervals.Length == 0 ? (double?)null : copyIntervals[^1],
                frameIdsStrictlyIncreasing = _state.FrameCopies.Zip(_state.FrameCopies.Skip(1))
                    .All(pair => pair.Second.FrameId > pair.First.FrameId),
                scope = "Every successful new bitmap copy observed on the UI thread during measurement; NOT displayed FPS or GPU completion timing."
            } : null,
            window = _state.Geometry, desktopAtMeasurementStart = _state.Desktop,
            actualAvaloniaRenderer = _state.Probe?.Receipt, screenshot = _state.Screenshot,
            modelAtStart = first?.Model, modelAtEnd = last?.Model,
            animatedArtworkAtStart = first?.AnimatedArtwork, animatedArtworkAtEnd = last?.AnimatedArtwork,
            animatedArtworkDrawCountDelta = _state.AnimatedDrawsAtEnd - _state.AnimatedDrawsAtStart,
            artwork = _state.ArtworkDescription, scroll = _state.ScrollDescription,
            nativeBluetoothCalls = 0, productionBackendStarted = false, forbiddenServiceCalls = _state.Service.ForbiddenCalls,
            isolatedPreferencesWritten = File.Exists(Path.Combine(output, "unwritten-preferences.json")),
            modelTimingScope = "Model render/readback values are last native CPU wall-clock statistics, not GPU timestamp timings; recent presentation intervals overlap.",
            samplingScope = "Callbacks are buffered in memory; process/model snapshots occur every 0.5 s. JSONL is written only after measurement. Includes sampling overhead.",
            focusScope = "Foreground HWND/process and Window.IsActive are recorded in every process sample; initial activation does not prove the entire measurement stayed foreground.",
            frozenScope = scenario == "model-frozen"
                ? "Fixed audit pose confirmed across a temporary size/generation barrier before measurement; bitmap/renderer/callback loop retained. Start/end camera and presented-frame counters must remain equal."
                : null,
            fixedPose = _state.FixedPose,
            windowClosed = _state.Closed, failure = _state.Failure
        };
        WriteNew(Path.Combine(output, "summary.json"), JsonSerializer.Serialize(report, Json));
        Console.WriteLine(JsonSerializer.Serialize(new { report.success, scenario, output, callbacks = _state.Callbacks.Count, failure = _state.Failure }));
        return report.success ? 0 : 1;
    }

    private static void WriteNew(string path, string text)
    {
        using var writer = new StreamWriter(new FileStream(path, FileMode.CreateNew, FileAccess.Write));
        writer.Write(text);
    }

    internal static bool TryParseOptions(string[] args, out Dictionary<string, string> options)
    {
        options = new(StringComparer.Ordinal);
        if (args.FirstOrDefault() != "--native-performance-audit") return false;
        for (int i = 1; i < args.Length; i += 2)
            if (i + 1 == args.Length || !args[i].StartsWith("--", StringComparison.Ordinal)
                || !options.TryAdd(args[i], args[i + 1])) return false;
        return !options.Keys.Any(key => key is not ("--scenario" or "--backend" or "--seconds" or "--output-dir"
                or "--artwork-file" or "--basic-render" or "--model-max-edge" or "--frame-ready-probe" or "--theme"))
            && options.TryGetValue("--scenario", out var scenario)
            && scenario is ("static" or "static-idle" or "static-scroll" or "animated" or "model" or "model-scroll" or "model-frozen")
            && options.TryGetValue("--backend", out var backend) && backend is ("vulkan" or "opengl")
            && options.TryGetValue("--seconds", out var secondsText) && int.TryParse(secondsText, out var seconds)
            && seconds is >= 5 and <= 120
            && options.TryGetValue("--output-dir", out var output) && !string.IsNullOrWhiteSpace(output)
            && (!options.TryGetValue("--basic-render", out var basic) || basic is ("true" or "false"))
            && (!options.TryGetValue("--model-max-edge", out var edgeText)
                || int.TryParse(edgeText, out var edge) && edge is >= 0 and <= 4096)
            && (!options.TryGetValue("--frame-ready-probe", out var probe) || probe is ("on" or "off"))
            && (!options.TryGetValue("--theme", out var theme) || theme is ("light" or "dark"));
    }

    private sealed class AuditState(string scenario, FilamentBackend backend, int seconds, string output, string? artwork,
        bool basicRenderMode, ModelRenderPolicy modelRenderPolicy, string theme)
    {
        public string Scenario { get; } = scenario;
        public FilamentBackend Backend { get; } = backend;
        public int Seconds { get; } = seconds;
        public string Output { get; } = output;
        public string? Artwork { get; } = artwork;
        public bool BasicRenderMode { get; } = basicRenderMode;
        public ModelRenderPolicy ModelRenderPolicy { get; } = modelRenderPolicy;
        public string Theme { get; } = theme;
        public AuditService Service { get; } = new();
        public NativeRenderingProbe? Probe;
        public List<CallbackSample> Callbacks { get; } = [];
        public List<ProcessSample> Samples { get; } = [];
        public List<FrameCopySample> FrameCopies { get; } = new((seconds + 1) * 256);
        public object? Geometry, ArtworkDescription, ScrollDescription, Desktop, Screenshot, FixedPose;
        public bool Completed, Closed;
        public double MeasurementSeconds;
        public DateTime? MeasurementStartedUtc, MeasurementEndedUtc;
        public long? ModelPresentedAtStart, ModelPresentedAtEnd;
        public long? AnimatedDrawsAtStart, AnimatedDrawsAtEnd;
        public string? Failure;
    }

    private sealed record CallbackSample(double Seconds, double? IntervalMilliseconds, double ScrollOffset,
        ulong? ModelFrameId, long? ModelPresentedFrameCount, double? ModelRenderMilliseconds, double? ModelReadbackMilliseconds);
    private readonly record struct FrameCopySample(double Seconds, ulong FrameId);
    private sealed record ProcessSample(DateTime TimestampUtc, double Seconds, double CpuSeconds, long WorkingSetBytes, long PrivateBytes,
        long AllocatedBytes, int[] GcCollections, bool WindowIsActive, DesktopIdentity Desktop, object? Model, object? AnimatedArtwork);
    private sealed record DesktopIdentity(long ForegroundHwnd, uint ForegroundProcessId, string? ForegroundProcessName);

    public sealed class AuditApplication : App
    {
        public override void OnFrameworkInitializationCompleted()
        {
            var desktop = (IClassicDesktopStyleApplicationLifetime)ApplicationLifetime!;
            ApplyTheme(_state.Theme);
            ProductArtwork.TransitionsEnabled = false;
            var window = new MainWindow(_state.Service, preview: true)
            {
                StartWithDemo = true, Width = 1280, Height = 900, CanResize = false,
                WindowStartupLocation = WindowStartupLocation.CenterScreen, ShowActivated = true, Topmost = true
            };
            var page = window.FindControl<MainView>("Page")!;
            if (!_state.Scenario.StartsWith("model", StringComparison.Ordinal))
                page.FindControl<ProductArtwork>("HeroArt")!.VisualTemplate = new AuditArtworkTemplate();
            page.SetModelDemo();
            _state.Probe = NativeRenderingProbe.Attach(window);
            desktop.MainWindow = window;
            window.Closed += (_, _) => _state.Closed = true;
            window.Opened += (_, _) => _ = MeasureAsync(window, page, desktop);
            _ = WatchdogAsync(window, desktop);
        }
    }

    private sealed class AuditArtworkTemplate : IDataTemplate
    {
        public bool Match(object? data) => true;
        public Control? Build(object? data)
        {
            if (_state.Scenario != "animated")
            {
                _state.ArtworkDescription = new { kind = "bundled-static-bitmap", source = DeviceVisuals.X4Product.Source.ToString() };
                return DeviceVisuals.DefaultTemplate.Build(DeviceVisuals.X4Product);
            }
            var path = Path.GetFullPath(_state.Artwork ?? Path.Combine(AppContext.BaseDirectory, "Fixtures", "x4-first-two-frames.webp"));
            var digest = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
            _state.ArtworkDescription = new { kind = "animated-webp", file = Path.GetFileName(path), sha256 = digest,
                scope = _state.Artwork is null ? "Two-frame test fixture, NOT representative of the full production X4 animation." : "Explicit local artwork input." };
            return new DeviceAnimatedWebpView(new(new ArtworkFile(new Uri(path), Path.GetDirectoryName(path)!, digest), DeviceVisuals.X4Product));
        }
    }

    private static async Task UntilAsync(Func<bool> condition, string failure)
    {
        var timeout = Stopwatch.StartNew();
        while (!condition())
        {
            if (timeout.Elapsed > TimeSpan.FromSeconds(40)) throw new TimeoutException(failure);
            await Task.Delay(50);
        }
    }

    private static async Task MeasureAsync(MainWindow window, MainView page, IClassicDesktopStyleApplicationLifetime desktop)
    {
        DeviceModelView? observedModel = null;
        try
        {
            var vm = (MainViewModel)page.DataContext!;
            await UntilAsync(() => vm.IsDemoSelected && vm.HasVerifiedSnapshot && vm.FeatureSections.Count > 0,
                "Local demo did not materialize.");
            page.ExpandAllSettings();
            DeviceModelView? model = null;
            DeviceAnimatedWebpView? animatedArtwork = null;
            if (_state.Scenario.StartsWith("model", StringComparison.Ordinal))
            {
                await UntilAsync(() => page.GetVisualDescendants().OfType<DeviceModelView>()
                    .Any(view => view.ModelReady && view.LastPresentedFrame.FrameId > 0), "Native model did not render.");
                model = page.GetVisualDescendants().OfType<DeviceModelView>().Single();
                observedModel = model;
                model.RenderingPolicyOverride = _state.ModelRenderPolicy;
                if (model.Diagnostics.ActualBackend != _state.Backend)
                    throw new InvalidOperationException("Model backend differs from request.");
                _state.ArtworkDescription = new { kind = "bundled-dynaudio-model", model.ModelDigest };
            }
            else if (page.GetVisualDescendants().OfType<DeviceModelView>().Any())
                throw new InvalidOperationException("A no-model scenario unexpectedly created a model view.");
            if (_state.Scenario == "animated")
            {
                var hero = page.FindControl<ProductArtwork>("HeroArt")!;
                await UntilAsync(() =>
                {
                    var view = hero.GetVisualDescendants().OfType<DeviceAnimatedWebpView>().SingleOrDefault();
                    if (view?.LoadError is not null) throw new InvalidOperationException("Animated artwork failed: " + view.LoadError);
                    return view is { AnimationLoaded: true, RenderedFrameCount: > 0 };
                }, "Animated artwork did not load and draw.");
                animatedArtwork = hero.GetVisualDescendants().OfType<DeviceAnimatedWebpView>().Single();
            }
            await Task.Delay(5000);
            if (_state.Scenario == "model-frozen")
            {
                var original = model!.LastPresentedFrame;
                var longEdge = Math.Max(original.Width, original.Height);
                if (longEdge <= 1) throw new InvalidOperationException("Fixed-pose audit requires a nontrivial viewport.");
                const double yaw = .45, pitch = -.1;
                model.FreezePoseForAudit(yaw, pitch);
                // A newer ID alone could be an old in-flight pose. Resize drains old slots and changes generation.
                model.RenderingPolicyOverride = new(longEdge - 1, _state.ModelRenderPolicy.ProbeBeforeBitmapLock);
                await UntilAsync(() => model.LastPresentedFrame.Generation > original.Generation
                    && (model.LastPresentedFrame.Width != original.Width || model.LastPresentedFrame.Height != original.Height),
                    "Fixed-pose generation barrier did not render.");
                var barrierGeneration = model.LastPresentedFrame.Generation;
                model.RenderingPolicyOverride = _state.ModelRenderPolicy;
                await UntilAsync(() => model.LastPresentedFrame.Generation > barrierGeneration
                    && model.LastPresentedFrame.Width == original.Width && model.LastPresentedFrame.Height == original.Height,
                    "Fixed audit pose did not render at the requested resolution.");
                _state.FixedPose = new { yawRadians = yaw, pitchRadians = pitch, barrierGeneration,
                    confirmedGeneration = model.LastPresentedFrame.Generation,
                    scope = "Motion is inactive before the temporary resize; only current-generation READY frames are copied after restoring the measured size." };
                await Task.Delay(1000);
            }
            var scroll = page.FindControl<ScrollViewer>("ControlScroll")!;
            var scrollMaximum = Math.Max(0, scroll.Extent.Height - scroll.Viewport.Height);
            var scrolling = _state.Scenario.EndsWith("-scroll", StringComparison.Ordinal);
            if (scrolling && scrollMaximum <= 0) throw new InvalidOperationException("Scroll scenario has no scrollable extent.");
            _state.ScrollDescription = new { enabled = scrolling, maximumOffsetDip = scrollMaximum,
                periodSeconds = 8, expandedSettings = true,
                scope = "Programmatic ScrollViewer.Offset triangle wave on each callback; NOT real pointer, wheel, or smooth-scroll input." };
            GetClientRect(window.TryGetPlatformHandle()!.Handle, out var client);
            _state.Geometry = new
            {
                requestedWidthDip = 1280, requestedHeightDip = 900, window.Width, window.Height, window.RenderScaling,
                hwnd = window.TryGetPlatformHandle()!.Handle.ToInt64(),
                clientWidthDip = window.ClientSize.Width, clientHeightDip = window.ClientSize.Height,
                physicalClientWidth = client.Right - client.Left, physicalClientHeight = client.Bottom - client.Top,
                window.IsActive, theme = window.ActualThemeVariant?.ToString(),
                modelWidth = model?.Diagnostics.Width, modelHeight = model?.Diagnostics.Height
            };
            _state.Desktop = RequireUnlockedDesktop();
            if (_state.Probe?.Queried != true) throw new InvalidOperationException("Actual compositor renderer probe did not execute.");
            _state.Screenshot = NativeRenderingProbe.Capture(window, Path.Combine(_state.Output, "measurement-start.png"));
            if (model is not null) RequireHealthyModel(model, model.Diagnostics);
            if (animatedArtwork is not null) RequireHealthyAnimation(animatedArtwork);
            _state.MeasurementStartedUtc = DateTime.UtcNow;
            var started = Stopwatch.StartNew();
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            double? lastCallback = null;
            double lastSample = 0;
            using var process = Process.GetCurrentProcess();
            var initialCameraUpdates = model?.CameraUpdateCount;
            var initialPresentedFrames = model?.PresentedFrameCount;
            _state.ModelPresentedAtStart = initialPresentedFrames;
            if (model is not null)
                model.FrameCopiedObserver = frameId => _state.FrameCopies.Add(new(started.Elapsed.TotalSeconds, frameId));
            _state.AnimatedDrawsAtStart = animatedArtwork?.RenderedFrameCount;
            long? endCameraUpdates = null;
            bool? endMotionActive = null;
            _state.Samples.Add(Sample(0));
            if (_state.Scenario == "static-idle")
            {
                while (true)
                {
                    var remaining = _state.Seconds - started.Elapsed.TotalSeconds;
                    if (remaining > 0) await Task.Delay(TimeSpan.FromSeconds(Math.Min(.5, remaining)));
                    var now = started.Elapsed.TotalSeconds;
                    if (now >= _state.Seconds) CaptureMeasurementEnd(now);
                    _state.Samples.Add(Sample(now));
                    if (_state.MeasurementEndedUtc is not null) break;
                }
            }
            else
            {
                window.RequestAnimationFrame(Callback);
                await done.Task;
            }
            if (_state.Scenario == "model-frozen"
                && (endMotionActive != false || endCameraUpdates != initialCameraUpdates
                    || _state.ModelPresentedAtEnd != initialPresentedFrames))
                throw new InvalidOperationException("Frozen-model motion/camera/presentation counters changed during measurement.");
            if (model is not null) RequireHealthyModel(model, model.Diagnostics);
            if (model is not null && (_state.FrameCopies.Count != _state.ModelPresentedAtEnd - initialPresentedFrames
                || _state.FrameCopies.Zip(_state.FrameCopies.Skip(1)).Any(pair => pair.Second.FrameId <= pair.First.FrameId)))
                throw new InvalidOperationException("Successful bitmap-copy observations disagree with monotonic presentation counters.");
            if (animatedArtwork is not null)
            {
                RequireHealthyAnimation(animatedArtwork);
                if (_state.AnimatedDrawsAtEnd <= _state.AnimatedDrawsAtStart)
                    throw new InvalidOperationException("Animated artwork did not draw during measurement.");
            }
            _state.Completed = true;

            void CaptureMeasurementEnd(double seconds)
            {
                if (model is not null) model.FrameCopiedObserver = null;
                _state.MeasurementSeconds = seconds;
                _state.MeasurementEndedUtc = DateTime.UtcNow;
                _state.ModelPresentedAtEnd = model?.PresentedFrameCount;
                _state.AnimatedDrawsAtEnd = animatedArtwork?.RenderedFrameCount;
                endCameraUpdates = model?.CameraUpdateCount;
                endMotionActive = model?.Motion.IsActive;
            }

            void Callback(TimeSpan _)
            {
                try
                {
                    var now = started.Elapsed.TotalSeconds;
                    var cachedFrame = model?.LastPresentedFrame;
                    _state.Callbacks.Add(new(now, lastCallback is { } previous ? (now - previous) * 1000 : null, scroll.Offset.Y,
                        cachedFrame?.FrameId, model?.PresentedFrameCount, cachedFrame?.RenderMilliseconds, cachedFrame?.ReadbackMilliseconds));
                    lastCallback = now;
                    if (now >= _state.Seconds)
                    {
                        CaptureMeasurementEnd(now);
                        _state.Samples.Add(Sample(now));
                        done.TrySetResult();
                        return;
                    }
                    if (scrolling)
                    {
                        double position = now % 8 / 4;
                        scroll.Offset = new Vector(0, scrollMaximum * (position <= 1 ? position : 2 - position));
                    }
                    if (now - lastSample >= .5) { _state.Samples.Add(Sample(now)); lastSample = now; }
                    window.RequestAnimationFrame(Callback);
                }
                catch (Exception error) { done.TrySetException(error); }
            }
            ProcessSample Sample(double seconds)
            {
                var timestampUtc = DateTime.UtcNow;
                var desktopIdentity = RequireUnlockedDesktop();
                process.Refresh();
                object? modelStats = null;
                if (model is not null)
                {
                    var frame = model.LastPresentedFrame;
                    var diagnostics = model.Diagnostics;
                    RequireHealthyModel(model, diagnostics);
                    var recent = model.PresentationIntervalsMilliseconds.Order().ToArray();
                    double? P(double p) => recent.Length == 0 ? null
                        : recent[Math.Clamp((int)Math.Ceiling(recent.Length * p) - 1, 0, recent.Length - 1)];
                    modelStats = new
                    {
                        model.AnimationCallbackCount, model.CameraUpdateCount, model.PresentedFrameCount,
                        model.BitmapLockCount, model.DirectFrameCopyCount, model.FallbackFrameCopyCount,
                        model.FrameProbeCount, model.FrameProbeReadyCount, model.FrameProbeNoFrameCount,
                        model.AnimationActive, motionActive = model.Motion.IsActive,
                        motionYawRadians = model.Motion.Yaw, motionPitchRadians = model.Motion.Pitch,
                        model.ModelReady, model.LoadError, rendererState = diagnostics.State.ToString(),
                        completedFrameId = diagnostics.FrameId, presentedFrameId = frame.FrameId,
                        frame.RenderMilliseconds, frame.ReadbackMilliseconds, diagnostics.Backend,
                        actualBackend = diagnostics.ActualBackend?.ToString(), diagnostics.Width, diagnostics.Height,
                        diagnostics.MsaaSamples, diagnostics.SourceMeshes, diagnostics.SourceTriangles,
                        recentPresentationIntervalCount = recent.Length, recentPresentationMedianMilliseconds = P(.5),
                        recentPresentationP95Milliseconds = P(.95)
                    };
                }
                object? animationStats = null;
                if (animatedArtwork is not null)
                {
                    RequireHealthyAnimation(animatedArtwork);
                    animationStats = new
                    {
                        animatedArtwork.AnimationLoaded, animatedArtwork.AnimationActive, animatedArtwork.LoadError,
                        animatedArtwork.RenderedFrameCount,
                        scope = "Actual draw callbacks, not distinct decoded WebP frames or displayed frames."
                    };
                }
                return new(timestampUtc, seconds, process.TotalProcessorTime.TotalSeconds, process.WorkingSet64, process.PrivateMemorySize64,
                    GC.GetTotalAllocatedBytes(precise: false), [GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2)],
                    window.IsActive, desktopIdentity, modelStats, animationStats);
            }
        }
        catch (Exception error) { _state.Failure = error.ToString(); }
        finally
        {
            if (observedModel is not null) observedModel.FrameCopiedObserver = null;
            window.Close();
            desktop.Shutdown(_state.Failure is null ? 0 : 1);
        }
    }

    private static void RequireHealthyModel(DeviceModelView model, FilamentDiagnostics diagnostics)
    {
        if (!model.ModelReady || model.LoadError is not null || diagnostics.State != FilamentRendererState.Ready
            || diagnostics.ActualBackend != _state.Backend)
            throw new InvalidOperationException($"Model is not healthy: ready={model.ModelReady}, state={diagnostics.State}, "
                + $"backend={diagnostics.ActualBackend}, error={model.LoadError}");
    }

    private static void RequireHealthyAnimation(DeviceAnimatedWebpView view)
    {
        if (!view.AnimationLoaded || !view.AnimationActive || view.LoadError is not null)
            throw new InvalidOperationException($"Animated artwork is not healthy: loaded={view.AnimationLoaded}, "
                + $"active={view.AnimationActive}, error={view.LoadError}");
    }

    private static async Task WatchdogAsync(Window window, IClassicDesktopStyleApplicationLifetime desktop)
    {
        await Task.Delay(TimeSpan.FromSeconds(_state.Seconds + 65));
        if (_state.Closed) return;
        _state.Failure ??= "Native performance audit watchdog expired.";
        window.Close();
        desktop.Shutdown(1);
    }

    private sealed class AuditService : IHeadsetService, IArtworkService
    {
        public int ForbiddenCalls { get; private set; }
        public Task<IReadOnlyList<UiDevice>> EnumerateAsync(CancellationToken token) => Task.FromResult<IReadOnlyList<UiDevice>>([]);
        public Task<UiSnapshot> ConnectAsync(string identity, CancellationToken token) => throw Unexpected();
        public Task DisconnectAsync(CancellationToken token) => throw Unexpected();
        public Task<UiSnapshot> RefreshAsync(CancellationToken token) => throw Unexpected();
        public Task<UiApplyResult> ApplyAsync(UiAction action, CancellationToken token) => throw Unexpected();
        public Task<UiArtwork?> AcquireArtworkAsync(string productId, int? color, string role, CancellationToken token) => throw Unexpected();
        public Task ReleaseArtworkAsync(string leaseId, CancellationToken token) => throw Unexpected();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        private Exception Unexpected()
        { ForbiddenCalls++; return new InvalidOperationException("Audit must not call a headset or artwork backend."); }
    }

    private static DesktopIdentity RequireUnlockedDesktop()
    {
        var foreground = GetForegroundWindow();
        GetWindowThreadProcessId(foreground, out var processId);
        if (foreground == IntPtr.Zero || processId == 0)
            throw new InvalidOperationException("No accessible foreground window; an unlocked interactive desktop cannot be verified.");
        string? processName = null;
        if (processId != 0)
        {
            using var process = Process.GetProcessById((int)processId);
            processName = process.ProcessName;
        }
        if (processName is "LockApp" or "LogonUI")
            throw new InvalidOperationException($"Desktop is locked ({processName}); no visible-window performance claim is possible.");
        return new(foreground.ToInt64(), processId, processName);
    }

    [StructLayout(LayoutKind.Sequential)] private struct ClientRect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetClientRect(IntPtr hwnd, out ClientRect rect);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
}
