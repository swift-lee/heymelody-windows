namespace HeyMelodyWindows.App;

public sealed record UiLocationPermission(string Status, string Message);

public sealed record UiRecordedLocation(double Latitude, double Longitude, double AccuracyMeters,
    DateTimeOffset DisconnectedAt, DateTimeOffset PositionTimestamp);

public sealed record UiFindHeadphonesDeviceState(string Identity, bool Enabled, UiRecordedLocation? Location,
    string Status, string Message);

public sealed record UiFindHeadphonesState(bool Enabled, UiLocationPermission Permission,
    IReadOnlyList<UiFindHeadphonesDeviceState> Devices, long Revision);

public sealed record FindHeadphonesEnabledArgs(bool Enabled);

/// <summary>Computer location recording is independent of headset protocol/control availability.</summary>
public interface IFindHeadphonesService
{
    Task<UiFindHeadphonesState> GetFindHeadphonesAsync(CancellationToken token);
    Task<UiLocationPermission> RequestLocationPermissionAsync(CancellationToken token);
    Task<UiFindHeadphonesState> SetFindHeadphonesEnabledAsync(bool enabled, CancellationToken token);
    Task<UiFindHeadphonesState> SetDeviceLocationRecordingAsync(string identity, bool enabled, CancellationToken token);
}
