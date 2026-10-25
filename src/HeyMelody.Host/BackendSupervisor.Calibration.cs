using HeyMelodyWindows.App;

namespace HeyMelodyWindows.Host;

internal sealed partial class BackendSupervisor
{
    private sealed class CalibrationLease(Peer peer, string owner, string handle, string kind, ICalibrationPeerWorker worker)
    {
        internal readonly Peer Peer = peer;
        internal readonly long Generation = peer.Generation;
        internal readonly ICalibrationPeerWorker Worker = worker;
        internal string Owner = owner;
        internal bool Claimed;
        internal bool StopUnconfirmed;
        internal long SpectrumEpoch = -1, SpectrumSequence;
        internal UiCalibrationState State = new() { Handle = handle, Workflow = kind, Identity = peer.Identity, Generation = peer.Generation };
    }
    private readonly Dictionary<string, CalibrationLease> _calibrations = [];
    internal event Action<string, BackendEvent>? CalibrationOwnerEventPublished;
    private static bool IsActiveCalibration(CalibrationLease lease) => lease.StopUnconfirmed || lease.State.Stage is not ("Completed" or "Failed" or "Cancelled");
    private bool HasCalibrationLocked(Peer peer) => _calibrations.Values.Any(l => l.Peer == peer && l.Generation == peer.Generation && IsActiveCalibration(l));
    private CalibrationLease FindCalibrationLocked(string owner, string handle, bool claim = false, string? role = null)
    {
        if (!_calibrations.TryGetValue(handle, out var lease)) throw new InvalidOperationException("此测试已结束或连接已变化。");
        if (lease.Owner != owner)
        {
            if (!claim || role != "calibration" || lease.Claimed) throw new InvalidOperationException("此测试属于另一个窗口。");
            // The random handle is handed directly to the independent component by its creator.
            lease.Owner = owner;
            lease.Claimed = true;
        }
        return lease;
    }
    private void ValidateCalibrationLocked(CalibrationLease lease)
    {
        if (lease.Generation != lease.Peer.Generation || !EligibleLocked(lease.Peer) || lease.Peer.Worker != lease.Worker)
            throw new InvalidOperationException("后台或连接已变化，未提交设置。");
    }
    public async Task<UiCalibrationState> CreateCalibrationAsync(string owner, string identity, long generation, string kind, CancellationToken token)
    {
        if (kind is not ("fit" or "hearing")) throw new InvalidDataException();
        Peer peer; lock (_sync) peer = FindLocked(identity);
        await peer.Commands.WaitAsync(token);
        CalibrationLease? lease = null;
        try
        {
            lock (_sync)
            {
                if (generation != peer.Generation || !EligibleLocked(peer) || !peer.Published.ControlReady
                    || peer.Worker is not ICalibrationPeerWorker worker || HasCalibrationLocked(peer) || _resourceTransition)
                    throw new InvalidOperationException("耳机正在执行其他测试，或当前连接不可用。");
                if (kind == "fit" ? !peer.Published.CanFitTest : !peer.Published.CanSetupHearing)
                    throw new InvalidOperationException("当前设备未声明此测试的必要能力。");
                foreach (var old in _calibrations.Where(x => !IsActiveCalibration(x.Value) && x.Value.Peer == peer).Select(x => x.Key).ToArray()) _calibrations.Remove(old);
                string handle = Guid.NewGuid().ToString("N");
                lease = new(peer, owner, handle, kind, worker); _calibrations.Add(handle, lease); PublishLocked(peer);
            }
            var state = await lease.Worker.CreateCalibrationAsync(lease.State.Handle, kind, peer.Lifetime.Token);
            lock (_sync) { ValidateCalibrationLocked(lease); AdoptCalibrationLocked(lease, state); return lease.State; }
        }
        catch
        {
            if (lease is not null) lock (_sync) { _calibrations.Remove(lease.State.Handle); PublishLocked(peer); }
            throw;
        }
        finally { peer.Commands.Release(); }
    }
    public UiCalibrationState GetCalibration(string owner, string handle, string? role)
    { lock (_sync) return FindCalibrationLocked(owner, handle, true, role).State; }
    public async Task<IReadOnlyList<UiPreviewAudioOutput>> GetPreviewAudioOutputsAsync(string owner, string handle, CancellationToken token)
    {
        CalibrationLease lease;
        lock (_sync) { lease = FindCalibrationLocked(owner, handle); ValidateCalibrationLocked(lease); }
        await lease.Peer.Commands.WaitAsync(token);
        try
        {
            lock (_sync) ValidateCalibrationLocked(lease);
            var outputs = await lease.Worker.GetPreviewAudioOutputsAsync(handle, token);
            lock (_sync) ValidateCalibrationLocked(lease);
            return outputs;
        }
        finally { lease.Peer.Commands.Release(); }
    }
    public async Task<UiCalibrationState> AdvanceCalibrationAsync(string owner, string handle, long revision, UiCalibrationInput input, CancellationToken token)
    {
        CalibrationLease lease; lock (_sync) lease = FindCalibrationLocked(owner, handle);
        if (input.Action == "cancel") return await CancelCalibrationAsync(owner, handle);
        await lease.Peer.Commands.WaitAsync(token);
        try
        {
            lock (_sync)
            {
                ValidateCalibrationLocked(lease);
                if (revision != lease.State.Revision) throw new InvalidOperationException("测试进度已变化，请按当前步骤操作。");
                if (_calibrations.Values.Any(x => x != lease && x.Peer == lease.Peer && IsActiveCalibration(x)))
                    throw new InvalidOperationException("另一项测试正在执行。");
                if (input.Action == "start")
                { lease.State = lease.State with { Stage = "Starting" }; PublishLocked(lease.Peer); }
            }
            var state = await lease.Worker.AdvanceCalibrationAsync(handle, revision, input, lease.Peer.Lifetime.Token);
            lock (_sync) { ValidateCalibrationLocked(lease); AdoptCalibrationLocked(lease, state); return lease.State; }
        }
        finally { lease.Peer.Commands.Release(); }
    }
    public async Task<UiCalibrationState> CancelCalibrationAsync(string owner, string handle)
    {
        CalibrationLease lease; lock (_sync) lease = FindCalibrationLocked(owner, handle);
        return await CancelLeaseAsync(lease);
    }
    private async Task<UiCalibrationState> CancelLeaseAsync(CalibrationLease lease)
    {
        lock (_sync) lease.StopUnconfirmed = IsActiveCalibration(lease);
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var state = await lease.Worker.CancelCalibrationAsync(lease.State.Handle, timeout.Token);
            lock (_sync)
            {
                lease.StopUnconfirmed = state.Stage != "Cancelled";
                // An earlier IPC failure may have published a synthetic revision.
                // This correlated STOP response still establishes its result.
                if (state.Revision <= lease.State.Revision) state = state with { Revision = lease.State.Revision + 1 };
                AdoptCalibrationLocked(lease, state); return lease.State;
            }
        }
        catch
        {
            lock (_sync)
            {
                if (lease.State.Stage == "Cancelled") { lease.StopUnconfirmed = false; return lease.State; }
                lease.StopUnconfirmed = true;
                lease.State = lease.State with { Stage = "Failed", Revision = lease.State.Revision + 1, Message = "停止测试尚未得到确认，请重新连接耳机后核实。" };
                EmitLocked(new() { Kind = "calibration", Calibration = lease.State }); PublishLocked(lease.Peer); return lease.State;
            }
        }
    }
    private async Task CancelAllCalibrationsAsync()
    {
        CalibrationLease[] leases; lock (_sync) leases = _calibrations.Values.Where(IsActiveCalibration).ToArray();
        await Task.WhenAll(leases.Select(CancelLeaseAsync));
    }
    private void ReceiveCalibration(Peer peer, long generation, UiCalibrationState state)
    {
        lock (_sync)
        {
            if (generation != peer.Generation || !_calibrations.TryGetValue(state.Handle, out var lease) || lease.Peer != peer) return;
            AdoptCalibrationLocked(lease, state);
        }
    }
    private void ReceiveCalibrationSpectrum(Peer peer, long generation, UiCalibrationSpectrum spectrum)
    {
        lock (_sync)
        {
            if (generation != peer.Generation || !_calibrations.TryGetValue(spectrum.Handle, out var lease)
                || lease.Peer != peer || lease.Worker != peer.Worker || lease.Generation != generation || !IsActiveCalibration(lease)
                || lease.State.Workflow != "hearing" || lease.State.Stage != "Previewing" || !lease.State.PreviewAudioPlaying
                || lease.State.PreviewMode is not ("Original" or "Enhanced") || spectrum.PreviewEpoch != lease.State.PreviewEpoch
                || spectrum.Generation != generation || spectrum.Sequence <= 0 || spectrum.Levels.Count != 6
                || spectrum.Levels.Any(x => !double.IsFinite(x) || x < 0 || x > 1)) return;
            if (lease.SpectrumEpoch == spectrum.PreviewEpoch && spectrum.Sequence <= lease.SpectrumSequence) return;
            lease.SpectrumEpoch = spectrum.PreviewEpoch; lease.SpectrumSequence = spectrum.Sequence;
            var qualified = spectrum with { BackendInstanceId = InstanceId, Generation = generation, Levels = spectrum.Levels.ToArray() };
            CalibrationOwnerEventPublished?.Invoke(lease.Owner, new() { Kind = "calibrationSpectrum", BackendInstanceId = InstanceId,
                CalibrationSpectrum = qualified });
        }
    }
    private void AdoptCalibrationLocked(CalibrationLease lease, UiCalibrationState state)
    {
        if (state.Revision < lease.State.Revision) return;
        if (state.Stage == "Cancelled") lease.StopUnconfirmed = false;
        lease.State = state with { BackendInstanceId = InstanceId, Generation = lease.Generation, Identity = lease.Peer.Identity };
        EmitLocked(new() { Kind = "calibration", Calibration = lease.State }); PublishLocked(lease.Peer);
        if (_catalogPending && !_calibrations.Values.Any(IsActiveCalibration)) _ = AdoptResourcesSafelyAsync(true);
    }
    private void InvalidateCalibrationLocked(Peer peer)
    {
        foreach (var lease in _calibrations.Values.Where(x => x.Peer == peer && IsActiveCalibration(x)))
        {
            lease.StopUnconfirmed = false;
            lease.State = lease.State with { Stage = "Cancelled", Revision = lease.State.Revision + 1,
                PreviewAudioPlaying = false, PreviewMode = "Stopped", PreviewEpoch = lease.State.PreviewEpoch + 1,
                PreviewAudioError = "", Message = "设备连接已变化，测试已中断。" };
            EmitLocked(new() { Kind = "calibration", Calibration = lease.State });
        }
    }
    public async Task<IReadOnlyList<UiHearingProfile>> HearingProfilesAsync(string identity, long generation, CancellationToken token)
    {
        Peer peer; lock (_sync) peer = FindLocked(identity);
        await peer.Commands.WaitAsync(token);
        try
        {
            ICalibrationPeerWorker worker;
            lock (_sync)
            { if (generation != peer.Generation || peer.Worker is not ICalibrationPeerWorker target || !EligibleLocked(peer)) throw new InvalidOperationException("后台或连接已变化，未提交设置。"); worker = target; }
            var profiles = await worker.HearingProfilesAsync(peer.Lifetime.Token);
            lock (_sync)
            {
                if (peer.Generation == generation && peer.Spp is { } spp)
                { spp.HearingProfiles = profiles; RememberPresentationLocked(peer); PublishLocked(peer); }
            }
            return profiles;
        }
        finally { peer.Commands.Release(); }
    }
    public async Task<UiApplyResult> SelectHearingAsync(string identity, long generation, int? uid, CancellationToken token)
    {
        Peer peer; lock (_sync) peer = FindLocked(identity);
        await peer.Commands.WaitAsync(token);
        try
        {
            ICalibrationPeerWorker worker;
            lock (_sync)
            {
                if (generation != peer.Generation || peer.Worker is not ICalibrationPeerWorker target || !peer.Published.ControlReady || HasCalibrationLocked(peer) || _resourceTransition)
                    throw new InvalidOperationException("后台或连接已变化，未提交设置。"); worker = target;
            }
            var result = await worker.SelectHearingAsync(uid, peer.Lifetime.Token);
            lock (_sync)
            {
                if (generation == peer.Generation && result.After is { } after) { AdoptSppLocked(peer, after); result.After = Copy(peer.Published); }
            }
            return result;
        }
        finally { peer.Commands.Release(); }
    }
    public void ClientDetached(string owner)
    {
        CalibrationLease[] leases;
        lock (_sync)
        {
            foreach (var id in _artworkLeases.Where(x => x.Value.Owner == owner).Select(x => x.Key).ToArray()) ReleaseArtwork(owner, id);
            leases = _calibrations.Values.Where(x => x.Owner == owner && IsActiveCalibration(x)).ToArray();
        }
        foreach (var lease in leases) _ = CancelLeaseAsync(lease);
    }
}
