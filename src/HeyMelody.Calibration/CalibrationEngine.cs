using System.Security.Cryptography;
using HeyMelodyWindows.Protocol;

using HeyMelody.Diagnostics;

namespace HeyMelodyWindows.Calibration;

/// <summary>One device workflow, using the caller's existing Bluetooth reader.</summary>
public sealed partial class CalibrationEngine : IAsyncDisposable
{
    public static IReadOnlyList<int> V2Decibels { get; } = Array.AsReadOnly(new[] { -55, -49, -41, -35, -25, -19, -11, -5, 3, 7, 13, 17 });
    private readonly ICalibrationTransport _transport;
    private readonly CalibrationDeviceContext _context;
    private readonly CalibrationHistoryStore? _history;
    private readonly CalibrationTimings _timings;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _stateGate = new();
    private CancellationTokenSource? _run, _levelDelay, _hearingIdle;
    private TaskCompletionSource<IReadOnlyDictionary<byte, byte>>? _fit;
    private TaskCompletionSource<EarScanResult>? _scan;
    private TaskCompletionSource<bool>? _curvesReady;
    private HearingFilterAssembler? _filters;
    private EarScanFilterAssembler? _scanFilters;
    private HearingV1Search _search = new();
    private CalibrationSnapshot _state;
    private long _revision, _lastLevelSent, _levelEpoch, _idleEpoch;
    private int _disposed, _interruptionBlocks;
    private bool _fitOwned, _scanOwned, _hearingOwned, _previewOwned;
    private bool _hearingStopping, _scanStopping;
    private byte _audioInterruptionStatus;
    public CalibrationSnapshot State { get { lock (_stateGate) return _state; } }
    public event Action<CalibrationSnapshot>? Changed;

    public CalibrationEngine(ICalibrationTransport transport, CalibrationDeviceContext context,
        CalibrationHistoryStore? history = null, CalibrationTimings? timings = null)
    {
        ArgumentNullException.ThrowIfNull(transport); ArgumentNullException.ThrowIfNull(context);
        if (string.IsNullOrWhiteSpace(context.StableKey)) throw new ArgumentException("缺少耳机的稳定设备标识。", nameof(context));
        _transport = transport; _context = context with { Commands = context.Commands.ToHashSet(), Events = context.Events.ToHashSet() };
        _history = history; _timings = timings ?? new();
        if (new[] { _timings.FitTimeout, _timings.EarScanTimeout, _timings.FilterTimeout, _timings.CommandTimeout, _timings.HearingIdleTimeout }.Any(x => x <= TimeSpan.Zero)
            || new[] { _timings.ScanSettle, _timings.StartToneDelay, _timings.StepPauseDelay, _timings.LevelThrottle }.Any(x => x < TimeSpan.Zero))
            throw new ArgumentOutOfRangeException(nameof(timings));
        _state = new() { Generation = context.Generation };
        transport.Notification += OnNotification; transport.Disconnected += OnDisconnected;
    }

    public async Task<CalibrationSnapshot> RunFitAsync(CancellationToken token = default)
    {
        Require(_context.Fit, 0x0405, 0x0109);
        CancellationTokenSource? run = null;
        await _gate.WaitAsync(token);
        try
        {
            run = NewRun(CalibrationWorkflow.Fit); _fit = new(TaskCreationOptions.RunContinuationsAsynchronously);
            using var linked = Link(token); await CheckWearAsync(linked.Token);
            Update(s => s with { Stage = CalibrationStage.Starting });
            _fitOwned = true; // A missing ACK does not prove the device ignored START.
            try { await RequestAsync(0x0405, CalibrationCodecs.Fit(true), linked.Token); }
            catch (CalibrationDeviceException) { _fitOwned = false; throw; }
            Update(s => s with { Stage = CalibrationStage.Measuring });
        }
        catch (Exception error) { if (run is not null) { await CleanupAsync(); FailWhenStopped(error); } throw; }
        finally { _gate.Release(); }
        try
        {
            using var linked = Link(token);
            var result = await _fit!.Task.WaitAsync(_timings.FitTimeout, linked.Token);
            await _gate.WaitAsync(linked.Token);
            try
            {
                EnsureRun(run!); await StopFitAsync(linked.Token);
                Update(s => s with { Stage = CalibrationStage.Completed, FitResults = result }); return State;
            }
            finally { _gate.Release(); }
        }
        catch (Exception error) { await EndFailedRunAsync(run!, error); throw; }
    }

    public async Task<CalibrationSnapshot> StartEarScanAsync(CancellationToken token = default)
    {
        // X4 sends event14 although its 0200 advertisement omits it and registration is rejected.
        Require(_context.EarScan, 0x040D, 0x0109);
        CancellationTokenSource? run = null;
        await _gate.WaitAsync(token);
        try
        {
            run = NewRun(CalibrationWorkflow.EarScan); _scan = new(TaskCreationOptions.RunContinuationsAsynchronously);
            using var linked = Link(token); await CheckWearAsync(linked.Token);
            Update(s => s with { Stage = CalibrationStage.Starting });
            _scanOwned = true;
            try { await RequestAsync(0x040D, CalibrationCodecs.EarScanDetection(true, State.Uid), linked.Token); }
            catch (CalibrationDeviceException) { _scanOwned = false; throw; }
            Update(s => s with { Stage = CalibrationStage.Measuring });
        }
        catch (Exception error) { if (run is not null) { await CleanupAsync(); FailWhenStopped(error); } throw; }
        finally { _gate.Release(); }
        try
        {
            using var linked = Link(token);
            var result = await _scan!.Task.WaitAsync(_timings.EarScanTimeout, linked.Token);
            await _gate.WaitAsync(linked.Token);
            try
            {
                EnsureRun(run!); await StopScanAsync(linked.Token);
                await Task.Delay(_timings.ScanSettle, linked.Token);
                Update(s => s with { Stage = CalibrationStage.Completed, ScanResult = result }); return State;
            }
            finally { _gate.Release(); }
        }
        catch (Exception error) { await EndFailedRunAsync(run!, error); throw; }
    }

    public async Task<CalibrationSnapshot> CancelAsync(CancellationToken token = default)
    {
        DisarmIdle(); CancelSafely(_run); CancelLevel(); _fit?.TrySetCanceled(); _scan?.TrySetCanceled(); _curvesReady?.TrySetCanceled();
        await _gate.WaitAsync(token);
        try
        {
            if (State.Stage == CalibrationStage.Idle || State.Stage == CalibrationStage.Cancelled && !HasOwnedAudio) return State;
            Update(s => s with { Stage = CalibrationStage.Cancelling, ToneAcknowledged = false, CanResume = false });
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(_timings.CommandTimeout * 3);
            await StopOwnedAsync(timeout.Token);
            Update(s => s with { Stage = CalibrationStage.Cancelled, PreviewMode = CalibrationPreviewMode.Stopped }); return State;
        }
        catch (Exception error) { FailWhenStopped(error); throw; }
        finally { _gate.Release(); }
    }

    private bool HasOwnedAudio => _fitOwned || _scanOwned || _hearingOwned || _previewOwned;
    private async Task StopFitAsync(CancellationToken token)
    {
        if (!_fitOwned) return;
        await RequestAsync(0x0405, CalibrationCodecs.Fit(false), token); _fitOwned = false;
    }
    private async Task StopScanAsync(CancellationToken token)
    {
        if (!_scanOwned) return;
        _scanStopping = true;
        try { await RequestAsync(0x040D, CalibrationCodecs.EarScanDetection(false, State.Uid), token); _scanOwned = false; }
        finally { _scanStopping = false; }
    }
    private async Task StopHearingDetectionAsync(CancellationToken token)
    {
        if (!_hearingOwned) return;
        _hearingStopping = true;
        try { await RequestAsync(0x040D, CalibrationCodecs.HearingDetection(false), token); _hearingOwned = false; }
        finally { _hearingStopping = false; }
    }
    private async Task StopOwnedAsync(CancellationToken token)
    {
        if (!_transport.IsConnected) { _fitOwned = _scanOwned = _hearingOwned = _previewOwned = false; return; }
        Exception? first = null;
        async Task Attempt(Func<Task> action) { try { await action(); } catch (Exception error) { first ??= error; } }
        await Attempt(() => StopFitAsync(token)); await Attempt(() => StopScanAsync(token));
        if (_hearingOwned)
        {
            await Attempt(() => PauseToneAsync(token));
            // An absent/rejected current-profile GET must never prevent stopping the owned detector.
            await Attempt(() => StopHearingDetectionAsync(token));
        }
        if (_previewOwned) await Attempt(() => StopPreviewCoreAsync(token));
        if (first is not null) throw first;
    }
    private async Task CleanupAsync()
    {
        try { using var timeout = new CancellationTokenSource(_timings.CommandTimeout * 3); await StopOwnedAsync(timeout.Token); }
        catch (Exception error) { DiagnosticLog.Error("Calibration", "cleanup.stop-unconfirmed", "Calibration cleanup could not confirm all owned detectors stopped; ownership remains retained.", error, State); }
    }
    private async Task EndFailedRunAsync(CancellationTokenSource run, Exception error)
    {
        await _gate.WaitAsync();
        try
        {
            if (!ReferenceEquals(run, _run)) return;
            await CleanupAsync();
            if (State.Stage is not (CalibrationStage.Cancelling or CalibrationStage.Cancelled or CalibrationStage.Failed)) FailWhenStopped(error);
        }
        finally { _gate.Release(); }
    }
    private async Task CheckWearAsync(CancellationToken token, bool publish = true)
    {
        if (publish) Update(s => s with { Stage = CalibrationStage.CheckingWear });
        var reply = await RequestAsync(0x0109, [], token);
        var wearing = NotificationParsers.ParseComponentPairs(reply.Payload.AsSpan(1));
        if (!BothInEar(wearing)) throw new InvalidOperationException("请戴好两只耳机后开始检测。");
    }
    private static bool BothInEar(IReadOnlyDictionary<byte, byte> pairs) => pairs.TryGetValue(1, out byte left)
        && pairs.TryGetValue(2, out byte right) && (left & 2) != 0 && (right & 2) != 0;
    private void Require(bool declared, params ushort[] commands)
    {
        if (!declared || commands.Any(c => !_context.Commands.Contains(c))) throw new NotSupportedException("该耳机不支持所需的检测功能。");
    }
    private async Task<Packet> RequestAsync(ushort command, byte[] payload, CancellationToken token, bool requireSuccess = true)
    {
        using var operation = DiagnosticLog.BeginOperation(DiagnosticLog.CurrentCorrelationId);
        DiagnosticLog.Bytes("Calibration", "request.payload", payload,
            new { command = $"0x{command:X4}", requireSuccess, State.Generation, State.Workflow, State.Stage });
        token.ThrowIfCancellationRequested();
        if (!_context.Commands.Contains(command)) throw new NotSupportedException($"耳机未声明所需的控制命令。");
        if (!_transport.IsConnected) throw new IOException("耳机连接已断开。");
        // Cancellation of a UI operation must not cancel a submitted shared RFCOMM transaction.
        // Drain its ACK (or bounded deadline) before the gate can send the owned STOP/next tone.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token); timeout.CancelAfter(_timings.CommandTimeout);
        Packet reply;
        try { reply = await _transport.RequestAsync(command, payload, timeout.Token).WaitAsync(timeout.Token); }
        catch (OperationCanceledException) when (!_lifetime.IsCancellationRequested && timeout.IsCancellationRequested)
        {
            DiagnosticLog.Warning("Calibration", "request.timeout", "The submitted calibration command reached its bounded deadline.",
                data: new { command = $"0x{command:X4}", timeout = _timings.CommandTimeout, State.Generation });
            throw new TimeoutException($"耳机响应超时，请检查连接后重试。");
        }
        catch (Exception error)
        { DiagnosticLog.Error("Calibration", "request.failed", "The calibration transport request failed.", error, new { command = $"0x{command:X4}", State.Generation }); throw; }
        DiagnosticLog.Bytes("Calibration", "response.payload", reply.Payload,
            new { command = $"0x{command:X4}", responseCommand = $"0x{reply.Command:X4}", reply.Sequence, State.Generation });
        try
        {
            if (reply.Command != (command | 0x8000)) throw new FormatException("耳机返回了不符合当前测试的数据。");
            if (requireSuccess) CalibrationCodecs.RequireSuccess(reply.Payload);
        }
        catch (Exception error)
        { DiagnosticLog.Error("Calibration", "response.invalid", "The calibration response failed validation.", error, new { command = $"0x{command:X4}", responseCommand = $"0x{reply.Command:X4}", reply.Sequence, payloadLength = reply.Payload.Length }); throw; }
        token.ThrowIfCancellationRequested();
        if (!_transport.IsConnected) throw new IOException("耳机连接已断开。");
        return reply;
    }
    private CancellationTokenSource Link(CancellationToken token) => _run is null
        ? CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime.Token)
        : CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime.Token, _run.Token);
    private CancellationTokenSource NewRun(CalibrationWorkflow workflow, EarScanResult? scan = null, int? uid = null)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        if (HasOwnedAudio || State.Stage is CalibrationStage.CheckingWear or CalibrationStage.Starting or CalibrationStage.Measuring
            or CalibrationStage.AwaitingAnswer or CalibrationStage.Paused or CalibrationStage.Saving or CalibrationStage.LoadingCurves or CalibrationStage.Interrupted)
            throw new InvalidOperationException("请先完成或取消当前检测。");
        DisarmIdle(); CancelSafely(_run); _run?.Dispose(); CancelLevel();
        _run = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _fit = null; _scan = null; _curvesReady = null; _filters = null; _scanFilters = null; _lastLevelSent = 0; _interruptionBlocks = 0; _audioInterruptionStatus = 0;
        Update(_ => new() { Generation = _context.Generation, Workflow = workflow, Stage = CalibrationStage.Starting,
            Uid = uid ?? scan?.Uid ?? RandomNumberGenerator.GetInt32(1, int.MaxValue), ScanResult = scan,
            Decibels = workflow == CalibrationWorkflow.HearingV2 ? -19 : 0 });
        return _run;
    }
    private void EnsureRun(CancellationTokenSource run)
    {
        if (!ReferenceEquals(run, _run)) throw new OperationCanceledException("当前检测会话已被替换。");
        run.Token.ThrowIfCancellationRequested();
    }
    private async Task<CalibrationSnapshot> LockedAsync(Func<CancellationToken, Task> action, CancellationToken token, bool preservePreflight = false, bool transientCancellation = false)
    {
        await _gate.WaitAsync(token); var before = State;
        try { await action(token); return State; }
        catch (CalibrationInterruptedException) { return State; }
        catch (Exception error)
        {
            if (transientCancellation && error is OperationCanceledException && _run?.IsCancellationRequested == false) return State;
            if (State.Stage == CalibrationStage.Interrupted && error is OperationCanceledException) return State;
            if (State.Revision == before.Revision || preservePreflight && State.Stage == before.Stage && State.Uid == before.Uid) throw;
            await CleanupAsync(); FailWhenStopped(error); throw;
        }
        finally { _gate.Release(); }
    }
    private static void CancelSafely(CancellationTokenSource? source) { try { source?.Cancel(); } catch (ObjectDisposedException) { } }
    private void CancelLevel() { Interlocked.Increment(ref _levelEpoch); CancelSafely(Interlocked.Exchange(ref _levelDelay, null)); }
    private void DisarmIdle() { Interlocked.Increment(ref _idleEpoch); CancelSafely(Interlocked.Exchange(ref _hearingIdle, null)); }
    private void ArmIdle()
    {
        long epoch = Interlocked.Increment(ref _idleEpoch); var run = _run;
        if (run?.IsCancellationRequested != false) return;
        var idle = Link(CancellationToken.None); CancelSafely(Interlocked.Exchange(ref _hearingIdle, idle));
        _ = WatchIdleAsync(run, idle, epoch);
    }
    private async Task WatchIdleAsync(CancellationTokenSource run, CancellationTokenSource idle, long epoch)
    {
        try
        {
            await Task.Delay(_timings.HearingIdleTimeout, idle.Token);
            if (ReferenceEquals(run, _run) && epoch == Interlocked.Read(ref _idleEpoch) && _hearingOwned
                && State.Stage is CalibrationStage.AwaitingAnswer or CalibrationStage.Interrupted)
                NotificationFailed(new TimeoutException("长时间未操作，测听已自动结束。请重新开始。"));
        }
        catch (OperationCanceledException) when (idle.IsCancellationRequested) { }
        finally { Interlocked.CompareExchange(ref _hearingIdle, null, idle); idle.Dispose(); }
    }
    private void ThrowIfInterrupted()
    {
        if (State.Stage == CalibrationStage.Interrupted) throw new CalibrationInterruptedException();
        if (State.Stage is CalibrationStage.Failed or CalibrationStage.Cancelling or CalibrationStage.Cancelled) throw new OperationCanceledException();
    }
    private sealed class CalibrationInterruptedException : Exception;
    private void OnNotification(Packet packet)
    {
        DiagnosticLog.Bytes("Calibration", "notification.payload", packet.Payload,
            new { command = $"0x{packet.Command:X4}", packet.Sequence, State.Generation, State.Stage });
        try
        {
            if (_run?.IsCancellationRequested != false || State.Stage is CalibrationStage.Cancelled or CalibrationStage.Failed) return;
            if (_fitOwned && CalibrationCodecs.TryEvent(packet, 4, out var fit)) _fit?.TrySetResult(CalibrationCodecs.ParseFit(fit));
            if (_scanOwned && CalibrationCodecs.TryEvent(packet, 14, out var data))
            {
                var scan = CalibrationCodecs.ParseEarScan(data);
                if (scan.Uid == State.Uid && scan.Data.Length != 0) _scan?.TrySetResult(scan);
            }
            if (packet.Command is 0x8116 or 0x811F) AddFilter(packet);
            if (CalibrationCodecs.TryEvent(packet, 8, out var status)) HandleDetectionState(CalibrationCodecs.ParseHearingState(status));
            if (CalibrationCodecs.TryEvent(packet, 2, out var placement) && _hearingOwned && !_hearingStopping && State.StepIndex < 12)
            {
                var pairs = NotificationParsers.ParseComponentPairs(placement);
                if (!BothInEar(pairs)) Interrupt(5, false);
                else if (State.Stage == CalibrationStage.Interrupted && State.RawStatus == 5) Interrupt(6, true);
            }
        }
        catch (Exception error) { DiagnosticLog.Error("Calibration", "notification.invalid", "A calibration notification failed parsing or dispatch.", error, new { command = $"0x{packet.Command:X4}", packet.Sequence, payloadLength = packet.Payload.Length }); NotificationFailed(error); }
    }
    private void HandleDetectionState(HearingState state)
    {
        bool hearing = state.DetectionType == 2 && _hearingOwned && !_hearingStopping;
        bool scan = state.DetectionType == 4 && _scanOwned && !_scanStopping;
        if (!hearing && !scan) return;
        if (state.Status is 8 or 9) { Update(s => s with { NoiseWarning = state.Status == 8 }); return; }
        if (state.Status == 0) return;
        if (hearing && state.Status is >= 1 and <= 6) { Interrupt(state.Status, state.Status is 2 or 4 or 6); return; }
        NotificationFailed(new IOException($"耳机检测已结束，请重新开始。"));
    }
    private void Interrupt(byte status, bool canResume)
    {
        if (canResume && State.Stage != CalibrationStage.Interrupted) return;
        // Music/call and wearing interruptions can overlap; resolving one must not dismiss the other.
        if (status is 1 or 3) { _audioInterruptionStatus = status; Interlocked.Or(ref _interruptionBlocks, 1); }
        else if (status is 2 or 4) Interlocked.And(ref _interruptionBlocks, ~1);
        else if (status == 5) Interlocked.Or(ref _interruptionBlocks, 2);
        else if (status == 6) Interlocked.And(ref _interruptionBlocks, ~2);
        int blocks = Volatile.Read(ref _interruptionBlocks);
        canResume = blocks == 0;
        byte displayed = (blocks & 2) != 0 ? (byte)5 : (blocks & 1) != 0 ? _audioInterruptionStatus : status;
        CancelLevel(); Update(s => s with { Stage = CalibrationStage.Interrupted, ToneAcknowledged = false,
            CanResume = canResume, RawStatus = displayed, Message = displayed is 5 or 6 ? "请调整并戴好两只耳机后继续。" : "音频输出或通话打断了测听，请等待恢复后继续。" });
        if (!canResume) _ = PauseInterruptedAsync();
    }
    private async Task PauseInterruptedAsync()
    {
        await _gate.WaitAsync();
        try { if (_hearingOwned && State.Stage == CalibrationStage.Interrupted) await PauseToneAsync(_lifetime.Token); }
        catch (Exception error) { await CleanupAsync(); FailWhenStopped(error); }
        finally { _gate.Release(); }
    }
    private void NotificationFailed(Exception error)
    {
        if (!HasOwnedAudio && State.Stage != CalibrationStage.LoadingCurves) return;
        Update(s => s with { Stage = CalibrationStage.Cancelling, ToneAcknowledged = false, CanResume = false, Message = "检测中断，正在停止耳机测试。" });
        _fit?.TrySetException(error); _scan?.TrySetException(error); _curvesReady?.TrySetException(error);
        DisarmIdle(); CancelLevel(); CancelSafely(_run); _ = CleanupNotificationAsync(error);
    }
    private async Task CleanupNotificationAsync(Exception error)
    {
        await _gate.WaitAsync(); try { await CleanupAsync(); FailWhenStopped(error); } finally { _gate.Release(); }
    }
    private void FailWhenStopped(Exception error)
    {
        if (!HasOwnedAudio) { Fail(error); return; }
        Update(s => s with { Stage = CalibrationStage.Cancelling, ToneAcknowledged = false, CanResume = false,
            RawStatus = (error as CalibrationDeviceException)?.Status ?? s.RawStatus,
            Message = "未能确认耳机测试已停止，请再次取消或关闭向导重试。" });
    }
    private void OnDisconnected()
    {
        DisarmIdle(); CancelSafely(_run); CancelLevel(); var error = new IOException("检测过程中耳机连接已断开。");
        _fit?.TrySetException(error); _scan?.TrySetException(error); _curvesReady?.TrySetException(error);
        _fitOwned = _scanOwned = _hearingOwned = _previewOwned = false;
        if (State.Stage is not (CalibrationStage.Idle or CalibrationStage.Cancelled)) Fail(error);
    }
    private void Fail(Exception error)
    {
        DiagnosticLog.Error("Calibration", "workflow.failed", "The calibration workflow reported a failure.", error, State);
        if (State.Stage == CalibrationStage.Failed && error is OperationCanceledException
            || error is OperationCanceledException && State.Stage is CalibrationStage.Cancelling or CalibrationStage.Cancelled) return;
        Update(s => s with { Stage = error is OperationCanceledException ? CalibrationStage.Cancelled : CalibrationStage.Failed,
            ToneAcknowledged = false, CanResume = false, PreviewMode = CalibrationPreviewMode.Stopped,
            RawStatus = (error as CalibrationDeviceException)?.Status ?? s.RawStatus,
            Message = error is TimeoutException && s.Workflow == CalibrationWorkflow.Fit ? "贴合度检测超时，请调整佩戴并检查连接后重试。"
                : error is TimeoutException && s.Workflow == CalibrationWorkflow.EarScan ? "耳道扫描超时，请检查连接后重新扫描。" : error.Message });
    }
    private void Update(Func<CalibrationSnapshot, CalibrationSnapshot> transform)
    {
        CalibrationSnapshot snapshot;
        lock (_stateGate) { _state = transform(_state) with { Revision = ++_revision }; snapshot = _state; }
        DiagnosticLog.Debug("Calibration", "state.changed", "The calibration state changed.", snapshot);
        if (Changed is { } observers)
            foreach (Action<CalibrationSnapshot> observer in observers.GetInvocationList())
                try { observer(snapshot); } catch (Exception error) { DiagnosticLog.Error("Calibration", "observer.failed", "A calibration observer failed; the read pump continues.", error, new { snapshot.Generation, snapshot.Revision }); }
    }
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { await CancelAsync(); } catch (Exception error) { DiagnosticLog.Error("Calibration", "dispose.cancel-failed", "Calibration cleanup during disposal failed.", error, State); }
        _lifetime.Cancel(); _transport.Notification -= OnNotification; _transport.Disconnected -= OnDisconnected;
        _run?.Dispose(); _lifetime.Dispose();
    }
}
