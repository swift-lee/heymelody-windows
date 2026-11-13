using System.Runtime.InteropServices;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using FluentAvalonia.UI.Controls;
using HeyMelody.Diagnostics;
using HeyMelodyWindows.App;

/// <summary>Runs only from the test executable, with a missing-font probe and isolated preferences.</summary>
internal static class NativeFontNoticeSmoke
{
    internal static int Run(string? output = null) => NativeSmoke.Run(useRenderingSetup: true,
        basicRenderMode: false, verifyFontNotice: true,
        outputDirectory: output ?? Path.Combine("artifacts", "native-font-notice-" + Guid.NewGuid().ToString("N")));
}

internal static partial class NativeSmoke
{
    private const string FontNoticeMessage =
        "您可以从Windows中获取“Segoe Fluent Icons”字体并安装到当前系统，以获取最佳的视觉体验。";

    private static async Task VerifyFontNoticeAsync(MainWindow window, MainView page,
        FAContentDialog initialDialog, Task initialOperation)
    {
        Check(!AppIcons.UsesSegoeFluentIcons && ThemePreferences.SettingsPathOverride is not null
            && !ThemePreferences.LoadHideSegoeFluentIconsNotice()
            && DiagnosticPaths.DataRootOverride == Path.Combine(_state.Output!, "isolated-diagnostics"),
            "The native font notice uses a missing-font probe and a fresh isolated preference file.");
        await CancelFontNoticeSettingsAsync(window, initialDialog, initialOperation);
        var before = File.ReadAllText(ThemePreferences.SettingsPathOverride!);
        FAContentDialog dialog = null!;
        Task operation = Task.CompletedTask;
        foreach (var scenario in new[]
        {
            (Name: "light", Dark: false, Width: 1000d, Height: 820d),
            (Name: "dark", Dark: true, Width: 1000d, Height: 820d),
            (Name: "narrow", Dark: false, Width: 650d, Height: 720d)
        })
        {
            ((App)Application.Current!).ApplyTheme(scenario.Dark ? "dark" : "light");
            window.Width = scenario.Width; window.Height = scenario.Height;
            await Task.Delay(150);
            operation = page.OpenSettingsDialogAsync();
            dialog = await FindDialogAsync(window, "应用设置");
            await Task.Delay(150);
            Dispatcher.UIThread.RunJobs();
            var panel = (AppSettingsPanel)dialog.Content!;
            var notice = panel.FindControl<FAInfoBar>("SegoeFluentIconsNotice")!;
            var button = panel.FindControl<Button>("HideSegoeFluentIconsNoticeButton")!;
            foreach (var scroll in notice.GetVisualAncestors().OfType<ScrollViewer>())
                scroll.Offset = default;
            Dispatcher.UIThread.RunJobs();
            Check(window.ActualThemeVariant == (scenario.Dark ? ThemeVariant.Dark : ThemeVariant.Light),
                "The native font recommendation follows the current light or dark theme.");
            Check(notice.IsOpen && notice.IsEffectivelyVisible && notice.Title == "推荐操作"
                && notice.Message == FontNoticeMessage && notice.Severity == FAInfoBarSeverity.Informational
                && notice.IconSource is null && !notice.IsClosable,
                "The native settings show the exact informational recommendation with the framework icon.");
            Check(button.IsEffectivelyVisible && Equals(button.Content, "不再提醒")
                && !notice.GetVisualDescendants().OfType<Button>().Single(control => control.Name == "CloseButton").IsVisible,
                "The recommendation provides its action without a separate close button.");
            var origin = notice.TranslatePoint(default, window)!.Value;
            var actionOrigin = button.TranslatePoint(default, notice)!.Value;
            Check(origin.Y >= 0 && origin.X >= 0
                && origin.X + notice.Bounds.Width <= window.Bounds.Width + .5
                && origin.Y + notice.Bounds.Height <= window.Bounds.Height + .5
                && actionOrigin.X >= 0 && actionOrigin.Y >= 0
                && actionOrigin.X + button.Bounds.Width <= notice.Bounds.Width + .5
                && actionOrigin.Y + button.Bounds.Height <= notice.Bounds.Height + .5,
                "The native banner and its action fit the normal and narrow settings widths.");
            foreach (var text in notice.GetVisualDescendants().OfType<TextBlock>()
                .Where(control => control.Text is "推荐操作" or FontNoticeMessage))
                Check(text.IsEffectivelyVisible && !text.TextLayout.TextLines.Any(line => line.HasCollapsed)
                    && text.TextLayout.Width <= text.Bounds.Width + .5
                    && text.TextLayout.Height <= text.Bounds.Height + .5,
                    "The native font recommendation text wraps without clipping or ellipsis.");
            Check(File.ReadAllText(ThemePreferences.SettingsPathOverride!) == before,
                "Opening and inspecting the native recommendation does not save preferences.");
            await CaptureFontNoticeAsync(window, "settings-font-notice-" + scenario.Name);
            _state.Receipts.Add(new
            {
                state = "font-notice-" + scenario.Name, renderScaling = window.RenderScaling,
                clientWidth = window.ClientSize.Width, clientHeight = window.ClientSize.Height,
                noticeBounds = notice.Bounds.ToString(), actionBounds = button.Bounds.ToString(),
                missingSegoeFont = !AppIcons.UsesSegoeFluentIcons
            });
            if (scenario.Name != "narrow")
                await CancelFontNoticeSettingsAsync(window, dialog, operation);
        }

        var finalPanel = (AppSettingsPanel)dialog.Content!;
        var finalNotice = finalPanel.FindControl<FAInfoBar>("SegoeFluentIconsNotice")!;
        var hide = finalPanel.FindControl<Button>("HideSegoeFluentIconsNoticeButton")!;
        Check(hide.Focus(NavigationMethod.Tab) && hide.IsFocused,
            "The native recommendation action accepts keyboard focus.");
        await FontNoticeKeyAsync(window, 0x20);
        Check(!operation.IsCompleted && !finalNotice.IsOpen && !finalNotice.IsVisible
            && ThemePreferences.LoadHideSegoeFluentIconsNotice(),
            "Native Space dismisses the recommendation and saves immediately before the settings dialog closes.");
        using (var persisted = JsonDocument.Parse(File.ReadAllText(ThemePreferences.SettingsPathOverride!)))
            Check(persisted.RootElement.GetProperty("hideSegoeFluentIconsNotice").GetBoolean()
                && persisted.RootElement.GetProperty("theme").GetString() == "light"
                && !persisted.RootElement.GetProperty("uiBasicRenderMode").GetBoolean(),
                "The isolated preference file persists dismissal and preserves the existing appearance choices.");
        await CancelFontNoticeSettingsAsync(window, dialog, operation);
        Check(ThemePreferences.LoadHideSegoeFluentIconsNotice(),
            "Cancelling the surrounding settings dialog keeps the immediate dismissal preference.");
        operation = page.OpenSettingsDialogAsync();
        dialog = await FindDialogAsync(window, "应用设置");
        await Task.Delay(150);
        var reopened = ((AppSettingsPanel)dialog.Content!).FindControl<FAInfoBar>("SegoeFluentIconsNotice")!;
        Check(!reopened.IsOpen && !reopened.IsVisible,
            "Reopening native settings keeps the dismissed font recommendation hidden.");
        await CaptureFontNoticeAsync(window, "settings-font-notice-hidden");
        await CancelFontNoticeSettingsAsync(window, dialog, operation);
        _state.FontNoticeCompleted = true;
    }

    private static async Task CancelFontNoticeSettingsAsync(Window window, FAContentDialog dialog, Task operation)
    {
        var cancel = dialog.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, "取消"));
        Check(cancel.Focus(NavigationMethod.Tab), "The native settings Cancel action accepts keyboard focus.");
        await FontNoticeKeyAsync(window, 0x20);
        await operation.WaitAsync(TimeSpan.FromSeconds(3));
    }

    private static async Task CaptureFontNoticeAsync(Window window, string name)
    {
        await NativeRenderingProbe.WaitForPixelsAsync(window);
        _state.Screenshots.Add(NativeRenderingProbe.Capture(window, Path.Combine(_state.Output!, name + ".png")));
    }

    private static async Task FontNoticeKeyAsync(Window window, int key)
    {
        var hwnd = window.TryGetPlatformHandle()!.Handle;
        if (!FontNoticePostMessage(hwnd, 0x0100, new IntPtr(key), new IntPtr(1))
            || !FontNoticePostMessage(hwnd, 0x0101, new IntPtr(key), new IntPtr(0xC0000001L)))
            throw new InvalidOperationException("Native font-notice keyboard message failed.");
        await Task.Delay(80);
    }

    [DllImport("user32.dll", EntryPoint = "PostMessageW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FontNoticePostMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
}
