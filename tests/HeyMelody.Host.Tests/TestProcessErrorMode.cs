using System.Runtime.InteropServices;

internal static class TestProcessErrorMode
{
    // Tests and their fake workers must report unhandled failures to the runner, never block on WER UI.
    internal static void SuppressWindowsErrorDialogs()
    {
        if (OperatingSystem.IsWindows()) SetErrorMode(GetErrorMode() | 0x0003);
    }

    [DllImport("kernel32.dll")]
    private static extern uint GetErrorMode();
    [DllImport("kernel32.dll")]
    private static extern uint SetErrorMode(uint mode);
}
