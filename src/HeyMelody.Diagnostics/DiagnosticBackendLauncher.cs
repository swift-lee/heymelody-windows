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
        catch (Exception) { }
        return true;
    }

    public static void Start()
    {
        var executable = Path.Combine(AppContext.BaseDirectory, "backend", "HeyMelody.Host.exe");
        if (!File.Exists(executable)) executable = Path.Combine(AppContext.BaseDirectory, "HeyMelody.Host.exe");
        var start = new ProcessStartInfo { FileName = executable, UseShellExecute = false, CreateNoWindow = true,
            WorkingDirectory = AppContext.BaseDirectory };
        if (!File.Exists(executable))
        {
            var dll = new[] { "Debug", "Release" }.Select(configuration => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
                "../../../../HeyMelody.Host/bin", configuration, "net10.0-windows10.0.19041.0/HeyMelody.Host.dll"))).FirstOrDefault(File.Exists);
            if (dll is null) throw new FileNotFoundException("蓝牙后台组件缺失，请重启软件或重新安装。");
            start.FileName = "dotnet";
            start.ArgumentList.Add(dll);
        }
        using var process = Process.Start(start) ?? throw new IOException("无法启动蓝牙后台。");
    }
}
