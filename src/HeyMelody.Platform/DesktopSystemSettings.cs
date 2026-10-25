namespace HeyMelody.Platform;

/// <summary>System settings targets; launching remains the responsibility of the desktop UI.</summary>
public static class DesktopSystemSettings
{
    public static Uri? BluetoothUri => OperatingSystem.IsWindows() ? new("ms-settings:bluetooth")
        : OperatingSystem.IsMacOS() ? new("x-apple.systempreferences:com.apple.BluetoothSettings") : null;

    public static string BluetoothHelp => OperatingSystem.IsWindows()
        ? "请在 Windows 设置中打开蓝牙与设备。"
        : OperatingSystem.IsMacOS() ? "请在 macOS 系统设置中打开蓝牙。"
        : "请在系统设置中打开蓝牙。";
}
