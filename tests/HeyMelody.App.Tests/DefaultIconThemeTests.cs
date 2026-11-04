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

/// <summary>Default control pictograms use the same portable paths without changing their behavior.</summary>
internal static class DefaultIconThemeTests
{
    internal static int Run(HeadlessUnitTestSession session)
    {
        Run("light default control icons keep selection and expansion behavior", () => Controls(ThemeVariant.Light));
        Run("dark default control icons keep selection and expansion behavior", () => Controls(ThemeVariant.Dark));
        Run("info bar severity and close actions use Fluent paths", InfoBars);
        Run("text flyout keeps standard commands while replacing their symbol sources", TextCommands);
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
            var arrow = Icon(combo, "DropDownGlyph");
            Vector(arrow.IconSource, 12, "ComboBox arrow");
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

            var tick = Icon(check, "CheckGlyph");
            Check(tick.Opacity == 0, "An unchecked box does not display its pictogram.");
            Check(check.Focus(), "The original check box remains keyboard-focusable.");
            Space(window);
            await Tick();
            Check(check.IsChecked == true && tick.Opacity == 1, "Space checks the box and shows the Fluent checkmark.");
            Vector(tick.IconSource, 12, "Checked mark");
            var checkedSource = tick.IconSource;
            Space(window);
            await Tick();
            Check(check.IsChecked is null && tick.Opacity == 1, "The next Space preserves the indeterminate state.");
            Vector(tick.IconSource, 12, "Indeterminate mark");
            Check(!ReferenceEquals(checkedSource, tick.IconSource), "Indeterminate selects a different icon source.");
            Space(window);
            await Tick();
            Check(check.IsChecked == false && tick.Opacity == 0, "The next Space restores the unchecked state.");
            check.IsEnabled = false;
            Space(window);
            Check(check.IsChecked == false, "A disabled check box does not change.");

            var chevron = Icon(expander, "ExpandCollapseChevron");
            Vector(chevron.IconSource, 18, "Settings expander arrow");
            var header = expander.GetVisualDescendants().OfType<ToggleButton>()
                .Single(button => button.Name == "ToggleButton");
            Check(header.Focus(), "The original settings header remains keyboard-focusable.");
            Space(window);
            await Tick();
            Check(expander.IsExpanded, "Space on the header still expands the settings content.");
            Check(chevron.RenderTransform?.Value.M11 < 0 && chevron.RenderTransform.Value.M22 < 0,
                "The same Fluent chevron rotates for the expanded state.");
            Space(window);
            await Tick();
            Check(!expander.IsExpanded, "Space collapses the same settings content.");
            NoVisibleFontIcons(panel);
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
            FAIconSource? previous = null;
            foreach (var theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
            {
                window.RequestedThemeVariant = theme;
                foreach (var severity in Enum.GetValues<FAInfoBarSeverity>())
                {
                    bar.Severity = severity;
                    await Tick();
                    Vector(bar.IconSource, 16, severity + " info bar");
                    if (previous is not null)
                        Check(!ReferenceEquals(previous, bar.IconSource), "A severity change gets its own icon source.");
                    previous = bar.IconSource;
                    var user = bar.GetVisualDescendants().OfType<Viewbox>().Single(box => box.Name == "UserIconBox");
                    var standard = bar.GetVisualDescendants().OfType<Panel>().Single(panel => panel.Name == "StandardIconArea");
                    Check(user.IsEffectivelyVisible && !standard.IsEffectivelyVisible,
                        "The supplied path replaces, rather than overlays, the standard symbol icon.");
                    NoVisibleFontIcons(bar);
                }
            }
            var supplied = AppIcons.Source(AppIcon.BatteryCase, 16);
            bar.IconSource = supplied;
            bar.Severity = FAInfoBarSeverity.Informational;
            await Tick();
            Check(ReferenceEquals(bar.IconSource, supplied),
                "An explicitly supplied artwork source wins over the default severity icon.");
            var close = bar.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "CloseButton");
            Vector(close.GetVisualDescendants().OfType<FAIconSourceElement>().Single().IconSource, 16, "InfoBar close");
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
            Check(text.ContextFlyout is FluentTextCommandBarFlyout, "Default text context flyouts use the icon-only subclass.");
            var flyout = (FluentTextCommandBarFlyout)text.ContextFlyout!;
            text.Focus();
            flyout.ShowAt(text);
            await Tick();
            var buttons = flyout.PrimaryCommands.Concat(flyout.SecondaryCommands).OfType<FACommandBarButton>().ToArray();
            Check(buttons.Length > 0, "The base flyout still populates standard commands.");
            var commands = buttons.Select(button => button.Command).ToArray();
            foreach (var button in buttons.Where(button => button.Command is FAStandardUICommand))
                Vector(button.IconSource, 16, ((FAStandardUICommand)button.Command!).Kind + " command");
            flyout.ReplaceIcons();
            Check(buttons.Select(button => button.Command).SequenceEqual(commands),
                "Replacing icons never replaces standard command instances or their handlers.");
            var selectAll = buttons.Select(button => button.Command).OfType<FAStandardUICommand>()
                .Single(command => command.Kind == FAStandardUICommandKind.SelectAll);
            selectAll.Execute(null);
            await Tick();
            Check(text.SelectionStart == 0 && text.SelectionEnd == text.Text!.Length,
                "The original Select All command still updates the original target text box.");
            if (flyout.CommandPresenter is { } presenter) NoVisibleFontIcons(presenter);
            flyout.Hide();
            var supplied = new Flyout { Content = "Custom context content" };
            text.ContextFlyout = supplied;
            Check(ReferenceEquals(text.ContextFlyout, supplied), "Explicit context flyouts are not replaced.");
        }
        finally { window.Close(); }
    }

    private static FAIconSourceElement Icon(Control owner, string name) =>
        owner.GetVisualDescendants().OfType<FAIconSourceElement>().Single(icon => icon.Name == name);

    private static void Vector(FAIconSource? source, double size, string reason)
    {
        Check(source is FAPathIconSource { Data: not null }, reason + " uses a vector source, not a font.");
        var bounds = ((FAPathIconSource)source!).Data.Bounds;
        Check(Math.Abs(bounds.X) < 0.001 && Math.Abs(bounds.Y) < 0.001
            && Math.Abs(bounds.Width - size) < 0.001 && Math.Abs(bounds.Height - size) < 0.001,
            reason + " preserves the full SVG viewport at its requested size: " + bounds);
    }

    private static void NoVisibleFontIcons(Control owner) =>
        Check(!owner.GetVisualDescendants().Any(control =>
            (control is FAFontIcon or FASymbolIcon) && control.IsEffectivelyVisible && control.Opacity > 0),
            "Default control pictograms do not render FluentAvalonia's built-in font glyphs.");

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
