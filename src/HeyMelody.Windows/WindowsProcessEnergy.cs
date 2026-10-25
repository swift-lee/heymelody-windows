using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace HeyMelodyWindows.Windows;

// POWER_THROTTLING_STATE of processthreadsapi.h: version, then the bits the caller controls, then their requested values.
[StructLayout(LayoutKind.Sequential)]
internal struct PowerThrottlingState { public uint Version, ControlMask, StateMask; }

/// <summary>EcoQoS (execution-speed throttling) plus idle priority for Eco, and the opposite for Default.</summary>
public sealed class WindowsProcessEnergy
{
    private const int ProcessPowerThrottling = 4, ThreadPowerThrottling = 3;
    private const uint CurrentVersion = 1, ExecutionSpeed = 0x1, IgnoreTimerResolution = 0x4;
    private static int _threadFailureTraced;

    public void Apply(Process process, bool eco, Action<string, int> failed)
    {
        try
        {
            // SafeHandle, not the raw value: the marshaller keeps the handle alive for the call even if the owner disposes the Process meanwhile.
            var handle = process.SafeHandle;
            if (eco)
            {
                // Ignoring timer-resolution requests exists from Windows 11; the older mask would be rejected.
                uint mask = ExecutionSpeed | (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000) ? IgnoreTimerResolution : 0);
                int error = SetThrottling(handle, mask);
                if (error != 0 && mask != ExecutionSpeed)
                { failed("timerResolution", error); error = SetThrottling(handle, ExecutionSpeed); }
                if (error != 0) failed("powerThrottling", error);
                SetPriority(process, ProcessPriorityClass.Idle, failed);
            }
            else
            {
                // Priority first: the class is what a child started right now would inherit.
                SetPriority(process, ProcessPriorityClass.Normal, failed);
                int error = SetThrottling(handle, 0);
                if (error != 0) failed("powerThrottling", error);
            }
        }
        catch (Exception error) when (error is EntryPointNotFoundException or DllNotFoundException) { failed("unsupported", 0); }
        catch (Exception error) when (error is InvalidOperationException or Win32Exception or NotSupportedException)
        { failed("process", error is Win32Exception win32 ? win32.NativeErrorCode : 0); }
    }

    private static int SetThrottling(SafeProcessHandle process, uint mask)
    {
        var state = new PowerThrottlingState { Version = CurrentVersion, ControlMask = mask, StateMask = mask };
        return SetProcessInformation(process, ProcessPowerThrottling, ref state, (uint)Marshal.SizeOf<PowerThrottlingState>())
            ? 0 : Marshal.GetLastPInvokeError();
    }

    private static void SetPriority(Process process, ProcessPriorityClass priority, Action<string, int> failed)
    {
        try { process.PriorityClass = priority; }
        catch (Win32Exception error) { failed("priorityClass", error.NativeErrorCode); }
        catch (InvalidOperationException) { failed("priorityClass", 0); }
    }

    /// <summary>Opts the calling thread out of EcoQoS: audio pumping must not be slowed while the process is in Eco.</summary>
    public static bool TryExemptCurrentThread()
    {
        try
        {
            var state = new PowerThrottlingState { Version = CurrentVersion, ControlMask = ExecutionSpeed, StateMask = 0 };
            if (SetThreadInformation(GetCurrentThread(), ThreadPowerThrottling, ref state, (uint)Marshal.SizeOf<PowerThrottlingState>())) return true;
            if (Interlocked.Exchange(ref _threadFailureTraced, 1) == 0)
                Trace.WriteLine($"HeyMelody energy: thread exemption failed (Win32 error {Marshal.GetLastPInvokeError()}).");
            return false;
        }
        catch (Exception error) when (error is EntryPointNotFoundException or DllNotFoundException) { return false; }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetProcessInformation(SafeProcessHandle process, int informationClass, ref PowerThrottlingState information, uint size);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetThreadInformation(nint thread, int informationClass, ref PowerThrottlingState information, uint size);
    [DllImport("kernel32.dll")] private static extern nint GetCurrentThread();
}
