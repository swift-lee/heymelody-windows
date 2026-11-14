namespace HeyMelody.Platform;

public enum LocationAccess
{
    Unknown,
    Allowed,
    Denied,
    SystemDisabled,
    Restricted,
    Unavailable,
    Failed
}

public sealed record LocationPermission(LocationAccess Access, string Message);
public sealed record LocationFix(double Latitude, double Longitude, double AccuracyMeters, DateTimeOffset Timestamp);

/// <summary>OS location only; permission requests are explicitly initiated by the user through the Host UI thread.</summary>
public interface ILocationProvider : IDisposable
{
    event Action<LocationPermission>? PermissionChanged;
    Task<LocationPermission> RequestPermissionAsync(CancellationToken token);
    Task<LocationPermission> CheckPermissionAsync(CancellationToken token);
    Task<LocationFix> GetLocationAsync(CancellationToken token);
}
