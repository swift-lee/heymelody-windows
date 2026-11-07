using System.Collections;
using System.Diagnostics;
using HeyMelody.Diagnostics.Platforms.MacOS;

internal static class MacBackendLaunchChecks
{
    internal static void Run(Action<bool, string> check)
    {
        var root = Path.Combine(Path.GetTempPath(), "HeyMelody launch plans");
        var bundle = Path.Combine(root, "HeyMelody.Host.app");
        var executable = Path.Combine(bundle, "Contents", "MacOS", "HeyMelody.Host");
        var emptyEnvironment = new Hashtable();
        var probed = new List<string>();
        bool Exists(string path) { probed.Add(path); return true; }

        var nonMac = MacBackendLauncher.CreatePlan(executable, false,
            _ => throw new InvalidOperationException("A non-macOS launch must not probe the bundle."), emptyEnvironment);
        check(nonMac is null, "non-macOS preserves direct startup without creating an open launch plan");
        if (!OperatingSystem.IsMacOS())
            check(!MacBackendLauncher.TryStart(new ProcessStartInfo(executable), out var nonMacBundle)
                && nonMacBundle.Length == 0, "non-macOS runtime helper does not start a native process");

        var environment = new Hashtable
        {
            ["HEYMELODY_SESSION"] = "a session with spaces",
            ["DOTNET_ROOT"] = Path.Combine(root, "dotnet runtime"),
            ["PATH"] = "must not be forwarded",
            ["HTTP_PROXY"] = "must not be forwarded",
            ["heymelody_lowercase"] = "must not be forwarded",
            ["DOTNET"] = "must not be forwarded",
            ["DOTNET_NULL"] = null,
            ["DOTNET_NUMBER"] = 123,
            [123] = "must not be forwarded"
        };
        var plan = MacBackendLauncher.CreatePlan(executable, true, Exists, environment);
        check(plan is not null, "a macOS app bundle receives a LaunchServices plan");
        if (plan is null) return;
        check(probed.SequenceEqual([Path.Combine(bundle, "Contents", "MacOS")]),
            "bundle validation probes only the expected Contents/MacOS directory");
        check(plan.Bundle == bundle, "the resolved bundle path is retained without a shell");
        check(plan.StartInfo.FileName == "/usr/bin/open" && !plan.StartInfo.UseShellExecute && plan.StartInfo.CreateNoWindow,
            "the macOS launch plan invokes open directly without shell execution");
        var arguments = plan.StartInfo.ArgumentList.ToArray();
        check(arguments.Take(2).SequenceEqual(["-g", "-j"]), "LaunchServices keeps background and hidden launch options");
        check(arguments[^1] == bundle, "a bundle path containing spaces remains one argument");
        var forwarded = new HashSet<string>(StringComparer.Ordinal);
        bool paired = true;
        for (int i = 2; i < arguments.Length - 1; i += 2)
        {
            paired &= arguments[i] == "--env" && i + 1 < arguments.Length - 1;
            if (i + 1 < arguments.Length - 1) forwarded.Add(arguments[i + 1]);
        }
        check(paired && arguments.Length == 7, "each whitelisted environment value is a separate --env argument");
        check(forwarded.SetEquals(["HEYMELODY_SESSION=a session with spaces", "DOTNET_ROOT=" + environment["DOTNET_ROOT"]]),
            "only string HEYMELODY_ and DOTNET_ variables are forwarded with exact values");

        check(MacBackendLauncher.CreatePlan(executable, true, _ => false, emptyEnvironment) is null,
            "a missing bundle MacOS directory preserves direct startup fallback");
        var uppercaseBundle = Path.Combine(root, "HeyMelody.Host.APP");
        check(MacBackendLauncher.CreatePlan(Path.Combine(uppercaseBundle, "Contents", "MacOS", "HeyMelody.Host"),
            true, _ => true, emptyEnvironment)?.Bundle == uppercaseBundle, "app bundle suffix matching retains its case-insensitive behavior");

        foreach (var looseExecutable in new[]
        {
            Path.Combine(root, "publish", "HeyMelody.Host"),
            Path.Combine(root, "src", "HeyMelody.Host", "bin", "Release", "net10.0", "HeyMelody.Host"),
            Path.Combine(root, "src", "HeyMelody.App", "bin", "Release", "net10.0", "HeyMelody.Host"),
            Path.Combine(root, "HeyMelody.Host.appex", "Contents", "MacOS", "HeyMelody.Host")
        })
        {
            check(MacBackendLauncher.CreatePlan(looseExecutable, true,
                _ => throw new InvalidOperationException("A non-bundle launch must not probe native directories."), emptyEnvironment) is null,
                "non-bundle source or loose publish retains direct startup: " + Path.GetFileName(Path.GetDirectoryName(looseExecutable)));
        }
    }
}
