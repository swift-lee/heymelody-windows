using System.Security.Cryptography;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using FluentAvalonia.Styling;
using HeyMelody.Rendering.Filament;
using HeyMelodyWindows.App;

if (ProcessLifetimeTests.TryRunHelper(args, out var helperExit)) return helperExit;
if (args is ["--diagnostic-settings-tests"]) return DiagnosticSettingsTests.Run();

if (args is ["--smooth-scroll-tests"]) return SmoothScrollingTests.Run();

if (args is ["--rendering-policy-tests"]) return RenderingPolicyTests.Run();
if (args is ["--native-rendering-smoke"]) return NativeSmoke.Run(useRenderingSetup: true);
if (args is ["--native-rendering-policy-smoke", var renderMode, "--output-dir", var renderOutput]
    && renderMode is "default" or "basic" or "software-fallback" or "warp-missing")
    return NativeSmoke.Run(useRenderingSetup: true, basicRenderMode: renderMode != "default",
        outputDirectory: Path.GetFullPath(renderOutput),
        renderingFailure: renderMode is "software-fallback" or "warp-missing" ? renderMode : null);
if (args.FirstOrDefault() == "--native-rendering-policy-smoke")
{ Console.Error.WriteLine("Usage: --native-rendering-policy-smoke <default|basic|software-fallback|warp-missing> --output-dir path"); return 2; }

if (args is ["--native-smoke"]) return NativeSmoke.Run();
if (args is ["--native-settings-smoke"]) return NativeSmoke.Run(openSettings: true);
if (args is ["--native-model-settings-smoke"]) return NativeModelSettingsSmoke.Run();
if (args is ["--native-model-settings-smoke", "--output-dir", var nativeSettingsOutput])
    return NativeModelSettingsSmoke.Run(Path.GetFullPath(nativeSettingsOutput));
if (args.FirstOrDefault() == "--native-model-settings-smoke")
{ Console.Error.WriteLine("Usage: --native-model-settings-smoke [--output-dir path]"); return 2; }
if (args is ["--native-startup-smoke"]) return NativeStartupSmoke.Run();
if (args is ["--native-backend-smoke"]) return NativeBackendSmoke.Run();
if (args.FirstOrDefault() == "--native-model-loading-benchmark") return ModelLoadingBenchmark.Run(args);
if (args.FirstOrDefault() == "--native-demo-smoke")
{
    FilamentBackend backend = FilamentBackend.Vulkan;
    string? nativeDemoOutput = null;
    for (int index = 1; index < args.Length; index++)
    {
        if (args[index] == "--backend" && ++index < args.Length && args[index] is "vulkan" or "opengl")
            backend = args[index] == "opengl" ? FilamentBackend.OpenGL : FilamentBackend.Vulkan;
        else if (args[index] == "--output-dir" && ++index < args.Length) nativeDemoOutput = Path.GetFullPath(args[index]);
        else { Console.Error.WriteLine("Usage: --native-demo-smoke [--backend vulkan|opengl] [--output-dir path]"); return 2; }
    }
    return NativeDemoSmoke.Run(nativeDemoOutput, backend);
}

RenderingPolicyTests.Run();

string output = args.Length == 2 && args[0] == "--output-dir" ? Path.GetFullPath(args[1])
    : args is ["--model-tests", "--output-dir", var modelOutput] ? Path.GetFullPath(modelOutput)
    : args is ["--model-loading-tests", "--output-dir", var modelLoadingOutput] ? Path.GetFullPath(modelLoadingOutput)
    : args is ["--visual-catalog-tests", "--output-dir", var visualCatalogOutput] ? Path.GetFullPath(visualCatalogOutput)
    : args is ["--artwork-persistence-tests", "--output-dir", var artworkPersistenceOutput] ? Path.GetFullPath(artworkPersistenceOutput)
    : args is ["--anc-feature-tests", "--output-dir", var ancOutput] ? Path.GetFullPath(ancOutput)
    : args is ["--rendering-settings-tests", "--output-dir", var renderSettingsOutput] ? Path.GetFullPath(renderSettingsOutput)
    : args is ["--device-bar-tests", "--output-dir", var deviceBarOutput] ? Path.GetFullPath(deviceBarOutput)
    : Path.Combine(Directory.GetCurrentDirectory(), "artifacts", "avalonia-ui-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..8]);
Directory.CreateDirectory(output);
var receipts = new List<object>();
int passed = 0;
// Portable UI checks exercise composition with an explicit fake, never by loading a host native DLL.
DeviceModelView.DefaultFactoryOverride = new RecordingFilamentRendererFactory();
// Keep the runner outside the UI dispatch thread; synchronous disposal must never wait on itself.
using var session = HeadlessUnitTestSession.StartNew(typeof(PreviewBootstrap));
try
{
    if (args is ["--device-bar-tests"] or ["--device-bar-tests", "--output-dir", _])
    {
        var count = DeviceBarTests.Run(session);
        Console.WriteLine(JsonSerializer.Serialize(new { passed = count, success = true, nativeBluetoothCalls = 0, screenshots = 0, output }));
        return 0;
    }
    if (args is ["--artwork-persistence-tests"] or ["--artwork-persistence-tests", "--output-dir", _])
    {
        var count = ArtworkPersistenceTests.Run(session, output, receipts);
        Console.WriteLine(JsonSerializer.Serialize(new { passed = count, success = true, nativeBluetoothCalls = 0, output }));
        return 0;
    }
    if (args is ["--model-loading-tests"] or ["--model-loading-tests", "--output-dir", _])
    {
        int count = FrameLoopTests.Run(session, output);
        count += VerifiedModelCacheTests.RunAsync(output).GetAwaiter().GetResult();
        session.Dispatch<bool>(async () =>
        {
            await ModelRendererLifecycleTests.RunAsync(output);
            await DynaudioModelTests.VerifyAsync();
            return true;
        }, CancellationToken.None).GetAwaiter().GetResult();
        Console.WriteLine(JsonSerializer.Serialize(new { passed = count + 2, success = true, nativeBluetoothCalls = 0, output }));
        return 0;
    }
    if (args is ["--visual-catalog-tests"] or ["--visual-catalog-tests", "--output-dir", _])
    {
        int count = DeviceVisualTests.RunCatalog(session);
        Console.WriteLine(JsonSerializer.Serialize(new { passed = count, success = true, nativeBluetoothCalls = 0, output }));
        return 0;
    }
    if (args is ["--rendering-settings-tests"] or ["--rendering-settings-tests", "--output-dir", _])
    {
        var checks = RenderingSettingsTests.Run(session, output);
        Console.WriteLine(JsonSerializer.Serialize(new { passed = checks, success = true, nativeBluetoothCalls = 0, output }));
        return 0;
    }
    if (args is ["--model-tests"] or ["--model-tests", "--output-dir", _])
    {
        int count = CompatibilityRenderingTests.Run(session, output) + FrameLoopTests.Run(session, output);
        session.Dispatch<bool>(async () => { await DynaudioModelTests.VerifyAsync(); return true; }, CancellationToken.None).GetAwaiter().GetResult();
        Console.WriteLine(JsonSerializer.Serialize(new { passed = count + 1, success = true, nativeBluetoothCalls = 0, output }));
        return 0;
    }
    if (args is ["--anc-feature-tests"] or ["--anc-feature-tests", "--output-dir", _])
    {
        var checks = AncFeatureTests.Run(session, output);
        Console.WriteLine(JsonSerializer.Serialize(new { passed = checks, success = true, nativeBluetoothCalls = 0, output }));
        return 0;
    }
    var propagated = false;
    try { session.Dispatch<bool>(() => Task.FromException<bool>(new InvalidOperationException("expected-dispatch-failure")), CancellationToken.None).GetAwaiter().GetResult(); }
    catch (InvalidOperationException error) when (error.Message == "expected-dispatch-failure") { propagated = true; }
    Check(propagated, "Headless dispatcher propagates asynchronous assertion failures");
    foreach (var scenario in new[] { "connected", "empty", "unsupported", "partial", "disconnected" })
    foreach (var dark in new[] { false, true })
    foreach (var size in new[] { new Size(1280, 900), new Size(900, 640) })
    {
        session.Dispatch<bool>(async () =>
        {
            Application.Current!.RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light;
            Application.Current.Styles.OfType<FluentAvaloniaTheme>().Single().CustomAccentColor = Color.FromRgb(0, 120, 212);
            await using var vm = new MainViewModel(new ScenarioPreviewService(scenario), new NoConfirm(), preview: true);
            await vm.InitializeAsync();
            var view = new MainView { DataContext = vm };
            var window = new Window { Width = size.Width, Height = size.Height, Content = view, WindowDecorations = WindowDecorations.None };
            try
            {
                window.Show(); Dispatcher.UIThread.RunJobs();
                Check(view.IsCompact == (size.Width < 1080), "Responsive breakpoint");
                var picker = view.FindControl<ListBox>("DevicePicker")!;
                Check(picker.IsVisible && picker.Bounds.Height > 0, "Device picker stays visible");
                var pickerOrigin = picker.TranslatePoint(default, window)!.Value;
                Check(pickerOrigin.Y >= 0 && pickerOrigin.Y + picker.Bounds.Height <= size.Height, "Device picker is within viewport");
                DeviceBarTests.CheckBottomBar(view, window);
                var anc = FeatureTestData.Anc(view);
                Check(anc.IsEnabled == (scenario is "connected" or "partial"), "Only verified models enable controls");
                Check(view.FindControl<Avalonia.Controls.Shapes.Ellipse>("ConnectionDot")!.Classes.Contains("online") == vm.HasVerifiedSnapshot,
                    "The header dot is green only for verified control");
                DeviceVisualTests.CheckScenario(view, scenario);
                if (scenario == "partial")
                {
                    Check(vm.CaseBatteryText == "—" && vm.HiResStateText == "未确认", "Unknown fields stay unknown");
                    Check(!vm.CanSetHiRes, "Unknown HiRes disables setter");
                }
                if (scenario == "empty") Check(view.FindControl<TextBlock>("EmptyDevicesText")!.IsVisible, "Empty state is visible");
                using var frame = window.CaptureRenderedFrame() ?? throw new Exception("Missing Skia pixels");
                Check(frame.PixelSize == new PixelSize((int)size.Width, (int)size.Height), "Correct image dimensions");
                var name = $"{scenario}-{(dark ? "dark" : "light")}-{(int)size.Width}x{(int)size.Height}.png";
                var path = Path.Combine(output, name);
                using (var stream = new FileStream(path, FileMode.CreateNew)) frame.Save(stream, Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
                receipts.Add(new { scenario, theme = dark ? "dark" : "light", width = size.Width, height = size.Height, file = name, sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant() });
                passed++; Console.WriteLine($"PASS {name}");
            }
            finally { window.Close(); }
            return true;
        }, CancellationToken.None).GetAwaiter().GetResult();
    }

    session.Dispatch<bool>(async () =>
    {
        var service = new InputTestService();
        await using var vm = new MainViewModel(service, new NoConfirm());
        await vm.InitializeAsync();
        await vm.ConnectCommand.ExecuteAsync(null);
        var view = new MainView { DataContext = vm };
        var window = new Window { Width = 1280, Height = 900, Content = view, WindowDecorations = WindowDecorations.None };
        try
        {
            window.Show(); Dispatcher.UIThread.RunJobs();
            Check(service.Submissions == 0, "Initial bindings never SET");
            Check(vm.SelectedAnc?.Key == 0x0104 && vm.SelectedPreset?.Key == 0 && vm.SelectedSpatial?.Key == 0, "Initial dropdown bindings preserve protocol IDs");
            var anc = FeatureTestData.Anc(view);
            anc.SelectedItem = vm.SelectedAncMode!.Options.Last();
            Dispatcher.UIThread.RunJobs();
            Check(service.Submissions == 0, "Programmatic dropdown changes never SET");
            var apply = view.GetVisualDescendants().OfType<RadioButton>().Single(button => Avalonia.Automation.AutomationProperties.GetName(button) == vm.AncModes.Single(mode => mode.Key == 0).Name);
            var point = apply.TranslatePoint(new Point(apply.Bounds.Width / 2, apply.Bounds.Height / 2), window)!.Value;
            window.MouseDown(point, MouseButton.Left); window.MouseUp(point, MouseButton.Left);
            Dispatcher.UIThread.RunJobs();
            Check(service.Submissions == 1 && service.LastAction is { Kind: "AncMode", Value: 0 }, "ANC radio immediately submits its parent exactly once");
            Check(vm.LastApplyAttempts is [{ BackendReceiptReceived: true, ConnectionState: ConnectionState.Connected,
                Accepted: true, AckStatus: 0, TargetSubmitted: true, ReadbackMatches: true }]
                && !vm.StatusWarning && vm.Status == "设置已确认，回读与请求一致。",
                "Bound Apply reaches confirmed success rather than incomplete-receipt recovery");
            await vm.RefreshStateCommand.ExecuteAsync(null);
            Dispatcher.UIThread.RunJobs();
            Check(service.Submissions == 1, "Refreshed bindings never SET");
            await MainUiTestLayout.ExpandSettingsAsync(view);
            var toggle = FeatureTestData.Switch(view, "dual");
            point = await MainUiTestLayout.ClickPointAsync(window, toggle, x: 16);
            window.MouseDown(point, MouseButton.Left); window.MouseUp(point, MouseButton.Left);
            Dispatcher.UIThread.RunJobs();
            Check(service.Submissions == 2 && service.LastAction is { Kind: "Feature", FeatureId: "dual", Value: 0 }, $"Generic toggle click submits desired value once: {service.Submissions} {service.LastAction}");
            view.ExpandAllSettings(); Dispatcher.UIThread.RunJobs();
            Check(view.FindControl<ItemsControl>("BandEditor")!.Items.Count == 10, "EQ binds ten device bands");
            passed++; Console.WriteLine("PASS bound controls stage, submit once, and preserve GET-only updates");
        }
        finally { window.Close(); }
        return true;
    }, CancellationToken.None).GetAwaiter().GetResult();
    passed += InteractionTests.Run(session);
    var ancFeatureChecks = AncFeatureTests.Run(session, output);
    passed += ancFeatureChecks;
    var gameAudioChecks = GameAudioInteractionTests.Run(session);
    passed += gameAudioChecks;
    var selectionChecks = SelectionRefreshTests.Run(session, output, receipts);
    passed += selectionChecks;
    passed += DeviceBarTests.Run(session);
    passed += DeviceVisualTests.Run(session);
    passed += ArtworkPersistenceTests.Run(session, output, receipts);
    var renderingChecks = CompatibilityRenderingTests.Run(session, output);
    passed += renderingChecks;
    var animationChecks = AnimatedWebpTests.Run(session, output, receipts);
    passed += animationChecks;
    var demoChecks = DemoDeviceTests.Run(session, output, receipts);
    passed += demoChecks;
    var layoutChecks = MainLayoutTests.Run(session, output, receipts);
    passed += layoutChecks;
    var startupChecks = StartupTests.Run(session);
    var settingsManagementChecks = SettingsManagementTests.Run(session);
    passed += RenderingSettingsTests.Run(session, output);
    passed += startupChecks + settingsManagementChecks;
    var typographyChecks = TypographyTests.Run(session);
    passed += typographyChecks;
    var frameLoopChecks = FrameLoopTests.Run(session, output);
    passed += frameLoopChecks;
    var gestureChecks = DeviceGestureTests.Run(session);
    passed += gestureChecks;
    Check(passed == (OperatingSystem.IsWindows() ? 66 : 63) + demoChecks + renderingChecks + animationChecks + layoutChecks + selectionChecks
        + gameAudioChecks + typographyChecks + frameLoopChecks + gestureChecks + ancFeatureChecks, "Every UI scenario and interaction case must finish");
    var report = new { schemaVersion = 1, version = AppVersion.Display, passed, success = true, nativeBluetoothCalls = 0,
        verificationScope = "source/mock checks; not a packaged executable or hardware execution receipt",
        screenshotScope = "headless Skia layout/content only; native titlebar and real 3D renderer pixels are not verified", screenshots = receipts };
    using (var stream = new FileStream(Path.Combine(output, "validation.json"), FileMode.CreateNew)) JsonSerializer.Serialize(stream, report, new JsonSerializerOptions { WriteIndented = true });
    Console.WriteLine(JsonSerializer.Serialize(new { passed, success = true, nativeBluetoothCalls = 0, output }));
    return 0;
}
catch (Exception error) { Console.Error.WriteLine(error); return 1; }

static void Check(bool condition, string reason) { if (!condition) throw new Exception(reason); }

sealed class NoConfirm : IConfirmationService
{
    public Task<bool> ConfirmAsync(string title, string message, CancellationToken token) => Task.FromResult(false);
}

sealed class InputTestService : IHeadsetService
{
    private readonly PreviewService _inner = new();
    private UiSnapshot? _snapshot;
    public int Submissions { get; private set; }
    public UiAction? LastAction { get; private set; }
    public Task<IReadOnlyList<UiDevice>> EnumerateAsync(CancellationToken token) => _inner.EnumerateAsync(token);
    public async Task<UiSnapshot> ConnectAsync(string identity, CancellationToken token) => _snapshot = FeatureTestData.Decorate(await _inner.ConnectAsync(identity, token));
    public Task DisconnectAsync(CancellationToken token) => Task.CompletedTask;
    public Task<UiSnapshot> RefreshAsync(CancellationToken token) => Task.FromResult(_snapshot!);
    public Task<UiApplyResult> ApplyAsync(UiAction action, CancellationToken token)
    {
        LastAction = action; Submissions++;
        if (action.Kind == "Anc") { _snapshot!.Anc = action.Value; _snapshot.AncPrimary = action.Value >> 8; }
        else if (action.Kind == "AncMode") { _snapshot!.AncPrimary = action.Value; _snapshot.Anc = null; }
        else if (action.Kind == "Feature" && action.FeatureId == "dual") _snapshot!.DualConnection = action.Value == 1;
        FeatureTestData.Decorate(_snapshot!);
        _snapshot!.ConnectionState = ConnectionState.Connected;
        return Task.FromResult(new UiApplyResult
        {
            BackendReceiptReceived = true, ConnectionState = ConnectionState.Connected,
            FailureKind = ApplyFailure.None, Accepted = true, AckStatus = 0,
            SubmissionAttempted = true, TargetSubmitted = true, ReadbackMatches = true, After = _snapshot
        });
    }
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
