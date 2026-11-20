using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace HeyMelodyWindows.App;

// BCL-only contracts. Identity values stay in memory and must never enter logs.
public sealed record UiDevice(string Identity, string Name, bool Connected)
{
    public bool IsPreferredSelection { get; init; }
    // Appearance hints remembered from earlier verified sessions or associated broadcasts.
    // They select artwork only and never grant control, capabilities or settings.
    public string? ProductId { get; init; }
    public byte? Color { get; init; }
}
public sealed record UiOption(int Key, string Name);
public sealed record UiBand(int FrequencyHz, int Gain);
public sealed record UiEqEntry(int Id, string Name, int MinimumGain, int MaximumGain,
    bool Selected, IReadOnlyList<UiBand> Bands);

public enum RefreshScope { Full, Identity, Features, Anc, Equalizer, Battery, Information }
public enum ConnectionState { Unknown, Connected, Disconnected }
public enum MonitoringState { Unknown, WaitingForConnection, Connecting, Monitoring, Paused, Unsupported, Unavailable }
public enum CaseLidState { Unknown, Open, Closed }
public enum EarbudPlacement { Unknown, InCase, InEar, OutOfCase }
public enum ApplyFailure
{
    None, Validation, StateUnavailable, PeerRejected, ReadbackMismatch,
    ReadbackUnavailable, TransportUnknown, Disconnected, WorkerUnavailable, MutexConsentRequired
}

public sealed class UiSnapshot
{
    // Cached settings may be presented offline; these never authorize control.
    public bool PresentationCached { get; set; }
    public DateTimeOffset? PresentationSampleTime { get; set; }
    public string BackendInstanceId { get; set; } = "";
    public long DeviceGeneration { get; set; }
    public long Revision { get; set; }
    public bool ControlReady { get; set; }
    public MonitoringState MonitoringState { get; set; }
    public CaseLidState CaseLid { get; set; }
    public EarbudPlacement LeftPlacement { get; set; }
    public EarbudPlacement RightPlacement { get; set; }
    public string? ObservationSource { get; set; }
    public DateTimeOffset? ObservationTime { get; set; }
    public DateTimeOffset? BatteryObservationTime { get; set; }
    public DateTimeOffset? PlacementObservationTime { get; set; }
    public DateTimeOffset? CaseObservationTime { get; set; }
    public DateTimeOffset? LeftPlacementObservationTime { get; set; }
    public DateTimeOffset? RightPlacementObservationTime { get; set; }
    public DateTimeOffset? LeftBatteryObservationTime { get; set; }
    public DateTimeOffset? RightBatteryObservationTime { get; set; }
    public DateTimeOffset? CaseBatteryObservationTime { get; set; }
    public bool ObservationFresh { get; set; }
    public string Identity { get; set; } = "";
    public string DeviceName { get; set; } = "";
    public string? ProductId { get; set; }
    public string? Failure { get; set; }
    public bool Connected { get; set; }
    public ConnectionState ConnectionState { get; set; }
    public bool SupportedModel { get; set; }
    public long ResourceRevision { get; set; }
    public byte? Color { get; set; }
    public DateTimeOffset? ColorObservationTime { get; set; }
    public int CustomEqualizerCapacity { get; set; } = 3;
    public IReadOnlyList<int> EqualizerFrequencies { get; set; } = [];
    public IReadOnlyList<UiAncMode> AncModes { get; set; } = [];
    public IReadOnlyList<UiFeatureSection> FeatureSections { get; set; } = [];
    public bool CanFitTest { get; set; }
    public bool CanSetupHearing { get; set; }
    public bool? HearingEnabled { get; set; }
    public IReadOnlyList<UiHearingProfile> HearingProfiles { get; set; } = [];
    public int? LeftBattery { get; set; }
    public int? RightBattery { get; set; }
    public int? CaseBattery { get; set; }
    public bool? LeftCharging { get; set; }
    public bool? RightCharging { get; set; }
    public bool? CaseCharging { get; set; }
    public int? Anc { get; set; }
    public int? AncPrimary { get; set; }
    public int? Equalizer { get; set; }
    public int? Spatial { get; set; }
    public bool? DualConnection { get; set; }
    public bool? GameMode { get; set; }
    public bool? HiRes { get; set; }
    public bool CanSetAnc { get; set; }
    public bool CanSetAncMode { get; set; }
    public bool CanSetEqualizer { get; set; }
    public bool CanSetCustomEqualizer { get; set; }
    public bool CanSetDualConnection { get; set; }
    public bool CanSetGameMode { get; set; }
    public bool CanSetSpatial { get; set; }
    public bool CanSetHiRes { get; set; }
    public UiFindHeadphonesToneState? FindHeadphonesTone { get; set; }
    public bool SpatialHiResMutex { get; set; }
    public IReadOnlyList<UiOption> AncOptions { get; set; } = Array.Empty<UiOption>();
    public IReadOnlyList<UiOption> EqualizerOptions { get; set; } = Array.Empty<UiOption>();
    public IReadOnlyList<UiOption> SpatialOptions { get; set; } = Array.Empty<UiOption>();
    public IReadOnlyList<UiEqEntry> EqualizerEntries { get; set; } = Array.Empty<UiEqEntry>();
    public string? Firmware { get; set; }
    public DateTimeOffset SampleTime { get; set; }
    public IReadOnlyList<string> Warnings { get; set; } = Array.Empty<string>();

    // Shallow copy for the Host's publish path: nested lists and records are never mutated in place, so they are shared.
    // A JSON round trip drops null members and the initializers refill them; the ??= lines reproduce that, and
    // UiSnapshot tests compare Clone() with the round trip byte for byte so a new reference property cannot be missed.
    public UiSnapshot Clone()
    {
        var copy = (UiSnapshot)MemberwiseClone();
        copy.BackendInstanceId ??= ""; copy.Identity ??= ""; copy.DeviceName ??= "";
        copy.EqualizerFrequencies ??= []; copy.AncModes ??= []; copy.FeatureSections ??= []; copy.HearingProfiles ??= [];
        copy.AncOptions ??= Array.Empty<UiOption>(); copy.EqualizerOptions ??= Array.Empty<UiOption>();
        copy.SpatialOptions ??= Array.Empty<UiOption>(); copy.EqualizerEntries ??= Array.Empty<UiEqEntry>();
        copy.Warnings ??= Array.Empty<string>();
        return copy;
    }
}

// Kind: AncMode | Anc | Equalizer | CustomEqualizer | CustomSelect | DualConnection | GameMode | Spatial | HiRes.
// AncMode Value is the parent protocol ID; Anc retains its packed (primary << 8) | level value.
// Other values are explicit protocol IDs or 0/1; custom EQ carries the complete entry.
public sealed record UiAction(string Kind, int Value, UiEqEntry? EqualizerEntry = null,
    bool AllowMutexChange = false, string? FeatureId = null, string? Text = null);
public sealed class UiApplyResult
{
    public bool BackendReceiptReceived { get; set; }
    public ConnectionState ConnectionState { get; set; }
    public ApplyFailure FailureKind { get; set; }
    public bool TargetSubmitted { get; set; }
    public bool PrerequisiteSubmitted { get; set; }
    public byte? AckStatus { get; set; }
    public int? ObservedEqualizerId { get; set; }
    public bool Accepted { get; set; }
    public bool? ReadbackMatches { get; set; }
    public bool SubmissionAttempted { get; set; }
    public bool PartialEffects { get; set; }
    public bool AcknowledgementUnknown { get; set; }
    public string Message { get; set; } = "";
    public UiSnapshot? After { get; set; }
}

public interface IHeadsetService : IAsyncDisposable
{
    Task<IReadOnlyList<UiDevice>> EnumerateAsync(CancellationToken cancellationToken);
    Task<UiSnapshot> ConnectAsync(string identity, CancellationToken cancellationToken);
    Task DisconnectAsync(CancellationToken cancellationToken);
    Task<UiSnapshot> RefreshAsync(CancellationToken cancellationToken);
    Task<UiSnapshot> RefreshAsync(RefreshScope scope, CancellationToken cancellationToken)
        => RefreshAsync(cancellationToken);
    Task<UiApplyResult> ApplyAsync(UiAction action, CancellationToken cancellationToken);
}
