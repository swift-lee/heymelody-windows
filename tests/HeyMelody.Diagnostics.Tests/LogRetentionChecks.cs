using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using HeyMelody.Diagnostics;

internal static class LogRetentionChecks
{
    internal static async Task<int?> TryRunChildAsync(string[] args)
    {
        if (args.Length == 0 || !args[0].StartsWith("--retention-", StringComparison.Ordinal)) return null;
        try
        {
            if (args.Length is < 3 or > 4) throw new ArgumentException("Invalid retention fixture arguments.");
            string root = Path.GetFullPath(args[1]);
            if (!root.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Retention fixtures may use only an isolated temporary data root.");
            DiagnosticPaths.DataRootOverride = root;
            IDisposable lease = args[0] switch
            {
                "--retention-host" => DiagnosticRuntime.InitializeHost(),
                "--retention-client" when args.Length == 4 && DiagnosticComponents.All.Contains(args[2])
                    => DiagnosticRuntime.InitializeClient(args[2], () => throw new InvalidOperationException("Real backend launch is forbidden."), () => true),
                "--retention-worker" when args.Length == 4 && args[2] is "worker" or "catalog-worker"
                    => DiagnosticRuntime.InitializeWorker(args[2]),
                _ => throw new ArgumentException("Unknown retention fixture mode.")
            };
            using (lease)
            {
                DiagnosticLog.Info("RetentionFixture", "retention.synthetic", "Synthetic lifecycle marker", new { marker = args[^1] });
                DiagnosticLog.Flush();
                Status("ready");
                while (await Console.In.ReadLineAsync() is { } command && command != "stop")
                {
                    if (command != "status") throw new InvalidOperationException("Unknown fixture command.");
                    Status("status");
                }
            }
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 2; }
        finally { DiagnosticLog.ResetForTests(); DiagnosticPaths.DataRootOverride = null; }
    }

    private static void Status(string kind) => Console.WriteLine(JsonSerializer.Serialize(new
    {
        kind, enabled = DiagnosticLog.IsEnabled, hasSession = DiagnosticLog.HasSession,
        sessionId = DiagnosticLog.SessionId, component = DiagnosticLog.Component,
        directory = DiagnosticRuntime.ReadCurrentSession()?.Directory
    }));

    internal static async Task<int> RunAsync(string root, Action<bool, string> check)
    {
        Directory.CreateDirectory(root);
        string? previousRoot = DiagnosticPaths.DataRootOverride;
        DiagnosticLog.ResetForTests();
        DiagnosticPaths.DataRootOverride = root;
        var stages = new List<object>();
        var firstArchive = new Dictionary<string, FileStamp>();
        var finalArchive = new Dictionary<string, FileStamp>();
        bool success = false;
        void Verify(bool valid, string name)
        {
            stages.Add(new { name, passed = valid }); check(valid, name);
        }
        try
        {
            DiagnosticSettingsStore.Save(DiagnosticComponents.All, DiagnosticArmIntent.ArmNew);
            await using (var host = await Fixture.StartAsync("--retention-host", root, "cycle-one"))
            {
                Verify(Enabled(host.Ready) && DiagnosticSettingsStore.Load().Pending is null,
                    "retention: first isolated Host consumes the armed request and logs");
                var active = Snapshot(DiagnosticPaths.LogRoot);
                Verify(active.Count >= 2, "retention: enabled Host creates a session descriptor and JSONL on disk");
                await using (var gui = await Fixture.StartAsync("--retention-client", root, DiagnosticComponents.Main, "first-gui"))
                {
                    Verify(Enabled(gui.Ready) && SameSession(gui.Ready, host.Ready), "retention: isolated GUI joins the enabled Host cycle");
                    active = Snapshot(DiagnosticPaths.LogRoot);
                    await gui.StopAsync();
                }
                Verify(Preserved(DiagnosticPaths.LogRoot, active, exact: false),
                    "retention: GUI close preserves all prior files and byte prefixes, allowing the normal stop tail");
                var afterFirstGui = Snapshot(DiagnosticPaths.LogRoot);
                await using (var gui = await Fixture.StartAsync("--retention-client", root, DiagnosticComponents.Main, "reopened-gui"))
                {
                    Verify(Enabled(gui.Ready) && SameSession(gui.Ready, host.Ready), "retention: reopened GUI joins the same cycle");
                    await gui.StopAsync();
                }
                Verify(Preserved(DiagnosticPaths.LogRoot, afterFirstGui, exact: true),
                    "retention: GUI reopen creates its own file without changing previously closed GUI or idle Host files");
                await JoinOtherComponentsAsync(root);
                var beforeClose = Snapshot(DiagnosticPaths.LogRoot);
                Verify(beforeClose.Count >= 9, "retention: all five product identities plus both worker roles have disk evidence");
                await host.StopAsync();
                Verify(Preserved(DiagnosticPaths.LogRoot, beforeClose, exact: false),
                    "retention: Host dispose preserves every file and prior byte prefix");
                firstArchive = Snapshot(DiagnosticPaths.LogRoot);
            }

            await using (var host = await Fixture.StartAsync("--retention-host", root, "cycle-two-off"))
            {
                Verify(!Enabled(host.Ready), "retention: second Host starts logging-off without rearming");
                Verify(Preserved(DiagnosticPaths.LogRoot, firstArchive, exact: true),
                    "retention: logging-off Host startup preserves all old paths and SHA256 values");
                for (int open = 0; open < 2; open++)
                {
                    await using var gui = await Fixture.StartAsync("--retention-client", root, DiagnosticComponents.Main, "off-gui-" + open);
                    Verify(!Enabled(gui.Ready), "retention: logging-off GUI open " + open + " stays off");
                    await gui.StopAsync();
                    Verify(Preserved(DiagnosticPaths.LogRoot, firstArchive, exact: true),
                        "retention: logging-off GUI close/reopen preserves every old SHA256, pass " + open);
                }
                await host.StopAsync();
            }
            Verify(SameTree(DiagnosticPaths.LogRoot, firstArchive), "retention: closing the second off cycle neither removes nor adds old log files");

            DiagnosticSettingsStore.Save(DiagnosticComponents.All, DiagnosticArmIntent.ArmNew);
            await using (var host = await Fixture.StartAsync("--retention-host", root, "cycle-three-rearmed"))
            {
                Verify(Enabled(host.Ready) && DiagnosticSettingsStore.Load().Pending is null,
                    "retention: an explicit rearm starts a fresh enabled cycle");
                Verify(Preserved(DiagnosticPaths.LogRoot, firstArchive, exact: true),
                    "retention: rearmed Host creation preserves the complete first archive");
                await JoinOtherComponentsAsync(root);
                await using var gui = await Fixture.StartAsync("--retention-client", root, DiagnosticComponents.Main, "third-cycle-live-gui");
                Verify(Enabled(gui.Ready) && SameSession(gui.Ready, host.Ready), "retention: new GUI joins the rearmed cycle");
                Verify(Preserved(DiagnosticPaths.LogRoot, firstArchive, exact: true),
                    "retention: all new component joins preserve first-cycle paths and hashes");
                await host.StopAsync();
                var timer = Stopwatch.StartNew();
                JsonElement status;
                do { status = await gui.StatusAsync(); if (!status.GetProperty("hasSession").GetBoolean()) break; await Task.Delay(20); }
                while (timer.Elapsed < TimeSpan.FromSeconds(5));
                Verify(!Enabled(status) && !status.GetProperty("hasSession").GetBoolean(),
                    "retention: owner exit stops the live GUI writer without deleting the archive");
                await gui.StopAsync();
            }
            Verify(Preserved(DiagnosticPaths.LogRoot, firstArchive, exact: true),
                "retention: completing the rearmed cycle leaves first-cycle SHA256 values unchanged");
            finalArchive = Snapshot(DiagnosticPaths.LogRoot);
            Verify(Directory.GetDirectories(DiagnosticPaths.LogRoot).Length == 2 && finalArchive.Count > firstArchive.Count,
                "retention: both completed logging-cycle directories coexist on disk");
            await using (var host = await Fixture.StartAsync("--retention-host", root, "cycle-four-off"))
            {
                Verify(!Enabled(host.Ready) && SameTree(DiagnosticPaths.LogRoot, finalArchive),
                    "retention: another off startup preserves both complete archives byte-for-byte");
                await host.StopAsync();
            }
            Verify(SameTree(DiagnosticPaths.LogRoot, finalArchive), "retention: final off teardown preserves every recorded path and SHA256");
            success = true;
            return stages.Count;
        }
        finally
        {
            DiagnosticLog.ResetForTests();
            DiagnosticPaths.DataRootOverride = previousRoot;
            var receipt = new { schemaVersion = 1, success, dataRoot = root, scope = "Only isolated test-process DiagnosticRuntime lifecycles; no real Host, GUI, Explorer, Bluetooth or installer",
                stages, firstArchive = firstArchive.Values, finalArchive = finalArchive.Values };
            File.WriteAllText(Path.Combine(root, "log-retention-receipt.json"), JsonSerializer.Serialize(receipt, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine("Retention evidence: " + root);
        }
    }

    private static async Task JoinOtherComponentsAsync(string root)
    {
        foreach (string component in new[] { DiagnosticComponents.DeviceManager, DiagnosticComponents.FitTest, DiagnosticComponents.HearingSetup })
        {
            await using var client = await Fixture.StartAsync("--retention-client", root, component, "synthetic-" + component);
            if (!Enabled(client.Ready)) throw new InvalidOperationException("Selected component did not log: " + component);
            await client.StopAsync();
        }
        foreach (string role in new[] { "worker", "catalog-worker" })
        {
            await using var worker = await Fixture.StartAsync("--retention-worker", root, role, "synthetic-" + role);
            if (!Enabled(worker.Ready)) throw new InvalidOperationException("Selected worker did not log: " + role);
            await worker.StopAsync();
        }
    }
    private static bool Enabled(JsonElement value) => value.GetProperty("enabled").GetBoolean();
    private static bool SameSession(JsonElement one, JsonElement two) => one.GetProperty("sessionId").GetString() == two.GetProperty("sessionId").GetString();
    private sealed record FileStamp(string Path, long Length, string Sha256);
    private static Dictionary<string, FileStamp> Snapshot(string root)
    {
        var result = new Dictionary<string, FileStamp>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(root)) return result;
        foreach (string path in Directory.GetFiles(root, "*", SearchOption.AllDirectories))
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            long length = file.Length;
            string relative = System.IO.Path.GetRelativePath(root, path);
            result.Add(relative, new(relative, length, Hash(file, length)));
        }
        return result;
    }
    private static bool Preserved(string root, Dictionary<string, FileStamp> expected, bool exact)
    {
        foreach (var entry in expected.Values)
        {
            string path = System.IO.Path.Combine(root, entry.Path);
            if (!File.Exists(path)) return false;
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (file.Length < entry.Length || exact && file.Length != entry.Length || Hash(file, entry.Length) != entry.Sha256) return false;
        }
        return true;
    }
    private static bool SameTree(string root, Dictionary<string, FileStamp> expected) => Snapshot(root).Count == expected.Count && Preserved(root, expected, exact: true);
    private static string Hash(Stream stream, long length)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[8192];
        while (length > 0)
        {
            int read = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, length));
            if (read == 0) throw new EndOfStreamException("Fixture log shrank while hashing.");
            hash.AppendData(buffer, 0, read); length -= read;
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }
    private sealed class Fixture(Process process, Task<string> error) : IAsyncDisposable
    {
        internal JsonElement Ready { get; private set; }
        internal static async Task<Fixture> StartAsync(params string[] args)
        {
            var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
            start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
            foreach (string argument in args) start.ArgumentList.Add(argument);
            var process = Process.Start(start) ?? throw new IOException("Could not start isolated retention fixture.");
            var fixture = new Fixture(process, process.StandardError.ReadToEndAsync());
            try { fixture.Ready = await fixture.ReadAsync(); return fixture; }
            catch { await fixture.DisposeAsync(); throw; }
        }
        private async Task<JsonElement> ReadAsync()
        {
            string? line = await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10));
            if (line is null) throw new IOException("Isolated fixture exited before its status: " + await error.WaitAsync(TimeSpan.FromSeconds(3)));
            return JsonDocument.Parse(line).RootElement.Clone();
        }
        internal async Task<JsonElement> StatusAsync()
        { await process.StandardInput.WriteLineAsync("status"); await process.StandardInput.FlushAsync(); return await ReadAsync(); }
        internal async Task StopAsync()
        {
            if (!process.HasExited) { await process.StandardInput.WriteLineAsync("stop"); await process.StandardInput.FlushAsync(); }
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            string stderr = await error;
            if (process.ExitCode != 0) throw new IOException("Isolated fixture failed with " + process.ExitCode + ": " + stderr);
        }
        public async ValueTask DisposeAsync()
        {
            try { await StopAsync(); }
            finally { if (!process.HasExited) process.Kill(entireProcessTree: true); process.Dispose(); }
        }
    }
}
