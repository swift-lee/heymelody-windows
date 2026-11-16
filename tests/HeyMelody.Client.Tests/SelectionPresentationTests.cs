using System.Text.Json;
using System.Reflection;
using HeyMelodyWindows.App;

internal static class SelectionPresentationTests
{
    private static readonly DateTimeOffset Sample = DateTimeOffset.Parse("2026-10-09T01:00:00Z");

    internal static async Task RunAsync(Func<string, Func<Task>, Task> test)
    {
        await test("the AOT presentation getter table covers every public presentation property exactly once", () =>
        {
            var expected = typeof(MainViewModel).GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(property => !property.Name.EndsWith("Command", StringComparison.Ordinal))
                .Select(property => property.Name).ToHashSet(StringComparer.Ordinal);
            var field = typeof(MainViewModel).GetField("PresentationProperties", BindingFlags.Static | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("The presentation getter table is missing.");
            var getters = ((string Name, Func<MainViewModel, object?> Read)[])field.GetValue(null)!;
            var actual = getters.Select(getter => getter.Name).ToHashSet(StringComparer.Ordinal);
            Check(getters.Length == actual.Count, "The presentation getter table contains duplicate property names.");
            Check(expected.SetEquals(actual),
                "Presentation getter coverage differs; missing: " + string.Join(", ", expected.Except(actual).Order(StringComparer.Ordinal))
                + "; extra: " + string.Join(", ", actual.Except(expected).Order(StringComparer.Ordinal)));
            return Task.CompletedTask;
        });
        await test("old cached controls and generic warnings cannot restore retired auto-volume or claim current write failures", async () =>
        {
            var service = new Service();
            foreach (var identity in new[] { "a", "b" })
            {
                var cached = Cached(Snapshot(identity));
                cached.FeatureSections = [cached.FeatureSections[0] with { Controls = cached.FeatureSections[0].Controls
                    .Append(new UiFeatureControl("auto-volume", "自动音量", "Switch", 0, CanWrite: true)).ToArray() },
                    new("legacy-only", "旧功能", false, [new("auto-volume", "自动音量", "Switch", 1, CanWrite: true),
                        new("game-sound", "游戏音效类型", "Combo", 3, Options: [new(3, "射击类游戏音效")], CanWrite: true),
                        new("bass", "低音强度", "Slider", -4, Minimum: -9, Maximum: 8, CanWrite: true),
                        new("bass-enabled", "低音增强", "Switch", 1, CanWrite: true)])];
                cached.Warnings = ["部分耳机状态暂时不可用，相关设置已禁用。"];
                service.Snapshots[identity] = Next(cached); // Exercise the serialized presentation received from an older Host.
            }
            await using var vm = Create(service); await vm.InitializeAsync();
            foreach (var device in vm.Devices.ToArray())
            {
                await vm.SelectDeviceAsync(device);
                Check(vm.PresentationCached && vm.FeatureSections is [{ Id: "settings" }]
                    && vm.FeatureSections[0].Controls.Select(control => control.Id).SequenceEqual(new[] { "game", "spatial" }),
                    "The shared rendering path must retain other cached controls and remove the retired control and its empty section.");
                Check(vm.WarningsText == "上次连接时有状态提示，连接后请刷新确认。",
                    "An old generic cached warning must not claim that current settings have been disabled.");
                Check(vm.EqualizerFeatures.Select(control => control.Id).SequenceEqual(["bass-enabled", "bass"])
                    && vm.EqualizerFeatures.All(control => !control.CanEdit) && vm.EqualizerFeatures[1].NumberValue == -4,
                    "Both sources relocate cached BassWave controls without granting write access or changing signed state.");
            }
            var connected = Snapshot(vm.SelectedDevice!.Identity); connected.Revision = 3;
            connected.Warnings = ["耳机拒绝了提示音音量读取，请手动刷新后重试。"];
            service.Push(connected);
            Check(vm.HasVerifiedSnapshot && vm.WarningsText == connected.Warnings.Single(),
                "A latest real read failure must not be replaced by historical guidance.");
            Check(service.Actions.Count == 0, "Filtering old presentation must not send settings.");
        });
        await test("offline startup restores the preferred real device and its complete read-only settings", async () =>
        {
            var service = new Service();
            service.Devices = [new("a", "第一副", false), new("b", "上次选择", false) { IsPreferredSelection = true }];
            service.Snapshots["b"] = Cached(Snapshot("b"));
            await using var vm = Create(service); await vm.InitializeAsync();
            Check(vm.SelectedDevice?.Identity == "b" && service.Selections.SequenceEqual(["b"]), "Startup must restore the preferred device before selecting the backend.");
            Check(vm.PresentationCached && vm.PresentationSampleTime == Sample && vm.ProductId == "06EC10" && vm.Firmware == "1234"
                && vm.AncModes.Count == 1 && vm.SelectedAnc?.Key == 0x0104 && vm.SelectedPreset?.Key == 7
                && vm.FeatureSections.SelectMany(section => section.Controls).Count() == 2
                && vm.EqEntries.Count == 1 && vm.HearingProfiles.Count == 2,
                "Cached ANC, EQ, dynamic controls, hearing profiles and information remain present.");
            Check(!vm.HasVerifiedSnapshot && !vm.CanSetGameMode && !vm.CanEditEq && !vm.CanFitTest && !vm.CanSetupHearing
                && vm.FeatureSections.SelectMany(section => section.Controls).All(control => !control.CanEdit)
                && vm.LeftBatteryText == "—", "Cached presentation grants no control or stale battery observation.");
            await vm.ToggleGameCommand.ExecuteAsync(true); await vm.ApplyAncCommand.ExecuteAsync(null);
            await vm.FeatureSections[0].Controls[0].SubmitAsync();
            Check(service.Actions.Count == 0 && !vm.EqDirty, "Binding cached settings must neither create a draft nor submit settings.");
        });

        await test("an initially disconnected selection accepts its connection push without a second selection", async () =>
        {
            var service = new Service(); service.Devices = [new("a", "第一副", false)];
            service.Snapshots["a"] = Cached(Snapshot("a"));
            await using var vm = Create(service); await vm.InitializeAsync();
            var connected = Next(service.Snapshots["a"]); connected.PresentationCached = false;
            connected.ControlReady = connected.Connected = true; connected.MonitoringState = MonitoringState.Monitoring;
            service.PushCatalog([new("a", "第一副 · 已连接", true)]);
            service.Push(connected);
            Check(vm.SelectedDevice?.Identity == "a" && ReferenceEquals(vm.SelectedDevice, service.Devices[0])
                && vm.HasVerifiedSnapshot && !vm.PresentationCached && vm.CanSetGameMode
                && service.Selections.SequenceEqual(["a"]), "Catalog replacement and the following verified snapshot must keep the original target.");
        });

        await test("multi-device catalog sorting and replacement preserve the selected canonical device without another RPC", async () =>
        {
            var service = new Service(); await using var vm = Create(service); await vm.InitializeAsync();
            await vm.SelectDeviceAsync(vm.Devices[1]);
            var selections = service.Selections.Count;
            service.PushCatalog([new("b", "第二副 · 新名称", false) { ProductId = "06EC10", Color = 2 },
                new("a", "第一副", true) { IsPreferredSelection = true }]);
            Check(ReferenceEquals(vm.SelectedDevice, service.Devices[0]) && vm.SelectedDevice.Identity == "b"
                && vm.HasVerifiedSnapshot && service.Selections.Count == selections, "Identity takes precedence over reorder, connection state, appearance hints and another preferred flag.");
            service.PushCatalog(service.Devices.Select(device => device with { }).Reverse().ToArray());
            Check(ReferenceEquals(vm.SelectedDevice, service.Devices[1]) && service.Selections.Count == selections,
                "Even equal replacement records must leave selection attached to the current list.");
            service.PushCatalog([service.Devices.Single(device => device.Identity == "a")]);
            Check(vm.SelectedDevice?.Identity == "a" && vm.HasVerifiedSnapshot && service.Selections.Count == selections,
                "A removed target falls back to the preferred remaining device and its already cached backend state.");
        });

        await test("a new backend restores its preferred random identity when catalog arrives before the snapshot", async () =>
        {
            var service = new Service(); await using var vm = Create(service); await vm.InitializeAsync();
            service.Backend = "backend-new";
            service.PushCatalog([new("new-a", "第一副", false), new("new-b", "上次选择", false) { IsPreferredSelection = true }]);
            Check(vm.SelectedDevice?.Identity == "new-b" && !vm.HasVerifiedSnapshot && vm.FeatureSections.Count == 0,
                "The new peer map must choose the preferred current identity and remove the previous device's controls.");
            var current = Snapshot("new-b", service.Backend); service.Push(current);
            Check(vm.HasVerifiedSnapshot && vm.ProductId == current.ProductId && service.Selections.SequenceEqual(["a"]),
                "The catalog-selected identity must accept the new backend's snapshot without replaying Select or writes.");
            service.Publish(new() { Kind = "snapshot", BackendInstanceId = "backend-a", Snapshot = Snapshot("a") });
            Check(vm.SelectedDevice?.Identity == "new-b" && vm.HasVerifiedSnapshot, "A retired backend must not reclaim the target.");
        });

        await test("a new backend restores a preferred snapshot already received before its catalog", async () =>
        {
            var service = new Service(); await using var vm = Create(service); await vm.InitializeAsync();
            service.Backend = "backend-next";
            service.Push(Cached(Snapshot("new-b", service.Backend)));
            service.PushCatalog([new("new-a", "第一副", false), new("new-b", "第二副", false) { IsPreferredSelection = true }]);
            Check(vm.SelectedDevice?.Identity == "new-b" && vm.PresentationCached && vm.FeatureSections.Count == 1
                && !vm.HasVerifiedSnapshot && service.Selections.Count == 1,
                "Subscription/event ordering must not lose the selected device's available offline presentation.");
        });

        await test("the cache flag forbids control even if cached fields incorrectly claim a ready session", async () =>
        {
            var service = new Service(); var spoof = Snapshot("a"); spoof.PresentationCached = true;
            spoof.PresentationSampleTime = Sample; spoof.ObservationFresh = false; service.Snapshots["a"] = spoof;
            await using var vm = Create(service); await vm.InitializeAsync();
            Check(!vm.HasVerifiedSnapshot && !vm.CanSetAncMode && !vm.CanSetCustomEqualizer && !vm.CanSetHiRes
                && !vm.CanSetDualConnection && !vm.CanSelectHearingProfile && vm.LeftBatteryText == "—",
                "Cached Connected, ControlReady and capability fields cannot authorize live actions.");
            await vm.ToggleHiResCommand.ExecuteAsync(false); await vm.ToggleDualCommand.ExecuteAsync(false);
            await vm.ApplyPresetCommand.ExecuteAsync(null); await vm.UseEqCommand.ExecuteAsync(null);
            vm.FeatureSections[0].Controls[0].BooleanValue = true;
            await vm.FeatureSections[0].Controls[0].SubmitAsync();
            Check(service.Actions.Count == 0, "Directly invoking a disabled command must still produce zero backend writes.");
        });

        await test("backend unavailability retains settings read-only and fresh BLE may overlay cached presentation", async () =>
        {
            var service = new Service(); await using var vm = Create(service); await vm.InitializeAsync();
            var controls = vm.FeatureSections;
            service.Publish(new() { Kind = "unavailable", Message = "测试后台已断开" });
            Check(vm.PresentationCached && !vm.HasVerifiedSnapshot && vm.Firmware == "1234" && vm.SelectedAnc?.Key == 0x0104
                && ReferenceEquals(controls, vm.FeatureSections) && vm.FeatureSections[0].Controls.All(control => !control.CanEdit)
                && vm.LeftBatteryText == "—", "Losing the pipe disables control while preserving the current device's last observed settings.");
            service.PushCatalog(service.Devices.Select(device => device with { Connected = false }).ToArray());
            Check(!vm.HasVerifiedSnapshot, "A catalog alone cannot restore invalidated authority from an old snapshot.");
            var ble = Cached(Snapshot("a")); ble.Revision = 2; ble.ObservationFresh = true;
            ble.ObservationSource = "ble"; ble.LeftBattery = 42; ble.LeftCharging = true; service.Push(ble);
            Check(vm.PresentationCached && vm.LeftBatteryText == "42%" && vm.LeftChargeText == "正在充电" && !vm.HasVerifiedSnapshot,
                "Fresh BLE observations remain visible without upgrading cached settings to a verified control session.");
            await vm.ToggleGameCommand.ExecuteAsync(true); Check(service.Actions.Count == 0, "A fresh broadcast still cannot submit settings.");
        });

        await test("a verified reconnect replaces cached function definitions and blocks an incompatible EQ draft", async () =>
        {
            var service = new Service(); service.Snapshots["a"] = Cached(Snapshot("a"));
            await using var vm = Create(service); await vm.InitializeAsync();
            var reconnect = Next(service.Snapshots["a"]); reconnect.PresentationCached = false;
            reconnect.ControlReady = reconnect.Connected = true; service.Push(reconnect);
            vm.EqName = "仅在当前窗口保留的草稿"; vm.Bands[0].Gain = 4;
            var revised = Next(reconnect); revised.Firmware = "2345"; revised.ResourceRevision = 2;
            revised.FeatureSections = [new("new-settings", "新固件功能", false,
                [new("new-control", "新开关", "Switch", 1, CanWrite: true)])];
            revised.EqualizerFrequencies = [100, 200];
            revised.EqualizerEntries = [new(1, "新固件 EQ", -6, 6, true, [new(100, 1), new(200, 2)])];
            service.Push(revised);
            Check(vm.HasVerifiedSnapshot && vm.FeatureSections.Single().Controls.Single().Id == "new-control"
                && vm.FeatureSections[0].Controls[0].CanEdit && vm.Firmware == "2345",
                "The current verified definition adds new functions and removes outdated cached functions.");
            Check(vm.EqDirty && vm.EqName.Contains("草稿") && !vm.CanSetCustomEqualizer && vm.CanEditEq,
                "An old-frequency draft stays local and cannot be saved until it is reset to the new layout.");
            await vm.SaveEqCommand.ExecuteAsync(null); Check(service.Actions.Count == 0, "A command invocation cannot submit the obsolete frequency layout.");
            vm.ResetEqCommand.Execute(null);
            Check(!vm.EqDirty && vm.Bands.Select(band => band.FrequencyHz).SequenceEqual([100, 200]) && vm.CanSetCustomEqualizer,
                "The existing reset action recovers the actual new EQ layout.");
        });

        await test("a candidate removed during EQ discard confirmation cannot become selected", async () =>
        {
            var service = new Service(); var confirmations = new Confirmations { Block = new(TaskCreationOptions.RunContinuationsAsynchronously) };
            await using var vm = Create(service, confirmations); await vm.InitializeAsync(); vm.EqName = "保留草稿";
            var change = vm.SelectDeviceAsync(vm.Devices[1]);
            await confirmations.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
            service.PushCatalog([service.Devices[0]]);
            confirmations.Block.SetResult(true); await change.WaitAsync(TimeSpan.FromSeconds(3));
            Check(vm.SelectedDevice?.Identity == "a" && vm.EqDirty && vm.EqName == "保留草稿"
                && service.Selections.SequenceEqual(["a"]), "Confirmation must revalidate membership and preserve the original draft when the candidate disappears.");
        });

        await test("cached disconnection cancels queued writes without persisting or replaying the local EQ draft", async () =>
        {
            var service = new Service { RefreshBlocked = new(TaskCreationOptions.RunContinuationsAsynchronously) };
            await using var vm = Create(service); await vm.InitializeAsync(); vm.EqName = "未提交草稿"; vm.Bands[0].Gain = 4;
            var refresh = vm.RefreshStateCommand.ExecuteAsync(null);
            await service.RefreshStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
            var queued = vm.ToggleGameCommand.ExecuteAsync(true);
            var offline = Cached(Snapshot("a")); offline.DeviceGeneration = 2; offline.Revision = 1; service.Push(offline);
            await refresh.WaitAsync(TimeSpan.FromSeconds(3)); await queued.WaitAsync(TimeSpan.FromSeconds(3));
            Check(service.Actions.Count == 0 && !vm.Busy && vm.EqDirty && vm.EqName == "未提交草稿"
                && offline.EqualizerEntries[0].Name == "设备上的 EQ", "Cancellation retains the window draft without inserting it into cached observed settings.");
            var reconnected = Snapshot("a"); reconnected.DeviceGeneration = 3; service.Push(reconnected);
            Check(vm.HasVerifiedSnapshot && vm.GameMode == false && service.Actions.Count == 0 && vm.EqDirty,
                "Reconnection restores actual settings without replaying an interrupted action.");
        });

        await test("demo and temporary device management do not replace the remembered real target", async () =>
        {
            var service = new Service(); await using var vm = Create(service); await vm.InitializeAsync();
            await vm.AddDemoDeviceAsync();
            service.PushCatalog(service.Devices.Select(device => device with { IsPreferredSelection = device.Identity == "b" }).ToArray());
            Check(vm.IsDemoSelected && service.Selections.SequenceEqual(["a"]), "Catalog preference flags cannot switch away from the active demo or persist it as a real selection.");
            Check(await vm.PrepareDeviceManagementAsync() && vm.SelectedDevice is null, "Management may explicitly detach the display target.");
            Check(await vm.CompleteDeviceManagementAsync(false) && vm.IsDemoSelected && service.Selections.SequenceEqual(["a"]),
                "Cancelling temporary management restores the demo locally and sends no replacement real selection.");
        });
    }

    private static MainViewModel Create(Service service, Confirmations? confirmations = null)
    {
        var vm = new MainViewModel(service, confirmations ?? new());
        service.EventReceived += vm.AdoptBackendEvent;
        return vm;
    }
    private static void Check(bool condition, string reason) { if (!condition) throw new InvalidOperationException(reason); }
    private static UiSnapshot Next(UiSnapshot source)
    {
        var result = JsonSerializer.Deserialize<UiSnapshot>(JsonSerializer.Serialize(source))!;
        result.Revision++; return result;
    }
    private static UiSnapshot Cached(UiSnapshot snapshot)
    {
        snapshot.PresentationCached = true; snapshot.PresentationSampleTime = snapshot.SampleTime;
        snapshot.Connected = snapshot.ControlReady = snapshot.ObservationFresh = false;
        snapshot.MonitoringState = MonitoringState.WaitingForConnection;
        return snapshot;
    }
    private static UiSnapshot Snapshot(string identity, string backend = "backend-a") => new()
    {
        BackendInstanceId = backend, Identity = identity, DeviceName = identity, DeviceGeneration = 1, Revision = 1,
        Connected = true, ConnectionState = ConnectionState.Connected, ControlReady = true, SupportedModel = true,
        MonitoringState = MonitoringState.Monitoring, ObservationFresh = true, LeftBattery = 90,
        ProductId = "06EC10", Firmware = "1234", Color = 1, SampleTime = Sample,
        AncPrimary = 1, Anc = 0x0104, Equalizer = 7, Spatial = 0, GameMode = false, DualConnection = true, HiRes = true,
        CanSetAncMode = true, CanSetAnc = true, CanSetEqualizer = true, CanSetCustomEqualizer = true,
        CanSetGameMode = true, CanSetDualConnection = true, CanSetSpatial = true, CanSetHiRes = true,
        CanSetupHearing = true, CanFitTest = true,
        AncModes = [new(1, "降噪", "anc", [new(0x0104, "深度降噪"), new(0x0107, "智能降噪")])],
        AncOptions = [new(0x0104, "深度降噪"), new(0x0107, "智能降噪")],
        EqualizerOptions = [new(0, "原声"), new(7, "高解析")], SpatialOptions = [new(0, "关闭"), new(2, "头部追踪")],
        EqualizerFrequencies = [31, 62], EqualizerEntries = [new(1, "设备上的 EQ", -6, 6, true, [new(31, 1), new(62, 2)])],
        FeatureSections = [new("settings", "设置", true,
            [new("game", "游戏模式", "Switch", 0, CanWrite: true), new("spatial", "空间音效", "Combo", 0,
                Options: [new(0, "关闭"), new(2, "头部追踪")], Maximum: 2, CanWrite: true)])],
        HearingEnabled = true, HearingProfiles = [new(1, "日常", true, true)]
    };

    private sealed class Confirmations : IConfirmationService
    {
        internal TaskCompletionSource<bool>? Block;
        internal TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<bool> ConfirmAsync(string title, string message, CancellationToken token)
        { Started.TrySetResult(); return Block?.Task.WaitAsync(token) ?? Task.FromResult(true); }
    }
    private sealed class Service : IHeadsetEventService
    {
        internal string Backend = "backend-a";
        internal IReadOnlyList<UiDevice> Devices = [new("a", "第一副", true), new("b", "第二副", true)];
        internal Dictionary<string, UiSnapshot> Snapshots = new(StringComparer.Ordinal)
            { ["a"] = Snapshot("a"), ["b"] = Snapshot("b") };
        internal List<string> Selections = [];
        internal List<UiAction> Actions = [];
        internal TaskCompletionSource<UiSnapshot>? RefreshBlocked;
        internal TaskCompletionSource RefreshStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public event Action<BackendEvent>? EventReceived;
        internal void Publish(BackendEvent notification) => EventReceived?.Invoke(notification);
        internal void Push(UiSnapshot snapshot)
        { Snapshots[snapshot.Identity] = snapshot; Publish(new() { Kind = "snapshot", BackendInstanceId = snapshot.BackendInstanceId, Snapshot = snapshot }); }
        internal void PushCatalog(IReadOnlyList<UiDevice> devices)
        { Devices = devices; Publish(new() { Kind = "catalog", BackendInstanceId = Backend, Devices = devices }); }
        public Task<BackendSubscription> SubscribeAsync(string role, CancellationToken token) => Task.FromResult(new BackendSubscription
        { BackendInstanceId = Backend, Devices = Devices, Snapshots = Snapshots.Values.ToArray() });
        public Task<IReadOnlyList<UiDevice>> EnumerateAsync(CancellationToken token) => Task.FromResult(Devices);
        public Task<UiSnapshot> SelectAsync(string identity, CancellationToken token)
        { Selections.Add(identity); return Task.FromResult(Snapshots[identity]); }
        public Task<UiSnapshot> RefreshAsync(string identity, RefreshScope scope, CancellationToken token)
        { RefreshStarted.TrySetResult(); return RefreshBlocked?.Task.WaitAsync(token) ?? Task.FromResult(Snapshots[identity]); }
        public Task<UiApplyResult> ApplyAsync(string identity, long generation, UiAction action, CancellationToken token)
        { Actions.Add(action); return Task.FromResult(new UiApplyResult { FailureKind = ApplyFailure.Validation }); }
        public Task<UiSnapshot> PauseAsync(string identity, CancellationToken token) => throw new InvalidOperationException();
        public Task<UiSnapshot> ResumeAsync(string identity, CancellationToken token) => throw new InvalidOperationException();
        public Task AcknowledgeExitAsync(string requestId, bool approved, CancellationToken token) => Task.CompletedTask;
        public Task ActivateMainUiAsync(CancellationToken token) => Task.CompletedTask;
        public Task<UiSnapshot> ConnectAsync(string identity, CancellationToken token) => throw new InvalidOperationException();
        public Task DisconnectAsync(CancellationToken token) => throw new InvalidOperationException();
        public Task<UiSnapshot> RefreshAsync(CancellationToken token) => throw new InvalidOperationException();
        public Task<UiApplyResult> ApplyAsync(UiAction action, CancellationToken token) => throw new InvalidOperationException();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
