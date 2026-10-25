using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using FluentAvalonia.UI.Controls;
using HeyMelodyWindows.App;

internal static class RenderingSettingsTests
{
    internal static int Run(HeadlessUnitTestSession session, string output)
    {
        var directory = Path.Combine(output, "ui-rendering-settings-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        ReadPreferences(Path.Combine(directory, "reader.json"));
        Console.WriteLine("PASS ordinary UI rendering preference accepts only JSON true");
        SavePreferences(Path.Combine(directory, "saved.json"));
        Console.WriteLine("PASS ordinary UI rendering preference saves atomically and preserves unrelated preferences");
        session.Dispatch<bool>(async () =>
        {
            await SettingsAsync(Path.Combine(directory, "dialog.json"));
            return true;
        }, CancellationToken.None).GetAwaiter().GetResult();
        Console.WriteLine("PASS ordinary UI rendering drafts, save notices and failure recovery do not submit device settings");
        return 3;
    }

    private static void ReadPreferences(string path)
    {
        Check(!UiRenderingPreferenceReader.Load(path), "A missing file retains the GPU default.");
        foreach (var fixture in new[]
        {
            "{}", "[]", "true", "null", "{", "{\"uiBasicRenderMode\":false}",
            "{\"uiBasicRenderMode\":\"true\"}", "{\"uiBasicRenderMode\":1}",
            "{\"uiBasicRenderMode\":null}", "{\"modelGpuAcceleration\":false,\"modelCompatibilityMode\":true}"
        })
        {
            File.WriteAllText(path, fixture);
            Check(!UiRenderingPreferenceReader.Load(path), "Only the dedicated JSON boolean true enables Basic Render: " + fixture);
            Check(File.ReadAllText(path) == fixture, "Reading preferences must never rewrite them.");
        }
        File.WriteAllText(path, "{\"uiBasicRenderMode\":true}");
        Check(UiRenderingPreferenceReader.Load(path), "The explicit Basic Render preference is recognized.");
        Check(!UiRenderingPreferenceReader.Load(Path.GetDirectoryName(path)), "An unreadable file retains the GPU default.");
    }

    private static void SavePreferences(string path)
    {
        var previous = ThemePreferences.SettingsPathOverride;
        try
        {
            ThemePreferences.SettingsPathOverride = path;
            File.WriteAllText(path, "{\"theme\":\"dark\",\"modelCompatibilityMode\":true,\"modelFrameRate\":\"30\",\"otherPreference\":{\"value\":42}}");
            Check(!ThemePreferences.LoadUiBasicRenderMode(), "The App reads the same missing-value default as child processes.");
            Check(ThemePreferences.Save("light", uiBasicRenderMode: true), "The Basic Render choice can be saved.");
            Check(ThemePreferences.LoadUiBasicRenderMode() && UiRenderingPreferenceReader.Load(path), "App and shared readers agree after saving.");
            using (var saved = JsonDocument.Parse(File.ReadAllText(path)))
            {
                var root = saved.RootElement;
                Check(root.GetProperty("theme").GetString() == "light"
                    && root.GetProperty("modelCompatibilityMode").GetBoolean()
                    && root.GetProperty("modelFrameRate").GetString() == "30"
                    && root.GetProperty("otherPreference").GetProperty("value").GetInt32() == 42,
                    "Saving the ordinary UI renderer preserves model and unrelated preferences.");
            }
            Check(ThemePreferences.Save("dark") && ThemePreferences.LoadUiBasicRenderMode(), "Omitting the new argument preserves its saved value.");
            Check(ThemePreferences.Save("dark", uiBasicRenderMode: false) && !ThemePreferences.LoadUiBasicRenderMode(), "Explicit false restores the GPU preference.");
            Check(Directory.GetFiles(Path.GetDirectoryName(path)!, Path.GetFileName(path) + ".*.tmp").Length == 0,
                "Successful atomic writes leave no temporary preference files.");
            var blocker = Path.Combine(Path.GetDirectoryName(path)!, "save-blocker");
            File.WriteAllText(blocker, "original blocker");
            ThemePreferences.SettingsPathOverride = Path.Combine(blocker, "preferences.json");
            Check(!ThemePreferences.Save("light", uiBasicRenderMode: true), "A failed write is reported as failure.");
            Check(File.ReadAllText(blocker) == "original blocker" && !UiRenderingPreferenceReader.Load(ThemePreferences.SettingsPathOverride),
                "Failed writes neither replace the blocker nor pretend the preference was enabled.");
        }
        finally { ThemePreferences.SettingsPathOverride = previous; }
    }

    private static async Task SettingsAsync(string path)
    {
        var previousPath = ThemePreferences.SettingsPathOverride;
        var previousTheme = Application.Current!.RequestedThemeVariant;
        var previousModelMode = ModelRenderingPreferences.CompatibilityMode;
        var previousModelRate = ModelRenderingPreferences.FrameRate;
        ThemePreferences.SettingsPathOverride = path;
        File.WriteAllText(path, "{\"theme\":\"system\",\"uiBasicRenderMode\":false,\"otherPreference\":42}");
        var original = File.ReadAllText(path);
        var service = new ReadOnlySettingsService();
        await using var vm = new MainViewModel(service, new NoConfirm(), preview: true);
        await vm.InitializeAsync();
        var view = new MainView { DataContext = vm };
        var window = new Window { Content = view, Width = 1280, Height = 900 };
        try
        {
            window.Show(); Dispatcher.UIThread.RunJobs();
            var operation = view.OpenSettingsDialogAsync();
            var dialog = await DialogAsync(window, "应用设置");
            var panel = (AppSettingsPanel)dialog.Content!;
            var card = panel.FindControl<FASettingsExpanderItem>("UiBasicRenderCard")!;
            var toggle = panel.FindControl<ToggleSwitch>("UiBasicRenderSwitch")!;
            Check(card.IsVisible == (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()) && toggle.IsChecked == false,
                "Supported desktop platforms expose compatibility mode and reflect the saved preference.");
            Check(Avalonia.Automation.AutomationProperties.GetName(toggle) == "使用 Basic Render 兼容模式",
                "The compatibility switch has a distinct accessible name.");
            toggle.IsChecked = true;
            Check(!ThemePreferences.LoadUiBasicRenderMode() && File.ReadAllText(path) == original,
                "Changing the switch only edits a local draft.");
            dialog.Hide(); await operation.WaitAsync(TimeSpan.FromSeconds(3));
            Check(File.ReadAllText(path) == original, "Cancelling discards the renderer draft.");

            foreach (var enabled in new[] { true, false })
            {
                operation = view.OpenSettingsDialogAsync();
                dialog = await DialogAsync(window, "应用设置");
                panel = (AppSettingsPanel)dialog.Content!;
                panel.FindControl<ToggleSwitch>("UiBasicRenderSwitch")!.IsChecked = enabled;
                ClickSave(window, dialog);
                var notice = await DialogAsync(window, "界面渲染设置已保存");
                var message = notice.Content?.ToString() ?? "";
                Check(ThemePreferences.LoadUiBasicRenderMode() == enabled && message.Contains("主界面")
                    && message.Contains("设备管理器") && message.Contains("贴合度测试") && message.Contains("黄金听感")
                    && message.Contains("无需退出托盘后台"), "Changed, persisted choices explain all GUI restarts while keeping Host running.");
                notice.Hide(); await operation.WaitAsync(TimeSpan.FromSeconds(3));
            }

            operation = view.OpenSettingsDialogAsync();
            dialog = await DialogAsync(window, "应用设置");
            Check(((AppSettingsPanel)dialog.Content!).FindControl<ToggleSwitch>("UiBasicRenderSwitch")!.IsChecked == false,
                "Reopening reflects the restored GPU preference.");
            ClickSave(window, dialog);
            await operation.WaitAsync(TimeSpan.FromSeconds(3));
            Check(!window.GetVisualDescendants().OfType<FAContentDialog>().Any(d => Equals(d.Title, "界面渲染设置已保存")),
                "Saving an unchanged renderer choice does not request a restart.");

            var beforeFailure = File.ReadAllText(path);
            var blocker = Path.Combine(Path.GetDirectoryName(path)!, "dialog-blocker");
            File.WriteAllText(blocker, "blocker");
            ThemePreferences.SettingsPathOverride = Path.Combine(blocker, "preferences.json");
            operation = view.OpenSettingsDialogAsync();
            dialog = await DialogAsync(window, "应用设置");
            ((AppSettingsPanel)dialog.Content!).FindControl<ToggleSwitch>("UiBasicRenderSwitch")!.IsChecked = true;
            ClickSave(window, dialog);
            var failure = await DialogAsync(window, "设置未保存");
            Check(failure.Content?.ToString()?.Contains("新的界面渲染选择未保存") == true
                && File.ReadAllText(path) == beforeFailure,
                "Write failure reports that the renderer was not saved and preserves the previous file.");
            failure.Hide(); await operation.WaitAsync(TimeSpan.FromSeconds(3));
            Check(service.Submissions == 0, "Application rendering settings never submit headset SET commands.");
        }
        finally
        {
            await view.DismissSettingsDialogAsync(); window.Close();
            ThemePreferences.SettingsPathOverride = previousPath;
            Application.Current.RequestedThemeVariant = previousTheme;
            ModelRenderingPreferences.Apply(previousModelMode, previousModelRate);
        }
    }

    private static async Task<FAContentDialog> DialogAsync(Window window, string title)
    {
        await ModelRendererLifecycleTests.UntilAsync(() => window.GetVisualDescendants().OfType<FAContentDialog>()
            .Any(dialog => Equals(dialog.Title, title)), "Expected dialog: " + title);
        return window.GetVisualDescendants().OfType<FAContentDialog>().Single(dialog => Equals(dialog.Title, title));
    }

    private static void ClickSave(Window window, FAContentDialog dialog)
    {
        var button = dialog.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, "保存"));
        button.BringIntoView(); Dispatcher.UIThread.RunJobs();
        var point = button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), window)!.Value;
        window.MouseDown(point, MouseButton.Left); window.MouseUp(point, MouseButton.Left); Dispatcher.UIThread.RunJobs();
    }

    private static void Check(bool condition, string reason) { if (!condition) throw new InvalidOperationException(reason); }
    private sealed class NoConfirm : IConfirmationService
    { public Task<bool> ConfirmAsync(string title, string message, CancellationToken token) => Task.FromResult(false); }
    private sealed class ReadOnlySettingsService : IHeadsetService
    {
        private readonly PreviewService _inner = new();
        public int Submissions { get; private set; }
        public Task<IReadOnlyList<UiDevice>> EnumerateAsync(CancellationToken token) => _inner.EnumerateAsync(token);
        public Task<UiSnapshot> ConnectAsync(string identity, CancellationToken token) => _inner.ConnectAsync(identity, token);
        public Task DisconnectAsync(CancellationToken token) => _inner.DisconnectAsync(token);
        public Task<UiSnapshot> RefreshAsync(CancellationToken token) => _inner.RefreshAsync(token);
        public Task<UiApplyResult> ApplyAsync(UiAction action, CancellationToken token)
        { Submissions++; throw new InvalidOperationException("Rendering settings cannot submit device commands."); }
        public ValueTask DisposeAsync() => _inner.DisposeAsync();
    }
}
