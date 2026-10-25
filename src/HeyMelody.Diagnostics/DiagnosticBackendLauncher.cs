using System.Diagnostics;

namespace HeyMelody.Diagnostics;

public static class DiagnosticBackendLauncher
{
    public static bool IsRunning()
    {
        if (!OperatingSystem.IsWindows()) return true;
        try
        {
            if (!Mutex.TryOpenExisting("Local\\HeyMelody.Host-" + DiagnosticPaths.UserSessionKey, out var singleton)) return false;
            singleton.Dispose();
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
        var executable = Path.Combine(AppContext.BaseDirectory, "backend", "HeyMelody.Host.exe");
        if (!File.Exists(executable)) executable = Path.Combine(AppContext.BaseDirectory, "HeyMelody.Host.exe");
        var start = new ProcessStartInfo { FileName = executable, UseShellExecute = false, CreateNoWindow = true,
            WorkingDirectory = AppContext.BaseDirectory };
        if (!File.Exists(executable))
        {
            var dll = new[] { "Debug", "Release" }.Select(configuration => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
                "../../../../HeyMelody.Host/bin", configuration, "net10.0-windows10.0.19041.0/HeyMelody.Host.dll"))).FirstOrDefault(File.Exists);
            if (dll is null)
            {
                var missing = new FileNotFoundException("蓝牙后台组件缺失，请重启软件或重新安装。");
                DiagnosticLog.Error("client.lifecycle", "backendLaunchFailed", "Backend executable was not found", missing, new { fileName = start.FileName });
                throw missing;
            }
            start.FileName = "dotnet";
            start.ArgumentList.Add(dll);
        }
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
