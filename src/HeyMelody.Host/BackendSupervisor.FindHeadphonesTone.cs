using HeyMelody.Diagnostics;
using HeyMelodyWindows.App;

namespace HeyMelodyWindows.Host;

internal sealed partial class BackendSupervisor
{
    private sealed record FindToneLease(string Owner, long Session, long Generation, IPeerWorker Worker)
    {
        internal bool AwaitingStart { get; set; } = true;
    }

    private static bool HasActiveFindToneLocked(Peer peer) => peer.Spp?.FindHeadphonesTone?.IsActive == true
        || peer.FindTone is { AwaitingStart: true };

    public async Task<UiFindHeadphonesToneResult> SetFindHeadphonesToneAsync(string owner, string identity, long generation,
        FindHeadphonesToneArgs args, CancellationToken queuedCancellation)
    {
        if (args.Play is not { } play || args.Session <= 0) throw new InvalidDataException();
        Peer peer; lock (_sync) peer = FindLocked(identity);
        await peer.Commands.WaitAsync(queuedCancellation);
        try
        {
            FindToneLease lease;
            lock (_sync)
            {
                queuedCancellation.ThrowIfCancellationRequested();
                if (generation != peer.Generation || peer.Worker is not { } worker)
                    return new(false, null, play ? "耳机连接已变化，未提交播放指令。" : "连接已变化，未确认耳机是否停播，请勿佩戴。");
                if (play)
                {
                    if (!args.Confirmed || !EligibleLocked(peer) || !peer.Published.ControlReady || _resourceTransition
                        || HasCalibrationLocked(peer) || HasActiveFindToneLocked(peer)
                        || peer.Published.FindHeadphonesTone is not { CanStart: true } tone || tone.Session != args.Session)
                        return new(false, null, "未确认播放，或当前耳机状态不允许播放。");
                    if (tone.Worn && !args.ConfirmedWorn)
                        return new(false, null, "耳机报告已佩戴。请取下耳机后重新确认播放。");
                    lease = peer.FindTone = new(owner, args.Session, generation, worker);
                }
                else
                {
                    if (peer.FindTone is not { } active || active.Owner != owner || active.Session != args.Session)
                        return new(false, null, "");
                    lease = active;
                }
            }
            return await SendFindToneAsync(peer, lease, args);
        }
        finally { peer.Commands.Release(); }
    }

    private async Task<UiFindHeadphonesToneResult> SendFindToneAsync(Peer peer, FindToneLease lease, FindHeadphonesToneArgs args)
    {
        CancellationToken token;
        lock (_sync)
        {
            if (!ReferenceEquals(peer.FindTone, lease) || !ReferenceEquals(peer.Worker, lease.Worker) || peer.Generation != lease.Generation)
                return new(false, null, "耳机连接已变化，未提交指令。");
            token = peer.Lifetime.Token;
        }
        UiFindHeadphonesToneResult result;
        try
        {
            result = await lease.Worker.SetFindHeadphonesToneAsync(peer.Identity, lease.Generation, args, token);
        }
        catch (Exception error) when (error is IOException or OperationCanceledException or InvalidOperationException)
        {
            DiagnosticLog.Warning("host.find-tone", "worker-unavailable", "Find tone receipt is unavailable.", error);
            result = new(true, null, "未确认耳机是否仍在响铃，请勿佩戴。");
        }
        lock (_sync)
        {
            lease.AwaitingStart = false;
            if (peer.Generation != lease.Generation || !ReferenceEquals(peer.FindTone, lease))
                return result with { After = null };
            if (args.Play == false || result.AckStatus != 0) peer.FindTone = null;
            if (result.After is { } after && after.Identity == peer.Identity && after.ControlReady)
            {
                AdoptSppLocked(peer, after);
                return result with { After = Copy(peer.Published) };
            }
            return result with { After = null };
        }
    }

    private void StopOwnedFindTones(string owner, string? keepIdentity = null)
    {
        (Peer Peer, FindToneLease Lease)[] active;
        lock (_sync)
            active = _peers.Values.Where(peer => peer.Identity != keepIdentity && peer.FindTone?.Owner == owner)
                .Select(peer => (peer, peer.FindTone!)).ToArray();
        foreach (var (peer, lease) in active) _ = StopOwnedFindToneAsync(peer, lease);
    }

    private async Task StopOwnedFindToneAsync(Peer peer, FindToneLease lease)
    {
        await peer.Commands.WaitAsync();
        try { await SendFindToneAsync(peer, lease, new(lease.Session, false)); }
        catch (Exception error)
        {
            DiagnosticLog.Warning("host.find-tone", "cleanup-failed", "Could not complete the original client's find tone cleanup.", error);
        }
        finally { peer.Commands.Release(); }
    }
}
