using HeyMelody.Diagnostics;

namespace HeyMelodyWindows.Host;

internal sealed partial class BackendPipeServer
{
    private async Task RequestExitFromClientAsync()
    {
        try { await RequestExitAsync(); }
        catch (Exception failure)
        {
            DiagnosticLog.Error("host.lifecycle", "clientExitFailed", "Client-requested exit failed", failure);
        }
    }
}
