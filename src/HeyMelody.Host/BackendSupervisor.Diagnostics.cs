using HeyMelody.Diagnostics;
using HeyMelodyWindows.App;

namespace HeyMelodyWindows.Host;

internal sealed partial class BackendSupervisor
{
    private readonly Dictionary<string, DiagnosticFailure> _diagnosticFailures = [];
    private bool _diagnosticsAttached;

    /// <summary>Attach after construction; startup failures are included in the next subscription baseline.</summary>
    public void AttachDiagnostics()
    {
        lock (_sync)
        {
            if (_diagnosticsAttached || _disposed) return;
            _diagnosticsAttached = true;
            DiagnosticLog.Failed += RecordDiagnosticFailure;
        }
        if (DiagnosticLog.LastFailure is { } failure) RecordDiagnosticFailure(failure);
    }

    public void DetachDiagnostics()
    {
        lock (_sync)
        {
            if (!_diagnosticsAttached) return;
            _diagnosticsAttached = false;
            DiagnosticLog.Failed -= RecordDiagnosticFailure;
        }
    }

    private void RecordDiagnosticFailure(DiagnosticFailure failure)
    {
        lock (_sync)
        {
            if (_disposed || !_diagnosticsAttached) return;
            if (DiagnosticLog.SessionId is { } current && failure.SessionId is { } session && session != current) return;
            string key = $"{failure.SessionId}\0{failure.Component}\0{failure.ProcessInstanceId}";
            if (_diagnosticFailures.TryGetValue(key, out var previous)
                && previous.Message == failure.Message && previous.Detail == failure.Detail) return;
            _diagnosticFailures[key] = failure;
            EmitLocked(new() { Kind = "diagnosticFailure", DiagnosticFailure = failure });
        }
    }

    private DiagnosticFailure[] DiagnosticFailuresLocked() => [.. _diagnosticFailures.Values];
}
