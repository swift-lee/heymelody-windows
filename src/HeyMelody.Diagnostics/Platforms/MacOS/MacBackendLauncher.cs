using System.Collections;
using System.Diagnostics;

namespace HeyMelody.Diagnostics.Platforms.MacOS;

internal sealed record MacBundleLaunchPlan(string Bundle, ProcessStartInfo StartInfo);

internal static class MacBackendLauncher
{
    internal static bool TryStart(ProcessStartInfo start, out string bundle)
    {
        bundle = "";
        if (!OperatingSystem.IsMacOS()) return false;
        var plan = CreatePlan(start.FileName, true, Directory.Exists, Environment.GetEnvironmentVariables());
        if (plan is null) return false;

        // LaunchServices reads LSUIElement before the Host starts, avoiding a transient helper Dock tile.
        using var process = Process.Start(plan.StartInfo);
        if (process is null || !process.WaitForExit(5000) || process.ExitCode != 0) return false;
        bundle = plan.Bundle;
        return true;
    }

    internal static MacBundleLaunchPlan? CreatePlan(string executablePath, bool isMacOS,
        Func<string, bool> directoryExists, IDictionary environment)
    {
        if (!isMacOS) return null;
        var directory = Path.GetDirectoryName(executablePath);
        if (directory is null) return null;
        var candidate = Path.GetFullPath(Path.Combine(directory, "..", ".."));
        if (!candidate.EndsWith(".app", StringComparison.OrdinalIgnoreCase)
            || !directoryExists(Path.Combine(candidate, "Contents", "MacOS"))) return null;

        var open = new ProcessStartInfo("/usr/bin/open") { UseShellExecute = false, CreateNoWindow = true };
        open.ArgumentList.Add("-g");
        open.ArgumentList.Add("-j");
        foreach (DictionaryEntry entry in environment)
            if (entry.Key is string key && entry.Value is string value
                && (key.StartsWith("HEYMELODY_", StringComparison.Ordinal) || key.StartsWith("DOTNET_", StringComparison.Ordinal)))
            {
                open.ArgumentList.Add("--env");
                open.ArgumentList.Add(key + "=" + value);
            }
        open.ArgumentList.Add(candidate);
        return new(candidate, open);
    }
}
