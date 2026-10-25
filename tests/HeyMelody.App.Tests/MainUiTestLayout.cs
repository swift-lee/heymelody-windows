using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using HeyMelodyWindows.App;

/// <summary>Opens optional controls and waits for their real headless input geometry.</summary>
internal static class MainUiTestLayout
{
    internal static async Task ExpandSettingsAsync(MainView view)
    {
        // First realize the resource templates, then request expansion. Expansion
        // animates its clip, so processing layout jobs alone does not finish it.
        Dispatcher.UIThread.RunJobs();
        view.ExpandAllSettings();
        var timer = System.Diagnostics.Stopwatch.StartNew();
        while (timer.Elapsed < TimeSpan.FromMilliseconds(350))
        {
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(20);
        }
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }

    internal static async Task<Point> ClickPointAsync(Window window, Control control, double? x = null, bool requireFullVisibility = false)
    {
        var timer = System.Diagnostics.Stopwatch.StartNew();
        Point? point = null;
        object? hit = null;
        while (timer.Elapsed < TimeSpan.FromSeconds(3))
        {
            control.BringIntoView();
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
            point = control.TranslatePoint(new Point(x ?? control.Bounds.Width / 2, control.Bounds.Height / 2), window);
            if (point is { } current && control.Bounds.Width > 0 && control.Bounds.Height > 0)
            {
                hit = window.InputHitTest(current);
                var scroll = requireFullVisibility ? control.GetVisualAncestors().OfType<ScrollViewer>().FirstOrDefault() : null;
                var origin = scroll is not null ? control.TranslatePoint(default, scroll) : null;
                bool visible = scroll is null || origin is { } offset && offset.X >= -.6 && offset.Y >= -.6
                    && offset.X + control.Bounds.Width <= scroll.Viewport.Width + .6
                    && offset.Y + control.Bounds.Height <= scroll.Viewport.Height + .6;
                if (visible && hit is Visual visual && (ReferenceEquals(visual, control) || visual.GetVisualAncestors().Contains(control)))
                    return current;
            }
            await Task.Delay(20);
        }
        var clips = control.GetVisualAncestors().OfType<Control>().Where(ancestor => ancestor.ClipToBounds)
            .Select(ancestor => ancestor.GetType().Name + ":" + ancestor.Name + " " + ancestor.Bounds);
        throw new InvalidOperationException($"Target cannot receive mouse input after expansion/scrolling: {control.GetType().Name}, "
            + $"bounds={control.Bounds}, point={point}, hit={hit}, clipping ancestors={string.Join("; ", clips)}");
    }
}
