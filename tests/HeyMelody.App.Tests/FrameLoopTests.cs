using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using HeyMelodyWindows.App;

/// <summary>The animation-driven 3D rotation loop and its 30 fps, 60 fps and unlimited preferences.</summary>
internal static class FrameLoopTests
{
    internal static int Run(HeadlessUnitTestSession session, string output)
    {
        var pacingChecks = ModelFramePacingTests.Run();
        var policyChecks = ModelRenderPolicyTests.Run();
        Run("model frame rates expose exactly 30 fps, 60 fps and unlimited", () => { Choices(); return Task.CompletedTask; });
        Run("the model frame-rate preference persists beside the theme and compatibility choices", () => { Preference(output); return Task.CompletedTask; });
        Run("the model loop advances on animation ticks and pauses while hidden, minimized or zero-sized", LoopAsync);
        Run("changing only the model frame rate retains its renderer and motion", ChangeRateAsync);
        Run("cleanup activity never changes the model's selected frame cadence", ActivityCadenceAsync);
        return pacingChecks + policyChecks + 5 + FrameCopyTests.Run(session);

        void Run(string name, Func<Task> action)
        {
            session.Dispatch<bool>(async () => { await action(); return true; }, CancellationToken.None).GetAwaiter().GetResult();
            Console.WriteLine("PASS " + name);
        }
    }

    private static void Choices()
    {
        Check(Enum.GetValues<ModelFrameRate>().Select(value => (int)value).Order().SequenceEqual(new[] { 0, 30, 60 }),
            "Only unlimited, 30 fps and 60 fps are available; the former 120 fps cap is removed.");
        foreach (var invalid in new[] { -1, 120, 144, 240, int.MaxValue })
            Check(ModelRenderingPreferences.NormalizeFrameRate((ModelFrameRate)invalid) == ModelFrameRate.Unlimited,
                "An invalid in-process frame-rate value uses the unlimited default.");
    }

    private static void Preference(string output)
    {
        var old = ThemePreferences.SettingsPathOverride;
        ThemePreferences.SettingsPathOverride = Path.Combine(output, "frame-rate-" + Guid.NewGuid().ToString("N"), "preferences.json");
        try
        {
            Check(ThemePreferences.LoadModelFrameRate() == ModelFrameRate.Unlimited, "A missing preference defaults to unlimited.");
            Check(ThemePreferences.Save("dark", false, ModelFrameRate.Fps30) && ThemePreferences.LoadModelFrameRate() == ModelFrameRate.Fps30
                && !ThemePreferences.LoadModelCompatibilityMode(), "The frame rate is saved with compatibility mode disabled.");
            Check(ThemePreferences.Save("light") && ThemePreferences.LoadModelFrameRate() == ModelFrameRate.Fps30 && ThemePreferences.Load() == "light",
                "Saving only the theme keeps the frame-rate choice.");
            foreach (var value in new[] { "\"display\"", "\"120\"", "\"unlimited\"", "\"144\"", "\"invalid\"", "null", "true", "120", "{}", "[]" })
            {
                File.WriteAllText(ThemePreferences.SettingsPathOverride, "{\"modelFrameRate\":" + value + ",\"keep\":\"unchanged\"}");
                Check(ThemePreferences.LoadModelFrameRate() == ModelFrameRate.Unlimited,
                    "Legacy, unknown and malformed frame-rate values all default to unlimited: " + value);
                Check(ThemePreferences.Save("dark"), "A normal settings save succeeds after legacy input.");
                var saved = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(ThemePreferences.SettingsPathOverride))!;
                Check(saved["modelFrameRate"]!.GetValue<string>() == "unlimited" && saved["keep"]!.GetValue<string>() == "unchanged",
                    "Saving migrates the frame-rate value to unlimited while preserving unrelated preferences.");
            }
            Check(ThemePreferences.Save("system", false, ModelFrameRate.Fps60)
                && ThemePreferences.LoadModelFrameRate() == ModelFrameRate.Fps60, "The fixed 60 fps choice round-trips.");
            Check(ThemePreferences.Save("system", false, ModelFrameRate.Unlimited)
                && ThemePreferences.LoadModelFrameRate() == ModelFrameRate.Unlimited, "The unlimited choice round-trips.");
            Check(ThemePreferences.Save("system", false, (ModelFrameRate)240)
                && ThemePreferences.LoadModelFrameRate() == ModelFrameRate.Unlimited, "Saving an invalid enum normalizes it to unlimited.");
        }
        finally { ThemePreferences.SettingsPathOverride = old; }
    }

    private static async Task LoopAsync()
    {
        var pending = new Queue<Action<TimeSpan>>();
        var timestamp = TimeSpan.Zero;
        var factory = new RecordingFilamentRendererFactory();
        var model = new DeviceModelView(new DeviceModelVisual(new ArtworkFile(
            new Uri("avares://HeyMelody/Assets/Devices/EncoFree4Dynaudio/model.glb"), "", EncoFree4DynaudioModelView.ModelSha256),
            DeviceVisuals.GenericProduct), factory, pending.Enqueue);
        var ancestor = new Border { Child = model };
        var window = new Window { Content = ancestor, Width = 280, Height = 240 };
        try
        {
            window.Show();
            Layout();
            await Until(() => model.AnimationActive, "The loop starts once the model is loaded in a visible window.");
            var renderer = factory.Instances.Single();
            Advance(0);
            Check(model.PresentedFrameCount > 0 && !model.LoadingIndicatorVisible, "The startup frame is presented.");
            Layout();
            Advance();
            Layout();

            var initialYaw = model.Motion.Yaw;
            var initialPitch = model.Motion.Pitch;
            var requests = renderer.FrameRequests;
            for (var frame = 0; frame < 3; frame++) Advance();
            Check(PoseUnchanged() && renderer.FrameRequests == requests,
                "The first 300 ms of the idle wait do not rotate or request a frame.");

            var width = renderer.Configuration.Width;
            model.Width = model.Bounds.Width / 2;
            Layout();
            Check(renderer.Configuration.Width < width && renderer.FrameRequests > requests && PoseUnchanged(),
                "Resizing during the idle wait requests a frame for the new viewport without rotating the model.");
            Advance(0);
            Layout();

            requests = renderer.FrameRequests;
            for (var frame = 0; frame < 3; frame++) Advance();
            Check(PoseUnchanged() && renderer.FrameRequests == requests,
                "Through 600 ms the idle pose does not change or request another frame after layout.");
            Advance();
            Check(model.Motion.Yaw > initialYaw && model.Motion.Pitch == initialPitch &&
                renderer.Configuration.YawRadians == (float)model.Motion.Yaw && renderer.FrameRequests == requests + 1,
                "By 700 ms the model rotates and the renderer receives the new pose.");

            window.Hide();
            Layout();
            CheckStopped(renderer, "A hidden window");
            window.Show();
            Layout();
            Check(model.AnimationActive && pending.Count == 1, "Showing the window resumes one loop.");

            window.WindowState = WindowState.Minimized;
            Layout();
            CheckStopped(renderer, "A minimized window");
            window.WindowState = WindowState.Normal;
            Layout();
            Check(model.AnimationActive && pending.Count == 1, "Restoring the window resumes one loop.");

            ancestor.IsVisible = false;
            Layout();
            CheckStopped(renderer, "An invisible ancestor");
            ancestor.IsVisible = true;
            Layout();
            Check(model.AnimationActive && pending.Count == 1, "Showing an ancestor resumes one loop.");

            model.Width = model.Height = 0;
            Layout();
            CheckStopped(renderer, "A zero-sized viewport");
            model.Width = model.Height = double.NaN;
            Layout();
            Check(model.AnimationActive && pending.Count == 1, "Restoring the viewport resumes one loop.");

            window.Hide();
            Layout();
            window.Show();
            Layout();
            Check(model.AnimationActive && pending.Count == 2, "A delayed old callback precedes the resumed loop's callback.");
            var callbacks = model.AnimationCallbackCount;
            requests = renderer.FrameRequests;
            Advance();
            Check(model.AnimationCallbackCount == callbacks && renderer.FrameRequests == requests && pending.Count == 1,
                "A callback from before hiding cannot revive its old chain after restore.");
            Advance();
            Check(model.AnimationCallbackCount == callbacks + 1 && pending.Count == 1, "Only the resumed callback chain continues.");

            bool PoseUnchanged() => model.Motion.Yaw == initialYaw && model.Motion.Pitch == initialPitch &&
                renderer.Configuration.YawRadians == (float)initialYaw && renderer.Configuration.PitchRadians == (float)initialPitch;
        }
        finally { window.Close(); }

        void Advance(int milliseconds = 100)
        {
            Check(pending.TryDequeue(out var callback), "The model has a queued animation callback to advance.");
            timestamp += TimeSpan.FromMilliseconds(milliseconds);
            callback!(timestamp);
        }

        void Layout()
        {
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
        }

        void CheckStopped(RecordingFilamentRenderer renderer, string state)
        {
            Check(!model.AnimationActive && !model.Motion.IsActive && pending.Count == 1,
                state + " stops the loop, leaving only its previously queued callback.");
            var callbacks = model.AnimationCallbackCount;
            var requests = renderer.FrameRequests;
            Advance();
            Check(model.AnimationCallbackCount == callbacks && renderer.FrameRequests == requests && pending.Count == 0,
                state + " prevents a queued callback from submitting work or scheduling another frame.");
        }
    }

    private static async Task ChangeRateAsync()
    {
        var compatibility = ModelRenderingPreferences.CompatibilityMode;
        var originalRate = ModelRenderingPreferences.FrameRate;
        var factory = new RecordingFilamentRendererFactory();
        var model = new DeviceModelView(new DeviceModelVisual(new ArtworkFile(
            new Uri("avares://HeyMelody/Assets/Devices/EncoFree4Dynaudio/model.glb"), "", EncoFree4DynaudioModelView.ModelSha256),
            DeviceVisuals.GenericProduct), factory);
        var window = new Window { Content = model, Width = 280, Height = 240 };
        try
        {
            window.Show(); Dispatcher.UIThread.RunJobs();
            await Until(() => model.AnimationActive, "The rate-change fixture finishes loading.");
            var callbacks = model.AnimationCallbackCount;
            await Until(() => model.AnimationCallbackCount >= callbacks + 2,
                "Consecutive Avalonia animation callbacks drive the default frame loop.");
            var renderer = factory.Instances.Single();
            foreach (var rate in new[] { ModelFrameRate.Fps30, ModelFrameRate.Fps60, ModelFrameRate.Unlimited })
            {
                var yaw = model.Motion.Yaw;
                var pitch = model.Motion.Pitch;
                ModelRenderingPreferences.Apply(compatibility, rate);
                Check(model.Motion.Yaw == yaw && model.Motion.Pitch == pitch,
                    "Changing the frame-rate choice retains the camera and motion state.");
                await Tick();
                Check(factory.CreateCalls == 1 && !renderer.Disposed && model.ModelReady && model.AnimationActive,
                    "A frame-rate-only change does not reload or retire the renderer.");
            }
            window.Hide(); Dispatcher.UIThread.RunJobs();
            ModelRenderingPreferences.Apply(compatibility, ModelFrameRate.Fps30);
            await Tick();
            Check(!model.AnimationActive && factory.CreateCalls == 1,
                "Changing rate while hidden keeps the model paused and preserves its renderer.");
            window.Show(); Dispatcher.UIThread.RunJobs();
            await Until(() => model.AnimationActive, "The hidden rate change applies when the window resumes.");
            Check(factory.CreateCalls == 1 && !renderer.Disposed, "Resuming after a rate change retains the same renderer.");
        }
        finally
        {
            window.Close();
            ModelRenderingPreferences.Apply(compatibility, originalRate);
        }
    }

    private static async Task ActivityCadenceAsync()
    {
        var compatibility = ModelRenderingPreferences.CompatibilityMode;
        var originalRate = ModelRenderingPreferences.FrameRate;
        var factory = new RecordingFilamentRendererFactory();
        var model = new DeviceModelView(new DeviceModelVisual(new ArtworkFile(
            new Uri("avares://HeyMelody/Assets/Devices/EncoFree4Dynaudio/model.glb"), "", EncoFree4DynaudioModelView.ModelSha256),
            DeviceVisuals.GenericProduct), factory);
        var window = new Window { Content = model, Width = 160, Height = 120 };
        long now = 0;
        int cleanups = 0;
        using var cleanup = ArtworkCleanupTriggers.BeginTestScope(() => now, () => cleanups++);
        try
        {
            ArtworkCleanupTriggers.AttachWindow(window);
            window.Show(); Dispatcher.UIThread.RunJobs();
            await Until(() => model.AnimationActive, "The activity-cadence fixture finishes loading.");
            var renderer = factory.Instances.Single();
            var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
            var frame = typeof(DeviceModelView).GetMethod("OnFrame", flags)!;
            int generation = (int)typeof(DeviceModelView).GetField("_loopGeneration", flags)!.GetValue(model)!;
            // The real callback chain is covered separately; isolate this controlled-clock fixture from its queue.
            typeof(DeviceModelView).GetField("_frameSource", flags)!.SetValue(model, null);
            long timestamp = TimeSpan.FromSeconds(100).Ticks;
            void Advance(long ticks)
            {
                timestamp += ticks;
                now = timestamp / TimeSpan.TicksPerMillisecond;
                frame.Invoke(model, [TimeSpan.FromTicks(timestamp), generation]);
                ArtworkCleanupTriggers.FlushPendingForTests();
            }

            // Use a controlled display clock; no wall-clock sleeps or catch-up callback loop is involved.
            for (int i = 0; i < 12; i++) Advance(TimeSpan.TicksPerSecond / 10);
            Check(model.Motion.Phase == ModelOrbitPhase.Idle, "The fixture samples automatic rotation, not a drag.");
            foreach (var rate in new[] { ModelFrameRate.Unlimited, ModelFrameRate.Fps60, ModelFrameRate.Fps30 })
            {
                ModelRenderingPreferences.Apply(compatibility, rate);
                long callbacks = model.AnimationCallbackCount;
                long updates = model.CameraUpdateCount;
                int requests = renderer.FrameRequests;
                int beforeCleanup = cleanups;
                long start = timestamp;
                for (int i = 1; i <= 288; i++)
                {
                    if (i % 48 == 1) ArtworkCleanupTriggers.ScheduleCleanup(ArtworkCleanupTriggers.ExpanderDelay);
                    if (i % 48 == 25) ArtworkCleanupTriggers.ScheduleCleanup(ArtworkCleanupTriggers.ScrollDelay);
                    if (i == 97) HeadlessWindowActivity.SetActive(window, false);
                    if (i == 193) HeadlessWindowActivity.SetActive(window, true);
                    Advance(start + i * TimeSpan.TicksPerSecond / 144 - timestamp);
                }
                long expected = rate == ModelFrameRate.Unlimited ? 288 : (int)rate * 2;
                long actual = model.CameraUpdateCount - updates;
                Check(model.AnimationCallbackCount - callbacks == 288, "Each controlled callback is processed once.");
                Check(Math.Abs(actual - expected) <= 1 && renderer.FrameRequests - requests == actual,
                    $"Cleanup cannot select another cadence: {rate}, expected about {expected}, got {actual}.");
                timestamp += 600 * TimeSpan.TicksPerMillisecond;
                now = timestamp / TimeSpan.TicksPerMillisecond;
                ArtworkCleanupTriggers.FlushPendingForTests();
                Check(cleanups == beforeCleanup + 2 && !ArtworkCleanupTriggers.HasPendingCleanup,
                    "Deactivation requests cleanup once and repeated activity coalesces independently of animation.");
            }
        }
        finally
        {
            window.Close();
            ModelRenderingPreferences.Apply(compatibility, originalRate);
        }
    }

    private static async Task Tick()
    {
        // The explicit headless fake only checks display-driven composition and lifetime.
        await Task.Delay(40);
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }

    private static async Task Until(Func<bool> condition, string reason)
    {
        for (var attempt = 0; attempt < 600 && !condition(); attempt++) { await Task.Delay(25); Dispatcher.UIThread.RunJobs(); }
        Check(condition(), reason);
    }

    private static void Check(bool condition, string reason) { if (!condition) throw new InvalidOperationException(reason); }
}
