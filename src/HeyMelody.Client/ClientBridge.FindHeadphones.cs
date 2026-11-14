using System.Text.Json;
using HeyMelodyWindows.Host;

namespace HeyMelodyWindows.App;

public sealed partial class ClientBridge : IFindHeadphonesService
{
    public Task<UiFindHeadphonesState> GetFindHeadphonesAsync(CancellationToken token) =>
        RequestAsync<UiFindHeadphonesState>(new() { Operation = "findHeadphones" }, TimeSpan.FromSeconds(15), token);

    public Task<UiLocationPermission> RequestLocationPermissionAsync(CancellationToken token) =>
        RequestAsync<UiLocationPermission>(new() { Operation = "locationPermission" }, TimeSpan.FromMinutes(2), token);

    public Task<UiFindHeadphonesState> SetFindHeadphonesEnabledAsync(bool enabled, CancellationToken token) =>
        RequestAsync<UiFindHeadphonesState>(new()
        {
            Operation = "setFindHeadphones",
            Action = JsonSerializer.SerializeToElement(new FindHeadphonesEnabledArgs(enabled), RpcWire.Json)
        }, TimeSpan.FromSeconds(15), token);

    public Task<UiFindHeadphonesState> SetDeviceLocationRecordingAsync(string identity, bool enabled, CancellationToken token) =>
        RequestAsync<UiFindHeadphonesState>(new()
        {
            Operation = "setDeviceLocationRecording", Identity = identity,
            Action = JsonSerializer.SerializeToElement(new FindHeadphonesEnabledArgs(enabled), RpcWire.Json)
        }, TimeSpan.FromSeconds(15), token);
}
