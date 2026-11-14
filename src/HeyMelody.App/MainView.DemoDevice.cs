using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using FluentAvalonia.UI.Controls;

namespace HeyMelodyWindows.App;

public partial class MainView
{
    private static readonly TimeSpan DemoHoldDuration = TimeSpan.FromSeconds(2);
    private readonly DispatcherTimer _addDeviceHoldTimer = new();
    private IPointer? _addDeviceHoldPointer, _completedDemoHoldPointer, _cancelledAddDeviceHoldPointer;
    private long _addDeviceHoldStarted, _addDeviceGestureVersion;
    private bool _suppressAddDeviceClick, _addingDemoDevice;
    private Window? _addDeviceGestureWindow;
    private bool _demoPresentationInitialized, _presentingDemo, _restoringFixedExpansion;
    private (string Id, FASettingsExpander Control)[] _fixedExpanders = [];

    // A monotonic clock also lets the interaction tests verify a release whose
    // timer callback has not yet been dispatched.
    internal TimeProvider DemoHoldTimeProvider { get; set; } = TimeProvider.System;

    private void InitializeDemoDeviceInteraction()
    {
        _addDeviceHoldTimer.Interval = DemoHoldDuration;
        _addDeviceHoldTimer.Tick += (_, _) => EvaluateAddDeviceHold();
        AddDeviceButton.AddHandler(PointerPressedEvent, AddDevice_PointerPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddDeviceButton.AddHandler(PointerReleasedEvent, AddDevice_PointerReleased, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddDeviceButton.AddHandler(PointerMovedEvent, AddDevice_PointerMoved, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddDeviceButton.PointerExited += AddDevice_PointerExited;
        AddDeviceButton.PointerCaptureLost += (_, _) => CancelAddDeviceHold();
        AddDeviceButton.AddHandler(KeyDownEvent, (_, _) =>
        {
            // Keyboard input cancels the mouse gesture, but its later release
            // must still be suppressed if that pointer completed a demo hold.
            CancelAddDeviceHold();
            _suppressAddDeviceClick = false;
            _addDeviceGestureVersion++;
        }, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddDeviceButton.PropertyChanged += (_, e) =>
        {
            if (e.Property == IsEffectivelyEnabledProperty && !AddDeviceButton.IsEffectivelyEnabled) CancelAddDeviceHold();
        };
        AttachedToVisualTree += (_, _) =>
        {
            _addDeviceGestureWindow = TopLevel.GetTopLevel(this) as Window;
            if (_addDeviceGestureWindow is { } window) window.Deactivated += AddDevice_WindowDeactivated;
        };
        DetachedFromVisualTree += (_, _) =>
        {
            CancelAddDeviceHold();
            _suppressAddDeviceClick = false;
            _addDeviceGestureVersion++;
            if (_addDeviceGestureWindow is { } window) window.Deactivated -= AddDevice_WindowDeactivated;
            _addDeviceGestureWindow = null;
        };

        _fixedExpanders = [("anc", AncPanel), ("eq", EqualizerPanel), ("hearing", HearingPanel),
            ("findHeadphones", FindHeadphonesPanel), ("information", DeviceInfoPanel), ("log", OperationLogPanel)];
        foreach (var (id, expander) in _fixedExpanders)
            expander.PropertyChanged += (_, e) =>
            {
                if (!_restoringFixedExpansion && e.Property == FASettingsExpander.IsExpandedProperty)
                    _sectionExpansion[(_presentingDemo ? "demo/" : "real/") + "fixed:" + id] = expander.IsExpanded;
            };
    }

    private bool CanAddDemoDevice => !_modelDemo && !_closingWindow && !_openingBluetoothSettings && !_addingDemoDevice
        && TopLevel.GetTopLevel(this) is not null && AddDeviceButton.IsEffectivelyEnabled && ViewModel is { Closing: false, CanManageDevices: true };

    private void AddDevice_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var properties = e.GetCurrentPoint(AddDeviceButton).Properties;
        if (e.Pointer.Type == PointerType.Mouse && properties.PointerUpdateKind != PointerUpdateKind.LeftButtonPressed
            || !properties.IsLeftButtonPressed || !CanAddDemoDevice
            || _addDeviceHoldPointer is not null || _completedDemoHoldPointer is not null) return;
        FinishAddDeviceHold();
        _suppressAddDeviceClick = false;
        _addDeviceGestureVersion++;
        _addDeviceHoldPointer = e.Pointer;
        _addDeviceHoldStarted = DemoHoldTimeProvider.GetTimestamp();
        _addDeviceHoldTimer.Interval = DemoHoldDuration;
        _addDeviceHoldTimer.Start();
    }

    private void AddDevice_PointerMoved(object? sender, PointerEventArgs e)
    {
        if (ReferenceEquals(e.Pointer, _addDeviceHoldPointer) && !WithinAddDeviceButton(e)) CancelAddDeviceHold();
    }

    private void AddDevice_PointerExited(object? sender, PointerEventArgs e)
    {
        if (ReferenceEquals(e.Pointer, _addDeviceHoldPointer) && !WithinAddDeviceButton(e)) CancelAddDeviceHold();
    }

    private bool WithinAddDeviceButton(PointerEventArgs e) => new Rect(AddDeviceButton.Bounds.Size).Contains(e.GetPosition(AddDeviceButton));

    internal void EvaluateAddDeviceHold()
    {
        if (_addDeviceHoldPointer is null || _completedDemoHoldPointer is not null) return;
        if (!CanAddDemoDevice) { CancelAddDeviceHold(); return; }
        var elapsed = DemoHoldTimeProvider.GetElapsedTime(_addDeviceHoldStarted);
        if (elapsed < DemoHoldDuration)
        {
            _addDeviceHoldTimer.Interval = DemoHoldDuration - elapsed;
            return;
        }
        _addDeviceHoldTimer.Stop();
        _completedDemoHoldPointer = _addDeviceHoldPointer;
        if (ViewModel is { } vm) _ = AddDemoFromHoldAsync(vm);
    }

    private async Task AddDemoFromHoldAsync(MainViewModel vm)
    {
        _addingDemoDevice = true;
        try { await vm.AddDemoDeviceAsync(); }
        catch (OperationCanceledException) { }
        catch (Exception) { if (!vm.Closing) vm.ShowDemoFeedback("无法加载演示设备，请重试。"); }
        finally { _addingDemoDevice = false; }
    }

    private void AddDevice_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (e.Pointer.Type == PointerType.Mouse && e.InitialPressMouseButton != MouseButton.Left) return;
        if (ReferenceEquals(e.Pointer, _addDeviceHoldPointer) && WithinAddDeviceButton(e)) EvaluateAddDeviceHold();
        if (ReferenceEquals(e.Pointer, _completedDemoHoldPointer) || ReferenceEquals(e.Pointer, _cancelledAddDeviceHoldPointer))
        {
            _suppressAddDeviceClick = true;
            var version = _addDeviceGestureVersion;
            // Button's release handler raises Click synchronously. Clear afterward
            // if release outside its bounds prevented that Click.
            Dispatcher.UIThread.Post(() => { if (_addDeviceGestureVersion == version) _suppressAddDeviceClick = false; });
        }
        if (ReferenceEquals(e.Pointer, _addDeviceHoldPointer) || ReferenceEquals(e.Pointer, _completedDemoHoldPointer)
            || ReferenceEquals(e.Pointer, _cancelledAddDeviceHoldPointer)) FinishAddDeviceHold();
    }

    private void AddDevice_WindowDeactivated(object? sender, EventArgs e) => CancelAddDeviceHold();

    internal void CancelAddDeviceHold()
    {
        _cancelledAddDeviceHoldPointer = _addDeviceHoldPointer ?? _completedDemoHoldPointer ?? _cancelledAddDeviceHoldPointer;
        _addDeviceHoldTimer.Stop();
        _addDeviceHoldPointer = _completedDemoHoldPointer = null;
    }

    private void FinishAddDeviceHold()
    {
        _addDeviceHoldTimer.Stop();
        _addDeviceHoldPointer = _completedDemoHoldPointer = _cancelledAddDeviceHoldPointer = null;
    }

    private string ExpansionKey(string id) => (ViewModel?.IsDemoSelected == true ? "demo/" : "real/") + id;

    private void UpdateHeroVisual() => HeroArt.Visual = _modelDemo || ViewModel?.IsDemoSelected == true
        ? new EncoFree4DynaudioModelVisual() : VisualCatalog.ProductFor(ViewModel?.SelectedDevice);

    private void UpdateDemoPresentation()
    {
        var demo = ViewModel?.IsDemoSelected == true;
        UpdateHeroVisual();
        if (_demoPresentationInitialized && demo == _presentingDemo) return;
        _demoPresentationInitialized = true;
        _presentingDemo = demo;
        _restoringFixedExpansion = true;
        try
        {
            foreach (var (id, expander) in _fixedExpanders)
                expander.IsExpanded = _sectionExpansion.GetValueOrDefault((demo ? "demo/" : "real/") + "fixed:" + id, id == "anc");
        }
        finally { _restoringFixedExpansion = false; }
    }
}
