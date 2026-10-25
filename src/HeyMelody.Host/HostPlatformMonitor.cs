using HeyMelody.Platform;
using HeyMelodyWindows.App;
using HeyMelodyWindows.Protocol;

namespace HeyMelodyWindows.Host;

internal sealed class HostPlatformMonitor(BackendSupervisor supervisor) : IDisposable
{
    private readonly IBluetoothPlatform _platform = HostPlatformFactory.CreateBluetooth();
    private readonly BluetoothObservationGate _observations = new(OperatingSystem.IsWindows());
    private bool _disposed;
    internal bool CanEnumerate => _platform.Status.Access == BluetoothAccess.Allowed && _platform.Status.RadioAvailable != false;
    public async Task StartAsync()
    {
        _platform.EventReceived += Observe;
        supervisor.ScanDemandChanged += ScanDemandChanged;
        supervisor.UpdatePlatformStatus(_platform.Status);
        SetAvailability(_platform.Status);
        await _platform.StartAsync(CancellationToken.None);
        ScanDemandChanged();
    }
    private void ScanDemandChanged() => SetScanRequest(_observations.SetDemand(supervisor.HasScanDemand(), DateTimeOffset.UtcNow));
    private void SetScanRequest(BluetoothScanRequest request) => _platform.SetScanDemand(request.Wanted, request.Generation);
    private void SetAvailability(BluetoothPlatformStatus status) => SetScanRequest(_observations.SetAvailable(
        status.Access == BluetoothAccess.Allowed && status.RadioAvailable != false, DateTimeOffset.UtcNow));
    private void Observe(BluetoothPlatformEvent observation)
    {
        if (_disposed) return;
        switch (observation)
        {
            case BluetoothStatusChanged value:
                SetAvailability(value.Status);
                supervisor.UpdatePlatformStatus(value.Status);
                if (value.Status.Access is BluetoothAccess.Denied or BluetoothAccess.Restricted or BluetoothAccess.Unsupported or BluetoothAccess.NotDetermined)
                    supervisor.SetRadioAvailable(false);
                else if (value.Status.RadioAvailable is { } available) supervisor.SetRadioAvailable(available);
                if (value.Status.Message is { } message) supervisor.ReportHealth(message);
                break;
            case BluetoothPairingChanged value: supervisor.ObservePairing(value.Address, value.Paired); break;
            case BluetoothConnectionChanged value: supervisor.ObserveConnection(value.Address, value.Connected); break;
            case BluetoothSuspensionChanged value:
                SetScanRequest(_observations.SetSuspended(value.Suspended, DateTimeOffset.UtcNow));
                supervisor.SetSuspended(value.Suspended); break;
            case BluetoothHealthChanged value: supervisor.ReportHealth(value.Message); break;
            case BluetoothCatalogChanged when CanEnumerate: _ = RefreshAsync(); break;
            case BluetoothScanChanged value: _observations.ObserveScan(value); break;
            case BluetoothAdvertisementReceived value:
                if (_observations.Accept(value) && OppoAdvertisementParser.TryParse(value.CompanyId, value.Data, out var parsed) && parsed is not null &&
                    parsed.TryResolveAddress(value.SenderAddress, value.PublicSender, out var address))
                    supervisor.ObserveBroadcast(address, new(parsed.ProductId,
                        parsed.CaseOpen is { } lid ? lid ? CaseLidState.Open : CaseLidState.Closed : CaseLidState.Unknown,
                        (EarbudPlacement)parsed.LeftPlacement, (EarbudPlacement)parsed.RightPlacement,
                        parsed.LeftBattery, parsed.RightBattery, parsed.CaseBattery, null, null, null, value.Timestamp) { Color = parsed.Color });
                break;
        }
    }
    private async Task RefreshAsync()
    {
        try { await supervisor.RefreshCatalogAsync(CancellationToken.None); }
        catch (Exception error) { HeyMelody.Diagnostics.DiagnosticLog.Warning("host.monitor", "catalogRefreshFailed", "Platform catalog refresh failed", error); }
    }
    public void Dispose()
    {
        if (_disposed) return; _disposed = true;
        supervisor.ScanDemandChanged -= ScanDemandChanged;
        _platform.EventReceived -= Observe; _platform.Dispose();
    }
}
