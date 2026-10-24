namespace HeyMelody.Diagnostics;

public enum DiagnosticLevel { Debug, Information, Warning, Error, Critical }
public enum DiagnosticArmIntent { Preserve, ArmNew, CancelPending }

public sealed class DiagnosticFailure
{
    public string Component { get; set; } = "";
    public string Role { get; set; } = "";
    public string ProcessInstanceId { get; set; } = "";
    public string? SessionId { get; set; }
    public string Message { get; set; } = "";
    public string? Detail { get; set; }
    public string? Directory { get; set; }
    public DateTimeOffset TimeUtc { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class DiagnosticSettings
{
    public int SchemaVersion { get; set; } = 1;
    public string[] SelectedComponents { get; set; } = DiagnosticComponents.All.ToArray();
    public DiagnosticPendingRequest? Pending { get; set; }
}

public sealed class DiagnosticPendingRequest
{
    public string RequestId { get; set; } = Guid.NewGuid().ToString("N");
    public string[] Components { get; set; } = [];
}

public static class DiagnosticComponents
{
    public const string Main = "HeyMelody";
    public const string Host = "HeyMelody.Host";
    public const string DeviceManager = "HeyMelody.DeviceManager";
    public const string FitTest = "HeyMelody.FitTest";
    public const string HearingSetup = "HeyMelody.HearingSetup";
    public static IReadOnlyList<string> All { get; } = Array.AsReadOnly(new[] { Main, Host, DeviceManager, FitTest, HearingSetup });
    internal static string[] Normalize(IEnumerable<string> components) => All.Where(components.Contains).ToArray();
}

public sealed class DiagnosticSessionInfo
{
    public int SchemaVersion { get; set; } = 1;
    public bool Enabled { get; set; }
    public string SessionId { get; set; } = "";
    public string RequestId { get; set; } = "";
    public string OwnerEpoch { get; set; } = Guid.NewGuid().ToString("N");
    public int OwnerProcessId { get; set; }
    public long OwnerStartTimeUtcTicks { get; set; }
    public string UserSessionKey { get; set; } = "";
    public DateTimeOffset StartedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public string[] Components { get; set; } = [];
    public string? Directory { get; set; }
    public DiagnosticFailure? Failure { get; set; }
}
