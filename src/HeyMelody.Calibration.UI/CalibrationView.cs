using Avalonia;
using Avalonia.Animation;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using FluentAvalonia.UI.Controls;
using HeyMelodyWindows.App;
using HeyMelodyWindows.Artwork;
using HeyMelodyWindows.Resources;
using HeyMelodyWindows.Ui;
using PathShape = Avalonia.Controls.Shapes.Path;

namespace HeyMelodyWindows.Calibration.UI;

/// <summary>One reusable workflow view shared by the two independently launched components.</summary>
/// <remarks>
/// Follows the device manager: a fixed header on Mica Alt, notices pinned above a scrolling page and an opaque 72 px
/// action bar. Model changes are coalesced into one update per dispatcher pass, and the page stays enabled while a
/// request runs because every control already disables itself; greying the whole page flickered on each click.
/// </remarks>
public sealed class CalibrationView : UserControl
{
    private const string Accent = "AccentFillColorDefaultBrush", OnAccent = "TextOnAccentFillColorPrimaryBrush",
        Primary = "TextFillColorPrimaryBrush", Secondary = "TextFillColorSecondaryBrush", Pending = "ControlStrongStrokeColorDefaultBrush";
    // Five rounded bars; FluentAvalonia's symbol font has no waveform glyph.
    private const string Waveform = "M3,10 L3,14 M7.5,7 L7.5,17 M12,4 L12,20 M16.5,7 L16.5,17 M21,10 L21,14";
    private const string DifferenceNotice = "左右耳在部分频段的选择差异较大，请根据实际听感确认结果。";
    private readonly CalibrationViewModel _model;
    private readonly bool _hearing;
    private readonly Grid _stepper;
    private readonly StepNode _scanStep, _testStep;
    private readonly Border _stepConnector = new() { Height = 2, CornerRadius = new CornerRadius(1), VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(8, 17, 8, 0) };
    private readonly ProgressBar _activity = new() { Height = 3, Opacity = 0, IsHitTestVisible = false,
        Transitions = [new DoubleTransition { Property = OpacityProperty, Duration = TimeSpan.FromMilliseconds(150) }] };
    private readonly FAInfoBar _errorBar = new() { IsClosable = false, Severity = FAInfoBarSeverity.Error };
    private readonly FAInfoBar _noticeBar = new() { IsClosable = false };
    private readonly StackPanel _body = new() { Spacing = 16 };
    private readonly TextBlock _stopped = Text("", 20, FontWeight.SemiBold, TextAlignment.Center);
    // The page heading persists across rebuilt pages so screen readers announce each new step.
    private readonly TextBlock _heading = Text("", 20, FontWeight.SemiBold, TextAlignment.Center);
    private readonly TextBlock _instruction = SecondaryText("", TextAlignment.Center);
    private readonly TextBlock _progress = new() { FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock _nameError = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap, IsVisible = false };
    private readonly List<Viewbox> _heroes = [];
    private bool _updateQueued, _compactHero;
    private readonly TextBlock _status = new() { FontSize = 12, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly StackPanel _actions = new() { Orientation = Orientation.Horizontal, Spacing = 12, VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _cancel = new() { Content = "取消", MinWidth = 80, VerticalAlignment = VerticalAlignment.Center };
    public event Func<Task>? CloseRequested;
    private string _bodyKey = "";
    private Slider? _level;
    private ComboBox? _outputPicker;
    private Button? _refreshOutputs;
    private bool _updatingOutput;
    private bool _updatingLevel, _rippleFollowsTone;
    private bool? _connectorDone;
    private Button? _next, _retryLevel, _save, _resume;
    private readonly List<(ToggleButton Button, FASymbolIcon Icon, string Mode)> _previewButtons = [];
    private HearingRadarChart? _radar;
    private RadarTeachingTip? _radarHelpTip;
    private TextBlock? _levelLabel, _previewLabel, _caption;
    private TextBlock? _leftLabel, _rightLabel;
    private Control? _heroGlyph;
    private Ellipse? _ripple;
    private readonly List<Button> _answerButtons = [];
    private readonly List<ProgressBar> _segments = [];
    private static readonly Lazy<BundledResourceStore> Artwork = new(() => new());

    public CalibrationView(CalibrationViewModel model)
    {
        _model = model; _hearing = model.Kind == "hearing";
        Styles.Add(SegmentStyle("ProgressBarRoot")); Styles.Add(SegmentStyle("ProgressBarTrack", 4)); Styles.Add(SegmentStyle("PART_Indicator"));
        Styles.Add(RippleStyle());
        _scanStep = new StepNode("耳道扫描", brush => Icon(AppIcon.Hearing, 22, brush));
        _testStep = new StepNode("听音测试", brush => Wave(20, brush));
        _stepper = new Grid { ColumnDefinitions = new("Auto,*,Auto"), MaxWidth = 320, HorizontalAlignment = HorizontalAlignment.Stretch,
            Children = { _scanStep.Root, _stepConnector, _testStep.Root } };
        Grid.SetColumn(_stepConnector, 1); Grid.SetColumn(_testStep.Root, 2);
        _progress.Bind(TextBlock.ForegroundProperty, new DynamicResourceExtension(Secondary));
        AutomationProperties.SetLiveSetting(_progress, AutomationLiveSetting.Polite);
        AutomationProperties.SetLiveSetting(_heading, AutomationLiveSetting.Polite);
        AutomationProperties.SetLiveSetting(_instruction, AutomationLiveSetting.Polite);
        _nameError.Bind(TextBlock.ForegroundProperty, new DynamicResourceExtension("SystemFillColorCriticalBrush"));
        var header = new StackPanel { Margin = new Thickness(24, 12, 24, 12), Spacing = 12, Children =
        {
            new StackPanel { Spacing = 2, Children = { Text(_hearing ? "黄金听感" : "耳塞贴合度测试", 24, FontWeight.SemiBold), _progress } },
            _stepper, _activity
        } };
        _stopped.Margin = new Thickness(0, 48, 0, 0);
        // Errors and notices stay visible whatever the page is scrolled to.
        var notices = new StackPanel { Margin = new Thickness(24, 0, 24, 0), Spacing = 8, Children = { _errorBar, _noticeBar } };
        var page = new StackPanel { Margin = new Thickness(24, 8, 24, 84), Spacing = 16, Children = { _stopped, _body } };
        _status.Bind(TextBlock.ForegroundProperty, new DynamicResourceExtension(Secondary));
        var bar = new Grid { ColumnDefinitions = new("*,Auto,Auto"), ColumnSpacing = 12, Children = { _status, _actions, _cancel } };
        Grid.SetColumn(_actions, 1); Grid.SetColumn(_cancel, 2);
        var actionBar = new Border { Name = "ActionBar", Height = 72, VerticalAlignment = VerticalAlignment.Bottom, Padding = new Thickness(24, 12), Child = bar };
        actionBar.Bind(Border.BackgroundProperty, new DynamicResourceExtension("SolidBackgroundFillColorSecondaryBrush"));
        var pageScroll = new ScrollViewer { Name = "PageScroll", Content = page,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        SmoothScrolling.Attach(pageScroll);
        var content = new Grid { Children =
        {
            pageScroll,
            actionBar
        } };
        Grid.SetRow(notices, 1); Grid.SetRow(content, 2);
        Content = new Grid { RowDefinitions = new("Auto,Auto,*"), Children = { header, notices, content } };
        _cancel.IsCancel = true;
        _cancel.Click += async (_, _) =>
        {
            if (CloseRequested is { } close) await close();
            else await _model.CancelAsync();
        };
        // One backend state fires several property changes; they are drawn together in a single update.
        model.PropertyChanged += (_, _) => QueueUpdate();
        ActualThemeVariantChanged += (_, _) => QueueUpdate();
        DetachedFromVisualTree += (_, _) => CloseRadarHelp();
        AddHandler(KeyDownEvent, DismissRadarHelpOnEscape, RoutingStrategies.Tunnel);
        SizeChanged += (_, e) => UpdateHeroSize(e.NewSize.Height);
        model.SpectrumChanged += _ =>
        {
            if (Dispatcher.UIThread.CheckAccess()) UpdateSpectrum(); else Dispatcher.UIThread.Post(UpdateSpectrum);
        };
        Update();
    }
    private void QueueUpdate()
    {
        if (_updateQueued) return;
        _updateQueued = true;
        Dispatcher.UIThread.Post(() => { _updateQueued = false; Update(); });
    }
    /// <summary>Short windows show a 112 px hero instead of 160 px, leaving room for the instructions.</summary>
    private void UpdateHeroSize(double height)
    {
        var compact = height is > 0 and < 640;
        if (compact == _compactHero) return;
        _compactHero = compact;
        foreach (var hero in _heroes) hero.Height = compact ? 112 : 160;
    }
    private void Update()
    {
        var state = _model.State;
        var resultPage = state.Stage is "Results" or "ResultReady" or "Previewing";
        var busy = _model.Busy || _model.LevelPending || state.Stage is "CheckingWear" or "Starting" or "Measuring" or "Scanning" or "LoadingCurves" or "LoadingFilters" or "Saving" or "Cancelling";
        // The fixed slot fades rather than collapsing, so the page never shifts; idle bars stop animating.
        _activity.Opacity = busy ? 1 : 0; _activity.IsIndeterminate = busy;
        var error = _model.Error.Length > 0 ? _model.Error : state.PreviewAudioError;
        _errorBar.Message = error; _errorBar.IsOpen = _errorBar.IsVisible = error.Length > 0;
        UpdateNotice(state, resultPage);
        UpdateStepper(state);
        var stopping = state.Stage is "Cancelled" or "Cancelling";
        _body.IsEnabled = !stopping;
        _actions.IsEnabled = !_model.Busy && !stopping;
        _body.IsVisible = _actions.IsVisible = !_model.CancellationRequested;
        if (_model.CancellationRequested) CloseRadarHelp();
        _stopped.IsVisible = _model.CancellationRequested;
        _stopped.Text = _model.CanCloseLocally ? "蓝牙后台已断开" : _model.CancellationPending ? "正在停止测试…"
            : _model.StopConfirmed ? "测试已停止" : _model.CancellationFailed ? "尚未确认停止测试" : "正在停止测试…";
        _cancel.IsEnabled = !_model.CancellationPending || _model.CanCloseLocally;
        var closing = _model.CanCloseLocally || state.Stage is "Completed" or "Cancelled";
        _cancel.Content = _model.CanCloseLocally ? "关闭" : _model.CancellationFailed ? "重试停止" : closing ? "关闭" : "取消";
        _cancel.Classes.Set("accent", closing && !_model.CancellationFailed);
        var hearingStep = _hearing && _model.Phase == CalibrationPhase.Hearing && !_model.CancellationRequested;
        _status.Text = _model.CancellationPending ? "正在停止测试…" : _model.Busy ? "正在处理…" : "";
        // Progress sits under the title for both workflows, with or without the ear-scan stepper.
        _progress.Text = _model.CancellationRequested ? ""
            : hearingStep && state.StepCount > 0 ? $"听音测试 · 第 {Math.Clamp(state.StepIndex + 1, 1, state.StepCount)} / {state.StepCount} 步"
            : resultPage || state.Stage == "Completed" ? "测试完成"
            : !_hearing && state.Stage is "CheckingWear" or "Starting" or "Measuring" ? "正在检测…" : "";
        _progress.IsVisible = _progress.Text.Length > 0;
        var key = string.Join('/', resultPage ? "Results" : state.Stage, _model.Phase, state.HearingVersion, state.SupportsEarScan, state.StepIndex > 0,
            state.FiltersReady, state.CanPreview, state.PreviewAudioOutputSelectionRequired, state.HearingRadar is not null,
            state.HearingLevels.Count == state.StepCount, state.FitResults.Count, state.HearingEnhanceType, state.RestoreDescriptionId, ActualThemeVariant);
        if (_bodyKey != key)
        {
            CloseRadarHelp();
            _radarHelpTip = null;
            _bodyKey = key; _body.Children.Clear(); _actions.Children.Clear(); _level = null; _next = _retryLevel = _save = _resume = null; _radar = null;
            _levelLabel = _previewLabel = _caption = _leftLabel = _rightLabel = null; _heroGlyph = null; _ripple = null; _rippleFollowsTone = false;
            _previewButtons.Clear(); _answerButtons.Clear(); _segments.Clear(); _heroes.Clear();
            _outputPicker = null; _refreshOutputs = null;
            _nameError.IsVisible = false;
            BuildBody();
        }
        if (_level is not null)
        {
            _updatingLevel = true; _level.Value = _model.SelectedLevelIndex; _updatingLevel = false;
            _level.IsEnabled = _model.CanAdjustLevel;
        }
        if (_caption is not null)
            _caption.Text = $"当前测试{(state.Side == 1 ? "左耳" : "右耳")} · {(state.FrequencyHz is { } hz ? $"{hz} Hz" : $"频段 {state.FrequencyType}")}";
        // The head faces the tested ear, as in the official wizard.
        if (_heroGlyph is not null) _heroGlyph.RenderTransform = new ScaleTransform(state.Side == 2 ? -1 : 1, 1);
        if (_ripple is not null && _rippleFollowsTone)
            _ripple.Classes.Set("ripple", state.Stage == "AwaitingAnswer" && state.ToneAcknowledged && !_model.LevelPending);
        for (var index = 0; index < _segments.Count; index++)
            _segments[index].Value = index < state.StepIndex ? 100 : index == state.StepIndex ? 50 : 0;
        if (_leftLabel is not null && _rightLabel is not null)
        {
            var right = state.StepIndex >= state.StepCount / 2 && state.StepIndex < state.StepCount;
            Emphasize(_leftLabel, !right && state.StepIndex < state.StepCount); Emphasize(_rightLabel, right);
        }
        if (_levelLabel is not null) _levelLabel.Text = _model.LevelPending ? "正在调整音量…"
            : state.ToneAcknowledged && _model.SelectedLevelIndex == state.LevelIndex ? $"音量：第 {_model.SelectedLevelIndex + 1} / {CalibrationViewModel.LevelCount} 档" : "测试音尚未确认，请重试播放。";
        if (_next is not null)
        { _next.IsEnabled = _model.CanContinue; _next.Content = state.StepIndex == state.StepCount - 1 ? "完成测听" : "下一步"; }
        if (_retryLevel is not null) _retryLevel.IsEnabled = _model.CanAdjustLevel && !_model.LevelPending;
        if (_save is not null) _save.IsEnabled = _model.CanSave;
        if (_resume is not null) _resume.IsEnabled = state.CanResume && !_model.Busy;
        if (_outputPicker is not null)
        {
            _updatingOutput = true;
            _outputPicker.ItemsSource = _model.PreviewOutputs;
            _outputPicker.SelectedItem = _model.PreviewOutputs.FirstOrDefault(x => x.Token == state.SelectedPreviewAudioOutputToken);
            _outputPicker.IsEnabled = !_model.Busy && !_model.LoadingPreviewOutputs;
            _outputPicker.PlaceholderText = _model.LoadingPreviewOutputs ? "正在读取蓝牙音频输出…" : _model.PreviewOutputs.Count == 0
                ? "未找到立体声蓝牙音频输出" : "选择当前耳机的音频输出";
            _updatingOutput = false;
        }
        if (_refreshOutputs is not null) _refreshOutputs.IsEnabled = !_model.Busy && !_model.LoadingPreviewOutputs;
        if (_previewLabel is not null) _previewLabel.Text = state.Stage == "Previewing" && state.PreviewAudioPlaying
            ? state.PreviewMode == "Enhanced" ? "正在试听增强后 · 再次点击可暂停" : "正在试听增强前 · 再次点击可暂停"
            : "内置试听音乐 · 选择增强前或增强后开始播放";
        foreach (var (button, icon, mode) in _previewButtons)
        {
            bool playing = state.Stage == "Previewing" && state.PreviewAudioPlaying && mode == state.PreviewMode;
            button.IsEnabled = _model.CanPreview; button.IsChecked = playing;
            icon.Symbol = playing ? FASymbol.Pause : FASymbol.Play;
            AutomationProperties.SetHelpText(button, playing ? "暂停试听" : "播放内置试听音乐");
        }
        _radar?.SetResult(state.HearingRadar); UpdateSpectrum();
        foreach (var button in _answerButtons) button.IsEnabled = !_model.Busy && state.Stage == "AwaitingAnswer" && state.ToneAcknowledged;
    }
    private void UpdateSpectrum() => _radar?.SetPreview(_model.State.PreviewMode,
        _model.State.Stage == "Previewing" && _model.State.PreviewAudioPlaying, _model.SpectrumLevels);
    private void UpdateNotice(UiCalibrationState state, bool resultPage)
    {
        // Host messages explain pauses and refusals; the result page gives its own inter-ear explanation once.
        var difference = resultPage && state.FiltersReady && state.HasInterEarDifference;
        var message = difference ? DifferenceNotice
            : state.Message.Length > 0 ? state.Message : state.NoiseWarning ? "环境较吵，请保持安静后继续。" : "";
        _noticeBar.Message = message;
        _noticeBar.Severity = state.Stage == "Failed" ? FAInfoBarSeverity.Error
            : difference || state.NoiseWarning || state.Stage is "Paused" or "Interrupted" ? FAInfoBarSeverity.Warning : FAInfoBarSeverity.Informational;
        _noticeBar.IsOpen = _noticeBar.IsVisible = message.Length > 0 && !_model.CancellationRequested;
    }
    private void UpdateStepper(UiCalibrationState state)
    {
        _stepper.IsVisible = _hearing && state.SupportsEarScan;
        if (!_stepper.IsVisible) return;
        var phase = _model.Phase;
        _scanStep.Apply(_model.EarScanCompleted ? StepState.Done : StepState.Active);
        _testStep.Apply(phase is CalibrationPhase.Results or CalibrationPhase.Done ? StepState.Done
            : phase == CalibrationPhase.Hearing ? StepState.Active : StepState.Pending);
        if (_connectorDone == _model.EarScanCompleted) return;
        _connectorDone = _model.EarScanCompleted;
        _stepConnector.Bind(Border.BackgroundProperty, new DynamicResourceExtension(_model.EarScanCompleted ? Accent : Pending));
    }
    private void BuildBody()
    {
        if (_hearing) BuildHearing(_model.State); else BuildFit(_model.State);
    }
    private void BuildHearing(UiCalibrationState state)
    {
        switch (state.Stage)
        {
            case "Ready" or "Idle":
                if (state.HearingVersion == "V2") AddHearingGuide();
                else AddHero(Icon(AppIcon.Hearing, 30, OnAccent));
                AddHeading("为你的双耳建立专属听感", state.SupportsEarScan
                    ? "先扫描耳道，再分别测试左耳和右耳的 6 个频段，整个过程需要几分钟。"
                    : "将分别测试左耳和右耳的 6 个频段，整个过程需要几分钟。");
                AddPreparation();
                AddAction("开始", "start", true);
                return;
            case "ScanCompleted":
                AddHero(Check(), ripple: false);
                AddHeading("耳道扫描已完成", "接下来开始听音测试，请留意耳机中的“滴滴”声。");
                AddAction("下一步", "next", true);
                return;
            case "Paused" or "Interrupted":
                AddEarHero(followTone: false);
                AddHeading("测试已暂停", "恢复后将继续当前频段，不会自动记录答案。");
                AddSegments();
                _resume = AddAction("继续测试", "resume", true);
                return;
            case "AwaitingAnswer" or "Measuring":
                AddEarHero(followTone: true);
                if (state.HearingVersion == "V2") BuildLevelTest(state);
                else BuildAnswerTest();
                return;
            case "LoadingCurves" or "LoadingFilters" or "Saving":
                AddHero(Ring(), filled: false);
                AddHeading(state.Stage == "Saving" ? "正在保存黄金听感" : "正在生成听感结果", "请保持耳机佩戴，稍候片刻。");
                return;
            case "Results" or "ResultReady" or "Previewing":
                if (!state.FiltersReady)
                {
                    AddHero(Ring(), filled: false);
                    AddHeading("正在准备听感结果", "请保持耳机佩戴，稍候片刻。");
                    return;
                }
                BuildResults(state);
                return;
            case "Completed":
                AddHero(Check());
                AddHeading("黄金听感已保存并启用", "可在主界面的“听感配置”中切换或关闭。");
                return;
            case "Failed":
                AddHero(Icon(AppIcon.Hearing, 30, OnAccent));
                AddHeading("测试未能完成", "请检查佩戴和连接后重试。");
                if (state.HearingLevels.Count == state.StepCount) AddAction("重新获取结果", "retry-curves", true);
                else AddAction("重试", "start", true);
                return;
            case "Cancelled" or "Cancelling":
                AddHeading(state.Stage == "Cancelled" ? "测试已取消" : "正在停止测试", "");
                return;
            default:
                // Wear checks and start-up stages belong to the scan or to the test, as the phase records.
                var scanning = _model.Phase == CalibrationPhase.EarScan;
                if (scanning) AddHero(Icon(AppIcon.Hearing, 30, OnAccent), ripple: true);
                else AddEarHero(followTone: false, ripple: true);
                AddHeading(state.Stage switch
                {
                    "CheckingWear" => "正在检查佩戴状态",
                    "Scanning" => "正在扫描耳道",
                    _ => scanning ? "正在准备耳道扫描" : "正在准备听音测试"
                }, state.Stage == "CheckingWear" ? "请佩戴好两只耳机。" : scanning ? "请保持耳机位置稳定，扫描约需 15 秒。" : "请保持安静，测试马上开始。");
                return;
        }
    }
    private void BuildLevelTest(UiCalibrationState state)
    {
        AddHeading("左右拖动音量条，找到“滴滴”声刚好消失的临界点", "");
        AddSideCaption();
        AddSegments();
        var slider = new Slider { Minimum = 0, Maximum = CalibrationViewModel.LevelCount - 1, TickFrequency = 1, IsSnapToTickEnabled = true, TickPlacement = TickPlacement.BottomRight,
            Value = _model.SelectedLevelIndex, VerticalAlignment = VerticalAlignment.Center };
        _level = slider;
        AutomationProperties.SetName(slider, "听感测试音量");
        slider.ValueChanged += async (_, _) =>
        {
            if (_updatingLevel || !ReferenceEquals(_level, slider)) return;
            await _model.AdjustLevelAsync((int)Math.Round(slider.Value), false);
        };
        slider.AddHandler(PointerReleasedEvent, async (_, _) =>
        { if (ReferenceEquals(_level, slider)) await _model.AdjustLevelAsync((int)Math.Round(slider.Value), true); }, Avalonia.Interactivity.RoutingStrategies.Bubble, true);
        slider.AddHandler(KeyUpEvent, async (_, e) =>
        { if (ReferenceEquals(_level, slider) && e.Key is Avalonia.Input.Key.Left or Avalonia.Input.Key.Right or Avalonia.Input.Key.Home or Avalonia.Input.Key.End or Avalonia.Input.Key.Up or Avalonia.Input.Key.Down)
            await _model.AdjustLevelAsync((int)Math.Round(slider.Value), true); }, Avalonia.Interactivity.RoutingStrategies.Bubble, true);
        var quiet = Symbol(FASymbol.Speaker0, 16, Secondary); var loud = Symbol(FASymbol.Speaker2, 16, Secondary);
        quiet.Margin = loud.Margin = new Thickness(0, 0, 0, 6);
        var volume = new Grid { ColumnDefinitions = new("Auto,*,Auto"), ColumnSpacing = 12, Children = { quiet, slider, loud } };
        Grid.SetColumn(slider, 1); Grid.SetColumn(loud, 2);
        _levelLabel = new TextBlock { FontSize = 12, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
        _levelLabel.Bind(TextBlock.ForegroundProperty, new DynamicResourceExtension(Secondary));
        _retryLevel = new Button { Content = "重试播放", VerticalAlignment = VerticalAlignment.Center };
        _retryLevel.Click += async (_, _) => await _model.AdjustLevelAsync(_model.SelectedLevelIndex, true);
        var level = new Grid { ColumnDefinitions = new("*,Auto"), ColumnSpacing = 12, Children = { _levelLabel, _retryLevel } };
        Grid.SetColumn(_retryLevel, 1);
        _body.Children.Add(new StackPanel { Spacing = 4, Children = { volume, level } });
        if (state.StepIndex > 0) AddAction("上一步", "previous", false);
        _next = AddAction(state.StepIndex == state.StepCount - 1 ? "完成测听" : "下一步", "next", true);
    }
    private void BuildAnswerTest()
    {
        AddHeading("能听到“滴滴”声吗？", "");
        AddSideCaption();
        AddSegments();
        var answers = new Grid { ColumnDefinitions = new("*,*"), ColumnSpacing = 12 };
        foreach (var (text, heard) in new[] { ("听到了", true), ("没听到", false) })
        {
            var button = new Button { Content = text, Height = 44, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Center };
            button.Click += async (_, _) => await _model.SendAsync("answer", heard: heard);
            Grid.SetColumn(button, answers.Children.Count); answers.Children.Add(button); _answerButtons.Add(button);
        }
        _body.Children.Add(answers);
    }
    private void BuildResults(UiCalibrationState state)
    {
        // The stepper already marks both steps done; the cards keep the name field above the action bar.
        AddHeading("你的黄金听感已生成", "切换增强前与增强后效果进行对比，满意后命名并保存。");
        if (HearingResultDescription.Create(state.HearingEnhanceType, state.RestoreDescriptionId) is { } description)
        {
            var text = SecondaryText("");
            text.Name = "HearingResultDescription";
            foreach (var segment in description.Segments)
            {
                var run = new Run(segment.Text);
                if (segment.IsHighlighted)
                    run.Bind(TextElement.ForegroundProperty, new DynamicResourceExtension("AccentTextFillColorPrimaryBrush"));
                text.Inlines!.Add(run);
            }
            AutomationProperties.SetName(text, description.Text);
            _body.Children.Add(text);
        }
        if (state.HearingRadar is not null)
        {
            _radar = new HearingRadarChart { HorizontalAlignment = HorizontalAlignment.Stretch };
            var legend = new StackPanel { Name = "HearingRadarLegend", Orientation = Orientation.Horizontal, Spacing = 16,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            // The legend uses the chart's theme colours, so dark mode shows the same light-on-dark tones.
            foreach (var (label, brush, legendName) in new[] { ("左耳", "HmChartLeftBrush", "HearingLegendLeft"),
                ("右耳", "HmChartRightBrush", "HearingLegendRight"), ("增强后", "HmChartEnhancedBrush", "HearingLegendEnhanced") })
            {
                var dot = new Ellipse { Name = legendName + "Dot", Width = 8, Height = 8, VerticalAlignment = VerticalAlignment.Center };
                dot.Bind(Shape.FillProperty, new DynamicResourceExtension(brush));
                var text = SecondaryText(label);
                text.Name = legendName + "Label";
                text.VerticalAlignment = VerticalAlignment.Center;
                var item = new Grid { Name = legendName, ColumnDefinitions = new("Auto,Auto"), ColumnSpacing = 6,
                    VerticalAlignment = VerticalAlignment.Center, Children = { dot, text } };
                Grid.SetColumn(text, 1);
                legend.Children.Add(item);
            }
            var info = new Button { Name = "HearingRadarHelp", Content = Symbol(FASymbol.Help, 16, Secondary), Padding = new Thickness(4),
                VerticalAlignment = VerticalAlignment.Center };
            AutomationProperties.SetName(info, "关于听感雷达图"); legend.Children.Add(info);
            var chartContent = new Grid { Children = { new StackPanel { Spacing = 4, Children = { _radar, legend } } } };
            _radarHelpTip = CreateRadarHelpTip(info);
            chartContent.Children.Add(_radarHelpTip);
            info.Click += (_, _) =>
            {
                if (_radarHelpTip is not { } tip) return;
                tip.IsOpen = !tip.IsOpen;
            };
            _body.Children.Add(Card(chartContent));
        }
        else _body.Children.Add(new FAInfoBar { IsOpen = true, IsClosable = false, Severity = FAInfoBarSeverity.Informational,
            Message = "当前耳机未提供可绘制的听感曲线，仍可试听并保存配置。" });
        if (state.CanPreview)
        {
            _previewLabel = SecondaryText("");
            var choices = new StackPanel { Spacing = 8 };
            if (state.PreviewAudioOutputSelectionRequired)
            {
                _outputPicker = new ComboBox { Name = "PreviewAudioOutput", HorizontalAlignment = HorizontalAlignment.Stretch,
                    ItemTemplate = new FuncDataTemplate<UiPreviewAudioOutput>((item, _) => new TextBlock { Text = item?.Name }) };
                AutomationProperties.SetName(_outputPicker, "本次试听的蓝牙音频输出");
                _outputPicker.SelectionChanged += async (_, _) =>
                {
                    if (!_updatingOutput && _outputPicker?.SelectedItem is UiPreviewAudioOutput output)
                        await _model.SelectPreviewOutputAsync(output.Token);
                };
                _refreshOutputs = new Button { Name = "RefreshPreviewAudioOutputs", Content = "刷新", MinWidth = 64 };
                _refreshOutputs.Click += async (_, _) => await _model.RefreshPreviewOutputsAsync();
                var outputRow = new Grid { ColumnDefinitions = new("*,Auto"), ColumnSpacing = 8,
                    Children = { _outputPicker, _refreshOutputs } };
                Grid.SetColumn(_refreshOutputs, 1);
                choices.Children.Add(SecondaryText("请选择当前耳机对应的蓝牙音频输出，本次试听会固定使用此输出。"));
                choices.Children.Add(outputRow);
            }
            foreach (var (text, action, mode, controlName) in new[] { ("增强前", "preview-original", "Original", "PreviewOriginal"), ("增强后", "preview-enhanced", "Enhanced", "PreviewEnhanced") })
            {
                var icon = Symbol(FASymbol.Play, 20, Primary);
                var title = Text(text, 16, FontWeight.SemiBold);
                var row = new Grid { ColumnDefinitions = new("*,Auto"), ColumnSpacing = 16, Children = { title, icon } };
                Grid.SetColumn(icon, 1);
                var button = new ToggleButton { Name = controlName, Content = row, MinHeight = 52, Padding = new Thickness(12),
                    HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch };
                AutomationProperties.SetName(button, text);
                button.Click += async (_, _) =>
                {
                    bool playing = _model.State.PreviewAudioPlaying && _model.State.PreviewMode == mode;
                    button.IsChecked = playing;
                    await _model.SendAsync(playing ? "preview-stop" : action);
                };
                choices.Children.Add(button); _previewButtons.Add((button, icon, mode));
            }
            _body.Children.Add(Card(Symbol(FASymbol.Audio, 20, Primary), "试听对比", _previewLabel, choices));
        }
        var name = new TextBox { Text = _model.Name, PlaceholderText = "例如：我的听感", MaxLength = 40 };
        AutomationProperties.SetName(name, "黄金听感配置名称");
        name.TextChanged += (_, _) => { _model.Name = name.Text ?? ""; _nameError.IsVisible = false; };
        _body.Children.Add(Card(Symbol(FASymbol.Rename, 20, Primary), "配置名称", SecondaryText("保存后可在主界面的“听感配置”中选择。"),
            new StackPanel { Spacing = 6, Children = { name, Detach(_nameError) } }));
        var save = new Button { Content = "保存并启用", MinWidth = 80, IsDefault = true };
        save.Classes.Add("accent");
        _save = save;
        save.Click += async (_, _) =>
        {
            // The problem is shown beside the field and stays until the name is edited.
            if (_model.ValidateName() is { } error) { _nameError.Text = error; _nameError.IsVisible = true; name.Focus(); return; }
            var replacement = false;
            if ((_model.NeedsReplacement || _model.ReplacesCurrent) && TopLevel.GetTopLevel(this) is { } owner)
            {
                var dialog = new FAContentDialog { Title = "保存黄金听感", Content = _model.NeedsReplacement
                        ? "新方案将替换耳机中的当前黄金听感。只有已保存到本机历史的配置可再次选择。若本机已有 10 份配置，保存时会替换最早且未在使用的配置。"
                        : "新方案将替换耳机中的当前黄金听感。只有已保存到本机历史的配置可再次选择。",
                    PrimaryButtonText = "保存", CloseButtonText = "取消", DefaultButton = FAContentDialogButton.Close };
                if (await dialog.ShowAsync(owner) != FAContentDialogResult.Primary) return;
                replacement = true;
            }
            await _model.SendAsync("save", allowReplacement: replacement);
        };
        _actions.Children.Add(save);
    }
    private void DismissRadarHelpOnEscape(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || _radarHelpTip is not { IsOpen: true }) return;
        CloseRadarHelp(restoreFocus: true);
        e.Handled = true;
    }
    private static RadarTeachingTip CreateRadarHelpTip(Button target)
    {
        return new RadarTeachingTip { Name = "HearingRadarHelpTip", Target = target, Title = "关于听感雷达图",
            Subtitle = "根据耳机返回的结果展示左右耳听感与增强区间。试听增强前或增强后时，左右耳曲线随音乐频谱变化；增强后还会显示橙色音乐频谱。" };
    }
    private void CloseRadarHelp(bool restoreFocus = false) => _radarHelpTip?.Dismiss(restoreFocus);
    private void BuildFit(UiCalibrationState state)
    {
        switch (state.Stage)
        {
            case "Ready" or "Idle":
                AddHero(EarbudPair());
                AddHeading("检查两只耳塞的贴合度", "贴合良好的耳塞能稳定佩戴，并带来更完整的低音和降噪效果。");
                AddPreparation();
                AddAction("开始", "start", true);
                return;
            case "Completed":
                BuildFitResults(state);
                return;
            case "Failed":
                AddHero(EarbudPair());
                AddHeading("检测未能完成", "请检查佩戴和连接后重试。");
                AddAction("重试", "start", true);
                return;
            case "Cancelled" or "Cancelling":
                AddHeading(state.Stage == "Cancelled" ? "检测已取消" : "正在停止检测", "");
                return;
            default:
                AddHero(EarbudPair(), ripple: true);
                AddHeading(state.Stage == "CheckingWear" ? "正在检查佩戴状态" : "正在检测贴合度", "请保持耳机佩戴稳定，并保持安静。");
                return;
        }
    }
    private void BuildFitResults(UiCalibrationState state)
    {
        AddHero(EarbudPair());
        AddHeading("检测完成", "");
        var statuses = new[] { 1, 2 }.Select(component => state.FitResults.FirstOrDefault(x => x.Component == component)?.Status).ToArray();
        var cards = new Grid { ColumnDefinitions = new("*,*"), ColumnSpacing = 12 };
        for (var index = 0; index < 2; index++)
        { var card = FitCard(index + 1, statuses[index]); Grid.SetColumn(card, index); cards.Children.Add(card); }
        _body.Children.Add(cards);
        var (severity, advice) = statuses.Any(x => x is null) ? (FAInfoBarSeverity.Warning, "未获得完整结果，请重新测试。")
            : statuses.Any(x => x == 6) ? (FAInfoBarSeverity.Warning, "请调整佩戴位置，或更换尺寸合适的耳塞后重新测试。")
            : statuses.Any(x => x == 0) ? (FAInfoBarSeverity.Informational, "可以微调佩戴位置或尝试其他尺寸的耳塞，以获得更好的贴合。")
            : (FAInfoBarSeverity.Success, "两只耳机均贴合良好。");
        _body.Children.Add(new FAInfoBar { IsClosable = false, IsOpen = true, Severity = severity, Message = advice });
        AddAction("重新测试", "start", false);
    }
    private static Control FitCard(int component, int? status)
    {
        var (text, brush) = status switch
        {
            1 => ("贴合良好", "SystemFillColorSuccessBrush"),
            0 => ("贴合一般", "SystemFillColorCautionBrush"),
            6 => ("贴合较差", "SystemFillColorCriticalBrush"),
            _ => ("未获得结果", Pending)
        };
        var badge = new Border { Width = 44, Height = 44, CornerRadius = new CornerRadius(22), VerticalAlignment = VerticalAlignment.Center,
            Child = Icon(AppIcon.FitTest, 26, OnAccent, mirrored: component == 2) };
        badge.Bind(Border.BackgroundProperty, new DynamicResourceExtension(brush));
        var labels = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center,
            Children = { SecondaryText(component == 1 ? "左耳" : "右耳"), Text(text, 16, FontWeight.SemiBold) } };
        var row = new Grid { ColumnDefinitions = new("Auto,*"), ColumnSpacing = 12, Children = { badge, labels } };
        Grid.SetColumn(labels, 1);
        return Card(row);
    }

    private void AddHeading(string instruction, string caption)
    {
        _heading.Text = instruction;
        _body.Children.Add(Detach(_heading));
        _instruction.Text = caption;
        if (caption.Length > 0) _body.Children.Add(Detach(_instruction));
    }
    /// <summary>Releases a persistent control from the page it was last shown in.</summary>
    private static T Detach<T>(T control) where T : Control
    {
        if (control.Parent is Panel panel) panel.Children.Remove(control);
        return control;
    }
    private void AddEarHero(bool followTone, bool ripple = false)
    {
        var glyph = Icon(AppIcon.Hearing, 30, OnAccent);
        glyph.Name = "HeroGlyph";
        AddHero(glyph, ripple: ripple);
        _heroGlyph = glyph; _rippleFollowsTone = followTone;
    }
    private void AddSideCaption()
    {
        _caption = SecondaryText("", TextAlignment.Center);
        _body.Children.Add(_caption);
    }
    /// <summary>Concentric accent discs echo the official wizard's ripples while staying theme-aware.</summary>
    private void AddHero(Control center, bool ripple = false, bool filled = true)
    {
        const double size = 160;
        var hero = new Grid { Name = "Hero", Width = size, Height = size, HorizontalAlignment = HorizontalAlignment.Center };
        foreach (var (scale, opacity) in new[] { (1.0, .06), (.78, .10), (.58, .16) })
        {
            var disc = new Ellipse { Width = size * scale, Height = size * scale, Opacity = opacity };
            disc.Bind(Shape.FillProperty, new DynamicResourceExtension(Accent));
            hero.Children.Add(disc);
        }
        _ripple = new Ellipse { Width = size, Height = size, StrokeThickness = 2, Opacity = 0, RenderTransform = new ScaleTransform(.45, .45) };
        _ripple.Bind(Shape.StrokeProperty, new DynamicResourceExtension(Accent));
        if (ripple) _ripple.Classes.Add("ripple");
        hero.Children.Add(_ripple);
        var core = new Border { Name = "HeroCore", Width = size * .4, Height = size * .4, CornerRadius = new CornerRadius(size * .2), Child = center };
        if (filled) core.Bind(Border.BackgroundProperty, new DynamicResourceExtension(Accent));
        center.HorizontalAlignment = HorizontalAlignment.Center; center.VerticalAlignment = VerticalAlignment.Center;
        hero.Children.Add(core);
        var box = new Viewbox { Height = _compactHero ? 112 : 160, Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Center, Child = hero };
        AutomationProperties.SetAccessibilityView(box, AccessibilityView.Raw);
        _heroes.Add(box);
        _body.Children.Add(box);
    }
    /// <summary>Two groups of six ProgressBars; the current frequency is half filled, as in the official wizard.</summary>
    private void AddSegments()
    {
        var state = _model.State;
        var count = Math.Max(state.StepCount, 2);
        var strip = new Grid { ColumnSpacing = 4 };
        for (var index = 0; index < count; index++)
        {
            if (index == count / 2) strip.ColumnDefinitions.Add(new ColumnDefinition(12, GridUnitType.Pixel));
            strip.ColumnDefinitions.Add(new ColumnDefinition(1, GridUnitType.Star));
            var segment = new ProgressBar { Minimum = 0, Maximum = 100, Height = 4, MinHeight = 4, MinWidth = 0 };
            segment.Classes.Add("segment");
            Grid.SetColumn(segment, strip.ColumnDefinitions.Count - 1);
            strip.Children.Add(segment); _segments.Add(segment);
        }
        AutomationProperties.SetName(strip, "听音测试进度");
        _leftLabel = SecondaryText("左耳", TextAlignment.Center); _rightLabel = SecondaryText("右耳", TextAlignment.Center);
        var labels = new Grid { ColumnDefinitions = new("*,12,*"), ColumnSpacing = 4, Children = { _leftLabel, _rightLabel } };
        Grid.SetColumn(_rightLabel, 2);
        _body.Children.Add(new StackPanel { Spacing = 8, Margin = new Thickness(0, 4), Children = { strip, labels } });
    }
    private void AddPreparation()
    {
        _body.Children.Add(Card(Icon(AppIcon.FitTest, 20, Primary), "佩戴好两只耳机", SecondaryText("两只耳机均已连接，并稳定佩戴在耳道中。")));
        _body.Children.Add(Card(Symbol(FASymbol.SpeakerMute, 20, Primary), "保持环境安静", SecondaryText("测试期间请暂停音乐，避免接听电话。")));
    }
    private Button AddAction(string text, string action, bool primary)
    {
        var button = new Button { Content = text, MinWidth = 80, IsDefault = primary };
        if (primary) button.Classes.Add("accent");
        button.Click += async (_, _) => await _model.SendAsync(action);
        _actions.Children.Add(button);
        return button;
    }
    private void AddHearingGuide()
    {
        try
        {
            var asset = Artwork.Value.FindNamedAsset("melody_ui_gold_hearing_detecting_guide", ActualThemeVariant == ThemeVariant.Dark);
            if (asset is null || asset.Kind != "lottie") { AddHero(Icon(AppIcon.Hearing, 30, OnAccent)); return; }
            var path = Artwork.Value.FullPath(asset);
            // The official volume-guide animation shows how the slider is used.
            _body.Children.Add(Card(new LottieView(new FileLottieSource(new Uri(path), System.IO.Path.GetDirectoryName(path)!, asset.Sha256,
                () => Artwork.Value.Acquire(asset) ?? throw new FileNotFoundException("Hearing guide asset is unavailable."))) { Height = 96, HorizontalAlignment = HorizontalAlignment.Stretch }));
        }
        catch (IOException) { AddHero(Icon(AppIcon.Hearing, 30, OnAccent)); }
    }

    private static Border Card(Control icon, string title, TextBlock description, Control? content = null, Control? trailing = null)
    {
        var titles = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center, Children = { Text(title, 14, FontWeight.SemiBold), description } };
        icon.VerticalAlignment = VerticalAlignment.Center;
        var header = new Grid { ColumnDefinitions = new("Auto,*,Auto"), ColumnSpacing = 16, Children = { icon, titles } };
        Grid.SetColumn(titles, 1);
        if (trailing is not null) { Grid.SetColumn(trailing, 2); header.Children.Add(trailing); }
        return Card(content is null ? header : new StackPanel { Spacing = 12, Children = { header, content } });
    }
    private static Border Card(Control child)
    {
        var card = new Border { Padding = new Thickness(16), BorderThickness = new Thickness(1), Child = child };
        card.Bind(Border.CornerRadiusProperty, new DynamicResourceExtension("ControlCornerRadius"));
        card.Bind(Border.BackgroundProperty, new DynamicResourceExtension("CardBackgroundFillColorDefaultBrush"));
        card.Bind(Border.BorderBrushProperty, new DynamicResourceExtension("CardStrokeColorDefaultBrush"));
        return card;
    }
    private static TextBlock Text(string text, double size, FontWeight weight, TextAlignment alignment = TextAlignment.Left)
    {
        var block = new TextBlock { Text = text, FontSize = size, FontWeight = weight, TextAlignment = alignment, TextWrapping = TextWrapping.Wrap };
        block.Bind(TextBlock.ForegroundProperty, new DynamicResourceExtension(Primary));
        return block;
    }
    private static TextBlock SecondaryText(string text, TextAlignment alignment = TextAlignment.Left)
    {
        var block = new TextBlock { Text = text, TextAlignment = alignment, TextWrapping = TextWrapping.Wrap };
        block.Bind(TextBlock.ForegroundProperty, new DynamicResourceExtension(Secondary));
        return block;
    }
    private static void Emphasize(TextBlock label, bool current)
    {
        label.FontWeight = current ? FontWeight.SemiBold : FontWeight.Normal;
        label.Bind(TextBlock.ForegroundProperty, new DynamicResourceExtension(current ? Primary : Secondary));
    }
    private static Control Icon(AppIcon icon, double size, string brush, bool mirrored = false)
    {
        var path = AppIcons.Element(icon, size);
        path.Bind(FAIconElement.ForegroundProperty, new DynamicResourceExtension(brush));
        if (mirrored) path.RenderTransform = new ScaleTransform(-1, 1);
        return path;
    }
    private static Control Wave(double size, string brush)
    {
        var path = new PathShape { Data = Geometry.Parse(Waveform), Stretch = Stretch.Uniform, Width = size, Height = size, StrokeThickness = 2, StrokeLineCap = PenLineCap.Round };
        path.Bind(Shape.StrokeProperty, new DynamicResourceExtension(brush));
        return path;
    }
    private static FASymbolIcon Symbol(FASymbol symbol, double size, string brush)
    {
        var icon = new FASymbolIcon { Symbol = symbol, FontSize = size, Width = size, Height = size };
        icon.Bind(FAIconElement.ForegroundProperty, new DynamicResourceExtension(brush));
        return icon;
    }
    private static Control Check() => Symbol(FASymbol.Checkmark, 30, OnAccent);
    private static Control Ring() => new FAProgressRing { IsActive = true, IsIndeterminate = true, Width = 48, Height = 48 };
    private static Control EarbudPair() => new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2,
        Children = { Icon(AppIcon.FitTest, 28, OnAccent), Icon(AppIcon.FitTest, 28, OnAccent, mirrored: true) } };
    private static Style SegmentStyle(string part, double? height = null)
    {
        var style = new Style(x => x.OfType<ProgressBar>().Class("segment").Template().OfType<Border>().Name(part))
        { Setters = { new Setter(Border.CornerRadiusProperty, new CornerRadius(2)) } };
        if (height is { } value) style.Setters.Add(new Setter(Border.HeightProperty, value));
        return style;
    }
    /// <summary>An expanding ring marks an active scan, measurement or acknowledged test tone.</summary>
    private static Style RippleStyle() => new(x => x.OfType<Ellipse>().Class("ripple"))
    {
        Animations =
        {
            new Animation
            {
                Duration = TimeSpan.FromSeconds(1.8), IterationCount = IterationCount.Infinite, Easing = new Avalonia.Animation.Easings.CubicEaseOut(),
                Children =
                {
                    new KeyFrame { Cue = new Cue(0), Setters = { new Setter(ScaleTransform.ScaleXProperty, .45), new Setter(ScaleTransform.ScaleYProperty, .45), new Setter(OpacityProperty, .7) } },
                    new KeyFrame { Cue = new Cue(1), Setters = { new Setter(ScaleTransform.ScaleXProperty, 1.0), new Setter(ScaleTransform.ScaleYProperty, 1.0), new Setter(OpacityProperty, 0.0) } }
                }
            }
        }
    };

    private enum StepState { Pending, Active, Done }
    /// <summary>One stepper node: a 36 px disc with the step glyph, or a check mark once done.</summary>
    private sealed class StepNode
    {
        private readonly Border _disc = new() { Width = 36, Height = 36, CornerRadius = new CornerRadius(18), BorderThickness = new Thickness(1.5), HorizontalAlignment = HorizontalAlignment.Center };
        private readonly Func<string, Control> _glyph;
        private readonly TextBlock _label;
        private StepState? _state;
        public StackPanel Root { get; }
        public StepNode(string label, Func<string, Control> glyph)
        {
            _glyph = glyph;
            _label = new TextBlock { Text = label, FontSize = 12, HorizontalAlignment = HorizontalAlignment.Center };
            Root = new StackPanel { Spacing = 6, Children = { _disc, _label } };
            AutomationProperties.SetName(Root, label);
        }
        public void Apply(StepState state)
        {
            if (_state == state) return;
            _state = state;
            var child = state == StepState.Done ? Symbol(FASymbol.Checkmark, 18, OnAccent) : _glyph(state == StepState.Pending ? Secondary : OnAccent);
            child.HorizontalAlignment = HorizontalAlignment.Center; child.VerticalAlignment = VerticalAlignment.Center;
            _disc.Child = child;
            _disc.Bind(Border.BackgroundProperty, new DynamicResourceExtension(state == StepState.Pending ? "SubtleFillColorTransparentBrush" : Accent));
            _disc.Bind(Border.BorderBrushProperty, new DynamicResourceExtension(state == StepState.Pending ? Pending : Accent));
            _label.FontWeight = state == StepState.Active ? FontWeight.SemiBold : FontWeight.Normal;
            _label.Bind(TextBlock.ForegroundProperty, new DynamicResourceExtension(state == StepState.Pending ? Secondary : Primary));
            AutomationProperties.SetHelpText(Root, state switch { StepState.Done => "已完成", StepState.Active => "当前步骤", _ => "未开始" });
        }
    }
}
