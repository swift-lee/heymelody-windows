using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using HeyMelody.Platform;

namespace HeyMelody.MacOS;

/// <summary>CoreLocation authorization and single position reads in the Host's native main run loop.</summary>
public sealed class MacLocationProvider : ILocationProvider
{
    private static readonly ConcurrentDictionary<nint, MacLocationProvider> Instances = new();
    private static readonly Native.Callback Callback = Receive;
    private static long _nextContext, _nextOperation;
    private readonly nint _context = (nint)Interlocked.Increment(ref _nextContext);
    private readonly ConcurrentDictionary<long, TaskCompletionSource<NativeResult>> _operations = new();
    private readonly object _gate = new();
    private LocationPermission? _lastPermission;
    private bool _started, _disposed;

    public event Action<LocationPermission>? PermissionChanged;

    public async Task<LocationPermission> RequestPermissionAsync(CancellationToken token)
    {
        try
        {
            var result = await ExecuteAsync(Native.RequestPermission, token).ConfigureAwait(false);
            return Publish(ToPermission(result.Access));
        }
        catch (OperationCanceledException) { throw; }
        catch (ObjectDisposedException) { throw; }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException or PlatformNotSupportedException)
        {
            return Publish(new(LocationAccess.Unavailable, "当前安装缺少可用的 macOS 定位组件。"));
        }
    }

    public async Task<LocationPermission> CheckPermissionAsync(CancellationToken token)
    {
        try
        {
            var result = await ExecuteAsync(Native.CheckPermission, token).ConfigureAwait(false);
            return Publish(ToPermission(result.Access));
        }
        catch (OperationCanceledException) { throw; }
        catch (ObjectDisposedException) { throw; }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException or PlatformNotSupportedException)
        {
            return Publish(new(LocationAccess.Unavailable, "当前安装缺少可用的 macOS 定位组件。"));
        }
    }

    public async Task<LocationFix> GetLocationAsync(CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        NativeResult result;
        try { result = await ExecuteAsync(Native.GetLocation, timeout.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            lock (_gate) ObjectDisposedException.ThrowIf(_disposed, this);
            throw new TimeoutException("系统定位超时，未取得本次断开的位置。");
        }
        token.ThrowIfCancellationRequested();
        lock (_gate) ObjectDisposedException.ThrowIf(_disposed, this);
        if (result.Kind != 1)
        {
            var permission = ToPermission(result.Access);
            if (permission.Access is LocationAccess.Denied or LocationAccess.SystemDisabled or LocationAccess.Restricted)
                throw new UnauthorizedAccessException(permission.Message);
            if (result.Error == 1)
                throw new TimeoutException("系统定位超时，未取得本次断开的位置。");
            throw new InvalidOperationException(result.Error == 2
                ? "系统暂时没有可用的位置，未记录本次断开。" : "无法取得系统位置，未记录本次断开。");
        }
        if (!double.IsFinite(result.Latitude) || result.Latitude is < -90 or > 90
            || !double.IsFinite(result.Longitude) || result.Longitude is < -180 or > 180
            || !double.IsFinite(result.Accuracy) || result.Accuracy < 0 || !double.IsFinite(result.Timestamp))
            throw new InvalidOperationException("系统返回的位置数据无效。");
        DateTimeOffset timestamp;
        try { timestamp = DateTimeOffset.FromUnixTimeMilliseconds(checked((long)(result.Timestamp * 1000))); }
        catch (Exception error) when (error is ArgumentOutOfRangeException or OverflowException)
        {
            throw new InvalidOperationException("系统返回的位置时间无效。");
        }
        return new(result.Latitude, result.Longitude, result.Accuracy, timestamp);
    }

    private async Task<NativeResult> ExecuteAsync(Action<nint, long> start, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        long operation;
        TaskCompletionSource<NativeResult> completion;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_started)
            {
                if (!OperatingSystem.IsMacOSVersionAtLeast(15) || RuntimeInformation.ProcessArchitecture != Architecture.Arm64)
                    throw new PlatformNotSupportedException();
                Instances[_context] = this;
                try { Native.Start(Callback, _context); _started = true; }
                catch { Instances.TryRemove(_context, out _); throw; }
            }
            operation = Interlocked.Increment(ref _nextOperation);
            completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _operations[operation] = completion;
            try { start(_context, operation); }
            catch { _operations.TryRemove(operation, out _); throw; }
        }
        try { return await completion.Task.WaitAsync(token).ConfigureAwait(false); }
        finally
        {
            _operations.TryRemove(operation, out _);
            // All native work is marshalled to the main queue, including cancellation and delegate cleanup.
            if (!completion.Task.IsCompleted) Native.Cancel(_context, operation);
        }
    }

    private static void Receive(nint context, long operation, int kind, int access,
        double latitude, double longitude, double accuracy, double timestamp, int error)
    {
        try
        {
            // Contexts are monotonically increasing IDs, never pointers to freed managed objects.
            if (!Instances.TryGetValue(context, out var provider)) return;
            var result = new NativeResult(kind, access, latitude, longitude, accuracy, timestamp, error);
            if (operation == 0) provider.Publish(ToPermission(access));
            else if (provider._operations.TryRemove(operation, out var completion)) completion.TrySetResult(result);
        }
        catch { /* No managed exception may unwind into a CoreLocation delegate. */ }
    }

    private static LocationPermission ToPermission(int access) => (LocationAccess)access switch
    {
        LocationAccess.Allowed => new(LocationAccess.Allowed, "已允许 HeyMelody 后台使用系统定位。"),
        LocationAccess.Denied => new(LocationAccess.Denied, "定位权限已被拒绝，请检查系统隐私与安全性中的定位服务。"),
        LocationAccess.SystemDisabled => new(LocationAccess.SystemDisabled, "系统定位服务已关闭。"),
        LocationAccess.Restricted => new(LocationAccess.Restricted, "定位受到系统策略限制。"),
        LocationAccess.Unavailable => new(LocationAccess.Unavailable, "当前安装无法使用 macOS 定位服务。"),
        LocationAccess.Failed => new(LocationAccess.Failed, "无法检查系统定位权限，请稍后重试。"),
        _ => new(LocationAccess.Unknown, "尚未授予定位权限，请打开全局开关以申请。")
    };

    private LocationPermission Publish(LocationPermission permission)
    {
        Action<LocationPermission>? listeners;
        lock (_gate)
        {
            if (_disposed || _lastPermission == permission) return permission;
            _lastPermission = permission;
            listeners = PermissionChanged;
        }
        if (listeners is not null)
            foreach (Action<LocationPermission> listener in listeners.GetInvocationList())
                try { listener(permission); }
                catch { /* A subscriber cannot interrupt native permission delivery. */ }
        return permission;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            Instances.TryRemove(_context, out _);
            foreach (var entry in _operations)
                if (_operations.TryRemove(entry.Key, out var completion)) completion.TrySetCanceled();
            if (_started) Native.Shutdown(_context);
        }
    }

    private sealed record NativeResult(int Kind, int Access, double Latitude, double Longitude,
        double Accuracy, double Timestamp, int Error);

    private static class Native
    {
        private const string Library = "HeyMelodyMac";
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        internal delegate void Callback(nint context, long operation, int kind, int access,
            double latitude, double longitude, double accuracy, double timestamp, int error);
        [DllImport(Library, EntryPoint = "hm_location_start", CallingConvention = CallingConvention.Cdecl)]
        internal static extern void Start(Callback callback, nint context);
        [DllImport(Library, EntryPoint = "hm_location_request_permission", CallingConvention = CallingConvention.Cdecl)]
        internal static extern void RequestPermission(nint context, long operation);
        [DllImport(Library, EntryPoint = "hm_location_check_permission", CallingConvention = CallingConvention.Cdecl)]
        internal static extern void CheckPermission(nint context, long operation);
        [DllImport(Library, EntryPoint = "hm_location_get", CallingConvention = CallingConvention.Cdecl)]
        internal static extern void GetLocation(nint context, long operation);
        [DllImport(Library, EntryPoint = "hm_location_cancel", CallingConvention = CallingConvention.Cdecl)]
        internal static extern void Cancel(nint context, long operation);
        [DllImport(Library, EntryPoint = "hm_location_shutdown", CallingConvention = CallingConvention.Cdecl)]
        internal static extern void Shutdown(nint context);
    }
}
