using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Input;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using FluentAvalonia.Styling;
using FluentAvalonia.UI.Windowing;
using HeyMelodyWindows.App;
using HeyMelodyWindows.Calibration.UI;
using HeyMelodyWindows.Ui;

internal static class NativeCalibrationSmoke
{
    private static string _output = "";
    private static string? _failure;
    private static bool _completed;
    private static int _checks;
    private static CancellationTokenSource _loop = null!;
    private static bool? _basicRenderMode;
    private static NativeRenderingProbe? _probe;
    private static readonly List<object> Screenshots = [];

    public static int Run(bool? basicRenderMode = null, string? outputDirectory = null)
    {
        if (!OperatingSystem.IsWindows()) return 2;
        _basicRenderMode = basicRenderMode;
        _output = Path.GetFullPath(outputDirectory ?? Path.Combine("artifacts", "calibration-native-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss")));
        Directory.CreateDirectory(_output);
        _loop = new CancellationTokenSource();
        var thread = new Thread(() =>
        {
            try
            {
                var builder = AppBuilder.Configure<NativeFixtureApplication>().UsePlatformDetect().UseHeyMelodyTypography();
                if (basicRenderMode is not null)
                    builder = HeyMelodyWindows.Ui.RenderingSetup.UseHeyMelodyRendering(builder, basicRenderMode.Value);
                builder.SetupWithoutStarting();
                Dispatcher.UIThread.MainLoop(_loop.Token);
            }
            catch (Exception error) { _failure = error.ToString(); }
        }) { IsBackground = true, Name = "Calibration native fixture" };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(basicRenderMode is null ? 30 : 50))) _failure ??= "Native fixture did not finish.";
        var result = new { success = _completed && _failure is null, passed = _checks, nativeBluetoothCalls = 0,
            requestedBasicRenderMode = _basicRenderMode, rendering = _probe?.Receipt, screenshots = Screenshots,
            scope = "Win32 wizard windows with fixture responses and routed keyboard input; no device or acoustic test", failure = _failure };
        File.WriteAllText(Path.Combine(_output, "receipt.json"), JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine(JsonSerializer.Serialize(result)); Console.WriteLine(_output);
        return result.success ? 0 : 1;
    }

    public sealed class NativeFixtureApplication : Application
    {
        public override void Initialize()
        {
            RequestedThemeVariant = ThemeVariant.Light; Styles.Add(new FluentAvaloniaTheme { PreferSystemTheme = false });
            Styles.Add(SharedTheme.Create());
            Typography.ApplyThemeResources(this);
        }
        public override void OnFrameworkInitializationCompleted()
        {
            var service = new TestService { BlockFirstLevel = true };
            service.State = service.State with { HearingVersion = "V2", Stage = "AwaitingAnswer", ToneAcknowledged = true, FrequencyHz = 500 };
            var model = new CalibrationViewModel(service, "operation", "hearing");
            var view = new CalibrationView(model);
            var window = CreateWindow(view, "黄金听感 fixture");
            if (_basicRenderMode is not null) _probe = NativeRenderingProbe.Attach(window);
            window.Opened += async (_, _) =>
            {
                try
                {
                    await model.InitializeAsync(); await Task.Delay(200);
                    if (_probe is { } probe)
                    {
                        for (int attempt = 0; !probe.Queried && attempt < 80; attempt++) await Task.Delay(50);
                        Check(probe.Queried && probe.HasGpuContext && !string.IsNullOrWhiteSpace(probe.Renderer),
                            "The calibration compositor exposes its actual ANGLE renderer.");
                        Check(_basicRenderMode == probe.Renderer!.Contains("Microsoft Basic Render Driver", StringComparison.OrdinalIgnoreCase),
                            "The calibration renderer matches the requested GPU policy.");
                    }
                    var handle = window.TryGetPlatformHandle();
                    Check(handle is not null && handle.Handle != IntPtr.Zero && view.Bounds.Width > 0, "A real Win32 wizard window is laid out.");
                    var slider = view.GetVisualDescendants().OfType<Slider>().Single();
                    Check(slider.Value == 5 && model.State.Decibels == -19, "Factory middle level is the initial native slider value.");
                    slider.Focus(); PressKey(slider, Key.Right);
                    await service.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
                    // The service can signal before CalibrationView's coalesced Dispatcher.Post
                    // applies CanContinue to the button. Keep the ACK blocked while awaiting it.
                    await Until(() => model.LevelPending && !Button(view, "下一步").IsEnabled);
                    Check(!Button(view, "下一步").IsEnabled, "Native Next is disabled while the tone ACK is pending.");
                    service.ReleaseLevel.TrySetResult(); await Until(() => !model.LevelPending);
                    Check(service.Inputs.Last() is { Action: "level", LevelIndex: 6 } && model.State.Decibels == -11,
                        "Native slider keyboard input sends the higher level.");
                    PressKey(slider, Key.Left); await Until(() => !model.LevelPending && service.Inputs.Count == 2);
                    Check(service.Inputs.Last().LevelIndex == 5 && model.State.Decibels == -19,
                        "Native slider keyboard input also sends the lower level.");
                    Push(service.State with { StepIndex = 1, FrequencyType = 2, FrequencyHz = 1000, LevelIndex = 5, Decibels = -19 });
                    Check(view.GetVisualDescendants().OfType<Slider>().Single().Value == 5, "A native step change resets the middle level.");
                    await CaptureAsync(view, "hearing-slider");
                    Push(service.State with { Stage = "Results", StepIndex = 12, FiltersReady = false });
                    Check(!view.GetVisualDescendants().OfType<TextBox>().Any(), "Incomplete native results do not expose Save.");
                    var resultRadar = new UiHearingRadar(new double[] { .85, .62, .66, .58, .7, .82 }, new double[] { .83, .6, .64, .58, .67, .55 });
                    Push(service.State with { FiltersReady = true, CanPreview = true, HasInterEarDifference = true, HearingRadar = resultRadar,
                        HearingEnhanceType = 0, RestoreDescriptionId = 1 });
                    Check(Preview(view, "PreviewOriginal").IsEnabled && Preview(view, "PreviewEnhanced").IsEnabled && Button(view, "保存并启用").IsEnabled,
                        "Complete native results expose effect comparison and Save.");
                    var chart = view.GetVisualDescendants().OfType<HearingRadarChart>().Single();
                    Check(!model.State.PreviewAudioPlaying && chart.ShowEnhancement, "The native result page shows a static radar without autoplay.");
                    HearingResultUiAssertions.LegendIsCentered(view);
                    HearingResultUiAssertions.DescriptionUsesAccent(view);
                    var detailScroll = view.GetVisualDescendants().OfType<ScrollViewer>().Single(control => control.Name == "PageScroll");
                    var detailOffset = detailScroll.Offset;
                    await NativeResultDetailCapture.DescriptionAsync(window, view, Path.Combine(_output, "hearing-results-description-screen.png"));
                    await NativeResultDetailCapture.LegendAsync(window, view, Path.Combine(_output, "hearing-results-legend-screen.png"));
                    var legendOffset = detailScroll.Offset;
                    var (radarHelp, radarHelpTip) = HearingResultUiAssertions.HelpControls(view);
                    var inputsBeforeHelp = service.Inputs.Count;
                    var cancelsBeforeHelp = service.Cancels;
                    Check(!radarHelpTip.IsOpen, "Native radar help opens only on request.");
                    radarHelp.RaiseEvent(new RoutedEventArgs(Avalonia.Controls.Button.ClickEvent)); await Task.Delay(60);
                    Check(radarHelpTip.IsOpen, "The native radar question button opens its teaching tip.");
                    Check(detailScroll.Offset == legendOffset, "Focusing native radar help does not scroll the result page underneath it.");
                    await CaptureAsync(view, "hearing-results-help");
                    NativeResultDetailCapture.Help(window, view, radarHelpTip, Path.Combine(_output, "hearing-results-help-detail-screen.png"));
                    radarHelp.RaiseEvent(new RoutedEventArgs(Avalonia.Controls.Button.ClickEvent)); await Task.Delay(60);
                    await Until(() => HearingResultUiAssertions.HelpIsDismissed(view, radarHelpTip));
                    Check(service.Inputs.Count == inputsBeforeHelp && service.Cancels == cancelsBeforeHelp,
                        "Native help dismissal preserves calibration state without device writes.");
                    (radarHelp, radarHelpTip) = HearingResultUiAssertions.HelpControls(view);
                    radarHelp.RaiseEvent(new RoutedEventArgs(Avalonia.Controls.Button.ClickEvent)); await Task.Delay(60);
                    Check(radarHelpTip.IsOpen, "Native radar help can reopen after dismissal completes.");
                    var escape = new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape };
                    HearingResultUiAssertions.PopupFocus(window, radarHelp).RaiseEvent(escape); await Task.Delay(60);
                    await Until(() => HearingResultUiAssertions.HelpIsDismissed(view, radarHelpTip));
                    Check(escape.Handled && service.Inputs.Count == inputsBeforeHelp && service.Cancels == cancelsBeforeHelp,
                        "Native Escape from the popup root dismisses radar help without invoking wizard cancellation.");
                    HearingResultUiAssertions.QuickDismissal(view, service);
                    (radarHelp, radarHelpTip) = HearingResultUiAssertions.HelpControls(view);
                    radarHelp.RaiseEvent(new RoutedEventArgs(Avalonia.Controls.Button.ClickEvent)); await Task.Delay(60);
                    var closeHelp = radarHelpTip.Child!.GetVisualDescendants().OfType<Button>()
                        .Single(button => button.Name == "HearingRadarHelpClose");
                    closeHelp.RaiseEvent(new RoutedEventArgs(Avalonia.Controls.Button.ClickEvent)); await Task.Delay(60);
                    Check(!radarHelpTip.IsOpen && ReferenceEquals(window.FocusManager?.GetFocusedElement(), radarHelp)
                        && service.Inputs.Count == inputsBeforeHelp && service.Cancels == cancelsBeforeHelp,
                        "The native help close button dismisses only the tip and restores focus to its question button.");
                    detailScroll.Offset = detailOffset; await Task.Delay(60);
                    await CaptureAsync(view, "hearing-results");
                    Preview(view, "PreviewOriginal").RaiseEvent(new RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
                    await Until(() => !model.Busy && service.Inputs.Last().Action == "preview-original");
                    Check(!chart.ShowEnhancement && model.State.PreviewAudioPlaying, "Native original audition hides enhancement.");
                    var spectrum = new double[] { .3, .55, .4, .9, .7, .6 };
                    long originalRevision = model.State.Revision;
                    model.AdoptSpectrum(new("operation", "fixture", 42, model.State.PreviewEpoch, 1, spectrum));
                    Check(chart.LeftLevels.Zip(resultRadar.Left).Any(pair => Math.Abs(pair.First - pair.Second) > .01)
                        && chart.RightLevels.Zip(resultRadar.Right).Any(pair => Math.Abs(pair.First - pair.Second) > .01)
                        && chart.EnhancementLevels.Count == 0 && model.State.Revision == originalRevision,
                        "Native original spectrum animates both ears while keeping orange hidden and the revision stable.");
                    await CaptureAsync(view, "hearing-results-original");
                    Preview(view, "PreviewEnhanced").RaiseEvent(new RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
                    await Until(() => !model.Busy && service.Inputs.Last().Action == "preview-enhanced");
                    long actionRevision = model.State.Revision;
                    model.AdoptSpectrum(new("operation", "fixture", 42, model.State.PreviewEpoch, 1, spectrum));
                    Check(chart.EnhancementLevels.SequenceEqual(spectrum) && model.State.Revision == actionRevision && ReferenceEquals(model.State.HearingRadar, resultRadar),
                        "Native enhanced spectrum displays orange without replacing the source curves or changing the action revision.");
                    await CaptureAsync(view, "hearing-results-enhanced");
                    var scroll = view.GetVisualDescendants().OfType<ScrollViewer>().Single(x => x.Name == "PageScroll");
                    scroll.Offset = new Vector(0, 300); await Task.Delay(50); await CaptureAsync(view, "hearing-audition-controls");
                    scroll.Offset = new Vector(0, 0);
                    Preview(view, "PreviewEnhanced").RaiseEvent(new RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
                    await Until(() => !model.Busy && service.Inputs.Last().Action == "preview-stop");
                    Check(chart.ShowEnhancement && chart.EnhancementLevels.Count == 0
                        && chart.LeftLevels.SequenceEqual(resultRadar.Left) && chart.RightLevels.SequenceEqual(resultRadar.Right),
                        "Native active-row pause restores static ears and the orange outline.");
                    window.RequestedThemeVariant = ThemeVariant.Dark; await Task.Delay(50);
                    HearingResultUiAssertions.DescriptionUsesAccent(view);
                    HearingResultUiAssertions.LegendIsCentered(view); await CaptureAsync(view, "hearing-results-dark");
                    window.Width = 400; window.Height = 520; await Task.Delay(50);
                    HearingResultUiAssertions.LegendIsCentered(view); await CaptureAsync(view, "hearing-results-narrow");
                    window.Width = 520; window.Height = 720; window.RequestedThemeVariant = ThemeVariant.Light; await Task.Delay(50);
                    var (cancelHelp, cancelTip) = HearingResultUiAssertions.HelpControls(view);
                    cancelHelp.RaiseEvent(new RoutedEventArgs(Avalonia.Controls.Button.ClickEvent)); await Task.Delay(60);
                    Check(cancelTip.IsOpen, "Native help can be open when cancellation is requested.");
                    service.StopFailuresRemaining = 1;
                    Button(view, "取消").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
                    await Until(() => model.CancellationFailed && !model.CancellationPending);
                    Check(!model.StopConfirmed && window.IsVisible && Button(view, "重试停止").IsEnabled,
                        "Native STOP failure keeps the window and its retry button available.");
                    Check(!cancelTip.IsOpen, "Native cancellation closes result help even when STOP must be retried.");
                    await CaptureAsync(view, "stop-failed");
                    Button(view, "重试停止").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
                    await Until(() => model.StopConfirmed && !model.CancellationPending);
                    Check(service.Cancels == 2 && model.State.Stage == "Cancelled" && model.Error.Length == 0,
                        "Native retry confirms STOP only after a successful response.");
                    var fitService = new TestService();
                    await using var fitModel = new CalibrationViewModel(fitService, "operation", "fit");
                    await fitModel.InitializeAsync();
                    var fitView = new CalibrationView(fitModel); var fitWindow = CreateWindow(fitView, "贴合度 fixture");
                    try
                    {
                        fitWindow.Show(); await Task.Delay(100);
                        fitService.State = fitService.State with { Revision = 2, Stage = "Completed", FitResults = [new(1, 0), new(2, 1)] };
                        fitModel.Adopt(fitService.State); await Task.Delay(50);
                        var text = fitView.GetVisualDescendants().OfType<TextBlock>().Select(x => x.Text).ToArray();
                        Check(text.Contains("贴合一般") && text.Contains("贴合良好") && Button(fitView, "重新测试").IsEnabled,
                            "Native fit results preserve the three-state wording and retry.");
                        await CaptureAsync(fitView, "fit-results");
                    }
                    finally { fitWindow.Close(); }
                    await CheckWindowClosingAsync();
                    _completed = true;
                }
                catch (Exception error) { _failure = error.ToString(); }
                finally { service.ReleaseLevel.TrySetResult(); await model.DisposeAsync(); window.Close(); _loop.Cancel(); }
            };
            base.OnFrameworkInitializationCompleted();
            // The fixture owns the dispatcher lifetime so production wizard closes can be tested independently.
            window.Show();

            void Push(UiCalibrationState state)
            { service.State = state with { Revision = model.State.Revision + 1 }; model.Adopt(service.State); Dispatcher.UIThread.RunJobs(); }
        }
    }

    private static FAAppWindow CreateWindow(CalibrationView view, string title)
    {
        view.Bind(TemplatedControl.BackgroundProperty, new DynamicResourceExtension("SolidBackgroundFillColorBaseBrush"));
        return _basicRenderMode is null
            ? new() { Content = view, Title = title, Width = 520, Height = 720, ShowActivated = false, Position = new PixelPoint(-32000, -32000) }
            : new() { Content = view, Title = title, Width = 520, Height = 720, ShowActivated = true,
                Topmost = true, WindowStartupLocation = WindowStartupLocation.CenterScreen };
    }
    private static async Task CheckWindowClosingAsync()
    {
        var service = ActiveService(); service.StopFailuresRemaining = 1;
        var window = CreateWindow();
        try
        {
            window.Show(); await Task.Delay(50); window.Close();
            await Until(() => service.Cancels == 1 && Button(window, "重试停止").IsEnabled);
            Check(window.IsVisible && !service.Disposed && service.State.Stage == "Failed",
                "A native window close retains its service after STOP fails.");
            Button(window, "重试停止").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
            await Until(() => !window.IsVisible);
            Check(service.Cancels == 2 && service.Disposed && service.State.Stage == "Cancelled",
                "A native retry click closes only after a confirmed STOP.");
        }
        finally { if (window.IsVisible) { service.StopFailuresRemaining = 0; window.Close(); await Until(() => !window.IsVisible); } }

        service = ActiveService(); service.StopFailuresRemaining = 1; window = CreateWindow();
        try
        {
            window.Show(); await Task.Delay(50);
            service.Push(new BackendEvent { Kind = "requestExit", ExitRequestId = "exit-failed" });
            await Until(() => service.ExitApprovals.Count == 1);
            Check(service.ExitApprovals is [("exit-failed", false)] && window.IsVisible && !service.Disposed,
                "A native failed STOP acknowledges the exit request as false.");
            service.Push(new BackendEvent { Kind = "requestExit", ExitRequestId = "exit-retry" });
            await Until(() => !window.IsVisible);
            Check(service.ExitApprovals is [("exit-failed", false), ("exit-retry", true)] && service.Cancels == 2 && service.Disposed,
                "A native confirmed retry acknowledges exit as true before closing.");
        }
        finally { if (window.IsVisible) { service.StopFailuresRemaining = 0; window.Close(); await Until(() => !window.IsVisible); } }

        service = ActiveService(); service.StopFailuresRemaining = 1; window = CreateWindow();
        try
        {
            window.Show(); await Task.Delay(50); window.Close();
            await Until(() => service.Cancels == 1 && Button(window, "重试停止").IsEnabled);
            service.Push(new BackendEvent { Kind = "unavailable", Message = "后台已断开。" });
            await Until(() => !window.IsVisible);
            Check(service.Cancels == 1 && service.Disposed && service.State.Stage == "Failed" && service.ExitApprovals.Count == 0,
                "A disconnected backend permits native local close without claiming STOP succeeded.");
        }
        finally { if (window.IsVisible) { service.StopFailuresRemaining = 0; window.Close(); await Until(() => !window.IsVisible); } }

        static TestEventService ActiveService() => new() { State = new UiCalibrationState
            { Handle = "operation", Identity = "earbuds", Generation = 42, Revision = 1, BackendInstanceId = "fixture",
                Stage = "AwaitingAnswer", HearingVersion = "V2", ToneAcknowledged = true } };
        CalibrationWindow CreateWindow() => new(service, "operation", "hearing")
            { ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual, Position = new PixelPoint(-32000, -32000) };
    }
    private static Button Button(Control view, string text) => view.GetVisualDescendants().OfType<Button>().Single(x => Equals(x.Content, text));
    private static ToggleButton Preview(Control view, string name) => view.GetVisualDescendants().OfType<ToggleButton>().Single(x => x.Name == name);
    private static void PressKey(Slider slider, Key key)
    {
        slider.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = key });
        slider.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyUpEvent, Key = key });
    }
    private static async Task Until(Func<bool> condition)
    {
        for (var i = 0; i < 300 && !condition(); i++) await Task.Delay(10);
        Check(condition(), "Native interaction completed.");
    }
    private static async Task CaptureAsync(Control view, string name)
    {
        if (_basicRenderMode is not null && TopLevel.GetTopLevel(view) is Window window)
        {
            await NativeRenderingProbe.WaitForPixelsAsync(window);
            Screenshots.Add(NativeRenderingProbe.Capture(window, Path.Combine(_output, name + "-screen.png")));
        }
        var actionBar = view.GetVisualDescendants().OfType<Border>().Single(x => x.Name == "ActionBar");
        Check(actionBar.Background is ISolidColorBrush brush && brush.Color.A == 255 && brush.Opacity == 1,
            "The native hearing and fit action bars remain opaque across scroll positions and themes.");
        using var bitmap = new RenderTargetBitmap(new PixelSize((int)Math.Ceiling(view.Bounds.Width), (int)Math.Ceiling(view.Bounds.Height)), new Vector(96, 96));
        bitmap.Render(view);
        using var file = File.Create(Path.Combine(_output, name + ".png"));
        bitmap.Save(file, PngBitmapEncoderOptions.Default);
    }
    private static void Check(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); _checks++; }
}
