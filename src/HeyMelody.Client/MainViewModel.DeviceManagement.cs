namespace HeyMelodyWindows.App;

public sealed partial class MainViewModel
{
    private bool _managingDevices, _completingDeviceManagement;
    private IReadOnlyList<UiDevice>? _managementDevices;
    private string? _managementSelectedIdentity;

    public bool ManagingDevices => _managingDevices;
    public bool CanManageDevices => !Closing && !_lifecyclePending;

    /// <summary>Detaches the selected display and suspends UI writes while shared monitoring continues.</summary>
    public async Task<bool> PrepareDeviceManagementAsync()
    {
        if (!CanManageDevices) return false;
        _lifecyclePending = true;
        _operations.CancelQueuedWrites();
        NotifyState();
        var prepared = false;
        try
        {
            await _operations.RunAsync(async () =>
            {
                if (Closing) return;
                var devices = Devices;
                var identity = SelectedDevice?.Identity;
                if (!await ChangeSelectedDeviceAsync(null, "管理设备", EventService is not null
                    ? "管理设备会丢弃尚未保存的均衡器调整。继续吗？"
                    : "管理设备需要先断开客户端，并丢弃尚未保存的均衡器调整。继续吗？")) return;
                if (Closing) return;
                _managementDevices = devices;
                _managementSelectedIdentity = identity;
                _managingDevices = prepared = true;
                SetStatus(EventService is not null ? "正在管理设备，后台继续监听耳机状态。" : "正在管理设备，耳机控制已暂停。");
            }, OperationCoordinator.Lifecycle, _lifetime.Token);
        }
        catch (OperationCanceledException) { }
        catch
        {
            if (!Closing)
            {
                InvalidateSnapshot();
                SetStatus("无法准备设备管理，请重试。", true);
            }
        }
        finally
        {
            if (!prepared) ClearDeviceManagement();
            NotifyState();
        }
        return prepared && !Closing;
    }

    /// <summary>Restores cached selection or reloads the saved list, then attaches its cached backend state.</summary>
    public async Task<bool> CompleteDeviceManagementAsync(bool saved)
    {
        if (!_managingDevices || _completingDeviceManagement || Closing) return false;
        _completingDeviceManagement = true;
        var completed = false;
        try
        {
            await _operations.RunAsync(async () =>
            {
                if (Closing) return;
                var devices = ComposeDevices(saved ? await _service.EnumerateAsync(_lifetime.Token) : _managementDevices!);
                if (Closing) return;
                _devices = devices;
                _selectedDevice = ChooseDevice(devices, _managementSelectedIdentity);
                _title = _selectedDevice?.Name ?? "选择一副耳机";
                ResetSelection();
                completed = devices.Count != 0;
                if (completed && (EventService is not null || IsDemoSelected))
                {
                    await AttachSelectedBackendAsync();
                    return;
                }
                SetStatus(completed ? "选择耳机后连接" : "没有可用的白名单设备，请重新管理设备。", !completed);
            }, OperationCoordinator.Lifecycle, _lifetime.Token);
        }
        catch (OperationCanceledException) { }
        catch
        {
            if (!Closing)
            {
                _devices = ComposeDevices([]);
                _selectedDevice = ChooseDevice(_devices, _managementSelectedIdentity);
                _title = _selectedDevice?.Name ?? "选择一副耳机";
                ResetSelection();
                if (IsDemoSelected) { AttachDemo(); completed = true; }
                SetStatus("无法读取管理后的设备名单，请重新管理设备。", true);
            }
        }
        finally
        {
            ClearDeviceManagement();
            NotifyState();
        }
        return completed && !Closing;
    }

    // Shared with ordinary device switching so management uses the actual EQ discard confirmation.
    private async Task<bool> ChangeSelectedDeviceAsync(UiDevice? device, string title, string message)
    {
        if (device?.Identity == SelectedDevice?.Identity) return true;
        if (device is not null && !Devices.Any(candidate => candidate.Identity == device.Identity)) return false;
        var epoch = _sessionEpoch;
        if (EqDirty && !IsDemoSelected && !await ConfirmAsync(title, message, _sessionWork.Token)) return false;
        if (Closing || epoch != _sessionEpoch) return false;
        if (device is not null)
        {
            // The confirmation may outlive catalog refreshes. Adopt the current
            // object only if the user-selected identity remains in the list.
            var current = Devices.FirstOrDefault(candidate => candidate.Identity == device.Identity);
            if (current is null) return false;
            device = current;
        }
        AdvanceSession();
        await StopFindToneForDeviceSwitchAsync();
        if (Closing) return false;
        var hadSession = _sessionIdentity is not null;
        var previousWasDemo = IsDemoSelected;
        CaptureDemoDraft();
        _sessionIdentity = null;
        _selectedDevice = device;
        ResetSelection();
        _title = device?.Name ?? "选择一副耳机";
        if (IsDemoSelected) { AttachDemo(); return true; }
        if (EventService is not null)
        {
            await AttachSelectedBackendAsync();
            return true;
        }
        if (hadSession && !previousWasDemo) await _service.DisconnectAsync(_lifetime.Token);
        SetStatus("选择耳机后连接");
        return true;
    }

    private void ClearDeviceManagement()
    {
        _managementDevices = null;
        _managementSelectedIdentity = null;
        _managingDevices = _completingDeviceManagement = false;
        _lifecyclePending = false;
    }
}
