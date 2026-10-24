using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Ellipse = Avalonia.Controls.Shapes.Ellipse;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using FluentAvalonia.Styling;
using HeyMelodyWindows.App;
using HeyMelodyWindows.Artwork;
using HeyMelodyWindows.Calibration.UI;
using HeyMelodyWindows.Ui;

internal static class CalibrationVisualTests
{
    public static int Run()
    {
        var output = Path.GetFullPath(Path.Combine("artifacts", "calibration-ui-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss")));
        Directory.CreateDirectory(output);
        using var session = HeadlessUnitTestSession.StartNew(typeof(CalibrationTestBootstrap));
        session.Dispatch<bool>(async () =>
        {
            var service = new TestService();
            service.State = service.State with { HearingVersion = "V2", SupportsEarScan = true };
            await using var model = new CalibrationViewModel(service, "operation", "hearing");
            await model.InitializeAsync();
            var view = new CalibrationView(model);
            var window = new Window { Content = view, Width = 520, Height = 720, WindowDecorations = WindowDecorations.None };
            try
            {
                window.Show(); Dispatcher.UIThread.RunJobs();
                var guide = view.GetVisualDescendants().OfType<LottieView>().Single();
                for (int attempt = 0; !guide.AnimationLoaded && guide.LoadError is null && attempt < 300; attempt++)
                { await Task.Delay(10); Dispatcher.UIThread.RunJobs(); }
                Check(guide.AnimationLoaded && guide.AnimationActive, "The independent setup's ready page displays the bundled HeyMelody Lottie guide.");
                for (int frame = 0; frame < 15; frame++)
                { using var tick = window.CaptureRenderedFrame(); await Task.Delay(100); Dispatcher.UIThread.RunJobs(); }
                Check(guide.RenderedFrameCount > 1, "The setup guide produces actual Avalonia compositor frames.");
                CheckActionBar(view, "开始");
                Check(Visible(view, "耳道扫描") && Visible(view, "听音测试"), "An ear-scan wizard shows its two-step progress header.");
                Capture(window, "hearing-ready");
                Push(service.State with { Stage = "Scanning" });
                Check(model.Phase == CalibrationPhase.EarScan && view.GetVisualDescendants().OfType<Ellipse>().Any(x => x.Classes.Contains("ripple"))
                    && !HasButton("开始"), "The ear scan page animates its hero and offers only cancellation.");
                Capture(window, "hearing-scan");
                Push(service.State with { Stage = "ScanCompleted" });
                Check(model.EarScanCompleted && HasButton("下一步"), "A completed scan marks its step and continues to the hearing test.");

                Push(service.State with { Stage = "AwaitingAnswer", HearingVersion = "V1", SupportsEarScan = false, ToneAcknowledged = true });
                Check(HasButton("听到了") && HasButton("没听到") && !view.GetVisualDescendants().OfType<Slider>().Any(),
                    "The V1 workflow uses two answer buttons.");
                Capture(window, "hearing-v1");
                Push(service.State with { Stage = "AwaitingAnswer", HearingVersion = "V2", SupportsEarScan = true, StepIndex = 1, LevelIndex = 8, FrequencyType = 2, FrequencyHz = 1000 });
                var slider = view.GetVisualDescendants().OfType<Slider>().Single();
                Check(slider.Value == 8 && HasButton("下一步") && HasButton("上一步") && service.Inputs.Count == 0,
                    "V2 has its discrete level slider and both navigation buttons; remote state updates send no command.");
                CheckActionBar(view, "上一步", "下一步");
                Check(Segments() is [100, 50, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0],
                    "Twelve ProgressBars show completed steps full, the current frequency half filled and later steps empty.");
                Push(service.State with { LevelIndex = 9 });
                Check(slider.Value == 9 && service.Inputs.Count == 0, "A pushed level changes the existing slider without starting a measurement command.");
                Check(view.GetVisualDescendants().OfType<TextBlock>().Any(x => x.Text?.Contains("1000 Hz") == true),
                    "The workflow displays the frequency supplied by the verified device mapping.");
                Push(service.State with { StepIndex = 2, FrequencyType = 3, FrequencyHz = 2000, LevelIndex = 5, Decibels = -19 });
                slider = view.GetVisualDescendants().OfType<Slider>().Single();
                Check(slider.Value == 5 && service.Inputs.Count == 0, "Every following frequency resets to the factory middle value without a duplicate UI write.");
                slider.Focus(NavigationMethod.Tab);
                window.KeyPress(Key.Right, RawInputModifiers.None, PhysicalKey.ArrowRight, null);
                window.KeyPress(Key.Right, RawInputModifiers.None, PhysicalKey.ArrowRight, null);
                window.KeyPress(Key.Right, RawInputModifiers.None, PhysicalKey.ArrowRight, null);
                window.KeyRelease(Key.Right, RawInputModifiers.None, PhysicalKey.ArrowRight, null);
                await Until(() => service.Inputs.Count > 0 && !model.LevelPending);
                Check(slider.Value == 8 && service.Inputs is [{ Action: "level", LevelIndex: 8, Released: true }] && model.CanContinue,
                    "Actual slider keyboard input sends the final changed level and enables Next only after its ACK.");
                FindButton("下一步").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Until(() => service.Inputs.Last().Action == "next" && !model.Busy);
                Check(view.GetVisualDescendants().OfType<Slider>().Single().Value == 5,
                    "A real Next click displays the following step's middle level.");
                Push(service.State with { FrequencyHz = null });
                Check(view.GetVisualDescendants().OfType<TextBlock>().Any(x => x.Text?.Contains("频段 4") == true)
                    && !view.GetVisualDescendants().OfType<TextBlock>().Any(x => x.Text?.Contains("4000 Hz") == true),
                    "An unverified model displays a frequency identifier rather than claiming the X4 Hz mapping.");
                Capture(window, "hearing-v2");
                Push(service.State with { StepIndex = 7, Side = 2, FrequencyType = 2, FrequencyHz = 1000 });
                var glyph = view.GetVisualDescendants().OfType<Control>().Single(x => x.Name == "HeroGlyph");
                Check(glyph.RenderTransform is ScaleTransform { ScaleX: -1 } && Visible(view, "当前测试右耳 · 1000 Hz")
                    && Segments() is [100, 100, 100, 100, 100, 100, 100, 50, 0, 0, 0, 0],
                    "The right-ear group continues the progress strip and mirrors the hero toward the tested ear.");
                window.RequestedThemeVariant = ThemeVariant.Dark; Dispatcher.UIThread.RunJobs();
                Capture(window, "hearing-v2-dark");
                window.RequestedThemeVariant = ThemeVariant.Light; Dispatcher.UIThread.RunJobs();
                Push(service.State with { Stage = "Interrupted", CanResume = false, ToneAcknowledged = false });
                Check(!view.GetVisualDescendants().OfType<Slider>().Any() && !FindButton("继续测试").IsEnabled,
                    "A hardware interruption removes answer controls until the earphones can resume.");
                Push(service.State with { CanResume = true });
                Check(FindButton("继续测试").IsEnabled, "A recovery event restores the resume button without rebuilding the operation.");
                FindButton("继续测试").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Until(() => service.Inputs.Last().Action == "resume" && !model.Busy);
                Push(service.State with { Stage = "Failed", StepIndex = 12, HearingLevels = Enumerable.Range(0, 12)
                    .Select(i => new UiHearingPoint(i / 6 + 1, i % 6 + 1, -19)).ToArray() });
                Check(HasButton("重新获取结果") && !HasButton("重试"), "A filter timeout retries the retained answers rather than starting a new hearing measurement.");
                FindButton("重新获取结果").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Until(() => service.Inputs.Last().Action == "retry-curves" && !model.Busy);
                Check(HasButton("保存并启用"), "A successful curve retry restores the result page.");
                Push(service.State with { Stage = "Results", StepIndex = 12, FiltersReady = false });
                Check(!view.GetVisualDescendants().OfType<TextBox>().Any() && !HasButton("保存并启用"),
                    "Twelve answers alone do not expose the result or save controls while filters are incomplete.");
                var resultRadar = new UiHearingRadar(new double[] { .85, .62, .66, .58, .7, .82 }, new double[] { .83, .6, .64, .58, .67, .55 });
                Push(service.State with { FiltersReady = true, CanPreview = true, HasInterEarDifference = true, HearingRadar = resultRadar,
                    HearingEnhanceType = 0, RestoreDescriptionId = 1 });
                Check(view.GetVisualDescendants().OfType<TextBox>().Count() == 1 && HasButton("保存并启用"),
                    "The result page names and saves a profile, separately from the main-window selector.");
                Check(view.GetVisualDescendants().OfType<TextBlock>().Count(x => x.Text?.Contains("左右耳在部分频段的选择差异较大") == true) == 1,
                    "The result reports an inter-ear selection difference once, without assigning a hearing diagnosis.");
                CheckActionBar(view, "保存并启用");
                HearingResultUiAssertions.LegendIsCentered(view);
                HearingResultUiAssertions.DescriptionUsesAccent(view);
                var (radarHelp, radarHelpTip) = HearingResultUiAssertions.HelpControls(view);
                var inputsBeforeHelp = service.Inputs.Count;
                var cancelsBeforeHelp = service.Cancels;
                var resultScroll = view.GetVisualDescendants().OfType<ScrollViewer>().Single(control => control.Name == "PageScroll");
                var offsetBeforeHelp = resultScroll.Offset;
                Check(!radarHelpTip.IsOpen, "Radar help stays closed until its question button is clicked.");
                radarHelp.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Dispatcher.UIThread.RunJobs();
                Check(radarHelpTip.IsOpen, "Clicking radar help opens its anchored teaching tip.");
                Check(resultScroll.Offset == offsetBeforeHelp, "Opening radar help keeps the underlying result page stationary.");
                Capture(window, "hearing-results-help");
                radarHelp.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Dispatcher.UIThread.RunJobs();
                await Until(() => HearingResultUiAssertions.HelpIsDismissed(view, radarHelpTip));
                Check(service.Inputs.Count == inputsBeforeHelp && service.Cancels == cancelsBeforeHelp,
                    "Clicking radar help again dismisses it without sending any device operation.");
                (radarHelp, radarHelpTip) = HearingResultUiAssertions.HelpControls(view);
                radarHelp.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Dispatcher.UIThread.RunJobs();
                Check(radarHelpTip.IsOpen, "Radar help can reopen after button dismissal completes.");
                var escape = new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape };
                view.RaiseEvent(escape); Dispatcher.UIThread.RunJobs();
                await Until(() => HearingResultUiAssertions.HelpIsDismissed(view, radarHelpTip));
                Check(escape.Handled && service.Inputs.Count == inputsBeforeHelp && service.Cancels == cancelsBeforeHelp,
                    "Escape dismisses radar help before the wizard can interpret it as cancellation.");
                (radarHelp, radarHelpTip) = HearingResultUiAssertions.HelpControls(view);
                radarHelp.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Dispatcher.UIThread.RunJobs();
                Check(radarHelpTip.IsOpen, "Radar help can reopen after keyboard dismissal.");
                window.MouseDown(new Point(10, 10), MouseButton.Left); window.MouseUp(new Point(10, 10), MouseButton.Left);
                Dispatcher.UIThread.RunJobs();
                await Until(() => HearingResultUiAssertions.HelpIsDismissed(view, radarHelpTip));
                Check(service.Inputs.Count == inputsBeforeHelp && service.Cancels == cancelsBeforeHelp,
                    "Clicking outside dismisses radar help without cancelling the wizard.");
                HearingResultUiAssertions.QuickDismissal(view, service);
                var editor = view.GetVisualDescendants().OfType<TextBox>().Single(); editor.Text = "我的听感";
                var chart = view.GetVisualDescendants().OfType<HearingRadarChart>().Single();
                Check(service.Inputs.All(input => !input.Action.StartsWith("preview-")) && chart.ShowEnhancement && chart.EnhancementLevels.Count == 0,
                    "The result radar opens with a static enhancement outline and never autoplays.");
                CheckRadarComposition(chart);
                chart.SetResult(resultRadar); chart.SetPreview("Stopped", false, []);
                foreach (var (name, action, mode) in new[] { ("PreviewOriginal", "preview-original", "Original"), ("PreviewEnhanced", "preview-enhanced", "Enhanced"), ("PreviewEnhanced", "preview-stop", "Stopped") })
                {
                    view.GetVisualDescendants().OfType<ToggleButton>().Single(button => button.Name == name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    await Until(() => service.Inputs.Last().Action == action && !model.Busy);
                    Check(model.State.PreviewMode == mode && ReferenceEquals(editor, view.GetVisualDescendants().OfType<TextBox>().Single())
                        && editor.Text == "我的听感", "Preview sends the chosen effect and preserves the unsaved name.");
                    if (mode == "Original") Check(!chart.ShowEnhancement, "Original audition hides the orange enhancement region.");
                    if (mode is "Original" or "Enhanced")
                    {
                        double[] spectrum = [.25, .4, .65, .9, .5, .35];
                        long revision = model.State.Revision;
                        model.AdoptSpectrum(new("operation", "fixture", 42, model.State.PreviewEpoch, 1, spectrum));
                        Check(chart.LeftLevels.Zip(resultRadar.Left).Any(pair => Math.Abs(pair.First - pair.Second) > .01)
                            && chart.RightLevels.Zip(resultRadar.Right).Any(pair => Math.Abs(pair.First - pair.Second) > .01)
                            && model.State.Revision == revision && ReferenceEquals(model.State.HearingRadar, resultRadar),
                            "Both audition modes animate the ears without changing the source curves or action revision.");
                        Check(mode == "Enhanced" ? chart.ShowEnhancement && chart.EnhancementLevels.SequenceEqual(spectrum)
                            : !chart.ShowEnhancement && chart.EnhancementLevels.Count == 0,
                            "Only enhanced audition displays the orange music spectrum.");
                        Capture(window, mode == "Enhanced" ? "hearing-results-enhanced" : "hearing-results-original");
                    }
                    if (mode == "Stopped") Check(chart.ShowEnhancement && chart.EnhancementLevels.Count == 0
                        && chart.LeftLevels.SequenceEqual(resultRadar.Left) && chart.RightLevels.SequenceEqual(resultRadar.Right),
                        "Clicking the active row pauses and restores static ears and the orange outline.");
                }
                Capture(window, "hearing-results");
                window.RequestedThemeVariant = ThemeVariant.Dark; Dispatcher.UIThread.RunJobs();
                HearingResultUiAssertions.DescriptionUsesAccent(view);
                HearingResultUiAssertions.LegendIsCentered(view); Capture(window, "hearing-results-dark");
                window.Width = 400; window.Height = 520; Dispatcher.UIThread.RunJobs();
                HearingResultUiAssertions.LegendIsCentered(view); Capture(window, "hearing-results-narrow");
                var (cancelHelp, cancelTip) = HearingResultUiAssertions.HelpControls(view);
                cancelHelp.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Dispatcher.UIThread.RunJobs();
                Check(cancelTip.IsOpen, "A result page can display help before cancellation.");
                await model.CancelAsync(); Dispatcher.UIThread.RunJobs();
                await Until(() => !cancelTip.IsOpen);
                Check(!cancelTip.IsOpen, "Cancelling the wizard closes result help even while the result body is hidden.");
            }
            finally { window.Close(); }
            return true;
            bool HasButton(string name) => view.GetVisualDescendants().OfType<Button>().Any(button => Equals(button.Content, name));
            Button FindButton(string name) => view.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, name));
            double[] Segments() => view.GetVisualDescendants().OfType<ProgressBar>().Where(x => x.Classes.Contains("segment")).Select(x => x.Value).ToArray();
            void Push(UiCalibrationState state)
            { service.State = state with { Revision = model.State.Revision + 1 }; model.Adopt(service.State); Dispatcher.UIThread.RunJobs(); }
        }, CancellationToken.None).GetAwaiter().GetResult();
        Console.WriteLine("PASS independent hearing wizard renders its original guide and V1/V2/results layouts without setters");

        session.Dispatch<bool>(async () =>
        {
            var service = new TestService();
            await using var model = new CalibrationViewModel(service, "operation", "fit");
            await model.InitializeAsync();
            var view = new CalibrationView(model);
            var window = new Window { Content = view, Width = 520, Height = 720, WindowDecorations = WindowDecorations.None };
            try
            {
                window.Show(); Dispatcher.UIThread.RunJobs();
                CheckActionBar(view, "开始");
                Check(!Visible(view, "耳道扫描"), "The fit test has no hearing stepper.");
                Capture(window, "fit-ready");
                service.FailNextStart = true;
                view.GetVisualDescendants().OfType<Button>().Single(x => Equals(x.Content, "开始")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Until(() => model.State.Stage == "Failed" && !model.Busy);
                Check(model.Error.Length > 0 && view.GetVisualDescendants().OfType<Button>().Single(x => Equals(x.Content, "重试")).IsEnabled,
                    "A failed fit start restores a usable retry and does not leave the body busy.");
                service.State = service.State with { Revision = service.State.Revision + 1, Stage = "Completed", FitResults = [new(1, 1), new(2, 6)] };
                model.Adopt(service.State);
                Dispatcher.UIThread.RunJobs();
                var labels = view.GetVisualDescendants().OfType<TextBlock>().Select(text => text.Text).ToArray();
                Check(labels.Contains("贴合良好") && labels.Contains("贴合较差") && labels.Contains("请调整佩戴位置，或更换尺寸合适的耳塞后重新测试。")
                    && view.GetVisualDescendants().OfType<Button>().Any(button => Equals(button.Content, "重新测试")),
                    "Fit results distinguish left/right, advise on a poor fit and offer a retry.");
                CheckActionBar(view, "重新测试");
                Capture(window, "fit-results");
                window.RequestedThemeVariant = ThemeVariant.Dark; Dispatcher.UIThread.RunJobs();
                Capture(window, "fit-results-dark");
            }
            finally { window.Close(); }
            return true;
        }, CancellationToken.None).GetAwaiter().GetResult();
        Console.WriteLine("PASS independent fit window renders both ears and retry");

        session.Dispatch<bool>(async () =>
        {
            var service = ActiveService();
            service.StopFailuresRemaining = 1;
            var window = new CalibrationWindow(service, "operation", "hearing");
            window.Show(); Dispatcher.UIThread.RunJobs();
            try
            {
                Check(window.ShowAsDialog && window.Icon is not null && window.Width == 520 && window.MinWidth == 400,
                    "The production wizard window uses the device manager's dialog chrome, icon and portrait size.");
                window.Close();
                await Until(() => service.Cancels == 1 && Button(window, "重试停止")?.IsEnabled == true);
                Check(window.IsVisible && !service.Disposed && service.State.Stage == "Failed",
                    "A real window close remains open after a failed STOP and keeps its IPC service available to retry.");
                Capture(window, "stop-failed");
                service.StopFailuresRemaining = 1;
                service.Push(new BackendEvent { Kind = "requestExit", ExitRequestId = "exit-failed" });
                await Until(() => service.ExitApprovals.Count == 1);
                Check(service.ExitApprovals is [("exit-failed", false)] && window.IsVisible && !service.Disposed,
                    "A background exit request receives false and leaves the window open when STOP fails.");
                service.Push(new BackendEvent { Kind = "requestExit", ExitRequestId = "exit-retry" });
                await Until(() => service.ExitApprovals.Count == 2);
                Check(service.ExitApprovals is [("exit-failed", false), ("exit-retry", true)] && service.Cancels == 3 &&
                    window.IsVisible && !service.Disposed,
                    "A retried background exit receives true only after a confirmed STOP and waits for the final decision.");
                service.Push(new BackendEvent { Kind = "stopping", BackendInstanceId = "fixture", ExitRequestId = "exit-retry" });
                await Until(() => !window.IsVisible);
                Check(service.Cancels == 3 && service.Disposed,
                    "Final stopping closes the window and disposes its service without issuing another STOP.");
            }
            finally { if (window.IsVisible) { service.StopFailuresRemaining = 0; window.Close(); await Until(() => !window.IsVisible); } }
            return true;
            static TestEventService ActiveService() => new() { State = new UiCalibrationState
                { Handle = "operation", Identity = "earbuds", Generation = 42, Revision = 1, BackendInstanceId = "fixture",
                    Stage = "AwaitingAnswer", HearingVersion = "V2", ToneAcknowledged = true } };
            static Button? Button(Window target, string text) => target.GetVisualDescendants().OfType<Button>().SingleOrDefault(x => Equals(x.Content, text));
        }, CancellationToken.None).GetAwaiter().GetResult();
        Console.WriteLine("PASS real window STOP failure/retry and truthful exit acknowledgements");
        Console.WriteLine("Calibration UI screenshots " + output);
        return 3;
        void Capture(Window window, string name)
        {
            using var pixels = window.CaptureRenderedFrame() ?? throw new InvalidOperationException("Missing calibration pixels.");
            using var file = File.Create(Path.Combine(output, name + ".png"));
            pixels.Save(file, Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
        }
    }
    private static void Check(bool passed, string message) { if (!passed) throw new InvalidOperationException(message); }
    private static bool Visible(Control view, string text) =>
        view.GetVisualDescendants().OfType<TextBlock>().Any(x => x.Text == text && x.IsEffectivelyVisible);
    private static void CheckRadarComposition(HearingRadarChart chart)
    {
        // Independent OEM formula uses raw filter offsets, including offsets lost by the static lower clamp.
        foreach (double offset in new[] { 0d, -.2, -.8, -1.2, -3 })
        foreach (double music in new[] { .2, .7, 1 })
        {
            var result = new UiHearingRadar(Enumerable.Repeat(Math.Clamp(1 + offset, .2, 1), 6).ToArray(),
                Enumerable.Repeat(Math.Clamp(1 + offset / 2, .2, 1), 6).ToArray());
            double[] spectrum = Enumerable.Repeat(music, 6).ToArray();
            chart.SetResult(result);
            foreach (string mode in new[] { "Original", "Enhanced" })
            {
                chart.SetPreview(mode, true, spectrum);
                Check(chart.LeftLevels.All(value => Math.Abs(value - Math.Clamp(music + offset, .2, 1)) < 1e-10)
                    && chart.RightLevels.All(value => Math.Abs(value - Math.Clamp(music + offset / 2, .2, 1)) < 1e-10),
                    "Music composition must match the raw OEM offsets, including severely saturated static curves.");
            }
            chart.SetPreview("Stopped", false, spectrum);
            Check(chart.LeftLevels.SequenceEqual(result.Left) && chart.RightLevels.SequenceEqual(result.Right)
                && chart.ShowEnhancement && chart.EnhancementLevels.Count == 0, "Stopping must restore static radar data.");
            chart.SetPreview("Enhanced", true, []);
            Check(chart.LeftLevels.SequenceEqual(result.Left) && chart.RightLevels.SequenceEqual(result.Right)
                && chart.EnhancementLevels.Count == 0, "Missing spectrum must preserve static ears without inventing music.");
            chart.SetResult(null); chart.SetPreview("Original", true, spectrum);
            Check(chart.LeftLevels.Count == 0 && chart.RightLevels.Count == 0 && !chart.ShowEnhancement,
                "Music cannot invent ears when no valid filter result exists.");
        }
    }
    /// <summary>The 72 px action bar is bottom-aligned over the page and holds the page commands in order before cancel/close.</summary>
    private static void CheckActionBar(Control view, params string[] commands)
    {
        var bar = view.GetVisualDescendants().OfType<Border>().Single(x => x.Name == "ActionBar");
        var bottom = bar.TranslatePoint(new Point(0, bar.Bounds.Height), view)?.Y ?? -1;
        var buttons = bar.GetVisualDescendants().OfType<Button>().Where(x => x.IsEffectivelyVisible).Select(x => x.Content as string).ToArray();
        Check(bar.Background is ISolidColorBrush brush && brush.Color.A == 255 && brush.Opacity == 1,
            "The shared calibration action bar uses an opaque gray surface.");
        Check(bar.Bounds.Height == 72 && Math.Abs(bottom - view.Bounds.Height) <= 1 && buttons.Length == commands.Length + 1
            && buttons.Take(commands.Length).SequenceEqual(commands) && buttons[^1] is "取消" or "关闭",
            "The action bar keeps the device manager's height and the Windows wizard command order: " + string.Join(", ", buttons));
    }
    private static async Task Until(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 300 && !condition(); attempt++)
        { await Task.Delay(10); Dispatcher.UIThread.RunJobs(); }
        Dispatcher.UIThread.RunJobs();
        Check(condition(), "Timed out waiting for calibration interaction.");
    }
}

public static class CalibrationTestBootstrap
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<CalibrationTestApplication>().UseSkia()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
        .UseHeyMelodyRendering().UseHeyMelodyTypography();
}
public sealed class CalibrationTestApplication : Application
{
    public override void Initialize()
    {
        RequestedThemeVariant = ThemeVariant.Light;
        Styles.Add(new FluentAvaloniaTheme { CustomAccentColor = Color.FromRgb(0, 120, 212) });
        Styles.Add(SharedTheme.Create());
        Typography.ApplyThemeResources(this);
    }
}
