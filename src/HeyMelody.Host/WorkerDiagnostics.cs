using HeyMelodyWindows.Daily;

namespace HeyMelodyWindows.Host;

internal sealed record WorkerEqualizerLayout(byte id, sbyte MinimumGain, sbyte MaximumGain, int[] frequencies);

// The worker diagnostics operation keeps the existing JSON field names and readback scope.
internal sealed record WorkerDiagnostics(DateTimeOffset? observedAtUtc, string? productId,
    bool? profileVerified, bool? connected, DailyCapabilities? capabilities, Dictionary<byte, byte>? features,
    BatterySnapshot? battery, AncSnapshot? anc, byte? presetId, bool? customEqualizersRead,
    int[]? customEqualizerIds, WorkerEqualizerLayout[]? customEqualizerLayouts, ushort[]? equalizerFrequencies,
    bool? hiRes, byte? spatialMode, bool? gameMode, bool? dualConnection, List<string>? warnings,
    long applicationGetSubmissions, long applicationSetSubmissions, long applicationOtherSubmissions,
    string evidenceScope);
