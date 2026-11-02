using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using HeyMelodyWindows.Ui;

internal static class MainUiTestLayoutTests
{
    internal static int Run(HeadlessUnitTestSession session)
    {
        int passed = 0;
        session.Dispatch<bool>(async () =>
        {
            await NormalTargetAsync();
            Pass("layout helper preserves real mouse input for an already visible target");
            await BottomClippingAsync(deferredGrowth: false);
            Pass("layout helper corrects a settled bottom clipping residual without another bring request");
            await BottomClippingAsync(deferredGrowth: true);
            Pass("layout helper positions against deferred extent growth after its single bring request");
            await RejectedTargetAsync("OverlayTarget", overlay: true);
            Pass("layout helper cannot accept an overlay hit instead of its target");
            await RejectedTargetAsync("OversizedTarget", oversized: true);
            Pass("layout helper rejects a target taller than its viewport without correcting it");
            await RejectedTargetAsync("HorizontalTarget", horizontalClipping: true);
            Pass("layout helper cannot hide horizontal clipping with a vertical correction");
            return true;
        }, CancellationToken.None).GetAwaiter().GetResult();
        return passed;

        void Pass(string message) { passed++; Console.WriteLine("PASS " + message); }
    }

    private static async Task NormalTargetAsync()
    {
        using var fixture = new Fixture("VisibleTarget");
        int requests = fixture.ObserveBringRequests(handled: false);
        int clicks = 0;
        fixture.Target.Click += (_, _) => clicks++;
        var point = await MainUiTestLayout.ClickPointAsync(fixture.Window, fixture.Target, requireFullVisibility: true);
        Check(fixture.BringRequests == requests + 1, "A stable target receives exactly one visibility request.");
        fixture.Window.MouseDown(point, MouseButton.Left);
        fixture.Window.MouseUp(point, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        Check(clicks == 1, "The returned point must deliver one real button click.");
    }

    private static async Task BottomClippingAsync(bool deferredGrowth)
    {
        using var fixture = new Fixture(deferredGrowth ? "DeferredTarget" : "ClippedTarget");
        fixture.Spacer.Height = fixture.Scroll.Viewport.Height - 20;
        Pump();
        double initialOffset = fixture.Scroll.Offset.Y;
        double initialResidual = BottomResidual(fixture);
        Check(initialResidual > .6, "The fixture must start with a genuinely clipped button.");
        bool growthCompleted = false;
        fixture.ObserveBringRequests(handled: true, requested: () =>
        {
            if (!deferredGrowth) return;
            Dispatcher.UIThread.Post(() =>
            {
                fixture.Spacer.Height += 48;
                fixture.Trailing.Height += 64;
                growthCompleted = true;
            }, DispatcherPriority.Background);
        });
        await MainUiTestLayout.ClickPointAsync(fixture.Window, fixture.Target, requireFullVisibility: true);
        Check(fixture.BringRequests == 1, "Preparing visibility must not restart BringIntoView.");
        Check(!deferredGrowth || growthCompleted, "The controlled deferred layout must run before acceptance.");
        double expectedOffset = initialOffset + initialResidual + (deferredGrowth ? 48 : 0);
        Check(Math.Abs(fixture.Scroll.Offset.Y - expectedOffset) <= .6 && Math.Abs(BottomResidual(fixture)) <= .6,
            "The single correction must use the settled target rectangle, not its earlier extent.");
    }

    private static async Task RejectedTargetAsync(string name, bool overlay = false,
        bool oversized = false, bool horizontalClipping = false)
    {
        using var fixture = new Fixture(name);
        fixture.Spacer.Height = 0;
        if (oversized) fixture.Target.Height = fixture.Scroll.Viewport.Height + 40;
        if (horizontalClipping) fixture.Target.Margin = new Thickness(fixture.Scroll.Viewport.Width - 70, 0, 0, 0);
        if (overlay)
            fixture.Root.Children.Add(new Border { Name = "InputOverlay", Background = Brushes.Black, ZIndex = 10 });
        fixture.ObserveBringRequests(handled: true);
        Pump();
        var timer = Stopwatch.StartNew();
        try
        {
            await MainUiTestLayout.ClickPointAsync(fixture.Window, fixture.Target, requireFullVisibility: true);
            throw new InvalidOperationException("An impossible input target was accepted: " + name);
        }
        catch (InvalidOperationException error) when (error.Message.StartsWith(
            "Target cannot receive mouse input after expansion/scrolling:", StringComparison.Ordinal))
        {
            Check(timer.Elapsed >= TimeSpan.FromSeconds(2.8), "A rejected target still uses the original bounded deadline.");
            Check(fixture.BringRequests == 1 && fixture.Scroll.Offset == default,
                "An overlay, oversized target or horizontal clipping must not trigger vertical correction.");
            Check(error.Message.Contains(name, StringComparison.Ordinal)
                && error.Message.Contains("targetInViewport=", StringComparison.Ordinal)
                && error.Message.Contains("viewport=", StringComparison.Ordinal)
                && error.Message.Contains("extent=", StringComparison.Ordinal)
                && error.Message.Contains("correctionAttempted=False", StringComparison.Ordinal),
                "The failure must retain the target, viewport and correction diagnostics.");
            if (overlay) Check(error.Message.Contains("InputOverlay", StringComparison.Ordinal),
                "Failure diagnostics must identify the actual overlay hit.");
        }
    }

    private static double BottomResidual(Fixture fixture) =>
        fixture.Target.TranslatePoint(default, fixture.Scroll)!.Value.Y
        + fixture.Target.Bounds.Height - fixture.Scroll.Viewport.Height;

    private static void Pump()
    {
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class Fixture : IDisposable
    {
        internal Border Spacer { get; } = new() { Height = 40 };
        internal Border Trailing { get; } = new() { Height = 400 };
        internal Button Target { get; }
        internal ScrollViewer Scroll { get; }
        internal Grid Root { get; }
        internal Window Window { get; }
        internal int BringRequests { get; private set; }

        internal Fixture(string name)
        {
            Target = new Button { Name = name, Content = "Target", Width = 100, Height = 33,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left };
            var content = new StackPanel();
            content.Children.Add(Spacer);
            content.Children.Add(Target);
            content.Children.Add(Trailing);
            Scroll = new ScrollViewer { Name = "HelperViewport", Content = content, Margin = new Thickness(12),
                Background = Brushes.Transparent, AllowAutoHide = false,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                VerticalScrollBarVisibility = ScrollBarVisibility.Hidden };
            SmoothScrolling.Attach(Scroll);
            Root = new Grid();
            Root.Children.Add(Scroll);
            Window = new Window { Width = 360, Height = 240, WindowDecorations = WindowDecorations.None, Content = Root };
            Window.Show();
            Pump();
            Check(Scroll.Viewport.Width > 100 && Scroll.Viewport.Height > 100, "A fixture requires a real viewport.");
        }

        internal int ObserveBringRequests(bool handled, Action? requested = null)
        {
            Target.AddHandler(Control.RequestBringIntoViewEvent, (_, args) =>
            {
                BringRequests++;
                args.Handled = handled;
                requested?.Invoke();
            }, RoutingStrategies.Bubble);
            return BringRequests;
        }

        public void Dispose() => Window.Close();
    }
}
