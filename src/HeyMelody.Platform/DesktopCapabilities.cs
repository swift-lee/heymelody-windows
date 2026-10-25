using System.Runtime.InteropServices;

namespace HeyMelody.Platform;

public static class DesktopCapabilities
{
    public static bool SupportsHeadsetControl => OperatingSystem.IsWindows()
        || OperatingSystem.IsMacOSVersionAtLeast(15) && RuntimeInformation.ProcessArchitecture == Architecture.Arm64;
}
