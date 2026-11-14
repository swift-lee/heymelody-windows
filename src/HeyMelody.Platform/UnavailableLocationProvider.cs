namespace HeyMelody.Platform;

public sealed class UnavailableLocationProvider : ILocationProvider
{
    private static readonly LocationPermission Permission =
        new(LocationAccess.Unavailable, "此平台尚不支持系统定位。");

    public event Action<LocationPermission>? PermissionChanged { add { } remove { } }

    public Task<LocationPermission> RequestPermissionAsync(CancellationToken token) => CheckPermissionAsync(token);

    public Task<LocationPermission> CheckPermissionAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        return Task.FromResult(Permission);
    }

    public Task<LocationFix> GetLocationAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        return Task.FromException<LocationFix>(new PlatformNotSupportedException(Permission.Message));
    }

    public void Dispose() { }
}
