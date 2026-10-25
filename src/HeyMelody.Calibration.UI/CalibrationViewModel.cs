using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text;
using HeyMelody.Diagnostics;
using HeyMelodyWindows.App;

namespace HeyMelodyWindows.Calibration.UI;

/// <summary>Wizard page group derived from Host stages; it only selects presentation.</summary>
public enum CalibrationPhase { Intro, EarScan, Hearing, Results, Done }

/// <summary>Reusable presentation of one Host-owned operation, without owning the earbud connection.</summary>
public sealed class CalibrationViewModel(ICalibrationService service, string handle, string kind) : INotifyPropertyChanged, IAsyncDisposable
{
    private readonly CancellationTokenSource _lifetime = new();
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private UiCalibrationState _state = new() { Handle = handle, Workflow = kind };
    private bool _busy, _cancelled, _cancelBusy, _stopConfirmed, _cancelFailed, _backendUnavailable, _disposed;
    private Task<bool>? _cancelOperation;
    private string _error = "", _name = "";
    private sealed record LevelChange(int Index, bool Released, (int Step, int Side, int Frequency, int? Uid) Step);
    private LevelChange? _pendingLevel;
    private Task? _levelOperation;
    private CancellationTokenSource? _levelDelay;
    private int _selectedLevel = 5;
    private bool _selectionActive;
    private CalibrationPhase _phase;
    private bool _earScanCompleted;
    private long _spectrumSequence;
    /// <summary>Volume steps of the V2 level test; the slider, its label and the backend share this count.</summary>
    public const int LevelCount = 12;
    public event PropertyChangedEventHandler? PropertyChanged;
    public event Action<IReadOnlyList<double>>? SpectrumChanged;
    public IReadOnlyList<double> SpectrumLevels { get; private set; } = [];
    public UiCalibrationState State => _state;
    public string Kind => kind;
    public CalibrationPhase Phase => _phase;
    /// <summary>True once this hearing run has a completed ear scan; drives the stepper only.</summary>
    public bool EarScanCompleted => _earScanCompleted;
    public bool Busy => _busy || _cancelBusy;
    public bool CancellationRequested => _cancelled;
    public bool CancellationPending => _cancelBusy;
    public bool CancellationFailed => _cancelFailed;
    public bool StopConfirmed => _stopConfirmed;
    public bool CanCloseLocally => _backendUnavailable;
    public int SelectedLevelIndex => _selectionActive ? _selectedLevel : _state.LevelIndex;
    public bool LevelPending => _levelOperation is not null || _pendingLevel is not null;
    public bool CanAdjustLevel => !_cancelled && !_busy && _state.Stage == "AwaitingAnswer";
    public bool CanContinue => !_cancelled && !_busy && !LevelPending && ConfirmedSelection;
    public bool CanSave => !_cancelled && !_busy && _state.Stage is ("Results" or "ResultReady" or "Previewing") && _state.FiltersReady;
    public bool CanPreview => CanSave && _state.CanPreview;
    private bool ConfirmedSelection => _state.Stage == "AwaitingAnswer" && _state.ToneAcknowledged
        && (!_selectionActive || _state.LevelIndex == _selectedLevel);
    public string Error => _error;
    public string Name { get => _name; set { _name = value; Changed(); } }
    public IReadOnlyList<UiHearingProfile> Profiles { get; private set; } = [];
    public bool NeedsReplacement => Profiles.Count(x => x.Uid.HasValue) >= 10;
    public bool ReplacesCurrent => Profiles.Any(x => x.Uid.HasValue && x.Current);
    public async Task InitializeAsync()
    {
        await RunAsync(async () =>
        {
            Adopt(await service.GetCalibrationAsync(handle, _lifetime.Token));
            if (kind == "hearing") Profiles = await service.GetHearingProfilesAsync(_state.Identity, _state.Generation, _lifetime.Token);
        });
    }
    public void Adopt(UiCalibrationState state)
    {
        if (state.Handle != handle || state.Revision < _state.Revision ||
            _state.BackendInstanceId.Length > 0 && state.BackendInstanceId != _state.BackendInstanceId ||
            _state.Generation != 0 && state.Generation != _state.Generation) return;
        if (Step(state) != Step(_state) || state.Stage is not ("AwaitingAnswer" or "Measuring"))
        {
            _pendingLevel = null; _levelDelay?.Cancel(); _selectionActive = false;
        }
        if (state.Stage != _state.Stage && state.Stage != "Failed")
        { _error = ""; Changed(nameof(Error)); }
        var previous = _state.Stage;
        bool resetSpectrum = state.Stage != "Previewing" || state.PreviewEpoch != _state.PreviewEpoch || !state.PreviewAudioPlaying
            || state.PreviewMode != _state.PreviewMode;
        _state = state;
        if (resetSpectrum) ClearSpectrum();
        UpdatePhase(previous);
        if (!_selectionActive) _selectedLevel = state.LevelIndex;
        Changed(nameof(State)); SelectionChanged();
    }
    public void AdoptSpectrum(UiCalibrationSpectrum frame)
    {
        if (_cancelled || _disposed || _state.Stage != "Previewing" || !_state.PreviewAudioPlaying || _state.PreviewMode is not ("Original" or "Enhanced")
            || frame.Handle != handle || frame.BackendInstanceId != _state.BackendInstanceId
            || frame.Generation != _state.Generation || frame.PreviewEpoch != _state.PreviewEpoch
            || frame.Sequence <= _spectrumSequence || frame.Levels.Count != 6
            || frame.Levels.Any(value => !double.IsFinite(value) || value is < 0 or > 1)) return;
        _spectrumSequence = frame.Sequence;
        SpectrumLevels = Array.AsReadOnly(frame.Levels.ToArray());
        SpectrumChanged?.Invoke(SpectrumLevels);
    }
    private void ClearSpectrum()
    {
        _spectrumSequence = 0; SpectrumLevels = [];
        SpectrumChanged?.Invoke(SpectrumLevels);
    }
    private void UpdatePhase(string previous)
    {
        var scanFirst = kind == "hearing" && _state.SupportsEarScan;
        // Wear checks and start-up stages are shared by the scan and the test; their origin decides the page.
        var phase = _state.Stage switch
        {
            "Ready" or "Idle" => CalibrationPhase.Intro,
            "Scanning" or "ScanCompleted" => CalibrationPhase.EarScan,
            "AwaitingAnswer" or "Measuring" or "Paused" or "Interrupted" => CalibrationPhase.Hearing,
            "LoadingCurves" or "LoadingFilters" or "Results" or "ResultReady" or "Previewing" or "Saving" => CalibrationPhase.Results,
            "Completed" => CalibrationPhase.Done,
            "CheckingWear" or "Starting" => previous switch
            {
                "Ready" or "Idle" or "Failed" or "Completed" => scanFirst ? CalibrationPhase.EarScan : CalibrationPhase.Hearing,
                "ScanCompleted" => CalibrationPhase.Hearing,
                _ => _phase
            },
            _ => _phase
        };
        var scanned = phase switch
        {
            CalibrationPhase.Intro => false,
            CalibrationPhase.EarScan => _state.Stage == "ScanCompleted",
            _ => scanFirst
        };
        if (phase != _phase) { _phase = phase; Changed(nameof(Phase)); }
        if (scanned != _earScanCompleted) { _earScanCompleted = scanned; Changed(nameof(EarScanCompleted)); }
    }
    public void BackendUnavailable(string message)
    {
        if (_disposed) return;
        _backendUnavailable = true; _cancelled = true;
        ClearSpectrum();
        _pendingLevel = null; _levelDelay?.Cancel(); _lifetime.Cancel();
        SetError((message.Length > 0 ? message : "蓝牙后台已断开。")
            + (_stopConfirmed ? "" : "\n无法确认耳机上的测试是否已停止，可关闭此窗口后重新连接核实。"));
        CancellationChanged();
    }
    public async Task SendAsync(string action, bool? heard = null, int? level = null, bool released = false, bool allowReplacement = false)
    {
        if (_cancelled || _busy) return;
        if (_pendingLevel is { } pending)
        { _pendingLevel = pending with { Released = true }; _levelDelay?.Cancel(); }
        if (_levelOperation is { } levelOperation) await levelOperation;
        if (_cancelled) return;
        if (action == "next" && _state.HearingVersion == "V2" && _state.Stage != "ScanCompleted" && !CanContinue)
        { SetError("请等待当前音量播放确认后再继续；如果没有声音，请重试播放。"); return; }
        if (action == "save" && !CanSave || action.StartsWith("preview-", StringComparison.Ordinal) && !CanPreview) return;
        if (action == "save" && ValidateName() is { } nameError) { SetError(nameError); return; }
        var step = Step(_state);
        await RunAsync(async () =>
        {
            Adopt(await service.GetCalibrationAsync(handle, _lifetime.Token));
            if (action is "next" or "previous" or "answer" && step != Step(_state))
                throw new InvalidOperationException("测试进度已变化，请按当前步骤操作。");
            if (action == "next" && _state.HearingVersion == "V2" && _state.Stage != "ScanCompleted" && !ConfirmedSelection)
                throw new InvalidOperationException("当前测试音尚未确认，请重试播放。");
            Adopt(await service.AdvanceCalibrationAsync(handle, _state.Revision,
                new UiCalibrationInput(action, heard, level, released, action == "save" ? Name.Trim() : null, allowReplacement), _lifetime.Token));
        });
    }
    public Task AdjustLevelAsync(int level, bool released)
    {
        if (!CanAdjustLevel || level < 0 || level >= LevelCount) return Task.CompletedTask;
        _selectedLevel = level; _selectionActive = true;
        _pendingLevel = new(level, released, Step(_state));
        _levelDelay?.Cancel();
        SelectionChanged();
        return _levelOperation ??= SendLevelsAsync();
    }
    private async Task SendLevelsAsync()
    {
        await Task.Yield();
        try
        {
            while (!_cancelled && _pendingLevel is { } input)
            {
                if (!input.Released)
                {
                    using var delay = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
                    _levelDelay = delay;
                    try { await Task.Delay(300, delay.Token); }
                    catch (OperationCanceledException) when (!_lifetime.IsCancellationRequested) { continue; }
                    finally { if (ReferenceEquals(_levelDelay, delay)) _levelDelay = null; }
                }
                if (!ReferenceEquals(_pendingLevel, input)) continue;
                _pendingLevel = null;
                await ApplyLevelAsync(input);
                if (_pendingLevel is null && Step(_state) == input.Step && _state.ToneAcknowledged && _state.LevelIndex == _selectedLevel)
                { _selectionActive = false; _error = ""; Changed(nameof(Error)); }
            }
        }
        catch (OperationCanceledException) when (_cancelled) { }
        catch (Exception failure)
        {
            if (!_cancelled)
            {
                DiagnosticLog.Error("ui.calibration", "levelAdjustFailed", "Volume adjustment was not confirmed", failure, new { handle, kind });
                _pendingLevel = null; SetError("音量调整未确认，请重试播放。");
            }
        }
        finally { _levelOperation = null; SelectionChanged(); }
    }
    private async Task ApplyLevelAsync(LevelChange change)
    {
        // An event can advance the Host revision while an IPC request is in flight.
        // Only this repeatable level change may be retried, and only on the same step.
        for (var attempt = 0; ; attempt++)
        {
            Adopt(await service.GetCalibrationAsync(handle, _lifetime.Token));
            if (Step(_state) != change.Step || _state.Stage != "AwaitingAnswer") return;
            var revision = _state.Revision;
            try
            {
                Adopt(await service.AdvanceCalibrationAsync(handle, revision,
                    new UiCalibrationInput("level", LevelIndex: change.Index, Released: true), _lifetime.Token));
                return;
            }
            catch (InvalidOperationException) when (attempt == 0)
            {
                Adopt(await service.GetCalibrationAsync(handle, _lifetime.Token));
                if (_state.Revision <= revision || Step(_state) != change.Step || _state.Stage != "AwaitingAnswer") throw;
            }
        }
    }
    public string? ValidateName()
    {
        if (Name.Any(char.IsControl)) return "名称不能包含控制字符。";
        var name = Name.Trim();
        if (name.Length == 0) return "请输入配置名称。";
        try { if (StrictUtf8.GetByteCount(name) > 39) return "名称过长，请缩短后重试。"; }
        catch (EncoderFallbackException) { return "名称包含无法识别的字符，请重新输入。"; }
        if (Profiles.Any(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase))) return "已有同名配置，请使用其他名称。";
        return null;
    }
    private async Task RunAsync(Func<Task> action)
    {
        if (_busy || _cancelled) return;
        _busy = true; _error = ""; Changed(nameof(Busy)); Changed(nameof(Error)); SelectionChanged();
        try { await action(); }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception error)
        {
            DiagnosticLog.Error("ui.calibration", "operationFailed", "Calibration operation failed", error, new { handle, kind, stage = _state.Stage });
            try { Adopt(await service.GetCalibrationAsync(handle, _lifetime.Token)); }
            catch (Exception recovery)
            { DiagnosticLog.Debug("ui.calibration", "recoveryReadFailed", "Calibration state could not be re-read after a failure", new { handle, failure = recovery.Message }); }
            SetError(error is InvalidOperationException ? error.Message : "操作未能完成，请重试或取消测试。");
        }
        finally { _busy = false; Changed(nameof(Busy)); SelectionChanged(); }
    }
    public Task<bool> CancelAsync()
    {
        if (_stopConfirmed) return Task.FromResult(true);
        if (_backendUnavailable || _disposed) return Task.FromResult(false);
        return _cancelOperation ??= CancelCoreAsync();
    }
    private async Task<bool> CancelCoreAsync()
    {
        await Task.Yield();
        if (_backendUnavailable || _disposed) { _cancelOperation = null; return false; }
        _cancelled = true; _cancelBusy = true; _cancelFailed = false;
        ClearSpectrum();
        _pendingLevel = null; _levelDelay?.Cancel(); _lifetime.Cancel(); CancellationChanged();
        try
        {
            // STOP must use its own request, even though measurement input was cancelled.
            Adopt(await service.CancelCalibrationAsync(handle, CancellationToken.None));
            _stopConfirmed = _state.Stage == "Cancelled";
            if (_stopConfirmed) SetError("");
            else
            {
                _cancelFailed = true;
                SetError(_state.Message.Length > 0 ? _state.Message : "停止测试尚未确认，请重试停止。");
            }
            return _stopConfirmed;
        }
        catch (Exception failure)
        {
            DiagnosticLog.Error("ui.calibration", "cancelFailed", "Calibration stop was not confirmed", failure, new { handle, kind });
            _cancelFailed = true;
            SetError("停止测试尚未确认，请重试停止或重新连接后核实。");
            return false;
        }
        finally { _cancelBusy = false; _cancelOperation = null; CancellationChanged(); }
    }
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        if (!_backendUnavailable && !await CancelAsync()) throw new InvalidOperationException(Error);
        _disposed = true; _lifetime.Dispose();
    }
    private static (int, int, int, int?) Step(UiCalibrationState state) => (state.StepIndex, state.Side, state.FrequencyType, state.Uid);
    private void SetError(string message) { _error = message; Changed(nameof(Error)); }
    private void CancellationChanged()
    {
        Changed(nameof(Busy)); Changed(nameof(CancellationRequested)); Changed(nameof(CancellationPending));
        Changed(nameof(CancellationFailed)); Changed(nameof(StopConfirmed)); Changed(nameof(CanCloseLocally)); SelectionChanged();
    }
    private void SelectionChanged()
    {
        Changed(nameof(SelectedLevelIndex)); Changed(nameof(LevelPending)); Changed(nameof(CanAdjustLevel));
        Changed(nameof(CanContinue)); Changed(nameof(CanSave)); Changed(nameof(CanPreview));
    }
    private void Changed([CallerMemberName] string? property = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
}
