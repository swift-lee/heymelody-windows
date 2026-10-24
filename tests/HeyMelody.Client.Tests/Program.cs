using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Json;
using HeyMelodyWindows.App;
using HeyMelodyWindows.Host;

if (args.Length == 3 && args[0] == "--fake-worker") return await FakeWorker(args[1], args[2]);

int passed = 0;
string scratch = Path.Combine(Path.GetTempPath(), "HeyMelody-ClientTests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(scratch);
try
{
    if (args is ["--diagnostics-tests"])
    {
        await DiagnosticBridgeTests.RunAsync(Test, scratch);
        Console.WriteLine(JsonSerializer.Serialize(new { passed, success = true, nativeBluetoothCalls = 0 }));
        return 0;
    }
    await Test("RPC rejects an incomplete EOF record", async () =>
        await Throws<InvalidDataException>(() => new RpcLineReader(new MemoryStream("{}"u8.ToArray())).ReadLineAsync(default).AsTask()));
    await Test("RPC bounds records before deserialization", async () =>
        await Throws<InvalidDataException>(() => new RpcLineReader(new MemoryStream(Encoding.UTF8.GetBytes(new string('a', RpcWire.MaximumLineCharacters + 1) + "\n"))).ReadLineAsync(default).AsTask()));
    await Test("RPC accepts its exact maximum", async () =>
        Check((await new RpcLineReader(new MemoryStream(Encoding.UTF8.GetBytes(new string('a', RpcWire.MaximumLineCharacters) + "\n"))).ReadLineAsync(default))?.Length == RpcWire.MaximumLineCharacters));
    await Test("isolated worker enumerates and reads without applying", async () =>
    {
        string counter = Counter("read");
        await using var bridge = Bridge("normal", counter);
        var devices = await bridge.EnumerateAsync(default);
        Check(devices.Count == 1 && devices[0].Identity == "opaque-peer");
        var snapshot = await bridge.ConnectAsync(devices[0].Identity, default);
        Check(snapshot.Connected && snapshot.HiRes == true);
        await bridge.RefreshAsync(default); await bridge.DisconnectAsync(default);
        Check(!File.Exists(counter));
    });
    await Test("every refresh scope crosses RPC and the legacy overload remains full", async () =>
    {
        string counter = Counter("scopes");
        await using var bridge = Bridge("scopes", counter);
        foreach (var scope in Enum.GetValues<RefreshScope>())
        {
            var snapshot = await bridge.RefreshAsync(scope, default);
            Check(snapshot.Connected && snapshot.ConnectionState == ConnectionState.Connected);
        }
        await bridge.RefreshAsync(default);
        Check(File.ReadAllLines(counter).SequenceEqual(Enum.GetValues<RefreshScope>()
            .Select(scope => "explicit:" + scope).Append("legacy:Full")));
    });
    await Test("device management keeps allowed state separate from connection and submits the complete selection", async () =>
    {
        string counter = Counter("device-management");
        await using var bridge = Bridge("device-management", counter);
        IDeviceManagementService management = bridge;
        var catalog = await management.EnumerateCandidatesAsync(default);
        var candidates = catalog.Candidates;
        Check(candidates.Count == 2 && candidates[0] is { Enabled: true, Connected: false }
            && candidates[1] is { Enabled: false, Connected: true });
        var result = await management.SaveWhitelistAsync(candidates.Select(device => device.Identity).ToArray(), default);
        Check(result.Saved && result.Error is null);
        Check(File.ReadAllText(counter).Trim() == "opaque-peer,other-peer");
        Check(!(await management.SaveWhitelistAsync([], default)).Saved);
    });
    await Test("stale whitelist selection failure crosses RPC without losing the worker", async () =>
    {
        await using var bridge = Bridge("device-management", Counter("stale-selection"));
        var result = await bridge.SaveWhitelistAsync(["removed-peer"], default);
        Check(!result.Saved && result.Error is not null);
        Check((await bridge.EnumerateCandidatesAsync(default)).Candidates.Count == 2);
    });
    await Test("catalog selection warning survives RPC for explicit repair", async () =>
    {
        await using var bridge = Bridge("damaged-whitelist", Counter("damaged-whitelist"));
        var catalog = await bridge.EnumerateCandidatesAsync(default);
        Check(catalog.SelectionWarning == "设备选择记录已损坏，请重新选择并保存。"
            && catalog.Candidates.All(device => !device.Enabled));
    });
    await Test("lost whitelist save receipt is not replayed", async () =>
    {
        string counter = Counter("save-eof");
        await using var bridge = Bridge("save-eof", counter);
        await Throws<IOException>(() => bridge.SaveWhitelistAsync(["opaque-peer"], default));
        Check(File.ReadAllLines(counter).Length == 1);
    });
    foreach (string mode in new[] { "eof", "wrong-id", "error", "malformed" })
        await Test("lost " + mode + " receipt is not replayed", async () =>
        {
            string counter = Counter(mode);
            await using var bridge = Bridge(mode, counter);
            var result = await bridge.ApplyAsync(new UiAction("GameMode", 0), default);
            Check(result.SubmissionAttempted && result.AcknowledgementUnknown && result.PartialEffects && result.After is null);
            Check(!result.BackendReceiptReceived && result.FailureKind == ApplyFailure.WorkerUnavailable
                && result.ConnectionState == ConnectionState.Unknown && result.ReadbackMatches is null);
            Check(File.ReadAllLines(counter).Length == 1);
        });
    await Test("worker timeout terminates an unresponsive process without replay", async () =>
    {
        string counter = Counter("hang");
        await using var bridge = Bridge("hang", counter, TimeSpan.FromSeconds(2));
        var timer = Stopwatch.StartNew();
        var result = await bridge.ApplyAsync(new UiAction("Spatial", 0), default);
        Check(timer.Elapsed < TimeSpan.FromSeconds(8));
        Check(result.SubmissionAttempted && result.AcknowledgementUnknown);
        Check(!result.BackendReceiptReceived && result.FailureKind == ApplyFailure.WorkerUnavailable
            && result.ConnectionState == ConnectionState.Unknown);
        Check(File.ReadAllLines(counter).Length == 1);
    });
    await Test("missing backend receipt cannot become a complete result or trigger replay", async () =>
    {
        string counter = Counter("missing-receipt");
        await using var bridge = Bridge("missing-receipt", counter);
        var result = await bridge.ApplyAsync(new UiAction("GameMode", 0), default);
        Check(!result.BackendReceiptReceived);
        // Preserve the received fields as evidence; only a complete backend receipt can
        // authorize confirmation or a retry in the operation coordinator.
        Check(result.Accepted && result.ReadbackMatches == true && result.TargetSubmitted);
        Check(File.ReadAllLines(counter).Length == 1);
    });
    await Test("unknown peer ACK with verified readback survives the process boundary", async () =>
    {
        string counter = Counter("unknown-ack");
        await using var bridge = Bridge("unknown-ack", counter);
        var result = await bridge.ApplyAsync(new UiAction("GameMode", 0), default);
        Check(!result.Accepted && result.ReadbackMatches == true && result.AcknowledgementUnknown && result.After?.GameMode == false);
        Check(result.BackendReceiptReceived && result.ConnectionState == ConnectionState.Connected
            && result.FailureKind == ApplyFailure.TransportUnknown && result.TargetSubmitted
            && !result.PrerequisiteSubmitted && result.AckStatus is null && !result.PartialEffects
            && result.After?.Identity == "opaque-peer" && result.After.ConnectionState == ConnectionState.Connected);
        Check(File.ReadAllLines(counter).Length == 1);
    });
    await Test("typed disconnection and partial prerequisite evidence survive RPC unchanged", async () =>
    {
        string counter = Counter("disconnected-receipt");
        await using var bridge = Bridge("disconnected-receipt", counter);
        var result = await bridge.ApplyAsync(new UiAction("Spatial", 1, AllowMutexChange: true), default);
        Check(result.BackendReceiptReceived && result.ConnectionState == ConnectionState.Disconnected
            && result.FailureKind == ApplyFailure.Disconnected && result.SubmissionAttempted
            && result.TargetSubmitted && result.PrerequisiteSubmitted && result.AckStatus is null
            && !result.Accepted && result.ReadbackMatches is null && result.AcknowledgementUnknown
            && result.PartialEffects && result.ObservedEqualizerId is null
            && result.After is { Connected: false, ConnectionState: ConnectionState.Disconnected, Identity: "opaque-peer" });
        Check(File.ReadAllLines(counter).Length == 1);
    });
    await VmTests.RunAsync(Test);
    await SharedBridgeTests.RunAsync(Test);
    await EventVmTests.RunAsync(Test);
    await SelectionPresentationTests.RunAsync(Test);
    await ResourceVmTests.RunAsync(Test);
    await FeatureInteractionTests.RunAsync(Test);
    await WriteQueueTests.RunAsync(Test);
    await ResourceVmTests.RunArtworkAsync(Test);
    await AncVmTests.RunAsync(Test);
    await DemoVmTests.RunAsync(Test);
    await SnapshotCloneTests.RunAsync(Test);
    await WireCompatTests.RunAsync(Test);
    await RpcLineReaderTests.RunAsync(Test);
    await DiagnosticBridgeTests.RunAsync(Test, scratch);
    Console.WriteLine(JsonSerializer.Serialize(new { passed, success = true, architecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(), nativeBluetoothCalls = 0 }, RpcWire.Json));
    return 0;
}
finally
{
    // The only recursive target is this exact GUID test directory created above.
    if (Path.GetFullPath(scratch).StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase))
        Directory.Delete(scratch, recursive: true);
}

string Counter(string name) => Path.Combine(scratch, name + ".txt");
LegacyWorkerBridge Bridge(string mode, string counter, TimeSpan? budget = null) => new(() =>
{
    string host = Environment.ProcessPath ?? throw new InvalidOperationException();
    var start = new ProcessStartInfo(host)
    {
        UseShellExecute = false, CreateNoWindow = true,
        RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
        StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = new UTF8Encoding(false)
    };
    if (Path.GetFileNameWithoutExtension(host).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
    start.ArgumentList.Add("--fake-worker"); start.ArgumentList.Add(mode); start.ArgumentList.Add(counter); return start;
}, budget);
async Task Test(string name, Func<Task> test)
{
    try { await test().WaitAsync(TimeSpan.FromSeconds(30)); }
    catch (TimeoutException error) { throw new TimeoutException(name + " timed out.", error); }
    passed++; Console.WriteLine("PASS " + name);
}
static void Check(bool value) { if (!value) throw new InvalidOperationException("Assertion failed."); }
static async Task Throws<T>(Func<Task> action) where T : Exception
{
    try { await action(); } catch (T) { return; } throw new InvalidOperationException("Expected " + typeof(T).Name);
}
static async Task<int> FakeWorker(string mode, string counter)
{
    Console.InputEncoding = new UTF8Encoding(false); Console.OutputEncoding = new UTF8Encoding(false);
    while (await Console.In.ReadLineAsync() is { } line)
    {
        var request = JsonSerializer.Deserialize<RpcRequest>(line, RpcWire.Json)!;
        if (request.Operation == "apply")
        {
            await File.AppendAllTextAsync(counter, "apply\n");
            if (mode == "eof") return 0;
            if (mode == "hang") { await Task.Delay(TimeSpan.FromSeconds(30)); return 0; }
            if (mode == "malformed") { Console.WriteLine("{}"); continue; }
        }
        if (mode == "scopes" && request.Operation == "refresh")
            await File.AppendAllTextAsync(counter, (request.RefreshScope.HasValue ? "explicit:" : "legacy:")
                + (request.RefreshScope ?? RefreshScope.Full) + "\n");
        if (request.Operation == "saveWhitelist")
        {
            await File.AppendAllTextAsync(counter, string.Join(",", request.DeviceIdentities ?? []) + "\n");
            if (mode == "save-eof") return 0;
        }
        var connected = new UiSnapshot { Identity = "opaque-peer", Connected = true,
            ConnectionState = ConnectionState.Connected, SupportedModel = true, HiRes = true, GameMode = false };
        object data = request.Operation switch
        {
            "enumerate" => new List<UiDevice> { new("opaque-peer", "OPPO Enco X4", true) },
            "enumerateCandidates" => new UiDeviceCatalog(new List<UiDeviceCandidate>
                { new("opaque-peer", "OPPO Enco X4", false, mode != "damaged-whitelist"), new("other-peer", "音箱", true, false) },
                mode == "damaged-whitelist" ? "设备选择记录已损坏，请重新选择并保存。" : null),
            "saveWhitelist" => request.DeviceIdentities is { Length: > 0 }
                && request.DeviceIdentities.All(identity => identity is "opaque-peer" or "other-peer")
                ? new UiWhitelistSaveResult(true) : new UiWhitelistSaveResult(false, "请重新选择设备。"),
            "connect" or "refresh" => connected,
            "apply" when mode == "missing-receipt" => new { Accepted = true, ReadbackMatches = true,
                TargetSubmitted = true, SubmissionAttempted = true, ConnectionState = ConnectionState.Connected, After = connected },
            "apply" when mode == "disconnected-receipt" => new UiApplyResult
            {
                BackendReceiptReceived = true, ConnectionState = ConnectionState.Disconnected,
                FailureKind = ApplyFailure.Disconnected, SubmissionAttempted = true,
                TargetSubmitted = true, PrerequisiteSubmitted = true, PartialEffects = true,
                AcknowledgementUnknown = true, Message = "The selected peer disconnected after a partial operation.",
                After = new UiSnapshot { Identity = "opaque-peer", ConnectionState = ConnectionState.Disconnected }
            },
            "apply" when mode == "unknown-ack" => new UiApplyResult
            {
                BackendReceiptReceived = true, ConnectionState = ConnectionState.Connected,
                FailureKind = ApplyFailure.TransportUnknown, SubmissionAttempted = true,
                TargetSubmitted = true, AcknowledgementUnknown = true, ReadbackMatches = true, After = connected
            },
            "apply" => new UiApplyResult
            {
                BackendReceiptReceived = true, ConnectionState = ConnectionState.Connected,
                TargetSubmitted = true, SubmissionAttempted = true, Accepted = true,
                AckStatus = 0, ReadbackMatches = true, After = connected
            },
            _ => true
        };
        var response = new RpcResponse
        {
            Id = request.Id + (mode == "wrong-id" ? 1 : 0), Success = mode != "error",
            Data = JsonSerializer.SerializeToElement(data, RpcWire.Json)
        };
        Console.WriteLine(JsonSerializer.Serialize(response, RpcWire.Json)); await Console.Out.FlushAsync();
    }
    return 0;
}
