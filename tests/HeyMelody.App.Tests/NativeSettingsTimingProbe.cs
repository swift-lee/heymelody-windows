using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Rendering.Composition;
using FluentAvalonia.UI.Controls;

/// <summary>Bounded, opt-in native measurements. RAF and batch completion are not displayed-frame timings.</summary>
internal sealed class NativeSettingsTimingProbe : IDisposable
{
    private readonly Window _window;
    private readonly IDisposable _templateSubscription;
    private readonly List<object> _receipts = [];
    private readonly List<object> _lifecycleReceipts = [];
    private readonly List<string> _phaseFailures = [];
    private readonly List<FAContentDialog> _dialogs = [];
    private Phase? _opening, _closing;
    private FAContentDialog? _dialog;
    private Control? _container;
    private DialogObservation? _observation;
    private string _theme = "", _artwork = "", _visit = "";
    private int _sequence;
    private bool _disposed;

    internal NativeSettingsTimingProbe(Window window)
    {
        _window = window;
        _templateSubscription = TemplatedControl.TemplateAppliedEvent.AddClassHandler<FAContentDialog>(TemplateApplied);
    }

    internal IReadOnlyList<object> Receipts => _receipts;
    internal IReadOnlyList<object> LifecycleReceipts => _lifecycleReceipts;

    internal Task BeginOpen(string theme, string artwork, string visit)
    {
        if (_disposed || _opening is not null || _closing is not null)
            throw new InvalidOperationException("A settings timing phase is already active.");
        _theme = theme; _artwork = artwork; _visit = visit; _sequence++;
        _dialog = null; _container = null;
        _observation = new DialogObservation();
        _opening = NewPhase("open", TimeSpan.FromMilliseconds(300));
        // The raw operation window is unchanged; callers also keep screenshots/input outside the transition window.
        return Task.WhenAll(_opening.Completion, _observation.OpeningTransitionCompletion);
    }

    internal void AssertIdentitySamples()
    {
        if (_receipts.Count == 0 || _opening is not null || _closing is not null || _observation is not null
            || _lifecycleReceipts.Count != _sequence)
            throw new InvalidOperationException("Settings timing did not finish.");
        if (_identitySamples == 0 || _nonIdentitySamples != 0 || _missingContainerSamples != 0)
            throw new InvalidOperationException(
                $"Settings transform samples: {_identitySamples} identity, {_nonIdentitySamples} changed, {_missingContainerSamples} missing.");
        if (_phaseFailures.Count > 0)
            throw new InvalidOperationException(string.Join(" ", _phaseFailures));
    }

    private int _identitySamples, _nonIdentitySamples, _missingContainerSamples;

    private Phase NewPhase(string name, TimeSpan? duration)
    {
        var phase = new Phase(_window, name, duration, SampleTransform);
        phase.Finished += receipt =>
        {
            if (phase.BatchFailure is { } failure)
                _phaseFailures.Add($"{_theme}/{_artwork}/{name}: {failure}");
            _receipts.Add(new
            {
                sequence = _sequence, theme = _theme, artwork = _artwork, visit = _visit,
                firstDialogInProcess = _sequence == 1, measurement = receipt
            });
            if (ReferenceEquals(_opening, phase)) _opening = null;
            if (ReferenceEquals(_closing, phase)) _closing = null;
        };
        phase.Start();
        return phase;
    }

    private void TemplateApplied(FAContentDialog dialog, TemplateAppliedEventArgs args)
    {
        if (_disposed || _observation is null || !Equals(dialog.Title, "应用设置")) return;
        _dialog = dialog;
        _container = args.NameScope.Find<Control>("Container");
        if (_container is null) _missingContainerSamples++;
        _opening?.Mark("templateApplied");
        _observation.Mark("templateApplied");
        if (_dialogs.Contains(dialog)) return;
        _dialogs.Add(dialog);
        dialog.Loaded += DialogLoaded;
        dialog.Opened += DialogOpened;
        dialog.Closing += DialogClosing;
        dialog.Closed += DialogClosed;
        var observation = _observation;
        void ObserveTransform(TimeSpan _)
        {
            if (_disposed || !ReferenceEquals(_observation, observation)) return;
            observation.ObserveFrame();
            SampleTransform();
            _window.RequestAnimationFrame(ObserveTransform);
        }
        // Keep transform coverage after a cold initial layout exceeds the fixed 300 ms metric window.
        _window.RequestAnimationFrame(ObserveTransform);
    }

    private void DialogLoaded(object? sender, Avalonia.Interactivity.RoutedEventArgs args)
    {
        _opening?.Mark("loaded");
        _observation?.Mark("loaded");
    }

    private void DialogOpened(FAContentDialog sender, EventArgs args)
    {
        _opening?.Mark("opened");
        _observation?.Mark("opened");
    }

    private void DialogClosing(FAContentDialog sender, FAContentDialogClosingEventArgs args)
    {
        if (_disposed || sender != _dialog) return;
        _observation?.Mark("closing");
        _observation?.FinishOpeningTransition();
        // Every measured interaction waits for the fixed opening window before clicking a close button.
        _opening?.Stop();
        _closing = NewPhase("close", null);
        _closing.SampleNow();
    }

    private void DialogClosed(FAContentDialog sender, FAContentDialogClosedEventArgs args)
    {
        if (sender != _dialog) return;
        _closing?.SampleNow();
        _closing?.Stop();
        if (_observation is { } observation)
        {
            observation.Mark("closed");
            observation.FinishOpeningTransition();
            if (observation.IdentitySamples == 0)
                _phaseFailures.Add($"{_theme}/{_artwork} has no post-template transform observation.");
            _lifecycleReceipts.Add(new
            {
                sequence = _sequence, theme = _theme, artwork = _artwork, visit = _visit,
                firstDialogInProcess = _sequence == 1, milestonesMs = observation.Marks,
                firstTransformSampleMs = observation.FirstTransformSampleMs,
                openingTransitionRaf = observation.OpeningTransitionReceipt(),
                identitySamples = observation.IdentitySamples, nonIdentitySamples = observation.NonIdentitySamples,
                scope = "Whole dialog lifecycle from operation start, including initial layout beyond the fixed opening metric window."
            });
        }
        _observation = null;
    }

    private bool? SampleTransform()
    {
        if (_container is null) return null;
        var matrix = _container.RenderTransform?.Value ?? Avalonia.Matrix.Identity;
        var identity = Math.Abs(matrix.M11 - 1) < 1e-9 && Math.Abs(matrix.M22 - 1) < 1e-9
            && Math.Abs(matrix.M12) < 1e-9 && Math.Abs(matrix.M21) < 1e-9
            && Math.Abs(matrix.M31) < 1e-9 && Math.Abs(matrix.M32) < 1e-9;
        if (identity) _identitySamples++; else _nonIdentitySamples++;
        _observation?.Sample(identity);
        return identity;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _opening?.Stop(); _closing?.Stop();
        _observation?.FinishOpeningTransition();
        _templateSubscription.Dispose();
        foreach (var dialog in _dialogs)
        {
            dialog.Loaded -= DialogLoaded;
            dialog.Opened -= DialogOpened;
            dialog.Closing -= DialogClosing;
            dialog.Closed -= DialogClosed;
        }
        _dialogs.Clear();
        _container = null; _dialog = null; _observation = null;
    }

    private sealed class DialogObservation
    {
        private readonly long _started = Stopwatch.GetTimestamp();
        private readonly TaskCompletionSource _openingTransition = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly List<double> _frameTimes = [];
        private double? _loadedAt, _transitionObservedEnd;
        internal Task OpeningTransitionCompletion => _openingTransition.Task;
        internal Dictionary<string, double> Marks { get; } = [];
        internal double? FirstTransformSampleMs { get; private set; }
        internal int IdentitySamples { get; private set; }
        internal int NonIdentitySamples { get; private set; }
        private double Now => Stopwatch.GetElapsedTime(_started).TotalMilliseconds;
        internal void Mark(string name)
        {
            var now = Now;
            Marks.TryAdd(name, Math.Round(now, 3));
            if (name == "loaded") _loadedAt ??= now;
        }
        internal void ObserveFrame()
        {
            var now = Now;
            _frameTimes.Add(now);
            if (_loadedAt is { } loaded && now >= loaded + 300)
                FinishOpeningTransition();
        }
        internal void FinishOpeningTransition()
        {
            _transitionObservedEnd ??= Now;
            _openingTransition.TrySetResult();
        }
        internal object OpeningTransitionReceipt()
        {
            var start = _loadedAt ?? 0;
            var observedEnd = _transitionObservedEnd ?? Now;
            var complete = _loadedAt is not null && observedEnd >= start + 300;
            var end = _loadedAt is null ? start : Math.Min(start + 300, observedEnd);
            var duration = complete ? 300 : Math.Max(0, end - start);
            var frames = _frameTimes.Where(at => at >= start && at <= end).Select(at => at - start).ToArray();
            return new
            {
                origin = "dialogLoaded", startOffsetFromOperationMs = _loadedAt is null ? (double?)null : Math.Round(start, 3),
                plannedDurationMs = 300, measuredDurationMs = Math.Round(duration, 3),
                observedCompletionOffsetFromOperationMs = _transitionObservedEnd is { } observed ? (double?)Math.Round(observed, 3) : null,
                censored = !complete,
                callbackOffsetsFromLoadedMs = frames.Select(at => Math.Round(at, 3)).ToArray(),
                raf = _loadedAt is null ? null : Phase.RafReceipt(frames, duration),
                scope = "Existing lifecycle RAF observations in the first 300 ms after Loaded; excludes earlier construction/layout and retains complete head/tail gaps. Not displayed frames."
            };
        }
        internal void Sample(bool identity)
        {
            FirstTransformSampleMs ??= Math.Round(Now, 3);
            if (identity) IdentitySamples++; else NonIdentitySamples++;
        }
    }

    private sealed class Phase(Window window, string name, TimeSpan? duration, Func<bool?> sampleTransform)
    {
        private readonly long _started = Stopwatch.GetTimestamp();
        private readonly CancellationTokenSource _stop = new();
        private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly List<double> _frames = [];
        private readonly List<BatchObservation> _batches = [];
        private readonly Dictionary<string, double> _marks = [];
        private readonly object _batchGate = new();
        private double? _ended;
        private int _identity, _nonIdentity, _missing;
        private string? _batchFailure;
        internal event Action<object>? Finished;
        internal Task Completion => _completion.Task;
        internal string? BatchFailure
        {
            get
            {
                lock (_batchGate)
                    return _batchFailure ?? _batches.FirstOrDefault(batch => batch.Failure is not null)?.Failure;
            }
        }

        private double Now => Stopwatch.GetElapsedTime(_started).TotalMilliseconds;

        internal void Start()
        {
            window.RequestAnimationFrame(Frame);
            _ = ObserveBatchesAsync();
            if (duration is { } fixedDuration) _ = EndOpeningAsync(fixedDuration);
        }

        internal void Mark(string name) => _marks.TryAdd(name, Now);

        internal void SampleNow()
        {
            // No pre-template sample: construction and template time are covered by the first RAF delay.
            var value = sampleTransform();
            if (value == true) _identity++;
            else if (value == false) _nonIdentity++;
            else _missing++;
        }

        private void Frame(TimeSpan _)
        {
            if (_ended is not null) return;
            var now = Now;
            if (duration is { } fixedDuration && now > fixedDuration.TotalMilliseconds)
            {
                Stop();
                return;
            }
            _frames.Add(now);
            SampleNow();
            window.RequestAnimationFrame(Frame);
        }

        private async Task EndOpeningAsync(TimeSpan fixedDuration)
        {
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    var remaining = fixedDuration.TotalMilliseconds - Now;
                    if (remaining <= 0) { Stop(); return; }
                    await Task.Delay(TimeSpan.FromMilliseconds(Math.Max(1, remaining)), _stop.Token);
                }
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        }

        private async Task ObserveBatchesAsync()
        {
            try
            {
                var visual = ElementComposition.GetElementVisual(window)
                    ?? throw new InvalidOperationException("The native window has no composition visual.");
                while (!_stop.IsCancellationRequested)
                {
                    var observation = new BatchObservation(Now);
                    lock (_batchGate) _batches.Add(observation);
                    var batch = visual.Compositor.RequestCompositionBatchCommitAsync();
                    // Timestamp Rendered on its completing thread, without waiting for a busy UI dispatcher.
                    var completed = batch.Rendered.ContinueWith(task =>
                    {
                        var at = Now;
                        lock (_batchGate)
                        {
                            if (task.IsCompletedSuccessfully) observation.CompletedAt = at;
                            else observation.Failure = task.Exception?.GetBaseException().Message ?? "Batch rendering was canceled.";
                        }
                    }, _stop.Token, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                    await completed.WaitAsync(_stop.Token);
                    await Task.Delay(8, _stop.Token);
                }
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
            catch (Exception error) { _batchFailure = error.Message; }
        }

        internal void Stop()
        {
            if (_ended is not null) return;
            var observedEnd = Now;
            var end = duration is { } fixedDuration
                ? Math.Min(observedEnd, fixedDuration.TotalMilliseconds) : observedEnd;
            _ended = end;
            _stop.Cancel();
            var frames = _frames.Where(at => at <= end).ToArray();
            object composition;
            lock (_batchGate)
            {
                var requested = _batches.Where(batch => batch.RequestedAt <= end).ToArray();
                var finished = requested.Where(batch => batch.CompletedAt is { } at && at <= end).ToArray();
                var pending = requested.Where(batch => batch.CompletedAt is null || batch.CompletedAt > end).ToArray();
                var latency = finished.Select(batch => batch.CompletedAt!.Value - batch.RequestedAt).ToArray();
                composition = new
                {
                    requests = requested.Length, completed = finished.Length, pendingAtDeadline = pending.Length,
                    oldestPendingWaitLowerBoundMs = pending.Length == 0 ? (double?)null
                        : Round(pending.Max(batch => end - batch.RequestedAt)),
                    latencyP95Ms = Percentile(latency, .95), latencyMaximumMs = Maximum(latency),
                    failure = _batchFailure ?? requested.FirstOrDefault(batch => batch.Failure is not null)?.Failure
                };
            }
            Finished?.Invoke(new
            {
                phase = name, plannedDurationMs = duration?.TotalMilliseconds, measuredDurationMs = Round(end),
                observedStopMs = Round(observedEnd), marksMs = _marks,
                raf = RafReceipt(frames, end),
                composition,
                transform = new { identitySamples = _identity, nonIdentitySamples = _nonIdentity, missingContainerSamples = _missing },
                scope = "Monotonic UI RAF and composition-batch Rendered observations; not DWM/displayed frames. Boundary gaps and pending requests are censored lower bounds."
            });
            _completion.TrySetResult();
        }

        private sealed class BatchObservation(double requestedAt)
        {
            internal double RequestedAt { get; } = requestedAt;
            internal double? CompletedAt;
            internal string? Failure;
        }

        internal static object RafReceipt(double[] frames, double duration)
        {
            var intervals = frames.Zip(frames.Skip(1), (a, b) => b - a).ToArray();
            var head = frames.Length > 0 ? frames[0] : duration;
            var tail = frames.Length > 0 ? Math.Max(0, duration - frames[^1]) : duration;
            return new
            {
                count = frames.Length, firstCallbackDelayMs = frames.Length == 0 ? (double?)null : Round(head),
                intervalCount = intervals.Length, internalIntervalP95Ms = Percentile(intervals, .95),
                internalIntervalMaximumMs = Maximum(intervals), tailWithoutCallbackMs = Round(tail),
                maximumGapIncludingBoundariesMs = Round(Math.Max(Math.Max(head, tail), intervals.DefaultIfEmpty().Max())),
                noCallbackWindow = frames.Length == 0
            };
        }

        private static double Round(double value) => Math.Round(value, 3);
        private static double? Maximum(double[] values) => values.Length == 0 ? null : Round(values.Max());
        private static double? Percentile(double[] values, double percentile)
        {
            if (values.Length == 0) return null;
            var sorted = values.Order().ToArray();
            return Round(sorted[Math.Clamp((int)Math.Ceiling(sorted.Length * percentile) - 1, 0, sorted.Length - 1)]);
        }
    }
}
