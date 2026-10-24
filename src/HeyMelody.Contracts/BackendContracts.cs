using System.Text.Json;
using HeyMelody.Diagnostics;

namespace HeyMelodyWindows.App;

// Public messages contain opaque device identities, never Bluetooth addresses.
public sealed class BackendRequest
{
    // Shared detached `null` element: building one per request through reflection SerializeToElement was the costly path.
    private static readonly JsonElement NullAction = JsonDocument.Parse("null").RootElement.Clone();
    public int SchemaVersion { get; set; } = 4;
    public long Id { get; set; }
    public string Operation { get; set; } = "";
    public string? BackendInstanceId { get; set; }
    public string? DiagnosticCorrelationId { get; set; }
    public string? Identity { get; set; }
    public long? DeviceGeneration { get; set; }
    public string[]? DeviceIdentities { get; set; }
    public RefreshScope? RefreshScope { get; set; }
    public JsonElement Action { get; set; } = NullAction;
    public string? Role { get; set; }
    public string? ExitRequestId { get; set; }
    public bool? Approved { get; set; }
    public string? CalibrationHandle { get; set; }
    public long? Revision { get; set; }
}

public sealed class BackendMessage
{
    public int SchemaVersion { get; set; } = 4;
    public string Kind { get; set; } = "response";
    public long Id { get; set; }
    public bool Success { get; set; }
    public string? ErrorCode { get; set; }
    public string? Error { get; set; }
    public JsonElement? Data { get; set; }
    public BackendEvent? Event { get; set; }
}

public sealed class BackendEvent
{
    // catalog | snapshot | caseOpen | health | diagnosticFailure | activate | requestExit | exitCancelled | stopping | unavailable
    public string Kind { get; set; } = "";
    public string BackendInstanceId { get; set; } = "";
    public UiSnapshot? Snapshot { get; set; }
    public UiDeviceCatalog? Catalog { get; set; }
    public IReadOnlyList<UiDevice>? Devices { get; set; }
    public string? ExitRequestId { get; set; }
    public string? Message { get; set; }
    public DiagnosticFailure? DiagnosticFailure { get; set; }
    public bool FirstObservation { get; set; }
    public UiCalibrationState? Calibration { get; set; }
    public UiCalibrationSpectrum? CalibrationSpectrum { get; set; }
    public UiResourceSettings? Resources { get; set; }
}

public sealed class BackendSubscription
{
    public string BackendInstanceId { get; set; } = "";
    public string Version { get; set; } = "0.8.0";
    public UiDeviceCatalog Catalog { get; set; } = new([]);
    public IReadOnlyList<UiDevice> Devices { get; set; } = [];
    public IReadOnlyList<UiSnapshot> Snapshots { get; set; } = [];
    public IReadOnlyList<DiagnosticFailure> DiagnosticFailures { get; set; } = [];
}

// Typed Action/Data payloads; property names and order match the anonymous objects they replaced.
public sealed record HearingSelectionArgs(int? Uid);
public sealed record CalibrationCreateArgs(string Kind);
public sealed record ArtworkAcquireArgs(string ProductId, int? Color, string Role);
public sealed record ArtworkReleaseArgs(string LeaseId);
public sealed record ArtworkLeaseReply(UiArtwork? Artwork);

/// <summary>Shared backend access; selection does not own a Bluetooth connection.</summary>
public interface IHeadsetEventService : IHeadsetService
{
    event Action<BackendEvent>? EventReceived;
    Task<BackendSubscription> SubscribeAsync(string role, CancellationToken cancellationToken);
    Task<UiSnapshot> SelectAsync(string identity, CancellationToken cancellationToken);
    Task<UiSnapshot> PauseAsync(string identity, CancellationToken cancellationToken);
    Task<UiSnapshot> ResumeAsync(string identity, CancellationToken cancellationToken);
    Task<UiSnapshot> RefreshAsync(string identity, RefreshScope scope, CancellationToken cancellationToken);
    Task<UiApplyResult> ApplyAsync(string identity, long deviceGeneration, UiAction action, CancellationToken cancellationToken);
    Task AcknowledgeExitAsync(string exitRequestId, bool approved, CancellationToken cancellationToken);
    Task ActivateMainUiAsync(CancellationToken cancellationToken);
}
