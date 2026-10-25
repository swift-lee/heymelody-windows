using System.Diagnostics;
using HeyMelody.Platform;

namespace HeyMelody.Diagnostics;

public static class DiagnosticBackendLauncher
{
    public static bool IsRunning()
    {
        try
        {
            if (!DesktopRuntime.TryOpenMutex(DesktopRuntime.HostMutexName, out var singleton)) return false;
            singleton!.Dispose();
        }
        catch (Exception failure)
        { DiagnosticLog.Debug("client.lifecycle", "backendProbeFailed", "Backend singleton probe failed; assuming it is running", new { failure = failure.Message }); }
        return true;
    }

    public static void Start()
    {
        if (DiagnosticRuntime.HasBoundLaunch)
        {
            var bound = new IOException("此窗口属于原后台运行周期，不能重新启动后台；请从主界面重新打开。");
            DiagnosticLog.Error("client.lifecycle", "backendLaunchRefused", "Backend launch refused for a window bound to a previous backend lifetime", bound);
            throw bound;
        }
        var start = ApplicationPaths.ResolveComponent("HeyMelody.Host");
        DiagnosticLog.Info("client.lifecycle", "backendLaunch", "Launching the backend", new { fileName = start.FileName, arguments = start.ArgumentList.ToArray() });
        try
        {
            using var process = Process.Start(start) ?? throw new IOException("无法启动蓝牙后台。");
            DiagnosticLog.Info("client.lifecycle", "backendLaunched", "Backend process started", new { processId = process.Id });
        }
        catch (Exception failure)
        {
            DiagnosticLog.Error("client.lifecycle", "backendLaunchFailed", "Backend process could not be started", failure, new { fileName = start.FileName });
            throw;
        }
    }
}
