using HeyMelody.Platform;
using System.IO.Pipes;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using HeyMelodyWindows.App;
using HeyMelodyWindows.Control;
using HeyMelodyWindows.Daily;
using HeyMelodyWindows.Devices;
using HeyMelodyWindows.Host;
using HeyMelodyWindows.Protocol;
using HeyMelodyWindows.Resources;

internal static class DeviceStateTests
{
    internal static async Task RunAsync(Func<string, Func<Task>, Task> test, string scratch)
    {
        await test("display store flushes its latest coalesced state atomically without persisting authority or telemetry", async () =>
        {
            string directory = DirectoryFor("atomic"); string path = Path.Combine(directory, "device-state.json");
            var store = new DeviceStateStore(path); string key = DeviceWhitelistStore.KeyFor(0x112233445566);
            Check(!File.Exists(path) && store.PreferredKey is null, "Reading an absent cache created state");
            for (int i = 0; i < 20; i++) store.Remember(key, DevicePresentation.From(Full("revision-" + i), DateTimeOffset.UtcNow));
            store.RememberSelection(key);
            store.Remember(key, DevicePresentation.From(Full("last"), DateTimeOffset.UtcNow));
            Check(store.Find(key)?.Firmware == "last", "Memory waited for disk persistence");
            await store.FlushAsync();
            var loaded = new DeviceStateStore(path);
            Check(loaded.PreferredKey == key && loaded.Find(key)?.Firmware == "last", "Shutdown flush lost the final state");
            string saved = File.ReadAllText(path); using var document = JsonDocument.Parse(saved);
            var forbidden = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "identity", "backendInstanceId", "deviceGeneration", "revision", "connected", "controlReady", "supportedModel", "failure",
                "canWrite", "canApply", "canSetAnc", "canFitTest", "canSetupHearing", "leftBattery", "rightBattery", "caseBattery",
                "caseLid", "leftPlacement", "rightPlacement", "observationFresh", "observationSource", "observationTime", "handle", "leaseId", "fullPath" };
            Check(!Names(document.RootElement).Any(forbidden.Contains), "Persistent DTO contains session, authority or live observation fields");
            Check(!saved.Contains("112233445566", StringComparison.Ordinal) && !saved.Contains("session-secret", StringComparison.Ordinal), "Persistent state exposed a physical address or session");
            Check(!Directory.EnumerateFiles(directory, "*.tmp").Any(), "Atomic writer left a temporary file");
        });
        await test("display store ignores corruption unsafe definitions and oversized files while keeping valid preference", async () =>
        {
            string directory = DirectoryFor("invalid"); string path = Path.Combine(directory, "device-state.json"); string key = DeviceWhitelistStore.KeyFor(1);
            var store = new DeviceStateStore(path); store.RememberSelection(key);
            store.Remember(key, DevicePresentation.From(Full("good"), DateTimeOffset.UtcNow)); await store.FlushAsync();
            var unsafeData = JsonNode.Parse(File.ReadAllText(path))!;
            unsafeData["devices"]![0]!["presentation"]!["featureSections"]![0]!["controls"]![0]!["kind"] = "unsupported-kind";
            File.WriteAllText(path, unsafeData.ToJsonString());
            var loaded = new DeviceStateStore(path);
            Check(loaded.PreferredKey == key && loaded.Find(key) is null, "Malformed control definitions reached the UI or erased the independent preference");
            File.WriteAllText(path, "{"); loaded = new(path);
            Check(loaded.PreferredKey is null && loaded.Find(key) is null && File.ReadAllText(path) == "{", "Damaged cache was treated as mandatory configuration");
            using (var large = new FileStream(path, FileMode.Create)) large.SetLength(16 * 1024 * 1024 + 1);
            Check(new DeviceStateStore(path).PreferredKey is null, "Oversized file was accepted");
            File.Delete(path); loaded = new(path);
            var tooLong = DevicePresentation.From(Full("large"), DateTimeOffset.UtcNow) with { Warnings = [new string('x', 4097)] };
            loaded.Remember(key, tooLong); await loaded.FlushAsync();
            Check(loaded.Find(key) is null && !File.Exists(path), "Oversized entry was scheduled for persistence");
        });
        await test("display store restart keeps the 60 s rewrite throttle for content already on disk", async () =>
        {
            string directory = DirectoryFor("restart-throttle"); string path = Path.Combine(directory, "device-state.json");
            string key = DeviceWhitelistStore.KeyFor(0x0A0B0C0D0E0F); var sample = DateTimeOffset.UtcNow;
            var first = new DeviceStateStore(path); first.Remember(key, At("same", sample)); await first.FlushAsync();
            DevicePresentation At(string firmware, DateTimeOffset time) => DevicePresentation.From(Full(firmware), time) with { SampleTime = time };
            DateTimeOffset Saved() => JsonNode.Parse(File.ReadAllText(path))!["devices"]![0]!["presentation"]!["sampleTime"]!.GetValue<DateTimeOffset>();
            Check(Saved() == sample, "Setup did not persist the first sample");
            var restarted = new DeviceStateStore(path);
            restarted.Remember(key, At("same", sample.AddSeconds(30))); await Task.Delay(400);
            Check(Saved() == sample && restarted.Find(key)?.SampleTime == sample.AddSeconds(30), "An unchanged re-read after restart rewrote the file");
            await restarted.FlushAsync();
            Check(Saved() == sample.AddSeconds(30), "Shutdown flush dropped the throttled sample time");
            var late = new DeviceStateStore(path);
            late.Remember(key, At("same", sample.AddSeconds(91))); await Task.Delay(400);
            Check(Saved() == sample.AddSeconds(91), "A sample 60 s after the saved one was not written after restart");
            var changed = new DeviceStateStore(path);
            changed.Remember(key, At("changed", sample.AddSeconds(92))); await Task.Delay(400);
            Check(Saved() == sample.AddSeconds(92) && new DeviceStateStore(path).Find(key)?.Firmware == "changed", "Changed content after restart was throttled");
        });
        await test("main selection persists through host restart rename disconnect and fresh catalogs without internal reads changing it", async () =>
        {
            string directory = DirectoryFor("selection"); var whitelist = Whitelist(directory, 1, 2);
            var devices = new List<BluetoothCandidate> { Audio(1, false), Audio(2, false) }; string oldIdentity;
            await using (var backend = Backend(whitelist, devices, () => new FakePeer(Full("unused"))))
            {
                await backend.InitializeAsync(default); var first = backend.Devices()[0]; var second = backend.Devices()[1]; oldIdentity = second.Identity;
                backend.Select(second.Identity, rememberSelection: true);
                backend.Select(first.Identity); await backend.RefreshAsync(first.Identity, RefreshScope.Full, default); backend.Pause(first.Identity); backend.Resume(first.Identity);
                devices[1] = devices[1] with { Name = "renamed" };
                await backend.RefreshCatalogAsync(default);
                Check(backend.Devices().Single(d => d.IsPreferredSelection).Identity == second.Identity, "Internal reads, pause or resume replaced the preference");
                Check(backend.Subscribe().Devices.Single(d => d.IsPreferredSelection).Identity == second.Identity, "Subscription omitted selection preference");
            }
            await using var restarted = Backend(whitelist, devices, () => new FakePeer(Full("unused")));
            await restarted.InitializeAsync(default);
            var selected = restarted.Devices().Single(d => d.IsPreferredSelection);
            Check(selected is { Name: "renamed", Connected: false } && selected.Identity != oldIdentity, "Preference depended on name connection or previous session identity");
            devices.RemoveAt(1); await restarted.RefreshCatalogAsync(default);
            Check(restarted.Devices().All(d => !d.IsPreferredSelection), "Unpaired preference selected another physical device");
        });
        await test("pipe main select and connect remember preference but observer select has no persistence side effect", async () =>
        {
            string directory = DirectoryFor("pipe-role"); var devices = new List<BluetoothCandidate> { Audio(1, false), Audio(2, false) };
            await using var backend = Backend(Whitelist(directory, 1, 2), devices, () => new FakePeer(Full("unused")));
            await backend.InitializeAsync(default); var ids = backend.Devices().Select(d => d.Identity).ToArray();
            string pipeName = "HeyMelody-state-test-" + Guid.NewGuid().ToString("N");
            await using var server = new BackendPipeServer(backend, pipeName); server.Start();
            await using var main = await PipeClient.Connect(pipeName); await using var observer = await PipeClient.Connect(pipeName);
            Check((await main.Request(new() { Id = 1, Operation = "subscribe", Role = "main" })).Success, "Main subscription failed");
            Check((await observer.Request(new() { Id = 2, Operation = "subscribe", Role = "observer" })).Success, "Observer subscription failed");
            Check((await main.Request(new() { Id = 3, Operation = "select", Identity = ids[1] })).Success, "Main selection failed");
            Check((await observer.Request(new() { Id = 4, Operation = "select", Identity = ids[0] })).Success, "Observer read failed");
            Check(backend.Devices().Single(d => d.IsPreferredSelection).Identity == ids[1], "Observer overwrote main preference");
            Check((await main.Request(new() { Id = 5, Operation = "connect", Identity = ids[0] })).Success
                && backend.Devices().Single(d => d.IsPreferredSelection).Identity == ids[0], "Legacy main connect did not remember selection");
            Check(!(await main.Request(new() { Id = 6, Operation = "select", Identity = "invalid" })).Success
                && backend.Devices().Single(d => d.IsPreferredSelection).Identity == ids[0], "Failed selection corrupted preference");
        });
        await test("disconnect and restart retain complete definitions values EQ and hearing metadata but never restore authority or live observations", async () =>
        {
            string directory = DirectoryFor("offline"); var devices = new List<BluetoothCandidate> { Audio(1, true) };
            var whitelist = Whitelist(directory, 1); var worker = new FakePeer(Full("original")); string oldIdentity; DateTimeOffset sampleTime;
            await using (var backend = Backend(whitelist, devices, () => worker))
            {
                int opened = 0; backend.EventPublished += e => { if (e.Kind == "caseOpen") opened++; };
                await backend.InitializeAsync(default); oldIdentity = backend.Devices().Single().Identity;
                await Until(() => backend.Select(oldIdentity).ControlReady); var live = backend.Select(oldIdentity); sampleTime = live.SampleTime;
                var profiles = await backend.HearingProfilesAsync(oldIdentity, live.DeviceGeneration, default);
                Check(profiles is [{ Uid: 8 }], "Direct hearing metadata refresh failed");
                backend.ObserveConnection(1, false); var cached = backend.Select(oldIdentity);
                CheckCached(cached, "original", sampleTime); Check(cached.HearingProfiles is [{ Uid: 8, CanApply: false }], "Direct hearing metadata was not cached");
                Check(opened == 1, "Restoring historical case state triggered an open event");
                var rejected = await backend.ApplyAsync(oldIdentity, live.DeviceGeneration, new("GameMode", 0), default);
                Check(rejected.FailureKind == ApplyFailure.StateUnavailable && !rejected.TargetSubmitted && worker.Applies == 0, "Cache granted a write");
                // A new BLE battery observation remains live while configuration is cached.
                var now = DateTimeOffset.UtcNow;
                backend.ObserveBroadcast(1, new("06EC10", CaseLidState.Unknown, EarbudPlacement.Unknown,
                    EarbudPlacement.Unknown, 12, null, null, false, null, null, now) { LeftBatteryTime = now });
                var ble = backend.Select(oldIdentity);
                Check(ble.PresentationCached && ble.ObservationFresh && ble.LeftBattery == 12 && ble.RightBattery is null && !ble.ControlReady,
                    "Cache hid live BLE or supplied historical telemetry");
            }
            devices[0] = Audio(1, false);
            await using var restarted = Backend(whitelist, devices, () => throw new InvalidOperationException("Offline cache started a worker"));
            int restartedOpen = 0; restarted.EventPublished += e => { if (e.Kind == "caseOpen") restartedOpen++; };
            await restarted.InitializeAsync(default); var restored = restarted.Subscribe().Snapshots.Single();
            CheckCached(restored, "original", sampleTime);
            Check(restored.Identity != oldIdentity && restored.BackendInstanceId == restarted.InstanceId && restartedOpen == 0,
                "Cache restored old session identity or emitted a historical event");
        });
        await test("failed reads and same-product unsupported reconnect preserve last good display while fresh full snapshots replace removed features", async () =>
        {
            string directory = DirectoryFor("replace"); var devices = new List<BluetoothCandidate> { Audio(1, true) };
            var workers = new List<FakePeer>(); var seeds = new Queue<UiSnapshot>(); seeds.Enqueue(Full("before"));
            var unsupported = new UiSnapshot { ProductId = "06EC10", Connected = true, Failure = "unsupported", SupportedModel = false }; seeds.Enqueue(unsupported);
            var upgraded = Full("after"); upgraded.FeatureSections = []; upgraded.AncModes = []; upgraded.AncOptions = [];
            upgraded.EqualizerEntries = []; upgraded.EqualizerOptions = []; upgraded.HearingProfiles = []; upgraded.HearingEnabled = null;
            seeds.Enqueue(upgraded);
            await using var backend = Backend(Whitelist(directory, 1), devices, () => { var w = new FakePeer(seeds.Dequeue()); workers.Add(w); return w; });
            await backend.InitializeAsync(default); string id = backend.Devices().Single().Identity; await Until(() => backend.Select(id).ControlReady);
            workers[0].Emit(s => { s.ControlReady = false; s.Failure = "read failed"; s.FeatureSections = []; s.Firmware = "failed"; });
            backend.ObserveConnection(1, false);
            Check(backend.Select(id) is { PresentationCached: true, Firmware: "before" } && backend.Select(id).FeatureSections.Count != 0,
                "Failed read overwrote complete cache");
            backend.ObserveConnection(1, true); await Until(() => backend.Select(id).MonitoringState == MonitoringState.Unsupported);
            Check(backend.Select(id) is { PresentationCached: true, Firmware: "before", ControlReady: false }, "Unready same-product snapshot replaced cached display");
            backend.ObserveConnection(1, false); backend.ObserveConnection(1, true); backend.Resume(id);
            await Until(() => backend.Select(id).ControlReady && backend.Select(id).Firmware == "after");
            var live = backend.Select(id); Check(!live.PresentationCached && live.FeatureSections.Count == 0 && live.EqualizerEntries.Count == 0 && live.HearingProfiles.Count == 0,
                "New full snapshot merged obsolete entries instead of replacing them");
            backend.ObserveConnection(1, false); var cached = backend.Select(id);
            Check(cached is { PresentationCached: true, Firmware: "after" } && cached.FeatureSections.Count == 0
                && cached.AncOptions.Count == 0 && cached.EqualizerOptions.Count == 0 && cached.EqualizerEntries.Count == 0 && cached.HearingProfiles.Count == 0,
                "Empty lists did not retire removed features in persisted presentation");
            workers[0].Emit(s => { s.ControlReady = true; s.Failure = null; s.Firmware = "stale-generation"; });
            Check(backend.Select(id).Firmware == "after", "Late old-generation callback poisoned display history");
        });
        await test("verified product replacement retires older model history", async () =>
        {
            string directory = DirectoryFor("product"); string key = DeviceWhitelistStore.KeyFor(1);
            var state = new DeviceStateStore(Path.Combine(directory, "device-state.json"));
            state.Remember(key, DevicePresentation.From(Full("old-model"), DateTimeOffset.UtcNow));
            var replacement = Full("new-model"); replacement.ProductId = "063C10";
            await using var backend = Backend(Whitelist(directory, 1), [Audio(1, true)], () => new FakePeer(replacement), state);
            await backend.InitializeAsync(default); string id = backend.Devices().Single().Identity; await Until(() => backend.Select(id).ControlReady);
            backend.ObserveConnection(1, false);
            Check(backend.Select(id) is { PresentationCached: true, ProductId: "063C10", Firmware: "new-model" }
                && state.Find(key)?.ProductId == "063C10", "Old model capabilities survived verified product change");
        });
        await test("legacy display history migrates only HeyMelody presentation and preserves device preference", async () =>
        {
            string directory = DirectoryFor("migration"), path = Path.Combine(directory, "device-state.json");
            string key = DeviceWhitelistStore.KeyFor(1), other = DeviceWhitelistStore.KeyFor(2);
            var good = DevicePresentation.From(Full("hey-good"), DateTimeOffset.UtcNow);
            var retired = DevicePresentation.From(Full("retired"), DateTimeOffset.UtcNow);
            File.WriteAllText(path, JsonSerializer.Serialize(new
            {
                schemaVersion = 1, preferredDeviceKey = other,
                devices = new[]
                {
                    new { key, source = "HeyMelody", presentation = good },
                    new { key, source = "WirelessEarphones", presentation = retired },
                    new { key = other, source = "WirelessEarphones", presentation = retired }
                }
            }, RpcWire.Json));
            var state = new DeviceStateStore(path);
            Check(state.Find(key)?.Firmware == "hey-good" && state.Find(other) is null,
                "Legacy presentation was merged or relabelled");
            Check(state.PreferredKey == other, "Migration erased the independent preferred device identity");
            await state.FlushAsync(); using var migrated = JsonDocument.Parse(File.ReadAllText(path));
            Check(migrated.RootElement.GetProperty("schemaVersion").GetInt32() == 2
                && migrated.RootElement.GetProperty("devices").GetArrayLength() == 1
                && !Names(migrated.RootElement).Contains("source", StringComparer.OrdinalIgnoreCase), "Migration retained the legacy schema or provider field");
            var reloaded = new DeviceStateStore(path);
            Check(reloaded.Find(key)?.Firmware == "hey-good" && reloaded.Find(other) is null && reloaded.PreferredKey == other,
                "Migrated history changed after restart");
        });
        await test("a peer discovered while catalog adoption waits for commands is retired and restarted with the updated catalog", async () =>
        {
            string directory = DirectoryFor("catalog-discovery-race"); var devices = new List<BluetoothCandidate> { Audio(1, true) };
            var workers = new List<FakePeer>(); var network = new CatalogNetwork(); using var http = new HttpClient(network);
            var coordinator = new ResourceCoordinator(Path.Combine(directory, "resources"), http);
            await coordinator.InitializeAsync(); await coordinator.UpdatePreferencesAsync(coordinator.Preferences with { UseOnlineResources = false });
            await using var backend = new BackendSupervisor(Whitelist(directory, 1, 2), _ => Task.FromResult<IReadOnlyList<BluetoothCandidate>>(devices),
                () => { var worker = new FakePeer(Full("catalog")) { StampCatalogVersion = true }; workers.Add(worker); return worker; }, resources: coordinator);
            int oldCatalogSnapshots = 0; long adoptedRevision = long.MaxValue;
            int oldVersion = coordinator.Catalog.ContentVersion, newVersion = oldVersion + 1;
            backend.EventPublished += e =>
            {
                if (e.Snapshot is { ControlReady: true } s && s.ResourceRevision >= adoptedRevision && s.Firmware == oldVersion.ToString())
                    Interlocked.Increment(ref oldCatalogSnapshots);
            };
            await backend.InitializeAsync(default); string firstId = backend.Devices().Single().Identity;
            await Until(() => backend.Select(firstId).ControlReady); var before = backend.Select(firstId); var firstWorker = workers.Single();
            firstWorker.HoldApply = true;
            var submitted = backend.ApplyAsync(firstId, before.DeviceGeneration, new("GameMode", 1), default);
            await firstWorker.ApplyEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            // Hold the real adoption gate so the resource Changed callback cannot race
            // this test's exact placement of the additional catalog observation.
            var gate = (SemaphoreSlim)typeof(BackendSupervisor).GetField("_resourceGate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(backend)!;
            await gate.WaitAsync();
            try
            {
                var node = JsonNode.Parse(coordinator.Catalog.Raw.GetRawText())!.AsObject(); node["versionCode"] = newVersion;
                network.Configuration = node.ToJsonString(); network.Version = newVersion;
                await coordinator.UpdatePreferencesAsync(coordinator.Preferences with { UseOnlineResources = true });
                Check(await coordinator.RefreshAsync(true), "Updated catalog was not validated");
                adoptedRevision = coordinator.Revision;
                // Calling an async method synchronously runs through its first pending
                // await. The first peer's held command proves the old peer set was taken.
                var adopting = (Task)typeof(BackendSupervisor).GetMethod("AdoptResourcesAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(backend, [true, CancellationToken.None])!;
                Check(!adopting.IsCompleted, "Adoption was not waiting for the submitted command");
                devices.Add(Audio(2, true)); await backend.RefreshCatalogAsync(default);
                string secondId = backend.Devices().Single(d => d.Identity != firstId).Identity;
                await Until(() => workers.Any(w => w.Address == 2)); var oldSecond = workers.Single(w => w.Address == 2);
                var blocked = backend.Select(secondId);
                Check(oldSecond.CatalogVersion == oldVersion && !blocked.ControlReady, "Newly discovered peer escaped the active transition");
                var rejected = await backend.ApplyAsync(secondId, blocked.DeviceGeneration, new("GameMode", 1), default);
                Check(!rejected.TargetSubmitted && oldSecond.Applies == 0, "Newly discovered peer submitted a SET while transition waited");
                firstWorker.ApplyRelease.TrySetResult(); var receipt = await submitted;
                await adopting.WaitAsync(TimeSpan.FromSeconds(5));
                await Until(() => backend.Subscribe().Snapshots.Count(s => s.ControlReady && s.Firmware == newVersion.ToString()) == 2);
                Check(receipt.TargetSubmitted && firstWorker.Applies == 1, "The submitted pre-transition command was not allowed to finish");
                Check(oldSecond.Disposals == 1 && workers.Count(w => w.Address == 2) == 2
                    && workers.Last(w => w.Address == 2).CatalogVersion == newVersion, "Additional peer retained its old-catalog worker");
                Check(backend.Select(secondId).DeviceGeneration != blocked.DeviceGeneration
                    && backend.Select(secondId).Firmware == newVersion.ToString() && oldCatalogSnapshots == 0,
                    "Additional peer retained its generation or published outdated presentation after adopting the new catalog");
                Check(workers.Where(w => w != firstWorker).All(w => w.Applies == 0), "Resource adoption issued an extra SET");
            }
            finally { firstWorker.ApplyRelease.TrySetResult(); gate.Release(); }
        });
        await test("retired-generation SPP cannot supply presentation failure or authority with or without a cache", async () =>
        {
            string directory = DirectoryFor("generation-guard"); string key = DeviceWhitelistStore.KeyFor(1);
            var state = new DeviceStateStore(Path.Combine(directory, "device-state.json")); var worker = new FakePeer(Full("current"));
            await using var backend = Backend(Whitelist(directory, 1), [Audio(1, true)], () => worker, state);
            await backend.InitializeAsync(default); string id = backend.Devices().Single().Identity; await Until(() => backend.Select(id).ControlReady);
            backend.ObserveConnection(1, false);
            worker.Emit(s => { s.Firmware = "retired"; s.Failure = "retired-failure"; s.GameMode = true; });
            var cached = backend.Select(id);
            Check(cached is { PresentationCached: true, Firmware: "current", Failure: null,
                    SupportedModel: false, ControlReady: false, GameMode: false }, "Retired SPP overlaid current cache or live verification");
            Check(state.Find(key)?.Firmware == "current", "Retired observation was persisted as current presentation");
            state.RetireOtherProducts(key, "FFFFFF");
            // Store changes do not republish an existing snapshot. Resume while the OS
            // still reports disconnected publishes the empty state without opening a worker.
            var cleared = backend.Resume(id);
            worker.Emit(s => s.Failure = null);
            var empty = backend.Select(id);
            Check(empty.Revision == cleared.Revision
                && empty is { PresentationCached: false, Firmware: null, ProductId: null, Failure: null, SupportedModel: false, ControlReady: false, GameMode: null }
                && empty.FeatureSections.Count == 0 && empty.AncOptions.Count == 0 && empty.EqualizerEntries.Count == 0,
                "Retired SPP was copied as a no-cache fallback");
        });
        await test("real bundled X4, Buds3 and Free4 projections preserve discrete audio options and independent controls", async () =>
        {
            var bundled = new BundledResourceStore(); string directory = DirectoryFor("profiles"); var state = new DeviceStateStore(Path.Combine(directory, "device-state.json"));
            foreach (string productId in new[] { "06EC10", "063C14", "06C010" })
            {
                var resource = bundled.GetCatalog().Find(productId) ?? throw new InvalidOperationException("Bundled profile missing");
                var profile = DeviceProfile.FromResource(resource.Raw, "fixture");
                await using var client = new DailyClient(new NeverConnector());
                typeof(DailyClient).GetField("_profile", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(client, profile);
                typeof(DailyClient).GetField("_commands", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(client, CommandCapabilities.Parse(Enumerable.Repeat((byte)255, 9).ToArray()));
                var daily = new DailySnapshot { Revision = 1, Connected = true, ProfileVerified = true, ProductId = productId,
                    FirmwareVersion = "123.123.123", ObservedAtUtc = DateTimeOffset.UtcNow, CustomEqualizerCapacity = profile.CustomEqualizerCapacity,
                    EqualizerFrequencies = profile.CustomEqFrequencies.Select(x => (ushort)x).ToArray(),
                    RawFeatures = Enumerable.Range(0, 64).ToDictionary(x => (byte)x, x => x == 0x18 ? (byte)1 : (byte)0), SpatialMode = 1,
                    BassEngine = new(-6, 6, 2), PromptVolume = 5,
                    GameSound = productId == "063C14" ? new(1, new byte[] { 0, 1 }) : new(3, new byte[] { 0, 3 }),
                    Gestures = [new(1, 0, 1, 1), new(2, 0, 1, 1), new(1, 0, 2, 6), new(2, 0, 2, 6)],
                    AncOptions = profile.NoiseReductionModes.SelectMany(p => (p.Children.Count == 0 ? new[] { p } : p.Children).Select(c =>
                        new AncOption((byte)p.ProtocolIndex, (byte)c.ProtocolIndex, c.ModeType, "模式"))).ToList() };
                typeof(DailyClient).GetMethod("ResolveCapabilities", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(client, [daily]);
                await using var native = new NativeHeadsetService(() => throw new InvalidOperationException("Projection enumerated Bluetooth"), Whitelist(directory, 1));
                var ui = (UiSnapshot)typeof(NativeHeadsetService).GetMethod("Map", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(native, [daily])!;
                Check(ui.FeatureSections.Count > 0, "Real profile produced no controls");
                var controls = ui.FeatureSections.SelectMany(section => section.Controls).ToArray();
                var hires = controls.Single(control => control.Id == "hires");
                var spatial = controls.Single(control => control.Id == "spatial");
                Check(hires is { Value: 1, CanWrite: true } && spatial is { Value: 1, CanWrite: true },
                    "Host projection must keep Hi-Res and spatial independently writable and preserve both observed values.");
                if (productId == "06EC10")
                    Check(spatial.Maximum == 1 && spatial.Options!.Any(option => option.Key == 2),
                        "Spatial mode 2 is a discrete option even when numeric bounds default to 0..1.");
                if (productId != "06C010")
                {
                    int type = productId == "063C14" ? 1 : 3;
                    var gameType = controls.Single(control => control.Id == "game-sound");
                    Check(gameType.CanWrite && gameType.Value == type && gameType.Maximum == 1
                        && gameType.Options!.Select(option => option.Key).SequenceEqual(new[] { type, 0 })
                        && gameType.Options!.Single(option => option.Key == 0).Name == "关闭"
                        && controls.Single(control => control.Id == "game-sound-enabled") is { Value: 0, CanWrite: true },
                        "HeyMelody must retain its model's game IDs with type selection available while the switch is off.");
                }
                string key = DeviceWhitelistStore.KeyFor(Convert.ToUInt64(productId, 16));
                state.Remember(key, DevicePresentation.From(ui, DateTimeOffset.UtcNow));
                var stored = state.Find(key)?.ToSnapshot();
                Check(stored is not null && stored.FeatureSections.SelectMany(s => s.Controls).Select(c => c.Id)
                    .SequenceEqual(ui.FeatureSections.SelectMany(s => s.Controls).Select(c => c.Id)), "Validation silently dropped real native definitions");
            }
            await state.FlushAsync();
        });

        string DirectoryFor(string label) { string path = Path.Combine(scratch, "device-state-" + label + "-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(path); return path; }
    }

    private static DeviceWhitelistStore Whitelist(string directory, params ulong[] addresses)
    { var store = new DeviceWhitelistStore(Path.Combine(directory, "devices.json")); store.Save(addresses.Select(DeviceWhitelistStore.KeyFor)); return store; }
    private static BackendSupervisor Backend(DeviceWhitelistStore whitelist, IReadOnlyList<BluetoothCandidate> devices,
        Func<IPeerWorker> factory, DeviceStateStore? state = null) => new(whitelist, _ => Task.FromResult(devices), factory, deviceState: state);
    private static BluetoothCandidate Audio(ulong address, bool connected) => new(address, "耳机 " + address, connected, true, true, 0x404);
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static async Task Until(Func<bool> condition)
    { using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(5)); while (!condition()) await Task.Delay(10, budget.Token); }
    private static IEnumerable<string> Names(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
            foreach (var p in element.EnumerateObject()) { yield return p.Name; foreach (string name in Names(p.Value)) yield return name; }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var item in element.EnumerateArray()) foreach (string name in Names(item)) yield return name;
    }
    private static UiSnapshot Copy(UiSnapshot snapshot) => JsonSerializer.Deserialize<UiSnapshot>(JsonSerializer.Serialize(snapshot, RpcWire.Json), RpcWire.Json)!;
    private static UiSnapshot Full(string firmware) => new()
    {
        Identity = "session-secret", BackendInstanceId = "old-backend", DeviceGeneration = 19, Revision = 1, Connected = true,
        ControlReady = true, SupportedModel = true, ProductId = "06EC10", Color = 3, Firmware = firmware,
        SampleTime = new DateTimeOffset(2026, 10, 9, 10, 0, 0, TimeSpan.Zero),
        LeftBattery = 80, RightBattery = 90, CaseBattery = 50, LeftCharging = true, CaseLid = CaseLidState.Open,
        CaseObservationTime = DateTimeOffset.UtcNow, LeftBatteryObservationTime = DateTimeOffset.UtcNow, LeftPlacement = EarbudPlacement.InEar,
        CanSetAnc = true, CanSetAncMode = true, CanSetEqualizer = true, CanSetCustomEqualizer = true,
        CanSetSpatial = true, CanSetDualConnection = true, CanSetGameMode = true, CanSetHiRes = true, CanFitTest = true, CanSetupHearing = true,
        Anc = 0x0102, AncPrimary = 1, AncOptions = [new(0x0102, "深度"), new(0x0103, "轻度")],
        AncModes = [new(1, "降噪", "anc", [new(0x0102, "深度"), new(0x0103, "轻度")])],
        Equalizer = 2, EqualizerOptions = [new(2, "均衡"), new(3, "人声")], CustomEqualizerCapacity = 7,
        EqualizerFrequencies = [100, 1000], EqualizerEntries = [new(6, "已保存 EQ", -6, 6, true, [new(100, -2), new(1000, 3)])],
        Spatial = 1, SpatialOptions = [new(0, "关闭"), new(1, "固定")], SpatialHiResMutex = true,
        DualConnection = true, GameMode = false, HiRes = true, HearingEnabled = true, HearingProfiles = [new(4, "听感", true, true)],
        FeatureSections = [new("controls", "全部类型", true,
        [new("switch", "开关", "Switch", 1, CanWrite: true), new("slider", "强度", "Slider", 4, Minimum: -6, Maximum: 6, Step: 2, CanWrite: true),
            new("radio", "模式", "Radio", 9, Options: [new(7, "A"), new(9, "B")], CanWrite: true),
            new("combo", "选择", "Combo", 7, Options: [new(7, "配置")], CanWrite: true),
            new("text", "文本", "Text", Text: "已保存文本", CanWrite: true, Description: "说明")], "分组说明")], Warnings = ["已验证的显示说明"]
    };
    private static void CheckCached(UiSnapshot snapshot, string firmware, DateTimeOffset sample)
    {
        Check(snapshot.PresentationCached && snapshot.PresentationSampleTime == sample && snapshot.Firmware == firmware && snapshot.ProductId == "06EC10" && snapshot.Color == 3,
            "Cached identity display or reading time changed");
        Check(!snapshot.ControlReady && !snapshot.CanSetAnc && !snapshot.CanSetAncMode && !snapshot.CanSetEqualizer && !snapshot.CanSetCustomEqualizer
            && !snapshot.CanSetDualConnection && !snapshot.CanSetGameMode && !snapshot.CanSetHiRes && !snapshot.CanSetSpatial && !snapshot.CanFitTest && !snapshot.CanSetupHearing,
            "Offline cached state contains control authority");
        Check(!snapshot.ObservationFresh && snapshot.ObservationSource is null && snapshot.LeftBattery is null && snapshot.RightBattery is null && snapshot.CaseBattery is null
            && snapshot.CaseLid == CaseLidState.Unknown && snapshot.LeftPlacement == EarbudPlacement.Unknown && snapshot.CaseObservationTime is null,
            "Cache restored historical transport observations");
        Check(snapshot.Anc == 0x0102 && snapshot.AncPrimary == 1 && snapshot.AncModes.Single().Options.Count == 2
            && snapshot.Equalizer == 2 && snapshot.EqualizerOptions.Count == 2 && snapshot.CustomEqualizerCapacity == 7
            && snapshot.EqualizerFrequencies.SequenceEqual(new[] { 100, 1000 }) && snapshot.EqualizerEntries.Single().Bands.Select(b => b.Gain).SequenceEqual(new[] { -2, 3 })
            && snapshot.Spatial == 1 && snapshot.SpatialOptions.Count == 2 && snapshot.SpatialHiResMutex && snapshot.DualConnection == true && snapshot.GameMode == false && snapshot.HiRes == true,
            "Cache omitted current values or complete options/EQ");
        var controls = snapshot.FeatureSections.Single().Controls;
        Check(controls.Select(c => c.Kind).SequenceEqual(new[] { "Switch", "Slider", "Radio", "Combo", "Text" }) && controls.All(c => !c.CanWrite)
            && controls[1] is { Value: 4, Minimum: -6, Maximum: 6, Step: 2 } && controls[2].Options!.Count == 2 && controls[4].Text == "已保存文本"
            && snapshot.HearingEnabled == true && snapshot.HearingProfiles.All(p => !p.CanApply), "Cached dynamic definitions or hearing permissions changed");
    }

    private sealed class FakePeer : IPeerWorker, IResourcePeerWorker, ICalibrationPeerWorker
    {
        private readonly Func<UiSnapshot> _seed;
        private UiSnapshot _snapshot = new();
        internal int Applies, Disposals;
        internal ulong Address;
        internal bool HoldApply, StampCatalogVersion;
        internal int CatalogVersion;
        internal readonly TaskCompletionSource ApplyEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource ApplyRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal FakePeer(UiSnapshot seed) : this(() => seed) { }
        internal FakePeer(Func<UiSnapshot> seed) { _seed = seed; }
        public event Action<UiSnapshot>? SnapshotChanged;
        public event Action? Unavailable { add { } remove { } }
        public event Action<UiCalibrationState>? CalibrationChanged { add { } remove { } }
        public event Action<UiCalibrationSpectrum>? CalibrationSpectrumChanged { add { } remove { } }
        public Task<UiSnapshot> InitializeAsync(ulong address, string identity, string name, CancellationToken token)
        { Address = address; _snapshot = Copy(_seed()); _snapshot.Identity = identity; _snapshot.DeviceName = name; _snapshot.Revision = 1;
            if (StampCatalogVersion) _snapshot.Firmware = CatalogVersion.ToString();
            SnapshotChanged?.Invoke(_snapshot); return Task.FromResult(_snapshot); }
        public Task<UiSnapshot> RefreshAsync(RefreshScope scope, CancellationToken token) => Task.FromResult(Copy(_snapshot));
        public async Task<UiApplyResult> ApplyAsync(UiAction action, CancellationToken token)
        {
            Applies++; ApplyEntered.TrySetResult(); if (HoldApply) await ApplyRelease.Task.WaitAsync(token);
            Emit(s => s.GameMode = action.Value == 1);
            return new UiApplyResult { TargetSubmitted = true, After = Copy(_snapshot) };
        }
        internal void Emit(Action<UiSnapshot> change) { _snapshot = Copy(_snapshot); _snapshot.Revision++; change(_snapshot); SnapshotChanged?.Invoke(_snapshot); }
        public void ConfigureResources(string catalogGzip, long revision, long appMarker, string stableKey, long generation, string? observedProductId, byte? observedColor)
        {
            using var zipped = new System.IO.Compression.GZipStream(new MemoryStream(Convert.FromBase64String(catalogGzip)), System.IO.Compression.CompressionMode.Decompress);
            using var bytes = new MemoryStream(); zipped.CopyTo(bytes);
            CatalogVersion = ResourceCatalog.Parse(bytes.ToArray(), false).ContentVersion;
        }
        public Task<UiSnapshot> SetObservedColorAsync(string productId, byte color, CancellationToken token) => Task.FromResult(Copy(_snapshot));
        public Task<IReadOnlyList<UiHearingProfile>> HearingProfilesAsync(CancellationToken token) => Task.FromResult<IReadOnlyList<UiHearingProfile>>([new(8, "刷新后的听感", true, true)]);
        public Task<UiApplyResult> SelectHearingAsync(int? uid, CancellationToken token) => throw new InvalidOperationException("Unexpected setting");
        public Task<UiCalibrationState> CreateCalibrationAsync(string handle, string kind, CancellationToken token) => throw new InvalidOperationException();
        public Task<UiCalibrationState> GetCalibrationAsync(string handle, CancellationToken token) => throw new InvalidOperationException();
        public Task<UiCalibrationState> AdvanceCalibrationAsync(string handle, long revision, UiCalibrationInput input, CancellationToken token) => throw new InvalidOperationException();
        public Task<UiCalibrationState> CancelCalibrationAsync(string handle, CancellationToken token) => throw new InvalidOperationException();
        public ValueTask DisposeAsync() { Disposals++; return ValueTask.CompletedTask; }
    }
    private sealed class CatalogNetwork : HttpMessageHandler
    {
        internal string Configuration = "{}";
        internal int Version;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            byte[] bytes = request.Method == HttpMethod.Post
                ? JsonSerializer.SerializeToUtf8Bytes(new { code = 0, data = new { version = Version.ToString(), size = Encoding.UTF8.GetByteCount(Configuration).ToString(), downloadUrl = "https://iot-earbuds-cn.heytapimage.com/config" } })
                : Encoding.UTF8.GetBytes(Configuration);
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new ByteArrayContent(bytes), RequestMessage = request });
        }
    }
    private sealed class NeverNetwork : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => throw new InvalidOperationException("Offline tests requested the network"); }
    private sealed class NeverConnector : IControlConnector
    { public Task<IControlTransport> OpenAsync(CancellationToken token) => throw new InvalidOperationException("Projection opened Bluetooth"); }
    private sealed class PipeClient(NamedPipeClientStream pipe) : IAsyncDisposable
    {
        private readonly StreamWriter _writer = new(pipe, new UTF8Encoding(false), 4096, true) { AutoFlush = true };
        private RpcLineReader? _lines;
        internal static async Task<PipeClient> Connect(string name)
        { var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly); await pipe.ConnectAsync(3000); return new(pipe); }
        internal async Task<BackendMessage> Request(BackendRequest request)
        {
            await _writer.WriteLineAsync(JsonSerializer.Serialize(request, RpcWire.Json));
            for (int attempt = 0; attempt < 40; attempt++)
            { string line = await (_lines ??= new(pipe)).ReadLineAsync(default).AsTask().WaitAsync(TimeSpan.FromSeconds(3)) ?? throw new IOException("Pipe closed");
                var message = JsonSerializer.Deserialize<BackendMessage>(line, RpcWire.Json)!; if (message.Kind == "response" && message.Id == request.Id) return message; }
            throw new InvalidOperationException("Pipe response missing");
        }
        public async ValueTask DisposeAsync() { await _writer.DisposeAsync(); await pipe.DisposeAsync(); }
    }
}
