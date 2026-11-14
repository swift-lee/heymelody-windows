using HeyMelody.Platform;
using HeyMelodyWindows.App;

namespace HeyMelodyWindows.Host;

internal sealed partial class BackendSupervisor : IFindHeadphonesService
{
    internal static readonly TimeSpan LocationTimeout = TimeSpan.FromSeconds(15);
    internal static readonly TimeSpan MaximumLocationAge = TimeSpan.FromSeconds(60);
    private sealed class LocationAttempt(long sequence, DateTimeOffset disconnectedAt, CancellationTokenSource cancellation)
    {
        internal long Sequence = sequence;
        internal DateTimeOffset DisconnectedAt = disconnectedAt;
        internal CancellationTokenSource Cancellation = cancellation;
    }
    private readonly ILocationProvider _locationProvider;
    private readonly FindHeadphonesStore _findHeadphonesStore;
    private readonly SemaphoreSlim _locationSettingsGate = new(1);
    private readonly Dictionary<string, DeviceLocationPreference> _locationDevices = new(StringComparer.Ordinal);
    private readonly Dictionary<string, LocationAttempt> _locationAttempts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (string Status, string Message)> _locationResults = new(StringComparer.Ordinal);
    private bool _findHeadphonesEnabled;
    private long _findHeadphonesRevision = 1, _locationSequence;
    private LocationPermission _locationPermission = new(LocationAccess.Unknown, "开启后将检查系统定位权限。");

    private void InitializeFindHeadphones()
    {
        try
        {
            var preferences = _findHeadphonesStore.Load();
            _findHeadphonesEnabled = preferences.Enabled;
            foreach (var entry in preferences.Devices) _locationDevices.Add(entry.Key, entry.Value);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            _locationPermission = new(LocationAccess.Failed, "无法读取位置记录，记录功能已关闭。");
        }
        _locationProvider.PermissionChanged += LocationPermissionChanged;
    }
    public async Task<UiFindHeadphonesState> GetFindHeadphonesAsync(CancellationToken token)
    {
        bool enabled;
        lock (_sync) { ThrowIfLocationStoppingLocked(); enabled = _findHeadphonesEnabled; }
        if (enabled) await CheckLocationPermissionAsync(token);
        lock (_sync) return FindHeadphonesLocked();
    }

    public async Task<UiLocationPermission> RequestLocationPermissionAsync(CancellationToken token)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime.Token);
        await _locationSettingsGate.WaitAsync(linked.Token);
        try
        {
            lock (_sync) ThrowIfLocationStoppingLocked();
            var permission = await _locationProvider.RequestPermissionAsync(linked.Token).WaitAsync(linked.Token);
            linked.Token.ThrowIfCancellationRequested();
            lock (_sync) { ThrowIfLocationStoppingLocked(); SetLocationPermissionLocked(permission); }
            return MapLocationPermission(permission);
        }
        finally { _locationSettingsGate.Release(); }
    }

    public async Task<UiFindHeadphonesState> SetFindHeadphonesEnabledAsync(bool enabled, CancellationToken token)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime.Token);
        linked.Token.ThrowIfCancellationRequested();
        lock (_sync)
        {
            ThrowIfLocationStoppingLocked();
            // Stop outstanding collection immediately, even if an earlier disk write is still draining.
            if (!enabled && _locationAttempts.Count > 0)
            { CancelAllLocationAttemptsLocked(); PublishFindHeadphonesLocked(); }
        }
        await _locationSettingsGate.WaitAsync(linked.Token);
        try
        {
            lock (_sync) ThrowIfLocationStoppingLocked();
            if (enabled)
            {
                var permission = await CheckLocationPermissionAsync(linked.Token, requestAccess: true);
                if (permission.Access != LocationAccess.Allowed)
                    throw new InvalidOperationException(permission.Message);
            }
            Dictionary<string, DeviceLocationPreference> next;
            lock (_sync)
            {
                linked.Token.ThrowIfCancellationRequested();
                ThrowIfLocationStoppingLocked();
                if (_findHeadphonesEnabled == enabled) return FindHeadphonesLocked();
                next = new(_locationDevices, StringComparer.Ordinal);
            }
            bool committed = await PersistLocationPreferencesAsync(enabled, next, linked.Token,
                () => !_disposed && !_stopping && (!enabled || _locationPermission.Access == LocationAccess.Allowed),
                () =>
                {
                    _findHeadphonesEnabled = enabled;
                    if (!enabled) CancelAllLocationAttemptsLocked();
                    PublishFindHeadphonesLocked();
                });
            lock (_sync)
            {
                linked.Token.ThrowIfCancellationRequested(); ThrowIfLocationStoppingLocked();
                if (!committed) throw new InvalidOperationException(_locationPermission.Message);
                return FindHeadphonesLocked();
            }
        }
        finally { _locationSettingsGate.Release(); }
    }

    public async Task<UiFindHeadphonesState> SetDeviceLocationRecordingAsync(string identity, bool enabled, CancellationToken token)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime.Token);
        linked.Token.ThrowIfCancellationRequested();
        lock (_sync)
        {
            ThrowIfLocationStoppingLocked();
            if (!_findHeadphonesEnabled) throw new InvalidOperationException("请先在设置中开启“记录耳机断开连接的位置”。");
            if (!enabled)
            {
                string key = LocationKey(FindLocked(identity));
                bool pending = _locationAttempts.ContainsKey(key);
                CancelLocationAttemptLocked(key);
                if (pending) PublishFindHeadphonesLocked();
            }
        }
        await _locationSettingsGate.WaitAsync(linked.Token);
        try
        {
            Dictionary<string, DeviceLocationPreference> next;
            string key;
            lock (_sync)
            {
                linked.Token.ThrowIfCancellationRequested(); ThrowIfLocationStoppingLocked();
                if (!_findHeadphonesEnabled) throw new InvalidOperationException("请先在设置中开启“记录耳机断开连接的位置”。");
                key = LocationKey(FindLocked(identity));
                var previous = _locationDevices.GetValueOrDefault(key) ?? new(false, null);
                if (previous.Enabled == enabled) return FindHeadphonesLocked();
                next = new(_locationDevices, StringComparer.Ordinal) { [key] = previous with { Enabled = enabled } };
            }
            bool committed = await PersistLocationPreferencesAsync(true, next, linked.Token,
                () => !_disposed && !_stopping && _findHeadphonesEnabled
                    && _peers.Values.Any(peer => peer.Identity == identity && peer.Present && peer.Allowed),
                () =>
                {
                    _locationDevices[key] = next[key];
                    if (!enabled) CancelLocationAttemptLocked(key);
                    PublishFindHeadphonesLocked();
                });
            lock (_sync)
            {
                linked.Token.ThrowIfCancellationRequested(); ThrowIfLocationStoppingLocked();
                if (!committed) throw new InvalidOperationException("所选耳机已不在有效设备名单中。");
                return FindHeadphonesLocked();
            }
        }
        finally { _locationSettingsGate.Release(); }
    }

    private async Task<LocationPermission> CheckLocationPermissionAsync(CancellationToken token, bool requestAccess = false)
    {
        // Only an explicit settings action may request / recheck OS authorization.
        var pending = requestAccess
            ? _locationProvider.RequestPermissionAsync(token)
            : _locationProvider.CheckPermissionAsync(token);
        var permission = await pending.WaitAsync(token);
        token.ThrowIfCancellationRequested();
        lock (_sync)
        {
            if (!_disposed && !_stopping) SetLocationPermissionLocked(permission);
        }
        return permission;
    }
    private void LocationPermissionChanged(LocationPermission permission)
    {
        lock (_sync) { if (!_disposed && !_stopping) SetLocationPermissionLocked(permission); }
    }
    private void SetLocationPermissionLocked(LocationPermission permission)
    {
        if (_locationPermission == permission) return;
        _locationPermission = permission;
        if (permission.Access is not (LocationAccess.Allowed or LocationAccess.Unknown))
        {
            foreach (string key in _locationAttempts.Keys)
                _locationResults[key] = ("failed", permission.Message);
            CancelAllLocationAttemptsLocked();
        }
        PublishFindHeadphonesLocked();
    }
    private static UiLocationPermission MapLocationPermission(LocationPermission permission) =>
        new(char.ToLowerInvariant(permission.Access.ToString()[0]) + permission.Access.ToString()[1..], permission.Message);
    private static string LocationKey(Peer peer) => DeviceWhitelistStore.KeyFor(peer.Candidate.Address);
    private UiFindHeadphonesState FindHeadphonesLocked() => new(_findHeadphonesEnabled, MapLocationPermission(_locationPermission),
        _peers.Values.Where(peer => peer.Present && peer.Allowed).Select(peer =>
        {
            string key = LocationKey(peer);
            var saved = _locationDevices.GetValueOrDefault(key);
            var result = _locationResults.GetValueOrDefault(key);
            return new UiFindHeadphonesDeviceState(peer.Identity, saved?.Enabled ?? false, saved?.Location,
                _locationAttempts.ContainsKey(key) ? "locating" : result.Status ?? (saved?.Location is null ? "idle" : "recorded"),
                _locationAttempts.ContainsKey(key) ? "正在记录断开位置…" : result.Message ?? "");
        }).ToArray(), _findHeadphonesRevision);
    private void PublishFindHeadphonesLocked()
    {
        _findHeadphonesRevision++;
        EmitLocked(new() { Kind = "findHeadphones", FindHeadphones = FindHeadphonesLocked() });
    }
    // The caller holds _locationSettingsGate. Serialization and Flush run outside _sync; only the atomic
    // replacement and in-memory commit share the final validity check. Retired writes only discard their temp file.
    private async Task<bool> PersistLocationPreferencesAsync(bool enabled,
        Dictionary<string, DeviceLocationPreference> devices, CancellationToken token,
        Func<bool> currentLocked, Action commitLocked)
    {
        IPreparedLocationWrite prepared;
        try { prepared = await _findHeadphonesStore.PrepareAsync(enabled, devices, token); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { throw new InvalidOperationException("无法保存查找耳机设置或位置，请重试。"); }
        using (prepared)
        {
            lock (_sync)
            {
                if (token.IsCancellationRequested || !currentLocked()) return false;
                try { prepared.Commit(); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                { throw new InvalidOperationException("无法保存查找耳机设置或位置，请重试。"); }
                commitLocked();
                return true;
            }
        }
    }
    private void ThrowIfLocationStoppingLocked()
    {
        if (_disposed || _stopping) throw new InvalidOperationException("后台正在退出，无法修改查找耳机设置。");
    }
    private void RecordDisconnectedLocationLocked(Peer peer)
    {
        string key = LocationKey(peer);
        if (_disposed || _stopping || !peer.Present || !peer.Allowed || !_findHeadphonesEnabled
            || _locationDevices.GetValueOrDefault(key)?.Enabled != true) return;
        CancelLocationAttemptLocked(key);
        var attempt = new LocationAttempt(++_locationSequence, _time.GetUtcNow(),
            CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token));
        _locationAttempts[key] = attempt;
        PublishFindHeadphonesLocked();
        _ = Task.Run(() => CaptureDisconnectedLocationAsync(key, attempt));
    }
    private async Task CaptureDisconnectedLocationAsync(string key, LocationAttempt attempt)
    {
        using var timeout = new CancellationTokenSource(LocationTimeout, _time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(attempt.Cancellation.Token, timeout.Token);
        string? failure = null;
        try
        {
            linked.Token.ThrowIfCancellationRequested();
            var permission = await _locationProvider.CheckPermissionAsync(linked.Token).WaitAsync(linked.Token);
            lock (_sync)
            {
                if (!IsCurrentLocationAttemptLocked(key, attempt)) return;
                SetLocationPermissionLocked(permission);
            }
            // A restarted Windows Host has no live position observer. Unknown is verified by this
            // one silent read at the observed disconnect; explicit enabling still requires Allowed.
            if (permission.Access is not (LocationAccess.Allowed or LocationAccess.Unknown))
            {
                failure = permission.Message;
                return;
            }
            var fix = await _locationProvider.GetLocationAsync(linked.Token).WaitAsync(linked.Token);
            var record = new UiRecordedLocation(fix.Latitude, fix.Longitude, fix.AccuracyMeters,
                attempt.DisconnectedAt, fix.Timestamp);
            var now = _time.GetUtcNow();
            if (!FindHeadphonesStore.ValidLocation(record) || fix.Timestamp > now + TimeSpan.FromSeconds(5)
                || fix.Timestamp < attempt.DisconnectedAt - MaximumLocationAge
                || fix.Timestamp > attempt.DisconnectedAt + LocationTimeout
                || now > attempt.DisconnectedAt + LocationTimeout)
            { failure = "系统未提供有效且新鲜的位置，已保留上次记录。"; return; }
            await _locationSettingsGate.WaitAsync(linked.Token);
            try
            {
                Dictionary<string, DeviceLocationPreference> next;
                lock (_sync)
                {
                    if (!IsCurrentLocationAttemptLocked(key, attempt)) return;
                    var previous = _locationDevices[key];
                    next = new(_locationDevices, StringComparer.Ordinal) { [key] = previous with { Location = record } };
                }
                bool committed = await PersistLocationPreferencesAsync(true, next, linked.Token,
                    () => IsCurrentLocationAttemptLocked(key, attempt)
                        && _time.GetUtcNow() <= attempt.DisconnectedAt + LocationTimeout,
                    () => { _locationDevices[key] = next[key]; _locationResults[key] = ("recorded", ""); });
                if (!committed && !attempt.Cancellation.IsCancellationRequested
                    && _time.GetUtcNow() > attempt.DisconnectedAt + LocationTimeout)
                    failure = "系统定位超时，已保留上次记录。";
            }
            finally { _locationSettingsGate.Release(); }
        }
        catch (OperationCanceledException)
        {
            if (timeout.IsCancellationRequested && !attempt.Cancellation.IsCancellationRequested)
                failure = "系统定位超时，已保留上次记录。";
        }
        catch (Exception)
        {
            // OS exceptions and location values must never be copied into diagnostic logs.
            failure = "无法记录本次断开位置，已保留上次记录。";
        }
        finally
        {
            lock (_sync)
            {
                if (_locationAttempts.TryGetValue(key, out var current) && ReferenceEquals(current, attempt))
                {
                    _locationAttempts.Remove(key);
                    if (failure is not null) _locationResults[key] = ("failed", failure);
                    if (!_disposed && !_stopping) PublishFindHeadphonesLocked();
                }
            }
            attempt.Cancellation.Dispose();
        }
    }
    private bool IsCurrentLocationAttemptLocked(string key, LocationAttempt attempt) =>
        !_disposed && !_stopping && _findHeadphonesEnabled && !attempt.Cancellation.IsCancellationRequested
        && _locationDevices.GetValueOrDefault(key)?.Enabled == true
        && _locationAttempts.TryGetValue(key, out var current) && ReferenceEquals(current, attempt);
    private void CancelLocationAttemptLocked(string key)
    {
        if (_locationAttempts.Remove(key, out var attempt)) attempt.Cancellation.Cancel();
    }
    private void CancelAllLocationAttemptsLocked()
    {
        foreach (var key in _locationAttempts.Keys.ToArray()) CancelLocationAttemptLocked(key);
    }
    private void DisposeFindHeadphonesLocked()
    {
        CancelAllLocationAttemptsLocked();
        _locationProvider.PermissionChanged -= LocationPermissionChanged;
    }
    private async Task DisposeFindHeadphonesAsync()
    {
        await _locationSettingsGate.WaitAsync();
        _locationSettingsGate.Release();
        _locationProvider.Dispose();
    }
}
