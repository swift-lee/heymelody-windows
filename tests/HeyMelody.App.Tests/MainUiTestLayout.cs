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

    internal static async Task CenterGroupAsync(Window window, Control first, Control last)
    {
        var scroll = first.GetVisualAncestors().OfType<ScrollViewer>().FirstOrDefault();
        if (scroll is null || !ReferenceEquals(scroll, last.GetVisualAncestors().OfType<ScrollViewer>().FirstOrDefault())
            || !ReferenceEquals(TopLevel.GetTopLevel(first), window) || !ReferenceEquals(TopLevel.GetTopLevel(last), window))
            throw new InvalidOperationException("A settings group requires one attached window and a shared nearest viewport.");

        var timer = System.Diagnostics.Stopwatch.StartNew();
        bool bringRequested = false, centerRequested = false;
        int stableSamples = 0;
        Rect? firstRectangle = null, lastRectangle = null, previousFirst = null, previousLast = null;
        Rect previousFirstBounds = default, previousLastBounds = default;
        Vector previousOffset = default, centerBefore = default, centerTarget = default;
        Size previousViewport = default, previousExtent = default;
        static bool Near(double firstValue, double secondValue) => Math.Abs(firstValue - secondValue) <= .1;
        static bool Same(Rect current, Rect previous) => Near(current.X, previous.X) && Near(current.Y, previous.Y)
            && current.Size == previous.Size;
        static bool FullyVisible(Rect rectangle, Size viewport) => rectangle.Left >= -.6 && rectangle.Top >= -.6
            && rectangle.Right <= viewport.Width + .6 && rectangle.Bottom <= viewport.Height + .6;
        while (timer.Elapsed < TimeSpan.FromSeconds(3))
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
            var firstOrigin = first.TranslatePoint(default, scroll);
            var lastOrigin = last.TranslatePoint(default, scroll);
            if (firstOrigin is { } firstPosition && lastOrigin is { } lastPosition
                && first.Bounds.Width > 0 && first.Bounds.Height > 0 && last.Bounds.Width > 0 && last.Bounds.Height > 0)
            {
                var firstRect = new Rect(firstPosition, first.Bounds.Size);
                var lastRect = new Rect(lastPosition, last.Bounds.Size);
                firstRectangle = firstRect;
                lastRectangle = lastRect;
                var offset = scroll.Offset;
                var viewport = scroll.Viewport;
                var extent = scroll.Extent;
                bool unchanged = previousFirst is { } oldFirst && previousLast is { } oldLast
                    && Same(firstRect, oldFirst) && Same(lastRect, oldLast)
                    && first.Bounds == previousFirstBounds && last.Bounds == previousLastBounds
                    && Near(offset.X, previousOffset.X) && Near(offset.Y, previousOffset.Y)
                    && viewport == previousViewport && extent == previousExtent;
                stableSamples = unchanged ? stableSamples + 1 : 1;
                if (stableSamples >= 3)
                {
                    if (!bringRequested)
                    {
                        last.BringIntoView();
                        bringRequested = true;
                        stableSamples = 0;
                    }
                    else if (!centerRequested)
                    {
                        double groupHeight = lastRect.Bottom - firstRect.Top;
                        if (groupHeight > 0 && groupHeight <= viewport.Height + .6
                            && firstRect.Left >= -.6 && firstRect.Right <= viewport.Width + .6
                            && lastRect.Left >= -.6 && lastRect.Right <= viewport.Width + .6)
                        {
                            // Preserve the layout fixture's centering formula, but only after the old bring has settled.
                            double desiredOffset = offset.Y + firstRect.Top - Math.Max(0, (viewport.Height - groupHeight) / 2);
                            centerBefore = offset;
                            centerTarget = new Vector(offset.X,
                                Math.Clamp(desiredOffset, 0, Math.Max(0, extent.Height - viewport.Height)));
                            scroll.Offset = centerTarget;
                            centerRequested = true;
                            stableSamples = 0;
                        }
                    }
                    else if (FullyVisible(firstRect, viewport) && FullyVisible(lastRect, viewport)) return;
                }
                previousFirst = firstRect;
                previousLast = lastRect;
                previousFirstBounds = first.Bounds;
                previousLastBounds = last.Bounds;
                previousOffset = offset;
                previousViewport = viewport;
                previousExtent = extent;
            }
            else stableSamples = 0;
            await Task.Delay(20);
        }
        throw new InvalidOperationException($"Settings group did not settle fully inside its viewport: "
            + $"first={first.Name}, firstRectangle={firstRectangle}, firstBounds={first.Bounds}, "
            + $"last={last.Name}, lastRectangle={lastRectangle}, lastBounds={last.Bounds}, "
            + $"viewport={previousViewport}, extent={previousExtent}, offset={previousOffset}, stableSamples={stableSamples}, "
            + $"bringRequested={bringRequested}, centerRequested={centerRequested}, centerBefore={centerBefore}, centerTarget={centerTarget}");
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
