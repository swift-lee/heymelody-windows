using System.Buffers.Binary;
using System.Runtime.InteropServices;
using HeyMelody.Diagnostics;

namespace HeyMelodyWindows.Ui;

/// <summary>Identifies DXGI's WARP adapter without relying on its display name or adding a graphics dependency.</summary>
internal static unsafe class WindowsWarpAdapterResolver
{
    private static readonly Guid Factory4Id = new("1bc6ea02-ef36-464f-bf0c-21ca39e5168a");
    private static readonly Guid Adapter1Id = new("29038f61-3839-4626-91fd-086879011a05");
    private static readonly Guid DxgiDeviceId = new("54ec77fa-1377-44e6-8c32-88fd5f44c84c");

    internal static byte[]? GetAdapterLuid()
    {
        if (!OperatingSystem.IsWindows())
            return null;

        nint factory = 0;
        nint adapter = 0;
        try
        {
            Marshal.ThrowExceptionForHR(CreateDXGIFactory1(in Factory4Id, out factory));
            if (factory == 0)
                return null;

            // IDXGIFactory4::EnumWarpAdapter is slot 27, including inherited IUnknown/DXGI methods.
            var enumWarpAdapter = (delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)(*(nint**)factory)[27];
            var adapterId = Adapter1Id;
            Marshal.ThrowExceptionForHR(enumWarpAdapter(factory, &adapterId, &adapter));
            if (adapter == 0)
                return null;

            // IDXGIAdapter1::GetDesc1 is slot 10. All HRESULTs are checked before reading the output.
            var getDescription = (delegate* unmanaged[Stdcall]<nint, AdapterDescription1*, int>)(*(nint**)adapter)[10];
            AdapterDescription1 description = default;
            Marshal.ThrowExceptionForHR(getDescription(adapter, &description));
            DiagnosticLog.Debug("Avalonia.GPU", "warpCandidate", "DXGI resolved the WARP adapter LUID.",
                new { description.VendorId, description.DeviceId, luid = Convert.ToHexString(description.AdapterLuid.ToBytes()) });
            return description.AdapterLuid.ToBytes();
        }
        finally
        {
            if (adapter != 0)
                Marshal.Release(adapter);
            if (factory != 0)
                Marshal.Release(factory);
        }
    }

    internal static object? DescribeAdapter(byte[]? luid)
    {
        if (!OperatingSystem.IsWindows() || luid is not { Length: 8 }) return null;
        nint factory = 0, adapter = 0;
        try
        {
            Marshal.ThrowExceptionForHR(CreateDXGIFactory1(in Factory4Id, out factory));
            if (factory == 0) return null;
            var identity = new AdapterLuid
            {
                LowPart = BinaryPrimitives.ReadUInt32LittleEndian(luid),
                HighPart = BinaryPrimitives.ReadInt32LittleEndian(luid.AsSpan(4))
            };
            // IDXGIFactory4::EnumAdapterByLuid is slot 26; query the adapter actually selected by ANGLE.
            var resolve = (delegate* unmanaged[Stdcall]<nint, AdapterLuid, Guid*, nint*, int>)(*(nint**)factory)[26];
            var interfaceId = Adapter1Id;
            Marshal.ThrowExceptionForHR(resolve(factory, identity, &interfaceId, &adapter));
            if (adapter == 0) return null;
            AdapterDescription1 description = default;
            var getDescription = (delegate* unmanaged[Stdcall]<nint, AdapterDescription1*, int>)(*(nint**)adapter)[10];
            Marshal.ThrowExceptionForHR(getDescription(adapter, &description));
            // IDXGIDevice queries the UMD version. Unsupported interfaces remain explicitly unknown.
            // https://learn.microsoft.com/windows/win32/api/dxgi/nf-dxgi-idxgiadapter-checkinterfacesupport
            var checkInterface = (delegate* unmanaged[Stdcall]<nint, Guid*, long*, int>)(*(nint**)adapter)[9];
            var dxgiDevice = DxgiDeviceId;
            long driver = 0;
            var driverResult = checkInterface(adapter, &dxgiDevice, &driver);
            var version = unchecked((ulong)driver);
            return new
            {
                description = new string(description.Description, 0, 128).TrimEnd('\0'),
                description.VendorId, description.DeviceId, description.SubSystemId, description.Revision,
                dedicatedVideoMemory = (ulong)description.DedicatedVideoMemory,
                dedicatedSystemMemory = (ulong)description.DedicatedSystemMemory,
                sharedSystemMemory = (ulong)description.SharedSystemMemory,
                description.Flags, software = (description.Flags & 2) != 0,
                luid = Convert.ToHexString(description.AdapterLuid.ToBytes()),
                driverVersion = driverResult >= 0
                    ? FormattableString.Invariant($"{version >> 48}.{(version >> 32) & 65535}.{(version >> 16) & 65535}.{version & 65535}") : null,
                driverVersionHResult = $"0x{driverResult:X8}", driverVersionInterface = "IDXGIDevice"
            };
        }
        catch (Exception error)
        {
            DiagnosticLog.Warning("Avalonia.GPU", "adapterDetailsUnavailable", "DXGI could not describe the selected adapter; selection is unchanged.",
                error, new { luid = Convert.ToHexString(luid) });
            return null;
        }
        finally
        {
            if (adapter != 0) Marshal.Release(adapter);
            if (factory != 0) Marshal.Release(factory);
        }
    }

    [DllImport("dxgi.dll", ExactSpelling = true)]
    private static extern int CreateDXGIFactory1(in Guid interfaceId, out nint factory);

    // Native LUID has four-byte alignment even in an x86 process. Using ulong here would change DESC1's layout.
    [StructLayout(LayoutKind.Sequential)]
    internal struct AdapterLuid
    {
        public uint LowPart;
        public int HighPart;

        internal readonly byte[] ToBytes()
        {
            var result = new byte[8];
            BinaryPrimitives.WriteUInt32LittleEndian(result, LowPart);
            BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(4), HighPart);
            return result;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct AdapterDescription1
    {
        public fixed char Description[128];
        public uint VendorId;
        public uint DeviceId;
        public uint SubSystemId;
        public uint Revision;
        public nuint DedicatedVideoMemory;
        public nuint DedicatedSystemMemory;
        public nuint SharedSystemMemory;
        public AdapterLuid AdapterLuid;
        public uint Flags;
    }
}
