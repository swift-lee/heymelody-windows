using HeyMelodyWindows.App;
using HeyMelodyWindows.Calibration;

using HeyMelody.Diagnostics;

namespace HeyMelodyWindows.Host;

public sealed partial class NativeHeadsetService
{
    private ICalibrationAuditionPlayer? _auditionPlayer;
    private CancellationTokenSource? _auditionRun;
    private readonly SemaphoreSlim _auditionGate = new(1, 1);
    private bool _auditionClosing;
    private bool _auditionTransitioning;
    private bool _auditionTransitionFailed;
    private long _auditionTransitionEpoch;
    private int _auditionStreamPlaying;
    private Task _auditionTransitionStop = Task.CompletedTask;
    private readonly object _auditionStopSync = new();
    private long _previewEpoch, _spectrumSequence;
    public event Action<UiCalibrationSpectrum>? CalibrationSpectrumChanged;

    private long InvalidateAudition()
    {
        CancellationTokenSource? previous;
        long epoch;
        lock (_auditionStopSync)
        {
            previous = Interlocked.Exchange(ref _auditionRun, null);
            epoch = Interlocked.Increment(ref _previewEpoch);
        }
        try { previous?.Cancel(); } catch (ObjectDisposedException) { }
        // The operation owns disposal. Cancellation may arrive while it is awaiting endpoint preparation.
        return epoch;
    }
    private void PublishAuditionState(bool playing, string error = "")
    {
        lock (_auditionStopSync)
        {
            if (_calibrationState is not { } state) return;
            _calibrationState = state with { PreviewEpoch = _previewEpoch, PreviewAudioPlaying = playing,
                PreviewAudioError = error, Revision = ++_calibrationRevision };
            DiagnosticLog.Debug("host.audio", "state", "Audition state changed",
                new { epoch = _previewEpoch, playing, error, state.Handle, state.Stage, state.PreviewMode });
            CalibrationChanged?.Invoke(_calibrationState);
        }
    }
    private async Task PreviewAuditionAsync(CalibrationPreviewMode mode, CancellationToken token)
    {
        long epoch = InvalidateAudition();
        DiagnosticLog.Info("host.audio", "previewRequested", "Changing audition playback mode", new { epoch, mode });
        using var run = CancellationTokenSource.CreateLinkedTokenSource(token);
        CancellationToken runToken = run.Token;
        await _auditionGate.WaitAsync(token);
        try
        {
            if (_auditionClosing || epoch != Interlocked.Read(ref _previewEpoch)) throw new OperationCanceledException(runToken);
            lock (_auditionStopSync)
            {
                _auditionTransitionStop = Task.CompletedTask;
                _auditionTransitionEpoch = epoch; _auditionTransitionFailed = false;
            }
            Interlocked.Exchange(ref _auditionRun, run);
            if (epoch != Interlocked.Read(ref _previewEpoch)) run.Cancel();
            var engine = _calibration ?? throw new InvalidOperationException("耳机连接已变化。");
            if (mode == CalibrationPreviewMode.Stopped)
            {
                PublishAuditionState(false);
                bool stopSubmitted = false;
                try
                {
                    Volatile.Write(ref _auditionStreamPlaying, 0);
                    if (_auditionPlayer is { } paused) await paused.PauseAsync(runToken);
                    stopSubmitted = true;
                    await engine.PreviewAsync(mode, runToken); return;
                }
                catch (Exception error)
                {
                    if (error is not OperationCanceledException)
                        DiagnosticLog.Error("host.audio", "pauseFailed", "Audition could not pause", error, new { epoch, mode, stopSubmitted });
                    if (_auditionPlayer is { } failed)
                    {
                        if (error is OperationCanceledException) await StopPlayerAsync(failed);
                        else await DiscardPlayerAsync(failed);
                    }
                    if (!stopSubmitted)
                        try { await engine.PreviewAsync(mode, CancellationToken.None); } catch { }
                    if (epoch == Interlocked.Read(ref _previewEpoch)) PublishAuditionState(false, "试听已停止，请检查耳机音频连接后重试。");
                    if (error is OperationCanceledException) throw;
                    throw new InvalidOperationException("试听未能暂停，请检查耳机音频连接后重试。");
                }
            }
            var player = _auditionPlayer ??= CreateAuditionPlayer();
            bool keepPlaying = Volatile.Read(ref _auditionStreamPlaying) != 0
                && _calibrationState is { PreviewAudioPlaying: true, Stage: "Previewing" };
            lock (_auditionStopSync) _auditionTransitioning = true;
            if (!keepPlaying) PublishAuditionState(false);
            bool dspStarted = false;
            try
            {
                if (!keepPlaying)
                {
                    Volatile.Write(ref _auditionStreamPlaying, 0);
                    await player.PauseAsync(runToken);
                    await player.PrepareAsync(_selectedAddress ?? throw new InvalidOperationException("耳机连接已变化。"), runToken);
                }
                runToken.ThrowIfCancellationRequested();
                ThrowIfAuditionTransitionFailed(epoch);
                await engine.PreviewAsync(mode, runToken); dspStarted = true;
                runToken.ThrowIfCancellationRequested();
                ThrowIfAuditionTransitionFailed(epoch);
                if (epoch != Interlocked.Read(ref _previewEpoch)) throw new OperationCanceledException(runToken);
                if (!keepPlaying) await player.PlayAsync(runToken);
                runToken.ThrowIfCancellationRequested();
                if (epoch != Interlocked.Read(ref _previewEpoch)) throw new OperationCanceledException(runToken);
                lock (_auditionStopSync)
                {
                    ThrowIfAuditionTransitionFailed(epoch);
                    runToken.ThrowIfCancellationRequested();
                    if (epoch != Interlocked.Read(ref _previewEpoch)) throw new OperationCanceledException(runToken);
                    Volatile.Write(ref _auditionStreamPlaying, 1);
                    PublishAuditionState(true);
                }
            }
            catch (Exception error)
            {
                if (error is not OperationCanceledException)
                    DiagnosticLog.Error("host.audio", "previewFailed", "Audition playback or DSP transition failed", error,
                        new { epoch, mode, dspStarted, keepPlaying });
                // Still inside the gate: a later epoch can cancel this operation but cannot start a newer stream yet.
                await _auditionTransitionStop;
                if (error is OperationCanceledException) await StopPlayerAsync(player);
                else await DiscardPlayerAsync(player);
                if (dspStarted)
                    try { await engine.PreviewAsync(CalibrationPreviewMode.Stopped, CancellationToken.None); }
                    catch { /* Engine preserves unconfirmed STOP ownership and the calibration lease. */ }
                if (epoch == Interlocked.Read(ref _previewEpoch))
                    PublishAuditionState(false, "试听未能播放，请检查耳机音频连接后重试。");
                if (error is OperationCanceledException) throw new OperationCanceledException(runToken);
                throw new InvalidOperationException("试听未能播放，请检查耳机音频连接后重试。");
            }
        }
        finally
        {
            // A failed DSP update silences the stream before device STOP can wait for an ACK.
            // Drain that local task here so its effects cannot reach the next operation.
            Task silence;
            lock (_auditionStopSync) { _auditionTransitioning = false; silence = _auditionTransitionStop; }
            try { await silence; }
            finally { Interlocked.CompareExchange(ref _auditionRun, null, run); _auditionGate.Release(); }
        }
    }
    private ICalibrationAuditionPlayer CreateAuditionPlayer()
    {
        var player = _auditionFactory();
        player.Spectrum += levels =>
        {
            var state = _calibrationState;
            if (Volatile.Read(ref _auditionTransitioning) || !ReferenceEquals(_auditionPlayer, player) || state is not { PreviewAudioPlaying: true, Stage: "Previewing" }
                || state.PreviewMode is not ("Original" or "Enhanced") || state.PreviewEpoch != Interlocked.Read(ref _previewEpoch)
                || levels.Count != 6 || levels.Any(x => !double.IsFinite(x) || x < 0 || x > 1)) return;
            CalibrationSpectrumChanged?.Invoke(new(state.Handle, "", state.Generation, state.PreviewEpoch,
                Interlocked.Increment(ref _spectrumSequence), levels.ToArray()));
        };
        player.Failed += () =>
        {
            if (!ReferenceEquals(_auditionPlayer, player)) return;
            Volatile.Write(ref _auditionStreamPlaying, 0);
            lock (_auditionStopSync)
            {
                // A/B keeps the old displayed mode/epoch until ACK, but the ongoing stream belongs to this transition.
                if (_auditionTransitioning && _auditionTransitionEpoch == Interlocked.Read(ref _previewEpoch))
                {
                    _auditionTransitionFailed = true;
                    SilenceAuditionTransition();
                    PublishAuditionState(false, "试听已停止，耳机音频输出不可用，请检查连接后重试。");
                    return;
                }
            }
            var state = _calibrationState;
            if (state is not { PreviewAudioPlaying: true } || state.PreviewEpoch != Interlocked.Read(ref _previewEpoch)) return;
            _ = HandleAuditionFailureAsync(player, state.PreviewEpoch);
        };
        return player;
    }
    private async Task StopLocalAuditionAsync(bool dispose = false)
    {
        if (dispose) _auditionClosing = true;
        long epoch = InvalidateAudition(); // Cancel preparation before waiting for the operation gate.
        SilenceAuditionTransition();
        await _auditionGate.WaitAsync();
        try
        {
            if (!dispose && epoch != Interlocked.Read(ref _previewEpoch)) return;
            if (_calibrationState is { } state && (state.PreviewAudioPlaying || state.PreviewEpoch != _previewEpoch)) PublishAuditionState(false);
            var player = dispose ? Interlocked.Exchange(ref _auditionPlayer, null) : _auditionPlayer;
            if (player is null) return;
            await StopPlayerAsync(player);
            if (dispose) try { await player.DisposeAsync(); } catch { }
        }
        finally { _auditionGate.Release(); }
    }
    private async Task StopPlayerAsync(ICalibrationAuditionPlayer player)
    {
        Volatile.Write(ref _auditionStreamPlaying, 0);
        try { await player.StopAsync(); }
        catch (Exception error)
        {
            DiagnosticLog.Error("host.audio", "stopFailed", "Local audition player could not stop and will be discarded", error);
            Interlocked.CompareExchange(ref _auditionPlayer, null, player);
            try { await player.DisposeAsync(); } catch { }
        }
    }
    private async Task DiscardPlayerAsync(ICalibrationAuditionPlayer player)
    {
        await StopPlayerAsync(player);
        Interlocked.CompareExchange(ref _auditionPlayer, null, player);
        try { await player.DisposeAsync(); } catch { }
    }
    private void ThrowIfAuditionTransitionFailed(long epoch)
    {
        lock (_auditionStopSync)
            if (_auditionTransitionEpoch == epoch && _auditionTransitionFailed)
                throw new IOException("试听音频输出已停止。");
    }
    private void SilenceAuditionTransition()
    {
        lock (_auditionStopSync)
            if (_auditionTransitioning && _auditionTransitionStop.IsCompleted && _auditionPlayer is { } player)
                _auditionTransitionStop = StopPlayerAsync(player);
    }
    private async Task HandleAuditionFailureAsync(ICalibrationAuditionPlayer player, long failedEpoch)
    {
        await _auditionGate.WaitAsync();
        try
        {
            if (!ReferenceEquals(_auditionPlayer, player) || failedEpoch != Interlocked.Read(ref _previewEpoch)
                || _calibrationState is not { PreviewAudioPlaying: true }) return;
            long epoch = InvalidateAudition();
            PublishAuditionState(false);
            await DiscardPlayerAsync(player);
            if (_calibration is { } engine)
                try { await engine.PreviewAsync(CalibrationPreviewMode.Stopped); } catch { }
            if (epoch == Interlocked.Read(ref _previewEpoch))
                PublishAuditionState(false, "试听已停止，耳机音频输出不可用，请检查连接后重试。");
        }
        finally { _auditionGate.Release(); }
    }
    private async Task StopTerminalAuditionAsync(long epoch)
    {
        await _auditionGate.WaitAsync();
        try
        {
            if (epoch == Interlocked.Read(ref _previewEpoch) && _auditionPlayer is { } player) await StopPlayerAsync(player);
        }
        finally { _auditionGate.Release(); }
    }
}
