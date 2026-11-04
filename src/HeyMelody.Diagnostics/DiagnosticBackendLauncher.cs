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
            // A child process launched directly keeps the parent's foreground/Dock grouping until Avalonia
            // applies ShowInDock=false, which can leave a bouncing helper tile. LaunchServices reads the
            // helper's LSUIElement before process start, so the Host never registers as a Dock application.
            if (TryStartMacBundle(start, out var bundle))
            {
                DiagnosticLog.Info("client.lifecycle", "backendLaunchRequested", "Backend launch requested through LaunchServices", new { bundle });
                return;
            }
            using var process = Process.Start(start) ?? throw new IOException("无法启动蓝牙后台。");
            DiagnosticLog.Info("client.lifecycle", "backendLaunched", "Backend process started", new { processId = process.Id });
        }
        catch (Exception failure)
        {
            DiagnosticLog.Error("client.lifecycle", "backendLaunchFailed", "Backend process could not be started", failure, new { fileName = start.FileName });
            throw;
        }
    }

    private static bool TryStartMacBundle(ProcessStartInfo start, out string bundle)
    {
        bundle = "";
        if (!OperatingSystem.IsMacOS()) return false;
        var directory = Path.GetDirectoryName(start.FileName);
        if (directory is null) return false;
        var candidate = Path.GetFullPath(Path.Combine(directory, "..", ".."));
        if (!candidate.EndsWith(".app", StringComparison.OrdinalIgnoreCase)
            || !Directory.Exists(Path.Combine(candidate, "Contents", "MacOS"))) return false;
        var open = new ProcessStartInfo("/usr/bin/open") { UseShellExecute = false, CreateNoWindow = true };
        open.ArgumentList.Add("-g");
        open.ArgumentList.Add("-j");
        foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
            if (entry.Key is string key && entry.Value is string value
                && (key.StartsWith("HEYMELODY_", StringComparison.Ordinal) || key.StartsWith("DOTNET_", StringComparison.Ordinal)))
            {
                open.ArgumentList.Add("--env");
                open.ArgumentList.Add(key + "=" + value);
            }
        open.ArgumentList.Add(candidate);
        using var process = Process.Start(open);
        if (process is null || !process.WaitForExit(5000) || process.ExitCode != 0) return false;
        bundle = candidate;
        return true;
    }
}
