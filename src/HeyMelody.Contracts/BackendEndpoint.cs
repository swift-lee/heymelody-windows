using HeyMelody.Diagnostics;

namespace HeyMelodyWindows.Host;

public static class BackendEndpoint
{
    public static string UserSessionKey => DiagnosticPaths.UserSessionKey;
    public static string PipeName => HeyMelody.Platform.DesktopRuntime.PipeName;
    public static string HostMutexName => HeyMelody.Platform.DesktopRuntime.HostMutexName;
    public static string MainMutexName => HeyMelody.Platform.DesktopRuntime.MainMutexName;
}
