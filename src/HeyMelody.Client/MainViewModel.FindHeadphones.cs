using System.Globalization;

namespace HeyMelodyWindows.App;

public sealed partial class MainViewModel
{
    private UiFindHeadphonesState? _findHeadphonesState;
    private bool _findHeadphonesAvailable, _changingLocationRecording;
    private long _findHeadphonesEpoch;
    private string _findHeadphonesError = "";
    private string? _findHeadphonesErrorIdentity;
    private IFindHeadphonesService? FindHeadphonesService => _service as IFindHeadphonesService;

    public bool FindHeadphonesServiceAvailable => FindHeadphonesService is not null && _findHeadphonesAvailable;
    public bool FindHeadphonesEnabled => _findHeadphonesState?.Enabled == true;
    public string FindHeadphonesPermissionStatus => _findHeadphonesState?.Permission?.Status ?? "unknown";
    public bool DeviceLocationRecordingEnabled => SelectedLocationState?.Enabled == true;
    public bool CanSetDeviceLocationRecording => FindHeadphonesServiceAvailable && FindHeadphonesEnabled
        && SelectedDevice is not null && !IsDemoSelected && !_changingLocationRecording && !Closing;
    public string DeviceLocationRecordingDescription => IsDemoSelected ? "演示设备不记录位置。"
        : !FindHeadphonesEnabled ? "请先在应用设置中开启“记录耳机断开连接的位置”。"
        : "记录此电脑在耳机断开连接时的位置，作为查找参考。";
    public string FindHeadphonesMessage => _findHeadphonesError.Length != 0
        && (_findHeadphonesErrorIdentity is null || _findHeadphonesErrorIdentity == SelectedDevice?.Identity) ? _findHeadphonesError
        : !FindHeadphonesServiceAvailable ? "定位记录服务暂不可用。已有记录仍可查看。"
        : IsDemoSelected ? "演示设备没有真实位置记录。"
        : FindHeadphonesEnabled && FindHeadphonesPermissionStatus is not ("allowed" or "unknown")
            ? "位置记录已暂停：" + (_findHeadphonesState?.Permission?.Message is { Length: > 0 } permissionMessage
                ? permissionMessage : "请在系统设置中检查定位权限。")
        : SelectedLocationState is { Message.Length: > 0 } device ? device.Message
        : SelectedLocationState?.Status == "locating" ? "正在获取本次断开连接的位置…"
        : FindHeadphonesEnabled && FindHeadphonesPermissionStatus == "unknown"
            ? _findHeadphonesState?.Permission?.Message is { Length: > 0 } pendingMessage
                ? pendingMessage : "将在耳机断开时核实定位权限。"
        : RecordedLocation is null ? "暂无位置记录。启用后，将在耳机断开连接时尝试记录此电脑的位置。" : "";
    public bool HasFindHeadphonesMessage => FindHeadphonesMessage.Length != 0;
    public UiRecordedLocation? RecordedLocation => !IsDemoSelected && SelectedLocationState?.Location is { } location
        && double.IsFinite(location.Latitude) && double.IsFinite(location.Longitude)
        && location.Latitude is >= -90 and <= 90 && location.Longitude is >= -180 and <= 180 ? location : null;
    public bool HasRecordedLocation => RecordedLocation is not null;
    public string LocationLatitudeText => RecordedLocation is { } location ? Coordinate(location.Latitude) : "—";
    public string LocationLongitudeText => RecordedLocation is { } location ? Coordinate(location.Longitude) : "—";
    public string LocationRecordedAtText => RecordedLocation is { } location
        ? location.DisconnectedAt.ToLocalTime().ToString("yyyy/M/d HH:mm:ss", CultureInfo.InvariantCulture) : "—";
    public string LocationAccuracyText => RecordedLocation is { AccuracyMeters: >= 0 } location
        && double.IsFinite(location.AccuracyMeters)
        ? "约 " + location.AccuracyMeters.ToString("0.#", CultureInfo.InvariantCulture) + " 米" : "—";
    public string LocationGeoUri => RecordedLocation is { } location
        ? $"geo:{Coordinate(location.Latitude)},{Coordinate(location.Longitude)}" : "";

    private UiFindHeadphonesDeviceState? SelectedLocationState => SelectedDevice is { } selected
        ? _findHeadphonesState?.Devices?.FirstOrDefault(device => device.Identity == selected.Identity) : null;
    private static string Coordinate(double value) => value.ToString("0.########", CultureInfo.InvariantCulture);

    public async Task RefreshFindHeadphonesAsync(CancellationToken token = default)
    {
        if (FindHeadphonesService is not { } service || Closing) return;
        var epoch = _findHeadphonesEpoch;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime.Token);
        try
        {
            var state = await service.GetFindHeadphonesAsync(linked.Token);
            if (epoch != _findHeadphonesEpoch || Closing) return;
            PresentFindHeadphones(state);
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested) { }
        catch
        {
            if (epoch != _findHeadphonesEpoch || Closing) return;
            _findHeadphonesAvailable = false;
            _findHeadphonesErrorIdentity = null;
            _findHeadphonesError = "无法读取定位记录设置，请检查后台连接后重新打开设置。";
            NotifyState();
        }
    }

    public async Task<UiLocationPermission> RequestFindHeadphonesPermissionAsync(CancellationToken token)
    {
        if (FindHeadphonesService is not { } service || !FindHeadphonesServiceAvailable || Closing)
            return new("unavailable", "定位记录服务暂不可用，请检查后台连接。");
        var epoch = _findHeadphonesEpoch;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime.Token);
        var permission = await service.RequestLocationPermissionAsync(linked.Token);
        return epoch == _findHeadphonesEpoch && !Closing ? permission
            : new("unavailable", "后台连接已更改，请重新打开设置并检查定位权限。");
    }

    public async Task SaveFindHeadphonesEnabledAsync(bool enabled, CancellationToken token)
    {
        if (FindHeadphonesService is not { } service || !FindHeadphonesServiceAvailable || Closing)
            throw new InvalidOperationException("定位记录服务暂不可用，请检查后台连接。");
        var epoch = _findHeadphonesEpoch;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime.Token);
        var state = await service.SetFindHeadphonesEnabledAsync(enabled, linked.Token);
        if (epoch != _findHeadphonesEpoch || Closing)
            throw new InvalidOperationException("后台连接已更改，请重新打开设置并核对。");
        PresentFindHeadphones(state);
        if (state.Enabled != enabled)
            throw new InvalidOperationException(state.Permission?.Message is { Length: > 0 } message
                ? message : "定位权限检查未通过，设置未启用。");
    }

    public async Task SetDeviceLocationRecordingAsync(bool enabled)
    {
        if (!CanSetDeviceLocationRecording || FindHeadphonesService is not { } service || SelectedDevice is not { } selected) return;
        var epoch = _findHeadphonesEpoch;
        _changingLocationRecording = true;
        _findHeadphonesError = "";
        NotifyState();
        try
        {
            var state = await service.SetDeviceLocationRecordingAsync(selected.Identity, enabled, _lifetime.Token);
            if (epoch == _findHeadphonesEpoch && !Closing) PresentFindHeadphones(state);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch
        {
            if (epoch == _findHeadphonesEpoch && SelectedDevice?.Identity == selected.Identity && !Closing)
            {
                _findHeadphonesError = "未能保存此设备的位置记录开关，请重试。";
                _findHeadphonesErrorIdentity = selected.Identity;
            }
        }
        finally { _changingLocationRecording = false; NotifyState(); }
    }

    private void PresentFindHeadphones(UiFindHeadphonesState? state)
    {
        if (state is null || state.Revision < 0 || _findHeadphonesState is { } previous && state.Revision < previous.Revision) return;
        // A duplicate revision can restore transport availability, but cannot replace its accepted contents.
        if (_findHeadphonesState?.Revision != state.Revision) _findHeadphonesState = state;
        _findHeadphonesAvailable = true;
        _findHeadphonesError = "";
        _findHeadphonesErrorIdentity = null;
        NotifyState();
    }

    private void InvalidateFindHeadphones(bool clear)
    {
        _findHeadphonesEpoch++;
        _findHeadphonesAvailable = false;
        _findHeadphonesError = "";
        _findHeadphonesErrorIdentity = null;
        if (clear) _findHeadphonesState = null;
        NotifyState();
    }
}
