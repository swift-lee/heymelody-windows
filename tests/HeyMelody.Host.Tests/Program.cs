using System.Text.Json;
using HeyMelodyWindows.App;
using HeyMelodyWindows.Host;
using HeyMelodyWindows.Windows;

TestProcessErrorMode.SuppressWindowsErrorDialogs();

if (args is ["--fake-peer"]) return await BackendTests.FakePeerMainAsync();
if (args is ["--diagnostic-fake-peer", var capture]) return await DiagnosticIntegrationTests.FakePeerMainAsync(capture);

int passed = 0;
string scratch = Path.Combine(Path.GetTempPath(), "HeyMelody-HostTests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(scratch);
try
{
    if (args is ["--diagnostics-tests"])
    {
        await DiagnosticIntegrationTests.RunAsync(Test, scratch);
        Console.WriteLine(JsonSerializer.Serialize(new { passed, success = true, nativeBluetoothCalls = 0 }));
        return 0;
    }
    if (args is ["--audition-tests"])
    {
        await CalibrationAuditionTests.RunAsync(Test, scratch);
        Console.WriteLine(JsonSerializer.Serialize(new { passed, success = true, nativeBluetoothCalls = 0 }));
        return 0;
    }
    await Test("cached classes include paired audio even while disconnected", () =>
    {
        foreach (uint minor in new uint[] { 1, 2, 4, 5, 6, 7, 8, 10, 15, 19, 20 })
            Check(WindowsBluetooth.IsAudioCandidate(Audio(1) with { ClassOfDevice = 0x400 | (minor << 2) }));
        Check(WindowsBluetooth.IsAudioCandidate(Audio(1) with { ClassOfDevice = 0x200400 }));
        Check(WindowsBluetooth.IsAudioCandidate(Audio(1) with { ClassOfDevice = 0x200000 }));
        Check(WindowsBluetooth.IsAudioCandidate(Audio(1) with { ClassOfDevice = 0x201F00 }));
        Check(WindowsBluetooth.IsAudioCandidate(Audio(1) with { ClassOfDevice = 0x404 | (1u << 14) }));
        return Task.CompletedTask;
    });
    await Test("remembered-only, keyboards, video and unknown classes are excluded", () =>
    {
        foreach (uint cod in new uint[] { 0, 0x540, 0x580, 0x200540, 0x438, 0x430, 0x20010C, 0x20020C })
            Check(!WindowsBluetooth.IsAudioCandidate(Audio(1) with { ClassOfDevice = cod }));
        Check(!WindowsBluetooth.IsAudioCandidate(Audio(1) with { Authenticated = false }));
        return Task.CompletedTask;
    });
    await Test("missing whitelist is empty and loading never creates a file", () =>
    {
        string path = Path.Combine(scratch, "missing", "devices.json");
        Check(new DeviceWhitelistStore(path).Load().Count == 0 && !File.Exists(path));
        return Task.CompletedTask;
    });
    await Test("persistent keys survive service restart and do not contain addresses or session tokens", async () =>
    {
        string path = Path.Combine(scratch, "restart.json");
        var store = new DeviceWhitelistStore(path);
        var peers = new List<BluetoothCandidate> { Audio(0x112233445566, "同名耳机"), Audio(2, "同名耳机") };
        string firstIdentity;
        await using (var service = new NativeHeadsetService(() => peers, store))
        {
            Check((await service.EnumerateAsync(default)).Count == 0);
            var candidates = (await service.EnumerateCandidatesAsync(default)).Candidates;
            Check(candidates.Count == 2 && candidates.All(device => !device.Enabled));
            firstIdentity = candidates.Single(device => device.Identity != candidates[0].Identity).Identity;
            var chosen = candidates.Single(device => device.Identity == firstIdentity);
            Check((await service.SaveWhitelistAsync([chosen.Identity], default)).Saved);
            var main = await service.EnumerateAsync(default);
            Check(main.Count == 1 && main[0].Identity == chosen.Identity && !main[0].Connected);
        }
        string saved = File.ReadAllText(path);
        Check(!saved.Contains("112233445566", StringComparison.Ordinal) && !saved.Contains(firstIdentity, StringComparison.Ordinal));
        await using var restarted = new NativeHeadsetService(() => peers, store);
        var after = (await restarted.EnumerateCandidatesAsync(default)).Candidates;
        Check(after.Count(device => device.Enabled) == 1 && after.All(device => device.Identity != firstIdentity));
        Check((await restarted.EnumerateAsync(default)).Count == 1);
    });
    await Test("renaming and disconnecting leave an allowed paired device visible", async () =>
    {
        var store = Store("rename");
        var peers = new List<BluetoothCandidate> { Audio(1, "旧名称") with { Connected = true } };
        await using var service = new NativeHeadsetService(() => peers, store);
        var candidates = (await service.EnumerateCandidatesAsync(default)).Candidates;
        Check((await service.SaveWhitelistAsync([candidates[0].Identity], default)).Saved);
        peers[0] = peers[0] with { Name = "新名称", Connected = false };
        var main = await service.EnumerateAsync(default);
        Check(main is [{ Name: "新名称", Connected: false }] && main[0].Identity == candidates[0].Identity);
    });
    await Test("unpaired entries disappear from the effective whitelist without deleting stored choices", async () =>
    {
        var store = Store("unpaired");
        var peers = new List<BluetoothCandidate> { Audio(1), Audio(2) };
        await using var service = new NativeHeadsetService(() => peers, store);
        var candidates = (await service.EnumerateCandidatesAsync(default)).Candidates;
        Check((await service.SaveWhitelistAsync([candidates[0].Identity], default)).Saved);
        peers[0] = peers[0] with { Authenticated = false };
        Check((await service.EnumerateAsync(default)).Count == 0 && store.Load().Count == 1);
        Check((await service.EnumerateCandidatesAsync(default)).Candidates.Count == 1);
    });
    await Test("saving revalidates every selection and never commits a partial choice", async () =>
    {
        string path = Path.Combine(scratch, "stale.json");
        var store = new DeviceWhitelistStore(path);
        var peers = new List<BluetoothCandidate> { Audio(1), Audio(2) };
        await using var service = new NativeHeadsetService(() => peers, store);
        var candidates = (await service.EnumerateCandidatesAsync(default)).Candidates;
        Check((await service.SaveWhitelistAsync([candidates[0].Identity], default)).Saved);
        string original = File.ReadAllText(path);
        peers.RemoveAt(1);
        var stale = await service.SaveWhitelistAsync(candidates.Select(device => device.Identity).ToArray(), default);
        Check(!stale.Saved && stale.Error is not null && File.ReadAllText(path) == original);
        Check(!(await service.SaveWhitelistAsync([], default)).Saved && File.ReadAllText(path) == original);
        Check(!(await service.SaveWhitelistAsync(["unknown-token"], default)).Saved && File.ReadAllText(path) == original);
    });
    await Test("saving cannot turn a keyboard or unauthenticated device into an audio choice", async () =>
    {
        var peers = new List<BluetoothCandidate> { Audio(1) };
        var store = Store("changed-class");
        await using var service = new NativeHeadsetService(() => peers, store);
        var candidates = (await service.EnumerateCandidatesAsync(default)).Candidates;
        peers[0] = peers[0] with { ClassOfDevice = 0x540 };
        Check(!(await service.SaveWhitelistAsync([candidates[0].Identity], default)).Saved && store.Load().Count == 0);
    });
    await Test("connect rechecks allowed membership before creating a control connection", async () =>
    {
        var store = Store("connect");
        var peers = new List<BluetoothCandidate> { Audio(1), Audio(2) };
        await using var service = new NativeHeadsetService(() => peers, store);
        var candidates = (await service.EnumerateCandidatesAsync(default)).Candidates;
        Check((await service.SaveWhitelistAsync([candidates[0].Identity], default)).Saved);
        await Throws<InvalidOperationException>(() => service.ConnectAsync(candidates[1].Identity, default));
        peers[0] = peers[0] with { Authenticated = false };
        await Throws<InvalidOperationException>(() => service.ConnectAsync(candidates[0].Identity, default));
    });
    await Test("enumeration failures preserve the prior file and are not empty results", async () =>
    {
        var store = Store("enumeration-failure");
        store.Save([DeviceWhitelistStore.KeyFor(1)]);
        await using var service = new NativeHeadsetService(() => throw new IOException("simulated failure"), store);
        await Throws<IOException>(() => service.EnumerateAsync(default));
        await Throws<IOException>(() => service.SaveWhitelistAsync(["session-token"], default));
        Check(store.Load().Count == 1);
    });
    await Test("malformed configuration fails explicitly and remains untouched", async () =>
    {
        string path = Path.Combine(scratch, "malformed.json");
        foreach (string invalid in new[] { "{", "{}", "{\"schemaVersion\":1,\"deviceKeys\":[\"not-a-key\"]}",
                     "{\"schemaVersion\":2,\"deviceKeys\":[]}" })
        {
            File.WriteAllText(path, invalid);
            await Throws<InvalidDataException>(() => Task.FromResult(new DeviceWhitelistStore(path).Load()));
            Check(File.ReadAllText(path) == invalid);
        }
    });
    await Test("failed atomic replacement leaves no temporary file and does not report success", async () =>
    {
        string target = Path.Combine(scratch, "blocked-target");
        var store = new DeviceWhitelistStore(target);
        await using var service = new NativeHeadsetService(() => [Audio(1)], store);
        var candidates = (await service.EnumerateCandidatesAsync(default)).Candidates;
        Directory.CreateDirectory(target);
        Check(!(await service.SaveWhitelistAsync([candidates[0].Identity], default)).Saved);
        Check(Directory.Exists(target) && !Directory.EnumerateFiles(scratch).Any(file => file.EndsWith(".tmp", StringComparison.Ordinal)));
    });
    await Test("management reports damaged settings and repairs them only after a valid save", async () =>
    {
        string path = Path.Combine(scratch, "repair.json");
        File.WriteAllText(path, "{damaged");
        var store = new DeviceWhitelistStore(path);
        await using var service = new NativeHeadsetService(() => [Audio(1)], store);
        await Throws<InvalidDataException>(() => service.EnumerateAsync(default));
        var catalog = await service.EnumerateCandidatesAsync(default);
        Check(catalog.SelectionWarning is not null && catalog.Candidates is [{ Enabled: false }]);
        Check(File.ReadAllText(path) == "{damaged");
        Check((await service.SaveWhitelistAsync([catalog.Candidates[0].Identity], default)).Saved);
        Check((await service.EnumerateCandidatesAsync(default)).SelectionWarning is null);
        Check((await service.EnumerateAsync(default)).Count == 1);
    });
    await Test("cancelled selection never enumerates or writes", async () =>
    {
        var store = Store("cancelled");
        await using var service = new NativeHeadsetService(() => throw new InvalidOperationException("must not enumerate"), store);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Throws<OperationCanceledException>(() => service.EnumerateAsync(cancellation.Token));
        await Throws<OperationCanceledException>(() => service.EnumerateCandidatesAsync(cancellation.Token));
        await Throws<OperationCanceledException>(() => service.SaveWhitelistAsync(["token"], cancellation.Token));
        Check(store.Load().Count == 0);
    });
    await HostWireTests.RunAsync(Test);
    await BackendTests.RunAsync(Test, scratch);
    await SupervisorDiagnosticsTests.RunAsync(Test, scratch);
    await CalibrationBackendTests.RunAsync(Test, scratch);
    await NativeCalibrationDescriptionTests.RunAsync(Test, scratch);
    await CalibrationAuditionTests.RunAsync(Test, scratch);
    await ResourceBackendTests.RunAsync(Test, scratch);
    await ResourceBackendTests.RunAppearanceAsync(Test, scratch);
    await NativeSnapshotMappingTests.RunAsync(Test, scratch);
    await AncBackendTests.RunAsync(Test, scratch);
    await AncPresentationTests.RunAsync(Test, scratch);
    await DeviceStateTests.RunAsync(Test, scratch);
    await EnergyTests.RunAsync(Test, scratch);
    await HostEfficiencyTests.RunAsync(Test, scratch);
    await DiagnosticIntegrationTests.RunAsync(Test, scratch);
    Console.WriteLine(JsonSerializer.Serialize(new { passed, success = true, nativeBluetoothCalls = 0 }));
    return 0;
}
catch (Exception failure)
{
    HeyMelody.Diagnostics.DiagnosticLog.Error("Tests", "runner.failed", "Host offline test runner failed", failure);
    Console.Error.WriteLine(failure);
    return 1;
}
finally
{
    string absoluteScratch = Path.GetFullPath(scratch);
    string temporaryRoot = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
    if (absoluteScratch.StartsWith(temporaryRoot, StringComparison.OrdinalIgnoreCase))
        Directory.Delete(absoluteScratch, recursive: true);
}

DeviceWhitelistStore Store(string name) => new(Path.Combine(scratch, name + ".json"));
static BluetoothCandidate Audio(ulong address, string name = "耳机") => new(address, name, false, true, true, 0x404);
async Task Test(string name, Func<Task> action) { await action(); passed++; Console.WriteLine("PASS " + name); }
static void Check(bool condition) { if (!condition) throw new InvalidOperationException("Assertion failed."); }
static async Task Throws<T>(Func<Task> action) where T : Exception
{
    try { await action(); } catch (T) { return; }
    throw new InvalidOperationException("Expected " + typeof(T).Name);
}
