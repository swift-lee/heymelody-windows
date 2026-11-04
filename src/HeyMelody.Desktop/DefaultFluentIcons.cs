using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using FluentAvalonia.UI.Controls;
using FluentAvalonia.UI.Input;

namespace HeyMelodyWindows.App;

/// <summary>Creates a fresh themed icon when an info bar changes severity.</summary>
public sealed class InfoBarFluentIconConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is FAInfoBarSeverity severity ? AppIcons.Source(severity switch
        {
            FAInfoBarSeverity.Error => AppIcon.Error,
            FAInfoBarSeverity.Warning => AppIcon.Warning,
            FAInfoBarSeverity.Success => AppIcon.Success,
            _ => AppIcon.Info
        }, 16) : AvaloniaProperty.UnsetValue;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Retains FluentAvalonia's text commands and only replaces their built-in symbol icons.</summary>
public sealed class FluentTextCommandBarFlyout : FATextCommandBarFlyout
{
    public FluentTextCommandBarFlyout()
    {
        // The base constructor registers its command-population handler first.
        Opening += (_, _) => ReplaceIcons();
    }

    internal Control? CommandPresenter => _commandBar;

    internal void ReplaceIcons()
    {
        foreach (var button in PrimaryCommands.Concat(SecondaryCommands).OfType<FACommandBarButton>())
        {
            if (button.Command is not FAStandardUICommand command) continue;
            var icon = command.Kind switch
            {
                FAStandardUICommandKind.Cut => AppIcon.Cut,
                FAStandardUICommandKind.Copy => AppIcon.Copy,
                FAStandardUICommandKind.Paste => AppIcon.Paste,
                FAStandardUICommandKind.Undo => AppIcon.Undo,
                FAStandardUICommandKind.Redo => AppIcon.Redo,
                FAStandardUICommandKind.SelectAll => AppIcon.SelectAll,
                _ => (AppIcon?)null
            };
            if (icon is { } role) button.IconSource = AppIcons.Source(role, 16);
        }
    }
}
