using System.Text.Json;
using HeyMelodyWindows.App;
using HeyMelodyWindows.Windows;

using HeyMelody.Diagnostics;

namespace HeyMelodyWindows.Host;

internal sealed record BroadcastObservation(string ProductId, CaseLidState Lid, EarbudPlacement Left,
    EarbudPlacement Right, int? LeftBattery, int? RightBattery, int? CaseBattery,
    bool? LeftCharging, bool? RightCharging, bool? CaseCharging, DateTimeOffset ReceivedAt)
{
    internal DateTimeOffset? CaseTime { get; init; }
    internal DateTimeOffset? LeftTime { get; init; }
    internal DateTimeOffset? RightTime { get; init; }
    internal DateTimeOffset? LeftBatteryTime { get; init; }
    internal DateTimeOffset? RightBatteryTime { get; init; }
    internal DateTimeOffset? CaseBatteryTime { get; init; }
    internal byte? Color { get; init; }
    internal DateTimeOffset? ColorTime { get; init; }
}

/// <summary>The single owner of device identities, policy and published state. Native work lives in disposable child processes.</summary>
internal sealed partial class BackendSupervisor : IAsyncDisposable
{
    private sealed record PeerDiagnostic(long Generation, string Message);
    private sealed class Peer(BluetoothCandidate candidate)
    {
        internal BluetoothCandidate Candidate = candidate;
        internal readonly string Identity = Guid.NewGuid().ToString("N");
        internal long Generation = 1, Revision, SppRevision;
        internal readonly Dictionary<long, long> PublicSppRevisions = [];
        internal bool Present = true, Allowed, Paused, Initializing;
        internal int Failures;
        internal MonitoringState State = MonitoringState.WaitingForConnection;
        internal UiSnapshot? Spp;
        internal PeerDiagnostic? Diagnostic;
        internal string? VerifiedProductId;
        internal CaseLidState LastObservedLid;
        internal DateTimeOffset? LastLidObservationTime;
        internal UiSnapshot Published = new();
        internal IPeerWorker? Worker;
        internal BroadcastObservation? Broadcast;
        // Display-only memory of the model and color, persisted per whitelist key.
        internal string? AppearanceProductId;
        internal byte? AppearanceColor;
        internal bool AppearanceColorFromSpp;
        // One timer per peer, re-armed through Change; Due is the deadline it stands for (null when disarmed).
        internal ITimer? BroadcastTimer;
        internal DateTimeOffset? BroadcastDue;
        internal CancellationTokenSource Lifetime = new();
        internal readonly SemaphoreSlim Commands = new(1);
    }

    private readonly object _sync = new();
    private readonly SemaphoreSlim _catalogGate = new(1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Dictionary<ulong, Peer> _peers = [];
    private readonly Dictionary<ulong, bool> _osConnections = [];
    private readonly Dictionary<ulong, bool> _osPresent = [];
    private readonly DeviceWhitelistStore _whitelist;
    private readonly DeviceAppearanceStore _appearance;
    private readonly DeviceStateStore _deviceState;
    private readonly Func<CancellationToken, Task<IReadOnlyList<BluetoothCandidate>>> _enumerate;
    private readonly Func<IPeerWorker> _workerFactory;
    private readonly TimeProvider _time;
    private HashSet<string> _allowed = new(StringComparer.Ordinal);
    private string? _catalogWarning;
    private bool _radioAvailable = true, _suspended, _stopping, _disposed, _scanDemand;
    public string InstanceId { get; } = Guid.NewGuid().ToString("N");
    public event Action<BackendEvent>? EventPublished;
    /// <summary>Raised on the thread pool when <see cref="HasScanDemand"/> may have changed; handlers re-read it (latest wins).</summary>
    public event Action? ScanDemandChanged;

    internal BackendSupervisor(DeviceWhitelistStore? whitelist = null,
        Func<CancellationToken, Task<IReadOnlyList<BluetoothCandidate>>>? enumerate = null,
        Func<IPeerWorker>? workerFactory = null, TimeProvider? time = null, HeyMelodyWindows.Resources.ResourceCoordinator? resources = null,
        DeviceAppearanceStore? appearance = null, DeviceStateStore? deviceState = null)
    {
        _whitelist = whitelist ?? DeviceWhitelistStore.CreateDefault();
        _appearance = appearance ?? DeviceAppearanceStore.NextTo(_whitelist);
        _deviceState = deviceState ?? DeviceStateStore.NextTo(_whitelist);
        _enumerate = enumerate ?? PeerWorker.EnumerateAsync;
        _workerFactory = workerFactory ?? (() => new PeerWorker());
        _resources = resources ?? (workerFactory is null ? new(bundleDirectory: BundleDirectory(AppContext.BaseDirectory)) : null);
        _time = time ?? TimeProvider.System;
        DiagnosticLog.Info("host.lifecycle", "supervisorCreated", "Backend supervisor created", new { InstanceId });
        try { _allowed = _whitelist.Load(); }
        catch (InvalidDataException) { _catalogWarning = "设备选择记录已损坏，请重新选择并保存。"; }
    }

    // Completes with true once the tray's first catalog refresh has succeeded; null outside start-up.
    private Task<bool>? _startup;

    public async Task InitializeAsync(CancellationToken token)
    {
        var startup = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Volatile.Write(ref _startup, startup.Task);
        try
        {
            await InitializeResourcesAsync(token);
            await RefreshCatalogAsync(token);
            startup.TrySetResult(true);
        }
        catch (Exception) { startup.TrySetResult(false); throw; }
        finally { _ = Interlocked.CompareExchange(ref _startup, null, startup.Task); }
    }

    /// <summary>
    /// A client's paired-record lookup. While the tray's own first refresh is running it is joined instead of
    /// queueing a second catalog worker behind it: that refresh began with this tray process, after any window that
    /// launched it, and later pairing changes arrive through the device watcher's catalog events. A failed or absent
    /// start-up refresh falls back to a fresh one; a client's cancellation never cancels the shared refresh.
    /// </summary>
    public async Task RefreshCatalogForClientAsync(CancellationToken token)
    {
        if (Volatile.Read(ref _startup) is { } startup && await startup.WaitAsync(token)) return;
        await RefreshCatalogAsync(token);
    }
    public BackendSubscription Subscribe(Action<BackendSubscription>? atomicReply = null)
    {
        lock (_sync)
        {
            var result = new BackendSubscription { BackendInstanceId = InstanceId, Catalog = CatalogLocked(),
                Devices = DevicesLocked(), Snapshots = _peers.Values.Where(p => p.Present && p.Allowed).Select(p => Copy(p.Published)).ToArray(),
                DiagnosticFailures = DiagnosticFailuresLocked() };
            atomicReply?.Invoke(result); return result;
        }
    }
    public IReadOnlyList<UiDevice> Devices() { lock (_sync) return DevicesLocked(); }
    public UiDeviceCatalog Catalog() { lock (_sync) return CatalogLocked(); }
    private IReadOnlyList<UiDevice> DevicesLocked() => _peers.Values.Where(p => p.Present && p.Allowed)
        .OrderByDescending(p => p.Candidate.Connected).ThenBy(p => p.Candidate.Name, StringComparer.OrdinalIgnoreCase)
        .Select(p => new UiDevice(p.Identity, p.Candidate.Name, p.Candidate.Connected)
        { ProductId = p.AppearanceProductId, Color = p.AppearanceColor,
            IsPreferredSelection = _deviceState.PreferredKey == DeviceWhitelistStore.KeyFor(p.Candidate.Address) }).ToArray();
    private UiDeviceCatalog CatalogLocked() => new(_peers.Values.Where(p => p.Present)
        .OrderByDescending(p => p.Candidate.Connected).ThenBy(p => p.Candidate.Name, StringComparer.OrdinalIgnoreCase)
        .Select(p => new UiDeviceCandidate(p.Identity, p.Candidate.Name, p.Candidate.Connected, p.Allowed)).ToArray(), _catalogWarning);

    public async Task RefreshCatalogAsync(CancellationToken token)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime.Token);
        await _catalogGate.WaitAsync(linked.Token);
        try
        {
            var candidates = await _enumerate(linked.Token);
            List<Peer> start = [];
            lock (_sync)
            {
                if (_stopping) return;
                var present = candidates.Where(WindowsBluetooth.IsAudioCandidate)
                    .Where(p => !_osPresent.TryGetValue(p.Address, out var paired) || paired).ToDictionary(p => p.Address);
                foreach (var peer in _peers.Values)
                    if (!present.ContainsKey(peer.Candidate.Address) && peer.Present)
                    { peer.Present = false; StopPeerLocked(peer); PublishLocked(peer); }
                foreach (var candidate in present.Values)
                {
                    if (!_peers.TryGetValue(candidate.Address, out var peer)) { _peers[candidate.Address] = peer = new(candidate); LoadAppearanceLocked(peer); }
                    var wasConnected = peer.Candidate.Connected;
                    peer.Present = true;
                    peer.Candidate = _osConnections.TryGetValue(candidate.Address, out var connected)
                        ? candidate with { Connected = connected } : candidate;
                    peer.Allowed = _allowed.Contains(DeviceWhitelistStore.KeyFor(candidate.Address));
                    if (!peer.Allowed || !peer.Candidate.Connected || !_radioAvailable || _suspended)
                    { if (peer.Worker is not null || wasConnected != peer.Candidate.Connected) StopPeerLocked(peer); }
                    else if (peer.State != MonitoringState.Unsupported && !peer.Paused && peer.Worker is null) start.Add(peer);
                    PublishLocked(peer);
                }
                PublishCatalogLocked();
            }
            foreach (var peer in start) _ = EnsureWorkerAsync(peer);
        }
        catch (Exception) when (!linked.IsCancellationRequested)
        {
            lock (_sync) EmitLocked(new() { Kind = "health", Message = "无法读取蓝牙设备列表，保留原来的设备选择。" });
            throw;
        }
        finally { _catalogGate.Release(); }
    }

    public async Task<UiWhitelistSaveResult> SaveWhitelistAsync(IReadOnlyList<string> identities, CancellationToken token)
    {
        if (identities.Count is 0 or > 4096 || identities.Any(string.IsNullOrWhiteSpace))
            return new(false, "请至少选择一台有效的音频设备。");
        await RefreshCatalogAsync(token);
        List<Peer> start = [];
        lock (_sync)
        {
            token.ThrowIfCancellationRequested();
            var requested = identities.Distinct(StringComparer.Ordinal).ToArray();
            var chosen = requested.Select(id => _peers.Values.FirstOrDefault(p => p.Present && p.Identity == id)).ToArray();
            if (chosen.Any(p => p is null)) return new(false, "所选设备已解除配对，请刷新列表后重试。");
            var keys = chosen.Select(p => DeviceWhitelistStore.KeyFor(p!.Candidate.Address)).ToHashSet(StringComparer.Ordinal);
            try { _whitelist.Save(keys); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return new(false, "无法保存设备选择，请重试。"); }
            _allowed = keys; _catalogWarning = null;
            foreach (var peer in _peers.Values)
            {
                peer.Allowed = keys.Contains(DeviceWhitelistStore.KeyFor(peer.Candidate.Address));
                if (!peer.Allowed) { StopPeerLocked(peer); peer.Broadcast = null; CancelBroadcastExpiryLocked(peer); }
                else if (EligibleLocked(peer) && peer.Worker is null) start.Add(peer);
                PublishLocked(peer);
            }
            PublishCatalogLocked();
        }
        foreach (var peer in start) _ = EnsureWorkerAsync(peer);
        return new(true);
    }

    public UiSnapshot Select(string identity, bool rememberSelection = false)
    {
        lock (_sync)
        {
            var peer = FindLocked(identity);
            if (rememberSelection && _deviceState.RememberSelection(DeviceWhitelistStore.KeyFor(peer.Candidate.Address)))
                PublishCatalogLocked();
            return Copy(peer.Published);
        }
    }
    public UiSnapshot Pause(string identity)
    {
        lock (_sync)
        {
            var peer = FindLocked(identity); peer.Paused = true; StopPeerLocked(peer);
            peer.State = MonitoringState.Paused; PublishLocked(peer); return Copy(peer.Published);
        }
    }
    public UiSnapshot Resume(string identity)
    {
        Peer peer;
        lock (_sync)
        {
            peer = FindLocked(identity); peer.Paused = false; peer.Failures = 0;
            if (peer.State is MonitoringState.Unsupported or MonitoringState.Unavailable) peer.State = MonitoringState.WaitingForConnection;
            PublishLocked(peer);
        }
        _ = EnsureWorkerAsync(peer); return Select(identity);
    }
    private Peer FindLocked(string identity) => _peers.Values.FirstOrDefault(p => p.Identity == identity && p.Present && p.Allowed)
        ?? throw new InvalidOperationException("所选耳机已不在有效设备名单中。");
    private bool EligibleLocked(Peer p) => !_stopping && !_suspended && _radioAvailable && p.Present && p.Allowed
        && p.Candidate.Connected && !p.Paused;

    private async Task EnsureWorkerAsync(Peer peer)
    {
        IPeerWorker worker; long generation; CancellationToken token;
        lock (_sync)
        {
            if (!EligibleLocked(peer) || peer.Worker is not null || peer.Initializing || peer.State == MonitoringState.Unsupported) return;
            peer.Initializing = true; peer.Diagnostic = null; generation = peer.Generation; token = peer.Lifetime.Token;
            worker = peer.Worker = _workerFactory(); peer.State = MonitoringState.Connecting;
            if (worker is ICalibrationPeerWorker calibration)
            {
                calibration.CalibrationChanged += state => ReceiveCalibration(peer, generation, state);
                calibration.CalibrationSpectrumChanged += spectrum => ReceiveCalibrationSpectrum(peer, generation, spectrum);
            }
            worker.SnapshotChanged += s => ReceiveSpp(peer, generation, s);
            worker.Unavailable += () => WorkerFailed(peer, generation);
            PublishLocked(peer);
        }
        try
        {
            DiagnosticLog.Info("host.connection", "initialize", "Initializing peer worker",
                new { peer.Identity, generation, peer.Candidate });
            lock (_sync)
            {
                if (peer.Generation != generation || !ReferenceEquals(peer.Worker, worker)) return;
                ConfigureWorkerResources(worker, peer);
            }
            var snapshot = await worker.InitializeAsync(peer.Candidate.Address, peer.Identity, peer.Candidate.Name, token);
            BroadcastObservation? colorObservation;
            lock (_sync)
            {
                if (peer.Generation != generation || !ReferenceEquals(peer.Worker, worker)) return;
                peer.Initializing = false;
                if (!snapshot.ControlReady)
                {
                    if (!snapshot.SupportedModel && snapshot.ProductId is not null)
                    { StopPeerLocked(peer); peer.Spp = Copy(snapshot); peer.VerifiedProductId = snapshot.ProductId; peer.Broadcast = null;
                        RetireOtherCachedProductsLocked(peer, snapshot.ProductId);
                        RecordAppearanceLocked(peer, snapshot.ProductId, snapshot.Color, fromSpp: true);
                        CancelBroadcastExpiryLocked(peer);
                        peer.State = MonitoringState.Unsupported; PublishLocked(peer); }
                    else WorkerFailedLocked(peer, generation, snapshot);
                    return;
                }
                peer.Failures = 0; AdoptSppLocked(peer, snapshot);
                colorObservation = peer.Broadcast;
            }
            if (colorObservation?.Color is { } color)
                _ = UpdateWorkerColorAsync(peer, colorObservation.ProductId, color);
        }
        catch (Exception) { WorkerFailed(peer, generation); }
    }
    private void ReceiveSpp(Peer peer, long generation, UiSnapshot snapshot)
    {
        lock (_sync)
        {
            if (peer.Generation != generation || peer.Paused || !peer.Allowed || snapshot.Identity != peer.Identity) return;
            AdoptSppLocked(peer, snapshot);
            if (!snapshot.ControlReady && peer.Worker is not null && !peer.Initializing) WorkerFailedLocked(peer, generation);
        }
    }
    private void AdoptSppLocked(Peer peer, UiSnapshot snapshot)
    {
        if (_disposed || snapshot.Revision != 0 && snapshot.Revision <= peer.SppRevision) return;
        peer.SppRevision = snapshot.Revision; peer.Spp = Copy(snapshot); peer.Diagnostic = null;
        if (snapshot.ProductId is { Length: > 0 } product)
        {
            peer.VerifiedProductId = product;
            if (snapshot.SupportedModel && snapshot.Failure is null) RetireOtherCachedProductsLocked(peer, product);
            RecordAppearanceLocked(peer, product, snapshot.Color, fromSpp: true);
            if (peer.Broadcast is { } ble && !string.Equals(ble.ProductId, product, StringComparison.OrdinalIgnoreCase))
            { peer.Broadcast = null; CancelBroadcastExpiryLocked(peer); }
        }
        RememberPresentationLocked(peer);
        peer.State = snapshot.ControlReady ? MonitoringState.Monitoring : MonitoringState.Unavailable;
        PublishLocked(peer);
        if (snapshot.Revision != 0)
        {
            peer.PublicSppRevisions[snapshot.Revision] = peer.Revision;
            if (peer.PublicSppRevisions.Count > 128) peer.PublicSppRevisions.Remove(peer.PublicSppRevisions.Keys.Min());
        }
    }
    private void WorkerFailed(Peer peer, long generation) { lock (_sync) WorkerFailedLocked(peer, generation); }
    private void WorkerFailedLocked(Peer peer, long generation, UiSnapshot? failureSnapshot = null)
    {
        if (peer.Generation != generation || peer.Paused || _stopping) return;
        DiagnosticLog.Warning("host.connection", "workerFailed", "Peer worker became unavailable", data:
            new { peer.Identity, generation, failureSnapshot });
        // Keep only the safe diagnostic, never the retired transport's state or permissions.
        // Initialization responses can arrive before their snapshot events.
        var failed = failureSnapshot ?? peer.Spp;
        var message = failed is { } observed && observed.Identity == peer.Identity
            ? observed.Failure : null;
        StopPeerLocked(peer);
        if (!string.IsNullOrWhiteSpace(message)) peer.Diagnostic = new(peer.Generation, message);
        peer.State = MonitoringState.Unavailable; PublishLocked(peer);
        var failures = ++peer.Failures;
        if (failures > 3 || !EligibleLocked(peer)) return;
        var nextGeneration = peer.Generation;
        var token = peer.Lifetime.Token;
        _ = RetryAsync();
        async Task RetryAsync()
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(failures switch { 1 => 1, 2 => 2, _ => 5 }), _time, token);
                lock (_sync) { if (peer.Generation != nextGeneration) return; }
                await EnsureWorkerAsync(peer);
            }
            catch (OperationCanceledException) { }
        }
    }
    private void StopPeerLocked(Peer peer)
    {
        InvalidateCalibrationLocked(peer);
        peer.Generation++; peer.SppRevision = 0; peer.PublicSppRevisions.Clear(); peer.Spp = null; peer.Diagnostic = null; peer.Initializing = false;
        peer.Lifetime.Cancel(); peer.Lifetime.Dispose(); peer.Lifetime = new();
        var worker = peer.Worker; peer.Worker = null;
        if (worker is not null) _ = worker.DisposeAsync();
        peer.State = peer.Paused ? MonitoringState.Paused : !_radioAvailable || _suspended ? MonitoringState.Unavailable
            : MonitoringState.WaitingForConnection;
    }

    public async Task<UiSnapshot> RefreshAsync(string identity, RefreshScope scope, CancellationToken queuedCancellation)
    {
        Peer peer; lock (_sync) peer = FindLocked(identity);
        await peer.Commands.WaitAsync(queuedCancellation);
        try
        {
            queuedCancellation.ThrowIfCancellationRequested();
            IPeerWorker? worker; long generation; CancellationToken token;
            lock (_sync) { worker = EligibleLocked(peer) ? peer.Worker : null; generation = peer.Generation; token = peer.Lifetime.Token; }
            lock (_sync) if (HasCalibrationLocked(peer)) return Copy(peer.Published);
            if (worker is null) return Select(identity);
            var snapshot = await worker.RefreshAsync(scope, token);
            lock (_sync) { if (generation == peer.Generation) AdoptSppLocked(peer, snapshot); return Copy(peer.Published); }
        }
        finally { peer.Commands.Release(); }
    }

    public async Task<UiApplyResult> ApplyAsync(string identity, long generation, UiAction action, CancellationToken queuedCancellation)
    {
        Peer peer; lock (_sync) peer = FindLocked(identity);
        await peer.Commands.WaitAsync(queuedCancellation);
        try
        {
            queuedCancellation.ThrowIfCancellationRequested();
            IPeerWorker worker; CancellationToken token; long initialRevision;
            lock (_sync)
            {
                if (generation != peer.Generation || !EligibleLocked(peer) || !peer.Published.ControlReady || peer.Worker is null || HasCalibrationLocked(peer) || _resourceTransition)
                    return new() { BackendReceiptReceived = true, FailureKind = ApplyFailure.StateUnavailable,
                        ConnectionState = peer.Published.ConnectionState, After = Copy(peer.Published), Message = "耳机连接或状态已变化，未提交设置。" };
                worker = peer.Worker; token = peer.Lifetime.Token; initialRevision = peer.Revision;
            }
            var result = await worker.ApplyAsync(action, token);
            lock (_sync)
            {
                if (generation == peer.Generation && result.After is { } after) AdoptSppLocked(peer, after);
                // Preserve the receipt's actual readback. A newer notification is a separate
                // observation, not permission to rewrite the evidence of this submission.
                if (result.After is { } receipt)
                {
                    var privateRevision = receipt.Revision;
                    receipt.BackendInstanceId = InstanceId; receipt.DeviceGeneration = generation;
                    receipt.Revision = generation == peer.Generation && peer.PublicSppRevisions.TryGetValue(privateRevision, out var revision)
                        ? revision : initialRevision;
                    receipt.MonitoringState = receipt.ControlReady ? MonitoringState.Monitoring : MonitoringState.Unavailable;
                }
            }
            return result;
        }
        finally { peer.Commands.Release(); }
    }

    public void ObserveConnection(ulong address, bool connected)
    {
        Peer? start = null;
        lock (_sync)
        {
            _osConnections[address] = connected;
            if (!_peers.TryGetValue(address, out var peer) || peer.Candidate.Connected == connected) return;
            peer.Candidate = peer.Candidate with { Connected = connected }; peer.Failures = 0;
            if (!connected) StopPeerLocked(peer);
            else { if (peer.State == MonitoringState.Unsupported) peer.State = MonitoringState.WaitingForConnection; if (EligibleLocked(peer)) start = peer; }
            PublishLocked(peer); PublishCatalogLocked();
        }
        if (start is not null) _ = EnsureWorkerAsync(start);
    }

    public void ObserveBroadcast(ulong address, BroadcastObservation observation)
    {
        Peer? colorPeer = null;
        lock (_sync)
        {
            if (_stopping || _suspended || !_radioAvailable || !_peers.TryGetValue(address, out var peer)
                || !peer.Present || !peer.Allowed || !KnownProduct(observation.ProductId)
                || peer.VerifiedProductId is { } product && !string.Equals(product, observation.ProductId, StringComparison.OrdinalIgnoreCase)
                || peer.Broadcast is { } previous && previous.ReceivedAt > observation.ReceivedAt) return;
            var old = peer.Broadcast;
            if (observation.Color is not null && observation.Color != old?.Color) colorPeer = peer;
            // Missing components do not erase independently observed components. Their own
            // timestamps expire them, so a stream of status packets cannot keep old batteries fresh.
            peer.Broadcast = observation with
            {
                Lid = observation.Lid == CaseLidState.Unknown ? old?.Lid ?? CaseLidState.Unknown : observation.Lid,
                Left = observation.Left == EarbudPlacement.Unknown ? old?.Left ?? EarbudPlacement.Unknown : observation.Left,
                Right = observation.Right == EarbudPlacement.Unknown ? old?.Right ?? EarbudPlacement.Unknown : observation.Right,
                LeftBattery = observation.LeftBattery ?? old?.LeftBattery,
                RightBattery = observation.RightBattery ?? old?.RightBattery,
                CaseBattery = observation.CaseBattery ?? old?.CaseBattery,
                LeftCharging = observation.LeftBattery is null ? old?.LeftCharging : observation.LeftCharging,
                RightCharging = observation.RightBattery is null ? old?.RightCharging : observation.RightCharging,
                CaseCharging = observation.CaseBattery is null ? old?.CaseCharging : observation.CaseCharging,
                CaseTime = observation.Lid != CaseLidState.Unknown ? observation.ReceivedAt : old?.CaseTime,
                LeftTime = observation.Left != EarbudPlacement.Unknown ? observation.ReceivedAt : old?.LeftTime,
                RightTime = observation.Right != EarbudPlacement.Unknown ? observation.ReceivedAt : old?.RightTime,
                LeftBatteryTime = observation.LeftBattery is not null ? observation.ReceivedAt : old?.LeftBatteryTime,
                RightBatteryTime = observation.RightBattery is not null ? observation.ReceivedAt : old?.RightBatteryTime,
                CaseBatteryTime = observation.CaseBattery is not null ? observation.ReceivedAt : old?.CaseBatteryTime
                , Color = observation.Color ?? old?.Color,
                ColorTime = observation.Color is not null ? observation.ReceivedAt : old?.ColorTime
            };
            PublishLocked(peer, suppressEquivalentObservation: true);
            ScheduleBroadcastExpiryLocked(peer);
            RecordAppearanceLocked(peer, observation.ProductId, observation.Color, fromSpp: false);
        }
        if (colorPeer is not null && observation.Color is { } color)
            _ = UpdateWorkerColorAsync(colorPeer, observation.ProductId, color);
    }

    private void ScheduleBroadcastExpiryLocked(Peer peer)
    {
        if (peer.Broadcast is not { } observation) { CancelBroadcastExpiryLocked(peer); return; }
        var now = _time.GetUtcNow();
        var timestamps = new[] { observation.CaseTime, observation.LeftTime, observation.RightTime,
            observation.LeftBatteryTime, observation.RightBatteryTime, observation.CaseBatteryTime, observation.ColorTime };
        var next = timestamps.Where(t => t is { } value && value + TimeSpan.FromSeconds(30) > now)
            .Select(t => t!.Value + TimeSpan.FromSeconds(30)).DefaultIfEmpty(now).Min();
        if (next <= now) { peer.Broadcast = null; CancelBroadcastExpiryLocked(peer); return; }
        peer.BroadcastDue = next;
        // A packet stream re-arms this timer instead of allocating a source, a task and a timer per observation.
        (peer.BroadcastTimer ??= _time.CreateTimer(OnBroadcastExpiry, peer, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan))
            .Change(next - now, Timeout.InfiniteTimeSpan);
    }

    private void OnBroadcastExpiry(object? state)
    {
        var peer = (Peer)state!;
        lock (_sync)
        {
            if (_disposed || peer.BroadcastDue is not { } due) return;
            var now = _time.GetUtcNow();
            // A firing that predates a later re-arm (or the clock's rounding) arrives early: wait for the real deadline.
            if (now < due) { peer.BroadcastTimer?.Change(due - now, Timeout.InfiniteTimeSpan); return; }
            peer.BroadcastDue = null;
            PublishLocked(peer, suppressEquivalentObservation: true);
            ScheduleBroadcastExpiryLocked(peer);
        }
    }

    private static void CancelBroadcastExpiryLocked(Peer peer)
    {
        peer.BroadcastDue = null;
        peer.BroadcastTimer?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    public void ObservePairing(ulong address, bool paired)
    {
        lock (_sync)
        {
            _osPresent[address] = paired;
            if (paired || !_peers.TryGetValue(address, out var peer)) return;
            peer.Present = false; StopPeerLocked(peer); peer.Broadcast = null; CancelBroadcastExpiryLocked(peer);
            PublishCatalogLocked();
        }
    }

    public void ReportHealth(string message)
    {
        DiagnosticLog.Warning("host.health", "message", message);
        lock (_sync) EmitLocked(new() { Kind = "health", Message = message });
    }

    public void SetRadioAvailable(bool available) => SetEnvironment(available, null);
    public void SetSuspended(bool suspended)
    {
        SetEnvironment(null, suspended);
        // The resource check waits on one long timer that may not count the time asleep; let it look at the wall clock again.
        if (!suspended) _resources?.Reschedule();
    }
    private void SetEnvironment(bool? radio, bool? suspended)
    {
        List<Peer> start = [];
        lock (_sync)
        {
            if (radio is { } r) _radioAvailable = r;
            if (suspended is { } s) _suspended = s;
            foreach (var peer in _peers.Values)
            {
                if (!_radioAvailable || _suspended)
                { StopPeerLocked(peer); peer.Broadcast = null; CancelBroadcastExpiryLocked(peer); }
                else if (EligibleLocked(peer) && peer.Worker is null) { peer.Failures = 0; start.Add(peer); }
                PublishLocked(peer);
            }
            EmitLocked(new() { Kind = "health", Message = _suspended ? "电脑休眠，耳机状态监听已暂停。"
                : !_radioAvailable ? "蓝牙不可用，等待蓝牙恢复。" : "蓝牙状态监听已恢复。" });
        }
        foreach (var peer in start) _ = EnsureWorkerAsync(peer);
    }

    private void PublishLocked(Peer peer, bool suppressEquivalentObservation = false)
    {
        // Presence and whitelist membership only change right before a publish, so the scan demand is settled here (also on the early return below).
        UpdateScanDemandLocked();
        bool verifiedSpp = peer.Spp is { ControlReady: true, SupportedModel: true, Failure: null, PresentationCached: false };
        var snapshot = verifiedSpp ? Copy(peer.Spp!)
            : _deviceState.Find(DeviceWhitelistStore.KeyFor(peer.Candidate.Address))?.ToSnapshot()
                ?? (peer.Spp is { } spp ? Copy(spp) : new UiSnapshot());
        if (!verifiedSpp && peer.Spp is { } unverified)
        { snapshot.Failure = unverified.Failure; snapshot.SupportedModel = unverified.SupportedModel; }
        if (peer.State == MonitoringState.Unavailable && peer.Diagnostic is { } diagnostic
            && diagnostic.Generation == peer.Generation)
            snapshot.Failure = diagnostic.Message;
        snapshot.Identity = peer.Identity; snapshot.DeviceName = peer.Candidate.Name;
        snapshot.BackendInstanceId = InstanceId; snapshot.DeviceGeneration = peer.Generation;
        snapshot.ResourceRevision = _activeResourceRevision;
        snapshot.Connected = peer.Candidate.Connected && _radioAvailable && !_suspended;
        snapshot.ConnectionState = !_radioAvailable || _suspended ? ConnectionState.Unknown
            : peer.Candidate.Connected ? ConnectionState.Connected : ConnectionState.Disconnected;
        snapshot.ControlReady = EligibleLocked(peer) && verifiedSpp && !HasCalibrationLocked(peer) && !_resourceTransition;
        snapshot.MonitoringState = peer.Paused ? MonitoringState.Paused : peer.State;
        if (!snapshot.ControlReady)
        {
            snapshot.CanSetAncMode = snapshot.CanSetAnc = snapshot.CanSetEqualizer = snapshot.CanSetCustomEqualizer = snapshot.CanSetDualConnection = false;
            snapshot.CanSetGameMode = snapshot.CanSetSpatial = snapshot.CanSetHiRes = false;
            snapshot.CanFitTest = snapshot.CanSetupHearing = false;
            snapshot.FeatureSections = snapshot.FeatureSections.Select(s => s with { Controls = s.Controls.Select(c => c with { CanWrite = false }).ToArray() }).ToArray();
        }
        var ble = peer.Broadcast;
        var usedBle = false;
        var now = _time.GetUtcNow();
        bool Fresh(DateTimeOffset? time) => time is { } value && value + TimeSpan.FromSeconds(30) > now;
        bool Newer(DateTimeOffset? broadcast, DateTimeOffset? spp) => Fresh(broadcast) && (spp is null || broadcast > spp);
        if (ble is not null)
        {
            snapshot.ProductId ??= ble.ProductId;
            if (ble.Color is not null && Newer(ble.ColorTime, snapshot.ColorObservationTime))
            { snapshot.Color = ble.Color; snapshot.ColorObservationTime = ble.ColorTime; usedBle = true; }
            if (ble.LeftBattery is not null && Newer(ble.LeftBatteryTime, snapshot.LeftBatteryObservationTime))
            { snapshot.LeftBattery = ble.LeftBattery; snapshot.LeftCharging = ble.LeftCharging; snapshot.LeftBatteryObservationTime = ble.LeftBatteryTime; usedBle = true; }
            if (ble.RightBattery is not null && Newer(ble.RightBatteryTime, snapshot.RightBatteryObservationTime))
            { snapshot.RightBattery = ble.RightBattery; snapshot.RightCharging = ble.RightCharging; snapshot.RightBatteryObservationTime = ble.RightBatteryTime; usedBle = true; }
            if (ble.CaseBattery is not null && Newer(ble.CaseBatteryTime, snapshot.CaseBatteryObservationTime))
            { snapshot.CaseBattery = ble.CaseBattery; snapshot.CaseCharging = ble.CaseCharging; snapshot.CaseBatteryObservationTime = ble.CaseBatteryTime; usedBle = true; }
            if (ble.Lid != CaseLidState.Unknown && Newer(ble.CaseTime, snapshot.CaseObservationTime))
            { snapshot.CaseLid = ble.Lid; snapshot.CaseObservationTime = ble.CaseTime; usedBle = true; }
            if (ble.Left != EarbudPlacement.Unknown && Newer(ble.LeftTime, snapshot.LeftPlacementObservationTime))
            { snapshot.LeftPlacement = ble.Left; snapshot.LeftPlacementObservationTime = ble.LeftTime; usedBle = true; }
            if (ble.Right != EarbudPlacement.Unknown && Newer(ble.RightTime, snapshot.RightPlacementObservationTime))
            { snapshot.RightPlacement = ble.Right; snapshot.RightPlacementObservationTime = ble.RightTime; usedBle = true; }
        }
        snapshot.BatteryObservationTime = Latest(snapshot.LeftBatteryObservationTime, snapshot.RightBatteryObservationTime, snapshot.CaseBatteryObservationTime);
        snapshot.PlacementObservationTime = Latest(snapshot.CaseObservationTime, snapshot.LeftPlacementObservationTime, snapshot.RightPlacementObservationTime);
        snapshot.ObservationFresh = snapshot.ControlReady || usedBle;
        snapshot.ObservationSource = snapshot.ControlReady ? usedBle ? "spp+ble" : "spp" : usedBle ? "ble" : null;
        snapshot.ObservationTime = Latest(snapshot.BatteryObservationTime, snapshot.PlacementObservationTime,
            snapshot.ControlReady ? snapshot.SampleTime : null);
        var firstLidObservation = peer.LastObservedLid == CaseLidState.Unknown;
        var openEdge = false;
        if (snapshot.CaseLid != CaseLidState.Unknown && snapshot.CaseObservationTime is { } lidTime
            && (peer.LastLidObservationTime is null || lidTime > peer.LastLidObservationTime))
        {
            openEdge = snapshot.CaseLid == CaseLidState.Open && peer.LastObservedLid != CaseLidState.Open;
            // Unknown, expiration, pause and sleep are not evidence that the case closed.
            // Nor is an older SPP value revealed when a newer BLE observation expires.
            peer.LastObservedLid = snapshot.CaseLid; peer.LastLidObservationTime = lidTime;
        }
        if (suppressEquivalentObservation && !openEdge && SameObservation(peer.Published, snapshot)) return;
        snapshot.Revision = ++peer.Revision;
        peer.Published = snapshot;
        if (peer.Present && peer.Allowed)
        {
            EmitLocked(new() { Kind = "snapshot", Snapshot = Copy(snapshot) });
            if (openEdge) EmitLocked(new() { Kind = "caseOpen", Snapshot = Copy(snapshot), FirstObservation = firstLidObservation });
        }
    }
    private static DateTimeOffset? Latest(params DateTimeOffset?[] values) => values.Where(v => v.HasValue).DefaultIfEmpty().Max();
    private static bool SameObservation(UiSnapshot left, UiSnapshot right) => left.ProductId == right.ProductId
        && left.Color == right.Color && left.ResourceRevision == right.ResourceRevision
        && left.CaseLid == right.CaseLid && left.LeftPlacement == right.LeftPlacement && left.RightPlacement == right.RightPlacement
        && left.LeftBattery == right.LeftBattery && left.RightBattery == right.RightBattery && left.CaseBattery == right.CaseBattery
        && left.LeftCharging == right.LeftCharging && left.RightCharging == right.RightCharging && left.CaseCharging == right.CaseCharging
        && left.ObservationFresh == right.ObservationFresh && left.ObservationSource == right.ObservationSource;
    private void PublishCatalogLocked()
    {
        UpdateScanDemandLocked();
        EmitLocked(new() { Kind = "catalog", Catalog = CatalogLocked(), Devices = DevicesLocked() });
    }

    /// <summary>True while a present, allowed headset exists: BLE scanning serves it even when it is only paired, not connected.</summary>
    public bool HasScanDemand() { lock (_sync) return _scanDemand; }
    private void UpdateScanDemandLocked()
    {
        bool demand = _peers.Values.Any(p => p.Present && p.Allowed);
        if (demand == _scanDemand) return;
        _scanDemand = demand;
        // Never call out under _sync: the monitor's handler takes its own lock, which Advertisement holds while it waits for _sync.
        if (ScanDemandChanged is { } handler)
            ThreadPool.UnsafeQueueUserWorkItem(static h => { try { h(); } catch { } }, handler, preferLocal: false);
    }

    private void RetireOtherCachedProductsLocked(Peer peer, string? productId)
    {
        if (productId is { Length: 6 })
            _deviceState.RetireOtherProducts(DeviceWhitelistStore.KeyFor(peer.Candidate.Address), productId.ToUpperInvariant());
    }
    private void RememberPresentationLocked(Peer peer)
    {
        if (_disposed || !peer.Allowed || peer.Spp is not { ControlReady: true, SupportedModel: true, Failure: null,
                PresentationCached: false, ProductId.Length: 6 } snapshot) return;
        // Worker generation is checked before accepting readbacks, including configuration replacement.
        _deviceState.Remember(DeviceWhitelistStore.KeyFor(peer.Candidate.Address),
            DevicePresentation.From(snapshot, _time.GetUtcNow()));
    }

    private void LoadAppearanceLocked(Peer peer)
    {
        if (_appearance.Find(DeviceWhitelistStore.KeyFor(peer.Candidate.Address)) is not { } stored) return;
        peer.AppearanceProductId = stored.ProductId; peer.AppearanceColor = stored.Color; peer.AppearanceColorFromSpp = stored.ColorFromSpp;
    }

    /// <summary>
    /// Remembers the model and color for artwork. A control-session product ID replaces any earlier
    /// value and its color read outranks a broadcast; a verified broadcast fills only gaps. Nothing here
    /// affects control eligibility.
    /// </summary>
    private void RecordAppearanceLocked(Peer peer, string? productId, byte? color, bool fromSpp)
    {
        if (productId is not { Length: 6 }) return;
        string pid = productId.ToUpperInvariant();
        string? nextProduct = peer.AppearanceProductId; byte? nextColor = peer.AppearanceColor; bool nextFromSpp = peer.AppearanceColorFromSpp;
        if (nextProduct != pid)
        {
            if (!fromSpp && nextProduct is not null) return;
            nextProduct = pid; nextColor = color; nextFromSpp = fromSpp && color is not null;
        }
        else if (color is not null && (fromSpp || !nextFromSpp)) { nextColor = color; nextFromSpp = fromSpp; }
        if (nextProduct == peer.AppearanceProductId && nextColor == peer.AppearanceColor && nextFromSpp == peer.AppearanceColorFromSpp) return;
        peer.AppearanceProductId = nextProduct; peer.AppearanceColor = nextColor; peer.AppearanceColorFromSpp = nextFromSpp;
        _appearance.Save(DeviceWhitelistStore.KeyFor(peer.Candidate.Address), new(nextProduct, nextColor, nextFromSpp));
        if (peer.Present && peer.Allowed) PublishCatalogLocked();
    }
    private void EmitLocked(BackendEvent value) { value.BackendInstanceId = InstanceId; EventPublished?.Invoke(value); }
    private static UiSnapshot Copy(UiSnapshot s) => s.Clone();

    public async ValueTask DisposeAsync()
    {
        DiagnosticLog.Info("host.lifecycle", "dispose", "Disposing backend supervisor", new { InstanceId });
        List<IPeerWorker> workers;
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true; _stopping = true;
            workers = _peers.Values.Where(p => p.Worker is not null).Select(p => p.Worker!).ToList();
            foreach (var peer in _peers.Values)
            { peer.Lifetime.Cancel(); CancelBroadcastExpiryLocked(peer); peer.BroadcastTimer?.Dispose(); peer.BroadcastTimer = null; }
            _lifetime.Cancel();
        }
        try { foreach (var worker in workers) await worker.DisposeAsync(); }
        finally
        {
            try { await _deviceState.FlushAsync(); DisposeResources(); }
            finally { DetachDiagnostics(); }
        }
    }

    public async Task StopGracefullyAsync()
    {
        DiagnosticLog.Info("host.lifecycle", "stop", "Stopping connected devices and calibrations", new { InstanceId });
        Peer[] peers;
        lock (_sync) { _stopping = true; peers = _peers.Values.ToArray(); }
        await CancelAllCalibrationsAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(125));
        foreach (var peer in peers)
        {
            try { await peer.Commands.WaitAsync(deadline.Token); peer.Commands.Release(); }
            catch (OperationCanceledException) { break; }
        }
        await DisposeAsync();
    }
}
