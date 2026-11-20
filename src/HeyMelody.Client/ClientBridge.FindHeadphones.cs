using System.Text.Json;
using HeyMelodyWindows.Host;

namespace HeyMelodyWindows.App;

public sealed partial class ClientBridge : IFindHeadphonesService, IFindHeadphonesToneService
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

    public async Task<UiFindHeadphonesToneResult> SetFindHeadphonesToneAsync(FindHeadphonesToneTarget target, bool play,
        bool confirmed, bool confirmedWorn, CancellationToken token)
    {
        Connection connection;
        lock (_stateGate)
        {
            if (_connection is not { Closed: false } original || original.BackendInstanceId != target.BackendInstanceId
                || _backendInstanceId != target.BackendInstanceId || _stopping || play && ExitPendingLocked)
                return new(false, null, play ? "后台连接已变化，未提交播放指令。" : "后台连接已变化，未确认耳机是否停播。");
            connection = original;
        }
        try
        {
            var result = await RequestAsync<UiFindHeadphonesToneResult>(new()
            {
                Operation = "findHeadphonesTone", BackendInstanceId = target.BackendInstanceId,
                Identity = target.Identity, DeviceGeneration = target.DeviceGeneration,
                Action = JsonSerializer.SerializeToElement(new FindHeadphonesToneArgs(target.Session, play, confirmed, confirmedWorn), RpcWire.Json)
            }, TimeSpan.FromSeconds(15), token, boundConnection: connection);
            if (result.After is { } snapshot) lock (_stateGate) Remember(snapshot);
            return result;
        }
        catch (BackendOperationException error) { return new(false, null, error.Message); }
        catch (BackendUnavailableException error)
        {
            return new(error.Submitted, null, error.Submitted ? "未确认耳机是否仍在响铃，请勿佩戴。" : error.Message);
        }
    }
}
