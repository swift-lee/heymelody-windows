using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using HeyMelody.Platform;

namespace HeyMelody.MacOS;

public sealed class MacBluetoothPlatform : IBluetoothPlatform
{
    private readonly ConcurrentDictionary<long, TaskCompletionSource<object>> _operations = new();
    private readonly ConcurrentDictionary<long, MacRfcommStream> _channels = new();
    private readonly NativeBluetooth.Callback _callback;
    private readonly object _callbackGate = new();
    private readonly Channel<BluetoothPlatformEvent> _events = Channel.CreateUnbounded<BluetoothPlatformEvent>(new UnboundedChannelOptions
    { SingleReader = true, SingleWriter = true });
    private static long _nextOperation;
    private GCHandle _self;
    private int _started, _disposed;
    public BluetoothPlatformStatus Status { get; private set; } = new(BluetoothAccess.Unknown, null);
    public event Action<BluetoothPlatformEvent>? EventReceived;

    public MacBluetoothPlatform()
    {
        _callback = Receive;
        _ = Task.Run(async () =>
        {
            await foreach (var observation in _events.Reader.ReadAllAsync())
                if (_disposed == 0)
                    try { EventReceived?.Invoke(observation); }
                    catch { /* A Host observer must not stop subsequent native observations. */ }
        });
    }

    public Task StartAsync(CancellationToken token)
    {
        lock (_callbackGate)
        {
            token.ThrowIfCancellationRequested();
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            if (!OperatingSystem.IsMacOS() || RuntimeInformation.ProcessArchitecture != Architecture.Arm64)
                throw new PlatformNotSupportedException("macOS 后台仅支持 Apple Silicon。");
            if (Interlocked.Exchange(ref _started, 1) == 0)
            {
                _self = GCHandle.Alloc(this);
                try { NativeBluetooth.hm_bt_start(_callback, GCHandle.ToIntPtr(_self)); }
                catch { _self.Free(); _started = 0; throw; }
            }
        }
        return Task.CompletedTask;
    }

    /// <summary>Workers have no Avalonia lifetime. Their main thread owns the native run loop.</summary>
    public static int RunWorker(Func<Task<int>> worker)
    {
        int result = 1;
        var task = Task.Run(async () =>
        {
            try { result = await worker(); }
            finally { NativeBluetooth.hm_bt_stop_loop(); }
        });
        NativeBluetooth.hm_bt_run_loop();
        task.GetAwaiter().GetResult();
        return result;
    }
    public void SetScanDemand(bool wanted, long requestGeneration = 0)
    { if (_started != 0 && _disposed == 0) NativeBluetooth.hm_bt_scan(wanted ? 1 : 0, requestGeneration); }

    public async Task<IReadOnlyList<BluetoothCandidate>> EnumeratePairedAsync(CancellationToken token)
    {
        await StartAsync(token);
        return (IReadOnlyList<BluetoothCandidate>)await ExecuteAsync(NativeBluetooth.hm_bt_enumerate, token);
    }
    public async Task<Stream> OpenControlChannelAsync(ulong address, IReadOnlyList<Guid> serviceIds, CancellationToken token)
    {
        if (address is 0 or > 0xFFFFFFFFFFFFUL) throw new ArgumentOutOfRangeException(nameof(address));
        if (serviceIds.Count == 0 || serviceIds.Any(x => x == Guid.Empty)) throw new ArgumentException("No valid control UUIDs.", nameof(serviceIds));
        await StartAsync(token);
        var ids = JsonSerializer.Serialize(serviceIds.Distinct());
        return (Stream)await ExecuteAsync(id => NativeBluetooth.hm_bt_open(id, address, ids), token);
    }
    internal async Task WriteAsync(long channel, ReadOnlyMemory<byte> bytes, CancellationToken token)
    {
        // Native code copies before returning; the managed buffer is never retained by native callbacks.
        var copy = bytes.ToArray();
        await ExecuteAsync(id => NativeBluetooth.hm_bt_write(channel, id, copy, (nuint)copy.Length), token);
    }
    internal void Close(long channel)
    {
        _channels.TryRemove(channel, out _);
        if (_disposed == 0) NativeBluetooth.hm_bt_close(channel);
    }
    private async Task<object> ExecuteAsync(Action<long> submit, CancellationToken token)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        token.ThrowIfCancellationRequested();
        var id = Interlocked.Increment(ref _nextOperation);
        var source = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            lock (_callbackGate)
            {
                ObjectDisposedException.ThrowIf(_disposed != 0, this);
                if (!_operations.TryAdd(id, source)) throw new InvalidOperationException();
                submit(id);
            }
            using var cancellation = token.Register(() =>
            {
                if (_operations.TryRemove(id, out var pending))
                { NativeBluetooth.hm_bt_cancel(id); pending.TrySetCanceled(token); }
            });
            return await source.Task;
        }
        finally { _operations.TryRemove(id, out _); }
    }
    private void Receive(nint context, int kind, long operation, long channel, int status, nint data, nuint length)
    {
        lock (_callbackGate) ReceiveCore(kind, operation, channel, status, data, length);
    }
    private void ReceiveCore(int kind, long operation, long channel, int status, nint data, nuint length)
    {
        if (_disposed != 0) return;
        TaskCompletionSource<object>? completing = null;
        try
        {
            if (length > 1024 * 1024) throw new InvalidDataException("Native Bluetooth payload exceeded its limit.");
            byte[] bytes = new byte[(int)length];
            if (length != 0) Marshal.Copy(data, bytes, 0, bytes.Length);
            if (kind == 3)
            { if (_channels.TryGetValue(channel, out var stream)) stream.Receive(bytes); return; }
            if (kind == 4)
            {
                if (_channels.TryRemove(channel, out var stream)) stream.Complete(status == 0 ? null : Failure(status));
                return;
            }
            if (kind == 0)
            {
                using var json = JsonDocument.Parse(bytes);
                var root = json.RootElement;
                var access = (BluetoothAccess)root.GetProperty("access").GetInt32();
                bool? radio = root.GetProperty("radioAvailable").ValueKind == JsonValueKind.Null ? null : root.GetProperty("radioAvailable").GetBoolean();
                Status = new(access, radio, AccessMessage(access));
                Publish(new BluetoothStatusChanged(Status)); return;
            }
            if (kind == 6) { Publish(new BluetoothCatalogChanged()); return; }
            if (kind == 7)
            {
                using var json = JsonDocument.Parse(bytes); var root = json.RootElement;
                Publish(new BluetoothAdvertisementReceived(root.GetProperty("companyId").GetUInt16(),
                    Convert.FromBase64String(root.GetProperty("data").GetString()!), 0, false,
                    DateTimeOffset.FromUnixTimeMilliseconds(root.GetProperty("timestamp").GetInt64()), root.GetProperty("rssi").GetInt16())
                    { Generation = root.GetProperty("generation").GetInt64(), RequestGeneration = root.GetProperty("requestGeneration").GetInt64() });
                return;
            }
            if (kind == 10)
            {
                using var json = JsonDocument.Parse(bytes); var root = json.RootElement;
                Publish(new BluetoothScanChanged(status != 0, root.GetProperty("generation").GetInt64(),
                    root.GetProperty("requestGeneration").GetInt64(), DateTimeOffset.FromUnixTimeMilliseconds(root.GetProperty("startedAt").GetInt64())));
                return;
            }
            if (kind == 8) { Publish(new BluetoothSuspensionChanged(status != 0)); return; }
            if (kind == 9) { Publish(new BluetoothConnectionChanged(unchecked((ulong)channel), status != 0)); return; }
            if (!_operations.TryRemove(operation, out var completion))
            { if (kind == 2 && status == 0) NativeBluetooth.hm_bt_close(channel); return; }
            completing = completion;
            if (status != 0) { completion.TrySetException(Failure(status)); return; }
            if (kind == 1)
            {
                var candidates = JsonSerializer.Deserialize<List<BluetoothCandidate>>(bytes, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? [];
                if (candidates.Count > 4096) throw new InvalidDataException("Bluetooth catalog exceeded its limit.");
                completion.TrySetResult(candidates);
            }
            else if (kind == 2)
            {
                var stream = new MacRfcommStream((bytes, token) => WriteAsync(channel, bytes, token), () => Close(channel));
                if (!_channels.TryAdd(channel, stream)) throw new InvalidDataException("Duplicate native channel.");
                completion.TrySetResult(stream);
            }
            else completion.TrySetResult(true);
        }
        catch (Exception failure)
        {
            // Exceptions must never unwind through the Objective-C delegate boundary.
            completing?.TrySetException(failure);
            if (_operations.TryRemove(operation, out var pending)) pending.TrySetException(failure);
        }
    }
    private void Publish(BluetoothPlatformEvent observation)
    {
        // Host handlers may perform IPC and take locks. Keep them out of native delegate stacks.
        _events.Writer.TryWrite(observation);
    }
    private static string? AccessMessage(BluetoothAccess access) => access switch
    {
        BluetoothAccess.NotDetermined => "请允许 HeyMelody 使用蓝牙。",
        BluetoothAccess.Denied => "蓝牙权限已被拒绝，请在系统设置的隐私与安全性中允许 HeyMelody 使用蓝牙。",
        BluetoothAccess.Restricted => "系统策略限制了蓝牙访问。",
        BluetoothAccess.Unsupported => "此 Mac 的蓝牙适配器不可用。",
        _ => null
    };
    private static Exception Failure(int status) => status switch
    {
        -2 => new BluetoothDiscoveryException(BluetoothDiscoveryFailure.ServiceNotFound, "耳机未公布已配置的控制服务。"),
        -3 => new BluetoothDiscoveryException(BluetoothDiscoveryFailure.AmbiguousServices, "耳机公布了多个控制服务，无法安全选择。"),
        -4 => new BluetoothAccessException(BluetoothAccess.Denied, AccessMessage(BluetoothAccess.Denied)!),
        -5 => new BluetoothAccessException(BluetoothAccess.Restricted, AccessMessage(BluetoothAccess.Restricted)!),
        -6 => new BluetoothAccessException(BluetoothAccess.NotDetermined, AccessMessage(BluetoothAccess.NotDetermined)!),
        -7 => new BluetoothAccessException(BluetoothAccess.Unsupported, AccessMessage(BluetoothAccess.Unsupported)!),
        -8 => new IOException("蓝牙已关闭，请在系统设置中开启蓝牙。"),
        _ => new IOException($"macOS 蓝牙操作失败（{status}）。")
    };
    public void Dispose()
    {
        lock (_callbackGate)
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            foreach (var stream in _channels.Values) stream.Complete(new ObjectDisposedException(nameof(MacBluetoothPlatform)));
            _channels.Clear();
            foreach (var pending in _operations.Values) pending.TrySetException(new ObjectDisposedException(nameof(MacBluetoothPlatform)));
            _operations.Clear();
            _events.Writer.TryComplete();
        }
        // Native shutdown clears callbacks synchronously on its run loop before the handle can be released.
        if (_started != 0)
        { NativeBluetooth.hm_bt_shutdown(GCHandle.ToIntPtr(_self)); _self.Free(); }
        GC.KeepAlive(_callback);
    }
}
