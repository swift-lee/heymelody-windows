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

public enum FindHeadphonesTonePhase { Idle, Starting, Ringing, Stopping, Unconfirmed }

public sealed record UiFindHeadphonesToneState(long Session, FindHeadphonesTonePhase Phase, bool CanStart, bool Worn)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsActive => Phase is FindHeadphonesTonePhase.Starting or FindHeadphonesTonePhase.Ringing or FindHeadphonesTonePhase.Stopping;
}

public sealed record FindHeadphonesToneArgs(long Session, bool? Play, bool Confirmed = false, bool ConfirmedWorn = false);

public sealed record FindHeadphonesToneTarget(string BackendInstanceId, string Identity, long DeviceGeneration, long Session);

public sealed record UiFindHeadphonesToneResult(bool Submitted, byte? AckStatus, string Message, UiSnapshot? After = null);

public interface IFindHeadphonesToneService
{
    Task<UiFindHeadphonesToneResult> SetFindHeadphonesToneAsync(FindHeadphonesToneTarget target, bool play,
        bool confirmed, bool confirmedWorn, CancellationToken token);
}
