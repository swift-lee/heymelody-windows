using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Presenters;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Styling;
using Avalonia.VisualTree;
using FluentAvalonia.Core;
using FluentAvalonia.UI.Controls;
using SmoothScroll.Avalonia.Controls;

namespace HeyMelodyWindows.Ui;

/// <summary>Opt-in smooth wheel scrolling for the non-virtualized page viewers shared by the GUI entry points.</summary>
internal static class SmoothScrolling
{
    private static readonly Lazy<ControlTheme> Theme = new(() =>
        (ControlTheme)new ScrollViewerSmoothTheme().Resources[typeof(ScrollViewer)]!);

    /// <returns>Whether the library is active; the device strip keeps its immediate wheel fallback otherwise.</returns>
    internal static bool Attach(ScrollViewer viewer, bool horizontal = false)
    {
        // Keep the standard presenter when animations are disabled at application startup.
        if (!FAUISettings.AreAnimationsEnabled()) return false;

        ScrollPresenter? currentPresenter = null;
        viewer.TemplateApplied += (_, e) =>
        {
            if (currentPresenter is not null)
            {
                currentPresenter.RemoveHandler(InputElement.PointerPressedEvent, AllowHeaderTouchPanning);
                currentPresenter.RemoveHandler(InputElement.PointerMovedEvent, AllowHeaderTouchPanning);
                currentPresenter.RemoveHandler(InputElement.PointerReleasedEvent, AllowHeaderTouchPanning);
            }
            currentPresenter = e.NameScope.Find<ScrollPresenter>("PART_ContentPresenter");
            // Header buttons handle press/release before they reach the library's input source.
            // Let that source observe touch input too; it takes capture only after its drag threshold.
            currentPresenter?.AddHandler(InputElement.PointerPressedEvent, AllowHeaderTouchPanning, RoutingStrategies.Bubble, true);
            currentPresenter?.AddHandler(InputElement.PointerMovedEvent, AllowHeaderTouchPanning, RoutingStrategies.Bubble, true);
            currentPresenter?.AddHandler(InputElement.PointerReleasedEvent, AllowHeaderTouchPanning, RoutingStrategies.Bubble, true);
            if (horizontal && currentPresenter is { } presenter)
            {
                presenter.ContentOrientation = ScrollContentOrientation.Horizontal;
                presenter.HorizontalAlignment = HorizontalAlignment.Center;
                presenter.VerticalScrollMode = ScrollMode.Disabled;
                presenter.HorizontalScrollMode = ScrollMode.Auto;
            }
        };

        // 12.0.0.14 arranges before coercing Offset after a geometry change. Re-arrange once
        // with the corrected offset; stable geometry and nested viewers do not request another pass.
        viewer.ScrollChanged += (_, e) =>
        {
            if (ReferenceEquals(e.Source, viewer) && (e.ExtentDelta != default || e.ViewportDelta != default)
                && currentPresenter is { } presenter && ReferenceEquals(presenter.TemplatedParent, viewer)
                && TopLevel.GetTopLevel(presenter) is not null)
                presenter.InvalidateArrange();
        };
        viewer.Theme = Theme.Value;
        return true;
    }

    private static void AllowHeaderTouchPanning(object? sender, PointerEventArgs e)
    {
        if (e.Pointer.Type is not (PointerType.Touch or PointerType.Pen)) return;
        if (e.Source is not Visual source || source.FindAncestorOfType<Button>(includeSelf: true) is not ToggleButton
            { Name: "ToggleButton", TemplatedParent: Expander { TemplatedParent: FASettingsExpander } } header) return;
        if (!ReferenceEquals(header.FindAncestorOfType<ScrollViewer>(), (sender as ScrollPresenter)?.TemplatedParent)) return;

        // Footer editors keep their own input handling. Once a header drag starts, the
        // library takes capture from the touched visual and cancels the button's pressed state.
        for (Visual? visual = source; visual is not null && !ReferenceEquals(visual, header); visual = visual.GetVisualParent())
            if (visual is ContentPresenter { Name: "FooterPresenter", TemplatedParent: FASettingsExpanderItem }) return;

        e.Handled = false;
    }
}
