internal static class NativePerformanceAuditCommandLineTests
{
    public static int Run()
    {
        int checks = 0;
        string[] baseline = ["--native-performance-audit", "--scenario", "model", "--backend", "vulkan",
            "--seconds", "30", "--output-dir", "unused-audit-output"];
        void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
            checks++;
        }

        Check(NativePerformanceAudit.TryParseOptions(baseline, out var defaults)
            && !defaults.ContainsKey("--verify-settings"), "Default audit unexpectedly requests settings verification.");
        Check(NativePerformanceAudit.TryParseOptions([.. baseline, "--verify-settings", "true"], out var settings)
            && settings["--verify-settings"] == "true", "Model settings verification rejected.");
        Check(NativePerformanceAudit.TryParseOptions([.. baseline, "--verify-settings", "false"], out _),
            "Explicitly disabled model settings verification rejected.");
        string[] animated = ["--native-performance-audit", "--scenario", "animated", "--backend", "vulkan",
            "--seconds", "5", "--output-dir", "unused-audit-output"];
        Check(NativePerformanceAudit.TryParseOptions([.. animated, "--artwork-file", "local-x4.webp",
            "--verify-settings", "true"], out _), "Explicit animated-artwork settings verification rejected.");
        Check(NativePerformanceAudit.TryParseOptions([.. animated, "--verify-settings", "false"], out _),
            "Explicitly disabled animated-artwork settings verification rejected.");
        foreach (var scenario in new[] { "static", "static-idle", "static-scroll", "model-scroll", "model-frozen" })
        {
            string[] unsupported = ["--native-performance-audit", "--scenario", scenario, "--backend", "vulkan",
                "--seconds", "5", "--output-dir", "unused-audit-output"];
            Check(NativePerformanceAudit.TryParseOptions(unsupported, out _),
                "Existing scenario without settings verification rejected: " + scenario);
            foreach (var value in new[] { "true", "false" })
                Check(!NativePerformanceAudit.TryParseOptions([.. unsupported, "--verify-settings", value], out _),
                    "Settings option accepted for unsupported scenario: " + scenario);
        }
        foreach (var value in new[] { "on", "TRUE", "1", "" })
            Check(!NativePerformanceAudit.TryParseOptions([.. baseline, "--verify-settings", value], out _),
                "Invalid settings verification value accepted: " + value);
        Check(!NativePerformanceAudit.TryParseOptions([.. baseline, "--verify-settings"], out _),
            "Missing settings verification value accepted.");
        Check(!NativePerformanceAudit.TryParseOptions([.. baseline, "--verify-settings", "true",
            "--verify-settings", "false"], out _), "Duplicate settings verification option accepted.");
        Check(NativePerformanceAudit.TryParseOptions([.. baseline, "--model-max-edge", "0",
            "--frame-ready-probe", "off", "--theme", "dark"], out var parsed)
            && parsed["--model-max-edge"] == "0" && parsed["--frame-ready-probe"] == "off",
            "Baseline quality override rejected.");
        Check(NativePerformanceAudit.TryParseOptions([.. baseline, "--model-max-edge", "800",
            "--frame-ready-probe", "on"], out _), "Candidate quality override rejected.");
        Check(NativePerformanceAudit.TryParseOptions([.. baseline, "--model-max-edge", "4096"], out _),
            "Maximum audit pixel budget rejected.");
        foreach (var edge in new[] { "-1", "4097", "NaN", "1.5", "2147483648" })
            Check(!NativePerformanceAudit.TryParseOptions([.. baseline, "--model-max-edge", edge], out _),
                "Invalid audit pixel budget accepted: " + edge);
        foreach (var probe in new[] { "true", "OFF", "pending" })
            Check(!NativePerformanceAudit.TryParseOptions([.. baseline, "--frame-ready-probe", probe], out _),
                "Invalid readiness mode accepted: " + probe);
        Check(!NativePerformanceAudit.TryParseOptions([.. baseline, "--theme", "system"], out _),
            "Nondeterministic audit theme accepted.");
        Check(!NativePerformanceAudit.TryParseOptions([.. baseline, "--model-max-edge"], out _),
            "Missing audit option value accepted.");
        Check(!NativePerformanceAudit.TryParseOptions([.. baseline, "--frame-ready-probe", "on",
            "--frame-ready-probe", "off"], out _), "Duplicate audit option accepted.");
        Check(!NativePerformanceAudit.TryParseOptions([.. baseline, "--unknown", "on"], out _),
            "Unknown audit option accepted.");
        Check(!NativePerformanceAudit.TryParseOptions(["--native-performance-audit"], out _),
            "Missing audit arguments accepted.");
        Check(!NativePerformanceAudit.TryParseOptions([.. baseline, "--theme", "light", "--seconds", "5"], out _),
            "Duplicate required audit option accepted.");
        return checks;
    }
}
