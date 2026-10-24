using HeyMelody.Diagnostics;

namespace HeyMelodyWindows.Host;

public static class BackendEndpoint
{
    public static string UserSessionKey => DiagnosticPaths.UserSessionKey;
    public static string PipeName => "HeyMelody-v2-" + UserSessionKey;
    public static string HostMutexName => "Local\\HeyMelody.Host-" + UserSessionKey;
    public static string MainMutexName => "Local\\HeyMelody.Main-" + UserSessionKey;
}
