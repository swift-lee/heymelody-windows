using System.Text.Json;
using HeyMelodyWindows.App;

internal static class DemoVmTests
{
    public static async Task RunAsync(Func<string, Func<Task>, Task> test)
    {
        await test("demo has a complete editable layout and is added only once", async () =>
        {
            var service = new CountingService();
            await using var vm = Create(service);
            await vm.InitializeAsync();
            var calls = service.Calls.Count;
            await vm.AddDemoDeviceAsync();
            var identity = vm.SelectedDevice?.Identity;
            await vm.AddDemoDeviceAsync();
            Check(vm.IsDemoSelected && vm.HasVerifiedSnapshot && vm.Devices.Count(MainViewModel.IsDemoDevice) == 1);
            Check(vm.SelectedDevice?.Identity == identity && service.Calls.Count == calls);
            Check(vm.ProductId == "06C010" && vm.SelectedDevice?.Name == "Demo · Enco Free4 丹拿版"
                && DemoDeviceData.CreateSnapshot().Color == 3,
                "The demo uses the verified Free4 Dynaudio product and artwork color.");
            Check(!MainViewModel.IsDemoDevice(null) && !MainViewModel.IsDemoDevice(service.Devices[0]));
            Check(vm.CanChooseAnc && vm.CanChoosePreset && vm.CanChooseSpatial && vm.CanEditEq);
            Check(vm.AncModes.Count >= 3 && vm.AncModes.Any(mode => mode.Options.Count > 1));
            Check(vm.PresetOptions.Count > 1 && vm.Bands.Count == 10 && vm.EqEntries.Count > 0);
            Check(vm.CanSelectHearingProfile && vm.CanSetupHearing && vm.CanFitTest);
            var controls = vm.FeatureSections.SelectMany(section => section.Controls).Concat(vm.EqualizerFeatures).ToArray();
            Check(!controls.Any(control => control.Id is "auto-volume" or "game-sound")
                && controls.Any(control => control.Id == "adaptive-volume"),
                "Retired volume and game type controls stay absent while adaptive volume remains independent.");
            Check(vm.EqualizerFeatures.Select(control => control.Id).SequenceEqual(["bass-enabled", "bass"])
                && !vm.FeatureSections.SelectMany(section => section.Controls).Any(control => control.Id.StartsWith("bass", StringComparison.Ordinal)),
                "BassWave controls appear together in the equalizer panel.");
            Check(controls.Select(control => control.Kind).ToHashSet().SetEquals(["Switch", "Slider", "Radio", "Text", "Combo"]));
            Check(controls.All(control => control.CanEdit) && vm.FeatureSections.All(section => section.DefaultExpanded));
            Check(controls.Count(control => control.Id.StartsWith("gesture:", StringComparison.Ordinal)) == 12,
                "The demo includes all six gestures for both ears.");
        });

        await test("demo setters, EQ and hearing selection remain local", async () =>
        {
            var service = new CountingService(); var confirmations = new Confirmations(false);
            await using var vm = Create(service, confirmations);
            await vm.InitializeAsync(); await vm.AddDemoDeviceAsync();
            var calls = service.Calls.Count;
            await vm.ToggleDualCommand.ExecuteAsync(false);
            await vm.ToggleGameCommand.ExecuteAsync(true);
            await vm.ToggleHiResCommand.ExecuteAsync(false);
            Check(vm.DualConnection == false && vm.GameMode == true && vm.HiRes == false);
            await vm.SelectAncModeAsync(vm.AncModes.Last());
            Check(vm.SelectedAncMode?.Key == vm.AncModes.Last().Key && vm.SelectedAnc is null && vm.SelectedAncSubmode is null);
            await vm.ApplyAncSubmodeAsync(vm.SelectedAncMode!.Options.First());
            Check(vm.SelectedAnc?.Key == vm.AncModes.Last().Options.First().Key);
            var preset = vm.PresetOptions.Last();
            await vm.SelectPresetAsync(preset); Check(vm.SelectedPreset?.Key == preset.Key);
            var spatial = vm.SpatialOptions.Last();
            vm.SelectedSpatial = spatial; await vm.ApplySpatialCommand.ExecuteAsync(null);
            Check(vm.SelectedSpatial?.Key == spatial.Key);
            foreach (var id in vm.FeatureSections.SelectMany(section => section.Controls).Concat(vm.EqualizerFeatures).Select(control => control.Id).ToArray())
            {
                var control = Feature(vm, id);
                switch (control.Kind)
                {
                    case "Switch": control.BooleanValue = !control.BooleanValue; break;
                    case "Slider": control.NumberValue = control.Maximum; break;
                    case "Text": control.TextValue = "布局输入示例"; break;
                    case "Radio" or "Combo": control.SelectedOption = control.Options.Last(); break;
                }
                var expected = control.Kind == "Switch" ? control.BooleanValue ? 1 : 0
                    : control.Kind is "Radio" or "Combo" ? control.SelectedOption!.Key : (int)control.NumberValue;
                await control.SubmitAsync();
                var after = Feature(vm, id);
                Check(control.Kind == "Text" ? after.TextValue == "布局输入示例" : after.Definition.Value == expected,
                    "Local control state must survive rebuilding its template: " + id);
            }
            var profile = vm.HearingProfiles.Last(profile => profile.Uid.HasValue);
            await vm.SelectHearingProfileAsync(profile);
            Check(vm.SelectedHearingProfile?.Uid == profile.Uid);
            await vm.NewEqCommand.ExecuteAsync(null);
            vm.EqName = "本地新建 EQ"; vm.Bands[0].Gain = 4;
            await vm.SaveEqCommand.ExecuteAsync(null);
            Check(!vm.EqDirty && vm.EqEntries.Any(entry => entry.Name == "本地新建 EQ" && entry.Bands[0].Gain == 4));
            vm.Bands[0].Gain = -3; vm.ResetEqCommand.Execute(null);
            Check(!vm.EqDirty && vm.Bands[0].Gain == 4);
            await vm.UseEqCommand.ExecuteAsync(null);
            await vm.ConnectCommand.ExecuteAsync(null); await vm.RefreshStateCommand.ExecuteAsync(null); await vm.PollAsync();
            Check(vm.IsDemoSelected && vm.HasVerifiedSnapshot && service.Calls.Count == calls && confirmations.Calls == 0,
                "Every demo operation is in memory and never submits to the backend.");
            foreach (var kind in new[] { "fit", "hearing" })
            {
                try { await vm.CreateCalibrationAsync(kind); throw new Exception("Demo calibration escaped its guard."); }
                catch (InvalidOperationException) { }
            }
            Check(service.Calls.Count == calls);
        });

        await test("catalog changes and backend interruption preserve the selected demo", async () =>
        {
            var service = new CountingService();
            await using var vm = Create(service);
            await vm.InitializeAsync(); await vm.AddDemoDeviceAsync();
            vm.EqName = "后台重连时保留"; vm.Bands[0].Gain = 3;
            var identity = vm.SelectedDevice!.Identity;
            service.Publish(new() { Kind = "catalog", BackendInstanceId = service.Backend, Devices = [] });
            Check(vm.Devices.Count == 1 && vm.IsDemoSelected && vm.HasVerifiedSnapshot);
            service.Publish(new() { Kind = "unavailable", Message = "后台测试中断" });
            Check(vm.IsDemoSelected && vm.HasVerifiedSnapshot && vm.CanEditEq && vm.EqName == "后台重连时保留");
            service.Backend = "backend-replacement";
            var replacement = Snapshot("real-a", service.Backend); replacement.LeftBattery = 37;
            service.Snapshots[replacement.Identity] = replacement;
            service.Publish(new() { Kind = "catalog", BackendInstanceId = service.Backend, Devices = service.Devices });
            service.Publish(new() { Kind = "snapshot", BackendInstanceId = service.Backend, Snapshot = replacement });
            Check(vm.IsDemoSelected && vm.SelectedDevice?.Identity == identity && vm.Devices.Count == 3
                && vm.EqDirty && vm.Bands[0].Gain == 3 && vm.HasVerifiedSnapshot);
            await vm.SelectDeviceAsync(vm.Devices.Single(device => device.Identity == "real-a"));
            Check(!vm.IsDemoSelected && vm.LeftBatteryText == "37%" && vm.HasVerifiedSnapshot,
                "Real snapshots received during demo remain available after switching back.");
            await vm.AddDemoDeviceAsync();
            var callsBeforeExit = service.Calls.Count;
            service.Publish(new() { Kind = "stopping" });
            await vm.SelectDeviceAsync(vm.Devices.Single(device => device.Identity == "real-a"));
            Check(vm.IsDemoSelected && service.Calls.Count == callsBeforeExit,
                "A terminal backend exit must retain demo presentation and refuse further device operations.");
        });

        await test("demo refresh and device management keep its identity and draft", async () =>
        {
            var service = new CountingService(); var confirmations = new Confirmations(false);
            await using var vm = Create(service, confirmations);
            await vm.InitializeAsync(); await vm.AddDemoDeviceAsync();
            vm.EqName = "管理后保留的演示 EQ"; vm.Bands[0].Gain = 2;
            var identity = vm.SelectedDevice!.Identity;
            await vm.RefreshDevicesCommand.ExecuteAsync(null);
            Check(vm.IsDemoSelected && vm.Devices.Count(MainViewModel.IsDemoDevice) == 1 && vm.EqDirty);
            foreach (var saved in new[] { false, true })
            {
                Check(await vm.PrepareDeviceManagementAsync() && vm.ManagingDevices);
                Check(await vm.CompleteDeviceManagementAsync(saved));
                Check(vm.IsDemoSelected && vm.SelectedDevice?.Identity == identity && vm.HasVerifiedSnapshot
                    && vm.EqDirty && vm.EqName == "管理后保留的演示 EQ" && vm.Bands[0].Gain == 2);
            }
            Check(confirmations.Calls == 0 && service.Calls.All(call => !call.Contains(identity, StringComparison.Ordinal)));
            service.FailEnumeration = true;
            Check(await vm.PrepareDeviceManagementAsync());
            await vm.CompleteDeviceManagementAsync(saved: true);
            Check(vm.Devices.Count(MainViewModel.IsDemoDevice) == 1 && vm.IsDemoSelected && vm.HasVerifiedSnapshot,
                "An enumeration failure must not discard the temporary demo.");
        });

        await test("demo selection preserves local drafts while real EQ discard still requires confirmation", async () =>
        {
            var service = new CountingService(); var confirmations = new Confirmations(false, true);
            await using var vm = Create(service, confirmations);
            await vm.InitializeAsync(); vm.EqName = "真实耳机草稿";
            await vm.AddDemoDeviceAsync();
            Check(!vm.IsDemoSelected && vm.SelectedDevice?.Identity == "real-a" && vm.EqDirty && confirmations.Calls == 1);
            await vm.AddDemoDeviceAsync();
            Check(vm.IsDemoSelected && confirmations.Calls == 2);
            vm.EqName = "演示草稿无需确认"; vm.Bands[0].Gain = 5;
            var demo = vm.SelectedDevice!;
            await vm.SelectDeviceAsync(vm.Devices.Single(device => device.Identity == "real-b"));
            Check(!vm.IsDemoSelected && vm.SelectedDevice?.Identity == "real-b" && vm.HasVerifiedSnapshot && confirmations.Calls == 2);
            await vm.ToggleGameCommand.ExecuteAsync(true);
            Check(service.Calls.Last() == "apply:real-b:GameMode" && vm.GameMode == true);
            await vm.SelectDeviceAsync(demo);
            Check(vm.IsDemoSelected && vm.EqDirty && vm.EqName == "演示草稿无需确认" && vm.Bands[0].Gain == 5
                && confirmations.Calls == 2);
        });

        await test("closing removes the demo even if the backend remains alive", async () =>
        {
            var service = new CountingService(); var confirmations = new Confirmations(false);
            var vm = Create(service, confirmations);
            await vm.InitializeAsync(); await vm.AddDemoDeviceAsync(); vm.EqName = "关闭时丢弃演示草稿";
            Check(await vm.ConfirmBackendExitAsync() && confirmations.Calls == 0);
            vm.CancelBackendExit();
            Check(await vm.RequestCloseAsync() && confirmations.Calls == 0 && service.Disposals == 1);
            Check(vm.Devices.All(device => !MainViewModel.IsDemoDevice(device)) && !vm.IsDemoSelected);
            await vm.DisposeAsync(); Check(service.Disposals == 1);
            var reopenedService = new CountingService();
            await using var reopened = Create(reopenedService);
            await reopened.InitializeAsync();
            Check(reopened.Devices.Count == 2 && reopened.Devices.All(device => !MainViewModel.IsDemoDevice(device))
                && !reopened.IsDemoSelected && reopenedService.Devices.Count == 2,
                "A new main-window session never restores a demo or inserts it into the backend catalog.");
        });

        await test("demo on a legacy service never connects, disconnects or applies to hardware", async () =>
        {
            var service = new LegacyService();
            await using var vm = new MainViewModel(service, new Confirmations(false));
            await vm.InitializeAsync();
            var calls = service.Calls;
            await vm.AddDemoDeviceAsync();
            await vm.ConnectCommand.ExecuteAsync(null); await vm.RefreshStateCommand.ExecuteAsync(null); await vm.PollAsync();
            await vm.ToggleGameCommand.ExecuteAsync(true);
            await vm.SelectDeviceAsync(vm.Devices.Single(device => device.Identity == "real-a"));
            Check(!vm.IsDemoSelected && service.Calls == calls && !vm.HasVerifiedSnapshot);
            await vm.ConnectCommand.ExecuteAsync(null);
            Check(service.Calls == calls + 1 && vm.HasVerifiedSnapshot);
        });
    }

    private static FeatureControlViewModel Feature(MainViewModel vm, string id) => vm.FeatureSections.SelectMany(section => section.Controls)
        .Concat(vm.EqualizerFeatures).Single(control => control.Id == id);
    private static MainViewModel Create(CountingService service, Confirmations? confirmations = null)
    {
        var vm = new MainViewModel(service, confirmations ?? new(true));
        service.EventReceived += vm.AdoptBackendEvent;
        return vm;
    }
    private static void Check(bool value, string reason = "Demo VM assertion failed.")
    { if (!value) throw new InvalidOperationException(reason); }
    private static UiSnapshot Snapshot(string identity, string backend = "backend-test") => new()
    {
        Identity = identity, DeviceName = identity, BackendInstanceId = backend, DeviceGeneration = 1, Revision = 1,
        Connected = true, ConnectionState = ConnectionState.Connected, ControlReady = true,
        SupportedModel = true, MonitoringState = MonitoringState.Monitoring, ObservationFresh = true,
        ProductId = "06EC10", LeftBattery = 90, RightBattery = 80, CaseBattery = 70,
        Anc = 0x0104, Equalizer = 0, Spatial = 0, HiRes = true, DualConnection = true, GameMode = false,
        CanSetAnc = true, CanSetEqualizer = true, CanSetCustomEqualizer = true,
        CanSetDualConnection = true, CanSetGameMode = true, CanSetSpatial = true, CanSetHiRes = true,
        AncOptions = [new(0x0104, "降噪"), new(0x0003, "关闭")],
        EqualizerOptions = [new(0, "原声"), new(7, "高解析")], SpatialOptions = [new(0, "关闭"), new(2, "追踪")],
        EqualizerFrequencies = [31, 62, 125, 250, 500, 1000, 2000, 4000, 8000, 16000],
        EqualizerEntries = [new(1, "真实 EQ", -6, 6, true, new[] { 31, 62, 125, 250, 500, 1000, 2000, 4000, 8000, 16000 }.Select(hz => new UiBand(hz, 0)).ToArray())]
    };
    private sealed class Confirmations(params bool[] answers) : IConfirmationService
    {
        public int Calls { get; private set; }
        public Task<bool> ConfirmAsync(string title, string message, CancellationToken token)
            => Task.FromResult(answers[Math.Min(Calls++, answers.Length - 1)]);
    }
    private sealed class CountingService : IHeadsetEventService, ICalibrationService
    {
        public event Action<BackendEvent>? EventReceived;
        public string Backend { get; set; } = "backend-test";
        public Dictionary<string, UiSnapshot> Snapshots { get; } = new()
            { ["real-a"] = Snapshot("real-a"), ["real-b"] = Snapshot("real-b") };
        public IReadOnlyList<UiDevice> Devices => Snapshots.Values.Select(snapshot => new UiDevice(snapshot.Identity, snapshot.DeviceName, true)).ToArray();
        public List<string> Calls { get; } = [];
        public int Disposals { get; private set; }
        public bool FailEnumeration { get; set; }
        public void Publish(BackendEvent notification) => EventReceived?.Invoke(notification);
        public Task<BackendSubscription> SubscribeAsync(string role, CancellationToken token)
        {
            Calls.Add("subscribe"); return Task.FromResult(new BackendSubscription
                { BackendInstanceId = Backend, Devices = Devices, Snapshots = Snapshots.Values.ToArray() });
        }
        public Task<IReadOnlyList<UiDevice>> EnumerateAsync(CancellationToken token)
        { Calls.Add("enumerate"); return FailEnumeration ? Task.FromException<IReadOnlyList<UiDevice>>(new IOException("catalog unavailable")) : Task.FromResult(Devices); }
        public Task<UiSnapshot> SelectAsync(string identity, CancellationToken token)
        { Calls.Add("select:" + identity); return Task.FromResult(Snapshots[identity]); }
        public Task<UiSnapshot> PauseAsync(string identity, CancellationToken token)
        { Calls.Add("pause:" + identity); return Task.FromResult(Snapshots[identity]); }
        public Task<UiSnapshot> ResumeAsync(string identity, CancellationToken token)
        { Calls.Add("resume:" + identity); return Task.FromResult(Snapshots[identity]); }
        public Task<UiSnapshot> RefreshAsync(string identity, RefreshScope scope, CancellationToken token)
        { Calls.Add("refresh:" + identity); return Task.FromResult(Snapshots[identity]); }
        public Task<UiApplyResult> ApplyAsync(string identity, long generation, UiAction action, CancellationToken token)
        {
            Calls.Add("apply:" + identity + ":" + action.Kind);
            var after = JsonSerializer.Deserialize<UiSnapshot>(JsonSerializer.Serialize(Snapshots[identity]))!;
            after.Revision++; if (action.Kind == "GameMode") after.GameMode = action.Value == 1;
            Snapshots[identity] = after;
            return Task.FromResult(new UiApplyResult { BackendReceiptReceived = true, Accepted = true, ReadbackMatches = true,
                TargetSubmitted = true, SubmissionAttempted = true, ConnectionState = ConnectionState.Connected, AckStatus = 0, After = after });
        }
        public Task AcknowledgeExitAsync(string requestId, bool approved, CancellationToken token)
        { Calls.Add("acknowledgeExit"); return Task.CompletedTask; }
        public Task ActivateMainUiAsync(CancellationToken token) { Calls.Add("activate"); return Task.CompletedTask; }
        public Task<UiSnapshot> ConnectAsync(string identity, CancellationToken token)
        { Calls.Add("legacyConnect:" + identity); throw new InvalidOperationException("Unexpected legacy connection."); }
        public Task DisconnectAsync(CancellationToken token) { Calls.Add("legacyDisconnect"); throw new InvalidOperationException("Unexpected disconnection."); }
        public Task<UiSnapshot> RefreshAsync(CancellationToken token)
        { Calls.Add("legacyRefresh"); throw new InvalidOperationException("Unexpected legacy refresh."); }
        public Task<UiApplyResult> ApplyAsync(UiAction action, CancellationToken token)
        { Calls.Add("legacyApply"); throw new InvalidOperationException("Unexpected legacy setter."); }
        public Task<IReadOnlyList<UiHearingProfile>> GetHearingProfilesAsync(string identity, long generation, CancellationToken token)
        { Calls.Add("profiles:" + identity); throw new InvalidOperationException("Demo profiles must be local."); }
        public Task<UiApplyResult> SelectHearingProfileAsync(string identity, long generation, int? uid, CancellationToken token)
        { Calls.Add("hearing:" + identity); throw new InvalidOperationException("Demo hearing must be local."); }
        public Task<UiCalibrationState> CreateCalibrationAsync(string identity, long generation, string kind, CancellationToken token)
        { Calls.Add("calibration:" + identity); throw new InvalidOperationException("Demo must never launch a calibration."); }
        public Task<UiCalibrationState> GetCalibrationAsync(string handle, CancellationToken token) => throw new InvalidOperationException();
        public Task<UiCalibrationState> AdvanceCalibrationAsync(string handle, long revision, UiCalibrationInput input, CancellationToken token) => throw new InvalidOperationException();
        public Task<UiCalibrationState> CancelCalibrationAsync(string handle, CancellationToken token) => throw new InvalidOperationException();
        public ValueTask DisposeAsync() { Disposals++; return ValueTask.CompletedTask; }
    }
    private sealed class LegacyService : IHeadsetService
    {
        public int Calls { get; private set; }
        public Task<IReadOnlyList<UiDevice>> EnumerateAsync(CancellationToken token)
        { Calls++; return Task.FromResult<IReadOnlyList<UiDevice>>([new("real-a", "真实耳机", true)]); }
        public Task<UiSnapshot> ConnectAsync(string identity, CancellationToken token)
        { Calls++; var snapshot = Snapshot(identity); snapshot.BackendInstanceId = ""; return Task.FromResult(snapshot); }
        public Task DisconnectAsync(CancellationToken token) { Calls++; return Task.CompletedTask; }
        public Task<UiSnapshot> RefreshAsync(CancellationToken token) { Calls++; return Task.FromResult(Snapshot("real-a")); }
        public Task<UiApplyResult> ApplyAsync(UiAction action, CancellationToken token)
        { Calls++; throw new InvalidOperationException("Unexpected hardware setter."); }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
