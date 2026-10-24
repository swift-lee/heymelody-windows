using System.IO.Compression;
using System.Text;
using HeyMelodyWindows.App;
using HeyMelodyWindows.Resources;

using HeyMelody.Diagnostics;

namespace HeyMelodyWindows.Host;

internal sealed partial class BackendSupervisor
{
    private readonly ResourceCoordinator? _resources;
    private readonly SemaphoreSlim _resourceGate = new(1);
    private ResourceCatalog? _activeCatalog;
    private long _activeResourceRevision;
    private bool _catalogPending;
    private bool _resourceTransition;
    /// <summary>
    /// The release ships one ResourceBundles, beside HeyMelody.exe, and none in backend/: the windows read icons and guides from it
    /// directly and the Host hands them paths into it. A Host started from its own build output still uses the copy next to it.
    /// </summary>
    internal static string BundleDirectory(string baseDirectory)
    {
        string own = Path.Combine(baseDirectory, "ResourceBundles");
        string shared = Path.GetFullPath(Path.Combine(baseDirectory, "..", "ResourceBundles"));
        return !Directory.Exists(own) && File.Exists(Path.Combine(shared, "artwork.json")) ? shared : own;
    }
    private void SetResourceTransitionLocked(bool value)
    {
        _resourceTransition = value;
        foreach (var peer in _peers.Values) PublishLocked(peer);
    }
    private readonly Dictionary<string, (string Owner, ResourceArtworkLease Lease)> _artworkLeases = [];
    private async Task InitializeResourcesAsync(CancellationToken token)
    {
        if (_resources is null) return;
        await _resources.InitializeAsync(token);
        _activeCatalog = _resources.Catalog; _activeResourceRevision = _resources.Revision;
        _resources.Changed += ResourcesChanged;
        _resources.StartAutoRefresh();
        _ = RefreshResourcesSafelyAsync();
    }
    private async Task RefreshResourcesSafelyAsync()
    {
        try { await _resources!.RefreshAsync(token: _lifetime.Token); }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            DiagnosticLog.Error("host.resources", "refreshFailed", "Background resource refresh failed", error);
            ReportHealth("在线资源暂时不可用，继续使用已有资源。");
        }
    }
    private void ResourcesChanged(object? sender, ResourceChangedEventArgs args)
    {
        _ = AdoptResourcesSafelyAsync(args.CatalogChanged);
        if (args.ArtworkChanged) ScheduleCompaction();
    }
    // An idle tray almost never reaches a gen2 collection, so the large-object-heap garbage of an artwork install or
    // redownload would stay committed indefinitely. One compacting collection after the burst settles gives it back.
    private CancellationTokenSource? _compaction;
    private void ScheduleCompaction()
    {
        CancellationTokenSource next; CancellationTokenSource? previous; CancellationToken token;
        lock (_sync)
        {
            if (_stopping) return;
            previous = _compaction; next = _compaction = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token); token = next.Token;
        }
        if (previous is not null) { previous.Cancel(); previous.Dispose(); }
        _ = CompactAfterQuietAsync(next, token);
    }
    private async Task CompactAfterQuietAsync(CancellationTokenSource source, CancellationToken token)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(5), _time, token);
            lock (_sync)
            {
                if (_stopping || !ReferenceEquals(_compaction, source)) return;
                _compaction = null;
            }
            GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
            source.Dispose();
        }
        catch (OperationCanceledException) { }
    }
    private async Task AdoptResourcesSafelyAsync(bool catalogChanged)
    {
        try
        {
            await _resourceGate.WaitAsync(_lifetime.Token);
            try { await AdoptResourcesAsync(catalogChanged, _lifetime.Token); }
            finally { _resourceGate.Release(); }
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            DiagnosticLog.Error("host.resources", "adoptionFailed", "Downloaded resources could not be adopted", error);
            ReportHealth("资源已下载，将在连接恢复后重新应用。");
        }
    }
    private async Task AdoptResourcesAsync(bool catalogChanged, CancellationToken token)
    {
        if (_resources is null) return;
        Peer[] peers;
        bool restart, ownsTransition = false;
        lock (_sync)
        {
            if (_stopping) return;
            restart = _activeCatalog?.Sha256 != _resources.Catalog.Sha256;
            if (restart && _calibrations.Values.Any(IsActiveCalibration))
            { _catalogPending = true; EmitLocked(new() { Kind = "resources", Resources = ResourceSettings() }); return; }
            peers = _peers.Values.ToArray();
            // Capture the peers whose existing commands need draining at the same
            // instant that new submissions are blocked, including newly discovered peers.
            if (restart) { ownsTransition = !_resourceTransition; if (ownsTransition) SetResourceTransitionLocked(true); }
        }
        if (restart)
        {
            // Finish any submitted ordinary command before retiring its configuration.
            var acquired = new List<Peer>();
            try
            {
                foreach (var peer in peers) { await peer.Commands.WaitAsync(token); acquired.Add(peer); }
                lock (_sync)
                {
                    if (_calibrations.Values.Any(IsActiveCalibration))
                    { _catalogPending = true; EmitLocked(new() { Kind = "resources", Resources = ResourceSettings() }); return; }
                    _activeCatalog = _resources.Catalog; _activeResourceRevision = _resources.Revision; _catalogPending = false;
                    // Catalog refresh can add a peer while the command gates above are
                    // awaited. It could only start reads during the transition, so cancel
                    // those reads and retire its old-configuration worker along with all others.
                    peers = _peers.Values.ToArray();
                    foreach (var peer in peers) { StopPeerLocked(peer); peer.Broadcast = null; peer.Failures = 0; PublishLocked(peer); }
                }
            }
            finally
            {
                foreach (var peer in acquired) peer.Commands.Release();
                if (ownsTransition) lock (_sync) SetResourceTransitionLocked(false);
            }
            foreach (var peer in peers) _ = EnsureWorkerAsync(peer);
        }
        else lock (_sync)
        {
            _catalogPending = false;
            _activeResourceRevision = _resources.Revision;
            foreach (var peer in peers) PublishLocked(peer);
        }
        lock (_sync) EmitLocked(new() { Kind = "resources", Resources = ResourceSettings() });
    }
    public UiResourceSettings ResourceSettings()
    {
        if (_resources is null) return new();
        var p = _resources.Preferences; var c = _activeCatalog ?? _resources.Catalog;
        return new(p.UseOnlineResources, p.HeyMelodyKey, c.ContentVersion, c.IsOnline,
            _catalogPending ? "已下载更新，当前测试完成后应用。" : _resources.LastError ?? (c.IsOnline ? "使用已下载的在线资源" : "使用预装资源"));
    }
    public async Task<UiResourceSettings> SaveResourceSettingsAsync(UiResourceSettings settings, CancellationToken token)
    {
        if (_resources is null) throw new InvalidOperationException("资源服务不可用。");
        if (settings.HeyMelodyKey is not { Length: <= 4096 })
            throw new InvalidDataException();
        await _resourceGate.WaitAsync(token);
        try
        {
            var p = _resources.Preferences with { UseOnlineResources = settings.UseOnlineResources, HeyMelodyKey = settings.HeyMelodyKey };
            await _resources.UpdatePreferencesAsync(p, token);
            await AdoptResourcesAsync(true, token);
        }
        finally { _resourceGate.Release(); }
        if (settings.UseOnlineResources) _ = RefreshResourcesSafelyAsync();
        return ResourceSettings();
    }
    private bool KnownProduct(string pid) => _activeCatalog is null ? pid.Equals("06EC10", StringComparison.OrdinalIgnoreCase)
        : _activeCatalog.Find(pid) is not null;
    // The gzip+base64 form of the active catalog is identical for every worker; compressing it at SmallestSize for each
    // start would repeat the same large allocations. The key is the catalog instance, which changes with every adoption.
    private (ResourceCatalog Catalog, string Payload)? _workerCatalogPayload;
    private void ConfigureWorkerResources(IPeerWorker worker, Peer peer)
    {
        if (worker is not IResourcePeerWorker target || _activeCatalog is null) return;
        string payload;
        lock (_sync)
        {
            if (_workerCatalogPayload is { } cached && ReferenceEquals(cached.Catalog, _activeCatalog)) payload = cached.Payload;
            else
            {
                using var bytes = new MemoryStream();
                using (var gzip = new GZipStream(bytes, CompressionLevel.SmallestSize, true))
                    gzip.Write(Encoding.UTF8.GetBytes(_activeCatalog.Raw.GetRawText()));
                payload = Convert.ToBase64String(bytes.ToArray());
                _workerCatalogPayload = (_activeCatalog, payload);
            }
        }
        target.ConfigureResources(payload,
            _activeResourceRevision, VendorResourceClient.RequestCompatVersion(),
            DeviceWhitelistStore.KeyFor(peer.Candidate.Address), peer.Generation, peer.Broadcast?.ProductId, peer.Broadcast?.Color);
    }
    private async Task UpdateWorkerColorAsync(Peer peer, string productId, byte color)
    {
        try
        {
            await peer.Commands.WaitAsync(_lifetime.Token);
            try
            {
                IResourcePeerWorker worker; long generation; CancellationToken token;
                lock (_sync)
                {
                    if (!EligibleLocked(peer) || peer.Worker is not IResourcePeerWorker target || peer.Initializing
                        || peer.VerifiedProductId != productId || peer.Broadcast?.Color != color) return;
                    worker = target; generation = peer.Generation; token = peer.Lifetime.Token;
                }
                var snapshot = await worker.SetObservedColorAsync(productId, color, token);
                lock (_sync) if (generation == peer.Generation) AdoptSppLocked(peer, snapshot);
            }
            finally { peer.Commands.Release(); }
        }
        catch (OperationCanceledException) { }
        catch (Exception) { /* BLE artwork metadata must not interrupt an active device session. */ }
    }
    public async Task<UiArtwork?> AcquireArtworkAsync(string owner, string productId, int? color, string role, CancellationToken token)
    {
        if (_resources is null) return null;
        if (role is not ("hero" or "model" or "case" or "left" or "right" or "lottie" or "capsule" or "detail" or "both"))
            throw new InvalidDataException();
        // Remembered hints can name a model HeyMelody does not list: it simply has no art.
        if (_resources.Catalog.Find(productId) is null) return null;
        var lease = await _resources.ResolveArtworkAsync(productId, color, role, token);
        if (lease is null) return null;
        if (token.IsCancellationRequested || _resources.Catalog.Find(productId) is null)
        { lease.Dispose(); token.ThrowIfCancellationRequested(); return null; }
        string id = Guid.NewGuid().ToString("N");
        lock (_sync)
        {
            if (_stopping) { lease.Dispose(); return null; }
            _artworkLeases[id] = (owner, lease);
        }
        return new(id, lease.FullPath, lease.Kind, lease.PackageRoot, lease.Sha256, lease.Length, lease.Revision);
    }
    public bool ReleaseArtwork(string owner, string leaseId)
    {
        lock (_sync)
        {
            if (!_artworkLeases.TryGetValue(leaseId, out var item) || item.Owner != owner) return false;
            _artworkLeases.Remove(leaseId); item.Lease.Dispose(); return true;
        }
    }
    private Task<UiResourceRedownloadResult>? _redownload;
    /// <summary>One shared re-download at a time; it continues if the requesting client disconnects.</summary>
    public Task<UiResourceRedownloadResult> RedownloadResourcesAsync()
    {
        if (_resources is null) throw new InvalidOperationException("资源服务不可用。");
        lock (_sync) return _redownload is { IsCompleted: false } running ? running : _redownload = RunRedownloadAsync();
    }
    private async Task<UiResourceRedownloadResult> RunRedownloadAsync()
    {
        await _resourceGate.WaitAsync(_lifetime.Token);
        try
        {
            (string, int)[] devices;
            lock (_sync) devices = _peers.Values.Where(p => p.Allowed && p.AppearanceProductId is not null && p.AppearanceColor is not null)
                .Select(p => (p.AppearanceProductId!, (int)p.AppearanceColor!.Value)).Distinct().ToArray();
            var result = await _resources!.RedownloadAsync(devices, _lifetime.Token);
            await AdoptResourcesAsync(result.CatalogChanged, _lifetime.Token);
            return new(ResourceSettings(), result.Failed == 0, result.Updated, result.Failed, result.Message);
        }
        finally { _resourceGate.Release(); }
    }
    private void DisposeResources()
    {
        lock (_sync) { foreach (var item in _artworkLeases.Values) item.Lease.Dispose(); _artworkLeases.Clear(); _workerCatalogPayload = null; _compaction?.Dispose(); _compaction = null; }
        if (_resources is not null) { _resources.Changed -= ResourcesChanged; _resources.Dispose(); }
    }
}

internal interface IResourcePeerWorker
{
    void ConfigureResources(string catalogGzip, long revision, long appMarker, string stableKey, long generation,
        string? observedProductId, byte? observedColor);
    Task<UiSnapshot> SetObservedColorAsync(string productId, byte color, CancellationToken token);
}
