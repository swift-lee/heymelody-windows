using HeyMelodyWindows.App;

// Feature rows and EQ choices stay editable during an in-flight write; later choices queue like ANC.
internal static class WriteQueueTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(5);

    public static async Task RunAsync(Func<string, Func<Task>, Task> test)
    {
        await test("feature rows stay editable during an in-flight write and a second row is sent after success", async () =>
        {
            var service = new Service(); await using var vm = await Connected(service);
            var gate = Gate(service);
            var a = Feature(vm, "a"); var b = Feature(vm, "b"); var c = Feature(vm, "c");
            a.BooleanValue = true; var first = a.SubmitAsync(); await service.Entered.Task.WaitAsync(Deadline);
            Check(a.CanEdit && b.CanEdit && c.CanEdit && vm.Busy, "No row may be disabled by another row's pending write.");
            b.BooleanValue = true; var second = b.SubmitAsync();
            Check(service.Actions.Count == 1 && !second.IsCompleted, "The second edit waits for the first confirmation.");
            var next = service.Next(); service.Set(next, "c", 1); service.Push(next);
            Check(Feature(vm, "b").BooleanValue && Feature(vm, "c").SelectedOption?.Key == 1, "A rebuilt row keeps its queued target.");
            gate.SetResult(); await Task.WhenAll(first, second).WaitAsync(Deadline);
            Check(service.Actions.Select(x => (x.FeatureId!, x.Value)).SequenceEqual(new[] { ("a", 1), ("b", 1) }) && service.MaxConcurrent == 1,
                "Targets go out in click order, one at a time.");
            Check(Feature(vm, "a").BooleanValue && Feature(vm, "b").BooleanValue && vm.CanWriteFeatures(), "Both confirmed values are shown.");
        });
        await test("repeated edits of one row keep only the last target", async () =>
        {
            var service = new Service(); await using var vm = await Connected(service);
            var gate = Gate(service);
            var a = Feature(vm, "a"); var c = Feature(vm, "c");
            a.BooleanValue = true; var first = a.SubmitAsync(); await service.Entered.Task.WaitAsync(Deadline);
            Pick(c, 1); var older = c.SubmitAsync(); Pick(c, 2); var newer = c.SubmitAsync();
            await older.WaitAsync(Deadline);
            Check(!newer.IsCompleted && c.SelectedOption?.Key == 2, "A superseded submit completes without clobbering the newer edit.");
            gate.SetResult(); await Task.WhenAll(first, newer).WaitAsync(Deadline);
            Check(service.Actions.Select(x => (x.FeatureId!, x.Value)).SequenceEqual(new[] { ("a", 1), ("c", 2) }), "Only the final choice is sent.");
            Check(Feature(vm, "c").SelectedOption?.Key == 2, "The sent value is observed.");
        });
        await test("a queued target already satisfied by an observation is skipped without a setter", async () =>
        {
            var service = new Service(); await using var vm = await Connected(service);
            var gate = Gate(service);
            var a = Feature(vm, "a"); var b = Feature(vm, "b");
            a.BooleanValue = true; var first = a.SubmitAsync(); await service.Entered.Task.WaitAsync(Deadline);
            b.BooleanValue = true; var second = b.SubmitAsync();
            var next = service.Next(); service.Set(next, "b", 1); service.Push(next);
            gate.SetResult(); await Task.WhenAll(first, second).WaitAsync(Deadline);
            Check(service.Actions.Count == 1 && Feature(vm, "b").BooleanValue, "The observed value satisfies the queued click.");
        });
        await test("an unconfirmed in-flight write discards queued feature targets and restores the observed display", async () =>
        {
            var dialogs = new Dialogs { AlertRelease = new(TaskCreationOptions.RunContinuationsAsynchronously) };
            var service = new Service(); await using var vm = await Connected(service, dialogs);
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            service.Write = async (action, _, token) => { service.Entered.TrySetResult(); await gate.Task.WaitAsync(token); return service.Uncertain(); };
            var a = Feature(vm, "a"); var b = Feature(vm, "b");
            a.BooleanValue = true; var first = a.SubmitAsync(); await service.Entered.Task.WaitAsync(Deadline);
            b.BooleanValue = true; var second = b.SubmitAsync();
            gate.SetResult(); await dialogs.AlertEntered.Task.WaitAsync(Deadline);
            Check(service.Actions.Count == 1 && !second.IsCompleted, "The queue waits for the failed intent's alert.");
            dialogs.AlertRelease.SetResult(); await Task.WhenAll(first, second).WaitAsync(Deadline);
            Check(service.Actions.Count == 1 && dialogs.Alerts == 1 && dialogs.Retries == 0, "Nothing is retried or replayed.");
            Check(!Feature(vm, "a").BooleanValue && !Feature(vm, "b").BooleanValue && !b.BooleanValue && !a.BooleanValue,
                "Every row shows the observed value again.");
            service.Write = null; var again = Feature(vm, "b"); again.BooleanValue = true; await again.SubmitAsync().WaitAsync(Deadline);
            Check(service.Actions.Count == 2 && Feature(vm, "b").BooleanValue, "A new explicit edit works after the discard.");
        });
        await test("a control generation or verified-control change drops queued feature targets without a setter", async () =>
        {
            foreach (bool generation in new[] { true, false })
            {
                var service = new Service(); await using var vm = await Connected(service);
                var gate = Gate(service);
                var a = Feature(vm, "a"); var b = Feature(vm, "b");
                a.BooleanValue = true; var first = a.SubmitAsync(); await service.Entered.Task.WaitAsync(Deadline);
                b.BooleanValue = true; var second = b.SubmitAsync();
                var next = service.Next();
                if (generation) { next.DeviceGeneration++; next.Revision = 1; } else { next.ControlReady = false; next.MonitoringState = MonitoringState.Unavailable; }
                service.Push(next);
                gate.SetResult(); await Task.WhenAll(first, second).WaitAsync(Deadline);
                Check(service.Actions.Count == 1, "A queued target must never reach a changed session.");
                Check(!Feature(vm, "b").BooleanValue, "The old queued choice is not displayed for the new session.");
            }
        });
        await test("an EQ preset clicked during an in-flight preset write is queued and sent, keeping only the last click", async () =>
        {
            var service = new Service(); await using var vm = await Connected(service);
            var gate = Gate(service);
            var first = vm.SelectPresetAsync(vm.PresetOptions.Single(x => x.Key == 1)); await service.Entered.Task.WaitAsync(Deadline);
            Check(vm.CanChoosePreset && vm.CanEditEq, "Radios stay enabled.");
            var skipped = vm.SelectPresetAsync(vm.PresetOptions.Single(x => x.Key == 2));
            var last = vm.SelectPresetAsync(vm.PresetOptions.Single(x => x.Key == 3));
            Check(vm.SelectedPreset?.Key == 3 && service.Actions.Count == 1, "The display follows the latest click.");
            gate.SetResult(); await Task.WhenAll(first, skipped, last).WaitAsync(Deadline);
            Check(service.Actions.Select(x => (x.Kind, x.Value)).SequenceEqual(new[] { ("Equalizer", 1), ("Equalizer", 3) }) && service.MaxConcurrent == 1,
                "The click made during the write is not lost, and the intermediate one is dropped.");
            Check(vm.SelectedPreset?.Key == 3 && vm.CanSetEqualizer, "The final preset is observed and settled.");
        });
        await test("a custom EQ entry chosen during a preset write replaces the queued preset and is sent after success", async () =>
        {
            var service = new Service(); await using var vm = await Connected(service);
            var gate = Gate(service);
            var first = vm.SelectPresetAsync(vm.PresetOptions.Single(x => x.Key == 1)); await service.Entered.Task.WaitAsync(Deadline);
            var queuedPreset = vm.SelectPresetAsync(vm.PresetOptions.Single(x => x.Key == 2));
            var entry = vm.EqEntries.Single(x => x.Id == 2);
            var choose = vm.ActivateEqEntryAsync(entry);
            Check(vm.SelectedPreset is null && vm.PendingEqEntryId == 2 && vm.SelectedEqEntry?.Id == 2, "The entry click owns the displayed choice.");
            gate.SetResult(); await Task.WhenAll(first, queuedPreset, choose).WaitAsync(Deadline);
            Check(service.Actions.Select(x => (x.Kind, x.Value)).SequenceEqual(new[] { ("Equalizer", 1), ("CustomSelect", 2) }), "Only the latest slot is sent.");
            Check(vm.PendingEqEntryId is null && vm.SelectedPreset is null && vm.EqEntries.Single(x => x.Id == 2).Selected, "The entry is observed.");
        });
        await test("an unconfirmed EQ write discards the queued click and redisplays the observed preset", async () =>
        {
            var dialogs = new Dialogs { AlertRelease = new(TaskCreationOptions.RunContinuationsAsynchronously) };
            var service = new Service(); await using var vm = await Connected(service, dialogs);
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            service.Write = async (_, _, token) => { service.Entered.TrySetResult(); await gate.Task.WaitAsync(token); return service.Uncertain(); };
            var first = vm.SelectPresetAsync(vm.PresetOptions.Single(x => x.Key == 1)); await service.Entered.Task.WaitAsync(Deadline);
            var second = vm.ActivateEqEntryAsync(vm.EqEntries.Single(x => x.Id == 2));
            gate.SetResult(); await dialogs.AlertEntered.Task.WaitAsync(Deadline);
            dialogs.AlertRelease.SetResult(); await Task.WhenAll(first, second).WaitAsync(Deadline);
            Check(service.Actions.Count == 1 && dialogs.Retries == 0, "The queued entry is never sent after a failure.");
            Check(vm.SelectedPreset?.Key == 0 && vm.PendingEqEntryId is null && vm.SelectedEqEntry?.Id == 1, "The observed preset and entry return.");
        });
        await test("an EQ click while a custom EQ save is in flight waits for it and is dropped if the save fails", async () =>
        {
            var dialogs = new Dialogs { AlertRelease = new(TaskCreationOptions.RunContinuationsAsynchronously) };
            var service = new Service(); await using var vm = await Connected(service, dialogs);
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            service.Write = async (_, _, token) => { service.Entered.TrySetResult(); await gate.Task.WaitAsync(token); return service.Uncertain(); };
            vm.Bands[0].Gain = 3; var save = vm.SaveEqCommand.ExecuteAsync(null); await service.Entered.Task.WaitAsync(Deadline);
            var click = vm.SelectPresetAsync(vm.PresetOptions.Single(x => x.Key == 2));
            Check(service.Actions.Count == 1 && !click.IsCompleted, "The click chains after the save instead of being lost.");
            gate.SetResult(); await dialogs.AlertEntered.Task.WaitAsync(Deadline);
            dialogs.AlertRelease.SetResult(); await Task.WhenAll(save, click).WaitAsync(Deadline);
            Check(service.Actions.Count == 1 && vm.SelectedPreset?.Key == 0, $"The unconfirmed save discards the click. Actions.Count={service.Actions.Count}; SelectedPreset.Key={vm.SelectedPreset?.Key.ToString() ?? "<null>"}.");
        });
    }

    private static TaskCompletionSource Gate(Service service)
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        service.Write = async (action, count, token) => { service.Entered.TrySetResult(); if (count == 1) await gate.Task.WaitAsync(token); return service.Success(action); };
        return gate;
    }
    private static void Pick(FeatureControlViewModel control, int key) => control.SelectedOption = control.Options.Single(option => option.Key == key);
    private static FeatureControlViewModel Feature(MainViewModel vm, string id) => vm.FeatureSections.SelectMany(section => section.Controls)
        .Concat(vm.EqualizerFeatures).Single(control => control.Id == id);
    private static bool CanWriteFeatures(this MainViewModel vm) => vm.FeatureSections.SelectMany(section => section.Controls).All(control => control.CanEdit);
    private static void Check(bool value, string reason) { if (!value) throw new InvalidOperationException(reason); }
    private static async Task<MainViewModel> Connected(Service service, Dialogs? dialogs = null)
    {
        var vm = new MainViewModel(service, dialogs ?? new Dialogs()); service.EventReceived += vm.AdoptBackendEvent;
        await vm.InitializeAsync(); Check(vm.HasVerifiedSnapshot, "The fixture must establish a current backend session."); return vm;
    }

    private sealed class Dialogs : IConfirmationService
    {
        public int Retries, Alerts;
        public TaskCompletionSource AlertEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource? AlertRelease;
        public Task<bool> ConfirmAsync(string title, string message, CancellationToken token) => Task.FromResult(true);
        public Task<bool> ConfirmRetryAsync(string title, string message, CancellationToken token) { Retries++; return Task.FromResult(true); }
        public Task AlertAsync(string title, string message, CancellationToken token)
        { Alerts++; AlertEntered.TrySetResult(); return AlertRelease?.Task.WaitAsync(token) ?? Task.CompletedTask; }
    }

    private sealed class Service : IHeadsetEventService
    {
        public event Action<BackendEvent>? EventReceived;
        public UiSnapshot State = new()
        {
            Identity = "peer", DeviceName = "OPPO Enco X4", BackendInstanceId = "queue", DeviceGeneration = 1, Revision = 1,
            ProductId = "06EC10", Connected = true, ConnectionState = ConnectionState.Connected, ControlReady = true,
            SupportedModel = true, MonitoringState = MonitoringState.Monitoring, ObservationFresh = true,
            Equalizer = 0, CanSetEqualizer = true, CanSetCustomEqualizer = true,
            EqualizerOptions = [new(0, "原声"), new(1, "低音"), new(2, "人声"), new(3, "高音")],
            EqualizerFrequencies = [31],
            EqualizerEntries = [new(1, "均衡器一", -6, 6, false, [new(31, 0)]), new(2, "均衡器二", -6, 6, false, [new(31, 0)])],
            FeatureSections = [new("audio", "声音", true,
            [new("a", "开关甲", "Switch", 0, CanWrite: true),
             new("b", "开关乙", "Switch", 0, CanWrite: true),
             new("c", "选项丙", "Combo", 0, Options: [new(0, "零"), new(1, "一"), new(2, "二")], CanWrite: true)])]
        };
        public List<UiAction> Actions { get; } = [];
        public TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Func<UiAction, int, CancellationToken, Task<UiApplyResult>>? Write;
        public int MaxConcurrent; private int _concurrent;
        public UiSnapshot Next() { var next = State.Clone(); next.Revision++; return next; }
        public void Push(UiSnapshot next) { State = next; EventReceived?.Invoke(new() { Kind = "snapshot", BackendInstanceId = next.BackendInstanceId, Snapshot = next }); }
        public void Set(UiSnapshot snapshot, string id, int value) => snapshot.FeatureSections = snapshot.FeatureSections.Select(section => section with
        { Controls = section.Controls.Select(control => control.Id == id ? control with { Value = value } : control).ToArray() }).ToArray();
        public UiApplyResult Success(UiAction action)
        {
            State = Next();
            switch (action.Kind)
            {
                case "Feature": Set(State, action.FeatureId!, action.Value); break;
                case "Equalizer":
                    State.Equalizer = action.Value; State.EqualizerEntries = State.EqualizerEntries.Select(x => x with { Selected = false }).ToArray(); break;
                case "CustomSelect":
                    State.Equalizer = null; State.EqualizerEntries = State.EqualizerEntries.Select(x => x with { Selected = x.Id == action.Value }).ToArray(); break;
            }
            return new() { BackendReceiptReceived = true, ConnectionState = ConnectionState.Connected, SubmissionAttempted = true,
                TargetSubmitted = true, Accepted = true, AckStatus = 0, ReadbackMatches = true, After = State };
        }
        public UiApplyResult Uncertain() => new() { BackendReceiptReceived = true, ConnectionState = ConnectionState.Unknown,
            FailureKind = ApplyFailure.WorkerUnavailable, SubmissionAttempted = true, TargetSubmitted = true, AcknowledgementUnknown = true };
        public Task<BackendSubscription> SubscribeAsync(string role, CancellationToken token) => Task.FromResult(new BackendSubscription
        { BackendInstanceId = State.BackendInstanceId, Devices = [new("peer", "OPPO Enco X4", true)], Snapshots = [State] });
        public Task<IReadOnlyList<UiDevice>> EnumerateAsync(CancellationToken token) => Task.FromResult<IReadOnlyList<UiDevice>>([new("peer", "OPPO Enco X4", true)]);
        public Task<UiSnapshot> SelectAsync(string identity, CancellationToken token) => Task.FromResult(State);
        public Task<UiSnapshot> PauseAsync(string identity, CancellationToken token) => throw new NotSupportedException();
        public Task<UiSnapshot> ResumeAsync(string identity, CancellationToken token) => throw new NotSupportedException();
        public Task<UiSnapshot> RefreshAsync(string identity, RefreshScope scope, CancellationToken token)
        { State = Next(); return Task.FromResult(State); }
        public async Task<UiApplyResult> ApplyAsync(string identity, long generation, UiAction action, CancellationToken token)
        {
            Actions.Add(action); _concurrent++; MaxConcurrent = Math.Max(MaxConcurrent, _concurrent);
            try { return Write is null ? Success(action) : await Write(action, Actions.Count, token); }
            finally { _concurrent--; }
        }
        public Task AcknowledgeExitAsync(string id, bool approved, CancellationToken token) => Task.CompletedTask;
        public Task ActivateMainUiAsync(CancellationToken token) => Task.CompletedTask;
        public Task<UiSnapshot> ConnectAsync(string identity, CancellationToken token) => throw new NotSupportedException();
        public Task DisconnectAsync(CancellationToken token) => throw new NotSupportedException();
        public Task<UiSnapshot> RefreshAsync(CancellationToken token) => throw new NotSupportedException();
        public Task<UiApplyResult> ApplyAsync(UiAction action, CancellationToken token) => throw new NotSupportedException();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
