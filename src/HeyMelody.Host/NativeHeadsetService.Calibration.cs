using HeyMelodyWindows.App;
using HeyMelodyWindows.Calibration;
using HeyMelodyWindows.Protocol;

using HeyMelody.Diagnostics;

namespace HeyMelodyWindows.Host;

public sealed partial class NativeHeadsetService
{
    private readonly CalibrationHistoryStore _hearingHistory = new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HeyMelody", "hearing"));
    private CalibrationEngine? _calibration;
    private CalibrationDeviceContext? _calibrationContext;
    private UiCalibrationState? _calibrationState;
    private long _calibrationRevision;
    private Task _calibrationBackground = Task.CompletedTask;
    private IReadOnlyList<UiHearingProfile> _hearingProfiles = [];
    private int? _currentHearingUid;
    private bool _suppressCalibrationEvents, _savingHearing;
    private readonly object _hearingRefreshGate = new();
    private CancellationTokenSource? _hearingRefreshLifetime;
    private Task _hearingRefreshTask = Task.CompletedTask;
    private bool _hearingRefreshRunning, _hearingRefreshNeeded;
    public event Action<UiCalibrationState>? CalibrationChanged;

    private async Task InitializeCalibrationAsync(CancellationToken token)
    {
        if (_client is null || !_client.IsConnected) return;
        _auditionClosing = false;
        if (_stableKey.Length == 0 && _selectedAddress is { } address) _stableKey = DeviceWhitelistStore.KeyFor(address);
        _calibrationContext = _client.CalibrationContext(_stableKey, _generation);
        _calibration = new(_client, _calibrationContext, _hearingHistory);
        _calibration.Changed += EngineChanged;
        _hearingRefreshLifetime = new();
        _client.Notification += OnHearingNotification;
        if (CalibrationAvailable("hearing"))
        {
            try { await ReadHearingProfilesAsync(token); }
            catch (Exception error) when (!token.IsCancellationRequested)
            {
                DiagnosticLog.Error("host.hearing", "profileInitializationFailed", "Initial hearing profiles could not be read", error);
                _hearingProfiles = [];
            }
        }
    }
    private void OnHearingNotification(Packet packet)
    {
        if (packet.Command != 0x0503 || !CalibrationAvailable("hearing") || _savingHearing
            || _calibrationState is { Stage: not ("Completed" or "Failed" or "Cancelled") }) return;
        try { if (!Daily.DailyParsers.ParseFeatures([0, .. packet.Payload]).ContainsKey(0x0B)) return; }
        catch (FormatException) { return; }
        lock (_hearingRefreshGate)
        {
            if (_hearingRefreshLifetime is not { IsCancellationRequested: false } lifetime || _client is not { } client) return;
            _hearingRefreshNeeded = true;
            if (_hearingRefreshRunning) return;
            _hearingRefreshRunning = true;
            // Never query on the Bluetooth reader callback. A phone's feature push
            // invalidates saved-profile metadata even when the enabled value is unchanged.
            _hearingRefreshTask = Task.Run(async () =>
            {
                while (true)
                {
                    lock (_hearingRefreshGate)
                    {
                        if (!_hearingRefreshNeeded || lifetime.IsCancellationRequested)
                        { _hearingRefreshRunning = false; return; }
                        _hearingRefreshNeeded = false;
                    }
                    try
                    {
                        if (_calibrationState is { Stage: not ("Completed" or "Failed" or "Cancelled") }) continue;
                        await ReadHearingProfilesAsync(lifetime.Token);
                        await client.RefreshAsync(Daily.DailyRefreshScope.Features, lifetime.Token);
                    }
                    catch (Exception error)
                    {
                        if (error is not OperationCanceledException)
                            DiagnosticLog.Error("host.hearing", "notificationRefreshFailed", "Hearing notification refresh failed; existing profiles remain", error);
                        /* Keep existing profiles; an explicit refresh can retry the GET. */
                    }
                }
            });
        }
    }
    private bool CalibrationAvailable(string kind)
    {
        if (_calibrationContext is null || _client is null) return false;
        var c = _client.CalibrationContext(_stableKey, _generation);
        bool Commands(params ushort[] ids) => ids.All(c.Commands.Contains);
        return kind == "fit" ? c.Fit && Commands(0x0109, 0x0405)
            : (c.HearingV1 || c.HearingV2) && Commands(0x0109, 0x040D, 0x040E, 0x0115, 0x0116, 0x0118, 0x0411, 0x010D, 0x0403)
                && (!c.EarScan || Commands(0x011E, 0x011F, 0x0415));
    }
    internal UiCalibrationState CreateCalibration(string handle, string kind)
    {
        if (kind is not ("fit" or "hearing") || !CalibrationAvailable(kind) || handle.Length != 32)
            throw new InvalidOperationException("当前设备不支持此测试，或必要的控制指令不可用。");
        if (_calibrationState is { Stage: not ("Completed" or "Failed" or "Cancelled") })
            throw new InvalidOperationException("请先完成或取消当前测试。");
        _calibrationState = new() { Handle = handle, Identity = _selected!, Generation = _generation,
            Revision = ++_calibrationRevision, Workflow = kind, Stage = "Ready",
            HearingVersion = _calibrationContext!.HearingV2 ? "V2" : "V1", SupportsEarScan = _calibrationContext.EarScan };
        _client!.SetCalibrationActive(true);
        return _calibrationState;
    }
    internal UiCalibrationState GetCalibration(string handle) => _calibrationState is { } state && state.Handle == handle
        ? state : throw new InvalidOperationException("此测试已结束或连接已变化。");
    internal async Task<UiCalibrationState> AdvanceCalibrationAsync(string handle, long revision, UiCalibrationInput input, CancellationToken token)
    {
        var state = GetCalibration(handle);
        if (revision != state.Revision) throw new InvalidOperationException("测试进度已变化，请按当前步骤操作。");
        if (_calibration is null) throw new InvalidOperationException("耳机连接已变化。");
        switch (input.Action)
        {
            case "start":
                if (state.Stage is not ("Ready" or "Failed") && !(state.Workflow == "fit" && state.Stage == "Completed")) throw new InvalidOperationException("测试已经启动。");
                if (state.Workflow == "fit") StartBackground(() => _calibration.RunFitAsync());
                else if (state.SupportsEarScan) StartBackground(() => _calibration.StartEarScanAsync());
                else await _calibration.BeginHearingAsync(_calibrationContext!.HearingV2 ? HearingVersion.V2 : HearingVersion.V1, token);
                break;
            case "scan":
                if (state.Workflow != "hearing" || !state.SupportsEarScan || state.Stage is not ("Ready" or "ScanCompleted")) throw new InvalidOperationException();
                StartBackground(() => _calibration.StartEarScanAsync()); break;
            case "answer": await _calibration.AnswerAsync(input.Heard ?? throw new InvalidDataException(), token); break;
            case "level": await _calibration.SetLevelAsync(input.LevelIndex ?? throw new InvalidDataException(), input.Released, token); break;
            case "next":
                if (state.Stage == "ScanCompleted") await _calibration.BeginHearingAsync(_calibrationContext!.HearingV2 ? HearingVersion.V2 : HearingVersion.V1, token);
                else await _calibration.ContinueAsync(token);
                break;
            case "previous": await _calibration.PreviousAsync(token); break;
            case "pause": await _calibration.PauseHearingAsync(token); break;
            case "resume": await _calibration.ResumeAsync(token); break;
            case "retry-curves": await _calibration.ReadCurvesAsync(token); break;
            case "preview-original": await PreviewAuditionAsync(CalibrationPreviewMode.Original, token); break;
            case "preview-enhanced": await PreviewAuditionAsync(CalibrationPreviewMode.Enhanced, token); break;
            case "preview-stop": await PreviewAuditionAsync(CalibrationPreviewMode.Stopped, token); break;
            case "save":
                await StopLocalAuditionAsync();
                int? replace = null;
                var history = await _hearingHistory.ReadAsync(_stableKey, token);
                if (history.Count >= 10 && input.AllowReplacement)
                    replace = history.Where(x => x.Uid != _currentHearingUid).OrderBy(x => x.SavedAtUtc).FirstOrDefault()?.Uid;
                _savingHearing = true;
                try
                {
                    await _calibration.SaveHearingAsync(input.Name ?? "", input.AllowReplacement, token, replace);
                    await ReadHearingProfilesAsync(token);
                    if (_client is not null)
                    {
                        var after = Map(await _client.RefreshAsync(Daily.DailyRefreshScope.Features, token));
                        SnapshotChanged?.Invoke(after);
                        if (after.HearingEnabled != true) throw new InvalidOperationException("配置已保存，启用状态尚未确认，请刷新后核实。");
                    }
                    _savingHearing = false; EngineChanged(_calibration.State);
                }
                catch (Exception) when (_calibration.State.Stage == CalibrationStage.Completed)
                {
                    // The engine already verified the write and enable. A failed UI
                    // metadata refresh must not offer another profile write.
                    _calibrationState = GetCalibration(handle) with { Stage = "Completed", Revision = ++_calibrationRevision,
                        Message = "配置已保存并启用，主界面状态尚未刷新。请返回主界面后刷新核实。" };
                    _client?.SetCalibrationActive(false);
                    CalibrationChanged?.Invoke(_calibrationState);
                }
                finally { _savingHearing = false; }
                break;
            case "cancel": return await CancelCalibrationAsync(handle, token);
            default: throw new InvalidDataException();
        }
        return GetCalibration(handle);
    }
    private void StartBackground(Func<Task<CalibrationSnapshot>> action)
    {
        if (!_calibrationBackground.IsCompleted) throw new InvalidOperationException("测试正在进行。");
        _calibrationBackground = RunAsync();
        async Task RunAsync()
        {
            try { await action(); }
            catch (Exception) { /* The engine publishes the failure and stops its own audio. */ }
        }
    }
    private void EngineChanged(CalibrationSnapshot snapshot)
    {
        lock (_auditionStopSync) EngineChangedCore(snapshot);
    }
    private void EngineChangedCore(CalibrationSnapshot snapshot)
    {
        if (_suppressCalibrationEvents || _calibrationState is not { } old) return;
        if (snapshot.Stage is CalibrationStage.Failed or CalibrationStage.Cancelled or CalibrationStage.Cancelling or CalibrationStage.Completed)
        {
            long epoch = InvalidateAudition();
            if (_auditionPlayer is not null)
            {
                SilenceAuditionTransition();
                _ = StopTerminalAuditionAsync(epoch);
            }
            old = old with { PreviewAudioPlaying = false, PreviewEpoch = _previewEpoch };
        }
        string stage = snapshot.Stage.ToString();
        if (_savingHearing && snapshot.Stage == CalibrationStage.Completed) stage = "Saving";
        if (snapshot.Workflow == CalibrationWorkflow.EarScan)
            stage = snapshot.Stage == CalibrationStage.Completed ? "ScanCompleted" : snapshot.Stage == CalibrationStage.Measuring ? "Scanning" : stage;
        else if (snapshot.Stage == CalibrationStage.ResultReady) stage = "Results";
        else if (snapshot.Stage == CalibrationStage.Interrupted) stage = "Paused";
        _client?.SetCalibrationActive(stage is not ("Completed" or "Failed" or "Cancelled"));
        int? frequencyHz = _calibrationContext?.ProductId == "06EC10" && snapshot.FrequencyType is >= 1 and <= 6
            ? new[] { 500, 1000, 2000, 4000, 6000, 8000 }[snapshot.FrequencyType - 1] : null;
        var radar = HearingRadarMath.Create(snapshot);
        byte? enhanceType = snapshot.FiltersReady && snapshot.Curves is [var mainCurve]
            && mainCurve.SourceCommand == CalibrationCodecs.FilterCommand && mainCurve.Uid == snapshot.Uid
            && mainCurve.EnhanceType is <= 2 ? mainCurve.EnhanceType : null;
        _calibrationState = old with { Revision = ++_calibrationRevision, Stage = stage, Uid = snapshot.Uid,
            Side = snapshot.Side, FrequencyType = snapshot.FrequencyType, FrequencyHz = frequencyHz, StepIndex = snapshot.StepIndex, StepCount = snapshot.StepCount,
            LevelIndex = snapshot.LevelIndex, DesiredLevelIndex = snapshot.DesiredLevelIndex, Decibels = snapshot.Decibels,
            ToneAcknowledged = snapshot.ToneAcknowledged, FiltersReady = snapshot.FiltersReady,
            CanPreview = snapshot.CanPreview, PreviewMode = snapshot.PreviewMode.ToString(),
            HearingRadar = radar is null ? null : new UiHearingRadar(radar.Left, radar.Right),
            HearingEnhanceType = enhanceType,
            RestoreDescriptionId = snapshot.RestoreDescriptionId is >= 1 and <= 20 ? snapshot.RestoreDescriptionId : null,
            CanResume = snapshot.CanResume, NoiseWarning = snapshot.NoiseWarning, HasInterEarDifference = snapshot.HasInterEarDifference,
            RawStatus = snapshot.RawStatus,
            Message = CalibrationMessage(snapshot), FitResults = snapshot.FitResults.Select(x => new UiFitResult(x.Key, x.Value)).ToArray(),
            HearingLevels = snapshot.HearingLevels.Select(x => new UiHearingPoint(x.Side, x.Type, x.Decibels)).ToArray() };
        CalibrationChanged?.Invoke(_calibrationState);
    }
    private static string CalibrationMessage(CalibrationSnapshot state) => state.Stage == CalibrationStage.Interrupted
        ? state.CanResume ? "测试已暂停，可以恢复并继续当前频段。" : state.RawStatus switch
        { 1 => "音乐播放打断了测试，请停止播放后继续。", 3 => "通话打断了测试，请结束通话后继续。",
          5 => "请重新正常佩戴两只耳机后继续。", _ => "测试已暂停，请恢复双耳佩戴并停止音乐或通话。" }
        : state.RawStatus switch
        {
            0x08 => "耳机音频通道暂不可用，请停止播放后重试。",
            0x09 => "通话正在使用耳机，请结束通话后重试。",
            0x0A => "两耳连接尚未就绪，请等待两耳连接后重试。",
            0x0B => "请正常佩戴两只耳机后重试。",
            0x0F => "耳机正在进行其他检测，请稍后重试。",
            0x10 => "耳机电量不足，请充电后重试。",
            _ => state.Stage == CalibrationStage.Failed ? state.Message ?? "测试未能完成，请确认双耳佩戴和连接状态后重试。"
                : state.NoiseWarning ? "环境噪声较大，请在安静环境继续测试。"
                : state.HasInterEarDifference ? "左右耳结果差异较大，建议重新检查佩戴并核实测试结果。" : ""
        };
    internal async Task<UiCalibrationState> CancelCalibrationAsync(string handle, CancellationToken token)
    {
        var state = GetCalibration(handle);
        await StopLocalAuditionAsync();
        if (_calibration is not null && state.Stage != "Ready")
        {
            try { await _calibration.CancelAsync(token); }
            catch (Exception)
            {
                string failedStage = _calibration.State.Stage == CalibrationStage.Cancelling ? "Cancelling" : "Failed";
                _calibrationState = GetCalibration(handle) with { Stage = failedStage, Revision = ++_calibrationRevision,
                    Message = "停止测试尚未得到确认，请重新连接耳机后核实。" };
                _client?.SetCalibrationActive(failedStage == "Cancelling");
                CalibrationChanged?.Invoke(_calibrationState); return _calibrationState;
            }
        }
        _calibrationState = GetCalibration(handle) with { Stage = "Cancelled", Revision = ++_calibrationRevision };
        _client?.SetCalibrationActive(false);
        CalibrationChanged?.Invoke(_calibrationState); return _calibrationState;
    }
    internal async Task<IReadOnlyList<UiHearingProfile>> ReadHearingProfilesAsync(CancellationToken token)
    {
        if (!CalibrationAvailable("hearing") || _client is null || _calibrationContext is null) return [];
        var history = await _hearingHistory.ReadAsync(_stableKey, token);
        var profiles = history.Select(x => new UiHearingProfile(x.Uid, x.Name, false,
            !(_calibrationContext.ProductId == "06EC10" && x.Uid == 0)
                && (x.Version == HearingVersion.V2 ? _calibrationContext.HearingV2 : _calibrationContext.HearingV1)
                && (!_calibrationContext.EarScan || x.ScanData?.Length > 0))).ToList();
        _currentHearingUid = null;
        try
        {
            var current = CalibrationCodecs.ParseCurrentHearing((await _client.RequestAsync(0x0115, [], token)).Payload);
            if (current.Levels.Count > 0)
            {
                _currentHearingUid = current.Uid;
                bool complete = current.Levels.Count == 12;
                if (_calibrationContext.EarScan)
                {
                    var scan = CalibrationCodecs.ParseCurrentEarScan((await _client.RequestAsync(0x011E, [], token)).Payload);
                    complete &= scan.Uid == current.Uid && scan.Data.Length > 0;
                }
                profiles.RemoveAll(x => x.Uid == current.Uid);
                profiles.Insert(0, new(current.Uid, current.Name.Length > 0 ? current.Name : "耳机中的当前配置", true, complete));
            }
        }
        catch (Exception error) when (!token.IsCancellationRequested)
        { DiagnosticLog.Error("host.hearing", "currentProfileFailed", "Current hearing profile could not be read", error); }
        _hearingProfiles = profiles;
        return profiles;
    }
    internal async Task<UiApplyResult> SelectHearingAsync(int? uid, CancellationToken token)
    {
        if (_client is null || _calibration is null || !CalibrationAvailable("hearing")) throw new InvalidOperationException("黄金听感不可用。");
        bool submitted = false;
        _suppressCalibrationEvents = true;
        try
        {
            if (uid.HasValue)
            {
                var profiles = await ReadHearingProfilesAsync(token);
                if (!profiles.Any(x => x.Uid == uid && x.CanApply)) throw new InvalidOperationException("该配置不完整或已不可用。");
                if (_currentHearingUid == uid) await _calibration.RestoreAsync(uid.Value, token);
                else
                {
                    var entry = (await _hearingHistory.ReadAsync(_stableKey, token)).FirstOrDefault(x => x.Uid == uid)
                        ?? throw new InvalidOperationException("该配置已不在本地记录中。");
                    submitted = true; await _calibration.ApplyHistoryAsync(entry, true, token);
                }
            }
            submitted = true; await EnableHearingAsync(uid.HasValue, token);
            await ReadHearingProfilesAsync(token);
            var after = Map(await _client.RefreshAsync(Daily.DailyRefreshScope.Features, token));
            bool matches = after.HearingEnabled == uid.HasValue && (!uid.HasValue || _currentHearingUid == uid);
            SnapshotChanged?.Invoke(after);
            return new() { BackendReceiptReceived = true, TargetSubmitted = submitted, SubmissionAttempted = submitted,
                Accepted = true, ReadbackMatches = matches, ConnectionState = after.ConnectionState, After = after,
                Message = matches ? "黄金听感设置已完成。" : "耳机已接受请求，请刷新确认当前配置。" };
        }
        catch (InvalidOperationException) when (!submitted) { throw; }
        catch (Exception)
        {
            return new() { BackendReceiptReceived = true, SubmissionAttempted = submitted, AcknowledgementUnknown = submitted,
                PartialEffects = submitted, FailureKind = ApplyFailure.TransportUnknown, ConnectionState = ConnectionEvidence(_last),
                Message = "听感设置结果未确认，请刷新后核实，后台不会重复提交。" };
        }
        finally { _suppressCalibrationEvents = false; }
    }
    private async Task EnableHearingAsync(bool enabled, CancellationToken token)
    {
        var reply = await _client!.RequestAsync(0x0403, [0x0B, enabled ? (byte)1 : (byte)0], token);
        CalibrationCodecs.RequireSuccess(reply.Payload);
    }
    internal async Task DisposeCalibrationAsync()
    {
        await StopLocalAuditionAsync(dispose: true);
        Task refresh;
        lock (_hearingRefreshGate)
        {
            _hearingRefreshLifetime?.Cancel(); _hearingRefreshNeeded = false;
            refresh = _hearingRefreshTask;
        }
        if (_client is not null) _client.Notification -= OnHearingNotification;
        try { await refresh; } catch { }
        _hearingRefreshLifetime?.Dispose(); _hearingRefreshLifetime = null;
        if (_calibration is null) return;
        var engine = _calibration; _calibration = null;
        try { await engine.CancelAsync(); } catch { }
        engine.Changed -= EngineChanged; await engine.DisposeAsync();
        _client?.SetCalibrationActive(false);
        try { await _calibrationBackground; } catch { }
        _calibrationState = null; _calibrationContext = null; _hearingProfiles = [];
    }
}
