using HeyMelody.Platform;
using Windows.Devices.Geolocation;
using Windows.Security.Authorization.AppCapabilityAccess;

namespace HeyMelodyWindows.Windows;

/// <summary>On-demand position reads. Idle permission checks never create a Geolocator or subscribe to location events.</summary>
public sealed class WindowsLocationProvider : ILocationProvider
{
    private readonly object _gate = new();
    private readonly object _publicationGate = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Func<TimeSpan, IWindowsLocationRequest> _requestFactory;
    private readonly Func<CancellationToken, Task<GeolocationAccessStatus>> _requestPermission;
    private readonly TimeSpan _timeout;
    private readonly HashSet<IWindowsLocationRequest> _activeRequests = [];
    private LocationPermission _permission = new(LocationAccess.Unknown, "将在耳机断开时核实系统定位权限；也可在应用设置中重新检查。");
    private long _permissionEpoch, _collectionSequence, _publicationVersion;
    private bool _disposed;

    public event Action<LocationPermission>? PermissionChanged;

    public WindowsLocationProvider() : this(timeout => new WindowsLocationRequest(timeout),
        WindowsLocationRequest.RequestPermissionAsync) { }

    internal WindowsLocationProvider(Func<TimeSpan, IWindowsLocationRequest> requestFactory,
        Func<CancellationToken, Task<GeolocationAccessStatus>> requestPermission, TimeSpan? timeout = null)
    {
        _requestFactory = requestFactory;
        _requestPermission = requestPermission;
        _timeout = timeout ?? TimeSpan.FromSeconds(15);
    }

    public async Task<LocationPermission> RequestPermissionAsync(CancellationToken token)
    {
        long epoch;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            token.ThrowIfCancellationRequested();
            epoch = ++_permissionEpoch;
        }
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime.Token);
        try
        {
            // Only this user-initiated operation may request consent. The Host dispatches it to its UI thread.
            var access = await _requestPermission(cancellation.Token).WaitAsync(cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            var permission = access switch
            {
                GeolocationAccessStatus.Allowed => Allowed(),
                GeolocationAccessStatus.Denied => Denied(),
                _ => new LocationPermission(LocationAccess.Unknown, "系统未能确定定位权限，请检查位置隐私设置后重试。")
            };
            return Publish(permission, epoch);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception error) { return Publish(FailedPermission(error), epoch); }
    }

    public Task<LocationPermission> CheckPermissionAsync(CancellationToken token)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            token.ThrowIfCancellationRequested();
            // A StatusChanged subscription activates the Windows location service even without requesting a fix.
            // Do not keep one merely to refresh permission. A cold process honestly reports Unknown until the
            // next explicit permission check or opted-in disconnect read; the OS remains the access authority.
            return Task.FromResult(_permission);
        }
    }

    public async Task<LocationFix> GetLocationAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime.Token);
        cancellation.CancelAfter(_timeout);
        IWindowsLocationRequest? request = null;
        long epoch = 0, sequence = 0;
        try
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (_permission.Access is LocationAccess.Denied or LocationAccess.SystemDisabled or LocationAccess.Restricted)
                    throw new UnauthorizedAccessException(_permission.Message);
                if (_permission.Access is LocationAccess.Failed or LocationAccess.Unavailable)
                    throw new InvalidOperationException(_permission.Message);
                cancellation.Token.ThrowIfCancellationRequested();
                epoch = _permissionEpoch;
                sequence = ++_collectionSequence;
                // The Host invokes this only for an opted-in, observed disconnect. Unknown is a cold-start
                // permission state, not a grant: the single OS request succeeds or denies it without RequestAccess.
                request = _requestFactory(_timeout);
                _activeRequests.Add(request);
            }
            var fix = await request.GetLocationAsync(cancellation.Token).WaitAsync(cancellation.Token).ConfigureAwait(false);
            cancellation.Token.ThrowIfCancellationRequested();
            if (!double.IsFinite(fix.Latitude) || !double.IsFinite(fix.Longitude)
                || fix.Latitude is < -90 or > 90 || fix.Longitude is < -180 or > 180
                || !double.IsFinite(fix.AccuracyMeters) || fix.AccuracyMeters < 0)
                throw new InvalidOperationException("系统返回的位置数据无效。");
            Publish(Allowed(), epoch, sequence);
            return fix;
        }
        catch (UnauthorizedAccessException)
        {
            Publish(Denied(), epoch, sequence);
            throw;
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested && !_lifetime.IsCancellationRequested)
        {
            throw new TimeoutException("系统定位超时，未取得本次断开的位置。");
        }
        catch (Exception error) when (error is not OperationCanceledException && error.HResult == unchecked((int)0x800705B4))
        {
            throw new TimeoutException("系统定位超时，未取得本次断开的位置。");
        }
        finally
        {
            if (request is not null)
            {
                lock (_gate) _activeRequests.Remove(request);
                request.Dispose();
            }
        }
    }

    private static LocationPermission Allowed() => new(LocationAccess.Allowed, "已允许 HeyMelody 后台使用系统定位。");
    private static LocationPermission Denied() => new(LocationAccess.Denied, "系统定位未获允许或已关闭，请检查位置隐私设置。");
    private static LocationPermission FailedPermission(Exception error) =>
        new(error is PlatformNotSupportedException ? LocationAccess.Unavailable : LocationAccess.Failed,
            $"无法检查系统定位权限（0x{error.HResult:X8}），请检查位置隐私设置后重试。");

    private LocationPermission Publish(LocationPermission permission, long epoch, long? sequence = null)
    {
        // Serialize publication separately from native ownership: subscribers may take the Host lock,
        // so never invoke them while holding _gate. Their order must still match the cache updates.
        lock (_publicationGate)
        {
            Action<LocationPermission>? listeners;
            long version;
            lock (_gate)
            {
                if (_disposed || epoch != _permissionEpoch || (sequence is { } value && value != _collectionSequence))
                    return _permission;
                if (sequence is null) ++_collectionSequence; // Explicit consent supersedes pending collection results.
                if (_permission == permission) return permission;
                _permission = permission;
                version = ++_publicationVersion;
                listeners = PermissionChanged;
            }
            if (listeners is not null)
                foreach (Action<LocationPermission> listener in listeners.GetInvocationList())
                {
                    lock (_gate)
                        if (_disposed || version != _publicationVersion) return _permission;
                    try { listener(permission); }
                    catch { /* A subscriber must not interrupt OS operation cleanup. */ }
                }
            return permission;
        }
    }

    public void Dispose()
    {
        IWindowsLocationRequest[] active;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            ++_permissionEpoch;
            active = [.. _activeRequests];
            _activeRequests.Clear();
        }
        _lifetime.Cancel();
        foreach (var request in active) request.Dispose();
    }

    // Metadata-only diagnostic. It deliberately never constructs a Geolocator or subscribes to StatusChanged.
    internal static WindowsLocationPermissionProbe InspectNativePermission()
    {
        AppCapability? capability = null;
        try
        {
            capability = AppCapability.Create("location");
            return new(capability.CheckAccess().ToString(), null, "NotRequested", null);
        }
        catch (Exception error) { return new(null, error.HResult, "NotRequested", null); }
        finally { WindowsLocationRequest.ReleaseNative(capability); }
    }

    internal static Task<WindowsLocationPermissionProbe> InspectNativePermissionAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        return Task.FromResult(InspectNativePermission());
    }
}

internal sealed record WindowsLocationPermissionProbe(string? CapabilityAccess, int? CapabilityHResult,
    string? LocationStatus, int? LocationHResult, string? ObservedLocationStatus = null, int? ObservedLocationHResult = null);

internal interface IWindowsLocationRequest : IDisposable
{
    Task<LocationFix> GetLocationAsync(CancellationToken token);
}
