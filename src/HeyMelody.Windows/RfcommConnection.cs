using System.ComponentModel;
using HeyMelody.Diagnostics;

namespace HeyMelodyWindows.Windows;

/// <summary>Raw RFCOMM transport. One native event registration reports readiness without polling.
/// The caller owns command policy; control connection discovery uses only its configured UUIDs.</summary>
public sealed class RfcommConnection : IAsyncDisposable
{
    private readonly SafeRfcommSocket _socket;
    private readonly string _diagnosticId = Guid.NewGuid().ToString("N");
    private readonly object _nativeGate = new();
    private readonly SemaphoreSlim _readGate = new(1, 1), _writeGate = new(1, 1);
    private readonly EventWaitHandle _networkEvent = new(false, EventResetMode.ManualReset);
    private RegisteredWaitHandle? _registration;
    private readonly TaskCompletionSource _connectionReady = NewSignal();
    private TaskCompletionSource? _readReady, _writeReady;
    private Exception? _failure;
    private bool _remoteClosed;
    private int _disposed;
    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private RfcommConnection(SafeRfcommSocket socket)
    {
        _socket = socket;
        int result = Native.WSAEventSelect(socket.DangerousGetHandle(), _networkEvent.SafeWaitHandle.DangerousGetHandle(),
            Native.FdRead | Native.FdWrite | Native.FdConnect | Native.FdClose);
        if (result != 0)
        {
            int error = Native.WSAGetLastError(); _networkEvent.Dispose();
            throw Native.Error(error, "RFCOMM event registration failed.");
        }
        ArmWait();
    }
    private void ArmWait()
    {
        // Native readiness belongs to the channel lifetime, not the request that happened to create it.
        using var operation = DiagnosticLog.SuspendOperation();
        _registration = ThreadPool.RegisterWaitForSingleObject(_networkEvent,
            static (state, _) => ((RfcommConnection)state!).OnNetworkEvent(), this, Timeout.Infinite, executeOnlyOnce: true);
    }

    /// <summary>Connects once to the explicit SDP-derived Melody channel; never falls back or reconnects.</summary>
    public static Task<RfcommConnection> ConnectAsync(ulong address, Guid serviceId, uint channel, CancellationToken token)
    {
        Native.Validate(address, serviceId);
        DiagnosticLog.Info("Windows.Bluetooth", "rfcomm.connect.requested", "Connecting to the explicit RFCOMM channel.", new { address = $"{address:X12}", serviceId, channel });
        if (channel is < 1 or > 30) throw new ArgumentOutOfRangeException(nameof(channel));
        return ConnectCoreAsync(address, channel, token);
    }

    /// <summary>Connects to a unique configured service. A failed cached endpoint is refreshed
    /// once before a second connection attempt; no commands are submitted here.</summary>
    public static Task<RfcommConnection> ConnectControlAsync(ulong address, IReadOnlyList<Guid> serviceIds, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(serviceIds);
        if (serviceIds.Count == 0) throw new ArgumentException("No control service UUIDs were configured.", nameof(serviceIds));
        foreach (var serviceId in serviceIds) Native.Validate(address, serviceId);
        DiagnosticLog.Info("Windows.Bluetooth", "rfcomm.control.requested", "Resolving the configured control services.", new { address = $"{address:X12}", serviceIds });
        return ConnectControlCoreAsync(serviceIds, (serviceId, refreshCache) => WindowsSdp.FindServices(address, serviceId, refreshCache),
            (service, connectionToken) => ConnectAsync(address, service.ServiceId, service.Channel, connectionToken), token);
    }

    internal static async Task<T> ConnectControlCoreAsync<T>(IReadOnlyList<Guid> serviceIds,
        Func<Guid, bool, IReadOnlyList<RfcommService>> lookup,
        Func<RfcommService, CancellationToken, Task<T>> connect, CancellationToken token, TimeSpan? connectionTimeout = null)
    {
        IReadOnlyList<RfcommService> Lookup(Guid serviceId, bool refreshCache)
        {
            token.ThrowIfCancellationRequested();
            var services = lookup(serviceId, refreshCache);
            DiagnosticLog.Debug("Windows.Bluetooth", "sdp.services", "Resolved RFCOMM service records.", new { serviceId, refreshCache, services });
            token.ThrowIfCancellationRequested();
            return services;
        }
        async Task<T> ConnectOnce(RfcommService service)
        {
            token.ThrowIfCancellationRequested();
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(token);
            budget.CancelAfter(connectionTimeout ?? TimeSpan.FromSeconds(5));
            return await connect(service, budget.Token);
        }
        var service = WindowsSdp.FindUniqueControlService(serviceIds, Lookup, false, out bool queriedPeer);
        try { return await ConnectOnce(service); }
        catch (Exception failure) when (!queriedPeer && !token.IsCancellationRequested
            && failure is IOException or Win32Exception or OperationCanceledException)
        {
            // A unique cached channel can still be stale. Use the complete fresh result,
            // without merging cached endpoints or retrying a failed fresh connection.
            token.ThrowIfCancellationRequested();
            service = WindowsSdp.FindUniqueControlService(serviceIds, Lookup, true, out _);
            return await ConnectOnce(service);
        }
    }

    private static async Task<RfcommConnection> ConnectCoreAsync(ulong address, uint channel, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); Native.Startup();
        nint raw = Native.socket(Native.AfBth, Native.SockStream, Native.Rfcomm);
        if (raw == -1) throw Native.Error(Native.WSAGetLastError(), "RFCOMM socket creation failed.");
        var socket = new SafeRfcommSocket(raw);
        RfcommConnection? connection = null;
        try
        {
            connection = new(socket);
            if (!connection.BeginConnect(address, channel))
                await connection._connectionReady.Task.WaitAsync(token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested(); connection.ThrowIfUnavailable();
            DiagnosticLog.Info("Windows.Bluetooth", "rfcomm.connected", "The RFCOMM connection is ready.", new { connectionId = connection._diagnosticId, address = $"{address:X12}", channel });
            return connection;
        }
        catch (Exception error)
        {
            DiagnosticLog.Error("Windows.Bluetooth", "rfcomm.connect.failed", "RFCOMM connection failed.", error,
                new { connectionId = connection?._diagnosticId, address = $"{address:X12}", channel });
            if (connection is not null) await connection.DisposeAsync(); else socket.Dispose();
            throw;
        }
    }
    private unsafe bool BeginConnect(ulong address, uint channel)
    {
        lock (_nativeGate)
        {
            var peer = new Native.SockAddrBth { Family = Native.AfBth, Address = address, ServiceId = Guid.Empty, Port = channel };
            int result = Native.connect(_socket.DangerousGetHandle(), ref peer, sizeof(Native.SockAddrBth));
            int error = result == 0 ? 0 : Native.WSAGetLastError();
            if (result != 0 && error != Native.WouldBlock) throw Native.Error(error, "RFCOMM connection failed.");
            if (result == 0) _connectionReady.TrySetResult();
            return result == 0;
        }
    }
    private void OnNetworkEvent()
    {
        lock (_nativeGate)
        {
            if (_disposed != 0) return;
            int result = Native.WSAEnumNetworkEvents(_socket.DangerousGetHandle(),
                _networkEvent.SafeWaitHandle.DangerousGetHandle(), out var events);
            if (result != 0) Fail(Native.Error(Native.WSAGetLastError(), "RFCOMM event enumeration failed."));
            else
            {
                for (int bit = 0; bit < 6; bit++)
                    if ((events.Events & (1 << bit)) != 0 && events.Errors[bit] != 0)
                    { Fail(Native.Error(events.Errors[bit], "RFCOMM network event failed.")); break; }
                if (_failure is null)
                {
                    if ((events.Events & Native.FdConnect) != 0) _connectionReady.TrySetResult();
                    if ((events.Events & Native.FdClose) != 0) _remoteClosed = true;
                    if ((events.Events & (Native.FdRead | Native.FdClose)) != 0)
                    { _readReady?.TrySetResult(); _readReady = null; }
                    if ((events.Events & (Native.FdWrite | Native.FdClose)) != 0)
                    { _writeReady?.TrySetResult(); _writeReady = null; }
                }
            }
            // EnumNetworkEvents atomically drains and resets the manual-reset event.
            // Rearming a one-shot managed wait avoids overlapping callbacks while it is signaled.
            if (_failure is null) ArmWait();
        }
    }
    private void Fail(Exception failure)
    {
        DiagnosticLog.Error("Windows.Bluetooth", "rfcomm.failed", "The RFCOMM channel reported a failure.", failure, new { connectionId = _diagnosticId });
        _failure ??= failure;
        _connectionReady.TrySetException(failure);
        _readReady?.TrySetException(failure); _writeReady?.TrySetException(failure);
    }
    public async Task SendAsync(ReadOnlyMemory<byte> bytes, CancellationToken token)
    {
        ThrowIfUnavailable(); await _writeGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            int offset = 0;
            while (offset < bytes.Length)
            {
                token.ThrowIfCancellationRequested();
                Task? ready;
                lock (_nativeGate)
                {
                    ThrowIfUnavailable();
                    if (_remoteClosed) throw new IOException("RFCOMM closed during send.");
                    int count = TrySend(bytes[offset..]);
                    if (count >= 0)
                    {
                        if (count == 0) throw new IOException("RFCOMM closed during send.");
                        offset += count; ready = null;
                    }
                    else ready = (_writeReady ??= NewSignal()).Task;
                }
                if (ready is not null) await ready.WaitAsync(token).ConfigureAwait(false);
            }
            token.ThrowIfCancellationRequested(); ThrowIfUnavailable();
        }
        finally { _writeGate.Release(); }
    }
    private unsafe int TrySend(ReadOnlyMemory<byte> bytes)
    {
        using var pinned = bytes.Pin();
        int count = Native.send(_socket.DangerousGetHandle(), (byte*)pinned.Pointer, Math.Min(bytes.Length, 65536), 0);
        if (count > 0) DiagnosticLog.Bytes("Windows.Bluetooth", "rfcomm.tx", bytes.Span[..count], new { connectionId = _diagnosticId, requestedLength = bytes.Length, sentLength = count });
        if (count < 0)
        {
            int error = Native.WSAGetLastError();
            if (error != Native.WouldBlock) throw Native.Error(error, "RFCOMM send failed.");
        }
        return count;
    }
    public async Task<int> ReadAsync(Memory<byte> bytes, CancellationToken token)
    {
        ThrowIfUnavailable(); await _readGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (bytes.IsEmpty) return 0;
            while (true)
            {
                token.ThrowIfCancellationRequested(); Task ready;
                lock (_nativeGate)
                {
                    ThrowIfUnavailable();
                    int count = TryRead(bytes);
                    if (count >= 0) return count;
                    if (_remoteClosed) return 0;
                    ready = (_readReady ??= NewSignal()).Task;
                }
                await ready.WaitAsync(token).ConfigureAwait(false);
            }
        }
        finally { _readGate.Release(); }
    }
    private unsafe int TryRead(Memory<byte> bytes)
    {
        using var pinned = bytes.Pin();
        int count = Native.recv(_socket.DangerousGetHandle(), (byte*)pinned.Pointer, Math.Min(bytes.Length, 65536), 0);
        if (count > 0) DiagnosticLog.Bytes("Windows.Bluetooth", "rfcomm.rx", bytes.Span[..count], new { connectionId = _diagnosticId, receivedLength = count });
        else if (count == 0) DiagnosticLog.Info("Windows.Bluetooth", "rfcomm.eof", "The native receive call returned EOF.", new { connectionId = _diagnosticId });
        if (count < 0)
        {
            int error = Native.WSAGetLastError();
            if (error != Native.WouldBlock) throw Native.Error(error, "RFCOMM receive failed.");
        }
        return count;
    }
    private void ThrowIfUnavailable()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (_failure is { } failure) throw new IOException("RFCOMM channel failed.", failure);
    }
    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return ValueTask.CompletedTask;
        DiagnosticLog.Info("Windows.Bluetooth", "rfcomm.disposing", "Closing the owned RFCOMM socket and network event.", new { connectionId = _diagnosticId, remoteClosed = _remoteClosed });
        lock (_nativeGate)
        {
            _registration?.Unregister(null);
            _connectionReady.TrySetCanceled(); _readReady?.TrySetCanceled(); _writeReady?.TrySetCanceled();
            _socket.Dispose(); _networkEvent.Dispose();
        }
        // Callers can still release their semaphores after cancellation, so do not dispose those here.
        return ValueTask.CompletedTask;
    }
}
