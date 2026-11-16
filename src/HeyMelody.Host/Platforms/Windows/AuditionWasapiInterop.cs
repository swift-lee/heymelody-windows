#if HEYMELODY_WINDOWS
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using System.Text;

namespace HeyMelodyWindows.Host;

// Self-contained adaptation of HeyMelody.Audio/WasapiInterop.cs; the calibration Host does not depend on its external driver-manager project.

// ABI declarations checked against Windows SDK 10.0.26100.0 headers:
// mmdeviceapi.h, Audioclient.h, devicetopology.h, propsys.h, functiondiscoverykeys_devpkey.h.
internal static partial class WasapiNative
{
    internal static readonly Guid AudioClientId = new("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2");
    internal static readonly Guid RenderClientId = new("F294ACFC-3146-4483-A7BF-ADDCA7C260E2");
    internal static readonly Guid TopologyId = new("2A07407E-6497-4A18-9787-32F79BD0D98F");
    internal static readonly PropertyKey DeviceInstanceId = new(new("78c34fc8-104a-4aca-9ea4-524d52996e57"), 256);
    internal static readonly Guid PcmSubFormat = new("00000001-0000-0010-8000-00aa00389b71");
    internal const uint Silent = 2;

    internal static void Check(int hr) { if (hr < 0) Marshal.ThrowExceptionForHR(hr); }
    // Every returned interface uses a unique wrapper: release stays on the owning STA and is deterministic.
    internal static void Release(object? value) { if (value is ComObject wrapper) wrapper.FinalRelease(); }

    internal static IDeviceEnumerator CreateDeviceEnumerator()
    {
        var clsid = new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E");
        var iid = new Guid("A95664D2-9614-4F35-A746-DE8DB63617E6");
        Check(CoCreateInstance(clsid, 0, 1 /* CLSCTX_INPROC_SERVER */, iid, out var enumerator));
        return enumerator;
    }

    [LibraryImport("ole32.dll")]
    private static partial int CoCreateInstance(in Guid clsid, nint outer, uint context, in Guid iid,
        [MarshalUsing(typeof(UniqueComInterfaceMarshaller<IDeviceEnumerator>))] out IDeviceEnumerator enumerator);

    [DllImport("ole32.dll")] internal static extern int CoInitializeEx(IntPtr reserved, uint flags);
    [DllImport("ole32.dll")] internal static extern void CoUninitialize();
    [DllImport("ole32.dll")] internal static extern int PropVariantClear(ref PropVariant value);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    internal static extern uint CM_Locate_DevNodeW(out uint node, string id, uint flags);
    [DllImport("cfgmgr32.dll")] internal static extern uint CM_Get_Parent(out uint parent, uint child, uint flags);
    [DllImport("cfgmgr32.dll")] internal static extern uint CM_Get_Device_ID_Size(out uint length, uint node, uint flags);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    internal static extern uint CM_Get_Device_IDW(uint node, StringBuilder buffer, uint length, uint flags);

    [StructLayout(LayoutKind.Sequential)] internal readonly struct PropertyKey(Guid format, uint id)
    { internal readonly Guid Format = format; internal readonly uint Id = id; }
    // PROPVARIANT's union is 16 bytes on 64-bit Windows. Oversizing to 24 bytes is also safe on x86.
    [StructLayout(LayoutKind.Explicit, Size = 24)] internal struct PropVariant
    { [FieldOffset(0)] internal ushort Type; [FieldOffset(8)] internal IntPtr Pointer; }

    [GeneratedComInterface(Options = ComInterfaceOptions.ComObjectWrapper, StringMarshalling = StringMarshalling.Utf16),
        Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
    internal partial interface IDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int flow, uint stateMask,
            [MarshalUsing(typeof(UniqueComInterfaceMarshaller<IDeviceCollection>))] out IDeviceCollection collection);
        [PreserveSig] int GetDefaultAudioEndpoint(int flow, int role,
            [MarshalUsing(typeof(UniqueComInterfaceMarshaller<IDevice>))] out IDevice device);
        [PreserveSig] int GetDevice(string id,
            [MarshalUsing(typeof(UniqueComInterfaceMarshaller<IDevice>))] out IDevice device);
        [PreserveSig] int RegisterEndpointNotificationCallback(IntPtr callback);
        [PreserveSig] int UnregisterEndpointNotificationCallback(IntPtr callback);
    }

    [GeneratedComInterface(Options = ComInterfaceOptions.ComObjectWrapper), Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E")]
    internal partial interface IDeviceCollection
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int Item(uint index, [MarshalUsing(typeof(UniqueComInterfaceMarshaller<IDevice>))] out IDevice device);
    }

    [GeneratedComInterface(Options = ComInterfaceOptions.ComObjectWrapper, StringMarshalling = StringMarshalling.Utf16),
        Guid("D666063F-1587-4E43-81F1-B948E807363F")]
    internal partial interface IDevice
    {
        [PreserveSig] int Activate(in Guid iid, uint context, IntPtr activation,
            [MarshalUsing(typeof(UniqueComInterfaceMarshaller<object>))] out object instance);
        [PreserveSig] int OpenPropertyStore(uint access,
            [MarshalUsing(typeof(UniqueComInterfaceMarshaller<IPropertyStore>))] out IPropertyStore properties);
        [PreserveSig] int GetId(out string id);
        [PreserveSig] int GetState(out uint state);
    }

    [GeneratedComInterface(Options = ComInterfaceOptions.ComObjectWrapper), Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
    internal partial interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetAt(uint index, out PropertyKey key);
        [PreserveSig] int GetValue(in PropertyKey key, out PropVariant value);
        [PreserveSig] int SetValue(in PropertyKey key, in PropVariant value);
        [PreserveSig] int Commit();
    }

    [GeneratedComInterface(Options = ComInterfaceOptions.ComObjectWrapper), Guid("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2")]
    internal partial interface IAudioClient
    {
        [PreserveSig] int Initialize(int shareMode, uint flags, long duration, long periodicity, IntPtr format, IntPtr sessionGuid);
        [PreserveSig] int GetBufferSize(out uint frames);
        [PreserveSig] int GetStreamLatency(out long latency);
        [PreserveSig] int GetCurrentPadding(out uint frames);
        [PreserveSig] int IsFormatSupported(int shareMode, IntPtr format, out IntPtr closest);
        [PreserveSig] int GetMixFormat(out IntPtr format);
        [PreserveSig] int GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);
        [PreserveSig] int Start();
        [PreserveSig] int Stop();
        [PreserveSig] int Reset();
        [PreserveSig] int SetEventHandle(IntPtr handle);
        [PreserveSig] int GetService(in Guid iid,
            [MarshalUsing(typeof(UniqueComInterfaceMarshaller<object>))] out object service);
    }

    [GeneratedComInterface(Options = ComInterfaceOptions.ComObjectWrapper), Guid("F294ACFC-3146-4483-A7BF-ADDCA7C260E2")]
    internal partial interface IRenderClient
    {
        [PreserveSig] int GetBuffer(uint frames, out IntPtr data);
        [PreserveSig] int ReleaseBuffer(uint frames, uint flags);
    }

    [GeneratedComInterface(Options = ComInterfaceOptions.ComObjectWrapper, StringMarshalling = StringMarshalling.Utf16),
        Guid("2A07407E-6497-4A18-9787-32F79BD0D98F")]
    internal partial interface IDeviceTopology
    {
        [PreserveSig] int GetConnectorCount(out uint count);
        [PreserveSig] int GetConnector(uint index,
            [MarshalUsing(typeof(UniqueComInterfaceMarshaller<IConnector>))] out IConnector connector);
        [PreserveSig] int GetSubunitCount(out uint count);
        [PreserveSig] int GetSubunit(uint index, out IntPtr subunit);
        [PreserveSig] int GetPartById(uint id, out IntPtr part);
        [PreserveSig] int GetDeviceId(out string id);
        [PreserveSig] int GetSignalPath(IntPtr from, IntPtr to, [MarshalAs(UnmanagedType.Bool)] bool rejectMixed, out IntPtr parts);
    }

    [GeneratedComInterface(Options = ComInterfaceOptions.ComObjectWrapper, StringMarshalling = StringMarshalling.Utf16),
        Guid("9C2C4058-23F5-41DE-877A-DF3AF236A09E")]
    internal partial interface IConnector
    {
        [PreserveSig] int GetType(out int type);
        [PreserveSig] int GetDataFlow(out int flow);
        [PreserveSig] int ConnectTo(IConnector other);
        [PreserveSig] int Disconnect();
        [PreserveSig] int IsConnected([MarshalAs(UnmanagedType.Bool)] out bool connected);
        [PreserveSig] int GetConnectedTo([MarshalUsing(typeof(UniqueComInterfaceMarshaller<IConnector>))] out IConnector connected);
        [PreserveSig] int GetConnectorIdConnectedTo(out string id);
        [PreserveSig] int GetDeviceIdConnectedTo(out string id);
    }
}

#endif
