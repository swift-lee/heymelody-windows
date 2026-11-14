using System.Runtime.InteropServices;
using Avalonia.Threading;
using HeyMelody.Platform;

namespace HeyMelodyWindows.Host;

internal static class HostPlatformFactory
{
    internal static bool IsSupported => OperatingSystem.IsWindows() ||
        (OperatingSystem.IsMacOSVersionAtLeast(15) && RuntimeInformation.ProcessArchitecture == Architecture.Arm64);
    internal static IBluetoothPlatform CreateBluetooth()
    {
#if WINDOWS
        return new HeyMelodyWindows.Windows.WindowsBluetoothPlatform();
#elif HEYMELODY_MACOS
        if (OperatingSystem.IsMacOS() && RuntimeInformation.ProcessArchitecture == Architecture.Arm64)
            return new HeyMelody.MacOS.MacBluetoothPlatform();
        return new UnavailableBluetoothPlatform();
#else
        return new UnavailableBluetoothPlatform();
#endif
    }
    internal static ILocationProvider CreateLocation()
    {
#if WINDOWS
        return new UiThreadLocationProvider(new HeyMelodyWindows.Windows.WindowsLocationProvider());
#elif HEYMELODY_MACOS
        if (OperatingSystem.IsMacOS() && RuntimeInformation.ProcessArchitecture == Architecture.Arm64)
            return new UiThreadLocationProvider(new HeyMelody.MacOS.MacLocationProvider());
        return UnavailableLocation();
#else
        return UnavailableLocation();
#endif
    }
    internal static ILocationProvider UnavailableLocation() => new UnavailableLocationProvider();
    private sealed class UiThreadLocationProvider(ILocationProvider provider) : ILocationProvider
    {
        public event Action<LocationPermission>? PermissionChanged
        { add => provider.PermissionChanged += value; remove => provider.PermissionChanged -= value; }
        public async Task<LocationPermission> RequestPermissionAsync(CancellationToken token)
        {
            if (Dispatcher.UIThread.CheckAccess()) return await provider.RequestPermissionAsync(token);
            return await Dispatcher.UIThread.InvokeAsync(() =>
            {
                token.ThrowIfCancellationRequested();
                return provider.RequestPermissionAsync(token);
            });
        }
        public Task<LocationPermission> CheckPermissionAsync(CancellationToken token) => provider.CheckPermissionAsync(token);
        public Task<LocationFix> GetLocationAsync(CancellationToken token) => provider.GetLocationAsync(token);
        public void Dispose() => provider.Dispose();
    }
    private sealed class UnavailableLocationProvider : ILocationProvider
    {
        private static readonly LocationPermission Permission = new(LocationAccess.Unavailable, "此平台尚未实现系统定位。");
        public event Action<LocationPermission>? PermissionChanged { add { } remove { } }
        public Task<LocationPermission> RequestPermissionAsync(CancellationToken token) => Task.FromResult(Permission);
        public Task<LocationPermission> CheckPermissionAsync(CancellationToken token) => Task.FromResult(Permission);
        public Task<LocationFix> GetLocationAsync(CancellationToken token) =>
            Task.FromException<LocationFix>(new PlatformNotSupportedException(Permission.Message));
        public void Dispose() { }
    }
    internal static int RunWorker(Func<Task<int>> worker)
    {
#if HEYMELODY_MACOS
        if (OperatingSystem.IsMacOS()) return HeyMelody.MacOS.MacBluetoothPlatform.RunWorker(worker);
#endif
        return worker().GetAwaiter().GetResult();
    }
    internal static ICalibrationAuditionPlayer CreateAuditionPlayer()
    {
#if WINDOWS
        return new WasapiCalibrationAuditionPlayer();
#elif HEYMELODY_MACOS
        return new MacCalibrationAuditionPlayer();
#else
        throw new PlatformNotSupportedException("此平台尚未实现校准预览音频。");
#endif
    }
    private sealed class UnavailableBluetoothPlatform : IBluetoothPlatform
    {
        public BluetoothPlatformStatus Status => new(BluetoothAccess.Unsupported, false, "此平台尚未实现蓝牙后台。");
        public event Action<BluetoothPlatformEvent>? EventReceived { add { } remove { } }
        public void Dispose() { }
        public void SetScanDemand(bool wanted, long requestGeneration = 0) { }
        public Task StartAsync(CancellationToken token) => Task.CompletedTask;
        public Task<IReadOnlyList<BluetoothCandidate>> EnumeratePairedAsync(CancellationToken token) => throw new PlatformNotSupportedException(Status.Message);
        public Task<Stream> OpenControlChannelAsync(ulong address, IReadOnlyList<Guid> serviceIds, CancellationToken token) => throw new PlatformNotSupportedException(Status.Message);
    }
}
