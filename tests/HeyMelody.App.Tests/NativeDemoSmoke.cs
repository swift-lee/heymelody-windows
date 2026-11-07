using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using HeyMelody.Rendering.Filament;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Interactivity;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.VisualTree;
using FluentAvalonia.UI.Controls;
using HeyMelodyWindows.App;
using HeyMelodyWindows.Ui;

/// <summary>Runs the dedicated demo startup in a real Win32 window without a Bluetooth backend.</summary>
internal static partial class NativeDemoSmoke
{
    private static State _state = new();

    public static int Run(string? output = null, FilamentBackend backend = FilamentBackend.Vulkan,
        bool activityRegression = false)
    {
        if (!OperatingSystem.IsWindows()) return 2;
        _state = new() { Output = output, RequestedBackend = backend, ActivityRegression = activityRegression };
        if (output is not null) Directory.CreateDirectory(output);
        var thread = new Thread(() =>
        {
            var oldPath = ThemePreferences.SettingsPathOverride;
            _state.PreferencesPath = Path.Combine(output ?? Path.GetTempPath(),
                "unwritten-demo-preferences-" + Guid.NewGuid().ToString("N") + ".json");
            ThemePreferences.SettingsPathOverride = _state.PreferencesPath;
            try
            {
                RunWithIsolatedModelPreferences(backend, activityRegression);
            }
            catch (Exception error) { _state.Failure = error.ToString(); }
            finally
            {
                ThemePreferences.SettingsPathOverride = oldPath;
            }
        }) { IsBackground = true, Name = "HeyMelody native demo smoke" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        var timeout = activityRegression ? TimeSpan.FromMinutes(8) : TimeSpan.FromSeconds(60);
        if (!thread.Join(timeout))
        {
            var timeoutReport = new
            {
                success = false, timedOut = true, windowClosed = false, activityRegression,
                requestedBackend = backend.ToString(), timeoutSeconds = timeout.TotalSeconds,
                scope = "minimal timeout receipt; the UI thread is still alive, so its mutable counters and samples are not inspected",
                failure = $"Native demo window did not stop within {timeout.TotalSeconds:0} seconds."
            };
            Console.WriteLine(JsonSerializer.Serialize(timeoutReport));
            if (output is not null)
                File.WriteAllText(Path.Combine(output, "native-demo-receipts.json"),
                    JsonSerializer.Serialize(timeoutReport, new JsonSerializerOptions { WriteIndented = true }));
            return 1;
        }
        bool success = _state.Failure is null && _state.Completed && _state.Closed;
        var report = new
        {
            success, passed = _state.Passed, windowClosed = _state.Closed,
            nativeBluetoothCalls = 0, calibrationProcessLaunches = _state.CalibrationLaunches,
            headsetOrArtworkCalls = _state.Service.ForbiddenCalls, initialMockEnumerations = _state.Service.Enumerations,
            modelSha256 = DynaudioModelTests.ExpectedSha256, _state.FirstRenderedTriangles, _state.FirstRenderedSceneItems,
            appAssemblySha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(DeviceModelView).Assembly.Location))).ToLowerInvariant(),
            nativeLibrarySha256 = NativeLibraryHash(),
            _state.PreferredBackend, _state.ActiveBackend, backendTransitions = _state.BackendTransitions, controlLayouts = _state.ControlLayouts,
            frameRateTransitions = _state.FrameRateTransitions,
            activityRegression = _state.ActivityRegression, activityPhases = _state.ActivityPhases,
            activityFocusDiagnostics = _state.ActivityFocusDiagnostics,
            startupDiagnostics = _state.StartupDiagnostics,
            modelRendering = _state.ModelRendering,
            guiRenderingPolicy = _state.ActivityRegression
                ? "shared default hardware compositor selection configured; actual Avalonia GPU is not observed by this receipt"
                : "existing native smoke platform defaults",
            preferencesWritten = _state.PreferencesPath is { } preferencePath && File.Exists(preferencePath),
            screenshots = _state.Screenshots,
            scope = _state.ActivityRegression
                ? "fixed native Win32 demo, DPI, Dynaudio model, actual backend and Unlimited; thirty-second activity/lifecycle phases with real sustained native focus loss; "
                    + "one hit-tested native click in this test's blank helper with the cursor restored; progress/backpressure observations, not a minimum FPS or hardware receipt"
                : "real Win32 demo startup, model scene and animation, themes, responsive layout, local action guards and dirty close; optional local Filament model-frame captures; no Bluetooth backend",
            failure = _state.Failure
        };
        Console.WriteLine(JsonSerializer.Serialize(report));
        if (output is not null)
            File.WriteAllText(Path.Combine(output, "native-demo-receipts.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        return success ? 0 : 1;
    }

    // Keep beforefieldinit preference reads behind the path override, including when the JIT compiles this helper.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void RunWithIsolatedModelPreferences(FilamentBackend backend, bool activityRegression)
    {
        var oldPreference = ModelRenderingPreferences.CompatibilityMode;
        var oldFrameRate = ModelRenderingPreferences.FrameRate;
        try
        {
            ModelRenderingPreferences.Reload();
            if (activityRegression) ModelRenderingPreferences.Apply(backend == FilamentBackend.OpenGL, ModelFrameRate.Unlimited);
            else ModelRenderingPreferences.Apply(backend == FilamentBackend.OpenGL);
            var builder = AppBuilder.Configure<SmokeApplication>().UsePlatformDetect().UseHeyMelodyTypography().LogToTrace();
            if (activityRegression) builder = builder.UseHeyMelodyRendering(basicRenderMode: false);
            builder.StartWithClassicDesktopLifetime([], ShutdownMode.OnExplicitShutdown);
            Check(!File.Exists(_state.PreferencesPath), "Native demo verification never writes user preferences.");
        }
        finally { ModelRenderingPreferences.Apply(oldPreference, oldFrameRate); }
    }

    private static async Task CheckBackendSwitchingAsync(DeviceModelView model)
    {
        var initial = _state.RequestedBackend;
        var other = initial == FilamentBackend.Vulkan ? FilamentBackend.OpenGL : FilamentBackend.Vulkan;
        foreach (var requested in new[] { other, initial })
        {
            ModelRenderingPreferences.Apply(requested == FilamentBackend.OpenGL);
            Check(!model.ModelReady && model.CaptureLatestFrame(out _) is null,
                "Changing backend immediately retires the old renderer and clears its frame.");
            await UntilAsync(() => model.ModelReady && model.Diagnostics.ActualBackend == requested
                && model.CaptureLatestFrame(out _) is not null, "The selected replacement backend presents an actual model frame.");
            Check(model.Diagnostics.RequestedBackend == requested
                && model.Diagnostics.SourceTriangles == DynaudioModelTests.ExpectedTriangles && model.LoadError is null,
                "A live Vulkan/OpenGL switch preserves the full model and uses exactly the chosen backend.");
            _state.BackendTransitions.Add(requested.ToString());
        }
    }

    private static async Task CheckFrameRateSwitchingAsync(DeviceModelView model)
    {
        var backend = _state.RequestedBackend;
        foreach (var rate in new[] { ModelFrameRate.Fps30, ModelFrameRate.Fps60, ModelFrameRate.Unlimited })
        {
            Check(model.CaptureLatestFrame(out var before) is not null && model.Viewport.Source is not null,
                "Frame-rate switching starts with a presented native bitmap.");
            var bitmap = model.Viewport.Source;
            var firstUpdate = model.CameraUpdateCount;
            var firstCallback = model.AnimationCallbackCount;
            var elapsed = Stopwatch.StartNew();
            ModelRenderingPreferences.Apply(backend == FilamentBackend.OpenGL, rate);
            var continuousBitmap = model.ModelReady && ReferenceEquals(model.Viewport.Source, bitmap);
            var monotonicFrames = true;
            var unchangedBackend = true;
            var previousFrameId = before.FrameId;
            do
            {
                await Task.Delay(25);
                var diagnostics = model.Diagnostics;
                continuousBitmap &= model.ModelReady && model.AnimationActive && model.LoadError is null
                    && ReferenceEquals(model.Viewport.Source, bitmap);
                monotonicFrames &= diagnostics.FrameId >= previousFrameId;
                previousFrameId = diagnostics.FrameId;
                unchangedBackend &= diagnostics.RequestedBackend == backend && diagnostics.ActualBackend == backend;
            } while (elapsed.Elapsed.TotalMilliseconds < 650);
            elapsed.Stop();
            var updates = model.CameraUpdateCount - firstUpdate;
            var callbacks = model.AnimationCallbackCount - firstCallback;
            long? limit = rate == ModelFrameRate.Unlimited ? null
                : (long)Math.Ceiling(elapsed.Elapsed.TotalSeconds * (int)rate) + 2;
            var selectedRate = ThemePreferences.Serialize(rate);
            var pixels = model.CaptureLatestFrame(out var after);
            var current = model.Diagnostics;
            Check(updates <= callbacks, $"The {selectedRate} choice submits at most once per actual animation callback ({updates} <= {callbacks}).");
            if (limit is { } frameLimit)
                Check(updates <= frameLimit,
                    $"The {(int)rate} FPS setting bounds camera submissions to its elapsed-time limit ({updates} <= {frameLimit}).");
            Check(continuousBitmap && monotonicFrames && unchangedBackend && pixels is not null
                && after.FrameId > before.FrameId && model.AnimationActive && model.LoadError is null
                && current.ActualBackend == backend && ModelRenderingPreferences.FrameRate == rate,
                $"Changing to {selectedRate} keeps the real {backend} renderer and bitmap alive while new frames continue.");
            _state.FrameRateTransitions.Add(new
            {
                selectedRate, framesPerSecond = rate == ModelFrameRate.Unlimited ? (int?)null : (int)rate,
                elapsedSeconds = elapsed.Elapsed.TotalSeconds, animationCallbacks = callbacks,
                cameraUpdates = updates, maximumExpectedUpdates = limit,
                frameIdBefore = before.FrameId, frameIdAfter = after.FrameId,
                requestedBackend = current.RequestedBackend.ToString(), actualBackend = current.ActualBackend.ToString(),
                animationActive = model.AnimationActive, continuousBitmap, monotonicFrames,
                scope = rate == ModelFrameRate.Unlimited
                    ? "one request per actual animation callback and continuity; no numeric frame-rate ceiling"
                    : "fixed-rate upper bound, one request per animation callback and continuity; no minimum frame-rate requirement"
            });
        }
    }

    private static string? NativeLibraryHash()
    {
        var module = Process.GetCurrentProcess().Modules.Cast<ProcessModule>().FirstOrDefault(candidate =>
            candidate.ModuleName.Contains("HeyMelody.Filament.Native", StringComparison.OrdinalIgnoreCase));
        return module is null ? null : Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(module.FileName))).ToLowerInvariant();
    }

    private static void CaptureStartupDiagnostics(MainWindow window, ProductArtwork hero)
    {
        try
        {
            var snapshot = new
            {
                capturedBeforeClose = !_state.Closed,
                requestedBackend = _state.RequestedBackend.ToString(),
                preferredBackend = ModelRenderingPreferences.Backend.ToString(),
                frameRate = ModelRenderingPreferences.FrameRate.ToString(),
                compatibilityMode = ModelRenderingPreferences.CompatibilityMode,
                window = new
                {
                    control = StartupControlState(window), state = window.WindowState.ToString(),
                    window.IsActive, window.RenderScaling
                },
                hero = StartupControlState(hero),
                models = hero.GetVisualDescendants().OfType<DeviceModelView>().Select(model =>
                {
                    var diagnostics = model.Diagnostics;
                    return new
                    {
                        type = model.GetType().Name,
                        model.ModelDigest, model.ModelReady, model.LoadError, model.VisibleStatus,
                        model.AnimationActive, model.AnimationCallbackCount, model.CameraUpdateCount, model.LoadTimings,
                        renderingPolicy = model.RenderingPolicy,
                        model.FrameProbeCount, model.FrameProbeReadyCount, model.FrameProbeNoFrameCount,
                        model.BitmapLockCount, model.DirectFrameCopyCount, model.FallbackFrameCopyCount,
                        motion = new { model.Motion.IsActive, phase = model.Motion.Phase.ToString() },
                        control = StartupControlState(model),
                        root = StartupControlState(model.Content as Control),
                        viewport = StartupControlState(model.Viewport),
                        diagnostics = new
                        {
                            state = diagnostics.State.ToString(), diagnostics.Backend,
                            requestedBackend = diagnostics.RequestedBackend?.ToString(),
                            actualBackend = diagnostics.ActualBackend?.ToString(),
                            diagnostics.Width, diagnostics.Height, diagnostics.FrameId, diagnostics.Generation,
                            diagnostics.SourceMeshes, diagnostics.SourceTriangles, diagnostics.CreationTimings
                        }
                    };
                }).ToArray()
            };
            // Validate and freeze the snapshot before teardown; diagnostic failures must not mask the scene assertion.
            _state.StartupDiagnostics = JsonSerializer.SerializeToElement(snapshot, new JsonSerializerOptions
            {
                NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals
            });
        }
        catch (Exception error)
        {
            _state.StartupDiagnostics = new { captureFailed = true, error = error.ToString() };
        }
    }

    private static object? StartupControlState(Control? control)
    {
        if (control is null) return null;
        var bounds = control.Bounds;
        return new
        {
            control.IsVisible, control.IsEffectivelyVisible,
            attached = TopLevel.GetTopLevel(control) is not null,
            x = bounds.X, y = bounds.Y, width = bounds.Width, height = bounds.Height
        };
    }

    private sealed class State
    {
        public int Passed, CalibrationLaunches;
        public bool Completed, Closed;
        public bool ActivityRegression;
        public string? Failure;
        public string? Output;
        public string? PreferencesPath, PreferredBackend, ActiveBackend;
        public FilamentBackend RequestedBackend;
        public long FirstRenderedTriangles;
        public int FirstRenderedSceneItems;
        public object? StartupDiagnostics;
        public object? ModelRendering;
        public object? ActivityFocusDiagnostics;
        public DemoService Service { get; } = new();
        public List<object> Screenshots { get; } = [];
        public List<object> ControlLayouts { get; } = [];
        public List<string> BackendTransitions { get; } = [];
        public List<object> FrameRateTransitions { get; } = [];
        public List<object> ActivityPhases { get; } = [];
    }

    public sealed class SmokeApplication : App
    {
        public override void OnFrameworkInitializationCompleted()
        {
            var desktop = (IClassicDesktopStyleApplicationLifetime)ApplicationLifetime!;
            var window = new MainWindow(_state.Service, preview: true)
            {
                StartWithDemo = true, ShowActivated = _state.Output is not null || _state.ActivityRegression,
                WindowStartupLocation = _state.Output is null && !_state.ActivityRegression
                    ? WindowStartupLocation.Manual : WindowStartupLocation.CenterScreen,
                Topmost = _state.Output is not null || _state.ActivityRegression
            };
            if (_state.Output is null && !_state.ActivityRegression) window.Position = new(-10000, -10000);
            if (_state.ActivityRegression)
            {
                window.Width = 1280;
                window.Height = 900;
                window.CanResize = false;
            }
            window.FindControl<MainView>("Page")!.SetModelDemo();
            desktop.MainWindow = window;
            window.Closed += (_, _) => _state.Closed = true;
            window.Opened += (_, _) => _ = VerifyAsync(window, desktop);
            _ = WatchdogAsync(desktop);
        }
    }

    private static async Task VerifyAsync(MainWindow window, IClassicDesktopStyleApplicationLifetime desktop)
    {
        var page = window.FindControl<MainView>("Page")!;
        var vm = (MainViewModel)page.DataContext!;
        try
        {
            await UntilAsync(() => vm.IsDemoSelected && vm.HasVerifiedSnapshot, "Dedicated startup selects the local demo.");
            Check(vm.Devices.Count == 1 && vm.CanFitTest && vm.CanSetupHearing, "All demo actions are available without a real backend.");
            var hero = page.FindControl<ProductArtwork>("HeroArt")!;
            Check(vm.ProductId == "06C010" && DemoDeviceData.Color == 3 && hero.Visual is EncoFree4DynaudioModelVisual
                && page.FindControl<Grid>("HeroPanel")!.Children.Count == 1,
                "Demo startup selects the Dynaudio model without duplicate artwork captions.");
            await UntilAsync(() => vm.FeatureSections.Sum(section => section.Controls.Count) + vm.EqualizerFeatures.Count == 32
                && page.GetVisualDescendants().OfType<FeatureSectionView>().Count() == vm.FeatureSections.Count
                && page.GetVisualDescendants().OfType<FeatureSectionView>().All(section => section.Content is FASettingsExpander),
                "The demo's dynamic section containers materialize before inspecting or expanding their editors.");
            RecordControlLayout(page, vm, "collapsed");
            var anc = page.FindControl<FASettingsExpander>("AncPanel")!;
            Check(anc is { IsVisible: true, IsExpanded: true }
                && page.GetVisualDescendants().OfType<FASettingsExpander>()
                    .Where(expander => !ReferenceEquals(expander, anc)).All(expander => !expander.IsExpanded)
                && !page.FindControl<FASettingsExpander>("OperationLogPanel")!.IsExpanded,
                "ANC starts expanded while the demo's optional groups start collapsed.");
            // The following native checks inspect all editors; explicitly open them
            // after verifying the same collapsed defaults offered to real users.
            try
            {
                await UntilAsync(() => hero.GetVisualDescendants().OfType<EncoFree4DynaudioModelView>().Any(model => model.ModelReady
                    && model.Diagnostics.FrameId > 0 && model.Diagnostics.SourceTriangles > 0),
                    "The bundled Dynaudio scene renders on the first native window load.");
            }
            finally { CaptureStartupDiagnostics(window, hero); }
            var model = hero.GetVisualDescendants().OfType<DeviceModelView>().Single();
            _state.PreferredBackend = _state.RequestedBackend.ToString();
            _state.ActiveBackend = model.Diagnostics.Backend;
            Check(model.Diagnostics.RequestedBackend == _state.RequestedBackend
                && model.Diagnostics.ActualBackend == _state.RequestedBackend
                && ModelRenderingPreferences.CompatibilityMode == (_state.RequestedBackend == FilamentBackend.OpenGL),
                "The native demo uses exactly the requested backend without silently changing the preference.");
            Check(model.LoadError is null && model.AnimationActive, "Demo model is ready and its animation is active.");
            _state.FirstRenderedTriangles = (long)model.Diagnostics.SourceTriangles;
            _state.FirstRenderedSceneItems = model.Diagnostics.SourceMeshes;
            Check(model is EncoFree4DynaudioModelView && _state.FirstRenderedSceneItems == 20
                && model.Diagnostics.SourceTriangles == DynaudioModelTests.ExpectedTriangles
                && _state.FirstRenderedTriangles is > 0 and <= DynaudioModelTests.ExpectedTriangles && model.Diagnostics.State == FilamentRendererState.Ready,
                "The first native scene retains all 20 Dynaudio meshes and 94,927 source triangles without a renderer error.");
            await DynaudioModelTests.VerifyAsync();
            if (_state.ActivityRegression)
            {
                await CheckActivityRegressionAsync(window, page, model);
                Check(_state.Service.Enumerations == 1 && _state.Service.ForbiddenCalls == 0 && _state.CalibrationLaunches == 0,
                    "Long native demo activity never calls a headset/artwork backend or launches device/audio calibration.");
                window.Close();
                await UntilAsync(() => _state.Closed, "The activity regression closes its native window.");
                Check(!model.ModelReady && !model.AnimationActive && model.Viewport.Source is null,
                    "The activity regression unloads its native scene and stops frame work on close.");
                _state.Completed = true;
                return;
            }
            await CheckNativeModelInteractionAsync(window, model);
            await CheckFrameRateSwitchingAsync(model);
            await CheckBackendSwitchingAsync(model);
            Check(_state.Service.Enumerations == 1 && _state.Service.ForbiddenCalls == 0,
                "First-load Dynaudio model integrity and Opaque correction require no headset or artwork request.");
            page.ExpandAllSettings();
            try
            {
                await UntilAsync(() => page.GetVisualDescendants().OfType<FeatureControlView>().Count() == 32,
                    "Every dynamic demo control materializes after its section is explicitly expanded.");
                // Let the native expander animation finish before resizing and
                // inspecting geometry. This is intentionally separate from VM readiness.
                await Task.Delay(350);
            }
            finally { RecordControlLayout(page, vm, "expanded"); }

            foreach (bool dark in new[] { false, true })
            foreach (var size in new[] { new Size(1280, 900), new Size(900, 640) })
            {
                ((App)Application.Current!).ApplyTheme(dark ? "dark" : "light");
                window.Width = size.Width; window.Height = size.Height;
                await Task.Delay(150);
                Check(window.ActualThemeVariant == (dark ? ThemeVariant.Dark : ThemeVariant.Light), "Demo follows the window theme.");
                Check(page.IsCompact == (page.Bounds.Width < 1080), "Demo follows the responsive breakpoint.");
                DeviceBarTests.CheckBottomBar(page, page);
                var actualControlCount = page.GetVisualDescendants().OfType<FeatureControlView>().Count();
                RecordControlLayout(page, vm, $"{(dark ? "dark" : "light")}-{(int)size.Width}x{(int)size.Height}");
                Check(actualControlCount == 32,
                    $"Every dynamic demo control is loaded in the native window; actual={actualControlCount}, expected=32.");
                var featureSliderLayouts = DemoDeviceTests.CheckFeatureEditorLayout(page);
                Check(featureSliderLayouts.Count == 2, "The native demo's tick sliders and right-hand integers fit both window sizes.");
                _state.ControlLayouts.Add(new
                {
                    phase = $"{(dark ? "dark" : "light")}-{(int)size.Width}x{(int)size.Height}-slider-readouts",
                    sliders = featureSliderLayouts
                });
                if (_state.Output is not null)
                {
                    window.Activate();
                    await Task.Delay(100);
                    var handle = window.TryGetPlatformHandle();
                    Check(handle is { HandleDescriptor: "HWND" } && handle.Handle != IntPtr.Zero,
                        "Dynaudio model capture runs in an actual Win32 window.");
                    string name = $"demo-dynaudio-{(dark ? "dark" : "light")}-{(int)size.Width}x{(int)size.Height}-3d.png";
                    string path = Path.Combine(_state.Output, name);
                    await UntilAsync(() =>
                    {
                        var current = model.CaptureLatestFrame(out var information);
                        var expected = model.RenderingPolicy.GetPixelSize(model.Viewport.Bounds.Size, window.RenderScaling);
                        return current is not null
                            && information.Width == expected.Width && information.Height == expected.Height;
                    }, "A resize presents the native frame at the policy's DPI-aware backing size.");
                    var pixels = model.CaptureLatestFrame(out var frame) ?? throw new InvalidOperationException("No native model frame is available.");
                    var captureSize = new PixelSize(frame.Width, frame.Height);
                    var expectedSize = model.RenderingPolicy.GetPixelSize(model.Viewport.Bounds.Size, window.RenderScaling);
                    Check(captureSize == expectedSize,
                        "Presented pixels follow the production policy's DPI-aware model backing dimensions.");
                    byte background = dark ? (byte)32 : (byte)243;
                    int foreground = 0;
                    for (int pixel = 0; pixel < pixels.Length; pixel += 4)
                        if (Math.Abs(pixels[pixel] - background) > 12 || Math.Abs(pixels[pixel + 1] - background) > 12
                            || Math.Abs(pixels[pixel + 2] - background) > 12) foreground++;
                    var pinned = GCHandle.Alloc(pixels, GCHandleType.Pinned);
                    try
                    {
                        using var capture = new Bitmap(PixelFormat.Rgba8888, AlphaFormat.Opaque, pinned.AddrOfPinnedObject(), captureSize,
                            new Vector(96, 96), frame.RowBytes);
                        using var stream = new FileStream(path, FileMode.CreateNew);
                        capture.Save(stream, Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
                    }
                    finally { pinned.Free(); }
                    Check(pixels.Length == captureSize.Width * captureSize.Height * 4 && foreground > 1000,
                        $"Native renderer capture contains Dynaudio geometry; foreground={foreground}, pixels={pixels.Length}, size={captureSize}, "
                        + $"frame={frame.FrameId}, yaw={model.Motion.Yaw}, pitch={model.Motion.Pitch}, saved={path}.");
                    _state.Screenshots.Add(new { theme = dark ? "dark" : "light", requestedWidth = size.Width, requestedHeight = size.Height,
                        width = captureSize.Width, height = captureSize.Height, foregroundPixels = foreground, file = name,
                        renderingPolicy = model.RenderingPolicy,
                        model.FrameProbeCount, model.FrameProbeReadyCount, model.FrameProbeNoFrameCount,
                        model.BitmapLockCount, model.DirectFrameCopyCount, model.FallbackFrameCopyCount,
                        source = "native Filament model frame; model area only",
                        sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant(),
                        model.Diagnostics.SourceTriangles, model.Diagnostics.SourceMeshes, model.Diagnostics.FrameId,
                        preferredBackend = "GPU", backend = model.Diagnostics.Backend });
                }
            }

            Check(model.RenderingPolicyOverride is null && model.RenderingPolicy == ModelRenderPolicy.PlatformDefault,
                "The native demo uses the production model rendering policy without an audit override.");
            Check(!model.RenderingPolicy.ProbeBeforeBitmapLock || model.FrameProbeReadyCount > 0,
                "The production metadata probe observes completed frames before direct bitmap copies.");
            _state.ModelRendering = new
            {
                renderingPolicy = model.RenderingPolicy, usesProductionDefault = model.RenderingPolicyOverride is null,
                viewportWidth = model.Viewport.Bounds.Width, viewportHeight = model.Viewport.Bounds.Height, window.RenderScaling,
                lastPresentedFrame = model.LastPresentedFrame,
                model.FrameProbeCount, model.FrameProbeReadyCount, model.FrameProbeNoFrameCount,
                model.BitmapLockCount, model.DirectFrameCopyCount, model.FallbackFrameCopyCount
            };
            page.RequestFitTest = () => { _state.CalibrationLaunches++; return Task.CompletedTask; };
            page.RequestHearingSetup = () => { _state.CalibrationLaunches++; return Task.CompletedTask; };
            page.FindControl<FASettingsExpander>("HearingPanel")!.IsExpanded = true;
            foreach (string label in new[] { "开始测试", "设置黄金听感" })
            {
                var button = page.GetVisualDescendants().OfType<Button>().Single(candidate => Equals(candidate.Content, label));
                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            }
            Check(_state.CalibrationLaunches == 0 && !vm.StatusWarning, "Demo test buttons give local feedback without launching calibration.");
            await vm.AddDemoDeviceAsync();
            Check(vm.Devices.Count == 1, "Adding the demo again retains a single card.");
            await vm.ToggleGameCommand.ExecuteAsync(true);
            Check(vm.GameMode == true, "Demo controls change local state.");
            Check(_state.Service.Enumerations == 1 && _state.Service.ForbiddenCalls == 0,
                "Themes, responsive layout, repeated demo selection and local interactions never request a backend.");
            vm.EqName = "临时演示草稿";
            Check(vm.EqDirty, "The demo EQ editor accepts a local draft.");
            window.Close();
            await UntilAsync(() => _state.Closed, "A dirty demo closes without a confirmation dialog.");
            Check(!model.ModelReady && !model.AnimationActive && model.Viewport.Source is null,
                "Closing the real window unloads model pixels and stops all view frame work.");
            Check(!vm.IsDemoSelected && !vm.Devices.Any(MainViewModel.IsDemoDevice), "Closing removes the demo from the disposed view model.");
            await using var fresh = new MainViewModel(new UnsupportedPlatformService(), new RejectConfirmation());
            await fresh.InitializeAsync();
            Check(!fresh.HasDevices && !fresh.IsDemoSelected, "A new UI session starts with no demo.");
            _state.Completed = true;
        }
        catch (Exception error) { _state.Failure = error.ToString(); }
        finally
        {
            if (!_state.Closed) { await vm.DisposeAsync(); window.Close(); }
            desktop.Shutdown(_state.Failure is null ? 0 : 1);
        }
    }

    private static async Task CheckNativeModelInteractionAsync(Window window, DeviceModelView model)
    {
        await UntilAsync(() => model.CaptureLatestFrame(out _) is not null && model.Viewport.Source is not null,
            "Avalonia presents the first real native bitmap before testing pointer input.");
        window.Activate();
        await Task.Delay(100);
        var handle = window.TryGetPlatformHandle() ?? throw new InvalidOperationException("Missing native window handle.");
        var screen = model.Viewport.PointToScreen(new Point(model.Viewport.Bounds.Width * .5, model.Viewport.Bounds.Height * .5));
        var point = NativeWindowCapture.ClientPoint(handle.Handle, screen);
        var logical = model.Viewport.TranslatePoint(new Point(model.Viewport.Bounds.Width * .5, model.Viewport.Bounds.Height * .5), window)!.Value;
        int presses = 0;
        bool leftPressed = false;
        model.Viewport.PointerPressed += (_, e) => { presses++; leftPressed = e.GetCurrentPoint(model.Viewport).Properties.IsLeftButtonPressed; };
        double yaw = model.Motion.Yaw, pitch = model.Motion.Pitch;
        var count = model.CameraUpdateCount;
        NativeWindowCapture.PointerMessage(handle.Handle, 0x0200, point.X, point.Y, false);
        await Task.Delay(30);
        NativeWindowCapture.PointerMessage(handle.Handle, 0x0201, point.X, point.Y, true);
        await Task.Delay(50);
        Check(model.Motion.IsDragging, $"A real Win32 pointer press captures the bitmap-composed model; viewport={model.Viewport.Bounds}, "
            + $"client={point}, logical={logical}, hit={window.InputHitTest(logical)?.GetType().Name}, active={model.Motion.IsActive}, presses={presses}, left={leftPressed}.");
        for (int step = 1; step <= 5; step++)
        {
            NativeWindowCapture.PointerMessage(handle.Handle, 0x0200, point.X + step * 12, point.Y + step * 4, true);
            await Task.Delay(20);
        }
        NativeWindowCapture.PointerMessage(handle.Handle, 0x0202, point.X + 60, point.Y + 20, false);
        await Task.Delay(40);
        Check(!model.Motion.IsDragging && model.Motion.Yaw - yaw < -.05 && model.Motion.Pitch - pitch > .02
            && model.CameraUpdateCount > count, "Native right/down drag keeps camera direction and updates the real renderer.");
        model.IsVisible = false;
        await Task.Delay(30);
        Check(!model.AnimationActive && !model.Motion.IsActive, "Hiding the real model stops its loop and gesture state.");
        count = model.CameraUpdateCount;
        await Task.Delay(100);
        Check(model.CameraUpdateCount == count, "A hidden model does not request camera updates.");
        model.IsVisible = true;
        await UntilAsync(() => model.AnimationActive, "The real model resumes after being shown.");
    }

    private static void RecordControlLayout(MainView page, MainViewModel vm, string phase)
    {
        var sections = page.GetVisualDescendants().OfType<FeatureSectionView>().ToArray();
        var controls = page.GetVisualDescendants().OfType<FeatureControlView>().Count();
        _state.ControlLayouts.Add(new
        {
            phase, expectedSections = vm.FeatureSections.Count, actualSections = sections.Length,
            expectedControls = vm.FeatureSections.Sum(section => section.Controls.Count) + vm.EqualizerFeatures.Count, actualControls = controls,
            sections = sections.Select(section => new
            {
                id = section.Section?.Id,
                expectedControls = section.Section?.Controls.Count,
                expanded = (section.Content as FASettingsExpander)?.IsExpanded,
                actualControls = section.GetVisualDescendants().OfType<FeatureControlView>().Count(),
                width = section.Bounds.Width, height = section.Bounds.Height
            }).ToArray()
        });
        Console.WriteLine($"LAYOUT {phase}: sections={sections.Length}/{vm.FeatureSections.Count}, controls={controls}/{vm.FeatureSections.Sum(section => section.Controls.Count) + vm.EqualizerFeatures.Count}");
    }

    private static async Task UntilAsync(Func<bool> ready, string message)
    {
        for (int attempt = 0; !ready() && attempt < 200; attempt++) await Task.Delay(25);
        Check(ready(), message);
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
        _state.Passed++;
        Console.WriteLine("PASS " + message);
    }

    private static async Task WatchdogAsync(IClassicDesktopStyleApplicationLifetime desktop)
    {
        await Task.Delay(_state.ActivityRegression ? TimeSpan.FromMinutes(7) : TimeSpan.FromSeconds(55));
        if (_state.Completed) return;
        _state.Failure ??= "Native demo watchdog expired.";
        desktop.Shutdown(1);
    }

    // A render target cannot include the system material behind transparent UI.
    // Capture the displayed compositor surface, as the DeviceManager smoke does.
    private static PixelSize CaptureCompositor(IntPtr hwnd, string output)
    {
        if (!GetWindowRect(hwnd, out var rect)) throw new InvalidOperationException("Cannot read native window bounds.");
        var width = rect.Right - rect.Left; var height = rect.Bottom - rect.Top;
        var screen = GetDC(IntPtr.Zero);
        var memory = IntPtr.Zero; var dib = IntPtr.Zero; var previous = IntPtr.Zero;
        try
        {
            if (screen == IntPtr.Zero) throw new InvalidOperationException("Cannot acquire the compositor surface.");
            memory = CreateCompatibleDC(screen);
            var info = new BitmapInfo
            {
                Header = new BitmapInfoHeader { Size = 40, Width = width, Height = -height, Planes = 1, BitCount = 32 }
            };
            dib = CreateDIBSection(screen, ref info, 0, out var pixels, IntPtr.Zero, 0);
            if (memory == IntPtr.Zero || dib == IntPtr.Zero || pixels == IntPtr.Zero)
                throw new InvalidOperationException("Cannot allocate native screenshot surface.");
            previous = SelectObject(memory, dib);
            if (!BitBlt(memory, 0, 0, width, height, screen, rect.Left, rect.Top, 0x00CC0020))
                throw new InvalidOperationException("Cannot capture compositor output.");
            using var bitmap = new Bitmap(PixelFormat.Bgra8888, AlphaFormat.Opaque, pixels,
                new PixelSize(width, height), new Vector(96, 96), width * 4);
            bitmap.Save(output, PngBitmapEncoderOptions.Default);
            return new PixelSize(width, height);
        }
        finally
        {
            if (previous != IntPtr.Zero) SelectObject(memory, previous);
            if (dib != IntPtr.Zero) DeleteObject(dib);
            if (memory != IntPtr.Zero) DeleteDC(memory);
            if (screen != IntPtr.Zero) ReleaseDC(IntPtr.Zero, screen);
        }
    }

    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct BitmapInfoHeader
    {
        public uint Size; public int Width, Height; public ushort Planes, BitCount;
        public uint Compression, SizeImage; public int XPelsPerMeter, YPelsPerMeter; public uint ColorsUsed, ColorsImportant;
    }
    [StructLayout(LayoutKind.Sequential)] private struct BitmapInfo { public BitmapInfoHeader Header; public uint Colors; }
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateDIBSection(IntPtr dc, ref BitmapInfo info, uint usage, out IntPtr bits, IntPtr section, uint offset);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool BitBlt(IntPtr dc, int x, int y, int width, int height, IntPtr source, int sourceX, int sourceY, uint operation);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeleteDC(IntPtr dc);

    private sealed class RejectConfirmation : IConfirmationService
    {
        public Task<bool> ConfirmAsync(string title, string message, CancellationToken token) => Task.FromResult(false);
        public Task<bool> ConfirmRetryAsync(string title, string message, CancellationToken token) => Task.FromResult(false);
    }

    private sealed class DemoService : IHeadsetService, IArtworkService
    {
        public int Enumerations { get; private set; }
        public int ForbiddenCalls { get; private set; }
        public Task<IReadOnlyList<UiDevice>> EnumerateAsync(CancellationToken token)
        { Enumerations++; return Task.FromResult<IReadOnlyList<UiDevice>>([]); }
        public Task<UiSnapshot> ConnectAsync(string identity, CancellationToken token) => throw Unexpected();
        public Task DisconnectAsync(CancellationToken token) => throw Unexpected();
        public Task<UiSnapshot> RefreshAsync(CancellationToken token) => throw Unexpected();
        public Task<UiApplyResult> ApplyAsync(UiAction action, CancellationToken token) => throw Unexpected();
        public Task<UiArtwork?> AcquireArtworkAsync(string productId, int? color, string role, CancellationToken token) => throw Unexpected();
        public Task ReleaseArtworkAsync(string leaseId, CancellationToken token) => throw Unexpected();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        private Exception Unexpected()
        { ForbiddenCalls++; return new InvalidOperationException("Native demo must not contact headset or artwork services."); }
    }
}
