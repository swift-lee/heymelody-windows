using System.ComponentModel;
using System.Runtime.InteropServices;
using HeyMelody.Diagnostics;

namespace HeyMelodyWindows.Windows;

public sealed record BluetoothCandidate(ulong Address, string Name, bool Connected, bool Remembered,
    bool Authenticated, uint ClassOfDevice);

public static class WindowsBluetooth
{
    /// <summary>Uses Windows' cached device class only; it does not verify a profile or contact the peer.</summary>
    public static bool IsAudioCandidate(BluetoothCandidate device)
    {
        if (!device.Authenticated) return false;
        uint major = (device.ClassOfDevice >> 8) & 0x1F;
        uint minor = (device.ClassOfDevice >> 2) & 0x3F;
        bool audioHint = (device.ClassOfDevice & 0x00200000) != 0;
        if (major == 4)
            return minor is 1 or 2 or 4 or 5 or 6 or 7 or 8 or 10 or 15 or 19 or 20 || audioHint;
        return (major is 0 or 31) && audioHint;
    }

    /// <summary>Enumerates authenticated or remembered devices from Windows, without inquiry or pairing.
    /// This synchronous native operation has no absolute time bound; supervise the caller process.</summary>
    public static unsafe IReadOnlyList<BluetoothCandidate> EnumeratePaired()
    {
        DiagnosticLog.Debug("Windows.Bluetooth", "devices.enumeration-started", "Enumerating cached authenticated or remembered Bluetooth devices without inquiry.");
        Native.RequireWindows();
        var search = new Native.BluetoothSearch
        {
            Size = (uint)sizeof(Native.BluetoothSearch), ReturnAuthenticated = 1, ReturnRemembered = 1
        };
        var info = new Native.BluetoothInfo { Size = (uint)sizeof(Native.BluetoothInfo) };
        var handle = Native.BluetoothFindFirstDevice(&search, &info);
        var firstError = handle == 0 ? Marshal.GetLastPInvokeError() : 0;
        if (handle == 0)
        {
            if (firstError == 259) return Array.Empty<BluetoothCandidate>();
            var failure = new Win32Exception(firstError, "Bluetooth device enumeration failed.");
            DiagnosticLog.Error("Windows.Bluetooth", "devices.enumeration-failed", "Bluetooth enumeration could not start.", failure);
            throw failure;
        }
        try
        {
            var result = new Dictionary<ulong, BluetoothCandidate>();
            for (var count = 0; ; count++)
            {
                if (count >= 4096) throw new InvalidDataException("Bluetooth enumeration exceeded the record limit.");
                if (info.Remembered != 0 || info.Authenticated != 0)
                {
                    var length = 0;
                    while (length < 248 && info.Name[length] != '\0') length++;
                    var address = info.Address & 0xFFFFFFFFFFFFUL;
                    result[address] = new(address, new string(info.Name, 0, length), info.Connected != 0,
                        info.Remembered != 0, info.Authenticated != 0, info.ClassOfDevice);
                }
                info = new Native.BluetoothInfo { Size = (uint)sizeof(Native.BluetoothInfo) };
                var found = Native.BluetoothFindNextDevice(handle, &info);
                var error = found ? 0 : Marshal.GetLastPInvokeError();
                if (found) continue;
                if (error != 259) throw new Win32Exception(error, "Bluetooth device enumeration failed.");
                var devices = result.Values.OrderBy(x => x.Address).ToArray();
                DiagnosticLog.Debug("Windows.Bluetooth", "devices.enumerated", "Enumerated the cached Bluetooth candidates.", devices);
                return devices;
            }
        }
        catch (Exception error) { DiagnosticLog.Error("Windows.Bluetooth", "devices.enumeration-failed", "Bluetooth enumeration failed.", error); throw; }
        finally { Native.BluetoothFindDeviceClose(handle); }
    }
}
