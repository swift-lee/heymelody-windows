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
        var model = new EncoFree4DynaudioModelView();
        var ancestor = new Border { Child = model };
        var window = new Window { Content = ancestor, Width = 280, Height = 240 };
        try
        {
            window.Show(); Dispatcher.UIThread.RunJobs();
            await Until(() => model.AnimationActive, "The loop starts once the model is loaded in a visible window.");
            var before = model.CameraUpdateCount;
            await Task.Delay(40); Dispatcher.UIThread.RunJobs();
            Check(model.CameraUpdateCount == before, "The initial automatic-rotation wait does not submit camera updates.");
            // Idle rotation starts 0.65 s after activation; tick through that pause first.
            var clock = System.Diagnostics.Stopwatch.StartNew();
            while (clock.Elapsed < TimeSpan.FromSeconds(ModelOrbitMotion.IdleDelaySeconds + .2)) await Tick();
            before = model.CameraUpdateCount;
            for (var frame = 0; frame < 6; frame++) await Tick();
            Check(model.CameraUpdateCount >= before + 4, "Spaced display frames advance the idle rotation; got "
                + (model.CameraUpdateCount - before) + " updates for 6 frames.");
            var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
            int oldGeneration = (int)typeof(DeviceModelView).GetField("_loopGeneration", flags)!.GetValue(model)!;
            window.Hide(); Dispatcher.UIThread.RunJobs();
            Check(!model.AnimationActive && !model.Motion.IsActive, "Hiding the window stops the loop.");
            var hidden = model.CameraUpdateCount;
            for (var frame = 0; frame < 3; frame++) await Tick();
            Check(model.CameraUpdateCount == hidden, "A hidden window receives no camera updates.");
            window.Show(); Dispatcher.UIThread.RunJobs();
            Check(model.AnimationActive, "Showing the window resumes the loop.");
            long callbacks = model.AnimationCallbackCount;
            typeof(DeviceModelView).GetMethod("OnFrame", flags)!.Invoke(model, [TimeSpan.FromDays(1), oldGeneration]);
            Check(model.AnimationCallbackCount == callbacks, "A callback from before hiding cannot revive its old chain after restore.");
            window.WindowState = WindowState.Minimized;
            Dispatcher.UIThread.RunJobs();
            Check(!model.AnimationActive && !model.Motion.IsActive, "Minimizing the window stops model work.");
            hidden = model.CameraUpdateCount;
            for (var frame = 0; frame < 3; frame++) await Tick();
            Check(model.CameraUpdateCount == hidden, "A minimized window receives no queued camera updates.");
            window.WindowState = WindowState.Normal;
            Dispatcher.UIThread.RunJobs();
            Check(model.AnimationActive, "Restoring the window resumes the model loop.");
            ancestor.IsVisible = false;
            await Tick();
            Check(!model.AnimationActive && !model.Motion.IsActive, "Hiding an ancestor container stops model work.");
            hidden = model.CameraUpdateCount;
            for (var frame = 0; frame < 3; frame++) await Tick();
            Check(model.CameraUpdateCount == hidden, "An invisible ancestor prevents queued camera updates.");
            ancestor.IsVisible = true;
            Dispatcher.UIThread.RunJobs();
            await Until(() => model.AnimationActive, "Showing an ancestor restarts the model frame loop.");
            model.Width = model.Height = 0;
            await Tick();
            await Until(() => !model.AnimationActive, "A zero-sized model viewport stops the loop.");
            hidden = model.CameraUpdateCount;
            for (var frame = 0; frame < 3; frame++) await Tick();
            Check(model.CameraUpdateCount == hidden, "A zero-sized viewport submits no queued camera updates.");
            model.Width = model.Height = double.NaN;
            await Tick();
            await Until(() => model.AnimationActive, "Restoring the model allocation resumes the loop.");
        }
        finally { window.Close(); }
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
