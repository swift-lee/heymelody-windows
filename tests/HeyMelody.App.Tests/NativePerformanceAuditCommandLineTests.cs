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

        Check(NativePerformanceAudit.TryParseOptions(baseline, out _), "Default audit options rejected.");
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
