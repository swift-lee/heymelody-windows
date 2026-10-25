using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using FluentAvalonia.UI.Controls;
using HeyMelody.Rendering.Filament;
using HeyMelodyWindows.App;
using SkiaSharp;

internal static class CompatibilityRenderingTests
{
    internal static int Run(HeadlessUnitTestSession session, string output)
    {
        Run("model compatibility defaults off and ignores legacy GPU settings", PreferencesAsync);
        Run("backend switching preserves views, frame-rate choices and retirement ordering", ViewsAsync);
        Run("model compatibility drafts cancel and saved choices apply without restart", () => SettingsAsync(output));
        Run("model settings use three labeled frame-rate choices and preserve draft semantics", () => ModelSettingsSliderTests.RunAsync(output));
        Run("renderer failures use static artwork and release resources", () => ModelRendererLifecycleTests.RunAsync(output));
        Run("audited models receive opaque depth correction without changing unknown artwork", ModelMaterialCorrectionTests.RunAsync);
        return 6;

        void Run(string name, Func<Task> action)
        {
            session.Dispatch<bool>(async () =>
            {
                var oldPath = ThemePreferences.SettingsPathOverride;
                var oldPreference = ModelRenderingPreferences.CompatibilityMode;
                var oldRate = ModelRenderingPreferences.FrameRate;
                ThemePreferences.SettingsPathOverride = Path.Combine(output, "compatibility-" + Guid.NewGuid().ToString("N"), "preferences.json");
                ModelRenderingPreferences.Reload();
                try { await action(); }
                finally { ThemePreferences.SettingsPathOverride = oldPath; ModelRenderingPreferences.Apply(oldPreference, oldRate); }
                return true;
            }, CancellationToken.None).GetAwaiter().GetResult();
            Console.WriteLine("PASS " + name);
        }
    }

    private static Task PreferencesAsync()
    {
        var path = ThemePreferences.SettingsPathOverride!;
        Check(!ThemePreferences.LoadModelCompatibilityMode() && !ModelRenderingPreferences.CompatibilityMode,
            "A new installation defaults to compatibility mode off.");
        Check(ModelRenderingPreferences.Backend == DefaultBackend, "The platform chooses Vulkan by default, or Metal on macOS.");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{\"theme\":\"dark\",\"modelGpuAcceleration\":false,\"otherPreference\":42,\"modelFrameRate\":\"30\"}");
        ModelRenderingPreferences.Reload();
        Check(ThemePreferences.Load() == "dark" && !ModelRenderingPreferences.CompatibilityMode,
            "Legacy GPU-off is ignored and does not enable compatibility.");
        Check(ThemePreferences.Save("light", true), "Explicit compatibility can be saved.");
        ModelRenderingPreferences.Reload();
        Check(ThemePreferences.Load() == "light" && ModelRenderingPreferences.CompatibilityMode,
            "Only the saved boolean true enables compatibility.");
        using (var document = JsonDocument.Parse(File.ReadAllText(path)))
            Check(!document.RootElement.TryGetProperty("modelGpuAcceleration", out _)
                && document.RootElement.GetProperty("otherPreference").GetInt32() == 42
                && document.RootElement.GetProperty("modelFrameRate").GetString() == "30",
                "Saving removes the obsolete key while preserving unrelated preferences and frame rate.");
        Check(ThemePreferences.Save("dark") && ThemePreferences.LoadModelCompatibilityMode(),
            "Theme-only saves retain compatibility.");
        Check(ThemePreferences.Save("light", false) && !ThemePreferences.LoadModelCompatibilityMode(),
            "Compatibility can be disabled explicitly.");
        foreach (var json in new[]
        {
            "{", "[]", "{}", "{\"modelCompatibilityMode\":false}", "{\"modelCompatibilityMode\":\"true\"}",
            "{\"modelCompatibilityMode\":\"false\"}", "{\"modelCompatibilityMode\":null}",
            "{\"modelCompatibilityMode\":1}", "{\"modelCompatibilityMode\":[]}", "{\"modelCompatibilityMode\":{}}",
            "{\"modelGpuAcceleration\":true}", "{\"modelGpuAcceleration\":false}"
        })
        {
            File.WriteAllText(path, json);
            ModelRenderingPreferences.Reload();
            Check(!ThemePreferences.LoadModelCompatibilityMode() && ModelRenderingPreferences.Backend == DefaultBackend,
                "Missing, invalid, mistyped and legacy settings retain the default backend.");
        }
        Check(!Directory.EnumerateFiles(Path.GetDirectoryName(path)!, "*.tmp").Any(), "Atomic saves leave no temporary file.");
        return Task.CompletedTask;
    }

    private static FilamentBackend DefaultBackend => OperatingSystem.IsMacOS() ? FilamentBackend.Metal : FilamentBackend.Vulkan;

    private static async Task ViewsAsync()
    {
        var factory = new RecordingFilamentRendererFactory();
        var model = new DeviceModelView(new DeviceModelVisual(new ArtworkFile(
            new Uri("avares://HeyMelody/Assets/Devices/EncoFree4Dynaudio/model.glb"), "",
            EncoFree4DynaudioModelView.ModelSha256), DeviceVisuals.GenericProduct), factory);
        var window = new Window { Content = model, Width = 280, Height = 240 };
        try
        {
            Check(factory.CreateCalls == 0, "Constructing a view does not eagerly create native resources.");
            window.Show(); Dispatcher.UIThread.RunJobs();
            await ModelRendererLifecycleTests.UntilAsync(() => model.ModelReady, "The default renderer loads.");
            Check(model.Diagnostics.ActualBackend == DefaultBackend, "The default renderer receives an explicit backend.");
            model.Motion.BeginDrag(0, 0, 0); model.Motion.MoveDrag(60, 20, .1); model.Motion.EndDrag(.11);
            int before = factory.CreateCalls;
            ModelRenderingPreferences.Apply(false, ModelFrameRate.Fps30);
            await Task.Delay(30);
            Check(factory.CreateCalls == before && model.ModelReady, "A frame-rate-only save does not rebuild the engine.");
            double yaw = model.Motion.Yaw;
            double yawVelocity = model.Motion.YawVelocity;
            ModelRenderingPreferences.Apply(true);
            if (!OperatingSystem.IsMacOS()) Check(!model.ModelReady && model.CaptureLatestFrame(out _) is null,
                "A backend change immediately stops old frames and clears the old bitmap.");
            // Observe the restored state before a display tick legitimately advances the inertia.
            for (int attempt = 0; attempt < 300 && !model.ModelReady; attempt++)
            { await Task.Delay(10); Dispatcher.UIThread.RunJobs(); }
            Check(model.ModelReady, "Compatibility renderer loads.");
            Check(model.Diagnostics.ActualBackend == (OperatingSystem.IsMacOS() ? FilamentBackend.Metal : FilamentBackend.OpenGL)
                && Math.Abs(model.Motion.Yaw - yaw) < .001 && Math.Abs(model.Motion.YawVelocity - yawVelocity) < .001
                && ModelRenderingPreferences.FrameRate == ModelFrameRate.Fps30,
                $"Switching preserves resource, motion and rate: backend={model.Diagnostics.ActualBackend}, "
                + $"yaw={model.Motion.Yaw}/{yaw}, velocity={model.Motion.YawVelocity}/{yawVelocity}, rate={ModelRenderingPreferences.FrameRate}.");
            Check(!factory.CreatedBeforePreviousDisposed, "A replacement is created only after the old renderer is disposed.");
            ModelRenderingPreferences.Apply(false);
            await ModelRendererLifecycleTests.UntilAsync(() => model.ModelReady && model.Diagnostics.ActualBackend == DefaultBackend,
                "Disabling compatibility restores the default backend.");
            window.Content = null; Dispatcher.UIThread.RunJobs();
            Check(!model.ModelReady && !model.AnimationActive, "Detached views release their renderer and frame loop.");
            ModelRenderingPreferences.Apply(true);
            window.Content = model; Dispatcher.UIThread.RunJobs();
            await ModelRendererLifecycleTests.UntilAsync(() => model.ModelReady, "Reattached views read the current shared preference.");
            Check(model.Diagnostics.ActualBackend == ModelRenderingPreferences.Backend, "Reattachment uses the shared backend choice.");
        }
        finally { window.Close(); ModelRenderingPreferences.Apply(false); }
    }

    private static async Task SettingsAsync(string output)
    {
        await using var vm = new MainViewModel(new PreviewService(), new NoConfirm(), preview: true);
        await vm.InitializeAsync();
        await vm.AddDemoDeviceAsync();
        var view = new MainView { DataContext = vm };
        var window = new Window { Content = view, Width = 1280, Height = 900 };
        try
        {
            window.Show(); Dispatcher.UIThread.RunJobs();
            var operation = view.OpenSettingsDialogAsync(); await Task.Delay(20); Dispatcher.UIThread.RunJobs();
            var dialog = Settings(window);
            var choice = BackendChoice(dialog);
            Check(choice.SelectedIndex == 0 && Equals(choice.SelectedItem, "Vulkan"), "The rendering dropdown defaults to Vulkan.");
            Check(choice.Items.Cast<object>().Select(item => item.ToString()).SequenceEqual(["Vulkan", "OpenGL"]),
                "The rendering dropdown contains exactly Vulkan and OpenGL.");
            var card = ((AppSettingsPanel)dialog.Content!).FindControl<FASettingsExpanderItem>("ModelCompatibilityCard")!;
            Check(Equals(card.Description, "若存在渲染问题，请尝试切换到 OpenGL")
                && Avalonia.Automation.AutomationProperties.GetName(choice) == "3D模型渲染API",
                "The rendering API row keeps its exact title, guidance and accessibility name.");
            Check(!dialog.GetVisualDescendants().OfType<ToggleSwitch>().Any(control => control.Name == "ModelCompatibilitySwitch"),
                "The previous renderer toggle is absent.");
            Check(card.IsVisible == !OperatingSystem.IsMacOS(), "macOS hides the compatibility row.");
            if (card.IsVisible)
            {
                card.BringIntoView();
                await Task.Delay(250); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); Dispatcher.UIThread.RunJobs();
                using var full = window.CaptureRenderedFrame() ?? throw new InvalidOperationException("Missing compatibility row frame.");
                using var memory = new MemoryStream();
                full.Save(memory, Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
                using var bitmap = SKBitmap.Decode(memory.ToArray());
                var origin = card.TranslatePoint(default, window)!.Value;
                var scale = window.RenderScaling;
                var rect = new SKRectI((int)Math.Floor(origin.X * scale), (int)Math.Floor(origin.Y * scale),
                    (int)Math.Ceiling((origin.X + card.Bounds.Width) * scale), (int)Math.Ceiling((origin.Y + card.Bounds.Height) * scale));
                using var crop = new SKBitmap();
                Check(bitmap.ExtractSubset(crop, rect), "The complete compatibility row fits its settings container.");
                using var encoded = crop.Encode(SKEncodedImageFormat.Png, 100);
                using var file = new FileStream(Path.Combine(output, "settings-compatibility.png"), FileMode.CreateNew);
                encoded.SaveTo(file);
            }
            choice.SelectedIndex = -1;
            Check(dialog.Content is AppSettingsPanel panel && !panel.ModelCompatibilityMode,
                "An invalid dropdown selection maps to the Vulkan default.");
            choice.SelectedIndex = 1;
            Check(!ModelRenderingPreferences.CompatibilityMode, "Editing a draft leaves the active backend unchanged.");
            dialog.Hide(); await operation.WaitAsync(TimeSpan.FromSeconds(3));
            Check(!ModelRenderingPreferences.CompatibilityMode && !File.Exists(ThemePreferences.SettingsPathOverride!),
                "Cancel preserves runtime preferences and the settings file.");
            var model = view.GetVisualDescendants().OfType<EncoFree4DynaudioModelView>().Single();
            foreach (bool enabled in new[] { true, false })
            {
                operation = view.OpenSettingsDialogAsync(); await Task.Delay(20); Dispatcher.UIThread.RunJobs();
                dialog = Settings(window); choice = BackendChoice(dialog);
                Check(choice.SelectedIndex == (ModelRenderingPreferences.CompatibilityMode ? 1 : 0),
                    "Reopening settings displays the currently saved Vulkan or OpenGL selection.");
                choice.SelectedIndex = enabled ? 1 : 0;
                ClickSave(window, dialog); await operation.WaitAsync(TimeSpan.FromSeconds(3));
                await ModelRendererLifecycleTests.UntilAsync(() => model.ModelReady
                    && model.Diagnostics.ActualBackend == ModelRenderingPreferences.Backend, "Saving applies to the current model.");
                Check(ModelRenderingPreferences.CompatibilityMode == enabled
                    && ThemePreferences.LoadModelCompatibilityMode() == enabled, "Saved mode persists and applies immediately.");
            }
            var originalPath = ThemePreferences.SettingsPathOverride!;
            Directory.CreateDirectory(Path.GetDirectoryName(originalPath)!);
            var blockingFile = Path.Combine(Path.GetDirectoryName(originalPath)!, "blocked");
            File.WriteAllText(blockingFile, "fixture");
            ThemePreferences.SettingsPathOverride = Path.Combine(blockingFile, "preferences.json");
            operation = view.OpenSettingsDialogAsync(); await Task.Delay(20); Dispatcher.UIThread.RunJobs();
            dialog = Settings(window); BackendChoice(dialog).SelectedIndex = 1; ClickSave(window, dialog);
            await ModelRendererLifecycleTests.UntilAsync(() => window.GetVisualDescendants().OfType<FAContentDialog>()
                .Any(d => Equals(d.Title, "设置未保存")), "Save failure is shown to the user.");
            Check(ModelRenderingPreferences.CompatibilityMode, "A failed disk save still applies the current compatibility mode.");
            window.GetVisualDescendants().OfType<FAContentDialog>().Single(d => Equals(d.Title, "设置未保存")).Hide();
            await operation.WaitAsync(TimeSpan.FromSeconds(3));
            ThemePreferences.SettingsPathOverride = originalPath;
        }
        finally { await view.DismissSettingsDialogAsync(); window.Close(); }
    }

    private static FAContentDialog Settings(Window window) => window.GetVisualDescendants().OfType<FAContentDialog>()
        .Single(d => Equals(d.Title, "应用设置"));
    private static ComboBox BackendChoice(FAContentDialog dialog) =>
        ((AppSettingsPanel)dialog.Content!).FindControl<ComboBox>("ModelBackendChoice")!;
    private static void ClickSave(Window window, FAContentDialog dialog)
    {
        var button = dialog.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, "保存"));
        button.BringIntoView(); Dispatcher.UIThread.RunJobs();
        var point = button.TranslatePoint(new Avalonia.Point(button.Bounds.Width / 2, button.Bounds.Height / 2), window)!.Value;
        window.MouseDown(point, MouseButton.Left); window.MouseUp(point, MouseButton.Left); Dispatcher.UIThread.RunJobs();
    }
    private static void Check(bool condition, string reason) { if (!condition) throw new InvalidOperationException(reason); }
    private sealed class NoConfirm : IConfirmationService
    { public Task<bool> ConfirmAsync(string title, string message, CancellationToken token) => Task.FromResult(false); }
}
