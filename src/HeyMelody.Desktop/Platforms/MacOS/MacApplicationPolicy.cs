using System.Runtime.InteropServices;
using Avalonia.Threading;
using HeyMelody.Diagnostics;

namespace HeyMelodyWindows.Ui;

internal enum MacActivationPolicy { Regular = 0, Accessory = 1 }

/// <summary>Process-wide macOS activation policy; Regular owns a Dock tile, Accessory is menu-bar only.</summary>
internal static class MacApplicationPolicy
{
    private const string ObjC = "/usr/lib/libobjc.A.dylib";
    [DllImport(ObjC)] private static extern IntPtr objc_getClass(string name);
    [DllImport(ObjC)] private static extern IntPtr sel_registerName(string name);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr Send(IntPtr receiver, IntPtr selector);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool SendPolicy(IntPtr receiver, IntPtr selector, long policy);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern long ReadPolicy(IntPtr receiver, IntPtr selector);

    internal static void Set(MacActivationPolicy policy)
    {
        if (!OperatingSystem.IsMacOS()) return;
        if (!Dispatcher.UIThread.CheckAccess()) { Dispatcher.UIThread.Post(() => Set(policy)); return; }
        try
        {
            var application = Send(objc_getClass("NSApplication"), sel_registerName("sharedApplication"));
            if (application == IntPtr.Zero) { Log("missingApplication", policy, null); return; }
            SendPolicy(application, sel_registerName("setActivationPolicy:"), (long)policy);
        }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException)
        {
            Log("setFailed", policy, error);
        }
    }

    /// <summary>UI-thread only; used by native window tests and close-path diagnostics.</summary>
    internal static MacActivationPolicy? Get()
    {
        if (!OperatingSystem.IsMacOS()) return null;
        if (!Dispatcher.UIThread.CheckAccess())
            throw new InvalidOperationException("The macOS activation policy must be read on the UI thread.");
        try
        {
            var application = Send(objc_getClass("NSApplication"), sel_registerName("sharedApplication"));
            if (application == IntPtr.Zero) return null;
            long value = ReadPolicy(application, sel_registerName("activationPolicy"));
            return value is 0 or 1 ? (MacActivationPolicy)value : null;
        }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException)
        {
            Log("getFailed", null, error);
            return null;
        }
    }

    private static void Log(string stage, MacActivationPolicy? policy, Exception? error) =>
        DiagnosticLog.Warning("ui.lifetime", "macActivationPolicyFailed", "The macOS activation policy was unavailable",
            error, new { stage, policy = policy?.ToString() });
}
