using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.InteropServices;
using HeyMelodyWindows.App;
using HeyMelodyWindows.Protocol;
using global::Windows.Devices.Bluetooth;
using global::Windows.Devices.Bluetooth.Advertisement;
using global::Windows.Devices.Enumeration;
using global::Windows.Devices.Radios;
using global::Windows.Foundation;
using global::Windows.Storage.Streams;

using HeyMelody.Diagnostics;

namespace HeyMelodyWindows.Host;

/// <summary>OS callbacks and BLE observations never perform synchronous SDP or native enumeration.</summary>
internal sealed class WindowsStateMonitor(BackendSupervisor supervisor) : IDisposable
{
    private readonly CancellationTokenSource _lifetime = new();
    private readonly ConcurrentDictionary<string, ulong> _addresses = new(StringComparer.Ordinal);
    private readonly List<Radio> _radios = [];
    private readonly object _refreshSync = new();
    private readonly object _observerSync = new();
    private DeviceWatcher? _devices;
    private List<Scan>? _advertisements;
    private PowerMessageWindow? _power;
    private CancellationTokenSource? _advertisementRetry;
    private CancellationTokenSource? _deviceRetry;
    private bool _catalogScheduled, _catalogDirty;
    // BLE scanning runs only while the supervisor has a present, allowed headset to serve (guarded by _observerSync).
    private bool _scanWanted;
    private bool? _radioAvailable;
    // Set once the radio state has been evaluated (UpdateRadio) or its read failed (the fallback in StartAsync); until then scanning waits.
    private bool _radioChecked;
    private int _advertisementFailures;
    private int _deviceFailures;
    private long _scanGeneration;
    private volatile bool _suspended, _disposed;

    public async Task StartAsync()
    {
        if (!OperatingSystem.IsWindows()) return;
        DiagnosticLog.Debug("host.monitor", "starting", "Starting Windows Bluetooth and power observers");
        supervisor.ScanDemandChanged += OnScanDemand;
        // The catalog may be known already; scanning itself starts below, once the radio state is.
        lock (_observerSync) _scanWanted = supervisor.HasScanDemand();
        try
        {
            _power = new PowerMessageWindow(OnSuspend);
            if (!_power.IsRegistered) supervisor.ReportHealth("休眠状态通知暂不可用；蓝牙状态监听继续运行。");
        }
        catch (Exception error)
        {
            DiagnosticLog.Error("host.monitor", "powerObserverFailed", "Power state observer could not start", error);
            supervisor.ReportHealth("休眠状态通知暂不可用；蓝牙状态监听继续运行。");
        }
        try
        {
            var selector = BluetoothDevice.GetDeviceSelectorFromPairingState(true);
            _devices = DeviceInformation.CreateWatcher(selector,
                ["System.Devices.Aep.DeviceAddress", "System.Devices.Aep.IsConnected"], DeviceInformationKind.AssociationEndpoint);
            _devices.Added += Added; _devices.Updated += Updated; _devices.Removed += Removed;
            _devices.EnumerationCompleted += (_, _) => { lock (_observerSync) _deviceFailures = 0; ScheduleCatalog(); };
            _devices.Stopped += (_, _) =>
            {
                DiagnosticLog.Debug("host.monitor", "deviceWatcherStopped", "Windows paired-device watcher stopped");
                RestartDeviceWatcher();
            };
            _devices.Start();
        }
        catch (Exception error)
        {
            DiagnosticLog.Error("host.monitor", "deviceWatcherStartFailed", "Windows paired-device watcher could not start", error);
            supervisor.ReportHealth("蓝牙设备事件监听暂不可用；已连接耳机的状态监听继续运行。"); RestartDeviceWatcher();
        }
        try
        {
            var radios = await Radio.GetRadiosAsync();
            lock (_observerSync)
                foreach (var radio in radios.Where(r => r.Kind == RadioKind.Bluetooth))
                { _radios.Add(radio); radio.StateChanged += RadioChanged; }
            UpdateRadio();
        }
        catch (Exception error)
        {
            DiagnosticLog.Error("host.monitor", "radioEnumerationFailed", "Windows Bluetooth radios could not be read", error);
            supervisor.ReportHealth("无法读取蓝牙开关状态；已连接耳机的状态监听继续运行。");
            lock (_observerSync) _radioChecked = true;
            StartAdvertisements();
        }
    }
    private void Added(DeviceWatcher watcher, DeviceInformation info)
    {
        if (_disposed || _suspended) return;
        if (TryAddress(info.Properties, out var address))
        {
            _addresses[info.Id] = address; supervisor.ObservePairing(address, true);
            if (TryConnected(info.Properties, out var connected)) supervisor.ObserveConnection(address, connected);
        }
        ScheduleCatalog();
    }
    private void Updated(DeviceWatcher watcher, DeviceInformationUpdate update)
    {
        if (_disposed || _suspended) return;
        if (TryAddress(update.Properties, out var parsed)) _addresses[update.Id] = parsed;
        if (_addresses.TryGetValue(update.Id, out var address) && TryConnected(update.Properties, out var connected))
            supervisor.ObserveConnection(address, connected);
        if (update.Properties.ContainsKey("System.Devices.Aep.IsConnected")
            || update.Properties.ContainsKey("System.Devices.Aep.DeviceAddress")
            || update.Properties.ContainsKey("System.ItemNameDisplay")) ScheduleCatalog();
    }
    private void Removed(DeviceWatcher watcher, DeviceInformationUpdate update)
    {
        if (_disposed || _suspended) return;
        if (_addresses.TryRemove(update.Id, out var address)) supervisor.ObservePairing(address, false);
        ScheduleCatalog();
    }
    private static bool TryAddress(IReadOnlyDictionary<string, object> properties, out ulong address)
    {
        address = 0;
        if (!properties.TryGetValue("System.Devices.Aep.DeviceAddress", out var value) || value is not string text) return false;
        var digits = string.Concat(text.Where(Uri.IsHexDigit));
        return digits.Length == 12 && ulong.TryParse(digits, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out address) && address != 0;
    }
    private static bool TryConnected(IReadOnlyDictionary<string, object> properties, out bool connected)
    {
        connected = false;
        if (!properties.TryGetValue("System.Devices.Aep.IsConnected", out var value) || value is not bool flag) return false;
        connected = flag; return true;
    }
    private void OnScanDemand()
    {
        try
        {
            lock (_observerSync)
            {
                if (_disposed) return;
                // The event only says "look again": notifications queue on the thread pool and may run out of order.
                bool wanted = supervisor.HasScanDemand();
                if (wanted == _scanWanted) return;
                _scanWanted = wanted;
                if (wanted) _advertisementFailures = 0;
                // Before the radio state is known only the demand is recorded: UpdateRadio (or the fallback in StartAsync) starts scanning.
                // Afterwards radio and suspend state decide inside StartAdvertisements, as they do for UpdateRadio.
                if (!wanted) StopAdvertisements();
                else if (_radioChecked) StartAdvertisements();
            }
        }
        catch (Exception error)
        { DiagnosticLog.Error("host.monitor", "scanDemandFailed", "Bluetooth scan demand update failed", error); }
    }
    private void RadioChanged(Radio sender, object args)
    {
        if (_disposed) return;
        try { UpdateRadio(); }
        catch (Exception error)
        {
            DiagnosticLog.Error("host.monitor", "radioChangedFailed", "Bluetooth radio state update failed", error);
            supervisor.ReportHealth("无法读取蓝牙开关状态；已连接耳机的状态监听继续运行。");
        }
    }
    private void UpdateRadio()
    {
        lock (_observerSync)
        {
            if (_disposed) return;
            _radioChecked = true;
            bool? available = _radios.Any(r => r.State == RadioState.On) ? true
                : _radios.Count > 0 && _radios.All(r => r.State is RadioState.Off or RadioState.Disabled) ? false : null;
            if (available is { } state)
            {
                var changed = _radioAvailable != available; _radioAvailable = available;
                if (changed)
                {
                    DiagnosticLog.Debug("host.monitor", "radioChanged", "Bluetooth radio availability changed", new { available });
                    supervisor.SetRadioAvailable(state); _advertisementFailures = _deviceFailures = 0;
                    if (state) RestartDeviceWatcher();
                }
            }
            if (_radioAvailable != false && !_suspended) { StartAdvertisements(); ScheduleCatalog(); }
            else StopAdvertisements();
        }
    }
    private void RestartDeviceWatcher()
    {
        lock (_observerSync)
        {
            if (_disposed || _suspended || _radioAvailable == false || _devices is null || _deviceRetry is not null
                || _devices.Status is DeviceWatcherStatus.Started or DeviceWatcherStatus.EnumerationCompleted or DeviceWatcherStatus.Stopping
                || ++_deviceFailures > 3) return;
            var delay = TimeSpan.FromMilliseconds(_deviceFailures switch { 1 => 250, 2 => 1000, _ => 2000 });
            var retry = _deviceRetry = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            _ = RetryAsync();
            async Task RetryAsync()
            {
                try
                {
                    await Task.Delay(delay, retry.Token);
                    lock (_observerSync)
                    {
                        if (!ReferenceEquals(_deviceRetry, retry)) return;
                        _deviceRetry = null;
                        if (_disposed || _suspended || _radioAvailable == false) return;
                        try
                        {
                            if (_devices.Status is DeviceWatcherStatus.Created or DeviceWatcherStatus.Stopped or DeviceWatcherStatus.Aborted)
                                _devices.Start();
                        }
                        catch (Exception error)
                        {
                            DiagnosticLog.Error("host.monitor", "deviceWatcherRestartFailed", "Windows paired-device watcher could not restart", error);
                            supervisor.ReportHealth("蓝牙设备事件监听暂不可用；已连接耳机的状态监听继续运行。"); RestartDeviceWatcher();
                        }
                    }
                }
                catch (OperationCanceledException) { }
                finally { retry.Dispose(); }
            }
        }
    }
    // A watcher with its registered delegates, so that releasing it can detach them.
    private sealed record Scan(BluetoothLEAdvertisementWatcher Watcher,
        TypedEventHandler<BluetoothLEAdvertisementWatcher, BluetoothLEAdvertisementReceivedEventArgs> Received,
        TypedEventHandler<BluetoothLEAdvertisementWatcher, BluetoothLEAdvertisementWatcherStoppedEventArgs> Stopped);
    private static readonly ushort[] CompanyIds = [0x079A, 0x072F];
    private void StartAdvertisements()
    {
        lock (_observerSync)
        {
            if (_disposed || _suspended || _radioAvailable == false || !_scanWanted || _advertisements is not null || _advertisementRetry is not null) return;
            long generation = Interlocked.Increment(ref _scanGeneration);
            var startedAt = DateTimeOffset.UtcNow;
            var scans = _advertisements = new List<Scan>(CompanyIds.Length);
            try
            {
                // One watcher per company ID: the documentation does not say whether several patterns in one filter match
                // any or all of them, so each watcher carries a single pattern. A byte pattern has documented semantics
                // (these bytes at this offset of a section of this type), unlike an empty manufacturer payload: the
                // manufacturer-specific section starts with the little-endian company ID, so the OS drops every other
                // packet before delivery. Advertisement() still checks the company ID itself.
                foreach (var company in CompanyIds)
                {
                    var watcher = new BluetoothLEAdvertisementWatcher { ScanningMode = BluetoothLEScanningMode.Active };
                    using (var writer = new DataWriter { ByteOrder = ByteOrder.LittleEndian })
                    {
                        writer.WriteUInt16(company);
                        watcher.AdvertisementFilter.BytePatterns.Add(new BluetoothLEAdvertisementBytePattern
                        { DataType = BluetoothLEAdvertisementDataTypes.ManufacturerSpecificData, Offset = 0, Data = writer.DetachBuffer() });
                    }
                    TypedEventHandler<BluetoothLEAdvertisementWatcher, BluetoothLEAdvertisementReceivedEventArgs> received =
                        (_, args) => Advertisement(generation, startedAt, args);
                    TypedEventHandler<BluetoothLEAdvertisementWatcher, BluetoothLEAdvertisementWatcherStoppedEventArgs> stopped =
                        (_, args) =>
                        {
                            DiagnosticLog.Debug("host.monitor", "advertisementWatcherStopped", "BLE advertisement watcher stopped",
                                new { generation, args.Error });
                            AdvertisementFailed(generation);
                        };
                    watcher.Received += received; watcher.Stopped += stopped;
                    scans.Add(new(watcher, received, stopped));
                }
                foreach (var scan in scans) scan.Watcher.Start();
            }
            catch (Exception error)
            {
                DiagnosticLog.Error("host.monitor", "advertisementWatcherStartFailed", "BLE advertisement watchers could not start", error, new { generation });
                AdvertisementFailed(generation);
            }
        }
    }
    private void AdvertisementFailed(long generation)
    {
        lock (_observerSync)
        {
            if (_disposed || _suspended || _radioAvailable == false || !_scanWanted || generation != _scanGeneration) return;
            // A new generation makes the sibling watcher's Stopped (and late Received) callbacks no-ops.
            Interlocked.Increment(ref _scanGeneration);
            ReleaseAdvertisementsLocked();
            supervisor.ReportHealth("耳机广播监听暂不可用；已连接耳机的状态监听继续运行。");
            if (_advertisementRetry is not null || ++_advertisementFailures > 3) return;
            var delay = TimeSpan.FromSeconds(_advertisementFailures switch { 1 => 1, 2 => 2, _ => 5 });
            var retry = _advertisementRetry = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            _ = RetryAsync();
            async Task RetryAsync()
            {
                try
                {
                    await Task.Delay(delay, retry.Token);
                    lock (_observerSync)
                    {
                        if (!ReferenceEquals(_advertisementRetry, retry)) return;
                        _advertisementRetry = null;
                        if (_scanWanted) StartAdvertisements();
                    }
                }
                catch (OperationCanceledException) { }
                finally { retry.Dispose(); }
            }
        }
    }
    private void ReleaseAdvertisementsLocked()
    {
        var scans = _advertisements; _advertisements = null;
        if (scans is null) return;
        foreach (var (watcher, received, stopped) in scans)
        {
            try { watcher.Received -= received; watcher.Stopped -= stopped; } catch { }
            try { watcher.Stop(); } catch { }
        }
    }
    internal static bool IsCurrentAdvertisement(DateTimeOffset timestamp, DateTimeOffset startedAt, short rssi)
        => timestamp >= startedAt && rssi != -127;
    private void Advertisement(long generation, DateTimeOffset startedAt, BluetoothLEAdvertisementReceivedEventArgs args)
    {
        if (_disposed || _suspended || generation != Volatile.Read(ref _scanGeneration)
            || !IsCurrentAdvertisement(args.Timestamp, startedAt, args.RawSignalStrengthInDBm)) return;
        try
        {
            foreach (var manufacturer in args.Advertisement.ManufacturerData)
            {
                if (manufacturer.CompanyId is not (0x079A or 0x072F)) continue;
                using var reader = DataReader.FromBuffer(manufacturer.Data);
                if (reader.UnconsumedBufferLength > 4096) continue;
                var data = new byte[reader.UnconsumedBufferLength]; reader.ReadBytes(data);
                DiagnosticLog.Bytes("host.monitor", "advertisementReceived", data,
                    new { manufacturer.CompanyId, args.BluetoothAddress, args.BluetoothAddressType, args.Timestamp, args.RawSignalStrengthInDBm, generation });
                if (!OppoAdvertisementParser.TryParse(manufacturer.CompanyId, data, out var parsed) || parsed is null
                    || !parsed.TryResolveAddress(args.BluetoothAddress, args.BluetoothAddressType == BluetoothAddressType.Public, out var address)) continue;
                lock (_observerSync)
                {
                    if (_disposed || _suspended || generation != _scanGeneration) return;
                    _advertisementFailures = 0;
                    supervisor.ObserveBroadcast(address, new(parsed.ProductId,
                        parsed.CaseOpen is { } lid ? lid ? CaseLidState.Open : CaseLidState.Closed : CaseLidState.Unknown,
                        (EarbudPlacement)parsed.LeftPlacement, (EarbudPlacement)parsed.RightPlacement,
                        parsed.LeftBattery, parsed.RightBattery, parsed.CaseBattery, null, null, null, args.Timestamp)
                        { Color = parsed.Color });
                }
            }
        }
        catch (Exception error)
        { DiagnosticLog.Error("host.monitor", "advertisementFailed", "BLE advertisement processing failed", error, new { generation }); }
    }
    private void StopAdvertisements()
    {
        lock (_observerSync)
        {
            Interlocked.Increment(ref _scanGeneration);
            _advertisementRetry?.Cancel(); _advertisementRetry?.Dispose(); _advertisementRetry = null;
            ReleaseAdvertisementsLocked();
        }
    }
    private void OnSuspend(bool suspended)
    {
        if (_disposed) return;
        lock (_observerSync)
        {
            if (_suspended == suspended) return;
            DiagnosticLog.Debug("host.monitor", "suspendChanged", "Windows suspend state changed", new { suspended });
            _suspended = suspended; _advertisementFailures = _deviceFailures = 0;
            if (suspended)
            {
                var retry = _deviceRetry; _deviceRetry = null;
                if (retry is not null) { retry.Cancel(); retry.Dispose(); }
            }
            supervisor.SetSuspended(suspended);
        }
        if (suspended)
        { StopAdvertisements(); try { _devices?.Stop(); } catch { } }
        else
        {
            RestartDeviceWatcher();
            UpdateRadio(); ScheduleCatalog();
        }
    }
    private void ScheduleCatalog()
    {
        if (_disposed || _suspended) return;
        lock (_refreshSync)
        {
            _catalogDirty = true;
            if (_catalogScheduled) return;
            _catalogScheduled = true;
        }
        _ = RefreshAsync();
        async Task RefreshAsync()
        {
            try
            {
                while (!_disposed && !_suspended)
                {
                    await Task.Delay(200, _lifetime.Token);
                    lock (_refreshSync) _catalogDirty = false;
                    try { await supervisor.RefreshCatalogAsync(_lifetime.Token); }
                    catch (Exception error) when (!_lifetime.IsCancellationRequested)
                    { DiagnosticLog.Error("host.monitor", "catalogRefreshFailed", "Scheduled Bluetooth catalog refresh failed", error); }
                    lock (_refreshSync) if (!_catalogDirty) return;
                }
            }
            catch (OperationCanceledException) { }
            finally
            {
                bool reschedule;
                lock (_refreshSync) { _catalogScheduled = false; reschedule = _catalogDirty && !_disposed && !_suspended; }
                if (reschedule) ScheduleCatalog();
            }
        }
    }
    public void Dispose()
    {
        if (_disposed) return; _disposed = true;
        supervisor.ScanDemandChanged -= OnScanDemand;
        StopAdvertisements();
        lock (_observerSync)
        {
            var retry = _deviceRetry; _deviceRetry = null;
            if (retry is not null) { retry.Cancel(); retry.Dispose(); }
            foreach (var radio in _radios) radio.StateChanged -= RadioChanged;
        }
        _lifetime.Cancel(); try { _devices?.Stop(); } catch { }
        _power?.Dispose();
    }
}

internal sealed class PowerMessageWindow : IDisposable
{
    private readonly WindowProc _callback;
    private readonly Action<bool> _suspend;
    private nint _window, _previous, _powerRegistration;
    internal bool IsRegistered => _powerRegistration != 0;
    internal PowerMessageWindow(Action<bool> suspend)
    {
        _suspend = suspend; _callback = Procedure;
        // Invisible top-level windows receive power broadcasts; message-only windows do not.
        _window = CreateWindowEx(0, "STATIC", "HeyMelodyPower", 0, 0, 0, 0, 0, 0, 0, 0, 0);
        if (_window == 0) return;
        var callback = Marshal.GetFunctionPointerForDelegate(_callback);
        _previous = nint.Size == 8 ? SetWindowLongPtr(_window, -4, callback) : SetWindowLong(_window, -4, callback);
        _powerRegistration = RegisterSuspendResumeNotification(_window, 0);
    }
    private nint Procedure(nint window, uint message, nuint wparam, nint lparam)
    {
        if (message == 0x218)
        {
            try
            {
                if (wparam == 4) _suspend(true);
                else if (wparam is 7 or 18) _suspend(false);
            }
            catch (Exception error)
            {
                DiagnosticLog.Error("host.monitor", "powerNotificationFailed", "Windows power notification callback failed", error,
                    new { message, wparam = (ulong)wparam });
            } // Never let a managed exception cross the native window procedure.
        }
        return _previous == 0 ? DefWindowProc(window, message, wparam, lparam) : CallWindowProc(_previous, window, message, wparam, lparam);
    }
    public void Dispose()
    {
        if (_window == 0) return;
        if (_powerRegistration != 0) { UnregisterSuspendResumeNotification(_powerRegistration); _powerRegistration = 0; }
        if (_previous != 0) { if (nint.Size == 8) SetWindowLongPtr(_window, -4, _previous); else SetWindowLong(_window, -4, _previous); }
        DestroyWindow(_window); _window = 0; GC.KeepAlive(_callback);
    }
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate nint WindowProc(nint window, uint message, nuint wparam, nint lparam);
    [DllImport("user32.dll", EntryPoint = "CreateWindowExW", CharSet = CharSet.Unicode)] private static extern nint CreateWindowEx(uint extendedStyle, string className, string name, uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern nint SetWindowLongPtr(nint window, int index, nint value);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")] private static extern nint SetWindowLong(nint window, int index, nint value);
    [DllImport("user32.dll", EntryPoint = "CallWindowProcW")] private static extern nint CallWindowProc(nint previous, nint window, uint message, nuint wparam, nint lparam);
    [DllImport("user32.dll", EntryPoint = "DefWindowProcW")] private static extern nint DefWindowProc(nint window, uint message, nuint wparam, nint lparam);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DestroyWindow(nint window);
    [DllImport("user32.dll", SetLastError = true)] private static extern nint RegisterSuspendResumeNotification(nint recipient, uint flags);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool UnregisterSuspendResumeNotification(nint registration);
}
