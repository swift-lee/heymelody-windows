using System.Diagnostics;

namespace HeyMelody.Diagnostics;

/// <summary>Opt-in execution check; never consults production settings, ready files or owners.</summary>
internal sealed class IsolatedBenchmarkDiagnostics : IDisposable
{
    private IsolatedBenchmarkDiagnostics() { }

    internal static IsolatedBenchmarkDiagnostics Start(string component, string role, string runId, string output)
    {
        DiagnosticLog.InitializeIdentity(component, role);
        using var process = Process.GetCurrentProcess();
        var directory = Path.Combine(output, "diagnostics");
        Directory.CreateDirectory(directory);
        var session = new DiagnosticSessionInfo
        {
            Enabled = true, SessionId = runId, RequestId = runId,
            OwnerProcessId = Environment.ProcessId, OwnerStartTimeUtcTicks = process.StartTime.ToUniversalTime().Ticks,
            UserSessionKey = DiagnosticPaths.UserSessionKey, Components = [component], Directory = directory
        };
        DiagnosticSettingsStore.AtomicWrite(Path.Combine(directory, "session.json"), session);
        DiagnosticLog.Configure(session);
        if (!DiagnosticLog.IsEnabled)
            throw new IOException("Isolated benchmark diagnostics could not start.");
        return new();
    }

    public void Dispose()
    {
        DiagnosticLog.Flush();
        DiagnosticLog.Stop("Isolated benchmark completed");
    }
}
