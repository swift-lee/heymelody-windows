using System.Runtime.InteropServices;
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
