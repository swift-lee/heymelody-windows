using System.Runtime.InteropServices;

namespace HeyMelody.MacOS;

internal static class NativeBluetooth
{
    internal const string Library = "HeyMelodyMac";
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate void Callback(nint context, int kind, long operation, long channel, int status, nint data, nuint length);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern void hm_bt_start(Callback callback, nint context);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern void hm_bt_shutdown(nint context);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern void hm_bt_enumerate(long operation);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern void hm_bt_open(long operation, ulong address, [MarshalAs(UnmanagedType.LPUTF8Str)] string serviceIds);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern void hm_bt_write(long channel, long operation, byte[] data, nuint length);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern void hm_bt_close(long channel);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern void hm_bt_cancel(long operation);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern void hm_bt_scan(int wanted, long requestGeneration);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern void hm_bt_run_loop();
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern void hm_bt_stop_loop();
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] internal static extern int hm_bt_bridge_version();
}
