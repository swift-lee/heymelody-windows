using HeyMelody.Platform;
using Windows.Devices.Geolocation;
using Windows.Foundation;
using WinRT;

namespace HeyMelodyWindows.Windows;

/// <summary>Owns exactly one location request. No position or status event subscription is installed.</summary>
internal sealed class WindowsLocationRequest : IWindowsLocationRequest
{
    private readonly object _gate = new();
    private readonly TimeSpan _timeout;
    private Geolocator? _locator;
    private IAsyncOperation<Geoposition>? _operation;
    private Task<Geoposition>? _nativeTask;
    private bool _disposed;

    internal WindowsLocationRequest(TimeSpan timeout)
    {
        _timeout = timeout;
        _locator = new Geolocator();
        try { _locator.DesiredAccuracy = PositionAccuracy.Default; }
        catch { ReleaseNative(_locator); _locator = null; throw; }
    }

    public async Task<LocationFix> GetLocationAsync(CancellationToken token)
    {
        Task<Geoposition> task;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            token.ThrowIfCancellationRequested();
            if (_operation is not null) throw new InvalidOperationException("A location request can only run once.");
            _operation = _locator!.GetGeopositionAsync(TimeSpan.FromSeconds(60), _timeout);
            task = _nativeTask = _operation.AsTask();
        }
        using var registration = token.Register(Cancel);
        var position = await task.WaitAsync(token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        var coordinate = position.Coordinate;
        var value = coordinate.Point.Position;
        // Result objects contain only sampled data. The OS may reuse their COM identity across concurrent
        // reads, so do not forcibly dispose their shared projection wrappers; none owns the active locator.
        return new(value.Latitude, value.Longitude, coordinate.Accuracy, coordinate.Timestamp);
    }

    private void Cancel()
    {
        IAsyncInfo? operation;
        lock (_gate) operation = _operation;
        TryCancel(operation);
    }

    public void Dispose()
    {
        Geolocator? locator;
        IAsyncOperation<Geoposition>? operation;
        Task<Geoposition>? task;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            locator = _locator;
            operation = _operation;
            task = _nativeTask;
            _locator = null;
            _operation = null;
            _nativeTask = null;
        }
        TryCancel(operation);
        // Geolocator does not implement IClosable. Release this privately owned CsWinRT COM reference
        // explicitly instead of waiting for GC, which can otherwise leave the OS privacy indicator active.
        ReleaseNative(locator);
        if (operation is not null && task is not null) _ = CloseCompletedOperationAsync(operation, task);
    }

    internal static async Task<GeolocationAccessStatus> RequestPermissionAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var operation = Geolocator.RequestAccessAsync();
        var task = operation.AsTask();
        using var registration = token.Register(() => TryCancel(operation));
        try { return await task.WaitAsync(token); }
        finally
        {
            TryCancel(operation);
            _ = CloseCompletedOperationAsync(operation, task);
        }
    }

    private static void TryCancel(IAsyncInfo? operation)
    {
        if (operation is null) return;
        try { if (operation.Status == AsyncStatus.Started) operation.Cancel(); }
        catch { /* Cancellation and disposal can race native completion. */ }
    }

    private static async Task CloseCompletedOperationAsync(IAsyncInfo operation, Task completion)
    {
        try { await completion.ConfigureAwait(false); }
        catch { /* The caller observes failures; this path owns only native resource cleanup. */ }
        try { operation.Close(); }
        catch { /* A completed operation may already have closed during cancellation. */ }
        finally { ReleaseNative(operation); }
    }

    internal static void ReleaseNative(object? value)
    {
        if (value is not IWinRTObject projected) return;
        // Only call this for privately created locators, operations or diagnostic capability objects.
        // The locator uses only its default IGeolocator interface. Other projection internals are not public;
        // IAsyncInfo.Close, rather than private cache manipulation, releases an operation's active resources.
        projected.NativeObject.Dispose();
    }
}
