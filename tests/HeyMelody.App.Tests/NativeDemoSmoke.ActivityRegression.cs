using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using Avalonia.VisualTree;
using FluentAvalonia.UI.Controls;
using HeyMelody.Rendering.Filament;
using HeyMelodyWindows.App;

internal static partial class NativeDemoSmoke
{
    private static readonly TimeSpan ActivityPhaseDuration = TimeSpan.FromSeconds(30);

    private sealed record ActivityGeometry(Size ClientSize, Size ViewportSize, PixelPoint Position,
        double RenderScaling, int RenderWidth, int RenderHeight);

    private sealed record ActivitySample(double Seconds, long Callbacks, long CameraFrameRequests,
        ulong CompletedFrameId, ulong PresentedFrameId, long PresentedFrames, ulong Generation,
        string MotionPhase, bool MainWindowActive, bool AnimationActive, bool EffectivelyVisible,
        double RenderMilliseconds, double ReadbackMilliseconds, int RecentIntervalSamples,
        double? RecentIntervalMedianMilliseconds, double? RecentIntervalP95Milliseconds,
        double? RecentIntervalMaximumMilliseconds);

    private sealed class ActivityPhase
    {
        public required string Name { get; init; }
        public required ActivityGeometry Geometry { get; init; }
        public required string ModelSha256 { get; init; }
        public required string RequestedBackend { get; init; }
        public required string ActualBackend { get; init; }
        public bool ExpectedVisible { get; init; }
        public string FrameRate { get; init; } = "unlimited";
        public string IntervalScope { get; init; } =
            "overlapping recent presentation history, not a complete phase histogram; resumed intervals can include time intentionally hidden";
        public List<ActivitySample> Samples { get; } = [];
        public double ElapsedSeconds { get; set; }
        public bool Completed { get; set; }
        public string? Failure { get; set; }
    }

    private static async Task CheckActivityRegressionAsync(MainWindow window, MainView page, DeviceModelView model)
    {
        Check(ModelRenderingPreferences.FrameRate == ModelFrameRate.Unlimited,
            "The opt-in activity regression starts at Unlimited without writing user preferences.");
        page.RequestFitTest = () => { _state.CalibrationLaunches++; return Task.CompletedTask; };
        page.RequestHearingSetup = () => { _state.CalibrationLaunches++; return Task.CompletedTask; };
        page.ExpandAllSettings();
        await UntilAsync(() => page.GetVisualDescendants().OfType<FeatureControlView>().Count() == 32,
            "The long regression materializes the local demo editors before fixing its geometry.");
        await Task.Delay(500);
        window.Activate();
        await UntilAsync(() => window.IsActive && model.AnimationActive && model.LastPresentedFrame.FrameId > 0,
            "The long regression starts with an active native window and a presented frame.");
        var first = model.LastPresentedFrame;
        var geometry = new ActivityGeometry(window.ClientSize, model.Viewport.Bounds.Size, window.Position,
            window.RenderScaling, first.Width, first.Height);

        await ObserveActivityPhaseAsync("auto-rotation", window, model, geometry, visible: true);
        await DragActivityModelAsync(window, model);
        await ObserveActivityPhaseAsync("drag-inertia-idle-recovery", window, model, geometry, visible: true);
        Check(model.Motion.Phase == ModelOrbitPhase.Idle,
            "The real drag passes through inertia and returns to idle rotation within the observed phase.");

        var helper = new Window
        {
            Title = "HeyMelody regression focus helper",
            Width = 180, Height = 90, CanResize = false, ShowInTaskbar = false,
            Topmost = true, ShowActivated = true, WindowStartupLocation = WindowStartupLocation.Manual,
            Content = new TextBlock { Text = "Native focus regression", Margin = new Thickness(12) }
        };
        bool focusAcquired = false;
        var focusClick = new ActivityFocusClickStatus();
        try
        {
            helper.Show();
            await Task.Delay(150);
            PlaceFocusHelperOutsideModel(window, helper, model);
            helper.Activate();
            var helperHandle = helper.TryGetPlatformHandle()?.Handle
                ?? throw new InvalidOperationException("The focus helper has no native window handle.");
            ClickActivityFocusHelper(helperHandle, focusClick);
            await UntilAsync(() => !window.IsActive && helper.IsActive && ActivityGetForegroundWindow() == helperHandle,
                "A separate native helper actually owns foreground focus while the model window is inactive.");
            focusAcquired = true;
            bool focusInterrupted = false;
            EventHandler<AvaloniaPropertyChangedEventArgs> focusChanged = (_, e) =>
            {
                if (e.Property.Name == nameof(Window.IsActive) && (window.IsActive || !helper.IsActive))
                    focusInterrupted = true;
            };
            window.PropertyChanged += focusChanged;
            helper.PropertyChanged += focusChanged;
            try
            {
                await ObserveActivityPhaseAsync("sustained-native-inactive", window, model, geometry, visible: true,
                    validate: () =>
                    {
                        RequireActivity(!focusInterrupted && !window.IsActive && helper.IsActive
                            && ActivityGetForegroundWindow() == helperHandle,
                            "The model window must remain genuinely inactive throughout the phase.");
                        RequireActivity(FocusHelperDoesNotCoverModel(helper, model),
                            "The native focus helper must not obscure the rendered model.");
                    });
            }
            finally
            {
                window.PropertyChanged -= focusChanged;
                helper.PropertyChanged -= focusChanged;
            }
        }
        finally
        {
            CaptureActivityFocusDiagnostics(window, helper, focusAcquired, focusClick);
            helper.Close();
        }
        window.Activate();
        await UntilAsync(() => window.IsActive, "The main window regains actual native focus after the inactive phase.");

        var scroll = page.FindControl<ScrollViewer>("ControlScroll")
            ?? throw new InvalidOperationException("Missing control scroll viewer.");
        var expander = page.FindControl<FASettingsExpander>("DeviceInfoPanel")
            ?? throw new InvalidOperationException("Missing activity expander.");
        int scrollChanges = 0, expanderChanges = 0;
        EventHandler<ScrollChangedEventArgs> scrolled = (_, _) => scrollChanges++;
        EventHandler<AvaloniaPropertyChangedEventArgs> expanded = (_, e) =>
        {
            if (e.Property.Name == nameof(FASettingsExpander.IsExpanded)) expanderChanges++;
        };
        bool wasExpanded = expander.IsExpanded;
        var oldOffset = scroll.Offset;
        scroll.ScrollChanged += scrolled;
        expander.PropertyChanged += expanded;
        try
        {
            await ObserveActivityPhaseAsync("scroll-and-expander-activity", window, model, geometry, visible: true,
                activity: tick =>
                {
                    expander.IsExpanded = !expander.IsExpanded;
                    double maximum = Math.Max(0, scroll.Extent.Height - scroll.Viewport.Height);
                    scroll.Offset = new Vector(0, tick % 2 == 0 ? 0 : maximum);
                });
            Check(scrollChanges > 0 && expanderChanges > 0,
                "The activity phase produces real scroll-offset and expander-property changes, not only timer events.");
        }
        finally
        {
            scroll.ScrollChanged -= scrolled;
            expander.PropertyChanged -= expanded;
            expander.IsExpanded = wasExpanded;
            scroll.Offset = oldOffset;
        }

        model.IsVisible = false;
        await UntilAsync(() => !model.AnimationActive && !model.Motion.IsActive,
            "Hiding the model stops the real animation loop before the hidden observation.");
        await Task.Delay(250);
        await ObserveActivityPhaseAsync("hidden", window, model, geometry, visible: false);
        model.IsVisible = true;
        await UntilAsync(() => model.AnimationActive && model.IsEffectivelyVisible,
            "Showing the model restores its real animation loop.");
        await ObserveActivityPhaseAsync("shown-recovery", window, model, geometry, visible: true);

        window.WindowState = WindowState.Minimized;
        await UntilAsync(() => !model.AnimationActive,
            "Minimizing the window stops its real animation loop before the minimized observation.");
        await Task.Delay(250);
        await ObserveActivityPhaseAsync("minimized", window, model, geometry, visible: false);
        window.WindowState = WindowState.Normal;
        window.Activate();
        await UntilAsync(() => model.AnimationActive && window.WindowState == WindowState.Normal,
            "Restoring the same native window resumes its model.");
        await ObserveActivityPhaseAsync("restored-recovery", window, model, geometry, visible: true);
    }

    private static async Task ObserveActivityPhaseAsync(string name, Window window, DeviceModelView model,
        ActivityGeometry geometry, bool visible, Action<int>? activity = null, Action? validate = null)
    {
        var diagnostics = model.Diagnostics;
        var phase = new ActivityPhase
        {
            Name = name, Geometry = geometry, ExpectedVisible = visible,
            ModelSha256 = model.ModelDigest, RequestedBackend = diagnostics.RequestedBackend.ToString()!,
            ActualBackend = diagnostics.ActualBackend.ToString()!
        };
        _state.ActivityPhases.Add(phase);
        var elapsed = Stopwatch.StartNew();
        try
        {
            Validate();
            phase.Samples.Add(Sample(0));
            int tick = 0;
            while (elapsed.Elapsed < ActivityPhaseDuration)
            {
                activity?.Invoke(++tick);
                var remaining = ActivityPhaseDuration - elapsed.Elapsed;
                await Task.Delay(TimeSpan.FromMilliseconds(Math.Max(1, Math.Min(1000, remaining.TotalMilliseconds))));
                Validate();
                phase.Samples.Add(Sample(elapsed.Elapsed.TotalSeconds));
            }
            phase.ElapsedSeconds = elapsed.Elapsed.TotalSeconds;
            var before = phase.Samples[0];
            var after = phase.Samples[^1];
            for (int index = 1; index < phase.Samples.Count; index++)
            {
                var previous = phase.Samples[index - 1];
                var current = phase.Samples[index];
                RequireActivity(current.CompletedFrameId >= previous.CompletedFrameId
                    && current.PresentedFrameId >= previous.PresentedFrameId,
                    name + ": native completion and presentation IDs remain monotonic.");
            }
            if (visible)
            {
                RequireActivity(after.Callbacks > before.Callbacks && after.CameraFrameRequests > before.CameraFrameRequests
                    && after.CompletedFrameId > before.CompletedFrameId && after.PresentedFrameId > before.PresentedFrameId
                    && after.PresentedFrames > before.PresentedFrames,
                    name + ": the visible model must make callback, camera-request, completion and presentation progress.");
                RequireActivity(after.CameraFrameRequests - before.CameraFrameRequests <= after.Callbacks - before.Callbacks,
                    name + ": camera requests cannot exceed received callbacks or batch-submit an accumulated request backlog.");
            }
            else
                RequireActivity(after.Callbacks == before.Callbacks && after.CameraFrameRequests == before.CameraFrameRequests
                    && after.PresentedFrameId == before.PresentedFrameId && after.PresentedFrames == before.PresentedFrames,
                    name + ": hidden/minimized UI work must stop; already in-flight native readback may finish.");
            phase.Completed = true;
            Check(phase.ElapsedSeconds >= ActivityPhaseDuration.TotalSeconds,
                $"Activity phase {name} records at least thirty seconds with its lifecycle/progress invariants; no minimum FPS assertion.");
        }
        catch (Exception error)
        {
            phase.ElapsedSeconds = elapsed.Elapsed.TotalSeconds;
            phase.Failure = error.ToString();
            throw;
        }

        void Validate()
        {
            var current = model.Diagnostics;
            RequireActivity(ModelRenderingPreferences.FrameRate == ModelFrameRate.Unlimited
                && current.RequestedBackend == _state.RequestedBackend && current.ActualBackend == _state.RequestedBackend
                && model.ModelDigest == DynaudioModelTests.ExpectedSha256 && model.ModelReady && model.LoadError is null,
                name + ": the model, Unlimited selection and actual native backend must remain unchanged.");
            RequireActivity(window.RenderScaling == geometry.RenderScaling
                && (window.WindowState == WindowState.Minimized || window.ClientSize == geometry.ClientSize),
                name + ": the window size and DPI must remain fixed.");
            if (visible)
                RequireActivity(model.AnimationActive && model.IsEffectivelyVisible && window.WindowState != WindowState.Minimized
                    && window.Position == geometry.Position && model.Viewport.Bounds.Size == geometry.ViewportSize
                    && current.Width == geometry.RenderWidth && current.Height == geometry.RenderHeight,
                    name + ": the visible model viewport and native render dimensions must remain fixed.");
            else RequireActivity(!model.AnimationActive, name + ": the hidden/minimized frame loop must remain stopped.");
            RequireActivity(_state.Service.ForbiddenCalls == 0 && _state.CalibrationLaunches == 0,
                name + ": activity must not contact a device/artwork backend or launch device/audio calibration.");
            validate?.Invoke();
        }

        ActivitySample Sample(double seconds)
        {
            var frame = model.LastPresentedFrame;
            var current = model.Diagnostics;
            var intervals = model.PresentationIntervalsMilliseconds.Order().ToArray();
            return new(seconds, model.AnimationCallbackCount, model.CameraUpdateCount, current.FrameId,
                frame.FrameId, model.PresentedFrameCount, frame.Generation, model.Motion.Phase.ToString(),
                window.IsActive, model.AnimationActive, model.IsEffectivelyVisible,
                frame.RenderMilliseconds, frame.ReadbackMilliseconds, intervals.Length,
                Percentile(.5), Percentile(.95), intervals.Length == 0 ? null : intervals[^1]);

            double? Percentile(double fraction) => intervals.Length == 0 ? null
                : intervals[Math.Clamp((int)Math.Ceiling(fraction * intervals.Length) - 1, 0, intervals.Length - 1)];
        }
    }

    private static async Task DragActivityModelAsync(Window window, DeviceModelView model)
    {
        var hwnd = window.TryGetPlatformHandle()?.Handle
            ?? throw new InvalidOperationException("Missing model HWND.");
        var point = NativeWindowCapture.ClientPoint(hwnd,
            model.Viewport.PointToScreen(new Point(model.Viewport.Bounds.Width * .5, model.Viewport.Bounds.Height * .5)));
        NativeWindowCapture.PointerMessage(hwnd, 0x0200, point.X, point.Y, false);
        NativeWindowCapture.PointerMessage(hwnd, 0x0201, point.X, point.Y, true);
        await UntilAsync(() => model.Motion.IsDragging, "The activity regression begins a real Win32 model drag.");
        for (int step = 1; step <= 8; step++)
        {
            NativeWindowCapture.PointerMessage(hwnd, 0x0200, point.X + step * 8, point.Y + step * 3, true);
            await Task.Delay(25);
        }
        NativeWindowCapture.PointerMessage(hwnd, 0x0202, point.X + 64, point.Y + 24, false);
        await UntilAsync(() => !model.Motion.IsDragging && model.Motion.Phase == ModelOrbitPhase.Inertia,
            "A fresh native pointer release enters inertia before the thirty-second recovery observation.");
    }

    private static void PlaceFocusHelperOutsideModel(Window window, Window helper, DeviceModelView model)
    {
        var handle = helper.TryGetPlatformHandle()?.Handle
            ?? throw new InvalidOperationException("Missing focus helper HWND.");
        if (!ActivityGetWindowRect(handle, out var rect)) throw new InvalidOperationException("Missing focus helper bounds.");
        var work = window.Screens.ScreenFromWindow(window)?.WorkingArea
            ?? throw new InvalidOperationException("Missing activity screen working area.");
        int width = rect.Right - rect.Left, height = rect.Bottom - rect.Top;
        foreach (var position in new[]
        {
            new PixelPoint(work.X + 8, work.Y + 8),
            new PixelPoint(work.Right - width - 8, work.Y + 8),
            new PixelPoint(work.X + 8, work.Bottom - height - 8),
            new PixelPoint(work.Right - width - 8, work.Bottom - height - 8)
        })
        {
            helper.Position = position;
            if (FocusHelperDoesNotCoverModel(helper, model)) return;
        }
        throw new InvalidOperationException("The focus helper cannot fit without covering the model.");
    }

    private static bool FocusHelperDoesNotCoverModel(Window helper, DeviceModelView model)
    {
        var handle = helper.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        if (handle == IntPtr.Zero || !ActivityGetWindowRect(handle, out var helperRect)) return false;
        var a = model.Viewport.PointToScreen(default);
        var b = model.Viewport.PointToScreen(new Point(model.Viewport.Bounds.Width, model.Viewport.Bounds.Height));
        return helperRect.Right <= Math.Min(a.X, b.X) || helperRect.Left >= Math.Max(a.X, b.X)
            || helperRect.Bottom <= Math.Min(a.Y, b.Y) || helperRect.Top >= Math.Max(a.Y, b.Y);
    }

    private sealed class ActivityFocusClickStatus
    {
        public bool TargetVerified { get; set; }
        public bool CursorSaved { get; set; }
        public bool InputSent { get; set; }
        public uint InputsInserted { get; set; }
        public bool CursorRestored { get; set; }
        public bool PartialClickReleased { get; set; }
        public bool ButtonMayRemainDown { get; set; }
        public bool CursorRestorationSkipped { get; set; }
        public string? Failure { get; set; }
    }

    private static void ClickActivityFocusHelper(IntPtr helperHandle, ActivityFocusClickStatus status)
    {
        if (!ActivityGetClientRect(helperHandle, out var client))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError(), "Cannot read the focus helper client rectangle.");
        RequireActivity(client.Right - client.Left >= 32 && client.Bottom - client.Top >= 32,
            "The focus helper has no safe blank client area for a native click.");
        var target = new ActivityNativePoint { X = (client.Left + client.Right) / 2, Y = client.Bottom - 8 };
        if (!ActivityClientToScreen(helperHandle, ref target))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError(), "Cannot locate the helper click in screen coordinates.");
        RequireActivity(ActivityPointBelongsToHelper(target, helperHandle),
            "The proposed native click does not hit this test's helper; no input is sent.");
        RequireActivity((ActivityGetAsyncKeyState(1) & 0x8000) == 0,
            "The physical left mouse button is already held; the focus test will not interfere.");
        if (!ActivityGetCursorPos(out var original))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError(), "Cannot save the original cursor position.");
        status.CursorSaved = true;
        Exception? failure = null;
        try
        {
            if (!ActivitySetCursorPos(target.X, target.Y))
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError(), "Cannot move the cursor to the verified helper.");
            if (!ActivityGetCursorPos(out var actual))
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError(), "Cannot verify the native click cursor position.");
            RequireActivity(actual.X == target.X && actual.Y == target.Y && ActivityPointBelongsToHelper(actual, helperHandle),
                "The cursor no longer hits this test's helper; no input is sent.");
            status.TargetVerified = true;
            int inputSize = Marshal.SizeOf<ActivityNativeInput>();
            RequireActivity(inputSize == (IntPtr.Size == 8 ? 40 : 28), "Unexpected native mouse INPUT layout.");
            var down = new ActivityNativeInput { Data = new ActivityNativeInputData { Mouse = new ActivityNativeMouseInput { Flags = 0x0002 } } };
            var up = new ActivityNativeInput { Data = new ActivityNativeInputData { Mouse = new ActivityNativeMouseInput { Flags = 0x0004 } } };
            // Do not pump messages between verifying the helper hit and sending this one down/up batch.
            status.InputsInserted = ActivitySendInput(2, new[] { down, up }, inputSize);
            status.InputSent = status.InputsInserted == 2;
            status.ButtonMayRemainDown = status.InputsInserted == 1;
            if (!status.InputSent)
            {
                Exception inputError = new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError(),
                    $"The protected focus click inserted {status.InputsInserted}/2 native inputs.");
                if (status.InputsInserted == 1)
                {
                    status.PartialClickReleased = ActivitySendInput(1, new[] { up }, inputSize) == 1;
                    status.ButtonMayRemainDown = !status.PartialClickReleased;
                    if (!status.PartialClickReleased)
                        inputError = new AggregateException("A partial native click could not be released.", inputError,
                            new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError()));
                }
                throw inputError;
            }
        }
        catch (Exception error) { failure = error; }
        finally
        {
            if (status.ButtonMayRemainDown)
            {
                status.CursorRestorationSkipped = true;
                var skipped = new InvalidOperationException("Cursor restoration was skipped because the native left button may still be down.");
                failure = failure is null ? skipped : new AggregateException("A partial click remains unreleased; the cursor must not move.", failure, skipped);
            }
            else
            {
                try
                {
                    if (!ActivitySetCursorPos(original.X, original.Y))
                        throw new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError(), "Cannot restore the original cursor position.");
                    if (!ActivityGetCursorPos(out var restored))
                        throw new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError(), "Cannot verify the restored cursor position.");
                    RequireActivity(restored.X == original.X && restored.Y == original.Y, "The original cursor position was not restored.");
                    status.CursorRestored = true;
                }
                catch (Exception restoreError)
                {
                    failure = failure is null ? restoreError
                        : new AggregateException("Native focus input and cursor restoration failed.", failure, restoreError);
                }
            }
        }
        if (failure is not null)
        {
            status.Failure = failure.Message;
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }

    private static bool ActivityPointBelongsToHelper(ActivityNativePoint point, IntPtr helperHandle)
    {
        var hit = ActivityWindowFromPoint(point);
        return hit != IntPtr.Zero && (hit == helperHandle || ActivityGetAncestor(hit, 2) == helperHandle);
    }

    private static void CaptureActivityFocusDiagnostics(Window main, Window helper, bool focusAcquired, ActivityFocusClickStatus focusClick)
    {
        try
        {
            var mainHandle = main.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
            var helperHandle = helper.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
            var foreground = ActivityGetForegroundWindow();
            _state.ActivityFocusDiagnostics = new
            {
                capturedBeforeHelperClose = true,
                helperShowMode = "unowned",
                focusAcquired,
                nativeClick = focusClick,
                main = new
                {
                    hwnd = mainHandle.ToInt64().ToString("X"),
                    main.IsActive, main.IsVisible, state = main.WindowState.ToString()
                },
                helper = new
                {
                    hwnd = helperHandle.ToInt64().ToString("X"),
                    helper.IsActive, helper.IsVisible, state = helper.WindowState.ToString()
                },
                foregroundHwnd = foreground.ToInt64().ToString("X"),
                foregroundIsMain = foreground == mainHandle,
                foregroundIsHelper = foreground == helperHandle
            };
        }
        catch (Exception error)
        {
            _state.ActivityFocusDiagnostics = new { captureFailed = true, error = error.ToString() };
        }
    }

    private static void RequireActivity(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ActivityNativeRect { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct ActivityNativePoint { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)]
    private struct ActivityNativeMouseInput
    {
        public int X, Y;
        public uint MouseData, Flags, Time;
        public UIntPtr ExtraInfo;
    }
    [StructLayout(LayoutKind.Explicit)]
    private struct ActivityNativeInputData
    {
        [FieldOffset(0)] public ActivityNativeMouseInput Mouse;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct ActivityNativeInput
    {
        public uint Type;
        public ActivityNativeInputData Data;
    }

    [DllImport("user32.dll", EntryPoint = "GetClientRect", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ActivityGetClientRect(IntPtr hwnd, out ActivityNativeRect rect);
    [DllImport("user32.dll", EntryPoint = "ClientToScreen", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ActivityClientToScreen(IntPtr hwnd, ref ActivityNativePoint point);
    [DllImport("user32.dll", EntryPoint = "WindowFromPoint")]
    private static extern IntPtr ActivityWindowFromPoint(ActivityNativePoint point);
    [DllImport("user32.dll", EntryPoint = "GetAncestor")]
    private static extern IntPtr ActivityGetAncestor(IntPtr hwnd, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetAsyncKeyState")]
    private static extern short ActivityGetAsyncKeyState(int key);
    [DllImport("user32.dll", EntryPoint = "GetCursorPos", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ActivityGetCursorPos(out ActivityNativePoint point);
    [DllImport("user32.dll", EntryPoint = "SetCursorPos", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ActivitySetCursorPos(int x, int y);
    [DllImport("user32.dll", EntryPoint = "SendInput", SetLastError = true)]
    private static extern uint ActivitySendInput(uint count, [In] ActivityNativeInput[] inputs, int inputSize);

    [DllImport("user32.dll", EntryPoint = "GetForegroundWindow")]
    private static extern IntPtr ActivityGetForegroundWindow();
    [DllImport("user32.dll", EntryPoint = "GetWindowRect")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ActivityGetWindowRect(IntPtr hwnd, out ActivityNativeRect rect);
}
