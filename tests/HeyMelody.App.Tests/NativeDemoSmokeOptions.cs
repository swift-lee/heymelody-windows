using HeyMelody.Rendering.Filament;

internal sealed record NativeDemoSmokeOptions(string? Output, FilamentBackend Backend, bool ActivityRegression)
{
    internal const string Usage =
        "Usage: --native-demo-smoke [--backend vulkan|opengl] [--output-dir path] [--activity-regression]";

    internal static bool TryParse(string[] args, out NativeDemoSmokeOptions options, out string? error)
    {
        options = new(null, FilamentBackend.Vulkan, false);
        error = null;
        if (args.FirstOrDefault() != "--native-demo-smoke")
        {
            error = "Native demo options require --native-demo-smoke.";
            return false;
        }

        bool backendSeen = false, outputSeen = false, activitySeen = false;
        for (int index = 1; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--activity-regression" when !activitySeen:
                    activitySeen = true;
                    options = options with { ActivityRegression = true };
                    break;
                case "--backend" when !backendSeen:
                    backendSeen = true;
                    if (++index >= args.Length || args[index] is not ("vulkan" or "opengl"))
                    {
                        error = "--backend requires vulkan or opengl.";
                        return false;
                    }
                    options = options with
                    {
                        Backend = args[index] == "opengl" ? FilamentBackend.OpenGL : FilamentBackend.Vulkan
                    };
                    break;
                case "--output-dir" when !outputSeen:
                    outputSeen = true;
                    if (++index >= args.Length || string.IsNullOrWhiteSpace(args[index])
                        || args[index].StartsWith("--", StringComparison.Ordinal))
                    {
                        error = "--output-dir requires a nonempty path.";
                        return false;
                    }
                    try { options = options with { Output = Path.GetFullPath(args[index]) }; }
                    catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
                    {
                        error = "--output-dir is not a valid path.";
                        return false;
                    }
                    break;
                default:
                    error = "Unknown or repeated native demo option: " + args[index];
                    return false;
            }
        }
        return true;
    }
}

internal static class NativeDemoSmokeCommandLineTests
{
    internal static int Run()
    {
        int passed = 0;
        Check(NativeDemoSmokeOptions.TryParse(["--native-demo-smoke"], out var defaults, out _)
            && defaults.Backend == FilamentBackend.Vulkan && defaults.Output is null && !defaults.ActivityRegression,
            "Native demo defaults retain the short Vulkan smoke without activity regression.");
        Check(NativeDemoSmokeOptions.TryParse(["--native-demo-smoke", "--activity-regression"], out var activity, out _)
            && activity.ActivityRegression && activity.Backend == FilamentBackend.Vulkan,
            "The long activity regression is explicitly opt-in.");
        var output = Path.Combine("artifacts", "native-demo-cli-test-only");
        Check(NativeDemoSmokeOptions.TryParse(
                ["--native-demo-smoke", "--output-dir", output, "--activity-regression", "--backend", "opengl"],
                out var selected, out _)
            && selected.Backend == FilamentBackend.OpenGL && selected.ActivityRegression
            && selected.Output == Path.GetFullPath(output),
            "Native demo options accept either backend and arbitrary option order without creating output.");
        Check(NativeDemoSmokeOptions.TryParse(["--native-demo-smoke", "--backend", "vulkan"], out var explicitVulkan, out _)
            && explicitVulkan.Backend == FilamentBackend.Vulkan && !explicitVulkan.ActivityRegression,
            "Selecting a backend alone does not enable the long regression.");

        foreach (var args in new string[][]
        {
            [], ["--activity-regression"], ["--native-demo-smoke", "--backend"],
            ["--native-demo-smoke", "--backend", "metal"],
            ["--native-demo-smoke", "--backend", "--activity-regression"],
            ["--native-demo-smoke", "--backend", "vulkan", "--backend", "opengl"],
            ["--native-demo-smoke", "--output-dir"],
            ["--native-demo-smoke", "--output-dir", "--activity-regression"],
            ["--native-demo-smoke", "--output-dir", " "],
            ["--native-demo-smoke", "--output-dir", "bad\0path"],
            ["--native-demo-smoke", "--output-dir", output, "--output-dir", output],
            ["--native-demo-smoke", "--activity-regression", "--activity-regression"],
            ["--native-demo-smoke", "--activity-regression", "true"],
            ["--native-demo-smoke", "--unknown"]
        })
            Check(!NativeDemoSmokeOptions.TryParse(args, out _, out var error) && !string.IsNullOrWhiteSpace(error),
                "Invalid native demo arguments fail before starting a window: " + string.Join(" ", args).Replace("\0", "\\0"));
        return passed;

        void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
            passed++;
            Console.WriteLine("PASS " + message);
        }
    }
}
