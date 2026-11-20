using HeyMelodyWindows.Protocol;

namespace HeyMelodyWindows.Daily;

public sealed record DailyFeatureOption(int Value, string Name);
public sealed record DailyFeatureControl(string Id, string Title, string Kind, int? Value, string? Text,
    IReadOnlyList<DailyFeatureOption> Options, int? Minimum, int? Maximum, bool CanWrite);
public sealed record DailyFeatureSection(string Id, string Title, bool DefaultExpanded, IReadOnlyList<DailyFeatureControl> Controls);

public enum DailyRefreshScope { Full, Identity, Features, Anc, Equalizer, Battery, Information }
public enum DailyApplyFailure
{
    None, Validation, StateUnavailable, PeerRejected, ReadbackMismatch, ReadbackUnavailable,
    TransportUnknown, Disconnected, WorkerUnavailable, MutexConsentRequired
}

public sealed record BatteryLevel(byte Raw, int? Percent, bool? Charging);
public sealed class BatterySnapshot
{
    public BatteryLevel? Left { get; set; }
    public BatteryLevel? Right { get; set; }
    public BatteryLevel? Case { get; set; }
    public Dictionary<byte, byte> RawComponents { get; set; } = [];
}
public sealed class AncSnapshot
{
    public byte Kind { get; set; }
    public byte ModeType { get; set; }
    public byte[] RawMask { get; set; } = [];
    public byte? Primary { get; set; }
    public byte? Level { get; set; }
}
public sealed record AncOption(byte Primary, byte Level, int ModeType, string Name);
public sealed record PresetOption(byte ProtocolId, int ModeType, string Name);
public sealed record EqualizerBand(ushort Frequency, sbyte Gain);
public sealed class CustomEqualizer
{
    public byte Id { get; set; }
    public string Name { get; set; } = "";
    public bool Selected { get; set; }
    public sbyte MinimumGain { get; set; }
    public sbyte MaximumGain { get; set; }
    public List<EqualizerBand> Bands { get; set; } = [];
}
public sealed class EqualizerEdit
{
    /// <summary>Zero creates a new entry; nonzero updates that exact returned device ID.</summary>
    public byte Id { get; set; }
    public string Name { get; set; } = "";
    public sbyte MinimumGain { get; set; } = -6;
    public sbyte MaximumGain { get; set; } = 6;
    public List<EqualizerBand> Bands { get; set; } = [];
    public bool Activate { get; set; } = true;
}
public sealed class DailyCapabilities
{
    public bool Battery { get; set; }
    public bool Anc { get; set; }
    public bool Preset { get; set; }
    public bool CustomEqualizer { get; set; }
    public bool DualConnection { get; set; }
    public bool GameMode { get; set; }
    public bool SpatialMode { get; set; }
    public bool HiRes { get; set; }
    public bool FindHeadphonesTone { get; set; }
    public bool FindHeadphonesWearCheck { get; set; }
    public bool SpatialHiResMutex { get; set; }
    public byte? GameFeatureId { get; set; }
    public string SpatialRoute { get; set; } = "unavailable";
}
public sealed class DailySnapshot
{
    public long Revision { get; set; }
    public DateTimeOffset ObservedAtUtc { get; set; }
    public bool Connected { get; set; }
    public bool ProfileVerified { get; set; }
    public string? ProductId { get; set; }
    // Protocol color from GET 0x010B; unknown when the device rejects or leaves the optional read unanswered.
    public byte? Color { get; set; }
    public DateTimeOffset? ColorObservedAtUtc { get; set; }
    public string? DeviceName { get; set; }
    public string? FirmwareVersion { get; set; }
    // Presentation only; FirmwareVersion retains the running earbud version used by capability gates.
    public string? FirmwareDisplayVersion { get; set; }
    public byte? CodecRaw { get; set; }
    public bool? CaseOpen { get; set; }
    public bool? LeftInCase { get; set; }
    public bool? RightInCase { get; set; }
    public bool? LeftInEar { get; set; }
    public bool? RightInEar { get; set; }
    public DateTimeOffset? CaseObservedAtUtc { get; set; }
    public DateTimeOffset? LeftPlacementObservedAtUtc { get; set; }
    public DateTimeOffset? RightPlacementObservedAtUtc { get; set; }
    public DateTimeOffset? LeftBatteryObservedAtUtc { get; set; }
    public DateTimeOffset? RightBatteryObservedAtUtc { get; set; }
    public DateTimeOffset? CaseBatteryObservedAtUtc { get; set; }
    public Dictionary<byte, byte> RawStatusComponents { get; set; } = [];
    public BatterySnapshot? Battery { get; set; }
    public AncSnapshot? Anc { get; set; }
    public byte? PresetId { get; set; }
    public List<PresetOption> Presets { get; set; } = [];
    public List<AncOption> AncOptions { get; set; } = [];
    public List<CustomEqualizer> CustomEqualizers { get; set; } = [];
    public bool CustomEqualizersRead { get; set; }
    public ushort[] EqualizerFrequencies { get; set; } = [];
    public int CustomEqualizerCapacity { get; set; } = 3;
    public bool? DualConnection { get; set; }
    public bool? GameMode { get; set; }
    public byte? SpatialMode { get; set; }
    public bool? HiRes { get; set; }
    public long FindHeadphonesToneSession { get; set; }
    public DailyFindHeadphonesTonePhase FindHeadphonesTonePhase { get; set; }
    internal long LeftWearSession { get; set; }
    internal long RightWearSession { get; set; }
    public bool FindHeadphonesWorn => Capabilities.FindHeadphonesWearCheck && FindHeadphonesToneSession > 0
        && (LeftInEar == true && LeftWearSession == FindHeadphonesToneSession
            || RightInEar == true && RightWearSession == FindHeadphonesToneSession);
    public DailyCapabilities Capabilities { get; set; } = new();
    public Dictionary<byte, byte> RawFeatures { get; set; } = [];
    public BassEngineRange? BassEngine { get; set; }
    public byte? PromptVolume { get; set; }
    public GameSoundState? GameSound { get; set; }
    public IReadOnlyList<GestureAssignment> Gestures { get; set; } = [];
    public IReadOnlyList<DailyFeatureSection> FeatureSections { get; set; } = [];
    public List<string> Warnings { get; set; } = [];
    public string? Error { get; set; }
}
public sealed class DailyApplyResult
{
    public bool SubmissionAttempted { get; set; }
    public bool TargetSubmitted { get; set; }
    public bool PrerequisiteSubmitted { get; set; }
    public bool ResponseMatched { get; set; }
    public bool? AckAccepted { get; set; }
    public byte? AckStatus { get; set; }
    public bool? ReadbackMatches { get; set; }
    public bool ReadbackVerified => ReadbackMatches == true;
    public DailyApplyFailure FailureKind { get; set; }
    public byte? ObservedEqualizerId { get; set; }
    public bool Success { get; set; }
    public DailySnapshot? Before { get; set; }
    public DailySnapshot? After { get; set; }
    public List<string> PartialEffects { get; set; } = [];
    public string? Error { get; set; }
    public string Message { get; set; } = "";
}
