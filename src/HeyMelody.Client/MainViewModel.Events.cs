namespace HeyMelodyWindows.App;

public sealed partial class MainViewModel
{
    private readonly Dictionary<string, UiSnapshot> _backendSnapshots = new(StringComparer.Ordinal);
    private readonly HashSet<string> _retiredBackends = new(StringComparer.Ordinal);
    private string? _backendInstanceId;
    private bool _subscribed, _backendExitApproved, _backendStopping;
    private IHeadsetEventService? EventService => _service as IHeadsetEventService;

    public Task AttachBackendAsync() => SubscribeToBackendAsync();

    /// <summary>
    /// The effective device list the shared backend held when this view subscribed, or null when it was empty, came
    /// with a selection warning, or the backend has since become unavailable. The tray keeps the list current from
    /// device events, so start-up may present it while a confirming enumeration runs; it never authorises a SET.
    /// </summary>
    public IReadOnlyList<UiDevice>? BackendStartupDevices { get; private set; }

    /// <summary>The desktop shell dispatches backend messages onto its UI thread.</summary>
    public void AdoptBackendEvent(BackendEvent notification)
    {
        if (notification.Kind == "stopping")
        { _backendStopping = _backendExitApproved = _closeRequested = true; _reconnectPaused = null; }
        if (_closing || EventService is null) return;
        if (notification.Kind == "resources" && notification.Resources is { } resources && AcceptBackend(notification.BackendInstanceId))
        { PresentResourceSettings(resources); return; }
        if (notification.Kind == "unavailable")
        {
            _subscribed = false;
            BackendStartupDevices = null;
            _backendSnapshots.Clear();
            if (IsDemoSelected) return;
            AdvanceSession();
            InvalidateSnapshot();
            SetStatus(string.IsNullOrWhiteSpace(notification.Message)
                ? "蓝牙后台连接已中断，请刷新或重新打开主界面。" : Sanitize(notification.Message), true);
            return;
        }
        if (notification.Kind == "stopping")
        {
            if (IsDemoSelected) return;
            AdvanceSession();
            InvalidateSnapshot();
            SetStatus("后台正在退出…");
            return;
        }
        if (notification.Kind == "exitCancelled")
        {
            CancelBackendExit();
            return;
        }
        if (notification.Kind is not ("catalog" or "snapshot" or "caseOpen" or "health")) return;
        if (!AcceptBackend(notification.BackendInstanceId)) return;
        if (notification.Kind == "health" && !IsDemoSelected && !string.IsNullOrWhiteSpace(notification.Message))
            SetStatus(Sanitize(notification.Message), true);
        if (notification.Kind == "catalog" && notification.Devices is { } devices)
        {
            if (!_managingDevices)
            {
                var changed = PresentDevices(devices, _selectedDevice?.Identity);
                // Selection owns only the display target. A catalog update must
                // not send another Select RPC or restart the shared connection.
                if (changed || _snapshot is null) PresentSelectedBackendSnapshot();
            }
            else _devices = ComposeDevices(devices);
            NotifyState();
        }
        if (notification.Snapshot is { } snapshot && CacheSnapshot(snapshot) && !Closing
            && !_managingDevices && snapshot.Identity == _selectedDevice?.Identity)
        {
            _sessionIdentity = snapshot.Identity;
            AdoptRead(snapshot);
        }
    }

    private async Task SubscribeToBackendAsync()
    {
        if (EventService is not { } events || _subscribed) return;
        var subscription = await events.SubscribeAsync("main", _lifetime.Token);
        if (!AcceptBackend(subscription.BackendInstanceId)) return;
        foreach (var snapshot in subscription.Snapshots) CacheSnapshot(snapshot);
        BackendStartupDevices = subscription.Devices.Count != 0 && subscription.Catalog.SelectionWarning is null
            ? subscription.Devices : null;
        _subscribed = true;
    }

    private bool AcceptBackend(string instance)
    {
        if (string.IsNullOrEmpty(instance) || _retiredBackends.Contains(instance)) return false;
        if (_backendInstanceId == instance) return true;
        if (_backendInstanceId is { } previous)
        {
            _retiredBackends.Add(previous);
            _backendSnapshots.Clear(); _artworkRevision = 0;
            if (!IsDemoSelected) { AdvanceSession(); InvalidateSnapshot(); }
        }
        _backendInstanceId = instance;
        return true;
    }

    private bool CacheSnapshot(UiSnapshot snapshot)
    {
        if (DemoDeviceData.IsDemoIdentity(snapshot.Identity)) return _demoDevice is not null && ReferenceEquals(snapshot, _demoSnapshot);
        if (EventService is null) { ObserveArtworkRevision(snapshot.ResourceRevision); return true; }
        if (_backendInstanceId is null && !AcceptBackend(snapshot.BackendInstanceId)) return false;
        if (snapshot.BackendInstanceId != _backendInstanceId) return false;
        if (_backendSnapshots.TryGetValue(snapshot.Identity, out var previous))
        {
            if (snapshot.DeviceGeneration < previous.DeviceGeneration
                || snapshot.DeviceGeneration == previous.DeviceGeneration && snapshot.Revision < previous.Revision)
                return false;
            if (snapshot.DeviceGeneration == previous.DeviceGeneration && snapshot.Revision == previous.Revision)
                return ReferenceEquals(snapshot, previous);
            if (snapshot.Identity == _sessionIdentity && (snapshot.DeviceGeneration != previous.DeviceGeneration
                || IsVerifiedControl(previous) && !IsVerifiedControl(snapshot)))
                AdvanceSession();
        }
        _backendSnapshots[snapshot.Identity] = snapshot;
        ObserveArtworkRevision(snapshot.ResourceRevision);
        return true;
    }

    private async Task AttachSelectedBackendAsync()
    {
        if (IsDemoSelected) { AttachDemo(); return; }
        if (EventService is not { } events || _selectedDevice is not { } selected) return;
        _sessionIdentity = selected.Identity;
        var snapshot = await events.SelectAsync(selected.Identity, _lifetime.Token);
        if (snapshot.Identity != selected.Identity) throw new InvalidDataException();
        if (_backendSnapshots.TryGetValue(selected.Identity, out var current)
            && current.BackendInstanceId == snapshot.BackendInstanceId
            && (current.DeviceGeneration > snapshot.DeviceGeneration
                || current.DeviceGeneration == snapshot.DeviceGeneration && current.Revision >= snapshot.Revision))
            snapshot = current;
        AdoptRead(snapshot);
    }

    private static bool IsVerifiedControl(UiSnapshot? snapshot) => snapshot is
        { PresentationCached: false,
          Connected: true, SupportedModel: true, Failure: null }
        && (string.IsNullOrEmpty(snapshot.BackendInstanceId) || snapshot.ControlReady);

    private bool CurrentBackendTarget(PendingAction intent) => EventService is null
        || _liveSnapshot is { } snapshot && IsVerifiedControl(snapshot)
            && snapshot.BackendInstanceId == intent.BackendInstanceId
            && snapshot.DeviceGeneration == intent.DeviceGeneration;

    private Task<UiSnapshot> ReadSelectedAsync(RefreshScope scope, CancellationToken token) =>
        IsDemoSelected && _demoSnapshot is { } demo ? Task.FromResult(demo) : EventService is { } events && _sessionIdentity is { } identity
            ? events.RefreshAsync(identity, scope, token) : _service.RefreshAsync(scope, token);

    public async Task<bool> ConfirmBackendExitAsync()
    {
        if (_closing || _backendExitApproved) return true;
        if (_closeDialogRunning) return false;
        SuspendActionsForClose();
        _closeDialogRunning = true;
        try
        {
            _backendExitApproved = !EqDirty || IsDemoSelected || _preview
                || await ConfirmAsync("关闭客户端", "均衡器还有未保存的调整，确定关闭吗？");
            return _backendExitApproved;
        }
        finally
        {
            _closeDialogRunning = false;
            if (!_backendExitApproved) CancelBackendExit();
            NotifyState();
        }
    }

    public void CancelBackendExit()
    {
        if (_closing || _backendStopping) return;
        _backendExitApproved = _closeRequested = false;
        if (_selectedDevice is { } selected && _backendSnapshots.TryGetValue(selected.Identity, out var snapshot))
            AdoptRead(snapshot);
        NotifyState();
    }
}
