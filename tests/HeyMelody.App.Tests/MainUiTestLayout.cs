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
        bool correctionAttempted = false;
        Vector correctionBefore = default, correctionTarget = default;
        Rect? targetInViewport = null;
        Point? previousPoint = null;
        Rect previousBounds = default;
        ScrollViewer? previousScroll = null;
        Vector previousOffset = default;
        Size previousViewport = default, previousExtent = default;
        int geometryStableSamples = 0, stableSamples = 0;
        static bool Near(double first, double second) => Math.Abs(first - second) <= .1;
        while (timer.Elapsed < TimeSpan.FromSeconds(3))
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
            point = control.TranslatePoint(new Point(x ?? control.Bounds.Width / 2, control.Bounds.Height / 2), window);
            if (point is { } current && control.Bounds.Width > 0 && control.Bounds.Height > 0)
            {
                hit = window.InputHitTest(current);
                var scroll = control.GetVisualAncestors().OfType<ScrollViewer>().FirstOrDefault();
                var origin = scroll is not null ? control.TranslatePoint(default, scroll) : null;
                targetInViewport = origin is { } position ? new Rect(position, control.Bounds.Size) : null;
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
                geometryStableSamples = unchanged ? geometryStableSamples + 1 : 1;
                if (!bringRequested && geometryStableSamples >= 3)
                {
                    // A request made while an expander is still changing geometry can keep an obsolete target.
                    control.BringIntoView();
                    bringRequested = true;
                    geometryStableSamples = stableSamples = 0;
                }
                else if (bringRequested && geometryStableSamples >= 3 && requireFullVisibility
                    && !correctionAttempted && scroll is not null && origin is { } target
                    && control.Bounds.Width <= viewport.Width + .6 && control.Bounds.Height <= viewport.Height + .6
                    && target.X >= -.6 && target.X + control.Bounds.Width <= viewport.Width + .6
                    && (target.Y < -.6 || target.Y + control.Bounds.Height > viewport.Height + .6))
                {
                    // Fixture positioning only: one bounded correction after scrolling and layout have settled.
                    correctionAttempted = true;
                    double residual = target.Y < 0 ? target.Y : target.Y + control.Bounds.Height - viewport.Height;
                    correctionBefore = scrollOffset;
                    correctionTarget = new Vector(scrollOffset.X,
                        Math.Clamp(scrollOffset.Y + residual, 0, Math.Max(0, extent.Height - viewport.Height)));
                    scroll.Offset = correctionTarget;
                    geometryStableSamples = stableSamples = 0;
                }
                else if (bringRequested && visible && hit is Visual visual
                    && (ReferenceEquals(visual, control) || visual.GetVisualAncestors().Contains(control)))
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
            else geometryStableSamples = stableSamples = 0;
            await Task.Delay(20);
        }
        static string Describe(Visual visual) => visual.GetType().Name
            + (visual is Control { Name: { Length: > 0 } name } ? ":" + name : "");
        var clips = control.GetVisualAncestors().OfType<Control>().Where(ancestor => ancestor.ClipToBounds)
            .Take(10).Select(ancestor => Describe(ancestor) + " " + ancestor.Bounds);
        var hitPath = hit is Visual hitVisual
            ? string.Join(" > ", new[] { Describe(hitVisual) }.Concat(hitVisual.GetVisualAncestors().Take(8).Select(Describe)))
            : hit?.ToString();
        throw new InvalidOperationException($"Target cannot receive mouse input after expansion/scrolling: {Describe(control)}, "
            + $"bounds={control.Bounds}, point={point}, targetInViewport={targetInViewport}, hit={hitPath}, "
            + $"stableSamples={stableSamples}, geometryStableSamples={geometryStableSamples}, bringRequested={bringRequested}, "
            + $"scrollOffset={previousOffset}, viewport={previousViewport}, extent={previousExtent}, "
            + $"correctionAttempted={correctionAttempted}, correctionBefore={correctionBefore}, correctionTarget={correctionTarget}, "
            + $"clipping ancestors={string.Join("; ", clips)}");
    }
}
