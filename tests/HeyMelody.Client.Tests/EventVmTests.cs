using System.Text.Json;
using HeyMelodyWindows.App;

internal static class EventVmTests
{
    public static async Task RunAsync(Func<string, Func<Task>, Task> test)
    {
        await test("platform authorization warning survives device observations and clears on a fresh allowed state", async () =>
        {
            var service = new EventService { Platform = new("macOS", "Denied", "Unknown", "请允许蓝牙访问。") };
            await using var vm = Create(service); await vm.InitializeAsync();
            Check(vm.HasPlatformWarning && vm.PlatformMessage == "请允许蓝牙访问。");
            service.Push(Next(service.Snapshots["device-a"]));
            Check(vm.HasPlatformWarning && service.Actions.Count == 0);
            service.Publish(new() { Kind = "platform", BackendInstanceId = "backend-a",
                Platform = new("macOS", "Allowed", "PoweredOn", "") });
            Check(!vm.HasPlatformWarning && vm.PlatformMessage.Length == 0 && service.Actions.Count == 0);
        });

        await test("event VM preserves warning guidance and clears it on a fresh state or device switch", async () =>
        {
            var service = new EventService();
            service.Snapshots["device-a"].Warnings = ["部分状态通知不可用，仍可手动刷新状态。"];
            await using var vm = Create(service); await vm.InitializeAsync();
            Check(vm.HasWarnings && vm.WarningsText == "部分状态通知不可用，仍可手动刷新状态。"
                && vm.CanSetGameMode && vm.CanRefreshState, "Notification guidance must leave verified settings and manual refresh available.");
            var cleared = Next(service.Snapshots["device-a"]); cleared.Warnings = []; service.Push(cleared);
            Check(!vm.HasWarnings && vm.WarningsText.Length == 0, "A newer healthy observation must clear the warning.");
            var failedRead = Next(cleared); failedRead.Warnings = ["耳机拒绝了提示音音量读取，请手动刷新后重试。"];
            service.Push(failedRead);
            Check(vm.HasWarnings && vm.WarningsText == failedRead.Warnings.Single(), "A real failed read must remain visible.");
            await vm.SelectDeviceAsync(vm.Devices[1]);
            Check(vm.SelectedDevice?.Identity == "device-b" && !vm.HasWarnings && vm.WarningsText.Length == 0,
                "A warning from another selected headset must not remain on screen.");
            Check(service.Actions.Count == 0, "Warning replacement must not submit settings.");
        });
        await test("live read-only preview attaches the shared backend without changing a paused device", async () =>
        {
            var backend = new EventService();
            backend.Snapshots["device-a"].MonitoringState = MonitoringState.Paused;
            backend.Snapshots["device-a"].ControlReady = false;
            await using var service = new ReadOnlyPreviewService(backend);
            var devices = await service.EnumerateAsync(CancellationToken.None);
            Check(devices is [{ Identity: "device-a" }]);
            var snapshot = await service.ConnectAsync(devices[0].Identity, CancellationToken.None);
            await service.RefreshAsync(RefreshScope.Battery, CancellationToken.None);
            await service.DisconnectAsync(CancellationToken.None);
            Check(snapshot.MonitoringState == MonitoringState.Paused && !snapshot.ControlReady);
            Check(backend.Selections.SequenceEqual(["device-a"]) && backend.Pauses.Count == 0 && backend.Resumes.Count == 0);
            Check(backend.RefreshTargets.Single() == ("device-a", RefreshScope.Battery) && backend.Actions.Count == 0);
            try { await service.ApplyAsync(new("GameMode", 1), CancellationToken.None); throw new Exception("Preview admitted a setter."); }
            catch (InvalidOperationException) { }
        });
        await test("event VM attaches cached devices without polling or owning their connections", async () =>
        {
            var service = new EventService(); await using var vm = Create(service);
            await vm.InitializeAsync();
            Check(vm.HasVerifiedSnapshot && service.Subscriptions == 1 && service.Selections.SequenceEqual(["device-a"]));
            await vm.PollAsync(); await vm.PollAsync();
            Check(service.Refreshes == 0 && service.Pauses.Count == 0 && service.Resumes.Count == 0);
            await vm.SelectDeviceAsync(vm.Devices[1]);
            Check(vm.HasVerifiedSnapshot && vm.SelectedDevice?.Identity == "device-b");
            Check(service.Selections.SequenceEqual(["device-a", "device-b"]) && service.Pauses.Count == 0 && service.Resumes.Count == 0);
            await vm.ToggleGameCommand.ExecuteAsync(true);
            Check(service.Actions.Single() is { Identity: "device-b", Generation: 1, Action.Kind: "GameMode" });
        });
        await test("event VM keeps the subscribed device list only as a start-up hint until the backend is unavailable", async () =>
        {
            var service = new EventService(); await using var vm = Create(service);
            await vm.AttachBackendAsync();
            Check(vm.BackendStartupDevices?.Select(d => d.Identity).SequenceEqual(["device-a", "device-b"]) == true
                && service.Selections.Count == 0 && service.Actions.Count == 0 && service.Enumerations == 0);
            await vm.InitializeAsync(vm.BackendStartupDevices);
            Check(vm.SelectedDevice?.Identity == "device-a" && vm.HasVerifiedSnapshot && service.Subscriptions == 1
                && service.Enumerations == 0 && service.Actions.Count == 0);
            vm.AdoptBackendEvent(new BackendEvent { Kind = "unavailable" });
            Check(vm.BackendStartupDevices is null);
            var warned = new EventService { SelectionWarning = "设备选择记录已损坏，请重新选择并保存。" };
            await using var warnedVm = Create(warned);
            await warnedVm.AttachBackendAsync();
            Check(warnedVm.BackendStartupDevices is null);
            var empty = new EventService(); empty.Snapshots.Clear();
            await using var emptyVm = Create(empty);
            await emptyVm.AttachBackendAsync();
            Check(emptyVm.BackendStartupDevices is null);
        });
        await test("event VM shows fresh BLE battery but requires verified control before every write", async () =>
        {
            var service = new EventService(); await using var vm = Create(service); await vm.InitializeAsync();
            var ble = Next(service.Snapshots["device-a"]);
            ble.ControlReady = false; ble.Connected = false; ble.ConnectionState = ConnectionState.Disconnected;
            ble.MonitoringState = MonitoringState.WaitingForConnection; ble.ObservationSource = "ble";
            ble.ObservationFresh = true; ble.LeftBattery = 42; ble.LeftCharging = true;
            service.Push(ble);
            Check(vm.LeftBatteryText == "42%" && vm.LeftChargeText == "正在充电" && !vm.HasVerifiedSnapshot);
            Check(!vm.CanSetGameMode && !vm.CanEditEq && !vm.CanChooseAnc);
            await vm.ToggleGameCommand.ExecuteAsync(true); await vm.ApplyAncCommand.ExecuteAsync(null);
            Check(service.Actions.Count == 0);
            var stale = Next(ble); stale.ObservationFresh = false; service.Push(stale);
            Check(vm.LeftBatteryText == "—" && vm.LeftChargeText == "尚未读取");
            var unverified = Next(stale); unverified.Connected = true; unverified.ConnectionState = ConnectionState.Connected;
            service.Push(unverified); await vm.ToggleGameCommand.ExecuteAsync(true);
            Check(!vm.HasVerifiedSnapshot && service.Actions.Count == 0);
        });
        await test("event VM distinguishes unverified connection states from unsupported models", async () =>
        {
            var expectedSubtitles = new Dictionary<MonitoringState, string>
            {
                [MonitoringState.Unknown] = "控制连接未就绪",
                [MonitoringState.WaitingForConnection] = "等待耳机连接",
                [MonitoringState.Connecting] = "正在连接…",
                [MonitoringState.Monitoring] = "控制连接未就绪",
                [MonitoringState.Paused] = "控制连接已暂停",
                [MonitoringState.Unavailable] = "控制连接不可用"
            };
            var service = new EventService();
            var initial = service.Snapshots["device-a"];
            initial.ControlReady = false; initial.Connected = false; initial.ConnectionState = ConnectionState.Disconnected;
            initial.SupportedModel = false; initial.ProductId = null; initial.ObservationFresh = false;
            initial.MonitoringState = MonitoringState.Unavailable;
            await using var vm = Create(service); await vm.InitializeAsync();
            Check(vm.ProductId is null && vm.Subtitle == "控制连接不可用");
            Check(vm.Status == "耳机控制连接暂不可用，请双击底栏中的当前设备刷新，或三击重新连接。" && vm.StatusWarning);
            foreach (var (state, subtitle) in expectedSubtitles)
            {
                var update = Next(service.Snapshots["device-a"]); update.MonitoringState = state; service.Push(update);
                Check(vm.Subtitle == subtitle, $"Unexpected subtitle for unverified {state}: {vm.Subtitle}");
                Check(!vm.Subtitle.Contains("未适配") && !vm.Status.Contains("未适配") && vm.ProductId is null);
                Check(vm.StatusWarning == (state == MonitoringState.Unavailable));
                Check(!vm.HasVerifiedSnapshot && !vm.CanSetGameMode && !vm.CanChooseAnc && vm.CanRefreshState);
            }
            await vm.ToggleGameCommand.ExecuteAsync(true); await vm.ApplyAncCommand.ExecuteAsync(null);
            Check(service.Actions.Count == 0);
        });
        await test("event VM retains control connection diagnostics and refresh guidance until a fresh state clears them", async () =>
        {
            const string guidance = "请双击底栏中的当前设备刷新，或三击重新连接。";
            var service = new EventService();
            var initial = service.Snapshots["device-a"];
            initial.ControlReady = false; initial.Connected = false; initial.ConnectionState = ConnectionState.Disconnected;
            initial.MonitoringState = MonitoringState.Unavailable;
            initial.Failure = "查询 0x0100 超时，控制连接未就绪。";
            await using var vm = Create(service); await vm.InitializeAsync();
            Check(vm.Status.Contains(initial.Failure) && vm.Status.EndsWith(guidance) && vm.StatusWarning,
                "The initial unavailable state lost its diagnostic or reconnection guidance.");
            Check(!vm.HasVerifiedSnapshot && !vm.CanSetGameMode && vm.CanRefreshState);
            var failed = Next(initial); failed.Failure = "连接耳机控制服务失败（Windows 错误码 10060）。";
            service.Push(failed);
            Check(vm.Status.Contains(failed.Failure) && !vm.Status.Contains("0x0100") && vm.Status.EndsWith(guidance),
                "A newer failure did not replace the earlier diagnostic.");
            foreach (var empty in new string?[] { null, "", "   " })
            {
                failed = Next(failed); failed.Failure = empty; service.Push(failed);
                Check(vm.Status == $"耳机控制连接暂不可用，{guidance}" && vm.StatusWarning,
                    "An empty failure must retain the existing unavailable text.");
            }
            var healthy = Next(failed); healthy.ControlReady = true; healthy.Connected = true;
            healthy.ConnectionState = ConnectionState.Connected; healthy.MonitoringState = MonitoringState.Monitoring;
            healthy.Failure = null; service.Push(healthy);
            Check(vm.HasVerifiedSnapshot && vm.CanSetGameMode && !vm.StatusWarning && !vm.Status.Contains("10060"),
                "A healthy observation retained the previous connection failure.");
            Check(service.Actions.Count == 0 && service.Pauses.Count == 0 && service.Resumes.Count == 0,
                "Displaying a diagnostic must not submit settings or restart the connection.");
        });
        await test("event VM reserves unsupported text for an explicit unsupported-model result", async () =>
        {
            var service = new EventService(); var snapshot = service.Snapshots["device-a"];
            snapshot.ProductId = "FFFF00"; snapshot.SupportedModel = false; snapshot.ControlReady = false;
            snapshot.MonitoringState = MonitoringState.Unsupported; snapshot.ObservationFresh = true;
            await using var vm = Create(service); await vm.InitializeAsync();
            Check(vm.ProductId == "FFFF00" && vm.Subtitle == "该型号尚未适配");
            Check(vm.Status == "该型号的设置尚未适配" && !vm.HasVerifiedSnapshot && !vm.CanSetGameMode);
            await vm.ToggleGameCommand.ExecuteAsync(true); Check(service.Actions.Count == 0);
        });
        await test("event VM preserves verified firmware and fresh BLE presentation", async () =>
        {
            var service = new EventService(); service.Snapshots["device-a"].Firmware = "1234";
            await using var vm = Create(service); await vm.InitializeAsync();
            Check(vm.Subtitle == "已连接" && vm.Firmware == "1234" && vm.HasVerifiedSnapshot);
            var ble = Next(service.Snapshots["device-a"]);
            ble.ControlReady = false; ble.Connected = false; ble.ConnectionState = ConnectionState.Disconnected;
            ble.SupportedModel = false; ble.ProductId = null; ble.MonitoringState = MonitoringState.WaitingForConnection;
            ble.ObservationSource = "ble"; ble.ObservationFresh = true; ble.LeftBattery = 52; service.Push(ble);
            Check(vm.Subtitle == "已收到耳机广播" && vm.LeftBatteryText == "52%");
            Check(vm.Status == "已收到耳机广播 · 等待蓝牙连接" && !vm.HasVerifiedSnapshot);
            ble = Next(ble); ble.MonitoringState = MonitoringState.Unavailable; service.Push(ble);
            Check(vm.Subtitle == "控制连接不可用" && vm.LeftBatteryText == "52%" && vm.StatusWarning);
            Check(service.Actions.Count == 0);
        });
        await test("non-event preview preserves verified firmware and explicit unsupported presentation", async () =>
        {
            var backend = new EventService(); backend.Snapshots["device-a"].Firmware = "1234";
            await using var vm = new MainViewModel(new ReadOnlyPreviewService(backend), new Confirmations(true), preview: true);
            await vm.InitializeAsync();
            Check(vm.HasVerifiedSnapshot && vm.Subtitle == "已连接" && vm.Firmware == "1234");
            var unsupported = backend.Snapshots["device-a"];
            unsupported.ControlReady = false; unsupported.SupportedModel = false;
            unsupported.MonitoringState = MonitoringState.Unsupported; await vm.PollAsync();
            Check(!vm.HasVerifiedSnapshot && vm.Subtitle == "该型号尚未适配");
            unsupported.MonitoringState = MonitoringState.Unknown; unsupported.ProductId = null; await vm.PollAsync();
            Check(vm.Subtitle == "控制连接未就绪" && !vm.Subtitle.Contains("未适配"));
            Check(backend.Actions.Count == 0);
        });
        await test("event VM pushes preserve draft and option choices without sending settings", async () =>
        {
            var service = new EventService(); await using var vm = Create(service); await vm.InitializeAsync();
            vm.SelectedAnc = vm.AncOptions.Last(); vm.SelectedPreset = vm.PresetOptions.Last(); vm.SelectedSpatial = vm.SpatialOptions.Last();
            vm.EqName = "手机更新时保留"; vm.Bands[0].Gain = 4;
            var update = Next(service.Snapshots["device-a"]); update.LeftBattery = 67; update.GameMode = true;
            update.EqualizerEntries = [new(1, "手机均衡器", -6, 6, true, [new(31, -2)])]; service.Push(update);
            Check(vm.LeftBatteryText == "67%" && vm.GameMode == true);
            Check(vm.SelectedAnc?.Key == 0x0107 && vm.SelectedPreset?.Key == 7 && vm.SelectedSpatial?.Key == 2);
            Check(vm.EqDirty && vm.EqName == "手机更新时保留" && vm.Bands[0].Gain == 4 && service.Actions.Count == 0);
        });
        await test("event VM retains staged protocol IDs through empty unverified options and a new control generation", async () =>
        {
            var service = new EventService(); await using var vm = Create(service); await vm.InitializeAsync();
            var initial = Copy(service.Snapshots["device-a"]);
            vm.SelectedAnc = vm.AncOptions.Last(); vm.SelectedPreset = vm.PresetOptions.Last(); vm.SelectedSpatial = vm.SpatialOptions.Last();
            vm.EqName = "控制重连草稿"; vm.Bands[0].Gain = 4;
            var waiting = Next(initial); waiting.DeviceGeneration = 2; waiting.Revision = 1;
            waiting.ControlReady = false; waiting.Connected = false; waiting.ConnectionState = ConnectionState.Disconnected;
            waiting.MonitoringState = MonitoringState.WaitingForConnection; waiting.ObservationFresh = true;
            waiting.AncOptions = []; waiting.EqualizerOptions = []; waiting.SpatialOptions = []; waiting.EqualizerEntries = [];
            service.Push(waiting);
            Check(vm.SelectedAnc?.Key == 0x0107 && vm.SelectedPreset?.Key == 7 && vm.SelectedSpatial?.Key == 2);
            Check(!vm.CanChooseAnc && !vm.CanChoosePreset && !vm.CanChooseSpatial && !vm.CanEditEq);
            await vm.ApplyAncCommand.ExecuteAsync(null); Check(service.Actions.Count == 0);
            var restored = Copy(initial); restored.DeviceGeneration = 3; restored.Revision = 1; service.Push(restored);
            Check(vm.HasVerifiedSnapshot && vm.SelectedAnc?.Key == 0x0107 && vm.SelectedPreset?.Key == 7 && vm.SelectedSpatial?.Key == 2);
            Check(vm.EqDirty && vm.EqName == "控制重连草稿" && vm.Bands[0].Gain == 4 && service.Actions.Count == 0);
            await vm.ApplyAncCommand.ExecuteAsync(null);
            Check(service.Actions.Single() is { Generation: 3, Action.Value: 0x0107 });
            var phone = Next(service.Snapshots["device-a"]); phone.Anc = 0x0104; service.Push(phone);
            Check(vm.SelectedAnc?.Key == 0x0104, "A completed submission must release its staged protocol ID.");
        });
        await test("event VM rejects older revisions, generations, and a retired backend", async () =>
        {
            var service = new EventService(); await using var vm = Create(service); await vm.InitializeAsync();
            var latest = Next(service.Snapshots["device-a"]); latest.Revision = 8; latest.LeftBattery = 68; service.Push(latest);
            var late = Copy(latest); late.Revision = 7; late.LeftBattery = 10; service.Push(late);
            Check(vm.LeftBatteryText == "68%");
            var sameRevision = Copy(latest); sameRevision.LeftBattery = 11; service.Push(sameRevision);
            Check(vm.LeftBatteryText == "68%");
            var generation = Copy(latest); generation.DeviceGeneration = 2; generation.Revision = 1; generation.LeftBattery = 53; service.Push(generation);
            late.Revision = 100; service.Push(late); Check(vm.LeftBatteryText == "53%");
            var restarted = Copy(generation); restarted.BackendInstanceId = "backend-b"; restarted.DeviceGeneration = 1;
            restarted.Revision = 1; restarted.LeftBattery = 31; service.Push(restarted);
            service.Push(generation); Check(vm.LeftBatteryText == "31%" && vm.HasVerifiedSnapshot);
        });
        await test("event VM ignores a stale explicit refresh after a newer phone update", async () =>
        {
            var service = new EventService { RefreshBlocked = Completion<UiSnapshot>() };
            await using var vm = Create(service); await vm.InitializeAsync();
            var old = Copy(service.Snapshots["device-a"]); var read = vm.RefreshStateCommand.ExecuteAsync(null);
            var phone = Next(old); phone.GameMode = true; phone.LeftBattery = 61; service.Push(phone);
            service.RefreshBlocked.SetResult(old); await read.WaitAsync(TimeSpan.FromSeconds(3));
            Check(vm.GameMode == true && vm.LeftBatteryText == "61%" && service.Refreshes == 1);
            Check(service.RefreshTargets.Single() == ("device-a", RefreshScope.Full) && service.Actions.Count == 0);
        });
        await test("event VM cannot roll back a newer push with an older successful write receipt", async () =>
        {
            var service = new EventService { ApplyBlocked = Completion<UiApplyResult>() };
            await using var vm = Create(service); await vm.InitializeAsync();
            var after = Next(service.Snapshots["device-a"]); after.GameMode = true;
            var write = vm.ToggleGameCommand.ExecuteAsync(true);
            var phone = Next(after); phone.GameMode = false; phone.LeftBattery = 55; service.Push(phone);
            service.ApplyBlocked.SetResult(Confirmed(after)); await write.WaitAsync(TimeSpan.FromSeconds(3));
            Check(vm.GameMode == false && vm.LeftBatteryText == "55%" && vm.HasVerifiedSnapshot);
            Check(service.Actions.Count == 1 && !vm.Busy && vm.LastApplyAttempts.Single().Accepted);
        });
        await test("event VM rejects write evidence from a different backend control session", async () =>
        {
            var service = new EventService { ApplyBlocked = Completion<UiApplyResult>() };
            await using var vm = Create(service); await vm.InitializeAsync();
            var write = vm.ToggleGameCommand.ExecuteAsync(true);
            var foreign = Next(service.Snapshots["device-a"]); foreign.BackendInstanceId = "retired-backend";
            foreign.GameMode = true; service.ApplyBlocked.SetResult(Confirmed(foreign));
            await write.WaitAsync(TimeSpan.FromSeconds(3));
            Check(service.Actions.Count == 1 && vm.HasVerifiedSnapshot && vm.GameMode == false);
            Check(vm.LastApplyAttempts.Single().FailureKind == ApplyFailure.WorkerUnavailable && vm.StatusWarning);
        });
        await test("event VM freezes queued option values and retains edits made after submission", async () =>
        {
            var service = new EventService { RefreshBlocked = Completion<UiSnapshot>() };
            await using var vm = Create(service); await vm.InitializeAsync();
            var read = vm.RefreshStateCommand.ExecuteAsync(null);
            vm.SelectedAnc = vm.AncOptions.Last(); var write = vm.ApplyAncCommand.ExecuteAsync(null);
            vm.SelectedAnc = vm.AncOptions.First();
            Check(service.Actions.Count == 0);
            service.RefreshBlocked.SetResult(service.Snapshots["device-a"]);
            await read.WaitAsync(TimeSpan.FromSeconds(3)); await write.WaitAsync(TimeSpan.FromSeconds(3));
            Check(service.Actions.Single() is { Identity: "device-a", Generation: 1, Action.Value: 0x0107 });
            Check(vm.SelectedAnc?.Key == 0x0104 && service.Snapshots["device-a"].Anc == 0x0107);
        });
        await test("event VM drops queued settings across a device generation change while retaining EQ drafts", async () =>
        {
            var service = new EventService { RefreshBlocked = Completion<UiSnapshot>() };
            await using var vm = Create(service); await vm.InitializeAsync(); vm.EqName = "断线保留"; vm.Bands[0].Gain = 3;
            var read = vm.RefreshStateCommand.ExecuteAsync(null); var queued = vm.ToggleGameCommand.ExecuteAsync(true);
            var reconnected = Next(service.Snapshots["device-a"]); reconnected.DeviceGeneration++;
            reconnected.Revision = 1; service.Push(reconnected);
            await read.WaitAsync(TimeSpan.FromSeconds(3)); await queued.WaitAsync(TimeSpan.FromSeconds(3));
            Check(vm.HasVerifiedSnapshot && vm.EqDirty && vm.EqName == "断线保留" && vm.Bands[0].Gain == 3);
            Check(service.Actions.Count == 0 && !vm.Busy && !vm.IsRefreshing);
        });
        await test("event VM does not replay an in-flight setting after backend receipt loss", async () =>
        {
            var service = new EventService { ApplyBlocked = Completion<UiApplyResult>() };
            await using var vm = Create(service); await vm.InitializeAsync();
            var write = vm.ToggleGameCommand.ExecuteAsync(true);
            service.Publish(new() { Kind = "unavailable", Message = "后台版本不兼容，请更新后重启。" });
            Check(!vm.HasVerifiedSnapshot && vm.Status.Contains("版本不兼容"));
            service.ApplyBlocked.SetException(new IOException("pipe ended")); await write.WaitAsync(TimeSpan.FromSeconds(3));
            var replacement = Copy(service.Snapshots["device-a"]); replacement.BackendInstanceId = "backend-b";
            replacement.Revision = 1; service.Push(replacement); await vm.PollAsync();
            Check(service.Actions.Count == 1 && vm.HasVerifiedSnapshot && !vm.Busy);
        });
        await test("event VM cancels a queued main mode across generation changes and restores the observed radio", async () =>
        {
            var service = new EventService { RefreshBlocked = Completion<UiSnapshot>() };
            var initial = service.Snapshots["device-a"];
            initial.AncPrimary = 1; initial.CanSetAncMode = true;
            initial.AncOptions = initial.AncOptions.Append(new UiOption(0x0B0D, "标准")).ToArray();
            await using var vm = Create(service); await vm.InitializeAsync();
            var read = vm.RefreshStateCommand.ExecuteAsync(null);
            var queued = vm.SelectAncModeAsync(vm.AncModes.Single(x => x.Key == 11));
            Check(vm.SelectedAncMode?.Key == 11 && service.Actions.Count == 0);
            var reconnected = Next(initial); reconnected.DeviceGeneration++; reconnected.Revision = 1;
            service.Push(reconnected);
            await Task.WhenAll(read, queued).WaitAsync(TimeSpan.FromSeconds(3));
            Check(vm.HasVerifiedSnapshot && vm.SelectedAncMode?.Key == 1 && vm.CanSetAncMode && service.Actions.Count == 0);
            await vm.SelectAncModeAsync(vm.AncModes.Single(x => x.Key == 11));
            Check(service.Actions.Single() is { Generation: 2, Action.Kind: "AncMode", Action.Value: 11 });
        });
        await test("event VM immediately replaces an old main intent with the new generation state before its receipt", async () =>
        {
            var service = new EventService { ApplyBlocked = Completion<UiApplyResult>() };
            var initial = service.Snapshots["device-a"];
            initial.AncPrimary = 1; initial.CanSetAncMode = true;
            initial.AncOptions = initial.AncOptions.Append(new UiOption(0x0B0D, "标准")).ToArray();
            await using var vm = Create(service); await vm.InitializeAsync();
            var write = vm.SelectAncModeAsync(vm.AncModes.Single(x => x.Key == 11));
            Check(service.Actions.Count == 1 && vm.SelectedAncMode?.Key == 11);
            var reconnected = Next(initial); reconnected.DeviceGeneration++; reconnected.Revision = 1;
            service.Push(reconnected);
            Check(!write.IsCompleted && vm.SelectedAncMode?.Key == 1 && vm.SelectedAncSubmode?.Key == 0x0104);
            var oldReceipt = Next(initial); oldReceipt.AncPrimary = 11; oldReceipt.Anc = null;
            service.ApplyBlocked.SetResult(Confirmed(oldReceipt)); await write.WaitAsync(TimeSpan.FromSeconds(3));
            Check(vm.SelectedAncMode?.Key == 1 && vm.CanSetAncMode && service.Actions.Count == 1);
        });
        await test("event VM discards the last queued ANC target across target and capability changes", async () =>
        {
            foreach (var change in new[] { "generation", "backend", "disconnect", "capability", "selection" })
            {
                var service = new EventService { ApplyBlocked = Completion<UiApplyResult>() };
                var initial = service.Snapshots["device-a"];
                initial.AncPrimary = 1; initial.CanSetAncMode = true;
                initial.AncOptions = initial.AncOptions.Append(new UiOption(0x0B0D, "标准")).ToArray();
                await using var vm = Create(service); await vm.InitializeAsync();
                var first = vm.SelectAncModeAsync(vm.AncModes.Single(x => x.Key == 11));
                var laterMode = vm.SelectAncModeAsync(vm.AncModes.Single(x => x.Key == 1));
                var laterChild = vm.ApplyAncSubmodeAsync(vm.SelectedAncMode!.Options.Last());
                Check(service.Actions.Count == 1 && vm.SelectedAncSubmode?.Key == 0x0107);
                var changed = Next(Next(initial));
                Task selection = Task.CompletedTask;
                switch (change)
                {
                    case "generation": changed.DeviceGeneration++; changed.Revision = 1; break;
                    case "backend": changed.BackendInstanceId = "backend-b"; changed.Revision = 1; break;
                    case "disconnect": changed.ControlReady = false; changed.Connected = false;
                        changed.ConnectionState = ConnectionState.Disconnected; break;
                    case "capability": changed.CanSetAnc = false; changed.CanSetAncMode = false; break;
                    case "selection": selection = vm.SelectDeviceAsync(vm.Devices.Single(x => x.Identity == "device-b")); break;
                }
                if (change != "selection") service.Push(changed);
                var oldReceipt = Next(initial); oldReceipt.AncPrimary = 11; oldReceipt.Anc = null;
                service.ApplyBlocked.SetResult(Confirmed(oldReceipt));
                await Task.WhenAll(first, laterMode, laterChild, selection).WaitAsync(TimeSpan.FromSeconds(3));
                Check(service.Actions.Count == 1, "A changed control target must discard every queued ANC write: " + change);
                Check(vm.SelectedAncSubmode?.Key == 0x0104, "A stale receipt must not restore the queued selection: " + change);
            }
        });
        await test("event VM accepts a new ANC click in a changed context after the old blocked SET fails", async () =>
        {
            foreach (var change in new[] { "generation", "backend" })
            {
                var service = new EventService { ApplyBlocked = Completion<UiApplyResult>() };
                var initial = service.Snapshots["device-a"];
                initial.AncPrimary = 1; initial.CanSetAncMode = true;
                initial.AncOptions = initial.AncOptions.Append(new UiOption(0x0B0D, "标准")).ToArray();
                await using var vm = Create(service); await vm.InitializeAsync();
                var old = vm.SelectAncModeAsync(vm.AncModes.Single(x => x.Key == 11));
                var changed = Next(initial);
                if (change == "generation") { changed.DeviceGeneration++; changed.Revision = 1; }
                else { changed.BackendInstanceId = "backend-b"; changed.Revision = 1; }
                service.Push(changed);
                var fresh = vm.ApplyAncSubmodeAsync(vm.SelectedAncMode!.Options.Last());
                Check(service.Actions.Count == 1 && vm.SelectedAncSubmode?.Key == 0x0107);
                var oldReceipt = service.ApplyBlocked!;
                service.ApplyBlocked = null;
                oldReceipt.SetException(new IOException("Old control request ended"));
                await Task.WhenAll(old, fresh).WaitAsync(TimeSpan.FromSeconds(3));
                Check(service.Actions.Count == 2 && service.Actions.Last() is { Action.Kind: "Anc", Action.Value: 0x0107 });
                Check(service.Actions.Last().Generation == changed.DeviceGeneration && vm.SelectedAncSubmode?.Key == 0x0107);
                Check(vm.CanSetAnc);
            }
        });
        await test("event VM header indicator follows control, connection and pending writes", async () =>
        {
            var service = new EventService { ApplyBlocked = Completion<UiApplyResult>() };
            service.Snapshots["device-a"].CaseCharging = true;
            await using var vm = Create(service); await vm.InitializeAsync();
            Check(vm.Indicator == ConnectionIndicator.Online && vm.Subtitle == "已连接" && !vm.LeftCharging && vm.CaseCharging);
            var after = Next(service.Snapshots["device-a"]); after.GameMode = true;
            var write = vm.ToggleGameCommand.ExecuteAsync(true);
            Check(vm.Indicator == ConnectionIndicator.Pending && vm.HasVerifiedSnapshot, "A pending write shows the connecting/writing state.");
            service.ApplyBlocked.SetResult(Confirmed(after)); await write.WaitAsync(TimeSpan.FromSeconds(3));
            Check(vm.Indicator == ConnectionIndicator.Online, "A completed write returns to the connected state.");
            var sample = new DateTimeOffset(2026, 10, 9, 6, 32, 0, TimeSpan.Zero);
            var cached = Next(after);
            cached.ControlReady = false; cached.Connected = false; cached.ConnectionState = ConnectionState.Disconnected;
            cached.PresentationCached = true; cached.PresentationSampleTime = sample; cached.MonitoringState = MonitoringState.WaitingForConnection;
            service.Push(cached);
            var expected = "上次连接 " + sample.ToLocalTime().ToString("yyyy/M/d HH:mm", System.Globalization.CultureInfo.InvariantCulture);
            Check(vm.Indicator == ConnectionIndicator.Offline && vm.Subtitle == expected, $"Offline cache shows the last connection: {vm.Subtitle}");
            var connecting = Next(cached); connecting.MonitoringState = MonitoringState.Connecting; service.Push(connecting);
            Check(vm.Indicator == ConnectionIndicator.Pending && vm.Subtitle == "正在连接…", "A reconnect in progress shows the connecting state.");
            var uncached = Next(connecting); uncached.PresentationCached = false; uncached.PresentationSampleTime = null;
            uncached.MonitoringState = MonitoringState.Paused; service.Push(uncached);
            Check(vm.Indicator == ConnectionIndicator.Offline && vm.Subtitle == "控制连接已暂停" && vm.ConnectButtonText == "连接耳机" && !vm.ShowsDisconnect);
        });
        await test("event VM manual button pauses only its selected peer and BLE display keeps updating", async () =>
        {
            var service = new EventService(); await using var vm = Create(service); await vm.InitializeAsync();
            await vm.ConnectCommand.ExecuteAsync(null);
            Check(service.Pauses.SequenceEqual(["device-a"]) && service.Resumes.Count == 0);
            Check(vm.ConnectButtonText == "连接耳机" && !vm.HasVerifiedSnapshot && service.Snapshots["device-b"].ControlReady);
            var ble = Next(service.Snapshots["device-a"]); ble.LeftBattery = 47; service.Push(ble);
            Check(vm.LeftBatteryText == "47%" && !vm.CanSetGameMode);
            await vm.ConnectCommand.ExecuteAsync(null);
            Check(service.Resumes.SequenceEqual(["device-a"]) && vm.HasVerifiedSnapshot && vm.ConnectButtonText == "断开客户端");
        });
        await test("event VM reconnect pauses, waits and resumes only the selected peer without writes", async () =>
        {
            var service = new EventService(); await using var vm = Create(service); await vm.InitializeAsync();
            vm.ReconnectDelay = TimeSpan.FromMilliseconds(400);
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var reconnect = vm.ReconnectCommand.ExecuteAsync(null);
            await Task.Delay(120);
            Check(service.Pauses.SequenceEqual(["device-a"]) && service.Resumes.Count == 0 && vm.Busy && !vm.CanReconnect
                && !vm.CanRefreshDevices && vm.Status == "正在重新连接耳机…", "Reconnect pauses first and holds the lifecycle gate during the delay.");
            await reconnect.WaitAsync(TimeSpan.FromSeconds(5));
            // Allow timer resolution variance; the earlier check still requires the pause and lifecycle gate.
            var timerTolerance = TimeSpan.FromMilliseconds(20);
            Check(watch.Elapsed >= vm.ReconnectDelay - timerTolerance && service.Resumes.SequenceEqual(["device-a"]) && vm.HasVerifiedSnapshot,
                $"Reconnect resumes the same peer after the delay: elapsed={watch.Elapsed.TotalMilliseconds:F3}ms, delay={vm.ReconnectDelay.TotalMilliseconds:F3}ms, resumes=[{string.Join(",", service.Resumes)}], verified={vm.HasVerifiedSnapshot}.");
            Check(service.Snapshots["device-b"].ControlReady && service.Actions.Count == 0 && vm.LogText.Contains("已重新发起耳机连接"),
                "Reconnect leaves other peers alone, replays no setting and logs the request.");
        });
        await test("event VM reconnect of a paused peer resumes at once and dirty EQ needs confirmation", async () =>
        {
            var service = new EventService(); await using var vm = Create(service, new Confirmations(false)); await vm.InitializeAsync();
            await vm.ConnectCommand.ExecuteAsync(null);
            vm.ReconnectDelay = TimeSpan.FromSeconds(10);
            var watch = System.Diagnostics.Stopwatch.StartNew();
            await vm.ReconnectCommand.ExecuteAsync(null).WaitAsync(TimeSpan.FromSeconds(5));
            Check(service.Pauses.Count == 1 && service.Resumes.SequenceEqual(["device-a"]) && vm.HasVerifiedSnapshot
                && watch.Elapsed < TimeSpan.FromSeconds(5), "A paused peer only resumes, without the disconnect delay.");
            vm.EqName = "重连前草稿"; vm.Bands[0].Gain = 4;
            await vm.ReconnectCommand.ExecuteAsync(null);
            Check(service.Pauses.Count == 1 && service.Resumes.Count == 1 && vm.EqDirty && vm.Bands[0].Gain == 4 && vm.HasVerifiedSnapshot,
                "Declining the unsaved-EQ confirmation leaves the session and the draft untouched.");
        });
        await test("event VM closing during a reconnect delay resumes the paused peer before disposal", async () =>
        {
            var service = new EventService(); var vm = Create(service); await vm.InitializeAsync();
            vm.ReconnectDelay = TimeSpan.FromSeconds(30);
            var reconnect = vm.ReconnectCommand.ExecuteAsync(null);
            var watch = System.Diagnostics.Stopwatch.StartNew();
            while (service.Pauses.Count == 0 && watch.Elapsed < TimeSpan.FromSeconds(5)) await Task.Delay(10);
            await vm.DisposeAsync();
            await reconnect.WaitAsync(TimeSpan.FromSeconds(5));
            Check(service.Pauses.SequenceEqual(["device-a"]) && service.Resumes.SequenceEqual(["device-a"]) && service.Disposals == 1
                && service.Actions.Count == 0, "The backend keeps no pause left behind by an interrupted reconnect.");
        });
        await test("event VM device management detaches display while backend monitoring continues", async () =>
        {
            var service = new EventService(); await using var vm = Create(service); await vm.InitializeAsync();
            Check(await vm.PrepareDeviceManagementAsync() && vm.ManagingDevices && !vm.HasVerifiedSnapshot);
            var push = Next(service.Snapshots["device-a"]); push.LeftBattery = 39; service.Push(push);
            Check(service.Pauses.Count == 0 && !vm.HasVerifiedSnapshot);
            Check(await vm.CompleteDeviceManagementAsync(false));
            Check(vm.HasVerifiedSnapshot && vm.LeftBatteryText == "39%" && service.Selections.Count == 2);
        });
        await test("event VM backend exit uses draft voting before detaching, and cancellation restores editing", async () =>
        {
            var service = new EventService(); var confirmations = new Confirmations(false, true, true);
            var vm = Create(service, confirmations); await vm.InitializeAsync(); vm.EqName = "托盘退出确认";
            Check(!await vm.ConfirmBackendExitAsync() && vm.EqDirty && vm.CanEditEq && service.Disposals == 0);
            Check(await vm.ConfirmBackendExitAsync() && vm.Closing && service.Disposals == 0);
            service.Publish(new() { Kind = "exitCancelled" });
            Check(!vm.Closing && vm.EqDirty && vm.CanEditEq);
            Check(await vm.ConfirmBackendExitAsync() && confirmations.Calls == 3);
            Check(await vm.RequestCloseAsync() && confirmations.Calls == 3 && service.Disposals == 1);
            await vm.DisposeAsync(); Check(service.Disposals == 1 && service.Pauses.Count == 0);
        });
    }

    private static MainViewModel Create(EventService service, Confirmations? confirmations = null)
    {
        var vm = new MainViewModel(service, confirmations ?? new(true));
        service.EventReceived += vm.AdoptBackendEvent;
        return vm;
    }
    private static void Check(bool value, string message = "Event VM assertion failed.")
    { if (!value) throw new InvalidOperationException(message); }
    private static TaskCompletionSource<T> Completion<T>() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static UiSnapshot Copy(UiSnapshot snapshot) => JsonSerializer.Deserialize<UiSnapshot>(JsonSerializer.Serialize(snapshot))!;
    private static UiSnapshot Next(UiSnapshot snapshot) { var next = Copy(snapshot); next.Revision++; return next; }
    private static UiApplyResult Confirmed(UiSnapshot after) => new()
    {
        BackendReceiptReceived = true, ConnectionState = ConnectionState.Connected, FailureKind = ApplyFailure.None,
        TargetSubmitted = true, AckStatus = 0, Accepted = true, ReadbackMatches = true, SubmissionAttempted = true, After = after
    };
    private static UiSnapshot Snapshot(string identity) => new()
    {
        BackendInstanceId = "backend-a", DeviceGeneration = 1, Revision = 1, ControlReady = true,
        MonitoringState = MonitoringState.Monitoring, ObservationFresh = true, ObservationSource = "spp",
        Identity = identity, ProductId = "06EC10", DeviceName = identity == "device-a" ? "OPPO Enco X4" : "另一副耳机",
        Connected = true, ConnectionState = ConnectionState.Connected, SupportedModel = true,
        LeftBattery = 90, RightBattery = 80, CaseBattery = 70, Anc = 0x0104, Equalizer = 0, Spatial = 0,
        HiRes = true, DualConnection = true, GameMode = false,
        CanSetAnc = true, CanSetEqualizer = true, CanSetCustomEqualizer = true, CanSetSpatial = true,
        CanSetHiRes = true, CanSetDualConnection = true, CanSetGameMode = true, SpatialHiResMutex = true,
        AncOptions = [new(0x0104, "深度降噪"), new(0x0107, "智能降噪")],
        EqualizerOptions = [new(0, "丹拿原声"), new(7, "丹拿高解析")], SpatialOptions = [new(0, "关闭"), new(2, "头部追踪")],
        EqualizerEntries = [new(1, "均衡器一", -6, 6, true, [new(31, 0)]), new(2, "均衡器二", -6, 6, false, [new(31, 2)])]
    };
    private sealed class Confirmations(params bool[] responses) : IConfirmationService
    {
        public int Calls;
        public Task<bool> ConfirmAsync(string title, string message, CancellationToken cancellationToken) =>
            Task.FromResult(responses[Math.Min(Calls++, responses.Length - 1)]);
    }
    private sealed record Submission(string Identity, long Generation, UiAction Action);
    private sealed class EventService : IHeadsetEventService
    {
        public event Action<BackendEvent>? EventReceived;
        public Dictionary<string, UiSnapshot> Snapshots { get; } = new(StringComparer.Ordinal)
        { ["device-a"] = Snapshot("device-a"), ["device-b"] = Snapshot("device-b") };
        public int Subscriptions, Refreshes, Disposals, Enumerations;
        public string? SelectionWarning;
        public UiPlatformStatus? Platform;
        public List<string> Selections { get; } = [];
        public List<string> Pauses { get; } = [];
        public List<string> Resumes { get; } = [];
        public List<Submission> Actions { get; } = [];
        public List<(string Identity, RefreshScope Scope)> RefreshTargets { get; } = [];
        public TaskCompletionSource<UiSnapshot>? RefreshBlocked;
        public TaskCompletionSource<UiApplyResult>? ApplyBlocked;
        public void Publish(BackendEvent notification) => EventReceived?.Invoke(notification);
        public void Push(UiSnapshot snapshot)
        {
            Snapshots[snapshot.Identity] = snapshot;
            Publish(new() { Kind = "snapshot", BackendInstanceId = snapshot.BackendInstanceId, Snapshot = snapshot });
        }
        public Task<BackendSubscription> SubscribeAsync(string role, CancellationToken cancellationToken)
        {
            Check(role == "main"); Subscriptions++;
            return Task.FromResult(new BackendSubscription
            { BackendInstanceId = Snapshots.Values.FirstOrDefault()?.BackendInstanceId ?? "instance", Devices = Devices(),
                Catalog = new([], SelectionWarning), Snapshots = Snapshots.Values.ToArray(), Platform = Platform });
        }
        private IReadOnlyList<UiDevice> Devices() => Snapshots.Values.Select(snapshot => new UiDevice(snapshot.Identity, snapshot.DeviceName, snapshot.Connected)).ToArray();
        public Task<IReadOnlyList<UiDevice>> EnumerateAsync(CancellationToken cancellationToken)
        { Enumerations++; return Task.FromResult(Devices()); }
        public Task<UiSnapshot> SelectAsync(string identity, CancellationToken cancellationToken)
        { Selections.Add(identity); return Task.FromResult(Snapshots[identity]); }
        public Task<UiSnapshot> PauseAsync(string identity, CancellationToken cancellationToken)
        {
            Pauses.Add(identity); var after = Next(Snapshots[identity]); after.MonitoringState = MonitoringState.Paused;
            after.ControlReady = false; Snapshots[identity] = after; return Task.FromResult(after);
        }
        public Task<UiSnapshot> ResumeAsync(string identity, CancellationToken cancellationToken)
        {
            Resumes.Add(identity); var after = Next(Snapshots[identity]); after.MonitoringState = MonitoringState.Monitoring;
            after.ControlReady = true; Snapshots[identity] = after; return Task.FromResult(after);
        }
        public Task<UiSnapshot> RefreshAsync(string identity, RefreshScope scope, CancellationToken cancellationToken)
        {
            Refreshes++; RefreshTargets.Add((identity, scope));
            return RefreshBlocked?.Task.WaitAsync(cancellationToken) ?? Task.FromResult(Snapshots[identity]);
        }
        public Task<UiApplyResult> ApplyAsync(string identity, long deviceGeneration, UiAction action, CancellationToken cancellationToken)
        {
            Actions.Add(new(identity, deviceGeneration, action));
            if (ApplyBlocked is not null) return ApplyBlocked.Task.WaitAsync(cancellationToken);
            var after = Next(Snapshots[identity]);
            switch (action.Kind)
            {
                case "Anc": after.Anc = action.Value; break;
                case "AncMode": after.AncPrimary = action.Value; after.Anc = null; break;
                case "Equalizer": after.Equalizer = action.Value; break;
                case "GameMode": after.GameMode = action.Value == 1; break;
            }
            Snapshots[identity] = after; return Task.FromResult(Confirmed(after));
        }
        public Task AcknowledgeExitAsync(string exitRequestId, bool approved, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task ActivateMainUiAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<UiSnapshot> ConnectAsync(string identity, CancellationToken cancellationToken) => throw new InvalidOperationException("Legacy connect must not own backend monitoring.");
        public Task DisconnectAsync(CancellationToken cancellationToken) => throw new InvalidOperationException("Selecting or closing must not disconnect shared monitoring.");
        public Task<UiSnapshot> RefreshAsync(CancellationToken cancellationToken) => throw new InvalidOperationException("Refresh must freeze its device target.");
        public Task<UiApplyResult> ApplyAsync(UiAction action, CancellationToken cancellationToken) => throw new InvalidOperationException("Apply must freeze its device target and generation.");
        public ValueTask DisposeAsync() { Disposals++; return ValueTask.CompletedTask; }
    }
}
