using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using HeyMelody.Diagnostics;

internal static class ChildLaunchChecks
{
    internal static async Task<int> RunChildAsync(string[] args)
    {
        DiagnosticPaths.DataRootOverride = args[1];
        Console.WriteLine("READY");
        await Console.In.ReadLineAsync();
        var expired = DiagnosticRuntime.IsBoundLaunchExpired;
        var ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        DiagnosticRuntime.BoundOwnerExited += () => ended.TrySetResult();
        int starts = 0;
        using var lifetime = DiagnosticRuntime.InitializeClient(DiagnosticComponents.DeviceManager,
            () => Interlocked.Increment(ref starts), () => false);
        bool rejected = false;
        try { DiagnosticBackendLauncher.Start(); } catch (IOException) { rejected = true; }
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            bound = DiagnosticRuntime.HasBoundLaunch, expired, starts, defaultLauncherRejected = rejected,
            joinedOwner = DiagnosticLog.Session?.OwnerProcessId
        }));
        if (args[2] == "live")
        {
            await ended.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Console.WriteLine("OWNER_ENDED");
        }
        return 0;
    }

    internal static async Task RunAsync(string root, Action<bool, string> check)
    {
        ProcessStartInfo StartInfo(params string[] arguments)
        {
            var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
            start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
            foreach (var argument in arguments) start.ArgumentList.Add(argument);
            return start;
        }
        async Task<Process> Owner()
        {
            var process = Process.Start(StartInfo("--owner")) ?? throw new IOException("Owner fixture failed");
            if (await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5)) != "READY")
                throw new InvalidDataException("Owner fixture did not start");
            return process;
        }
        DiagnosticSessionInfo Info(Process owner) => new()
        {
            Enabled = false, OwnerProcessId = owner.Id, OwnerStartTimeUtcTicks = owner.StartTime.ToUniversalTime().Ticks,
            UserSessionKey = DiagnosticPaths.UserSessionKey
        };
        async Task End(Process owner)
        {
            if (owner.HasExited) return;
            await owner.StandardInput.WriteLineAsync("exit");
            await owner.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        }

        foreach (var mode in new[] { "expired", "replacement", "live", "unknown", "recovered" })
        {
            DiagnosticLog.ResetForTests();
            using var owner = await Owner();
            var info = Info(owner);
            DiagnosticSettingsStore.AtomicWrite(DiagnosticPaths.ReadyFile, info);
            if (mode != "unknown")
            {
                DiagnosticLog.InitializeIdentity(DiagnosticComponents.Main, "main");
                DiagnosticLog.Configure(info);
            }
            else File.Delete(DiagnosticPaths.ReadyFile);
            Process? replacement = null;
            var expectedOwner = owner;
            if (mode == "recovered")
            {
                await End(owner);
                replacement = await Owner();
                expectedOwner = replacement;
                DiagnosticSettingsStore.AtomicWrite(DiagnosticPaths.ReadyFile, Info(replacement));
            }
            var start = StartInfo("--bound-child", root, mode == "recovered" ? "live" : mode);
            DiagnosticRuntime.BindChildStart(start);
            if (mode == "recovered")
                check(DiagnosticLog.Session?.OwnerProcessId == owner.Id, "recovering component launch does not switch or re-enable the old logging session");
            using var child = Process.Start(start) ?? throw new IOException("Bound child fixture failed");
            try
            {
                check(await child.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5)) == "READY",
                    "owned child has started before its bootstrap gate: " + mode);
                if (mode is "expired" or "replacement") await End(owner);
                if (mode == "replacement")
                {
                    replacement = await Owner();
                    DiagnosticSettingsStore.AtomicWrite(DiagnosticPaths.ReadyFile, Info(replacement));
                }
                await child.StandardInput.WriteLineAsync("bootstrap");
                var line = await child.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5));
                using var state = JsonDocument.Parse(line ?? throw new InvalidDataException("Missing child state"));
                var value = state.RootElement;
                check(value.GetProperty("bound").GetBoolean() && value.GetProperty("starts").GetInt32() == 0
                    && value.GetProperty("defaultLauncherRejected").GetBoolean(), "owned component can never auto-start Host: " + mode);
                check(value.GetProperty("expired").GetBoolean() == (mode is not ("live" or "recovered")), "owner expiry and replacement are explicit: " + mode);
                if (mode is "live" or "recovered")
                {
                    check(value.GetProperty("joinedOwner").GetInt32() == expectedOwner.Id, "logging-off child joins its exact live owner");
                    await End(expectedOwner);
                    check(await child.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5)) == "OWNER_ENDED",
                        "bound component receives owner exit even without an IPC subscription");
                }
                else check(value.GetProperty("joinedOwner").ValueKind == JsonValueKind.Null,
                    "expired owned child cannot join a replacement Host: " + mode);
                await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                check(child.ExitCode == 0, "owned bootstrap fixture exits normally: " + mode);
            }
            finally
            {
                await End(owner);
                if (replacement is not null) { await End(replacement); replacement.Dispose(); }
                if (!child.HasExited) { await child.StandardInput.WriteLineAsync("finish"); await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); }
            }
        }
        DiagnosticLog.ResetForTests();
    }
}
