namespace HeyMelody.Platform;

public readonly record struct BluetoothScanRequest(bool Wanted, long Generation);

/// <summary>Rejects observations queued before a scan request or from a retired native scanner.</summary>
public sealed class BluetoothObservationGate(bool acceptLegacyWatcher = false)
{
    private readonly object _sync = new();
    private bool _demand, _suspended, _available, _sourceActive;
    private long _requestGeneration, _sourceGeneration, _retiredGeneration;
    private DateTimeOffset _requestedAt, _sourceStartedAt;
    private BluetoothScanRequest Current => new(_demand && !_suspended && _available, _requestGeneration);
    public BluetoothScanRequest SetDemand(bool wanted, DateTimeOffset now)
    { lock (_sync) { if (_demand != wanted) { _demand = wanted; Advance(now); } return Current; } }
    public BluetoothScanRequest SetSuspended(bool suspended, DateTimeOffset now)
    { lock (_sync) { if (_suspended != suspended) { _suspended = suspended; Advance(now); } return Current; } }
    public BluetoothScanRequest SetAvailable(bool available, DateTimeOffset now)
    { lock (_sync) { if (_available != available) { _available = available; Advance(now); } return Current; } }
    private void Advance(DateTimeOffset now)
    {
        _requestGeneration++;
        _requestedAt = Milliseconds(now);
        _sourceActive = false;
    }
    public void ObserveScan(BluetoothScanChanged scan)
    {
        lock (_sync)
        {
            if (!Current.Wanted || scan.RequestGeneration != _requestGeneration || scan.Generation < _sourceGeneration) return;
            _sourceGeneration = scan.Generation;
            if (!scan.Active)
            { _retiredGeneration = Math.Max(_retiredGeneration, scan.Generation); _sourceActive = false; return; }
            if (scan.Generation <= _retiredGeneration) return;
            _sourceStartedAt = Milliseconds(scan.StartedAt); _sourceActive = true;
        }
    }
    public bool Accept(BluetoothAdvertisementReceived advertisement)
    {
        lock (_sync)
        {
            if (!Current.Wanted || !BluetoothCandidates.IsCurrentAdvertisement(Milliseconds(advertisement.Timestamp), _requestedAt, advertisement.Rssi)) return false;
            // The Windows watcher validates its own captured scan generation before publishing.
            if (advertisement.Generation == 0) return acceptLegacyWatcher;
            return _sourceActive && advertisement.RequestGeneration == _requestGeneration && advertisement.Generation == _sourceGeneration &&
                Milliseconds(advertisement.Timestamp) >= _sourceStartedAt;
        }
    }
    private static DateTimeOffset Milliseconds(DateTimeOffset value) => DateTimeOffset.FromUnixTimeMilliseconds(value.ToUnixTimeMilliseconds());
}
