using Avalonia.Controls;
using Avalonia.Threading;
using FluentAvalonia.UI.Controls;
using HeyMelodyWindows.Artwork;

namespace HeyMelodyWindows.App;

/// <summary>Schedules event-driven GPU cleanup without changing artwork animation cadence. UI thread only.</summary>
internal static class ArtworkCleanupTriggers
{
    internal static readonly TimeSpan ExpanderDelay = TimeSpan.FromMilliseconds(600), ScrollDelay = TimeSpan.FromMilliseconds(300);
    private static bool _registered;
    private static DispatcherTimer? _timer;
    private static long? _deadline;
    private static Func<long> _clock = static () => Environment.TickCount64;
    private static Action _requestCleanup = GpuJanitor.Request;
    private static bool _testing;

    internal static bool HasPendingCleanup => _deadline.HasValue;
    internal static long? PendingDeadlineMilliseconds => _deadline;

    /// <summary>Process-wide, once: clean up after an expander's layout and first raster settle.</summary>
    internal static void RegisterExpanders()
    {
        if (_registered) return;
        _registered = true;
        FASettingsExpander.IsExpandedProperty.Changed.Subscribe(new Observer(_ => ScheduleCleanup(ExpanderDelay)));
    }

    internal static void AttachScroll(ScrollViewer scroll) => scroll.ScrollChanged += (_, _) => ScheduleCleanup(ScrollDelay);

    internal static void AttachWindow(Window window)
    {
        window.Deactivated -= OnWindowDeactivated;
        window.Closed -= OnWindowClosed;
        window.Deactivated += OnWindowDeactivated;
        window.Closed += OnWindowClosed;
    }

    private static void OnWindowDeactivated(object? sender, EventArgs e) => _requestCleanup();

    private static void OnWindowClosed(object? sender, EventArgs e)
    {
        if (sender is not Window window) return;
        window.Deactivated -= OnWindowDeactivated;
        window.Closed -= OnWindowClosed;
    }

    internal static void ScheduleCleanup(TimeSpan delay)
    {
        if (delay <= TimeSpan.Zero) return;
        long deadline = _clock() + (long)Math.Ceiling(delay.TotalMilliseconds);
        _deadline = Math.Max(_deadline ?? deadline, deadline);
        if (_timer is not null || _testing) return;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(Math.Max(1, _deadline.Value - _clock())) };
        _timer = timer;
        timer.Tick += (_, _) =>
        {
            if (!ReferenceEquals(_timer, timer)) return;
            timer.Stop();
            if (!FlushPending())
            {
                timer.Interval = TimeSpan.FromMilliseconds(Math.Max(1, _deadline!.Value - _clock()));
                timer.Start();
            }
        };
        timer.Start();
    }

    private static bool FlushPending()
    {
        if (_deadline is not { } deadline) return true;
        if (_clock() < deadline) return false;
        ResetPending();
        _requestCleanup();
        return true;
    }

    private static void ResetPending()
    {
        _timer?.Stop();
        _timer = null;
        _deadline = null;
    }

    internal static void FlushPendingForTests() => FlushPending();

    /// <summary>Uses a deterministic clock and no dispatcher timer; disposal drops pending work and test delegates.</summary>
    internal static IDisposable BeginTestScope(Func<long> clock, Action requestCleanup)
    {
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(requestCleanup);
        if (_testing) throw new InvalidOperationException("Cleanup test scopes cannot overlap.");
        ResetPending();
        _testing = true;
        _clock = clock;
        _requestCleanup = requestCleanup;
        return new TestScope();
    }

    private sealed class TestScope : IDisposable
    {
        private bool _disposed;
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            ResetPending();
            _clock = static () => Environment.TickCount64;
            _requestCleanup = GpuJanitor.Request;
            _testing = false;
        }
    }

    private sealed class Observer(Action<Avalonia.AvaloniaPropertyChangedEventArgs> next) : IObserver<Avalonia.AvaloniaPropertyChangedEventArgs>
    {
        public void OnNext(Avalonia.AvaloniaPropertyChangedEventArgs value) => next(value);
        public void OnError(Exception error) { }
        public void OnCompleted() { }
    }
}
