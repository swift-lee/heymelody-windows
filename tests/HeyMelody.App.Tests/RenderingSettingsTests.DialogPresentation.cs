using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using FluentAvalonia.UI.Controls;
using HeyMelodyWindows.App;

internal static partial class RenderingSettingsTests
{
    private static async Task DialogPresentationAsync(string path)
    {
        var previousPath = ThemePreferences.SettingsPathOverride;
        var previousTheme = Application.Current!.RequestedThemeVariant;
        ThemePreferences.SettingsPathOverride = path;
        var service = new ReadOnlySettingsService();
        await using var vm = new MainViewModel(service, new NoConfirm(), preview: true);
        await vm.InitializeAsync();
        var view = new MainView { DataContext = vm };
        var window = new Window { Content = view, Width = 1000, Height = 740 };
        try
        {
            window.Show(); Dispatcher.UIThread.RunJobs();
            foreach (var dark in new[] { false, true })
            {
                Check(ThemePreferences.Save(dark ? "dark" : "light"), "The isolated theme is saved.");
                Application.Current.RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light;
                var saved = File.ReadAllText(path);
                var operation = view.OpenSettingsDialogAsync();
                var dialog = await DialogAsync(window, "应用设置");
                await ObserveUnscaledAsync(dialog);
                Check(DialogPart<Border>(dialog, "BackgroundElement").Effect is not null,
                    "The settings dialog retains the default shadow.");

                var panel = (AppSettingsPanel)dialog.Content!;
                panel.FindControl<ComboBox>("ThemeChoice")!.SelectedIndex = dark ? 1 : 2;
                Check(window.ActualThemeVariant == (dark ? ThemeVariant.Light : ThemeVariant.Dark),
                    "The theme choice still previews immediately.");
                await ObserveUnscaledAsync(dialog);

                var firstContainer = DialogPart<Border>(dialog, "Container");
                dialog.Template = new ReappliedDialogTemplate(dialog.Template!);
                dialog.ApplyTemplate(); window.UpdateLayout();
                Check(!ReferenceEquals(firstContainer, DialogPart<Border>(dialog, "Container")),
                    "The regression really rebuilds the library template.");
                await ObserveUnscaledAsync(dialog);

                var cancel = dialog.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, "取消"));
                Check(cancel.Focus(), "Keyboard input is focused inside the rebuilt settings dialog.");
                window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
                window.KeyRelease(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
                await ObserveUnscaledAsync(dialog);
                await operation.WaitAsync(TimeSpan.FromSeconds(3));
                Check(File.ReadAllText(path) == saved
                    && window.ActualThemeVariant == (dark ? ThemeVariant.Dark : ThemeVariant.Light),
                    "Escape discards the preview and restores the saved theme without writing preferences.");
            }

            var ordinary = new FAContentDialog
            {
                Title = "普通对话框动画对照", Content = "Only application settings changes its presentation.", CloseButtonText = "关闭"
            };
            var ordinaryOperation = ordinary.ShowAsync(window);
            await DialogAsync(window, "普通对话框动画对照");
            var sawScale = false;
            for (var sample = 0; sample < 8; sample++)
            {
                TickDialogAnimation();
                sawScale |= !(DialogPart<Border>(ordinary, "Container").RenderTransform?.Value ?? Matrix.Identity).IsIdentity;
                await Task.Delay(15);
            }
            Check(sawScale, "An ordinary dialog still performs the library's actual scale animation.");
            ordinary.Hide(); await ordinaryOperation.WaitAsync(TimeSpan.FromSeconds(3));

            var original = File.ReadAllText(path);
            var closingOperation = view.OpenSettingsDialogAsync();
            var closingDialog = await DialogAsync(window, "应用设置");
            ((AppSettingsPanel)closingDialog.Content!).FindControl<ToggleSwitch>("UiBasicRenderSwitch")!.IsChecked = true;
            var dismiss = view.DismissSettingsDialogAsync();
            await ObserveUnscaledAsync(closingDialog);
            await dismiss.WaitAsync(TimeSpan.FromSeconds(3));
            window.Close();
            await closingOperation.WaitAsync(TimeSpan.FromSeconds(3));
            Check(File.ReadAllText(path) == original && service.Submissions == 0,
                "Window dismissal discards drafts, finishes the dialog lease and never submits a headset command.");
        }
        finally
        {
            try { await view.DismissSettingsDialogAsync(); window.Close(); }
            finally
            {
                ThemePreferences.SettingsPathOverride = previousPath;
                Application.Current.RequestedThemeVariant = previousTheme;
            }
        }
    }

    private static T DialogPart<T>(FAContentDialog dialog, string name) where T : Control =>
        dialog.GetVisualDescendants().OfType<T>()
            .SingleOrDefault(control => control.Name == name && ReferenceEquals(control.TemplatedParent, dialog))
        ?? throw new InvalidOperationException($"FluentAvalonia dialog template is incompatible: missing {name}.");

    private static async Task ObserveUnscaledAsync(FAContentDialog dialog)
    {
        for (var sample = 0; sample < 6; sample++)
        {
            TickDialogAnimation();
            Check((DialogPart<Border>(dialog, "Container").RenderTransform?.Value ?? Matrix.Identity).IsIdentity,
                "The application settings container must not scale during opening, preview, retemplating or closing.");
            await Task.Delay(15);
        }
    }

    private static void TickDialogAnimation()
    {
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }

    private sealed class ReappliedDialogTemplate(IControlTemplate template) : IControlTemplate
    {
        public TemplateResult<Control>? Build(TemplatedControl param) => template.Build(param);
    }
}
