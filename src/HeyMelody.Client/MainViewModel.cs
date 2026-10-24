using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace HeyMelodyWindows.App;

public sealed class EqBandViewModel : ObservableObject
{
    private int _gain, _minimum = -6, _maximum = 6;
    public EqBandViewModel(int frequencyHz) => FrequencyHz = frequencyHz;
    public int FrequencyHz { get; }
    public string FrequencyLabel => FrequencyHz >= 1000 ? $"{FrequencyHz / 1000}k" : FrequencyHz.ToString();
    public int Gain
    {
        get => _gain;
        set { if (SetProperty(ref _gain, Math.Clamp(value, Minimum, Maximum))) OnPropertyChanged(nameof(GainLabel)); }
    }
    public string GainLabel => $"{Gain:+0;-0;0} dB";
    public int Minimum { get => _minimum; internal set => SetProperty(ref _minimum, value); }
    public int Maximum { get => _maximum; internal set => SetProperty(ref _maximum, value); }
}

/// <summary>Header status: verified control, connection or write in progress, or no verified control.</summary>
public enum ConnectionIndicator { Offline, Pending, Online }

/// <summary>UI-independent editing, prioritized device work and bounded user-authorized retries.</summary>
public sealed partial class MainViewModel : ObservableObject, IAsyncDisposable
{
    private static readonly int[] Frequencies = [31, 62, 125, 250, 500, 1000, 2000, 4000, 8000, 16000];
    // Resolved once: state notifications compare every presentation property after each backend update.
    private static readonly System.Reflection.PropertyInfo[] PresentationProperties = typeof(MainViewModel).GetProperties()
        .Where(p => !p.Name.EndsWith("Command", StringComparison.Ordinal)).ToArray();
    private readonly IHeadsetService _service;
    private readonly IConfirmationService _confirmations;
    private readonly bool _preview;
    private readonly OperationCoordinator _operations = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly List<string> _logs = [];
    private readonly List<IRelayCommand> _commands = [];
    private readonly Dictionary<string, object?> _published = [];
    private readonly Dictionary<string, PendingAction> _pending = [];
    private readonly Dictionary<string, string> _actionWarnings = [];
    private CancellationTokenSource _sessionWork = new();
    private long _sessionEpoch, _ancVersion, _presetVersion, _spatialVersion, _eqVersion, _draftIdentity;
    private int? _eqDraftId;
    private bool _refreshing, _lifecyclePending, _closeDialogRunning;
    private Task? _refreshTask;
    private UiSnapshot? _snapshot, _liveSnapshot;
    private string? _sessionIdentity;
    private UiDevice? _selectedDevice;
    private UiEqEntry? _selectedEqEntry;
    private UiOption? _selectedAnc, _selectedPreset, _selectedSpatial;
    private int? _pendingAncKey, _pendingAncPrimary, _pendingPresetKey, _pendingSpatialKey;
    private IReadOnlyList<UiDevice> _devices = [];
    private IReadOnlyList<UiOption> _ancOptions = [], _presetOptions = [], _spatialOptions = [];
    private IReadOnlyList<UiEqEntry> _eqEntries = [];
    private string _title = "选择一副耳机", _subtitle = "未连接", _status = "选择耳机后连接", _eqName = "我的均衡器";
    private bool _displaying, _eqDirty, _pendingAnc, _pendingPreset, _pendingSpatial, _closing, _closeRequested, _statusWarning;
    private Task? _disposeTask;
    private string? _reconnectPaused;

    public MainViewModel(IHeadsetService service, IConfirmationService confirmations, bool preview = false)
    {
        _service = service; _confirmations = confirmations; _preview = preview;
        Dialogs = new(_lifetime.Token);
        Bands = new(Frequencies.Select(hz => new EqBandViewModel(hz)));
        foreach (var band in Bands) band.PropertyChanged += BandChanged;
        RefreshDevicesCommand = Register(new AsyncRelayCommand(RefreshDevicesAsync, () => CanRefreshDevices));
        ConnectCommand = Register(new AsyncRelayCommand(ConnectOrDisconnectAsync, () => CanConnect));
        RefreshStateCommand = Register(new AsyncRelayCommand(RefreshStateAsync, () => CanRefreshState));
        ReconnectCommand = Register(new AsyncRelayCommand(ReconnectAsync, () => CanReconnect));
        ApplyAncCommand = Register(new AsyncRelayCommand(() => ApplyOptionAsync("Anc", SelectedAnc), () => CanSetAnc && SelectedAnc is not null));
        ApplyPresetCommand = Register(new AsyncRelayCommand(() => ApplyOptionAsync("Equalizer", SelectedPreset), () => CanSetEqualizer && SelectedPreset is not null));
        ApplySpatialCommand = Register(new AsyncRelayCommand(() => ApplyOptionAsync("Spatial", SelectedSpatial), () => CanSetSpatial && SelectedSpatial is not null));
        ToggleDualCommand = Register(new AsyncRelayCommand<bool>(value => ApplyAsync(new("DualConnection", value ? 1 : 0)), _ => CanSetDualConnection));
        ToggleGameCommand = Register(new AsyncRelayCommand<bool>(value => ApplyAsync(new("GameMode", value ? 1 : 0)), _ => CanSetGameMode));
        ToggleHiResCommand = Register(new AsyncRelayCommand<bool>(value => ApplyAsync(new("HiRes", value ? 1 : 0)), _ => CanSetHiRes));
        NewEqCommand = Register(new AsyncRelayCommand(NewEqAsync, () => CanCreateEq));
        ResetEqCommand = Register(new RelayCommand(() => LoadEntry(SelectedEqEntry), () => CanEditEq));
        SaveEqCommand = Register(new AsyncRelayCommand(SaveEqAsync, () => CanSetCustomEqualizer && EqDirty));
        UseEqCommand = Register(new AsyncRelayCommand(UseEqAsync, () => CanSetCustomEqualizer && !EqDirty && SelectedEqEntry is { Id: > 0 }));
    }

    public IReadOnlyList<UiDevice> Devices => _devices;
    public UiDevice? SelectedDevice => _selectedDevice;
    public bool HasDevices => Devices.Count != 0;
    public string Title => _title;
    public string Subtitle => _subtitle;
    public string Status => _actionWarnings.Count == 0 ? _status : string.Join(Environment.NewLine, _actionWarnings.Values);
    public bool StatusWarning => _statusWarning || _actionWarnings.Count != 0;
    public bool Busy => _lifecyclePending || _pending.Count != 0;
    public bool IsRefreshing => _refreshing;
    public bool HasActivity => Busy || IsRefreshing;
    public bool Closing => _closing || _closeRequested;
    public bool UpdatingState => _displaying;
    public DialogCoordinator Dialogs { get; }
    public bool HasVerifiedSnapshot => IsVerifiedControl(_liveSnapshot)
        && _liveSnapshot?.Identity == _sessionIdentity && _sessionIdentity == SelectedDevice?.Identity
        && (IsDemoSelected || EventService is null || _liveSnapshot?.BackendInstanceId == _backendInstanceId);
    public bool PresentationCached => _snapshot?.PresentationCached == true;
    public DateTimeOffset? PresentationSampleTime => _snapshot?.PresentationSampleTime;
    private bool CanWrite => HasVerifiedSnapshot && !Closing && !_lifecyclePending;
    public bool CanConnect => !Closing && !_lifecyclePending && SelectedDevice is not null;
    public bool CanRefreshDevices => !Closing && !_lifecyclePending;
    public bool CanRefreshState => !Closing && !_lifecyclePending && _sessionIdentity is not null;
    public bool CanReconnect => CanConnect && !IsDemoSelected;
    /// <summary>The pause between ending and starting the session in <see cref="ReconnectCommand"/>.</summary>
    internal TimeSpan ReconnectDelay { get; set; } = TimeSpan.FromSeconds(1);
    public bool ShowsDisconnect => !IsDemoSelected && (EventService is not null
        ? _snapshot is not null && _snapshot.MonitoringState != MonitoringState.Paused
        : _sessionIdentity is not null);
    public string ConnectButtonText => IsDemoSelected ? "演示设备" : ShowsDisconnect ? "断开客户端" : "连接耳机";
    // Background reads are excluded: non-event sessions poll continuously and would flicker.
    public ConnectionIndicator Indicator => Closing ? ConnectionIndicator.Offline
        : _pending.Count != 0 || _hearingPending || _snapshot?.MonitoringState == MonitoringState.Connecting
            || _lifecyclePending && !ManagingDevices && SelectedDevice is not null ? ConnectionIndicator.Pending
        : HasVerifiedSnapshot ? ConnectionIndicator.Online : ConnectionIndicator.Offline;
    public bool CanChooseAnc => CanWrite && _snapshot!.CanSetAnc;
    public bool CanChooseAncMode => CanWrite && _snapshot!.CanSetAncMode;
    public bool CanChoosePreset => CanWrite && _snapshot!.CanSetEqualizer;
    public bool CanChooseSpatial => CanWrite && _snapshot!.CanSetSpatial;
    public bool CanSetAnc => CanChooseAnc && !_pending.ContainsKey("Anc");
    public bool CanSetAncMode => CanChooseAncMode && !_pending.ContainsKey("Anc");
    public bool CanSetEqualizer => CanChoosePreset && !_pending.ContainsKey("Equalizer");
    public bool CanSetCustomEqualizer => CanEditEq && EqLayoutCompatible && !_pending.ContainsKey("Equalizer");
    public bool CanSetSpatial => CanChooseSpatial && !_pending.ContainsKey("Audio");
    public bool CanSetDualConnection => CanWrite && _snapshot!.CanSetDualConnection && _snapshot.DualConnection is not null && !_pending.ContainsKey("DualConnection");
    public bool CanSetGameMode => CanWrite && _snapshot!.CanSetGameMode && _snapshot.GameMode is not null && !_pending.ContainsKey("GameMode");
    public bool CanSetHiRes => CanWrite && _snapshot!.CanSetHiRes && _snapshot.HiRes is not null && !_pending.ContainsKey("Audio");
    public bool CanEditEq => CanWrite && _snapshot!.CanSetCustomEqualizer;
    public bool? DualConnection => PendingToggle("DualConnection") ?? _snapshot?.DualConnection;
    public bool? GameMode => PendingToggle("GameMode") ?? _snapshot?.GameMode;
    public bool? HiRes => PendingToggle("HiRes") ?? _snapshot?.HiRes;
    public string DualStateText => PendingState("DualConnection") ?? BooleanState(_snapshot?.DualConnection);
    public string GameStateText => PendingState("GameMode") ?? BooleanState(_snapshot?.GameMode);
    public string HiResStateText => PendingState("Audio") ?? BooleanState(_snapshot?.HiRes);
    private bool HasFreshObservation => EventService is null || HasVerifiedSnapshot || _liveSnapshot?.ObservationFresh == true;
    public string LeftBatteryText => Battery(HasFreshObservation ? _snapshot?.LeftBattery : null);
    public string RightBatteryText => Battery(HasFreshObservation ? _snapshot?.RightBattery : null);
    public string CaseBatteryText => Battery(HasFreshObservation ? _snapshot?.CaseBattery : null);
    public string LeftChargeText => Charge(HasFreshObservation ? _snapshot?.LeftCharging : null, HasFreshObservation ? _snapshot?.LeftBattery : null);
    public string RightChargeText => Charge(HasFreshObservation ? _snapshot?.RightCharging : null, HasFreshObservation ? _snapshot?.RightBattery : null);
    public string CaseChargeText => Charge(HasFreshObservation ? _snapshot?.CaseCharging : null, HasFreshObservation ? _snapshot?.CaseBattery : null);
    public bool LeftCharging => HasFreshObservation && _snapshot?.LeftCharging == true;
    public bool RightCharging => HasFreshObservation && _snapshot?.RightCharging == true;
    public bool CaseCharging => HasFreshObservation && _snapshot?.CaseCharging == true;
    public string? Firmware => _snapshot?.Firmware;
    public string? ProductId => _snapshot is { } snapshot
        && snapshot.Identity == _sessionIdentity && _sessionIdentity == SelectedDevice?.Identity
        ? snapshot.ProductId : null;
    public bool SpatialHiResMutex => _snapshot?.SpatialHiResMutex == true;
    public IReadOnlyList<string> Warnings => _snapshot?.Warnings ?? [];
    public bool HasWarnings => Warnings.Count != 0;
    public string WarningsText => string.Join(Environment.NewLine, Warnings.Select(warning => Sanitize(
        _snapshot?.PresentationCached == true && warning == "部分耳机状态暂时不可用，相关设置已禁用。"
            ? "上次连接时有状态提示，连接后请刷新确认。" : warning)));
    public string LogText => string.Join(Environment.NewLine, _logs);
    public IReadOnlyList<UiOption> AncOptions => _ancOptions;
    public IReadOnlyList<UiOption> PresetOptions => _presetOptions;
    public IReadOnlyList<UiOption> SpatialOptions => _spatialOptions;
    public UiOption? SelectedAnc { get => _selectedAnc; set { if (SetProperty(ref _selectedAnc, value)) { if (!_displaying) { _pendingAnc = true; _pendingAncKey = value?.Key; _pendingAncPrimary = value?.Key >> 8; _ancVersion++; } NotifyCommands(); } } }
    public UiOption? SelectedPreset { get => _selectedPreset; set { if (SetProperty(ref _selectedPreset, value)) { if (!_displaying) { _pendingPreset = true; _pendingPresetKey = value?.Key; _presetVersion++; } NotifyCommands(); } } }
    public UiOption? SelectedSpatial { get => _selectedSpatial; set { if (SetProperty(ref _selectedSpatial, value)) { if (!_displaying) { _pendingSpatial = true; _pendingSpatialKey = value?.Key; _spatialVersion++; } NotifyCommands(); } } }
    public IReadOnlyList<UiEqEntry> EqEntries => _eqEntries;
    public UiEqEntry? SelectedEqEntry => _selectedEqEntry;
    public string EqName { get => _eqName; set { if (SetProperty(ref _eqName, value) && !_displaying) SetEqDirty(true); } }
    public ObservableCollection<EqBandViewModel> Bands { get; }
    public bool EqDirty => _eqDirty;
    public string EqDirtyText => EqDirty ? "有未保存的调整" : "";
    public string EqHint => IsDemoSelected ? "调节各频段查看布局，演示均衡器只保留到关闭界面。"
        : PresentationCached ? "显示上次读取的均衡器，连接耳机后才可编辑。"
        : EqDirty && !EqLayoutCompatible ? "耳机的均衡器频段已变化，请先重置当前草稿后再保存。"
        : _snapshot?.CanSetCustomEqualizer == true ? "调节各频段，然后将整组设置写入耳机。" : "连接支持自定义 EQ 的耳机后可编辑。";

    public IAsyncRelayCommand RefreshDevicesCommand { get; }
    public IAsyncRelayCommand ConnectCommand { get; }
    public IAsyncRelayCommand RefreshStateCommand { get; }
    public IAsyncRelayCommand ReconnectCommand { get; }
    public IAsyncRelayCommand ApplyAncCommand { get; }
    public IAsyncRelayCommand ApplyPresetCommand { get; }
    public IAsyncRelayCommand ApplySpatialCommand { get; }
    public IAsyncRelayCommand<bool> ToggleDualCommand { get; }
    public IAsyncRelayCommand<bool> ToggleGameCommand { get; }
    public IAsyncRelayCommand<bool> ToggleHiResCommand { get; }
    public IAsyncRelayCommand NewEqCommand { get; }
    public IRelayCommand ResetEqCommand { get; }
    public IAsyncRelayCommand SaveEqCommand { get; }
    public IAsyncRelayCommand UseEqCommand { get; }
    private T Register<T>(T command) where T : IRelayCommand { _commands.Add(command); return command; }
    private void NotifyCommands() { foreach (var command in _commands) command.NotifyCanExecuteChanged(); }
    private void NotifyState()
    {
        foreach (var control in _featureSections.SelectMany(s => s.Controls).Concat(_equalizerFeatures)) control.RefreshPermissions();
        foreach (var property in PresentationProperties)
        {
            var value = property.GetValue(this);
            if (!_published.TryGetValue(property.Name, out var old) || !Equals(old, value))
            { _published[property.Name] = value; OnPropertyChanged(property.Name); }
        }
        NotifyCommands();
    }

    public async Task InitializeAsync(IReadOnlyList<UiDevice>? devices = null)
    {
        await SubscribeToBackendAsync();
        await EnumerateAsync(devices);
        await LoadResourcePreferencesAsync();
        if (_preview && SelectedDevice is not null) await ConnectOrDisconnectAsync();
    }

    private async Task RunAsync(string status, Func<Task> action, bool silent = false)
    {
        if (Closing || _lifecyclePending) return;
        _lifecyclePending = true; _operations.CancelQueuedWrites(); NotifyState();
        try
        {
            await _operations.RunAsync(async () =>
            {
                if (!silent) SetStatus(status);
                await action();
            }, OperationCoordinator.Lifecycle, _lifetime.Token);
        }
        catch (OperationCanceledException) { }
        catch { if (!Closing) { InvalidateSnapshot(); SetStatus("无法读取耳机，请确认蓝牙连接后重试。", true); } }
        finally { _lifecyclePending = false; NotifyState(); }
    }

    private Task EnumerateAsync(IReadOnlyList<UiDevice>? initialDevices = null) => RunAsync("正在查找已配对耳机…", async () =>
    {
        var devices = initialDevices ?? await _service.EnumerateAsync(_lifetime.Token);
        var previousIdentity = SelectedDevice?.Identity;
        var disconnect = EventService is null && _sessionIdentity is not null && !IsDemoSelected
            && ChooseDevice(ComposeDevices(devices), previousIdentity)?.Identity != previousIdentity;
        if (disconnect) await _service.DisconnectAsync(_lifetime.Token);
        var changed = PresentDevices(devices, previousIdentity);
        if (EventService is not null || IsDemoSelected)
        {
            if (changed) await AttachSelectedBackendAsync(); else if (_snapshot is null) PresentSelectedBackendSnapshot();
            NotifyState(); return;
        }
        SetStatus(_service is UnsupportedPlatformService ? UnsupportedPlatformService.Message : devices.Count == 0 ? "未发现已配对耳机" : _snapshot is { Connected: true } ? "设备列表已刷新 · 耳机保持连接" : "选择耳机后连接", _service is UnsupportedPlatformService);
        NotifyState();
    });

    private async Task RefreshDevicesAsync()
    {
        await RunAsync("正在查找已配对耳机…", async () =>
        {
            if (EqDirty && !IsDemoSelected && !await ConfirmAsync("刷新设备", "刷新设备后，若连接变化，未保存的均衡器调整会被丢弃。继续刷新吗？")) return;
            // Enumerate directly because the operation gate is already held.
            var devices = await _service.EnumerateAsync(_lifetime.Token);
            var previousIdentity = SelectedDevice?.Identity;
            var disconnect = EventService is null && _sessionIdentity is not null && !IsDemoSelected
                && ChooseDevice(ComposeDevices(devices), previousIdentity)?.Identity != previousIdentity;
            if (disconnect) await _service.DisconnectAsync(_lifetime.Token);
            var changed = PresentDevices(devices, previousIdentity);
            if (EventService is not null || IsDemoSelected)
            {
                if (changed) await AttachSelectedBackendAsync(); else if (_snapshot is null) PresentSelectedBackendSnapshot();
                NotifyState(); return;
            }
            SetStatus(_service is UnsupportedPlatformService ? UnsupportedPlatformService.Message : devices.Count == 0 ? "未发现已配对耳机" : _snapshot is { Connected: true } ? "设备列表已刷新 · 耳机保持连接" : "选择耳机后连接", _service is UnsupportedPlatformService);
            NotifyState();
        });
    }

    public Task SelectDeviceAsync(UiDevice? device) => RunAsync("正在切换耳机…", async () =>
    {
        await ChangeSelectedDeviceAsync(device, "切换耳机", "切换耳机会丢弃尚未保存的均衡器调整。继续切换吗？");
    });

    private Task ConnectOrDisconnectAsync() => RunAsync("正在连接耳机并读取状态…", async () =>
    {
        if (IsDemoSelected) { AttachDemo(); return; }
        if (EventService is not null && SelectedDevice is null) return;
        if (SessionActive)
        {
            if (EqDirty && !await ConfirmAsync("断开客户端", EventService is not null
                ? "暂停此设备的控制连接会丢弃尚未保存的均衡器调整。继续吗？" : "断开客户端会丢弃尚未保存的均衡器调整。继续吗？")) return;
            await EndSessionAsync();
            return;
        }
        await StartSessionAsync();
    });

    /// <summary>Ends the selected device's control session, waits <see cref="ReconnectDelay"/> and starts it again.</summary>
    /// <remarks>
    /// Nothing is replayed: the new session reads the device like any connection. A paused or closed session simply
    /// connects. The lifecycle gate is held throughout, so device switching and further requests wait for it.
    /// </remarks>
    private Task ReconnectAsync() => RunAsync("正在重新连接耳机…", async () =>
    {
        if (IsDemoSelected || SelectedDevice is not { } selected) return;
        if (SessionActive)
        {
            if (EqDirty && !await ConfirmAsync("重新连接", "重新连接会丢弃尚未保存的均衡器调整。继续吗？")) return;
            await EndSessionAsync();
            if (EventService is not null) _reconnectPaused = selected.Identity;
            SetStatus("正在重新连接耳机…");
            await Task.Delay(ReconnectDelay, _lifetime.Token);
            _reconnectPaused = null;
            if (SelectedDevice?.Identity != selected.Identity)
            {
                // The backend keeps a pause until told otherwise; resume the device even though it is no longer shown.
                if (EventService is { } events) await events.ResumeAsync(selected.Identity, _lifetime.Token);
                return;
            }
        }
        await StartSessionAsync();
        Log("已重新发起耳机连接。");
    });

    /// <summary>
    /// The backend keeps a pause after this window exits, so closing between a reconnect's pause and resume resumes
    /// the device before the service is disposed.
    /// </summary>
    private async Task ResumeInterruptedReconnectAsync()
    {
        if (_reconnectPaused is not { } identity || EventService is not { } events) return;
        _reconnectPaused = null;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try { await events.ResumeAsync(identity, timeout.Token); } catch { }
    }

    /// <summary>An unpaused backend peer, or an open direct connection, that disconnecting would end.</summary>
    private bool SessionActive => EventService is not null
        ? _snapshot is { MonitoringState: not MonitoringState.Paused } : _sessionIdentity is not null;

    private async Task EndSessionAsync()
    {
        AdvanceSession();
        if (EventService is { } events && SelectedDevice is { } selected)
        {
            ResetSelection();
            _sessionIdentity = selected.Identity;
            AdoptRead(await events.PauseAsync(selected.Identity, _lifetime.Token));
            return;
        }
        await _service.DisconnectAsync(_lifetime.Token); _sessionIdentity = null; ResetSelection();
        SetStatus("客户端已断开 · 蓝牙配对保留"); Log("已断开客户端连接。");
    }

    private async Task StartSessionAsync()
    {
        if (SelectedDevice is not { } device) return;
        AdvanceSession();
        if (EventService is { } events)
        {
            _sessionIdentity = device.Identity;
            AdoptRead(await events.ResumeAsync(device.Identity, _lifetime.Token));
            return;
        }
        var snapshot = await _service.ConnectAsync(device.Identity, _lifetime.Token);
        if (snapshot.Identity != device.Identity) throw new InvalidOperationException();
        _sessionIdentity = device.Identity; DisplaySnapshot(snapshot);
        SetStatus(snapshot.Failure is null && snapshot.Connected ? "已连接 · 状态会自动刷新" : "连接尚未就绪，请检查耳机状态。", snapshot.Failure is not null);
        Log(snapshot.Failure is null && snapshot.Connected ? "已连接耳机，读取当前设置。" : "连接尚未就绪，设置暂不可用。");
    }

    public Task PollAsync() => EventService is null && CanRefreshState && !Busy ? RefreshStateAsync() : Task.CompletedTask;
    private Task RefreshStateAsync()
    {
        if (!CanRefreshState) return Task.CompletedTask;
        if (_refreshing) return _refreshTask ?? Task.CompletedTask;
        _refreshing = true; NotifyState();
        return _refreshTask = RefreshCycleAsync(_sessionEpoch, _sessionWork.Token);
    }

    private async Task RefreshCycleAsync(long epoch, CancellationToken token)
    {
        try
        {
            var scopes = EventService is not null || IsDemoSelected ? new[] { RefreshScope.Full }
                : new[] { RefreshScope.Identity, RefreshScope.Features, RefreshScope.Anc,
                    RefreshScope.Equalizer, RefreshScope.Battery, RefreshScope.Information };
            foreach (var scope in scopes)
            {
                if (epoch != _sessionEpoch || Closing || _lifecyclePending) break;
                var valid = false;
                await _operations.RunAsync(async () =>
                {
                    if (epoch != _sessionEpoch || Closing) return;
                    var snapshot = await ReadSelectedAsync(scope, EventService is not null ? token : _lifetime.Token);
                    if (epoch != _sessionEpoch || Closing) return;
                    valid = AdoptRead(snapshot);
                }, OperationCoordinator.Refresh, token);
                if (!valid) break;
            }
        }
        catch (OperationCanceledException) { }
        catch { if (epoch == _sessionEpoch && !Closing) { InvalidateSnapshot(); SetStatus("耳机状态未确认，请刷新或重新连接。", true); } }
        finally { _refreshing = false; NotifyState(); }
    }

    private bool AdoptRead(UiSnapshot snapshot)
    {
        if (snapshot.Identity != _sessionIdentity || _sessionIdentity != SelectedDevice?.Identity)
        { InvalidateSnapshot(); SetStatus("连接设备已变化，请重新选择并连接耳机。", true); return false; }
        if (!DisplaySnapshot(snapshot)) return HasVerifiedSnapshot;
        if (IsDemoSelected) { ShowDemoFeedback(""); return true; }
        if (EventService is not null)
        {
            var message = snapshot.MonitoringState switch
            {
                MonitoringState.Paused => "已暂停控制连接 · 三击底栏中的当前设备可重新连接",
                MonitoringState.Connecting => "正在建立耳机控制连接…",
                MonitoringState.WaitingForConnection => snapshot.ObservationFresh ? "已收到耳机广播 · 等待蓝牙连接" : "等待耳机连接 · 后台继续监听",
                MonitoringState.Unsupported => "该型号的设置尚未适配",
                MonitoringState.Unavailable => string.IsNullOrWhiteSpace(snapshot.Failure)
                    ? "耳机控制连接暂不可用，请双击底栏中的当前设备刷新，或三击重新连接。"
                    : $"耳机控制连接暂不可用：{snapshot.Failure.Trim()} 请双击底栏中的当前设备刷新，或三击重新连接。",
                _ => HasVerifiedSnapshot ? "已连接 · 接收耳机状态更新" : snapshot.ObservationFresh ? "已收到耳机广播 · 设置暂不可用" : "耳机当前状态未确认"
            };
            SetStatus(message, snapshot.MonitoringState == MonitoringState.Unavailable);
            return HasVerifiedSnapshot;
        }
        var valid = snapshot.Connected && snapshot.Failure is null;
        SetStatus(valid ? "已连接 · 状态已刷新" : "耳机状态未确认，暂时不能修改设置。", !valid);
        return valid;
    }

    private void AdvanceSession()
    {
        ClearQueuedChoices(preserveUnsubmittedDraft: true);
        _sessionEpoch++;
        _sessionWork.Cancel(); _sessionWork.Dispose(); _sessionWork = new();
        _operations.CancelQueuedWrites(); _pending.Clear();
    }

    private bool DisplaySnapshot(UiSnapshot snapshot)
    {
        if (!CacheSnapshot(snapshot)) return false;
        if (snapshot.Identity != _sessionIdentity
            || EventService is null && (snapshot.Failure is not null || !snapshot.Connected))
        { InvalidateSnapshot(); return false; }
        _snapshot = snapshot;
        // A current backend snapshot can combine cached settings with fresh BLE
        // telemetry. Keep that observation, but IsVerifiedControl rejects its cache flag.
        _liveSnapshot = snapshot;
        _displaying = true;
        UpdateResourcePresentation(snapshot);
        _title = snapshot.DeviceName;
        _subtitle = IsDemoSelected ? "演示设备"
            : snapshot.PresentationCached ? snapshot.MonitoringState == MonitoringState.Connecting ? "正在连接…" : LastConnectedText(snapshot)
            : IsVerifiedControl(snapshot) ? "已连接"
            : snapshot.MonitoringState switch
            {
                MonitoringState.Unsupported => "该型号尚未适配",
                MonitoringState.Paused => "控制连接已暂停",
                MonitoringState.Connecting => "正在连接…",
                MonitoringState.Unavailable => "控制连接不可用",
                _ when EventService is not null && snapshot.ObservationFresh => "已收到耳机广播",
                MonitoringState.WaitingForConnection => "等待耳机连接",
                _ => "控制连接未就绪"
            };
        var anc = _pendingAnc ? _pendingAncKey : snapshot.Anc;
        var preset = _pendingPreset ? _pendingPresetKey : snapshot.Equalizer;
        var spatial = _pendingSpatial ? _pendingSpatialKey : snapshot.Spatial;
        var unverified = EventService is not null && !IsVerifiedControl(snapshot);
        _ancOptions = unverified && _pendingAnc && snapshot.AncOptions.Count == 0
            ? _ancOptions : RetainOptions(_ancOptions, snapshot.AncOptions);
        _presetOptions = unverified && _pendingPreset && snapshot.EqualizerOptions.Count == 0
            ? _presetOptions : RetainOptions(_presetOptions, snapshot.EqualizerOptions);
        _spatialOptions = unverified && _pendingSpatial && snapshot.SpatialOptions.Count == 0
            ? _spatialOptions : RetainOptions(_spatialOptions, snapshot.SpatialOptions);
        // Publish the new item lists first: two-way ComboBox bindings may clear an old selection
        // when ItemsSource changes. Restore the explicit protocol keys only after that notification.
        NotifyState();
        SelectedAnc = _ancOptions.FirstOrDefault(x => x.Key == anc);
        SelectedPreset = _presetOptions.FirstOrDefault(x => x.Key == preset);
        SelectedSpatial = _spatialOptions.FirstOrDefault(x => x.Key == spatial);
        var selectedId = _eqDraftId;
        _eqEntries = RetainEntries(_eqEntries, snapshot.EqualizerEntries);
        NotifyState();
        if (!EqDirty)
        {
            var entry = _eqEntries.FirstOrDefault(x => x.Id == selectedId) ?? _eqEntries.FirstOrDefault(x => x.Selected) ?? _eqEntries.FirstOrDefault();
            if (!ReferenceEquals(entry, _selectedEqEntry)) LoadEntry(entry);
        }
        else _selectedEqEntry = _eqEntries.FirstOrDefault(x => x.Id == selectedId);
        NotifyState(); _displaying = false;
        return true;
    }

    private void InvalidateSnapshot(bool clearPresentation = false)
    {
        ClearQueuedChoices(preserveUnsubmittedDraft: true);
        _liveSnapshot = null;
        _snapshot = !clearPresentation && EventService is not null && _snapshot is { } previous
            ? ReadOnlyPresentation(previous) : null;
        _displaying = true;
        if (_snapshot is not null)
        {
            if (!_pendingAnc)
            {
                var primary = _snapshot.AncPrimary ?? (_snapshot.Anc >> 8);
                _selectedAncMode = _ancModes.FirstOrDefault(mode => mode.Key == primary);
                _selectedAncSubmode = _selectedAncMode?.Options.FirstOrDefault(option => option.Key == _snapshot.Anc);
                SelectedAnc = _ancOptions.FirstOrDefault(option => option.Key == _snapshot.Anc);
            }
            _subtitle = LastConnectedText(_snapshot);
            NotifyState(); _displaying = false; return;
        }
        if (!_pendingAnc) { SelectedAnc = null; _selectedAncMode = null; _selectedAncSubmode = null; }
        if (!_pendingPreset) SelectedPreset = null;
        if (!_pendingSpatial) SelectedSpatial = null;
        _subtitle = SelectedDevice is null ? "未连接" : "状态未确认";
        NotifyState(); _displaying = false;
    }

    // The cached presentation time is the Host's last verified-control sample for this device.
    private static string LastConnectedText(UiSnapshot snapshot) => snapshot.PresentationSampleTime is { } time
        ? "上次连接 " + time.ToLocalTime().ToString("yyyy/M/d HH:mm", CultureInfo.InvariantCulture) : "未连接";

    private void ResetSelection()
    {
        ClearQueuedChoices();
        _pendingAnc = _pendingPreset = _pendingSpatial = false;
        _pendingAncKey = _pendingPresetKey = _pendingSpatialKey = null;
        _pendingAncPrimary = null;
        _ancOptions = []; _presetOptions = []; _spatialOptions = []; _actionWarnings.Clear();
        _ancModes = []; _selectedAncMode = null; _selectedAncSubmode = null;
        _featureSections = []; _equalizerFeatures = []; _featureContext = "";
        _hearingProfiles = []; _selectedHearingProfile = null;
        _eqEntries = []; _snapshot = _liveSnapshot = null; LoadEntry(null); InvalidateSnapshot(clearPresentation: true);
    }

    private Task ApplyOptionAsync(string kind, UiOption? option) => option is null ? Task.CompletedTask
        : kind == "Anc" ? QueueAncActionAsync(new(kind, option.Key))
        : kind == "Equalizer" ? QueueEqActionAsync(new(kind, option.Key)) : ApplyAsync(new(kind, option.Key));
    private bool Supports(UiAction action) => _liveSnapshot is { } s && !s.PresentationCached && action.Kind switch
    {
        "Anc" => s.CanSetAnc && s.AncOptions.Any(x => x.Key == action.Value),
        "AncMode" => s.CanSetAncMode && AncModes.Any(x => x.Key == action.Value),
        "Equalizer" => s.CanSetEqualizer && s.EqualizerOptions.Any(x => x.Key == action.Value),
        "Spatial" => s.CanSetSpatial && s.SpatialOptions.Any(x => x.Key == action.Value),
        "DualConnection" => s.CanSetDualConnection && s.DualConnection is not null,
        "GameMode" => s.CanSetGameMode && s.GameMode is not null,
        "HiRes" => s.CanSetHiRes && s.HiRes is not null,
        "CustomEqualizer" => s.CanSetCustomEqualizer && action.EqualizerEntry is { } entry
            && (s.EqualizerFrequencies.Count == 0 || entry.Bands.Select(band => band.FrequencyHz).SequenceEqual(s.EqualizerFrequencies)),
        "CustomSelect" => s.CanSetCustomEqualizer && s.EqualizerEntries.Any(x => x.Id == action.Value && x.Id > 0),
        "Feature" => s.FeatureSections.SelectMany(x => x.Controls).Any(x => x.Id == action.FeatureId && x.CanWrite &&
            (x.Kind switch
            {
                "Text" => true,
                "Combo" or "Radio" => x.Options?.Any(o => o.Key == action.Value) == true,
                "Switch" => action.Value is 0 or 1,
                "Slider" => action.Value >= x.Minimum && action.Value <= x.Maximum,
                _ => false
            })),
        _ => false
    };

    public async Task SelectEqEntryAsync(UiEqEntry? entry)
    {
        if (Closing || _displaying) return;
        if (entry?.Id == SelectedEqEntry?.Id) return;
        if (entry is not null && !EqEntries.Any(x => x.Id == entry.Id)) return;
        var epoch = _sessionEpoch; var token = _sessionWork.Token;
        try
        {
            if (EqDirty && !IsDemoSelected && !await ConfirmAsync("切换均衡器", "要丢弃尚未保存的均衡器调整吗？", token)) return;
            if (Closing || epoch != _sessionEpoch) return;
            var latest = entry is null ? null : EqEntries.FirstOrDefault(x => x.Id == entry.Id);
            if (entry is not null && latest is null) return;
            LoadEntry(latest);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested || Closing) { }
    }

    private async Task NewEqAsync()
    {
        if (Closing) return;
        if (!CanCreateEq) return;
        var epoch = _sessionEpoch; var token = _sessionWork.Token;
        try
        {
            if (EqDirty && !IsDemoSelected && !await ConfirmAsync("新建均衡器", "新建均衡器会丢弃尚未保存的调整。继续吗？", token)) return;
            if (Closing || epoch != _sessionEpoch) return;
            LoadEntry(null); SetEqDirty(true);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested || Closing) { }
    }

    private Task SaveEqAsync()
    {
        var name = EqName.Trim();
        if (name.Length is < 1 or > 30 || name.Any(char.IsControl)) { SetStatus("请输入有效的均衡器名称（1 至 30 个字符）。", true); return Task.CompletedTask; }
        if (!EqDirty) return Task.CompletedTask;
        var entry = new UiEqEntry(_eqDraftId ?? 0, name, Bands[0].Minimum, Bands[0].Maximum,
            SelectedEqEntry?.Selected ?? false, Bands.Select(b => new UiBand(b.FrequencyHz, b.Gain)).ToArray());
        return ApplyAsync(new("CustomEqualizer", entry.Id, entry));
    }

    private Task UseEqAsync() => SelectedEqEntry is { Id: > 0 } entry && !EqDirty
        ? QueueEqActionAsync(new("CustomSelect", entry.Id)) : Task.CompletedTask;

    private void LoadEntry(UiEqEntry? entry)
    {
        var displaying = _displaying; _displaying = true; _selectedEqEntry = entry; _eqDraftId = entry?.Id;
        var frequencies = entry?.Bands.Select(b => b.FrequencyHz).ToArray()
            ?? (_snapshot?.EqualizerFrequencies.Count > 0 ? _snapshot.EqualizerFrequencies.ToArray() : Frequencies);
        EnsureBands(frequencies);
        _draftIdentity++; _eqVersion++; EqName = entry?.Name ?? "我的均衡器";
        foreach (var band in Bands)
        {
            band.Minimum = entry?.MinimumGain ?? -6; band.Maximum = entry?.MaximumGain ?? 6;
            band.Gain = entry?.Bands.FirstOrDefault(b => b.FrequencyHz == band.FrequencyHz)?.Gain ?? 0;
        }
        SetEqDirty(false); _displaying = displaying; NotifyState();
    }

    private void BandChanged(object? sender, PropertyChangedEventArgs e) { if (!_displaying && e.PropertyName == nameof(EqBandViewModel.Gain)) SetEqDirty(true); }
    private void SetEqDirty(bool value) { _eqDirty = value; if (value) _eqVersion++; NotifyState(); }
    private async Task<bool> ConfirmAsync(string title, string message, CancellationToken cancellation = default)
    {
        return await Dialogs.RunAsync(token => _confirmations.ConfirmAsync(title, message, token), cancellation);
    }
    private void SetStatus(string text, bool warning = false)
    {
        if (_status != text || _statusWarning != warning)
            HeyMelody.Diagnostics.DiagnosticLog.Info("client.ui", "status", text, new { warning });
        _status = text; _statusWarning = warning; NotifyState();
    }
    private void Log(string text)
    {
        HeyMelody.Diagnostics.DiagnosticLog.Info("client.ui", "message", text);
        _logs.Add($"{DateTime.Now:HH:mm:ss}  {Sanitize(text)}");
        if (_logs.Count > 80) _logs.RemoveAt(0);
        OnPropertyChanged(nameof(LogText));
    }
    private string Sanitize(string text)
    {
        foreach (var identity in Devices.Select(d => d.Identity).Append(_sessionIdentity ?? "").Distinct())
            if (identity.Length != 0) text = text.Replace(identity, "[设备标识]", StringComparison.Ordinal);
        var clean = Regex.Replace(text, @"(?i)\b(?:[0-9a-f]{2}[:-]){5}[0-9a-f]{2}\b|\b[0-9a-f]{12}\b|(?:BTHENUM|BTHLEDEVICE|SWD)\\\S+", "[设备标识]");
        return string.Concat(clean.Where(c => !char.IsControl(c))).Trim();
    }
    private static string Battery(int? value) => value is >= 0 and <= 100 ? $"{value}%" : "—";
    private static string Charge(bool? charging, int? battery) => charging == true ? "正在充电" : battery is null ? "尚未读取" : charging == false ? "未在充电" : "充电状态未确认";
    private static string BooleanState(bool? value) => value is null ? "未确认" : value.Value ? "已开启" : "已关闭";

    public async Task<bool> RequestCloseAsync()
    {
        if (_closing) return true;
        if (_closeDialogRunning) return false;
        SuspendActionsForClose(); _closeDialogRunning = true;
        try
        {
            if (EqDirty && !IsDemoSelected && !_preview && !_backendExitApproved && !await ConfirmAsync("关闭客户端", "均衡器还有未保存的调整，确定关闭吗？")) return false;
            _closing = true; _lifetime.Cancel(); NotifyState();
            await DisposeAsync(); return true;
        }
        finally { _closeDialogRunning = false; if (!_closing) _closeRequested = false; NotifyState(); }
    }

    public void SuspendActionsForClose()
    {
        if (_closeRequested || _closing) return;
        _closeRequested = true; AdvanceSession(); NotifyState();
    }

    public ValueTask DisposeAsync() => new(_disposeTask ??= DisposeCoreAsync());
    private async Task DisposeCoreAsync()
    {
        _closing = true; _lifetime.Cancel();
        await ResumeInterruptedReconnectAsync();
        try { await _service.DisposeAsync(); } catch { }
        await _operations.WhenIdle;
        ClearDeviceManagement();
        _devices = _devices.Where(device => !IsDemoDevice(device)).ToArray();
        if (IsDemoSelected) { _selectedDevice = null; _sessionIdentity = null; ResetSelection(); }
        _demoDevice = null; _demoSnapshot = null; _demoEqDraft = null;
        foreach (var band in Bands) band.PropertyChanged -= BandChanged;
        NotifyState();
    }

    private static IReadOnlyList<UiOption> RetainOptions(IReadOnlyList<UiOption> previous, IReadOnlyList<UiOption> incoming)
    {
        var normalized = incoming.Select(value => previous.FirstOrDefault(old => old == value) ?? value).ToArray();
        return previous.Count == normalized.Length && previous.Zip(normalized).All(pair => ReferenceEquals(pair.First, pair.Second)) ? previous : normalized;
    }

    private static bool SameEntry(UiEqEntry first, UiEqEntry second) => first.Id == second.Id && first.Name == second.Name
        && first.MinimumGain == second.MinimumGain && first.MaximumGain == second.MaximumGain
        && first.Selected == second.Selected && first.Bands.SequenceEqual(second.Bands);

    private static IReadOnlyList<UiEqEntry> RetainEntries(IReadOnlyList<UiEqEntry> previous, IReadOnlyList<UiEqEntry> incoming)
    {
        var normalized = incoming.Select(value => previous.FirstOrDefault(old => SameEntry(old, value)) ?? value).ToArray();
        return previous.Count == normalized.Length && previous.Zip(normalized).All(pair => ReferenceEquals(pair.First, pair.Second)) ? previous : normalized;
    }
}
