using HeyMelody.Diagnostics;

namespace HeyMelodyWindows.App;

public sealed partial class MainViewModel
{
    private const string FindToneWarning = "佩戴耳机时播放提示音可能造成听力受损，请先取下耳机。";
    private const string FindToneUnconfirmed = "未确认耳机是否仍在响铃，请勿佩戴。";
    private sealed record FindToneConfirmation(FindHeadphonesToneTarget Target, bool Worn, CancellationTokenSource Cancellation);

    private FindToneConfirmation? _findToneConfirmation;
    private FindHeadphonesToneTarget? _findToneAction, _findToneSending;
    private FindHeadphonesToneTarget? _findToneNoticeTarget;
    private string? _findToneNotice, _findToneInterruptedIdentity;

    private UiFindHeadphonesToneState? LiveFindTone => !IsDemoSelected && _liveSnapshot?.Identity == SelectedDevice?.Identity
        ? _liveSnapshot?.FindHeadphonesTone : null;

    private FindHeadphonesToneTarget? CurrentFindToneTarget => HasVerifiedSnapshot && LiveFindTone is { Session: > 0 } tone
        && _liveSnapshot is { BackendInstanceId.Length: > 0 } snapshot
        ? new(snapshot.BackendInstanceId, snapshot.Identity, snapshot.DeviceGeneration, tone.Session) : null;

    public bool CanToggleFindHeadphonesTone => !_preview && _service is IFindHeadphonesToneService && !Closing && !_lifecyclePending
        && CurrentFindToneTarget is { } target && _findToneAction != target
        && (LiveFindTone?.CanStart == true || LiveFindTone?.Phase == FindHeadphonesTonePhase.Ringing);

    public string FindHeadphonesToneButtonText => LiveFindTone?.Phase switch
    {
        FindHeadphonesTonePhase.Starting => "正在启动…",
        FindHeadphonesTonePhase.Ringing => "停止",
        FindHeadphonesTonePhase.Stopping => "正在停止…",
        _ => "播放"
    };

    public string FindHeadphonesToneDescription
    {
        get
        {
            if (LiveFindTone?.Phase == FindHeadphonesTonePhase.Unconfirmed
                || _findToneInterruptedIdentity is not null && _findToneInterruptedIdentity == SelectedDevice?.Identity && !HasVerifiedSnapshot)
                return FindToneUnconfirmed;
            if (_findToneNoticeTarget is not null && _findToneNoticeTarget == CurrentFindToneTarget && _findToneNotice is { Length: > 0 } notice)
                return notice;
            if (LiveFindTone?.Phase == FindHeadphonesTonePhase.Ringing) return "耳机已接受播放指令。";
            return IsDemoSelected || !HasVerifiedSnapshot ? "连接支持的耳机后可播放"
                : LiveFindTone is null ? "当前耳机不支持播放查找提示音" : "让耳机响铃以便查找";
        }
    }

    public async Task ToggleFindHeadphonesToneAsync()
    {
        if (!CanToggleFindHeadphonesTone || _service is not IFindHeadphonesToneService service
            || CurrentFindToneTarget is not { } target || LiveFindTone is not { } tone) return;
        bool play = tone.CanStart;
        var sessionToken = _sessionWork.Token;
        _findToneAction = target;
        _findToneNotice = null;
        NotifyState();
        try
        {
            if (play)
            {
                using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(sessionToken);
                var confirmation = new FindToneConfirmation(target, tone.Worn, cancellation);
                _findToneConfirmation = confirmation;
                bool confirmed;
                try
                {
                    string warning = tone.Worn ? "耳机报告仍在佩戴。" + FindToneWarning : FindToneWarning;
                    confirmed = await Dialogs.RunAsync(token => _confirmations.ConfirmFindHeadphonesToneAsync(warning, token), cancellation.Token);
                }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { return; }
                finally { if (ReferenceEquals(_findToneConfirmation, confirmation)) _findToneConfirmation = null; }
                if (!confirmed || cancellation.IsCancellationRequested || Closing || _lifecyclePending
                    || target != CurrentFindToneTarget || LiveFindTone is not { CanStart: true } current || current.Worn && !tone.Worn) return;
            }
            if (sessionToken.IsCancellationRequested || Closing || target != CurrentFindToneTarget) return;
            _findToneSending = target;
            var result = await service.SetFindHeadphonesToneAsync(target, play, confirmed: play, confirmedWorn: play && tone.Worn, sessionToken);
            if (Closing || target != CurrentFindToneTarget) return;
            _findToneNoticeTarget = target;
            _findToneNotice = result.Message;
            if (result.After is { } after && after.Identity == target.Identity && after.BackendInstanceId == target.BackendInstanceId
                && after.DeviceGeneration == target.DeviceGeneration)
                AdoptRead(after);
        }
        catch (OperationCanceledException) when (sessionToken.IsCancellationRequested) { }
        catch (Exception error)
        {
            DiagnosticLog.Warning("client.find-tone", "operation-failed", "Find tone operation did not complete.", error);
            if (!Closing && target == CurrentFindToneTarget)
            {
                _findToneNoticeTarget = target;
                _findToneNotice = ReferenceEquals(_findToneSending, target) ? FindToneUnconfirmed : "未能显示播放确认。";
            }
        }
        finally
        {
            if (ReferenceEquals(_findToneSending, target)) _findToneSending = null;
            if (ReferenceEquals(_findToneAction, target)) _findToneAction = null;
            NotifyState();
        }
    }

    private void UpdateFindTonePresentation()
    {
        if (_findToneConfirmation is not { } confirmation) return;
        if (Closing || CurrentFindToneTarget != confirmation.Target || LiveFindTone is not { CanStart: true }
            || !confirmation.Worn && LiveFindTone.Worn)
            confirmation.Cancellation.Cancel();
    }

    private void RememberInterruptedFindTone()
    {
        if (SelectedDevice is { } selected && (LiveFindTone?.IsActive == true || _findToneSending is { } pending && pending.Identity == selected.Identity))
            _findToneInterruptedIdentity = selected.Identity;
    }

    private async Task StopFindToneForDeviceSwitchAsync()
    {
        if (CurrentFindToneTarget is not { } target || _service is not IFindHeadphonesToneService service
            || LiveFindTone?.IsActive != true && _findToneSending != target) return;
        try { await service.SetFindHeadphonesToneAsync(target, play: false, confirmed: false, confirmedWorn: false, _lifetime.Token); }
        catch (Exception error)
        {
            DiagnosticLog.Warning("client.find-tone", "switch-cleanup-failed", "Find tone cleanup did not complete before device switch.", error);
        }
    }
}
