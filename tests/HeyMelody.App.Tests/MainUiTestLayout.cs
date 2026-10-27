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
        bool bringRequested = false;
        Point? previousPoint = null;
        Rect previousBounds = default;
        ScrollViewer? previousScroll = null;
        Vector previousOffset = default;
        Size previousViewport = default, previousExtent = default;
        int stableSamples = 0;
        static bool Near(double first, double second) => Math.Abs(first - second) <= .1;
        while (timer.Elapsed < TimeSpan.FromSeconds(3))
        {
            Dispatcher.UIThread.RunJobs();
            if (!bringRequested && control.Bounds.Width > 0 && control.Bounds.Height > 0)
            {
                // Request visibility once, then observe the smooth scroll instead of restarting it.
                control.BringIntoView();
                bringRequested = true;
            }
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
            point = control.TranslatePoint(new Point(x ?? control.Bounds.Width / 2, control.Bounds.Height / 2), window);
            if (bringRequested && point is { } current && control.Bounds.Width > 0 && control.Bounds.Height > 0)
            {
                hit = window.InputHitTest(current);
                var scroll = control.GetVisualAncestors().OfType<ScrollViewer>().FirstOrDefault();
                var origin = requireFullVisibility && scroll is not null ? control.TranslatePoint(default, scroll) : null;
                bool visible = !requireFullVisibility || scroll is null || origin is { } offset && offset.X >= -.6 && offset.Y >= -.6
                    && offset.X + control.Bounds.Width <= scroll.Viewport.Width + .6
                    && offset.Y + control.Bounds.Height <= scroll.Viewport.Height + .6;
                var scrollOffset = scroll?.Offset ?? default;
                var viewport = scroll?.Viewport ?? default;
                var extent = scroll?.Extent ?? default;
                bool unchanged = previousPoint is { } last && Near(current.X, last.X) && Near(current.Y, last.Y)
                    && previousBounds == control.Bounds && ReferenceEquals(previousScroll, scroll)
                    && Near(scrollOffset.X, previousOffset.X) && Near(scrollOffset.Y, previousOffset.Y)
                    && previousViewport == viewport && previousExtent == extent;
                if (visible && hit is Visual visual && (ReferenceEquals(visual, control) || visual.GetVisualAncestors().Contains(control)))
                {
                    stableSamples = unchanged ? stableSamples + 1 : 1;
                    if (stableSamples >= 3) return current;
                }
                else stableSamples = 0;
                previousPoint = current;
                previousBounds = control.Bounds;
                previousScroll = scroll;
                previousOffset = scrollOffset;
                previousViewport = viewport;
                previousExtent = extent;
            }
            else stableSamples = 0;
            await Task.Delay(20);
        }
        var clips = control.GetVisualAncestors().OfType<Control>().Where(ancestor => ancestor.ClipToBounds)
            .Select(ancestor => ancestor.GetType().Name + ":" + ancestor.Name + " " + ancestor.Bounds);
        throw new InvalidOperationException($"Target cannot receive mouse input after expansion/scrolling: {control.GetType().Name}, "
            + $"bounds={control.Bounds}, point={point}, hit={hit}, stableSamples={stableSamples}, scrollOffset={previousOffset}, "
            + $"clipping ancestors={string.Join("; ", clips)}");
    }
}
