using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using FluentAvalonia.UI.Controls;
using HeyMelody.Diagnostics;
using HeyMelodyWindows.App;

internal static class FontNoticeTests
{
    private const string PreferenceName = "hideSegoeFluentIconsNotice";
    private const string FailureSuffix = "\n未能保存‘不再提醒’，请重试。";
    private const string Recommendation =
        "您可以从Windows中获取“Segoe Fluent Icons”字体并安装到当前系统，以获取最佳的视觉体验。";

    internal static int RunAssets(HeadlessUnitTestSession session, string output)
    {
        var directory = NewDirectory(output, "font-notice-assets");
        RunUi(session, "font notice follows cached font selection and only an explicit saved hide flag", () =>
        {
            Availability(Path.Combine(directory, "availability.json"));
            return Task.CompletedTask;
        });
        RunUi(session, "font notice keeps default information styling, one action and keyboard access at dialog widths",
            () => AppearanceAndKeyboardAsync(Path.Combine(directory, "appearance.json")));
        return 2;
    }

    internal static int RunSettings(HeadlessUnitTestSession session, string output)
    {
        var directory = NewDirectory(output, "font-notice-settings");
        ReadPreferences(Path.Combine(directory, "reader.json"));
        Console.WriteLine("PASS font notice suppression accepts only the JSON boolean true without rewriting preferences");
        SavePreferences(Path.Combine(directory, "saved.json"));
        Console.WriteLine("PASS font notice suppression saves independently, preserves other preferences and reports failed writes");
        RunUi(session, "font notice dismissal survives Cancel, resource updates, reopening and frontend reinitialization",
            () => DialogAsync(Path.Combine(directory, "dialog.json")));
        RunUi(session, "font notice stays visible after failed persistence and allows a successful retry",
            () => FailureAndRetryAsync(Path.Combine(directory, "retry")));
        return 4;
    }

    private static void ReadPreferences(string path)
    {
        using var preferences = new PreferenceScope(path);
        Check(!ThemePreferences.LoadHideSegoeFluentIconsNotice(), "Missing preferences keep the recommendation eligible.");
        foreach (var fixture in new[]
        {
            "{}", "[]", "true", "null", "{", "{\"hideSegoeFluentIconsNotice\":false}",
            "{\"hideSegoeFluentIconsNotice\":\"true\"}", "{\"hideSegoeFluentIconsNotice\":1}",
            "{\"hideSegoeFluentIconsNotice\":null}", "{\"uiBasicRenderMode\":true}"
        })
        {
            File.WriteAllText(path, fixture);
            Check(!ThemePreferences.LoadHideSegoeFluentIconsNotice(), "Only literal JSON true hides the notice: " + fixture);
            Check(File.ReadAllText(path) == fixture, "Reading the notice preference never rewrites the source file.");
        }
        File.WriteAllText(path, "{\"hideSegoeFluentIconsNotice\":true}");
        Check(ThemePreferences.LoadHideSegoeFluentIconsNotice(), "An explicit saved true is recognized.");
        ThemePreferences.SettingsPathOverride = Path.GetDirectoryName(path);
        Check(!ThemePreferences.LoadHideSegoeFluentIconsNotice(), "An unreadable preferences file keeps the default.");
    }

    private static void SavePreferences(string path)
    {
        using var preferences = new PreferenceScope(path);
        const string original = """
            {"theme":"dark","modelCompatibilityMode":true,"modelFrameRate":"legacy-value",
             "modelGpuAcceleration":false,"uiBasicRenderMode":true,"otherPreference":{"value":42}}
            """;
        File.WriteAllText(path, original);
        Check(ThemePreferences.SaveHideSegoeFluentIconsNotice(true), "The dedicated notice preference can be saved.");
        Check(ThemePreferences.LoadHideSegoeFluentIconsNotice(), "The saved hide flag can be read.");
        CheckOnlyHideFlagChanged(path, original);
        Check(ThemePreferences.SaveHideSegoeFluentIconsNotice(false) && !ThemePreferences.LoadHideSegoeFluentIconsNotice(),
            "The dedicated writer can also restore the default.");
        CheckOnlyHideFlagChanged(path, original);
        Check(ThemePreferences.SaveHideSegoeFluentIconsNotice(true) && ThemePreferences.Save("light"),
            "A later ordinary settings save can succeed.");
        Check(ThemePreferences.LoadHideSegoeFluentIconsNotice(), "Ordinary settings save preserves the hide choice.");
        Check(NoTemporaryFiles(path), "Successful atomic writes leave no temporary files.");

        var blocker = Path.Combine(Path.GetDirectoryName(path)!, "preference-save-blocker");
        File.WriteAllText(blocker, "original blocker");
        ThemePreferences.SettingsPathOverride = Path.Combine(blocker, "preferences.json");
        Check(!ThemePreferences.SaveHideSegoeFluentIconsNotice(true), "A failed independent write reports failure.");
        Check(File.ReadAllText(blocker) == "original blocker" && !ThemePreferences.LoadHideSegoeFluentIconsNotice(),
            "A failed write preserves the blocker and cannot pretend suppression was saved.");
        File.Delete(blocker);
        Check(ThemePreferences.SaveHideSegoeFluentIconsNotice(true) && ThemePreferences.LoadHideSegoeFluentIconsNotice(),
            "The same preference operation can be retried after the storage problem is resolved.");
        Check(NoTemporaryFiles(ThemePreferences.SettingsPathOverride!), "A recovered save leaves no temporary files.");
    }

    private static void Availability(string path)
    {
        using var preferences = new PreferenceScope(path);
        foreach (var family in new string?[] { "Segoe Fluent Icons", null, "Segoe UI" })
        foreach (var fixture in new[] { "{}", "{\"hideSegoeFluentIconsNotice\":true}", "{\"hideSegoeFluentIconsNotice\":\"true\"}" })
        {
            File.WriteAllText(path, fixture);
            AppIcons.Initialize(() => family);
            var panel = new AppSettingsPanel();
            var notice = Notice(panel);
            var expected = family != "Segoe Fluent Icons" && !fixture.Contains(":true", StringComparison.Ordinal);
            Check(notice.IsVisible == expected && notice.IsOpen == expected,
                "Only unavailable Segoe and an absent explicit hide preference show the notice.");
            Check(File.ReadAllText(path) == fixture, "Constructing a panel never writes a hide preference.");
            Check(ReferenceEquals(panel.FindControl<StackPanel>("SettingsRoot")!.Children[0], notice),
                "The recommendation is the first item above the settings groups.");
        }
    }

    private static async Task AppearanceAndKeyboardAsync(string path)
    {
        using var preferences = new PreferenceScope(path);
        AppIcons.Initialize(() => null);
        File.WriteAllText(path, "{}");
        var panel = new AppSettingsPanel();
        var notice = Notice(panel);
        var action = Action(panel);
        var window = new Window { Width = 500, Height = 760, Content = new ScrollViewer { Content = panel } };
        try
        {
            window.Show();
            foreach (var theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
            foreach (var width in new[] { 500d, 600d })
            {
                window.RequestedThemeVariant = theme; window.Width = width;
                await Tick();
                Check(notice.IsEffectivelyVisible && notice.Severity == FAInfoBarSeverity.Informational
                    && notice.IconSource is null && notice.Title == "推荐操作" && notice.Message == Recommendation,
                    "The notice retains the requested wording and default information severity.");
                Check(!notice.IsClosable, "The notice does not offer a close X.");
                Check(notice.GetVisualDescendants().OfType<Button>().Where(button => button.IsEffectivelyVisible)
                    .SequenceEqual(new[] { action }), "The only visible action is the not-again button.");
                Check(notice.GetVisualDescendants().OfType<Panel>()
                    .Single(part => part.Name == "StandardIconArea").IsEffectivelyVisible,
                    "The framework information icon remains visible.");
                var message = notice.GetVisualDescendants().OfType<TextBlock>().Single(text => text.Text == Recommendation);
                Check(message.TextWrapping != TextWrapping.NoWrap, "The recommendation wraps at the available width.");
                Within(message, notice, "The wrapped message fits within the notice.");
                Within(action, notice, "The action fits within the notice.");
                Within(notice, window, "The top notice stays within the narrow window.");
            }
            var firstGroup = panel.GetVisualDescendants().OfType<TextBlock>().Single(text => text.Text == "外观");
            var groupPositionBefore = firstGroup.TranslatePoint(default, panel)!.Value.Y;
            var clicks = 0;
            action.Click += (_, _) => clicks++;
            Check(action.Focus(), "The not-again action accepts keyboard focus.");
            window.KeyPress(Key.Space, RawInputModifiers.None, PhysicalKey.Space, " ");
            window.KeyRelease(Key.Space, RawInputModifiers.None, PhysicalKey.Space, " ");
            await Tick();
            Check(clicks == 1 && ThemePreferences.LoadHideSegoeFluentIconsNotice(),
                "Space activates the action exactly once and immediately persists the preference.");
            Check(!notice.IsVisible && !notice.IsOpen && notice.DesiredSize.Height == 0
                && firstGroup.TranslatePoint(default, panel)!.Value.Y < groupPositionBefore,
                "Successful keyboard dismissal removes the notice and its layout space.");
        }
        finally { window.Close(); }
    }

    private static async Task DialogAsync(string path)
    {
        using var preferences = new PreferenceScope(path);
        AppIcons.Initialize(() => null);
        const string original = "{\"theme\":\"system\",\"uiBasicRenderMode\":false,\"otherPreference\":{\"value\":42}}";
        File.WriteAllText(path, original);
        var previousTheme = Application.Current!.RequestedThemeVariant;
        var service = new NoticeSettingsService();
        await using var vm = new MainViewModel(service, new NoConfirm(), preview: true);
        await vm.InitializeAsync();
        var view = new MainView { DataContext = vm };
        var window = new Window { Content = view, Width = 900, Height = 760 };
        try
        {
            window.Show(); await Tick();
            var operation = view.OpenSettingsDialogAsync();
            var dialog = await SettingsDialogAsync(window);
            var panel = (AppSettingsPanel)dialog.Content!;
            Check(Notice(panel).IsVisible, "A new settings dialog shows the missing-font recommendation.");
            panel.FindControl<ComboBox>("ThemeChoice")!.SelectedIndex = 1;
            panel.FindControl<ToggleSwitch>("UiBasicRenderSwitch")!.IsChecked = true;
            panel.FindControl<ToggleSwitch>("OnlineSwitch")!.IsChecked = false;
            panel.FindControl<TextBox>("KeyBox")!.Text = "unsaved-resource-key";
            Check(File.ReadAllText(path) == original, "Other setting edits remain drafts before notice dismissal.");
            Action(panel).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(ThemePreferences.LoadHideSegoeFluentIconsNotice() && !Notice(panel).IsVisible,
                "The explicit action saves suppression immediately.");
            CheckOnlyHideFlagChanged(path, original);
            Check(service.Saves == 0 && service.Redownloads == 0 && service.Submissions == 0,
                "Saving the notice preference cannot save resources, download artwork or submit device commands.");

            await vm.RedownloadResourcesAsync(); await Tick();
            Check(service.Redownloads == 1 && vm.ResourceStatus == "fixture resource refresh" && !Notice(panel).IsVisible,
                "A real resource presentation update cannot reopen a dismissed notice.");
            Check(panel.FindControl<TextBox>("KeyBox")!.Text == "unsaved-resource-key",
                "Resource presentation does not consume the unsaved panel draft.");
            dialog.Hide(); await operation.WaitAsync(TimeSpan.FromSeconds(3));
            CheckOnlyHideFlagChanged(path, original);
            Check(!ThemePreferences.LoadUiBasicRenderMode(), "Cancel still discards other application drafts.");

            operation = view.OpenSettingsDialogAsync();
            dialog = await SettingsDialogAsync(window);
            panel = (AppSettingsPanel)dialog.Content!;
            Check(!Notice(panel).IsVisible && panel.FindControl<ToggleSwitch>("UiBasicRenderSwitch")!.IsChecked == false,
                "Reopening honors suppression while reloading the saved settings.");
            dialog.Hide(); await operation.WaitAsync(TimeSpan.FromSeconds(3));
            AppIcons.Initialize(() => null);
            Check(!Notice(new AppSettingsPanel()).IsVisible,
                "A newly initialized frontend with unavailable Segoe reads the persisted hide preference.");
            Check(service.Submissions == 0 && service.Saves == 0, "The complete notice interaction sends no headset SET or resource save.");
        }
        finally
        {
            await view.DismissSettingsDialogAsync(); window.Close();
            Application.Current.RequestedThemeVariant = previousTheme;
        }
    }

    private static async Task FailureAndRetryAsync(string blocker)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(blocker)!);
        File.WriteAllText(blocker, "storage blocker");
        using var preferences = new PreferenceScope(Path.Combine(blocker, "preferences.json"));
        AppIcons.Initialize(() => null);
        var panel = new AppSettingsPanel();
        var notice = Notice(panel);
        var action = Action(panel);
        var window = new Window { Content = new ScrollViewer { Content = panel }, Width = 500, Height = 760 };
        try
        {
            window.Show(); await Tick();
            for (var attempt = 0; attempt < 2; attempt++)
            {
                action.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Tick();
                Check(notice.IsOpen && notice.IsVisible && action.IsEnabled && notice.Message == Recommendation + FailureSuffix,
                    "Failed persistence keeps the recommendation and retry action visible without duplicating the failure text.");
                Check(!ThemePreferences.LoadHideSegoeFluentIconsNotice() && File.ReadAllText(blocker) == "storage blocker",
                    "A failed click cannot alter storage or report a saved hide choice.");
            }
            File.Delete(blocker);
            action.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Tick();
            Check(ThemePreferences.LoadHideSegoeFluentIconsNotice() && !notice.IsVisible && !notice.IsOpen,
                "A successful retry persists suppression and removes the notice.");
            Check(!Notice(new AppSettingsPanel()).IsVisible, "A newly created panel honors the recovered save.");
        }
        finally { window.Close(); }
    }

    private static void CheckOnlyHideFlagChanged(string path, string original)
    {
        var saved = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        Check(saved.Remove(PreferenceName), "The dedicated preference is present.");
        Check(JsonNode.DeepEquals(saved, JsonNode.Parse(original)),
            "The independent action preserves theme, renderer, model, resource and unrelated preference values.");
    }

    private static bool NoTemporaryFiles(string path) =>
        Directory.GetFiles(Path.GetDirectoryName(path)!, Path.GetFileName(path) + ".*.tmp").Length == 0;

    private static FAInfoBar Notice(AppSettingsPanel panel) => panel.FindControl<FAInfoBar>("SegoeFluentIconsNotice")!;
    private static Button Action(AppSettingsPanel panel) => panel.FindControl<Button>("HideSegoeFluentIconsNoticeButton")!;

    private static void Within(Control child, Control parent, string reason)
    {
        var point = child.TranslatePoint(default, parent);
        Check(point is { } origin && child.Bounds.Width > 0 && child.Bounds.Height > 0
            && origin.X >= -1 && origin.Y >= -1 && origin.X + child.Bounds.Width <= parent.Bounds.Width + 1
            && origin.Y + child.Bounds.Height <= parent.Bounds.Height + 1, reason);
    }

    private static async Task<FAContentDialog> SettingsDialogAsync(Window window)
    {
        await ModelRendererLifecycleTests.UntilAsync(() => window.GetVisualDescendants().OfType<FAContentDialog>()
            .Any(dialog => Equals(dialog.Title, "应用设置")), "Expected application settings dialog.");
        return window.GetVisualDescendants().OfType<FAContentDialog>().Single(dialog => Equals(dialog.Title, "应用设置"));
    }

    private static void RunUi(HeadlessUnitTestSession session, string name, Func<Task> test)
    {
        session.Dispatch(async () =>
        {
            try { await test(); return true; }
            finally { AppIcons.Initialize(); }
        }, CancellationToken.None).GetAwaiter().GetResult();
        Console.WriteLine("PASS " + name);
    }

    private static string NewDirectory(string output, string name)
    {
        var directory = Path.Combine(output, name + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static async Task Tick()
    {
        Dispatcher.UIThread.RunJobs();
        await Task.Delay(20);
        Dispatcher.UIThread.RunJobs();
    }

    private static void Check(bool condition, string reason)
    {
        if (!condition) throw new InvalidOperationException(reason);
    }

    private sealed class PreferenceScope : IDisposable
    {
        private readonly string? _previousPath = ThemePreferences.SettingsPathOverride;
        private readonly string? _previousDiagnosticRoot = DiagnosticPaths.DataRootOverride;
        internal PreferenceScope(string path)
        {
            ThemePreferences.SettingsPathOverride = path;
            DiagnosticPaths.DataRootOverride = path + ".diagnostics";
        }
        public void Dispose()
        {
            ThemePreferences.SettingsPathOverride = _previousPath;
            DiagnosticPaths.DataRootOverride = _previousDiagnosticRoot;
        }
    }

    private sealed class NoConfirm : IConfirmationService
    {
        public Task<bool> ConfirmAsync(string title, string message, CancellationToken token) => Task.FromResult(false);
    }

    private sealed class NoticeSettingsService : IHeadsetService, IResourceSettingsService
    {
        private readonly PreviewService _inner = new();
        public int Saves { get; private set; }
        public int Redownloads { get; private set; }
        public int Submissions { get; private set; }
        public Task<UiResourceSettings> GetResourceSettingsAsync(CancellationToken token) =>
            Task.FromResult(new UiResourceSettings(HeyMelodyKey: "saved-fixture-key"));
        public Task<UiResourceSettings> SaveResourceSettingsAsync(UiResourceSettings settings, CancellationToken token)
        { Saves++; return Task.FromResult(settings); }
        public Task<UiResourceRedownloadResult> RedownloadResourcesAsync(CancellationToken token)
        {
            Redownloads++;
            return Task.FromResult(new UiResourceRedownloadResult(
                new UiResourceSettings(HeyMelodyKey: "saved-fixture-key", Status: "fixture resource refresh"), true, 0, 0, "fixture only"));
        }
        public Task<IReadOnlyList<UiDevice>> EnumerateAsync(CancellationToken token) => _inner.EnumerateAsync(token);
        public Task<UiSnapshot> ConnectAsync(string identity, CancellationToken token) => _inner.ConnectAsync(identity, token);
        public Task DisconnectAsync(CancellationToken token) => _inner.DisconnectAsync(token);
        public Task<UiSnapshot> RefreshAsync(CancellationToken token) => _inner.RefreshAsync(token);
        public Task<UiApplyResult> ApplyAsync(UiAction action, CancellationToken token)
        { Submissions++; throw new InvalidOperationException("The font recommendation cannot submit device commands."); }
        public ValueTask DisposeAsync() => _inner.DisposeAsync();
    }
}
