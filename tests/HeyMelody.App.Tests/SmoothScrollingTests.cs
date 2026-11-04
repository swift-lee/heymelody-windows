using System.Diagnostics;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Presenters;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using FluentAvalonia.Core;
using HeyMelodyWindows.App;
using HeyMelodyWindows.Artwork;
using HeyMelodyWindows.Ui;
using SmoothScroll.Avalonia.Controls;

internal static class SmoothScrollingTests
{
    private static readonly List<object> Checks = [];
    private static readonly List<string> DispatcherErrors = [];

    public static int Run()
    {
        string output = Path.GetFullPath(Path.Combine("artifacts", "smooth-scroll-20261024",
            "focused-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..6]));
        Directory.CreateDirectory(output);
        try
        {
            using var session = HeadlessUnitTestSession.StartNew(typeof(PreviewBootstrap));
            session.Dispatch<bool>(async () =>
            {
                bool animationsBefore = FAUISettings.AreAnimationsEnabled();
                void OnDispatcherError(object? sender, DispatcherUnhandledExceptionEventArgs args)
                { DispatcherErrors.Add(args.Exception.ToString()); args.Handled = true; }
                Dispatcher.UIThread.UnhandledException += OnDispatcherError;
                try
                {
                    FAUISettings.SetAnimationsEnabledAtAppLevel(true);
                    await ScopedThemeAsync();
                    await WheelAndProgrammaticAsync();
                    await HorizontalAsync();
                    await NestedChainingAsync();
                    Checks.AddRange(await SmoothScrollingInputTests.RunAsync());
                    await VerticalLayoutChangesAsync();
                    await LifetimeAsync();
                    await ReducedMotionAsync();
                    Check(DispatcherErrors.Count == 0, "The focused runner finishes without deferred dispatcher errors.");
                    return true;
                }
                finally
                {
                    FAUISettings.SetAnimationsEnabledAtAppLevel(animationsBefore);
                    Dispatcher.UIThread.UnhandledException -= OnDispatcherError;
                }
            }, CancellationToken.None).GetAwaiter().GetResult();
            var receipt = new { success = true, passed = Checks.Count, nativeBluetoothCalls = 0, screenshots = 0,
                scope = "production opt-in helper with headless Skia composition and routed mouse wheel input; no device or hardware input",
                checks = Checks, dispatcherErrors = DispatcherErrors };
            File.WriteAllText(Path.Combine(output, "receipt.json"), JsonSerializer.Serialize(receipt, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine(JsonSerializer.Serialize(new { receipt.success, receipt.passed, receipt.nativeBluetoothCalls, receipt.screenshots, output }));
            return 0;
        }
        catch (Exception error)
        {
            var receipt = new { success = false, passed = Checks.Count, failure = error.ToString(), checks = Checks, dispatcherErrors = DispatcherErrors };
            File.WriteAllText(Path.Combine(output, "receipt.json"), JsonSerializer.Serialize(receipt, new JsonSerializerOptions { WriteIndented = true }));
            Console.Error.WriteLine(error); Console.WriteLine(output); return 1;
        }
    }

    private static async Task ScopedThemeAsync()
    {
        var nested = Viewer(Rows(20)); nested.Height = 100;
        var content = Rows(20); content.Children.Insert(0, nested);
        var outer = Viewer(content);
        var originalNestedTheme = nested.Theme;
        Check(SmoothScrolling.Attach(outer), "The explicit page helper accepts enabled animations.");
        var window = WindowFor(outer);
        try
        {
            window.Show(); await PumpAsync(window, 100);
            Check(Presenter(outer) is not null, "Only the opted-in page receives the library ScrollPresenter.");
            Check(Presenter(nested) is null && HasStandardPresenter(nested) && ReferenceEquals(nested.Theme, originalNestedTheme),
                "An unmarked nested ScrollViewer retains the FluentAvalonia presenter and its theme.");
        }
        finally { window.Close(); }
    }

    private static async Task WheelAndProgrammaticAsync()
    {
        var content = Rows(100); var target = content.Children[70];
        var scroll = Viewer(content); SmoothScrolling.Attach(scroll);
        long cleanupTime = 1_000;
        int cleanupRequests = 0;
        using var cleanup = ArtworkCleanupTriggers.BeginTestScope(() => cleanupTime, () => cleanupRequests++);
        ArtworkCleanupTriggers.AttachScroll(scroll);
        var window = WindowFor(scroll);
        try
        {
            window.Show(); await PumpAsync(window, 100);
            Wheel(window, scroll, -1);
            await PumpAsync(window, 30);
            Check(ArtworkCleanupTriggers.HasPendingCleanup && cleanupRequests == 0,
                "Smooth offsets schedule delayed cleanup without changing artwork animation cadence.");
            var single = await SettleAsync(window, scroll);
            double singleFinal = scroll.Offset.Y;
            Check(singleFinal > 1 && single.Any(value => value.Y > .1 && value.Y < singleFinal - .1)
                && single.Select(value => Math.Round(value.Y, 2)).Distinct().Count() >= 3,
                "One wheel notch produces several intermediate offsets before it settles.", new { singleFinal });
            cleanupTime += (long)ArtworkCleanupTriggers.ScrollDelay.TotalMilliseconds - 1;
            ArtworkCleanupTriggers.FlushPendingForTests();
            Check(cleanupRequests == 0, "Scroll cleanup waits for its quiet-period deadline.");
            cleanupTime++; ArtworkCleanupTriggers.FlushPendingForTests();
            Check(cleanupRequests == 1 && !ArtworkCleanupTriggers.HasPendingCleanup,
                "Settled scrolling requests cleanup once, independently of artwork scheduling.");

            scroll.Offset = default; await PumpAsync(window, 60);
            for (int index = 0; index < 3; index++) { Wheel(window, scroll, -1); await PumpAsync(window, 12); }
            await SettleAsync(window, scroll);
            double tripleFinal = scroll.Offset.Y;
            Check(tripleFinal > singleFinal * 2.2,
                "Rapid wheel input accumulates motion instead of replacing earlier input; physical acceleration is allowed.",
                new { singleFinal, tripleFinal });

            scroll.Offset = new Vector(0, 700); await PumpAsync(window, 50);
            Wheel(window, scroll, -1); await PumpAsync(window, 40);
            for (int index = 0; index < 3; index++) { Wheel(window, scroll, 1); await PumpAsync(window, 12); }
            var reversed = await SettleAsync(window, scroll);
            Check(scroll.Offset.Y < 700 && reversed.Zip(reversed.Skip(1)).Any(pair => pair.Second.Y < pair.First.Y - .1),
                "Opposite wheel input reverses an active scroll and settles in the requested direction.");

            Wheel(window, scroll, -1); await PumpAsync(window, 20);
            scroll.Offset = new Vector(0, 900);
            double immediateAssignment = scroll.Offset.Y;
            Dispatcher.UIThread.RunJobs();
            Check(Math.Abs(scroll.Offset.Y - 900) < .01, "A direct Offset assignment remains immediate while wheel motion is active.",
                new { immediateAssignment, afterQueuedJobs = scroll.Offset.Y });
            var assigned = await SettleAsync(window, scroll);
            Check(assigned.All(value => Math.Abs(value.Y - 900) < .01), "An old wheel target cannot pull back a direct programmatic position.");

            Wheel(window, scroll, -1); await PumpAsync(window, 20);
            target.BringIntoView(); Dispatcher.UIThread.RunJobs();
            await SettleAsync(window, scroll);
            double top = target.TranslatePoint(default, scroll)!.Value.Y;
            double positioned = scroll.Offset.Y;
            await PumpAsync(window, 200);
            Check(top >= -.5 && top + target.Bounds.Height <= scroll.Viewport.Height + .5
                && Math.Abs(scroll.Offset.Y - positioned) < .1,
                "BringIntoView during wheel motion reveals the target and keeps its final position.");

            scroll.Offset = new Vector(0, 350); await PumpAsync(window, 60);
            Wheel(window, scroll, -1); await PumpAsync(window, 20);
            var bar = scroll.GetVisualDescendants().OfType<ScrollBar>().Single(control => control.Name == "PART_VerticalScrollBar");
            var thumb = bar.GetVisualDescendants().OfType<Thumb>().Single();
            var point = thumb.TranslatePoint(new Point(thumb.Bounds.Width / 2, thumb.Bounds.Height / 2), window)!.Value;
            double beforeDrag = scroll.Offset.Y;
            window.MouseMove(point); window.MouseDown(point, MouseButton.Left);
            window.MouseMove(point + new Vector(0, 45)); window.MouseUp(point + new Vector(0, 45), MouseButton.Left);
            Dispatcher.UIThread.RunJobs();
            double dragged = scroll.Offset.Y;
            await PumpAsync(window, 250);
            Check(dragged > beforeDrag + 1 && Math.Abs(scroll.Offset.Y - dragged) < .1,
                "Dragging the scrollbar interrupts wheel motion without a later pullback.");
        }
        finally { window.Close(); }
    }

    private static async Task HorizontalAsync()
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal };
        for (int index = 0; index < 30; index++) content.Children.Add(new TextBlock { Text = $"Device {index}", Width = 100, Height = 40 });
        var scroll = new ScrollViewer { Content = content, Background = Brushes.Transparent,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            AllowAutoHide = false };
        Check(SmoothScrolling.Attach(scroll, horizontal: true), "The horizontal page helper accepts enabled animations.");
        var window = WindowFor(scroll, height: 150);
        try
        {
            window.Show(); await PumpAsync(window, 100);
            Wheel(window, scroll, -1, y: 20);
            var samples = await SettleAsync(window, scroll);
            double final = scroll.Offset.X;
            Check(final > 1 && samples.Any(value => value.X > .1 && value.X < final - .1)
                && samples.All(value => Math.Abs(value.Y) < .01),
                "The library maps a vertical mouse wheel onto horizontal smooth motion without moving Y.");
        }
        finally { window.Close(); }
    }

    private static async Task NestedChainingAsync()
    {
        var inner = Viewer(Rows(40)); inner.Height = 120;
        var content = Rows(30); content.Children.Insert(0, inner);
        var outer = Viewer(content); SmoothScrolling.Attach(outer);
        var window = WindowFor(outer);
        try
        {
            window.Show(); await PumpAsync(window, 100);
            Wheel(window, inner, -1); await PumpAsync(window, 250);
            Check(inner.Offset.Y > 0 && Math.Abs(outer.Offset.Y) < .01,
                "A nested FluentAvalonia viewer consumes its own wheel input before the smooth outer viewer.");

            inner.Offset = new Vector(0, inner.Extent.Height - inner.Viewport.Height); await PumpAsync(window, 60);
            double edge = inner.Offset.Y;
            Wheel(window, inner, -1); await SettleAsync(window, outer);
            Check(outer.Offset.Y > 1 && Math.Abs(inner.Offset.Y - edge) < .1,
                "At its lower edge the nested viewer chains wheel input to the smooth outer page.");

            outer.Offset = default; inner.IsScrollChainingEnabled = false; await PumpAsync(window, 60);
            Wheel(window, inner, -1); await PumpAsync(window, 350);
            Check(Math.Abs(outer.Offset.Y) < .01 && Math.Abs(inner.Offset.Y - edge) < .1,
                "Explicitly disabling nested chaining prevents movement in the outer page at the boundary.");
        }
        finally { window.Close(); }
    }

    private static async Task VerticalLayoutChangesAsync()
    {
        foreach (bool shrinkContent in new[] { true, false })
        {
            string scenario = shrinkContent ? "content shrink" : "viewport growth";
            var content = Rows(shrinkContent ? 100 : 20); var first = content.Children[0];
            var scroll = Viewer(content); SmoothScrolling.Attach(scroll);
            int dimensions = 0, layouts = 0;
            scroll.LayoutUpdated += (_, _) => layouts++;
            scroll.ScrollChanged += (_, args) =>
            { if (args.ExtentDelta != default || args.ViewportDelta != default) dimensions++; };
            var window = WindowFor(scroll, shrinkContent ? 360 : 240);
            try
            {
                window.Show(); await PumpAsync(window, 100);
                scroll.Offset = new Vector(0, scroll.Extent.Height - scroll.Viewport.Height);
                await PumpAsync(window, 100);
                double before = scroll.Offset.Y;
                Check(before >= 100, "Vertical " + scenario + " starts at a nonzero end offset.");
                if (shrinkContent)
                { for (int index = content.Children.Count - 1; index >= 6; index--) content.Children.RemoveAt(index); }
                else window.Height = 800;
                await PumpAsync(window, 150);
                double top = first.TranslatePoint(default, scroll)!.Value.Y;
                Check(Math.Abs(scroll.Offset.Y) < .01 && Math.Abs(top) < .5,
                    "Vertical " + scenario + " coerces the offset and rearranges the first row into the visible viewport.",
                    new { before, after = scroll.Offset.Y, firstRowTop = top, contentY = content.Bounds.Y });
                int quietDimensions = dimensions, quietLayouts = layouts;
                await PumpAsync(window, 250);
                Check(Math.Abs(first.TranslatePoint(default, scroll)!.Value.Y - top) < .01
                    && Math.Abs(scroll.Offset.Y) < .01 && dimensions == quietDimensions,
                    "Vertical " + scenario + " remains stable without a repeated geometry-change loop.",
                    new { quietDimensionChanges = dimensions - quietDimensions, quietLayoutPasses = layouts - quietLayouts });
            }
            finally { window.Close(); }
        }
    }

    private static async Task LifetimeAsync()
    {
        var scroll = Viewer(Rows(80)); SmoothScrolling.Attach(scroll);
        var holder = new Border { Child = scroll }; var window = WindowFor(holder);
        try
        {
            window.Show(); await PumpAsync(window, 100);
            Wheel(window, scroll, -1); await PumpAsync(window, 20);
            holder.Child = null; await PumpAsync(window, 200);
            holder.Child = scroll; await PumpAsync(window, 120);
            double before = scroll.Offset.Y;
            Wheel(window, scroll, -1); await SettleAsync(window, scroll);
            Check(scroll.Offset.Y > before + 1 && DispatcherErrors.Count == 0,
                "A page detached during motion can be reattached and scroll again without deferred exceptions.");
            Wheel(window, scroll, -1); await PumpAsync(window, 20);
            window.Close(); await PumpAsync(window, 300);
            Check(!window.IsVisible && DispatcherErrors.Count == 0, "Closing during wheel motion leaves no delayed dispatcher exception.");
        }
        finally { if (window.IsVisible) window.Close(); }
    }

    private static async Task ReducedMotionAsync()
    {
        FAUISettings.SetAnimationsEnabledAtAppLevel(false);
        var scroll = Viewer(Rows(40)); var original = scroll.Theme;
        try
        {
            Check(!SmoothScrolling.Attach(scroll), "The opt-in helper declines smooth scrolling when application animations are disabled.");
            var window = WindowFor(scroll);
            try
            {
                window.Show(); await PumpAsync(window, 100);
                Check(ReferenceEquals(scroll.Theme, original) && Presenter(scroll) is null && HasStandardPresenter(scroll),
                    "Reduced motion retains the original FluentAvalonia theme and presenter.");
                Wheel(window, scroll, -1);
                double immediate = scroll.Offset.Y; await PumpAsync(window, 200);
                Check(immediate > 0 && Math.Abs(scroll.Offset.Y - immediate) < .01,
                    "Reduced motion uses immediate wheel scrolling rather than a delayed animation.");
            }
            finally { window.Close(); }
        }
        finally { FAUISettings.SetAnimationsEnabledAtAppLevel(true); }
    }

    private static ScrollViewer Viewer(Control content) => new()
    {
        Content = content, Background = Brushes.Transparent, AllowAutoHide = false,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
    };
    private static StackPanel Rows(int count)
    {
        var panel = new StackPanel();
        for (int index = 0; index < count; index++) panel.Children.Add(new TextBlock { Text = $"Row {index:00}", Height = 28, FontSize = 15 });
        return panel;
    }
    private static Window WindowFor(Control content, double height = 360) => new()
    { Content = content, Width = 420, Height = height, WindowDecorations = WindowDecorations.None };
    private static ScrollPresenter? Presenter(ScrollViewer viewer) => viewer.GetVisualDescendants().OfType<ScrollPresenter>()
        .SingleOrDefault(control => ReferenceEquals(control.TemplatedParent, viewer));
    private static bool HasStandardPresenter(ScrollViewer viewer) => viewer.GetVisualDescendants().OfType<ScrollContentPresenter>()
        .Any(control => ReferenceEquals(control.TemplatedParent, viewer));
    private static void Wheel(Window window, ScrollViewer viewer, double delta, double y = 50)
    {
        var point = viewer.TranslatePoint(new Point(80, y), window)!.Value;
        window.MouseWheel(point, new Vector(0, delta), RawInputModifiers.None); Dispatcher.UIThread.RunJobs();
    }
    private static async Task<List<Vector>> SettleAsync(Window window, ScrollViewer viewer)
    {
        var samples = new List<Vector> { viewer.Offset }; var clock = Stopwatch.StartNew(); int stable = 0;
        while (clock.Elapsed < TimeSpan.FromSeconds(3))
        {
            await PumpAsync(window, 20); var value = viewer.Offset;
            stable = (value - samples[^1]).Length < .01 ? stable + 1 : 0; samples.Add(value);
            if (clock.ElapsedMilliseconds >= 180 && stable >= 8) return samples;
        }
        throw new TimeoutException("Smooth scrolling did not settle within three seconds.");
    }
    private static async Task PumpAsync(Window window, int duration)
    {
        var clock = Stopwatch.StartNew();
        do
        {
            Dispatcher.UIThread.RunJobs();
            if (DispatcherErrors.Count != 0) throw new InvalidOperationException("Deferred scrolling error: " + DispatcherErrors[0]);
            if (window.IsVisible) { using var pixels = window.CaptureRenderedFrame(); }
            await Task.Delay(8);
        } while (clock.ElapsedMilliseconds < duration);
        Dispatcher.UIThread.RunJobs();
    }
    private static void Check(bool condition, string message, object? details = null)
    {
        if (!condition) throw new InvalidOperationException(details is null ? message : message + " " + JsonSerializer.Serialize(details));
        Checks.Add(new { message, details }); Console.WriteLine("PASS " + message);
    }
}
