using HeyMelodyWindows.App;
using HeyMelodyWindows.Calibration.UI;

if (args is ["--native-exit-smoke"]) return WindowExitLifecycleTests.Run();
if (args is ["--native-rendering-policy-smoke", var renderMode, "--output-dir", var renderOutput]
    && renderMode is "default" or "basic")
    return NativeCalibrationSmoke.Run(basicRenderMode: renderMode == "basic", outputDirectory: Path.GetFullPath(renderOutput));
if (args.FirstOrDefault() == "--native-rendering-policy-smoke")
{ Console.Error.WriteLine("Usage: --native-rendering-policy-smoke <default|basic> --output-dir path"); return 2; }
if (args.Contains("--native", StringComparer.Ordinal)) return NativeCalibrationSmoke.Run();

var passed = 0;
try
{
    var service = new TestService();
    await using (var model = new CalibrationViewModel(service, "operation", "hearing"))
    {
        await model.InitializeAsync();
        model.Adopt(service.State with { Revision = 0, Stage = "Failed" });
        Check(model.State.Stage == "Ready", "Late events cannot roll the operation state back.");
        model.Adopt(service.State with { Handle = "another-operation", Revision = 99, Stage = "Completed" });
        Check(model.State.Stage == "Ready", "A different operation cannot update this component.");
        model.Name = ""; Check(model.ValidateName() is not null, "Empty profile names are rejected.");
        model.Name = "A"; Check(model.ValidateName() is null, "A one-byte wire name is valid.");
        model.Name = new string('测', 13); Check(model.ValidateName() is null, "A 39-byte UTF-8 name fits the wire limit.");
        model.Name = new string('测', 14); Check(model.ValidateName() is not null, "The name boundary counts UTF-8 bytes rather than characters.");
        foreach (var invalid in new[] { "A\0B", "A\tB", "A\nB", "\tA", "A\n", "\uD800", "\uDC00" })
        { model.Name = invalid; Check(model.ValidateName() is not null, "Control characters and unpaired surrogates are rejected before saving."); }
        model.Name = "试听😀"; Check(model.ValidateName() is null, "A valid Unicode surrogate pair is accepted.");
        model.Name = "Existing"; Check(model.ValidateName() is not null, "Duplicate profile names are rejected.");
        model.Name = "新配置"; Check(model.ValidateName() is null && model.ReplacesCurrent, "The current signed UID zero is a profile that requires replacement consent.");
    }
    Console.WriteLine("PASS isolated calibration state and naming"); passed++;

    service = new TestService();
    service.State = service.State with { Stage = "Results", FiltersReady = true };
    await using (var model = new CalibrationViewModel(service, "operation", "hearing"))
    {
        await model.InitializeAsync();
        foreach (var invalid in new[] { "A\0B", "A\tB", "\uD800" })
        {
            model.Name = invalid; await model.SendAsync("save");
            Check(service.Inputs.Count == 0 && !model.Busy && model.Error.Length > 0,
                "An invalid name never reaches the save IPC request and leaves the editor usable.");
        }
    }
    Console.WriteLine("PASS strict name rejection before save IPC"); passed++;

    service = new TestService { BlockStart = true };
    await using (var model = new CalibrationViewModel(service, "operation", "fit"))
    {
        await model.InitializeAsync();
        var start = model.SendAsync("start");
        await service.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Check(model.Busy, "The measurement request is active.");
        await model.CancelAsync().WaitAsync(TimeSpan.FromSeconds(2));
        await start.WaitAsync(TimeSpan.FromSeconds(2));
        Check(service.Cancels == 1 && model.State.Stage == "Cancelled", "Closing cancels without waiting for a measurement lock.");
    }
    Console.WriteLine("PASS independent cancellation during measurement"); passed++;

    service = new TestService { StopFailuresRemaining = 1 };
    service.State = service.State with { Stage = "AwaitingAnswer", HearingVersion = "V2", ToneAcknowledged = true };
    await using (var model = new CalibrationViewModel(service, "operation", "hearing"))
    {
        await model.InitializeAsync();
        var confirmed = await model.CancelAsync();
        Check(!confirmed && service.Cancels == 1 && model.State.Stage == "Failed" && model.CancellationFailed
            && !model.StopConfirmed && !model.Busy && model.Error.Contains("停止"),
            "A failed STOP is not confirmed or hidden, and restores a usable retry.");
        await model.AdjustLevelAsync(9, true); await model.SendAsync("next");
        Check(service.Inputs.Count == 0, "Failed stopping never resumes tones or accepts a new answer.");
        Check(await model.CancelAsync() && service.Cancels == 2 && model.StopConfirmed && model.Error.Length == 0,
            "A subsequent STOP success is confirmed after the first failure.");
    }
    service = new TestService();
    await using (var model = new CalibrationViewModel(service, "operation", "fit"))
    {
        await model.InitializeAsync(); model.BackendUnavailable("连接已断开。");
        Check(!await model.CancelAsync() && model.CanCloseLocally && !model.StopConfirmed && service.Cancels == 0
            && model.State.Stage != "Cancelled" && model.Error.Contains("无法确认"),
            "An unavailable backend permits local closing without inventing a successful earbud STOP.");
    }
    Console.WriteLine("PASS failed STOP retry and truthful local-close state"); passed++;

    service = new TestService { BlockFirstLevel = true };
    service.State = service.State with { Stage = "AwaitingAnswer", HearingVersion = "V2", ToneAcknowledged = true };
    await using (var model = new CalibrationViewModel(service, "operation", "hearing"))
    {
        await model.InitializeAsync();
        var initial = model.AdjustLevelAsync(2, false);
        await service.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        service.State = service.State with { Revision = service.State.Revision + 1, ToneAcknowledged = false };
        model.Adopt(service.State);
        var released = model.AdjustLevelAsync(10, true);
        Check(model.SelectedLevelIndex == 10 && !model.CanContinue, "The pending user value remains visible and cannot be confirmed early.");
        service.ReleaseLevel.TrySetResult();
        await released.WaitAsync(TimeSpan.FromSeconds(2)); await initial;
        Check(service.Inputs is [{ Action: "level", LevelIndex: 2, Released: true }, { Action: "level", LevelIndex: 10, Released: true }],
            "The released slider value is queued and never dropped behind an in-flight level request.");
        await model.SendAsync("next");
        Check(service.Inputs.Last().Action == "next" && model.State.Revision == 5 && model.SelectedLevelIndex == 5,
            "Continue uses the latest acknowledged revision and the following step resets to index 5.");
    }
    Console.WriteLine("PASS coalesced level releases and revision ordering"); passed++;

    service = new TestService { StaleNextLevel = true };
    service.State = service.State with { Stage = "AwaitingAnswer", HearingVersion = "V2", ToneAcknowledged = false };
    await using (var model = new CalibrationViewModel(service, "operation", "hearing"))
    {
        await model.InitializeAsync();
        await model.SendAsync("next");
        Check(service.Inputs.Count == 0 && !model.CanContinue, "Next cannot commit an initial test tone without its ACK.");
        await model.AdjustLevelAsync(7, true);
        Check(service.LevelAttempts == 2 && service.Inputs is [{ Action: "level", LevelIndex: 7 }] && model.CanContinue,
            "A newer same-step IPC revision retries the requested level once and waits for its ACK.");
        service.FailNextLevel = true;
        await model.AdjustLevelAsync(9, true);
        await model.SendAsync("next");
        Check(!model.CanContinue && model.Error.Length > 0 && service.Inputs.All(x => x.Action != "next"),
            "A failed level keeps Next blocked rather than recording the displayed value.");
        await model.AdjustLevelAsync(9, true);
        Check(model.CanContinue && model.Error.Length == 0 && !model.Busy && !model.LevelPending,
            "A successful retry restores level controls and clears the previous error.");
    }
    Console.WriteLine("PASS stale revision recovery and ACK-gated navigation"); passed++;

    service = new TestService();
    service.State = service.State with { Stage = "AwaitingAnswer", HearingVersion = "V2", ToneAcknowledged = true };
    await using (var model = new CalibrationViewModel(service, "operation", "hearing"))
    {
        await model.InitializeAsync();
        var first = model.AdjustLevelAsync(6, false);
        await Task.Delay(40);
        var second = model.AdjustLevelAsync(8, false);
        await Task.Delay(40);
        var last = model.AdjustLevelAsync(10, false);
        await Task.WhenAll(first, second, last).WaitAsync(TimeSpan.FromSeconds(2));
        Check(service.Inputs is [{ Action: "level", LevelIndex: 10 }], "Trailing debounce sends the final drag value without requiring pointer release.");
        var pending = model.AdjustLevelAsync(7, false);
        await model.CancelAsync(); await pending;
        Check(service.Inputs.Count == 1 && model.State.Stage == "Cancelled", "Closing removes delayed slider input before it can send.");
    }
    Console.WriteLine("PASS trailing slider debounce and delayed-input cancellation"); passed++;

    foreach (var (kind, scan, path) in new (string, bool, (string Stage, CalibrationPhase Phase, bool Scanned)[])[]
    {
        ("hearing", true, [("Starting", CalibrationPhase.EarScan, false), ("Scanning", CalibrationPhase.EarScan, false), ("Failed", CalibrationPhase.EarScan, false),
            ("Starting", CalibrationPhase.EarScan, false), ("CheckingWear", CalibrationPhase.EarScan, false), ("Scanning", CalibrationPhase.EarScan, false),
            ("ScanCompleted", CalibrationPhase.EarScan, true), ("Starting", CalibrationPhase.Hearing, true), ("CheckingWear", CalibrationPhase.Hearing, true),
            ("AwaitingAnswer", CalibrationPhase.Hearing, true), ("Paused", CalibrationPhase.Hearing, true), ("AwaitingAnswer", CalibrationPhase.Hearing, true),
            ("LoadingCurves", CalibrationPhase.Results, true), ("Failed", CalibrationPhase.Results, true), ("Results", CalibrationPhase.Results, true),
            ("Previewing", CalibrationPhase.Results, true), ("Saving", CalibrationPhase.Results, true), ("Completed", CalibrationPhase.Done, true)]),
        ("hearing", false, [("Starting", CalibrationPhase.Hearing, false), ("AwaitingAnswer", CalibrationPhase.Hearing, false), ("Results", CalibrationPhase.Results, false)]),
        ("fit", false, [("Starting", CalibrationPhase.Hearing, false), ("Measuring", CalibrationPhase.Hearing, false), ("Completed", CalibrationPhase.Done, false),
            ("Starting", CalibrationPhase.Hearing, false), ("Failed", CalibrationPhase.Hearing, false), ("Starting", CalibrationPhase.Hearing, false)])
    })
    {
        service = new TestService();
        service.State = service.State with { HearingVersion = scan ? "V2" : "V1", SupportsEarScan = scan };
        await using var model = new CalibrationViewModel(service, "operation", kind);
        await model.InitializeAsync();
        Check(model.Phase == CalibrationPhase.Intro && !model.EarScanCompleted, "Every wizard opens on its introduction page.");
        foreach (var (stage, phase, scanned) in path)
        {
            service.State = service.State with { Revision = service.State.Revision + 1, Stage = stage }; model.Adopt(service.State);
            Check(model.Phase == phase && model.EarScanCompleted == scanned, $"{kind}/{scan}: {stage} presents {phase} with scan completion {scanned}.");
        }
    }
    Console.WriteLine("PASS wizard phases follow scan, test, retry and result transitions"); passed++;
    passed += await HearingPreviewTests.RunAsync();
    passed += HearingResultDescriptionTests.Run();
    passed += CalibrationVisualTests.Run();
    Console.WriteLine($"{{\"success\":true,\"passed\":{passed},\"nativeBluetoothCalls\":0}}");
    return 0;
}
catch (Exception error) { Console.Error.WriteLine(error); return 1; }

static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }

class TestService : ICalibrationService
{
    public UiCalibrationState State { get; set; } = new() { Handle = "operation", Identity = "earbuds", Generation = 42, Revision = 1, BackendInstanceId = "fixture", Stage = "Ready" };
    public bool BlockStart { get; init; }
    public bool BlockFirstLevel { get; init; }
    public bool StaleNextLevel { get; set; }
    public bool FailNextLevel { get; set; }
    public bool FailNextStart { get; set; }
    public int StopFailuresRemaining { get; set; }
    public int LevelAttempts { get; private set; }
    public int Cancels { get; private set; }
    public List<UiCalibrationInput> Inputs { get; } = [];
    public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource ReleaseLevel { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Task<IReadOnlyList<UiHearingProfile>> GetHearingProfilesAsync(string identity, long generation, CancellationToken token)
        => Task.FromResult<IReadOnlyList<UiHearingProfile>>([new(0, "Existing", true, true)]);
    public Task<UiApplyResult> SelectHearingProfileAsync(string identity, long generation, int? uid, CancellationToken token) => throw new NotSupportedException();
    public Task<UiCalibrationState> CreateCalibrationAsync(string identity, long generation, string kind, CancellationToken token) => throw new NotSupportedException();
    public Task<UiCalibrationState> GetCalibrationAsync(string handle, CancellationToken token) => Task.FromResult(State);
    public async Task<UiCalibrationState> AdvanceCalibrationAsync(string handle, long revision, UiCalibrationInput input, CancellationToken token)
    {
        if (revision != State.Revision) throw new InvalidOperationException("Stale operation revision.");
        if (input.Action == "level")
        {
            LevelAttempts++;
            if (StaleNextLevel)
            { StaleNextLevel = false; State = State with { Revision = State.Revision + 1 }; throw new InvalidOperationException("Stale operation revision."); }
        }
        Inputs.Add(input);
        if (BlockStart && input.Action == "start") { Started.TrySetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, token); }
        if (BlockFirstLevel && Inputs.Count == 1 && input.Action == "level") { Started.TrySetResult(); await ReleaseLevel.Task.WaitAsync(token); }
        if (input.Action == "level" && FailNextLevel)
        { FailNextLevel = false; State = State with { Revision = State.Revision + 1, ToneAcknowledged = false }; throw new TimeoutException("No tone ACK."); }
        if (input.Action == "start" && FailNextStart)
        { FailNextStart = false; State = State with { Revision = State.Revision + 1, Stage = "Failed" }; throw new InvalidOperationException("请结束通话后重试。"); }
        var next = input.Action == "next" ? State.StepIndex + 1 : State.StepIndex;
        var index = input.Action == "next" ? 5 : input.LevelIndex ?? State.LevelIndex;
        State = State with { Revision = State.Revision + 1, Stage = input.Action switch
            { "save" => "Completed", "preview-original" or "preview-enhanced" => "Previewing", "preview-stop" or "retry-curves" => "Results", _ => "AwaitingAnswer" },
            StepIndex = next, Side = next < 6 ? 1 : 2, FrequencyType = next % 6 + 1,
            LevelIndex = index, Decibels = new[] { -55, -49, -41, -35, -25, -19, -11, -5, 3, 7, 13, 17 }[index],
            ToneAcknowledged = true, CanResume = false,
            FiltersReady = input.Action == "retry-curves" || State.FiltersReady,
            PreviewMode = input.Action switch { "preview-original" => "Original", "preview-enhanced" => "Enhanced", "preview-stop" => "Stopped", _ => State.PreviewMode },
            PreviewAudioPlaying = input.Action is "preview-original" or "preview-enhanced",
            PreviewEpoch = State.PreviewEpoch + (input.Action.StartsWith("preview-", StringComparison.Ordinal) ? 1 : 0) };
        return State;
    }
    public Task<UiCalibrationState> CancelCalibrationAsync(string handle, CancellationToken token)
    {
        Cancels++;
        var failed = StopFailuresRemaining > 0;
        if (failed) StopFailuresRemaining--;
        State = State with { Revision = State.Revision + 1, Stage = failed ? "Failed" : "Cancelled",
            Message = failed ? "耳机未确认停止测试，请重试停止。" : "测试已取消。" };
        return Task.FromResult(State);
    }
}

sealed class TestEventService : TestService, IHeadsetEventService
{
    public event Action<BackendEvent>? EventReceived;
    public bool Disposed { get; private set; }
    public List<(string Request, bool Approved)> ExitApprovals { get; } = [];
    public void Push(BackendEvent notification) => EventReceived?.Invoke(notification);
    public Task<BackendSubscription> SubscribeAsync(string role, CancellationToken token)
        => Task.FromResult(new BackendSubscription { BackendInstanceId = "fixture" });
    public Task AcknowledgeExitAsync(string request, bool approved, CancellationToken token)
    { ExitApprovals.Add((request, approved)); return Task.CompletedTask; }
    public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    public Task<IReadOnlyList<UiDevice>> EnumerateAsync(CancellationToken token) => throw new NotSupportedException();
    public Task<UiSnapshot> ConnectAsync(string identity, CancellationToken token) => throw new NotSupportedException();
    public Task DisconnectAsync(CancellationToken token) => throw new NotSupportedException();
    public Task<UiSnapshot> RefreshAsync(CancellationToken token) => throw new NotSupportedException();
    public Task<UiApplyResult> ApplyAsync(UiAction action, CancellationToken token) => throw new NotSupportedException();
    public Task<UiSnapshot> SelectAsync(string identity, CancellationToken token) => throw new NotSupportedException();
    public Task<UiSnapshot> PauseAsync(string identity, CancellationToken token) => throw new NotSupportedException();
    public Task<UiSnapshot> ResumeAsync(string identity, CancellationToken token) => throw new NotSupportedException();
    public Task<UiSnapshot> RefreshAsync(string identity, RefreshScope scope, CancellationToken token) => throw new NotSupportedException();
    public Task<UiApplyResult> ApplyAsync(string identity, long generation, UiAction action, CancellationToken token) => throw new NotSupportedException();
    public Task ActivateMainUiAsync(CancellationToken token) => throw new NotSupportedException();
}
