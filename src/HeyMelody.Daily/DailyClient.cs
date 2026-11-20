using HeyMelodyWindows.Control;
namespace HeyMelodyWindows.Daily;

/// <summary>Daily OPPO controls are serialized; constructing a client never opens or writes a device.</summary>
public sealed partial class DailyClient(IControlConnector connector,
    Func<string, HeyMelodyWindows.Devices.DeviceProfile?>? profileResolver = null,
    long appVersionMarker = 17006004, string? observedProductId = null, byte? observedColor = null,
    TimeProvider? timeProvider = null) : IAsyncDisposable
{
    private readonly IControlConnector _connector = connector ?? throw new ArgumentNullException(nameof(connector));
    private readonly Func<string, HeyMelodyWindows.Devices.DeviceProfile?> _profileResolver = profileResolver ?? HeyMelodyWindows.Devices.DeviceModelResolver.FindByProductId;
    private readonly long _appVersionMarker = appVersionMarker;
    private string? _observedProductId = observedProductId;
    private byte? _observedColor = observedColor;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private volatile bool _disposed;
    private int _disposeStarted;
    public Task<DailySnapshot> RefreshAsync(CancellationToken token = default) => RefreshCoreAsync(DailyRefreshScope.Full, token);
    public Task<DailySnapshot> RefreshAsync(DailyRefreshScope scope, CancellationToken token = default) => RefreshCoreAsync(scope, token);
    /// <summary>Selects a configured main ANC mode without choosing a child adjustment.</summary>
    public Task<DailyApplyResult> ApplyAncModeAsync(byte primary, CancellationToken token = default) =>
        ApplyCoreAsync("anc-mode", primary, 0, null, false, token);
    public Task<DailyApplyResult> ApplyAncAsync(byte primary, byte level, CancellationToken token = default) =>
        ApplyCoreAsync("anc", primary, level, null, false, token);
    public Task<DailyApplyResult> ApplyPresetAsync(byte presetId, CancellationToken token = default) =>
        ApplyCoreAsync("preset", presetId, 0, null, false, token);
    public Task<DailyApplyResult> SelectCustomEqualizerAsync(byte id, CancellationToken token = default) =>
        ApplyCoreAsync("custom-select", id, 0, null, false, token);
    public Task<DailyApplyResult> SaveEqualizerAsync(EqualizerEdit edit, CancellationToken token = default) =>
        ApplyCoreAsync("custom-save", 0, 0, edit, false, token);
    public Task<DailyApplyResult> SetDualConnectionAsync(bool value, CancellationToken token = default) =>
        ApplyCoreAsync("dual", value ? (byte)1 : (byte)0, 0, null, false, token);
    public Task<DailyApplyResult> SetGameModeAsync(bool value, CancellationToken token = default) =>
        ApplyCoreAsync("game", value ? (byte)1 : (byte)0, 0, null, false, token);
    public Task<DailyApplyResult> SetSpatialModeAsync(byte mode, CancellationToken token = default) =>
        SetSpatialModeAsync(mode, false, token);
    /// <summary>The compatibility argument is retained; only the requested setting is submitted.</summary>
    public Task<DailyApplyResult> SetSpatialModeAsync(byte mode, bool allowMutexChange, CancellationToken token = default) =>
        ApplyCoreAsync("spatial", mode, 0, null, allowMutexChange, token);
    public Task<DailyApplyResult> SetHiResAsync(bool value, CancellationToken token = default) =>
        SetHiResAsync(value, false, token);
    /// <summary>The compatibility argument is retained; only the requested setting is submitted.</summary>
    public Task<DailyApplyResult> SetHiResAsync(bool value, bool allowMutexChange, CancellationToken token = default) =>
        ApplyCoreAsync("hires", value ? (byte)1 : (byte)0, 0, null, allowMutexChange, token);
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeStarted, 1) != 0) return;
        _monitorLifetime.Cancel();
        await _gate.WaitAsync();
        try
        {
            if (_findTone is { } tone) await StopFindHeadphonesToneCoreAsync(tone, CancellationToken.None);
            _disposed = true;
            await DropSessionAsync();
        }
        finally { _gate.Release(); }
        await _notificationRefresh;
        _monitorLifetime.Dispose();
    }
}
