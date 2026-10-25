using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace HeyMelody.Windows;

/// <summary>Windows session isolation supplements NamedPipeStream's current-user check.</summary>
public static class WindowsPipeSecurity
{
    public static bool IsCurrentSession(SafePipeHandle pipe)
    {
        if (!OperatingSystem.IsWindows()) return true;
        try
        {
            if (!GetNamedPipeClientProcessId(pipe, out uint processId)) return false;
            using var client = Process.GetProcessById(checked((int)processId));
            using var host = Process.GetCurrentProcess();
            return client.SessionId == host.SessionId;
        }
        catch { return false; }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint clientProcessId);
}
