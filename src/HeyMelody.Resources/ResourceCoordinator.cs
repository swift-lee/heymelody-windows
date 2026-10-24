using System.Diagnostics;
using HeyMelody.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Channels;

namespace HeyMelodyWindows.Resources;

public sealed record ResourceChangedEventArgs(long Revision, bool CatalogChanged, bool ArtworkChanged);
public sealed record ResourceRedownloadResult(bool CatalogChanged, int Updated, int Unchanged, int Failed, string Message);

/// <summary>Host-owned single writer for preferences, immutable catalogs and leased art packages.</summary>
public sealed class ResourceCoordinator : IDisposable
{
    private readonly string _storage;
    private readonly string _online;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly VendorResourceClient _vendor;
    private ResourceCatalog _catalog;
    private string? _fileVersion;
    private readonly Dictionary<string, DateTimeOffset> _checks = [];
    private readonly Dictionary<string, int> _leases = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _retired = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<(string ProductId, int Color, bool Model)> _artworkTargets = [];
    private readonly object _leaseGate = new();
    private readonly Dictionary<string, Task<bool>> _artworkRefreshes = new(StringComparer.Ordinal);
    // Wakes the auto-refresh loop whenever a due time or the online preference may have changed.
    private readonly Channel<byte> _wake = Channel.CreateBounded<byte>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
    private int _batchedChanges;
    private CancellationTokenSource _settingsCancellation = new();
    private readonly CancellationTokenSource _lifetime = new();
    private Task? _autoRefresh;
    private bool _initialized;
    public BundledResourceStore Bundled { get; }
    public ResourcePreferences Preferences { get; private set; } = new();
    public ResourceCatalog Catalog => _catalog;
    public long Revision { get; private set; } = 1;
    public string? LastError { get; private set; }
    public DateTimeOffset? LastSuccessfulCheck { get; private set; }
    /// <summary>How long a first artwork request waits for its download before falling back.</summary>
    public TimeSpan FirstDownloadWait { get; set; } = TimeSpan.FromSeconds(10);
    public event EventHandler<ResourceChangedEventArgs>? Changed;
    /// <summary>Clock for the check throttle and the auto-refresh schedule; tests substitute a manual one.</summary>
    internal TimeProvider Time { get; init; } = TimeProvider.System;

    public ResourceCoordinator(string? storageDirectory = null, HttpClient? httpClient = null, string? bundleDirectory = null)
    {
        _storage = Path.GetFullPath(storageDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HeyMelody", "resources"));
        _online = Path.Combine(_storage, "online");
        Bundled = new(bundleDirectory); _vendor = new(httpClient);
        _catalog = Bundled.GetCatalog();
    }
    public static ResourceCoordinator CreateDefault() => new();

    public async Task InitializeAsync(CancellationToken token = default)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (_initialized) return;
            Directory.CreateDirectory(_storage);
            string preferencesFile = Path.Combine(_storage, "preferences.json");
            if (File.Exists(preferencesFile))
            {
                try
                {
                    var saved = JsonSerializer.Deserialize<ResourcePreferences>(await File.ReadAllBytesAsync(preferencesFile, token).ConfigureAwait(false), ResourceJson.Options);
                    // Older preferences may contain Source=1 and a second key. Unknown fields
                    // are ignored; the HeyMelody key, offline choice and cache key survive.
                    if (saved is not null) Preferences = saved;
                }
                catch (Exception error) when (error is IOException or JsonException) { LastError = "无法读取资源设置，已采用默认设置。"; }
            }
            if (!ValidKey(Preferences.CacheEncryptionKey)) Preferences = Preferences with { CacheEncryptionKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)) };
            await SavePreferencesAsync(token).ConfigureAwait(false);
            LoadCachedCatalog(); SweepArtwork();
            _initialized = true;
        }
        finally { _gate.Release(); }
    }

    public async Task UpdatePreferencesAsync(ResourcePreferences preferences, CancellationToken token = default)
    {
        _ = new System.Text.UTF8Encoding(false, true).GetBytes(preferences.HeyMelodyKey);
        // Cancel requests using the previous settings before waiting for the writer.
        _settingsCancellation.Cancel();
        await InitializeAsync(token).ConfigureAwait(false);
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var old = Preferences;
            Preferences = preferences with { CacheEncryptionKey = old.CacheEncryptionKey };
            await SavePreferencesAsync(token).ConfigureAwait(false);
            _settingsCancellation.Dispose(); _settingsCancellation = new();
            _checks.Clear(); LastError = null; Revision++;
            lock (_leaseGate) _artworkTargets.Clear();
        }
        finally { _gate.Release(); }
        Wake();
        Changed?.Invoke(this, new(Revision, false, false));
    }

    public string ExportEffectiveCatalogJson() => Catalog.Raw.GetRawText();

    public async Task<bool> RefreshAsync(bool force = false, CancellationToken token = default)
        => await RefreshConfigurationAsync(force, false, token).ConfigureAwait(false) == RefreshOutcome.Updated;

    private async Task<RefreshOutcome> RefreshConfigurationAsync(bool force, bool redownload, CancellationToken token)
    {
        using var operation = DiagnosticLog.BeginOperation(DiagnosticLog.CurrentCorrelationId);
        DiagnosticLog.Debug("Resources", "configuration.refresh-started", "Checking online configuration resources.", new { force, redownload });
        await InitializeAsync(token).ConfigureAwait(false);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, _settingsCancellation.Token, _lifetime.Token);
        await _gate.WaitAsync(linked.Token).ConfigureAwait(false);
        var outcome = RefreshOutcome.Skipped;
        try
        {
            if (!Preferences.UseOnlineResources) return RefreshOutcome.Skipped;
            const string check = "config";
            if (!force && _checks.TryGetValue(check, out var next) && next > Time.GetUtcNow()) return RefreshOutcome.Skipped;
            try
            {
                outcome = RefreshOutcome.Unchanged;
                var metadata = await _vendor.GetConfigurationMetadataAsync(ActiveKey(), linked.Token).ConfigureAwait(false);
                bool cacheValid = CachedCatalogExists();
                if (redownload || _fileVersion != metadata.FileVersion || !cacheValid)
                {
                    byte[] bytes = await _vendor.DownloadAsync(metadata.DownloadUrl, ResourceLimits.ConfigBytes, linked.Token).ConfigureAwait(false);
                    if (metadata.Size is long expected && bytes.LongLength != expected) throw new InvalidDataException("Configuration length differs from metadata.");
                    var candidate = ResourceCatalog.Parse(bytes, true);
                    if (candidate.ContentVersion > _catalog.ContentVersion)
                    {
                        await SaveCatalogAsync(candidate, bytes, linked.Token).ConfigureAwait(false);
                        _catalog = candidate; Revision++; outcome = RefreshOutcome.Updated;
                        CleanupCatalogs(candidate.Sha256);
                    }
                    else if (_catalog.IsOnline && (!cacheValid || redownload && candidate.Sha256 == _catalog.Sha256))
                    {
                        // Repair or rewrite the persisted current snapshot without adopting an equal/older response.
                        var retained = _catalog;
                        await SaveCatalogAsync(retained, retained.OriginalBytes ?? throw new IOException("Cached configuration bytes are unavailable."), linked.Token).ConfigureAwait(false);
                        CleanupCatalogs(retained.Sha256);
                    }
                    _fileVersion = metadata.FileVersion;
                    await ResourceFiles.AtomicWriteAsync(Path.Combine(ResourceRoot(), "metadata.json"), JsonSerializer.SerializeToUtf8Bytes(metadata.FileVersion), linked.Token).ConfigureAwait(false);
                }
                var now = Time.GetUtcNow();
                _checks[check] = now.AddMinutes(10); LastSuccessfulCheck = now; LastError = null; Wake();
            }
            catch (OperationCanceledException) when (linked.IsCancellationRequested) { throw; }
            // An HttpClient timeout is an ordinary failure, not a caller cancellation.
            catch (Exception error) when (error is OperationCanceledException or HttpRequestException or IOException or InvalidDataException or JsonException
                or CryptographicException or ArgumentException or UnauthorizedAccessException)
            { DiagnosticLog.Error("Resources", "configuration.refresh-failed", "Configuration refresh failed; keeping the current resources.", error, new { force, redownload, Revision }); _checks[check] = Time.GetUtcNow().AddSeconds(30); LastError = "在线资源更新失败，继续使用当前有效资源。"; outcome = RefreshOutcome.Failed; Wake(); }
        }
        finally { _gate.Release(); }
        if (outcome == RefreshOutcome.Updated && Volatile.Read(ref _batchedChanges) == 0) Changed?.Invoke(this, new(Revision, true, false));
        DiagnosticLog.Info("Resources", "configuration.refresh-completed", "Configuration refresh completed.", new { outcome, Revision, LastError, LastSuccessfulCheck });
        return outcome;
    }

    public void StartAutoRefresh()
    {
        if (_autoRefresh is not null) return;
        _autoRefresh = Task.Run(async () =>
        {
            using var backgroundOperation = DiagnosticLog.SuspendOperation();
            while (!_lifetime.IsCancellationRequested)
            {
                var started = Time.GetUtcNow();
                try
                {
                    await RefreshAsync(token: _lifetime.Token).ConfigureAwait(false);
                    (string ProductId, int Color, bool Model)[] targets;
                    lock (_leaseGate) targets = _artworkTargets.ToArray();
                    foreach (var target in targets)
                        await RefreshArtworkAsync(target.ProductId, target.Color, target.Model, token: _lifetime.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { if (_lifetime.IsCancellationRequested) break; }
                // Anything else must not end the loop silently. The pass recorded no throttle, so the next one runs after the 30-second floor.
                catch (Exception error)
                {
                    DiagnosticLog.Error("Resources", "auto-refresh.failed", "An automatic resource refresh pass failed.", error, new { Revision });
                    Trace.WriteLine($"HeyMelody resources: auto-refresh pass failed: {error}");
                    LastError = "在线资源更新失败，继续使用当前有效资源。";
                }
                try { await WaitForDueAsync(started, _lifetime.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
        }, _lifetime.Token);
    }

    private static readonly TimeSpan MinimumPassInterval = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Sleeps until a check is due: never while online resources are off, and never sooner than
    /// <see cref="MinimumPassInterval"/> after the previous pass started (so a target the catalogue no
    /// longer lists, or a failing key, cannot spin the loop). A wake signal only re-evaluates the schedule.
    /// </summary>
    private async Task WaitForDueAsync(DateTimeOffset passStarted, CancellationToken token)
    {
        while (true)
        {
            // Drain before reading state: a change after this point leaves a signal for the next wait.
            while (_wake.Reader.TryRead(out _)) { }
            DateTimeOffset? due;
            await _gate.WaitAsync(token).ConfigureAwait(false);
            try { due = NextDue(); }
            finally { _gate.Release(); }
            TimeSpan? delay = null;
            if (due is { } at)
            {
                delay = (at > passStarted + MinimumPassInterval ? at : passStarted + MinimumPassInterval) - Time.GetUtcNow();
                if (delay <= TimeSpan.Zero) return;
            }
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(token);
            var signal = _wake.Reader.WaitToReadAsync(stop.Token).AsTask();
            try
            {
                if (delay is { } wait) await Task.WhenAny(signal, Task.Delay(wait, Time, stop.Token)).ConfigureAwait(false);
                else await signal.ConfigureAwait(false);
            }
            finally { stop.Cancel(); }
            token.ThrowIfCancellationRequested();
        }
    }

    /// <summary>Earliest throttle expiry (caller holds the gate); a missing key means due now, null means nothing to do.</summary>
    private DateTimeOffset? NextDue()
    {
        if (!Preferences.UseOnlineResources) return null;
        var due = _checks.GetValueOrDefault("config", DateTimeOffset.MinValue);
        (string ProductId, int Color, bool Model)[] targets;
        lock (_leaseGate) targets = _artworkTargets.ToArray();
        foreach (var (productId, color, model) in targets)
            // The pass skips products the catalogue lacks without recording a check; they must not keep the loop awake.
            if (Catalog.Find(productId) is not null) due = Min(due, _checks.GetValueOrDefault(ArtworkSlot(productId, color, model), DateTimeOffset.MinValue));
        return due;
        static DateTimeOffset Min(DateTimeOffset a, DateTimeOffset b) => a < b ? a : b;
    }

    private void Wake() => _wake.Writer.TryWrite(0);

    /// <summary>
    /// Re-evaluates the auto-refresh schedule against the wall clock now. The wait is one long timer, which may not count the time
    /// the machine slept, so the Host calls this on resume: a check that came due meanwhile then runs at once, still not sooner than
    /// 30 seconds after the previous pass started. Does nothing while online resources are off.
    /// </summary>
    public void Reschedule() => Wake();

    /// <summary>
    /// Returns installed art without waiting for the network. Online checks run in the background and
    /// announce replacements through <see cref="Changed"/>. Only a first download is awaited, briefly.
    /// </summary>
    public async Task<ResourceArtworkLease?> ResolveArtworkAsync(string productId, int? color, string role = "hero", CancellationToken token = default)
    {
        await InitializeAsync(token).ConfigureAwait(false);
        var profile = _catalog.Find(productId);
        if (profile is null) return null;
        // Never invent a protocol color. Uncolored same-model bundled art remains eligible.
        if (color.HasValue)
        {
            bool model = role is "model" or "hero";
            // Appearance module 14 can contain either GLB or animated WebP. X4 declares customDress
            // without support3DModel in the installed catalogs, but still supplies modelWebp.
            if (model && HasAppearanceModule(profile))
            {
                var found = await CachedOrDownloadedAsync(productId, color.Value, true, role,
                    () => Bundled.Acquire(Bundled.FindAsset(productId, color, "model"), Revision), token).ConfigureAwait(false);
                if (found is not null) return found;
            }
            else if (model)
            {
                // A retained package stays usable even if a later catalog omits its download flag.
                var found = AcquireCachedArtwork(productId, color.Value, true, role)
                    ?? Bundled.Acquire(Bundled.FindAsset(productId, color, "model"), Revision);
                if (found is not null) return found;
            }
            if (role != "model")
            {
                var found = await CachedOrDownloadedAsync(productId, color.Value, false, role, null, token).ConfigureAwait(false);
                if (found is not null) return found;
            }
        }
        return Bundled.Acquire(Bundled.FindAsset(productId, color, role), Revision)
            ?? Bundled.Acquire(Bundled.FindAsset(productId, color, "detail"), Revision)
            ?? Bundled.Acquire(Bundled.FindGenericAsset(profile.Type, role), Revision);
    }

    // Desktop query eligibility, not a claim that these are the complete OEM request conditions.
    private static bool HasAppearanceModule(ResourceDeviceProfile profile)
    {
        bool Flag(string name) => profile.Functions.ValueKind == JsonValueKind.Object
            && profile.Functions.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt32(out int enabled) && enabled > 0;
        return Flag("support3DModel") || Flag("customDress");
    }

    private async Task<ResourceArtworkLease?> CachedOrDownloadedAsync(string productId, int color, bool model, string role,
        Func<ResourceArtworkLease?>? installed, CancellationToken token)
    {
        var refresh = ScheduleArtworkRefresh(productId, color, model);
        var found = AcquireCachedArtwork(productId, color, model, role) ?? installed?.Invoke();
        if (found is not null || !Preferences.UseOnlineResources) return found;
        // First use of this slot: wait for the shared download, then fall back. A late package still
        // raises Changed, and clients resolve again.
        try { await refresh.WaitAsync(FirstDownloadWait, token).ConfigureAwait(false); }
        catch (TimeoutException error) { DiagnosticLog.Warning("Resources", "artwork.first-wait-timeout", "The first artwork wait expired; the shared download may continue.", error, new { productId, color, model, role, FirstDownloadWait }); return null; }
        return AcquireCachedArtwork(productId, color, model, role);
    }

    private Task<bool> ScheduleArtworkRefresh(string productId, int color, bool model)
    {
        if (!Preferences.UseOnlineResources) return Task.FromResult(false);
        string key = ArtworkSlot(productId, color, model);
        lock (_artworkRefreshes)
        {
            if (_artworkRefreshes.TryGetValue(key, out var running) && !running.IsCompleted) return running;
            var refresh = Task.Run(async () =>
            {
                try { return await RefreshArtworkAsync(productId, color, model, token: _lifetime.Token).ConfigureAwait(false); }
                catch (Exception error) { DiagnosticLog.Error("Resources", "artwork.background-refresh-failed", "The shared artwork refresh task failed.", error, new { productId, color, model, key }); return false; }
            });
            _artworkRefreshes[key] = refresh;
            return refresh;
        }
    }

    public async Task<bool> RefreshArtworkAsync(string productId, int color, bool model = false, bool force = false, CancellationToken token = default)
        => await RefreshArtworkCoreAsync(productId, color, model, force, false, token).ConfigureAwait(false) == RefreshOutcome.Updated;

    private async Task<RefreshOutcome> RefreshArtworkCoreAsync(string productId, int color, bool model, bool force, bool redownload, CancellationToken token)
    {
        await InitializeAsync(token).ConfigureAwait(false);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, _settingsCancellation.Token, _lifetime.Token);
        await _gate.WaitAsync(linked.Token).ConfigureAwait(false);
        var outcome = RefreshOutcome.Skipped;
        try
        {
            if (!Preferences.UseOnlineResources || Catalog.Find(productId) is null) return RefreshOutcome.Skipped;
            bool added; lock (_leaseGate) added = _artworkTargets.Add((productId, color, model));
            if (added) Wake();
            string key = ArtworkSlot(productId, color, model);
            if (!force && _checks.TryGetValue(key, out var next) && next > Time.GetUtcNow()) return RefreshOutcome.Skipped;
            try
            {
                outcome = RefreshOutcome.Unchanged;
                var metadata = await _vendor.GetArtworkMetadataAsync(ActiveKey(), productId, color, model, linked.Token).ConfigureAwait(false);
                if (metadata is not null)
                {
                    string slot = ArtworkRoot(productId, color, model);
                    var current = ReadArtworkPointer(slot);
                    if (redownload || current is null || PackageDigest(current) != metadata.Sha256 || !ValidArtworkPackage(Path.Combine(slot, current)))
                    {
                        string temporary = Path.Combine(slot, ".download-" + Guid.NewGuid().ToString("N"));
                        try
                        {
                            var entries = await DownloadAndExtractAsync(slot, temporary, metadata, productId, color, linked.Token).ConfigureAwait(false);
                            if (entries.Length == 0) throw new InvalidDataException("Resource package has no usable artwork.");
                            await ResourceFiles.AtomicWriteAsync(Path.Combine(temporary, "artwork.json"), JsonSerializer.SerializeToUtf8Bytes(entries, ResourceJson.Options), linked.Token).ConfigureAwait(false);
                            await ResourceFiles.AtomicWriteAsync(Path.Combine(temporary, ArtworkPackageExtractor.MarkerFile),
                                JsonSerializer.SerializeToUtf8Bytes(new { schemaVersion = ArtworkPackageExtractor.SchemaVersion, sha256 = metadata.Sha256 }), linked.Token).ConfigureAwait(false);
                            linked.Token.ThrowIfCancellationRequested();
                            string name = metadata.Sha256, package = Path.Combine(slot, name);
                            lock (_leaseGate)
                            {
                                if (Directory.Exists(package))
                                {
                                    // A renderer still reads this digest's previous extraction; publish beside it.
                                    if (_leases.ContainsKey(package)) { name += "-" + Guid.NewGuid().ToString("N")[..8]; package = Path.Combine(slot, name); }
                                    else ResourcePaths.DeleteOwnedDirectory(_online, package);
                                }
                                Directory.Move(temporary, package);
                                _retired.Remove(package);
                            }
                            await ResourceFiles.AtomicWriteAsync(Path.Combine(slot, "current.json"), JsonSerializer.SerializeToUtf8Bytes(name), linked.Token).ConfigureAwait(false);
                            foreach (string old in Directory.EnumerateDirectories(slot)) if (old != package) Retire(old);
                            Revision++; outcome = RefreshOutcome.Updated;
                        }
                        finally { if (Directory.Exists(temporary)) ResourcePaths.DeleteOwnedDirectory(_online, temporary); }
                    }
                }
                _checks[key] = Time.GetUtcNow().AddMinutes(10); LastError = null; Wake();
            }
            catch (OperationCanceledException) when (linked.IsCancellationRequested) { throw; }
            catch (Exception error) when (error is OperationCanceledException or HttpRequestException or IOException or InvalidDataException or JsonException
                or CryptographicException or ArgumentException or UnauthorizedAccessException)
            { DiagnosticLog.Error("Resources", "artwork.refresh-failed", "Artwork refresh failed; keeping the current resources.", error, new { key, Revision }); _checks[key] = Time.GetUtcNow().AddSeconds(30); LastError = "在线美术资源更新失败，继续使用当前有效资源。"; outcome = RefreshOutcome.Failed; Wake(); }
        }
        finally { _gate.Release(); }
        if (outcome == RefreshOutcome.Updated && Volatile.Read(ref _batchedChanges) == 0) Changed?.Invoke(this, new(Revision, false, true));
        DiagnosticLog.Info("Resources", "artwork.refresh-completed", "Artwork refresh completed.", new { outcome, Revision, LastError });
        return outcome;
    }

    /// <summary>
    /// Streams the archive to a disk-backed scratch file, verifies its digest and extracts exactly those bytes.
    /// The scratch lives in a ".download-" directory of its own (not the extraction directory) and is gone before
    /// this returns: publication retires every other directory in the slot. A slot created by this attempt
    /// is removed again when the download fails before extraction begins.
    /// </summary>
    private async Task<ResourceArtwork[]> DownloadAndExtractAsync(string slot, string temporary, ArtworkMetadata metadata, string productId, int color, CancellationToken token)
    {
        bool createdSlot = !Directory.Exists(slot), extracting = false;
        string scratch = Path.Combine(slot, ".download-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(scratch);
            using var archive = new FileStream(Path.Combine(scratch, "archive.zip"), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 4096, FileOptions.Asynchronous | FileOptions.DeleteOnClose);
            if (await _vendor.DownloadToAsync(metadata.DownloadUrl, archive, ResourceLimits.ArchiveBytes, token).ConfigureAwait(false) != metadata.Sha256) throw new InvalidDataException("Resource digest mismatch.");
            archive.Seek(0, SeekOrigin.Begin);
            extracting = true;
            Directory.CreateDirectory(temporary);
            return ArtworkPackageExtractor.Extract(archive, temporary, productId, color);
        }
        finally
        {
            // The archive handle is already closed here; a leftover is swept by the publication loop or the next start.
            try
            {
                if (Directory.Exists(scratch)) ResourcePaths.DeleteOwnedDirectory(_online, scratch);
                if (createdSlot && !extracting && Directory.Exists(slot)) ResourcePaths.DeleteOwnedDirectory(_online, slot);
            }
            catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException)
            { DiagnosticLog.Warning("Resources", "artwork.scratch-cleanup-failed", "Could not remove a resource extraction scratch directory.", error, new { scratch, slot, createdSlot, extracting }); }
        }
    }

    /// <summary>
    /// Re-fetches HeyMelody configuration and every known artwork slot. Replacements are
    /// validated before they are published; on failure the existing files stay in use.
    /// </summary>
    public async Task<ResourceRedownloadResult> RedownloadAsync(IEnumerable<(string ProductId, int Color)> devices, CancellationToken token = default)
    {
        await InitializeAsync(token).ConfigureAwait(false);
        if (!Preferences.UseOnlineResources) throw new InvalidOperationException("在线资源已关闭，开启后才能重新下载。");
        // Stop background checks of the outgoing state before taking the writer.
        _settingsCancellation.Cancel();
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            _settingsCancellation.Dispose(); _settingsCancellation = new();
            _checks.Clear(); LastError = null;
        }
        finally { _gate.Release(); }
        Wake();
        long before = Revision; int updated = 0, unchanged = 0, failed = 0;
        Interlocked.Increment(ref _batchedChanges);
        bool catalogChanged;
        try
        {
            var configuration = await RefreshConfigurationAsync(true, true, token).ConfigureAwait(false);
            catalogChanged = configuration == RefreshOutcome.Updated;
            if (configuration == RefreshOutcome.Failed) failed++;
            foreach (var target in RedownloadTargets(devices))
            {
                switch (await RefreshArtworkCoreAsync(target.ProductId, target.Color, target.Model, true, true, token).ConfigureAwait(false))
                {
                    case RefreshOutcome.Updated: updated++; break;
                    case RefreshOutcome.Failed: failed++; break;
                    case RefreshOutcome.Unchanged: unchanged++; break;
                }
            }
        }
        finally { Interlocked.Decrement(ref _batchedChanges); }
        LastError = failed == 0 ? null : "部分在线资源重新下载失败，已保留原有资源。";
        if (Revision != before) Changed?.Invoke(this, new(Revision, catalogChanged, true));
        string message = failed != 0 ? $"{failed} 项在线资源重新下载失败，已保留原有资源。"
            : updated == 0 ? "已重新下载在线资源，配置与美术资源均已核对。" : $"已重新下载在线资源，更新了 {updated} 项美术资源。";
        return new(catalogChanged, updated, unchanged, failed, message);
    }

    private (string ProductId, int Color, bool Model)[] RedownloadTargets(IEnumerable<(string ProductId, int Color)> devices)
    {
        var targets = new HashSet<(string ProductId, int Color, bool Model)>();
        lock (_leaseGate) targets.UnionWith(_artworkTargets);
        string root = Path.Combine(ResourceRoot(), "artwork");
        if (Directory.Exists(root))
            foreach (string slot in Directory.EnumerateDirectories(root))
                if (System.Text.RegularExpressions.Regex.Match(Path.GetFileName(slot), "^([0-9A-F]{6})_([0-9]{1,3})_(14|control)$") is { Success: true } match
                    && int.TryParse(match.Groups[2].Value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int color) && color <= 255)
                    targets.Add((match.Groups[1].Value, color, match.Groups[3].Value == "14"));
        foreach (var (productId, color) in devices)
        {
            if (_catalog.Find(productId) is not { } profile) continue;
            targets.Add((productId, color, false));
            if (HasAppearanceModule(profile))
                targets.Add((productId, color, true));
        }
        return targets.Where(target => _catalog.Find(target.ProductId) is not null)
            .OrderBy(target => target.ProductId, StringComparer.Ordinal).ThenBy(target => target.Color).ThenBy(target => target.Model).ToArray();
    }

    private ResourceArtworkLease? AcquireCachedArtwork(string productId, int color, bool model, string role)
    {
        string slot = ArtworkRoot(productId, color, model); var current = ReadArtworkPointer(slot);
        if (current is null) return null;
        string package = Path.Combine(slot, current);
        lock (_leaseGate)
        {
            try
            {
                var entries = JsonSerializer.Deserialize<ResourceArtwork[]>(File.ReadAllBytes(Path.Combine(package, "artwork.json")), ResourceJson.Options) ?? [];
                var candidates = entries.Where(e => e.ProductIds.Contains(productId, StringComparer.Ordinal) && e.Color == color
                    && (!model || role != "hero" || e.Role is "model" or "model-animation"));
                var entry = SelectPackageEntry(candidates, role);
                if (entry is null) return null;
                string path = ResourcePaths.Resolve(package, entry.Path);
                if (!File.Exists(path) || new FileInfo(path).Length != entry.Length || ResourceFiles.Sha256File(path, out _) != entry.Sha256) return null;
                _leases[package] = _leases.GetValueOrDefault(package) + 1;
                return new(path, entry.Kind, package, entry.Sha256, entry.Length, Revision, () => Release(package));
            }
            catch (Exception error) when (error is IOException or InvalidDataException or JsonException or ArgumentException) { return null; }
        }
    }

    /// <summary>
    /// Official product display fields: model3D, modelWebp, then detailImageRes.
    /// Lottie lists contain gesture tutorials and must never become the product hero.
    /// </summary>
    internal static ResourceArtwork? SelectPackageEntry(IEnumerable<ResourceArtwork> candidates, string role)
    {
        var entries = candidates.ToArray();
        if (role != "hero") return entries.Where(e => BundledResourceStore.MatchesRole(e.Role, role)).OrderBy(e => e.Order ?? 0).FirstOrDefault();
        return entries.FirstOrDefault(e => e.Role == "model")
            ?? entries.FirstOrDefault(e => e.Role == "model-animation")
            ?? entries.FirstOrDefault(e => e.Role == "detail");
    }

    private void Release(string package)
    {
        lock (_leaseGate)
        {
            if (_leases.TryGetValue(package, out int count) && count > 1) _leases[package] = count - 1;
            else { _leases.Remove(package); if (_retired.Contains(package)) TryDelete(package); }
        }
    }
    private void Retire(string package)
    { lock (_leaseGate) { _retired.Add(package); if (!_leases.ContainsKey(package)) TryDelete(package); } }
    private void TryDelete(string package)
    {
        try { ResourcePaths.DeleteOwnedDirectory(_online, package); _retired.Remove(package); }
        catch (Exception error) when (error is IOException or InvalidDataException) { /* Retain for the next cleanup attempt; never invalidate the new pointer. */ }
    }

    private void LoadCachedCatalog()
    {
        string root = Path.Combine(ResourceRoot(), "config"); ResourceCatalog? newest = null; string? newestDirectory = null;
        if (Directory.Exists(root)) foreach (string directory in Directory.EnumerateDirectories(root))
        {
            try
            {
                string name = Path.GetFileName(directory);
                if (!IsHash(name)) continue;
                byte[] plain = ResourceCacheCodec.Decrypt(File.ReadAllBytes(Path.Combine(directory, "config.cache")), Convert.FromBase64String(Preferences.CacheEncryptionKey));
                var candidate = ResourceCatalog.Parse(plain, true);
                if (candidate.Sha256 != name) continue;
                if (newest is null || candidate.ContentVersion > newest.ContentVersion) { newest = candidate; newestDirectory = directory; }
            }
            catch (Exception error) when (error is IOException or InvalidDataException or JsonException or CryptographicException or ArgumentException) { }
        }
        _catalog = ResourceCatalog.Select(Bundled.GetCatalog(), newest);
        if (Directory.Exists(root)) foreach (string directory in Directory.EnumerateDirectories(root))
            if (directory != newestDirectory && IsHash(Path.GetFileName(directory))) Retire(directory);
        string metadata = Path.Combine(ResourceRoot(), "metadata.json");
        try { if (File.Exists(metadata)) _fileVersion = JsonSerializer.Deserialize<string>(File.ReadAllBytes(metadata))!; }
        catch (Exception error) when (error is IOException or JsonException) { }
    }

    private bool CachedCatalogExists()
    {
        if (!_catalog.IsOnline) return false;
        try
        {
            byte[] bytes = ResourceCacheCodec.Decrypt(File.ReadAllBytes(Path.Combine(ResourceRoot(), "config", _catalog.Sha256, "config.cache")), Convert.FromBase64String(Preferences.CacheEncryptionKey));
            return Convert.ToHexStringLower(SHA256.HashData(bytes)) == _catalog.Sha256;
        }
        catch (Exception error) when (error is IOException or InvalidDataException or CryptographicException or ArgumentException) { return false; }
    }
    private async Task SaveCatalogAsync(ResourceCatalog catalog, byte[] bytes, CancellationToken token)
    {
        string root = Path.Combine(ResourceRoot(), "config"), directory = Path.Combine(root, catalog.Sha256);
        await ResourceFiles.AtomicWriteAsync(Path.Combine(directory, "config.cache"), ResourceCacheCodec.Encrypt(bytes, Convert.FromBase64String(Preferences.CacheEncryptionKey)), token).ConfigureAwait(false);
    }
    private void CleanupCatalogs(string currentHash)
    {
        string root = Path.Combine(ResourceRoot(), "config");
        foreach (string old in Directory.EnumerateDirectories(root)) if (Path.GetFileName(old) != currentHash) Retire(old);
    }
    private Task SavePreferencesAsync(CancellationToken token) => ResourceFiles.AtomicWriteAsync(Path.Combine(_storage, "preferences.json"), JsonSerializer.SerializeToUtf8Bytes(Preferences, ResourceJson.Options), token);
    private string ActiveKey() => Preferences.HeyMelodyKey is { Length: > 0 } key ? key : Bundled.GetDefaultKey();
    private string ResourceRoot() => Path.Combine(_online, "heymelody");
    private static string ArtworkSlot(string productId, int color, bool model) => productId + "_" + color.ToString(System.Globalization.CultureInfo.InvariantCulture) + "_" + (model ? "14" : "control");
    private string ArtworkRoot(string productId, int color, bool model) => Path.Combine(ResourceRoot(), "artwork", ArtworkSlot(productId, color, model));
    private static bool ValidKey(string value) { try { return Convert.FromBase64String(value).Length == 32; } catch (FormatException) { return false; } }
    private static bool IsHash(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);
    // "<sha256>" or "<sha256>-<8 hex>" when the digest was re-extracted while its first copy was leased.
    private static bool IsPackageName(string? value) => value is { Length: 64 } ? IsHash(value)
        : value is { Length: 73 } && value[64] == '-' && IsHash(value[..64]) && value[65..].All(Uri.IsHexDigit);
    private static string PackageDigest(string name) => name[..64];
    private static string? ReadArtworkPointer(string slot)
    {
        try { var value = JsonSerializer.Deserialize<string>(File.ReadAllBytes(Path.Combine(slot, "current.json"))); return IsPackageName(value) ? value : null; }
        catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException) { return null; }
    }
    private static bool ValidArtworkPackage(string package)
    {
        try
        {
            if (!File.Exists(Path.Combine(package, "config.json"))) return false;
            // Packages indexed by an older extractor are re-downloaded once and served until then.
            using (var marker = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(package, ArtworkPackageExtractor.MarkerFile))))
                if (!marker.RootElement.TryGetProperty("schemaVersion", out var schema) || !schema.TryGetInt32(out int version)
                    || version != ArtworkPackageExtractor.SchemaVersion) return false;
            var entries = JsonSerializer.Deserialize<ResourceArtwork[]>(File.ReadAllBytes(Path.Combine(package, "artwork.json")), ResourceJson.Options);
            if (entries is null || entries.Length == 0) return false;
            return entries.All(entry =>
            {
                string path = ResourcePaths.Resolve(package, entry.Path);
                return File.Exists(path) && new FileInfo(path).Length == entry.Length && ResourceFiles.Sha256File(path, out _) == entry.Sha256;
            });
        }
        catch (Exception error) when (error is IOException or InvalidDataException or JsonException or ArgumentException) { return false; }
    }
    /// <summary>Removes interrupted downloads and superseded packages left by an earlier Host process.</summary>
    private void SweepArtwork()
    {
        string root = Path.Combine(ResourceRoot(), "artwork");
        if (!Directory.Exists(root)) return;
        foreach (string slot in Directory.EnumerateDirectories(root))
        {
            try
            {
                var current = ReadArtworkPointer(slot);
                foreach (string directory in Directory.EnumerateDirectories(slot))
                {
                    string name = Path.GetFileName(directory);
                    if (name.StartsWith(".download-", StringComparison.Ordinal) || current is not null && name != current
                        && Directory.Exists(Path.Combine(slot, current)) && IsPackageName(name)) Retire(directory);
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }

    private enum RefreshOutcome { Skipped, Unchanged, Updated, Failed }

    public void Dispose() { _lifetime.Cancel(); Wake(); _settingsCancellation.Cancel(); _vendor.Dispose(); }
}
