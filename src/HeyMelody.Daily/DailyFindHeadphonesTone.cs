using HeyMelody.Diagnostics;
using HeyMelodyWindows.Protocol;

namespace HeyMelodyWindows.Daily;

public enum DailyFindHeadphonesTonePhase { Idle, Starting, Ringing, Stopping, Unconfirmed }

public sealed record DailyFindHeadphonesToneResult(bool Submitted, byte? AckStatus, string Message, DailySnapshot? After = null);

public sealed partial class DailyClient
{
    private sealed class FindToneAttempt(DailySession session, bool confirmedWorn)
    {
        internal readonly DailySession Session = session;
        internal readonly bool ConfirmedWorn = confirmedWorn;
        internal readonly CancellationTokenSource Deadline = new();
        internal bool WearStopQueued;
    }

    private readonly TimeProvider _findToneTime = timeProvider ?? TimeProvider.System;
    private FindToneAttempt? _findTone;
    private long _findToneSession;
    private DailyFindHeadphonesTonePhase _findTonePhase;

    public bool FindHeadphonesToneActive { get { lock (_stateGate) return _findTone is not null; } }

    public async Task<DailyFindHeadphonesToneResult> SetFindHeadphonesToneAsync(long session, bool play,
        bool confirmed, bool confirmedWorn, CancellationToken token)
    {
        await _gate.WaitAsync(token);
        try
        {
            token.ThrowIfCancellationRequested();
            FindToneAttempt attempt;
            DailySnapshot? starting = null;
            lock (_stateGate)
            {
                if (_disposed || _disposeStarted != 0 || session <= 0 || session != _findToneSession
                    || _session is not { Usable: true } transport)
                    return new(false, null, "耳机控制连接已变化，未提交指令。");
                if (!play)
                {
                    if (_findTone is not { } active) return new(false, null, "", _latest is { } latest ? Clone(latest) : null);
                    attempt = active;
                }
                else
                {
                    if (!confirmed || _calibrationActive || _findTonePhase != DailyFindHeadphonesTonePhase.Idle
                        || transport.Remaining < 2 || _latest is not { Connected: true, ProfileVerified: true, Error: null } snapshot
                        || snapshot.FindHeadphonesToneSession != session || !snapshot.Capabilities.FindHeadphonesTone)
                        return new(false, null, "未确认播放，或当前耳机状态不允许播放。");
                    if (snapshot.FindHeadphonesWorn && !confirmedWorn)
                        return new(false, null, "耳机报告已佩戴。请取下耳机后重新确认播放。");
                    attempt = _findTone = new(transport, confirmedWorn && snapshot.FindHeadphonesWorn);
                    starting = SetFindTonePhaseLocked(DailyFindHeadphonesTonePhase.Starting);
                }
            }
            if (!play) return await StopFindHeadphonesToneCoreAsync(attempt, token);
            if (starting is not null) Publish(starting);
            DailySnapshot? cancelled = null;
            lock (_stateGate)
            {
                if (!ReferenceEquals(_findTone, attempt)) return new(false, null, "耳机控制连接已变化，未提交指令。");
                if (attempt.WearStopQueued || token.IsCancellationRequested)
                    cancelled = FinishFindToneLocked(attempt, DailyFindHeadphonesTonePhase.Idle);
            }
            if (cancelled is not null)
            {
                Publish(cancelled);
                return new(false, null, "播放条件已变化，请重新确认。", cancelled);
            }
            return await SendFindHeadphonesToneAsync(attempt, play: true, token);
        }
        finally { _gate.Release(); }
    }

    private async Task<DailyFindHeadphonesToneResult> StopFindHeadphonesToneCoreAsync(FindToneAttempt attempt, CancellationToken token)
    {
        DailySnapshot? stopping;
        lock (_stateGate)
        {
            if (!ReferenceEquals(_findTone, attempt)) return new(false, null, "");
            stopping = SetFindTonePhaseLocked(DailyFindHeadphonesTonePhase.Stopping);
        }
        if (stopping is not null) Publish(stopping);
        return await SendFindHeadphonesToneAsync(attempt, play: false, token);
    }

    private async Task<DailyFindHeadphonesToneResult> SendFindHeadphonesToneAsync(FindToneAttempt attempt, bool play, CancellationToken token)
    {
        DailyResponse? reply = null;
        try
        {
            if (ReferenceEquals(_session, attempt.Session) && attempt.Session.Usable)
                reply = await attempt.Session.RequestAsync(FindHeadphonesToneCodec.Command, FindHeadphonesToneCodec.BuildPayload(play), token);
        }
        catch (IOException error) when (!attempt.Session.Usable)
        {
            DiagnosticLog.Warning("Daily", "find-tone.connection-lost", "The original find tone session is no longer usable.", error);
        }

        byte? ack = reply is { Matched: true } ? reply.Status : null;
        bool submitted = reply?.Submitted == true;
        DailySnapshot? changed = null;
        lock (_stateGate)
        {
            if (ReferenceEquals(_findTone, attempt))
            {
                if (play && ack == 0 && attempt.Session.Usable)
                {
                    changed = SetFindTonePhaseLocked(DailyFindHeadphonesTonePhase.Ringing);
                    _ = StopFindToneAtDeadlineAsync(attempt, attempt.Deadline.Token);
                }
                else
                {
                    bool uncertain = play ? submitted && ack is null : ack != 0;
                    changed = FinishFindToneLocked(attempt, uncertain
                        ? DailyFindHeadphonesTonePhase.Unconfirmed : DailyFindHeadphonesTonePhase.Idle);
                }
            }
        }
        if (changed is not null) Publish(changed);
        string message = ack switch
        {
            0 => "",
            null when play && !submitted => "耳机控制连接已变化，未提交播放指令。",
            null => play ? "未确认耳机是否开始响铃，请勿佩戴。" : "未确认耳机是否停止响铃，请勿佩戴。",
            _ => play ? "耳机拒绝了播放指令。" : "耳机拒绝了停止指令，未确认已停播，请勿佩戴。"
        };
        return new(submitted, ack, message, changed);
    }

    private async Task StopFindToneAtDeadlineAsync(FindToneAttempt attempt, CancellationToken token)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(50), _findToneTime, token); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
        await StopFindToneInBackgroundAsync(attempt);
    }

    private async Task StopFindToneInBackgroundAsync(FindToneAttempt attempt)
    {
        await _gate.WaitAsync();
        try { await StopFindHeadphonesToneCoreAsync(attempt, CancellationToken.None); }
        finally { _gate.Release(); }
    }

    private FindToneAttempt? FindToneWearStopLocked(DailySession source, DailySnapshot snapshot)
    {
        if (_findTone is not { ConfirmedWorn: false, WearStopQueued: false } attempt
            || !ReferenceEquals(attempt.Session, source) || !snapshot.FindHeadphonesWorn
            || _findTonePhase is not (DailyFindHeadphonesTonePhase.Starting or DailyFindHeadphonesTonePhase.Ringing)) return null;
        attempt.WearStopQueued = true;
        return attempt;
    }

    private void InterruptFindToneLocked(DailySession? source)
    {
        if (_findTone is { } attempt && ReferenceEquals(attempt.Session, source))
            FinishFindToneLocked(attempt, DailyFindHeadphonesTonePhase.Unconfirmed);
    }

    private DailySnapshot? FinishFindToneLocked(FindToneAttempt attempt, DailyFindHeadphonesTonePhase phase)
    {
        _findTone = null;
        attempt.Deadline.Cancel();
        attempt.Deadline.Dispose();
        var changed = SetFindTonePhaseLocked(phase);
        if (_disposeStarted == 0 && _invalidated.Count != 0 && _session is { Usable: true })
            QueueRefresh(_invalidated.First());
        return changed;
    }

    private DailySnapshot? SetFindTonePhaseLocked(DailyFindHeadphonesTonePhase phase)
    {
        _findTonePhase = phase;
        if (_latest is null) return null;
        _latest.FindHeadphonesTonePhase = phase;
        _latest.Revision = ++_revision;
        return Clone(_latest);
    }
}
