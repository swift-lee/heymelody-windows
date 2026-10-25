using HeyMelody.Platform;
using System.IO.Compression;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using HeyMelodyWindows.App;
using HeyMelodyWindows.Host;
using HeyMelodyWindows.Resources;

internal static class ResourceBackendTests
{
    internal static async Task RunAsync(Func<string, Func<Task>, Task> test, string scratch)
    {
        await test("HeyMelody configures one complete catalog and artwork leases retain ownership", async () =>
        {
            await using var f = await Fixture.Create(scratch);
            var worker = f.Current;
            Check(worker.Configuration is { AppMarker: 17006004, ProfileCount: 85 }, "Initial HeyMelody catalog or App marker was incomplete");
            Check(worker.Configuration.StableKey == DeviceWhitelistStore.KeyFor(1), "Worker persistence key was not bound to peer address");
            var lease = await f.Backend.AcquireArtworkAsync("first", "06EC10", null, "detail", default)
                ?? throw new InvalidOperationException("Bundled artwork lease was not available");
            Check(File.Exists(lease.FullPath), "Bundled artwork lease has no file");
            Check(!f.Backend.ReleaseArtwork("other", lease.LeaseId), "Another owner released an artwork lease");
            f.Backend.ClientDetached("first");
            Check(!f.Backend.ReleaseArtwork("first", lease.LeaseId), "Client EOF retained its artwork lease");
            var second = await f.Backend.AcquireArtworkAsync("second", "06EC10", null, "detail", default);
            Check(second is not null && File.Exists(second.FullPath), "Artwork could not be reacquired");
            Check(worker.Applies == 0 && f.Workers.Count == 1, "Artwork lookup changed device settings or connection");
        });
        await test("backend without its own ResourceBundles uses the single copy beside the GUI", () =>
        {
            string root = Path.Combine(scratch, "bundle-layout-" + Guid.NewGuid().ToString("N")), backend = Path.Combine(root, "backend");
            string own = Path.Combine(backend, "ResourceBundles"), shared = Path.Combine(root, "ResourceBundles");
            Directory.CreateDirectory(backend);
            Check(BackendSupervisor.BundleDirectory(backend) == own, "A missing shared copy was chosen");
            Directory.CreateDirectory(shared); File.WriteAllText(Path.Combine(shared, "artwork.json"), "[]");
            Check(BackendSupervisor.BundleDirectory(backend) == shared, "The release layout did not use the GUI copy");
            Directory.CreateDirectory(own);
            Check(BackendSupervisor.BundleDirectory(backend) == own, "A Host with its own copy (build output) left it");
            var store = new BundledResourceStore(BackendSupervisor.BundleDirectory(AppContext.BaseDirectory));
            Check(store.Artwork.Count > 0 && File.Exists(store.FullPath(store.Artwork[0])), "The test build's own bundle did not resolve");
            return Task.CompletedTask;
        });
        await test("resource settings updates preserve worker connection and active calibration", async () =>
        {
            await using var f = await Fixture.Create(scratch);
            var before = f.Snapshot; var worker = f.Current;
            string catalogHash = f.Coordinator.Catalog.Sha256;
            var calibration = await f.Backend.CreateCalibrationAsync("editor", before.Identity, before.DeviceGeneration, "fit", default);
            var settings = f.Backend.ResourceSettings() with { HeyMelodyKey = "local-plaintext-test", UseOnlineResources = false };
            var saved = await f.Backend.SaveResourceSettingsAsync(settings, default);
            await Until(() => f.Snapshot.ResourceRevision > before.ResourceRevision);
            Check(f.Workers.Count == 1 && f.Snapshot.DeviceGeneration == before.DeviceGeneration && worker.Disposals == 0,
                "Changing a key restarted an unchanged catalog");
            Check(worker.Applies == 0 && worker.Refreshes == 0 && f.Handler.Requests == 0, "Offline setting update issued device or network commands");
            Check(f.Backend.GetCalibration("editor", calibration.Handle, null).Stage == "Ready", "A settings update cancelled the active calibration");
            Check(saved.HeyMelodyKey == "local-plaintext-test" && !saved.UseOnlineResources
                && f.Coordinator.Catalog.Sha256 == catalogHash
                && !saved.Status.Contains("当前测试完成后应用", StringComparison.Ordinal)
                && !f.Backend.ResourceSettings().Status.Contains("当前测试完成后应用", StringComparison.Ordinal),
                "Saving a key against an unchanged catalog invented a pending configuration update");

            long revision = f.Snapshot.ResourceRevision;
            saved = await f.Backend.SaveResourceSettingsAsync(saved with { UseOnlineResources = true }, default);
            await Until(() => f.Snapshot.ResourceRevision > revision && f.Handler.Requests > 0);
            Check(saved.UseOnlineResources && f.Coordinator.Catalog.Sha256 == catalogHash
                && !saved.Status.Contains("当前测试完成后应用", StringComparison.Ordinal)
                && !f.Backend.ResourceSettings().Status.Contains("当前测试完成后应用", StringComparison.Ordinal)
                && f.Backend.GetCalibration("editor", calibration.Handle, null).Stage == "Ready"
                && f.Workers.Count == 1 && worker.Disposals == 0 && f.Snapshot.DeviceGeneration == before.DeviceGeneration,
                "Enabling online resources without a catalog change interrupted calibration or invented a pending update");

            worker.CompleteCalibration();
            await Until(() => f.Snapshot.ControlReady);
            Check(f.Backend.GetCalibration("editor", calibration.Handle, null).Stage == "Completed"
                && !f.Backend.ResourceSettings().Status.Contains("当前测试完成后应用", StringComparison.Ordinal)
                && f.Coordinator.Catalog.Sha256 == catalogHash && f.Workers.Count == 1 && worker.Disposals == 0
                && f.Snapshot.DeviceGeneration == before.DeviceGeneration && worker.Applies == 0 && worker.Refreshes == 0,
                "Completing calibration retained a false pending update or restarted the unchanged worker");
        });
        await test("observed BLE color is PID-bound and stale queued colors cannot write or reconnect", async () =>
        {
            await using var f = await Fixture.Create(scratch);
            var before = f.Snapshot; var worker = f.Current;
            var time = DateTimeOffset.UtcNow;
            f.Backend.ObserveBroadcast(1, Broadcast("06EC10", 0, time));
            await Until(() => worker.Colors.Count == 1);
            Check(worker.Colors[0] == ("06EC10", (byte)0), "Actual color zero was treated as missing");
            f.Backend.ObserveBroadcast(1, Broadcast("063C10", 2, time.AddSeconds(1)));
            f.Backend.ObserveBroadcast(1, Broadcast("06EC10", 3, time.AddMilliseconds(-1)));
            f.Backend.ObserveBroadcast(1, Broadcast("06EC10", 0, time.AddMilliseconds(1)));
            Check(worker.Colors.Count == 1, "Wrong PID, stale advertisement or unchanged color reached worker");
            worker.HoldApply = true;
            var submitted = f.Backend.ApplyAsync(before.Identity, before.DeviceGeneration, new("GameMode", 1), default);
            await worker.ApplyEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            f.Backend.ObserveBroadcast(1, Broadcast("06EC10", 1, time.AddSeconds(2)));
            f.Backend.ObserveBroadcast(1, Broadcast("06EC10", 2, time.AddSeconds(3)));
            worker.ApplyRelease.TrySetResult(); await submitted;
            await Until(() => worker.Colors.Count == 2);
            Check(worker.Colors[^1] == ("06EC10", (byte)2), "Superseded queued color replaced the latest observation");
            Check(worker.Applies == 1 && worker.Refreshes == 0 && f.Workers.Count == 1 && worker.Disposals == 0
                && f.Snapshot.DeviceGeneration == before.DeviceGeneration, "BLE color added GET/SET or reconnected the peer");
        });
        await test("BLE color supplied before SPP initialization is consumed only by its verified PID", async () =>
        {
            foreach (string verified in new[] { "06EC10", "063C10" })
            {
                await using var f = await Fixture.Create(scratch, connected: false, product: verified);
                f.Backend.ObserveBroadcast(1, Broadcast("06EC10", 0, DateTimeOffset.UtcNow));
                f.Backend.ObserveConnection(1, true);
                await Until(() => f.Snapshot.ControlReady);
                var worker = f.Current;
                Check(worker.Configuration is { ObservedProduct: "06EC10", ObservedColor: 0 }, "Initial color did not preserve its BLE PID");
                Check(worker.InitialColor == (verified == "06EC10" ? (byte?)0 : null), "Color from a different product was consumed at initialization");
                Check(worker.Colors.All(c => c.Product == verified) && worker.Applies == 0 && worker.Refreshes == 0 && f.Workers.Count == 1,
                    "Initial color crossed verified PID or issued device commands");
                if (verified != "06EC10") Check(f.Snapshot.Color is null, "Conflicting BLE color remained published after SPP identity verification");
            }
        });
        await test("online catalog waits for an existing calibration lease then advances one generation", async () =>
        {
            await using var f = await Fixture.Create(scratch);
            await f.EnableOnline(); var before = f.Snapshot; var worker = f.Current;
            var lease = await f.Backend.CreateCalibrationAsync("editor", before.Identity, before.DeviceGeneration, "fit", default);
            Check(await f.PublishNewCatalog(), "New online catalog was not accepted by resource coordinator");
            await Until(() => f.Backend.ResourceSettings().Status.Contains("当前测试"));
            Check(f.Snapshot.DeviceGeneration == before.DeviceGeneration && f.Workers.Count == 1 && worker.Disposals == 0,
                "Online update interrupted an active calibration");
            var rejected = await f.Backend.ApplyAsync(before.Identity, before.DeviceGeneration, new("GameMode", 1), default);
            Check(!rejected.TargetSubmitted && worker.Applies == 0, "Resource update weakened calibration write exclusion");
            await f.Backend.CancelCalibrationAsync("editor", lease.Handle);
            await Until(() => f.Snapshot.ControlReady && f.Workers.Count == 2);
            Check(f.Backend.ResourceSettings().ContentVersion == f.Handler.Version && f.Snapshot.DeviceGeneration != before.DeviceGeneration,
                "Deferred catalog did not become active after lease cancellation");
        });
        await test("online adoption cannot retire a calibration queued ahead of its command gate", async () =>
        {
            await using var f = await Fixture.Create(scratch);
            await f.EnableOnline(); var before = f.Snapshot; var worker = f.Current; worker.HoldApply = true;
            var submitted = f.Backend.ApplyAsync(before.Identity, before.DeviceGeneration, new("GameMode", 1), default);
            await worker.ApplyEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            var creating = f.Backend.CreateCalibrationAsync("editor", before.Identity, before.DeviceGeneration, "fit", default);
            Check(await f.PublishNewCatalog(), "New catalog was not downloaded");
            await Until(() => f.ResourceGate.CurrentCount == 0);
            worker.ApplyRelease.TrySetResult(); await submitted;
            UiCalibrationState lease;
            try { lease = await creating.WaitAsync(TimeSpan.FromSeconds(3)); }
            catch (InvalidOperationException)
            {
                // A published transition may reject the queued creation before it reaches the worker.
                await Until(() => f.Snapshot.ControlReady && f.Workers.Count == 2);
                Check(worker.CalibrationCreates == 0, "A rejected calibration creation still reached the retired worker");
                return;
            }
            await Until(() => f.Backend.ResourceSettings().Status.Contains("当前测试") || f.Workers.Count > 1);
            Check(f.Snapshot.DeviceGeneration == before.DeviceGeneration && f.Workers.Count == 1 && worker.Disposals == 0,
                "Catalog adoption cancelled the lease created while it awaited the command gate");
            Check(f.Backend.GetCalibration("editor", lease.Handle, null).Stage == "Ready", "Waiting online adoption retired a newly created calibration");
            await f.Backend.CancelCalibrationAsync("editor", lease.Handle);
            await Until(() => f.Snapshot.ControlReady && f.Workers.Count == 2);
        });
        await test("catalog adoption rejects queued old-generation SET while finishing its submitted command", async () =>
        {
            await using var f = await Fixture.Create(scratch);
            await f.EnableOnline(); var before = f.Snapshot; var worker = f.Current; worker.HoldApply = true;
            var submitted = f.Backend.ApplyAsync(before.Identity, before.DeviceGeneration, new("GameMode", 1), default);
            await worker.ApplyEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            var queued = f.Backend.ApplyAsync(before.Identity, before.DeviceGeneration, new("GameMode", 0), default);
            Check(await f.PublishNewCatalog(), "New catalog was not downloaded");
            await Until(() => f.ResourceGate.CurrentCount == 0);
            worker.ApplyRelease.TrySetResult(); var first = await submitted; var second = await queued;
            await Until(() => f.Snapshot.ControlReady && f.Workers.Count == 2);
            Check(first.TargetSubmitted, "Catalog adoption invalidated the receipt of its already submitted command");
            Check(!second.TargetSubmitted && worker.Applies == 1 && f.Current.Applies == 0,
                "Queued old-generation SET reached a retired catalog");
        });
    }

    internal static async Task RunAppearanceAsync(Func<string, Func<Task>, Task> test, string scratch)
    {
        await test("remembered appearance is published before control and restored after a Host restart", async () =>
        {
            string path;
            await using (var f = await Fixture.Create(scratch))
            {
                path = f.Path;
                Check(f.Backend.Devices().Single() is { ProductId: "06EC10", Color: null }, "Verified PID was not published as a hint");
                f.Backend.ObserveBroadcast(1, Broadcast("06EC10", 1, DateTimeOffset.UtcNow));
                await Until(() => f.Backend.Devices().Single().Color == 1);
                f.Backend.ObserveBroadcast(1, Broadcast("063C10", 2, DateTimeOffset.UtcNow.AddSeconds(1)));
                Check(f.Backend.Devices().Single() is { ProductId: "06EC10", Color: 1 }, "A broadcast for another product replaced the hint");
                Check(File.ReadAllText(Path.Combine(path, "device-appearance.json")) is var json && json.Contains("\"06EC10\"", StringComparison.Ordinal)
                    && !json.Contains("Identity", StringComparison.OrdinalIgnoreCase) && json.Contains(DeviceWhitelistStore.KeyFor(1), StringComparison.Ordinal),
                    "Appearance file did not use only the whitelist key");
            }
            await using var restarted = await Fixture.Create(scratch, connected: false, path: path);
            Check(restarted.Backend.Devices().Single() is { ProductId: "06EC10", Color: 1 } && restarted.Workers.Count == 0 && !restarted.Snapshot.ControlReady,
                "Hints were not restored before a control session, or granted control");
        });
        await test("a session color outranks broadcasts and unknown products have no artwork", async () =>
        {
            await using var f = await Fixture.Create(scratch);
            f.Current.ReportColor(4);
            await Until(() => f.Backend.Devices().Single().Color == 4);
            f.Backend.ObserveBroadcast(1, Broadcast("06EC10", 5, DateTimeOffset.UtcNow));
            await Until(() => f.Current.Colors.Count == 1);
            Check(f.Backend.Devices().Single().Color == 4, "A broadcast overrode the control session color");
            Check(await f.Backend.AcquireArtworkAsync("owner", "FFFFFF", 0, "hero", default) is null, "An uncatalogued hint failed instead of having no art");
        });
        await test("re-download refreshes HeyMelody once and requires online resources", async () =>
        {
            await using var f = await Fixture.Create(scratch);
            await f.EnableOnline(); int before = f.Handler.Requests;
            var first = f.Backend.RedownloadResourcesAsync(); var second = f.Backend.RedownloadResourcesAsync();
            Check(ReferenceEquals(first, second), "Concurrent re-downloads were not shared");
            var result = await first;
            Check(result.Success && result.Failed == 0 && result.Settings.UseOnlineResources && result.Message.Length != 0 && f.Handler.Requests > before
                && f.Workers.All(worker => worker.Applies == 0), "Re-download did not report a validated result: " + result);
            await f.Backend.SaveResourceSettingsAsync(f.Backend.ResourceSettings() with { UseOnlineResources = false }, default);
            try { await f.Backend.RedownloadResourcesAsync(); throw new InvalidOperationException("Offline re-download was accepted"); }
            catch (InvalidOperationException error) when (error.Message.Contains("在线资源已关闭", StringComparison.Ordinal)) { }
        });
    }

    private static BroadcastObservation Broadcast(string product, byte color, DateTimeOffset time) =>
        new(product, CaseLidState.Unknown, EarbudPlacement.Unknown, EarbudPlacement.Unknown, null, null, null, null, null, null, time) { Color = color };
    private static void Check(bool condition, string reason) { if (!condition) throw new InvalidOperationException(reason); }
    private static async Task Until(Func<bool> predicate)
    { using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5)); while (!predicate()) await Task.Delay(10, timeout.Token); }

    private sealed class Fixture : IAsyncDisposable
    {
        internal ResourceCoordinator Coordinator { get; }
        internal Vendor Handler { get; }
        private readonly HttpClient _http;
        internal BackendSupervisor Backend { get; }
        internal readonly List<ResourcePeer> Workers = [];
        private readonly string _product;
        internal UiSnapshot Snapshot => Backend.Select(Backend.Devices().Single().Identity);
        internal string Path { get; }
        internal ResourcePeer Current => Workers[^1];
        // Observe the adoption gate to make queued-command races deterministic.
        internal SemaphoreSlim ResourceGate => (SemaphoreSlim)typeof(BackendSupervisor).GetField("_resourceGate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Backend)!;
        private Fixture(string path, bool connected, string product)
        {
            _product = product; Handler = new(); _http = new(Handler); Path = path;
            Coordinator = new(System.IO.Path.Combine(path, "resources"), _http);
            var device = new BluetoothCandidate(1, "耳机", connected, true, true, 0x404);
            var store = new DeviceWhitelistStore(System.IO.Path.Combine(path, "devices.json")); store.Save([DeviceWhitelistStore.KeyFor(1)]);
            Backend = new(store, _ => Task.FromResult<IReadOnlyList<BluetoothCandidate>>([device]), () =>
            { var worker = new ResourcePeer(_product); Workers.Add(worker); return worker; }, resources: Coordinator);
        }
        internal static async Task<Fixture> Create(string scratch, bool connected = true, string product = "06EC10", string? path = null)
        {
            path ??= System.IO.Path.Combine(scratch, "resources-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(path);
            var fixture = new Fixture(path, connected, product);
            await fixture.Coordinator.InitializeAsync();
            await fixture.Coordinator.UpdatePreferencesAsync(fixture.Coordinator.Preferences with { UseOnlineResources = false });
            fixture.Handler.Configuration = fixture.Coordinator.Catalog.Raw.GetRawText();
            fixture.Handler.Version = fixture.Coordinator.Catalog.ContentVersion;
            await fixture.Backend.InitializeAsync(default);
            if (connected) await Until(() => fixture.Snapshot.ControlReady);
            return fixture;
        }
        internal async Task EnableOnline()
        { await Backend.SaveResourceSettingsAsync(Backend.ResourceSettings() with { UseOnlineResources = true }, default); await Coordinator.RefreshAsync(true); }
        internal Task<bool> PublishNewCatalog()
        {
            Handler.Version += 10; var node = JsonNode.Parse(Coordinator.Catalog.Raw.GetRawText())!.AsObject(); node["versionCode"] = Handler.Version;
            Handler.Configuration = node.ToJsonString(); return Coordinator.RefreshAsync(true);
        }
        public async ValueTask DisposeAsync() { await Backend.DisposeAsync(); _http.Dispose(); }
    }
    private sealed record Configuration(long AppMarker, int ProfileCount, string StableKey, long Generation, string? ObservedProduct, byte? ObservedColor);
    private sealed class ResourcePeer(string product) : IPeerWorker, IResourcePeerWorker, ICalibrationPeerWorker
    {
        public event Action<UiSnapshot>? SnapshotChanged;
        public event Action? Unavailable { add { } remove { } }
        public event Action<UiCalibrationState>? CalibrationChanged;
        public event Action<UiCalibrationSpectrum>? CalibrationSpectrumChanged { add { } remove { } }
        internal Configuration Configuration = new(0, 0, "", 0, null, null);
        internal byte? InitialColor;
        internal int Applies, Refreshes, Disposals, CalibrationCreates;
        internal bool HoldApply;
        internal readonly List<(string Product, byte Color)> Colors = [];
        internal readonly TaskCompletionSource ApplyEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource ApplyRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private UiSnapshot _snapshot = new();
        private UiCalibrationState _calibration = new();
        public void ConfigureResources(string catalogGzip, long revision, long appMarker, string stableKey, long generation, string? observedProductId, byte? observedColor)
        {
            using var zipped = new GZipStream(new MemoryStream(Convert.FromBase64String(catalogGzip)), CompressionMode.Decompress);
            using var bytes = new MemoryStream(); zipped.CopyTo(bytes);
            var catalog = ResourceCatalog.Parse(bytes.ToArray(), false);
            Configuration = new(appMarker, catalog.Profiles.Count, stableKey, generation, observedProductId, observedColor);
        }
        public Task<UiSnapshot> InitializeAsync(ulong address, string identity, string name, CancellationToken token)
        {
            InitialColor = product == Configuration.ObservedProduct ? Configuration.ObservedColor : null;
            _snapshot = new() { Identity = identity, DeviceName = name, ProductId = product, Revision = 1, Connected = true,
                ConnectionState = ConnectionState.Connected, ControlReady = true, SupportedModel = true, CanSetGameMode = true,
                CanFitTest = true, CanSetupHearing = true, LeftPlacement = EarbudPlacement.InEar, RightPlacement = EarbudPlacement.InEar, Color = InitialColor };
            SnapshotChanged?.Invoke(_snapshot); return Task.FromResult(_snapshot);
        }
        private void Emit(Action<UiSnapshot> edit)
        { _snapshot = JsonSerializer.Deserialize<UiSnapshot>(JsonSerializer.Serialize(_snapshot, RpcWire.Json), RpcWire.Json)!; _snapshot.Revision++; edit(_snapshot); SnapshotChanged?.Invoke(_snapshot); }
        internal void ReportColor(byte color) => Emit(snapshot => snapshot.Color = color);
        public Task<UiSnapshot> SetObservedColorAsync(string productId, byte color, CancellationToken token)
        // Like the native worker: a broadcast color tunes presets but never replaces the session's own color read.
        { Colors.Add((productId, color)); Emit(_ => { }); return Task.FromResult(_snapshot); }
        public Task<UiSnapshot> RefreshAsync(RefreshScope scope, CancellationToken token) { Refreshes++; return Task.FromResult(_snapshot); }
        public async Task<UiApplyResult> ApplyAsync(UiAction action, CancellationToken token)
        {
            Applies++; ApplyEntered.TrySetResult(); if (HoldApply) await ApplyRelease.Task.WaitAsync(token);
            Emit(snapshot => snapshot.GameMode = action.Value == 1);
            return new() { TargetSubmitted = true, BackendReceiptReceived = true, SubmissionAttempted = true, Accepted = true, After = _snapshot };
        }
        public Task<UiCalibrationState> CreateCalibrationAsync(string handle, string kind, CancellationToken token)
        { CalibrationCreates++; _calibration = new() { Handle = handle, Workflow = kind, Stage = "Ready", Revision = 1 }; return Task.FromResult(_calibration); }
        public Task<UiCalibrationState> GetCalibrationAsync(string handle, CancellationToken token) => Task.FromResult(_calibration);
        internal void CompleteCalibration()
        { _calibration = _calibration with { Stage = "Completed", Revision = _calibration.Revision + 1 }; CalibrationChanged?.Invoke(_calibration); }
        public Task<UiCalibrationState> AdvanceCalibrationAsync(string handle, long revision, UiCalibrationInput input, CancellationToken token)
        { _calibration = _calibration with { Stage = "FitTesting", Revision = _calibration.Revision + 1 }; return Task.FromResult(_calibration); }
        public Task<UiCalibrationState> CancelCalibrationAsync(string handle, CancellationToken token)
        { _calibration = _calibration with { Stage = "Cancelled", Revision = _calibration.Revision + 1 }; CalibrationChanged?.Invoke(_calibration); return Task.FromResult(_calibration); }
        public Task<IReadOnlyList<UiHearingProfile>> HearingProfilesAsync(CancellationToken token) => Task.FromResult<IReadOnlyList<UiHearingProfile>>([]);
        public Task<UiApplyResult> SelectHearingAsync(int? uid, CancellationToken token) => Task.FromResult(new UiApplyResult { TargetSubmitted = true });
        public ValueTask DisposeAsync() { Disposals++; return ValueTask.CompletedTask; }
    }
    private sealed class Vendor : HttpMessageHandler
    {
        internal int Requests, Version;
        internal string Configuration = "{}";
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Requests++; byte[] bytes = request.Method == HttpMethod.Post
                ? JsonSerializer.SerializeToUtf8Bytes(new { code = 0, data = new { version = Version.ToString(), size = Encoding.UTF8.GetByteCount(Configuration).ToString(), downloadUrl = "https://iot-earbuds-cn.heytapimage.com/config" } })
                : Encoding.UTF8.GetBytes(Configuration);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes), RequestMessage = request });
        }
    }
}
