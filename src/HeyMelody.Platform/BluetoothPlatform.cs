namespace HeyMelody.Platform;

public sealed record BluetoothCandidate(ulong Address, string Name, bool Connected, bool Remembered,
    bool Authenticated, uint ClassOfDevice);

public static class BluetoothCandidates
{
    public static bool IsCurrentAdvertisement(DateTimeOffset timestamp, DateTimeOffset startedAt, short rssi)
        => timestamp >= startedAt && rssi != -127;
    // Bluetooth Class of Device is a standard field, not proof of a control protocol.
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
}

public enum BluetoothAccess { Unknown, NotDetermined, Allowed, Denied, Restricted, Unsupported }
public sealed record BluetoothPlatformStatus(BluetoothAccess Access, bool? RadioAvailable, string? Message = null);
public abstract record BluetoothPlatformEvent;
public sealed record BluetoothStatusChanged(BluetoothPlatformStatus Status) : BluetoothPlatformEvent;
public sealed record BluetoothPairingChanged(ulong Address, bool Paired) : BluetoothPlatformEvent;
public sealed record BluetoothConnectionChanged(ulong Address, bool Connected) : BluetoothPlatformEvent;
public sealed record BluetoothSuspensionChanged(bool Suspended) : BluetoothPlatformEvent;
public sealed record BluetoothCatalogChanged : BluetoothPlatformEvent;
public sealed record BluetoothHealthChanged(string Message) : BluetoothPlatformEvent;
public sealed record BluetoothScanChanged(bool Active, long Generation, long RequestGeneration, DateTimeOffset StartedAt) : BluetoothPlatformEvent;
public sealed record BluetoothAdvertisementReceived(ushort CompanyId, byte[] Data, ulong SenderAddress,
    bool PublicSender, DateTimeOffset Timestamp, short Rssi) : BluetoothPlatformEvent
{
    public long Generation { get; init; }
    public long RequestGeneration { get; init; }
}

public enum BluetoothDiscoveryFailure { ServiceNotFound, AmbiguousServices }
public sealed class BluetoothDiscoveryException(BluetoothDiscoveryFailure failure, string message) : IOException(message)
{
    public BluetoothDiscoveryFailure Failure { get; } = failure;
}
public sealed class BluetoothAccessException(BluetoothAccess access, string message) : IOException(message)
{
    public BluetoothAccess Access { get; } = access;
}

/// <summary>OS Bluetooth only. Protocol policy, identity and device permissions remain in the Host.</summary>
public interface IBluetoothPlatform : IDisposable
{
    BluetoothPlatformStatus Status { get; }
    event Action<BluetoothPlatformEvent>? EventReceived;
    Task StartAsync(CancellationToken token);
    void SetScanDemand(bool wanted, long requestGeneration = 0);
    Task<IReadOnlyList<BluetoothCandidate>> EnumeratePairedAsync(CancellationToken token);
    Task<Stream> OpenControlChannelAsync(ulong address, IReadOnlyList<Guid> serviceIds, CancellationToken token);
}
