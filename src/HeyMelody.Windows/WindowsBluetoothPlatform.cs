using HeyMelody.Platform;

namespace HeyMelodyWindows.Windows;

public sealed class WindowsBluetoothPlatform : IBluetoothPlatform
{
    private WindowsStateMonitor? _monitor;
    private bool _scanDemand;
    private long _scanRequestGeneration;
    private readonly object _scanGate = new();
    public BluetoothPlatformStatus Status { get; private set; } = new(BluetoothAccess.Allowed, null);
    public event Action<BluetoothPlatformEvent>? EventReceived;
    internal event Action? ScanDemandChanged;
    public async Task StartAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        _monitor ??= new(this);
        await _monitor.StartAsync();
    }
    public void SetScanDemand(bool wanted, long requestGeneration = 0)
    {
        lock (_scanGate)
        {
            if (requestGeneration < _scanRequestGeneration) return;
            _scanRequestGeneration = requestGeneration;
            if (_scanDemand == wanted) return;
            _scanDemand = wanted;
        }
        ScanDemandChanged?.Invoke();
    }
    internal bool HasScanDemand() { lock (_scanGate) return _scanDemand; }
    internal void ObservePairing(ulong address, bool paired) => EventReceived?.Invoke(new BluetoothPairingChanged(address, paired));
    internal void ObserveConnection(ulong address, bool connected) => EventReceived?.Invoke(new BluetoothConnectionChanged(address, connected));
    internal void SetSuspended(bool suspended) => EventReceived?.Invoke(new BluetoothSuspensionChanged(suspended));
    internal void SetRadioAvailable(bool available)
    {
        Status = new(BluetoothAccess.Allowed, available); EventReceived?.Invoke(new BluetoothStatusChanged(Status));
    }
    internal void ReportHealth(string message) => EventReceived?.Invoke(new BluetoothHealthChanged(message));
    internal Task RefreshCatalogAsync(CancellationToken token)
    { token.ThrowIfCancellationRequested(); EventReceived?.Invoke(new BluetoothCatalogChanged()); return Task.CompletedTask; }
    internal void ObserveAdvertisement(ushort companyId, byte[] data, ulong sender, bool publicSender, DateTimeOffset timestamp, short rssi)
        => EventReceived?.Invoke(new BluetoothAdvertisementReceived(companyId, data, sender, publicSender, timestamp, rssi));
    public Task<IReadOnlyList<BluetoothCandidate>> EnumeratePairedAsync(CancellationToken token)
    { token.ThrowIfCancellationRequested(); return Task.FromResult(WindowsBluetooth.EnumeratePaired()); }
    public async Task<Stream> OpenControlChannelAsync(ulong address, IReadOnlyList<Guid> serviceIds, CancellationToken token)
    {
        try { return new ChannelStream(await RfcommConnection.ConnectControlAsync(address, serviceIds, token)); }
        catch (RfcommServiceDiscoveryException error)
        {
            throw new BluetoothDiscoveryException(error.IsAmbiguous ? BluetoothDiscoveryFailure.AmbiguousServices : BluetoothDiscoveryFailure.ServiceNotFound,
                error.Message);
        }
    }
    public void Dispose() { _monitor?.Dispose(); _monitor = null; }

    private sealed class ChannelStream(RfcommConnection connection) : Stream
    {
        public override bool CanRead => true;
        public override bool CanWrite => true;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default) => new(connection.ReadAsync(buffer, token));
        public override void Write(byte[] buffer, int offset, int count) => WriteAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken token = default) => new(connection.SendAsync(buffer, token));
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) connection.DisposeAsync().AsTask().GetAwaiter().GetResult(); base.Dispose(disposing); }
        public override ValueTask DisposeAsync() => connection.DisposeAsync();
    }
}
