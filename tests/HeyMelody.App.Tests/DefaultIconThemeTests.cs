using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using FluentAvalonia.UI.Controls;
using FluentAvalonia.UI.Input;
using HeyMelodyWindows.App;

/// <summary>Framework control themes retain their own pictograms and behavior.</summary>
internal static class DefaultIconThemeTests
{
    internal static int Run(HeadlessUnitTestSession session)
    {
        Run("light default control icons keep selection and expansion behavior", () => Controls(ThemeVariant.Light));
        Run("dark default control icons keep selection and expansion behavior", () => Controls(ThemeVariant.Dark));
        Run("info bar severity and close actions use framework defaults", InfoBars);
        Run("the framework text flyout retains its standard commands and targets", TextCommands);
        return 4;

        void Run(string name, Func<Task> action)
        {
            session.Dispatch(async () => { await action(); return true; }, CancellationToken.None)
                .GetAwaiter().GetResult();
            Console.WriteLine("PASS " + name);
        }
    }

    private static async Task Controls(ThemeVariant theme)
    {
        var combo = new ComboBox { ItemsSource = new[] { "First", "Second" }, SelectedIndex = 0 };
        var check = new CheckBox { Content = "Three state", IsThreeState = true };
        var expander = new FASettingsExpander { Header = "Expandable settings", IsExpanded = false };
        expander.Items.Add(new FASettingsExpanderItem { Content = "A setting" });
        var panel = new StackPanel { Spacing = 12, Children = { combo, check, expander } };
        var window = new Window { Width = 440, Height = 400, Content = panel, RequestedThemeVariant = theme };
        try
        {
            window.Show();
            await Tick();
            var arrow = Part(combo, "DropDownGlyph");
            Check(arrow.IsEffectivelyVisible, "The default drop-down arrow remains visible.");
            var changed = 0;
            combo.SelectionChanged += (_, _) => changed++;
            combo.SelectedIndex = 1;
            await Tick();
            Check(changed == 1 && combo.SelectedItem as string == "Second", "Selection changes exactly once.");
            combo.IsDropDownOpen = true;
            await Tick();
            Check(combo.GetVisualDescendants().OfType<Popup>().Any(popup => popup.IsOpen),
                "The original drop-down popup still opens.");
            combo.IsDropDownOpen = false;

            Check(check.Focus(), "The original check box remains keyboard-focusable.");
            Space(window);
            await Tick();
            Check(check.IsChecked == true, "Space checks the box.");
            Space(window);
            await Tick();
            Check(check.IsChecked is null, "The next Space preserves the indeterminate state.");
            Space(window);
            await Tick();
            Check(check.IsChecked == false, "The next Space restores the unchecked state.");
            check.IsEnabled = false;
            Space(window);
            Check(check.IsChecked == false, "A disabled check box does not change.");

            var chevron = Part(expander, "ExpandCollapseChevron");
            var header = expander.GetVisualDescendants().OfType<ToggleButton>()
                .Single(button => button.Name == "ToggleButton");
            Check(header.Focus(), "The original settings header remains keyboard-focusable.");
            Space(window);
            await Tick();
            Check(expander.IsExpanded, "Space on the header still expands the settings content.");
            Check(chevron.RenderTransform?.Value.M11 < 0 && chevron.RenderTransform.Value.M22 < 0,
                "The framework chevron rotates for the expanded state.");
            Space(window);
            await Tick();
            Check(!expander.IsExpanded, "Space collapses the same settings content.");
        }
        finally { window.Close(); }
    }

    private static async Task InfoBars()
    {
        var bar = new FAInfoBar { IsOpen = true, IsClosable = true, Title = "Status", Message = "A message" };
        var window = new Window { Width = 500, Height = 220, Content = bar };
        try
        {
            window.Show();
            await Tick();
            foreach (var theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
            {
                window.RequestedThemeVariant = theme;
                foreach (var severity in Enum.GetValues<FAInfoBarSeverity>())
                {
                    bar.Severity = severity;
                    await Tick();
                    Check(bar.IconSource is null, "Severity uses the framework's standard icon area.");
                    var user = bar.GetVisualDescendants().OfType<Viewbox>().Single(box => box.Name == "UserIconBox");
                    var standard = bar.GetVisualDescendants().OfType<Panel>().Single(panel => panel.Name == "StandardIconArea");
                    Check(!user.IsEffectivelyVisible && standard.IsEffectivelyVisible,
                        "The framework severity icon is visible without a supplied icon source.");
                }
            }
            var supplied = AppIcons.Source(AppIcon.BatteryCase, 16);
            bar.IconSource = supplied;
            bar.Severity = FAInfoBarSeverity.Informational;
            await Tick();
            Check(ReferenceEquals(bar.IconSource, supplied),
                "An explicitly supplied artwork source wins over the default severity icon.");
            var close = bar.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "CloseButton");
            Check(close.GetVisualDescendants().OfType<FASymbolIcon>().Any(icon => icon.IsEffectivelyVisible),
                "The close button keeps its framework symbol.");
            var closed = 0;
            bar.Closed += (_, _) => closed++;
            close.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Tick();
            Check(!bar.IsOpen && closed == 1, "The original close handler still closes once.");
        }
        finally { window.Close(); }
    }

    private static async Task TextCommands()
    {
        var text = new TextBox { Text = "Portable text commands", SelectionStart = 0, SelectionEnd = 8 };
        var window = new Window { Width = 440, Height = 180, Content = text };
        try
        {
            window.Show();
            await Tick();
            Check(text.ContextFlyout?.GetType() == typeof(FATextCommandBarFlyout),
                "The text box keeps FluentAvalonia's default command flyout.");
            var flyout = (FATextCommandBarFlyout)text.ContextFlyout!;
            text.Focus();
            flyout.ShowAt(text);
            await Tick();
            var buttons = flyout.PrimaryCommands.Concat(flyout.SecondaryCommands).OfType<FACommandBarButton>().ToArray();
            Check(buttons.Length > 0, "The base flyout still populates standard commands.");
            foreach (var button in buttons.Where(button => button.Command is FAStandardUICommand))
                Check(button.IconSource is FASymbolIconSource,
                    ((FAStandardUICommand)button.Command!).Kind + " retains its framework symbol source.");
            var selectAll = buttons.Select(button => button.Command).OfType<FAStandardUICommand>()
                .Single(command => command.Kind == FAStandardUICommandKind.SelectAll);
            selectAll.Execute(null);
            await Tick();
            Check(text.SelectionStart == 0 && text.SelectionEnd == text.Text!.Length,
                "The original Select All command still updates the original target text box.");
            flyout.Hide();
            var supplied = new Flyout { Content = "Custom context content" };
            text.ContextFlyout = supplied;
            Check(ReferenceEquals(text.ContextFlyout, supplied), "Explicit context flyouts are not replaced.");
        }
        finally { window.Close(); }
    }

    private static Control Part(Control owner, string name) =>
        owner.GetVisualDescendants().OfType<Control>().Single(control => control.Name == name);

    private static void Space(Window window)
    {
        window.KeyPress(Key.Space, RawInputModifiers.None, PhysicalKey.Space, " ");
        window.KeyRelease(Key.Space, RawInputModifiers.None, PhysicalKey.Space, " ");
        Dispatcher.UIThread.RunJobs();
    }

    private static async Task Tick()
    {
        Dispatcher.UIThread.RunJobs();
        await Task.Delay(20);
        Dispatcher.UIThread.RunJobs();
    }

    private static void Check(bool condition, string reason)
    {
        if (!condition) throw new Exception(reason);
    }
}
