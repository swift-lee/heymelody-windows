using System.Diagnostics;
using System.Runtime.InteropServices;
using HeyMelody.Diagnostics;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using HeyMelody.Rendering.Filament;
using HeyMelodyWindows.Artwork;
using HeyMelodyWindows.Ui;

namespace HeyMelodyWindows.App;

/// <summary>Filament renders the original GLB on its worker; Avalonia owns presentation, input and overlays.</summary>
public class DeviceModelView : UserControl
{
    private const int VerifiedModelCacheCapacity = 2;
    private static readonly VerifiedModelCache VerifiedModels = new(VerifiedModelCacheCapacity, ReadModel);
    internal static IFilamentRendererFactory? DefaultFactoryOverride { get; set; }
    private readonly DeviceModelVisual _visual;
    private readonly IFilamentRendererFactory _factory;
    private readonly Func<ArtworkFile, Task<FilamentModelData>>? _readModelOverride;
    private readonly SemaphoreSlim _lifetimeGate = new(1, 1);
    private readonly Grid _root;
    private readonly ContentControl _fallback = new() { IsVisible = false, IsHitTestVisible = false };
    private IDisposable? _resourceLease;
    private bool _resourceAvailable;
    private readonly List<Visual> _visibilityAncestors = new();
    private bool _visibilityRefreshQueued;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly TextBlock _diagnostics = new()
    {
        IsHitTestVisible = false, IsVisible = false, FontSize = 12, Margin = new Thickness(8), TextWrapping = TextWrapping.Wrap,
        HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top
    };
    // Failure and availability guidance only; loading is shown by the shared ring, never as text.
    private readonly TextBlock _status = new()
    {
        IsVisible = false, IsHitTestVisible = false,
        HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, Margin = new Thickness(16)
    };
    private readonly HeroLoadingIndicator _loadingIndicator = new() { IsVisible = false };
    private Window? _window;
    private IPointer? _pointer;
    private IFilamentRenderer? _renderer;
    private WriteableBitmap? _bitmap;
    private byte[]? _frameBuffer;
    private FilamentFrameInfo _lastFrameInfo;
    private FilamentDiagnostics _lastDiagnostics = FilamentDiagnostics.Empty;
    private FilamentRenderConfiguration _configuration;
    private bool _attached;
    private bool _loading;
    private int _loadGeneration;
    private long _attachedAt, _timingOriginAt, _modelLoadedAt;
    private bool _hasLoadSinceAttach, _firstFrameTimingPending;
    private ModelLoadTimings? _loadTimings;
    internal ModelLoadTimings? LoadTimings => _loadTimings;
    internal string ModelDigest => _visual.File.Sha256;
    internal static event Action<DeviceModelView, ModelLoadTimings>? LoadMeasured;
    private CancellationTokenSource? _loadCancellation;
    private Task _rendererRetirement = Task.CompletedTask;
    private FilamentBackend? _requestedBackend;
    private ModelOrbitMotion.SavedMotion? _motionToRestore;
    private TopLevel? _frameSource;
    private bool _loopActive;
    private int _loopGeneration;
    private TimeSpan? _lastFrame;
    private readonly ModelFramePacer _framePacer = new();
    // Idle auto-rotation steps down to 30 fps while AnimationYield is active; dragging and inertia keep _framePacer.
    private readonly ModelFramePacer _yieldPacer = new(ModelFrameRate.Fps30);
    private bool _yielding;
    // Locking a WriteableBitmap invalidates its Skia image (a new GPU upload) even if nothing is written, so the renderer
    // is polled only until a frame arrives after the last request. Requests can overlap (the frame copied first may predate
    // the last pose), so polling continues for a short grace after the last request instead of stopping at the first frame.
    private const double FramePollGraceSeconds = .25;
    private bool _frameRequested;
    private double _frameRequestedAt;
    /// <summary>Set when the renderer's frame layout cannot go straight into the bitmap; tests set it to force the managed path.</summary>
    internal bool DirectCopyDisabled { get; set; }
    private float _appliedYaw = float.NaN, _appliedPitch = float.NaN;
    private ulong _afterFrameId;
    private double _lastPresentedAt;
    private readonly Queue<double> _presentationIntervals = new();
    private long _presentedFrames;
    private double _diagnosticsAt;

    public Image Viewport { get; } = new()
    {
        Stretch = Stretch.Fill, IsVisible = false,
        HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch
    };
    public ModelOrbitMotion Motion { get; } = new();
    public bool ModelReady { get; private set; }
    internal bool LoadingIndicatorVisible => _loadingIndicator.IsVisible;
    internal string? VisibleStatus => _status.IsVisible ? _status.Text : null;
    public string? LoadError { get; private set; }
    public long CameraUpdateCount { get; private set; }
    internal long AnimationCallbackCount { get; private set; }
    public bool AnimationActive => _loopActive;
    public FilamentDiagnostics Diagnostics
    {
        get
        {
            try { return _renderer?.GetDiagnostics() ?? _lastDiagnostics; }
            catch (Exception) { return _lastDiagnostics; }
        }
    }

    public DeviceModelView(DeviceModelVisual visual, IFilamentRendererFactory? rendererFactory = null)
    {
        _visual = visual;
        _factory = rendererFactory ?? DefaultFactoryOverride ?? NativeFilamentRendererFactory.Instance;
        ClipToBounds = true;
        _root = new Grid
        {
            RowDefinitions = new RowDefinitions("*,Auto"),
            HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch,
            Children = { _fallback, Viewport, _loadingIndicator, _status, _diagnostics }
        };
        Grid.SetRowSpan(Viewport, 2);
        Grid.SetRowSpan(_loadingIndicator, 2);
        Grid.SetRowSpan(_diagnostics, 2);
        Content = _root;
        AutomationProperties.SetName(this, "耳机三维模型，可用鼠标拖动旋转");
        AutomationProperties.SetAccessibilityView(_diagnostics, AccessibilityView.Raw);
        Viewport.PropertyChanged += (_, change) => { if (change.Property == BoundsProperty) FitCamera(); };
        _root.PointerPressed += OnPointerPressed;
        _root.PointerMoved += OnPointerMoved;
        _root.AddHandler(PointerReleasedEvent, OnPointerReleased, RoutingStrategies.Tunnel, handledEventsToo: true);
        _root.PointerCaptureLost += (_, _) =>
        {
            if (_pointer is null) return;
            _pointer = null;
            Motion.CancelDrag();
        };
        ActualThemeVariantChanged += (_, _) => ApplyBackground();
        ApplyBackground();
        ApplyRenderingPreference();
    }

    internal DeviceModelView(DeviceModelVisual visual, IFilamentRendererFactory rendererFactory,
        Func<ArtworkFile, Task<FilamentModelData>> readModel) : this(visual, rendererFactory)
        => _readModelOverride = readModel;

    /// <summary>Validates the original GLB without loading the native renderer.</summary>
    internal static Task<FilamentModelData> LoadModelAsync(DeviceModelVisual visual)
        => VerifiedModels.GetAsync(visual.File, out _);

    internal static void PrefetchModel(DeviceModelVisual visual)
    {
        // GetAsync acquires the package reference before queuing a miss; a cache hit does not read the file.
        try { _ = ObservePrefetchAsync(LoadModelAsync(visual)); }
        catch (Exception error) { DiagnosticLog.Warning("Model.Prefetch", "readStartFailed", "Model prefetch could not be started.", error); }
        if ((DefaultFactoryOverride ?? NativeFilamentRendererFactory.Instance) is NativeFilamentRendererFactory native)
            try { _ = ObservePrefetchAsync(native.PrepareBackendAsync(ModelRenderingPreferences.Backend)); }
            catch (Exception error) { DiagnosticLog.Warning("Model.Prefetch", "backendStartFailed", "Backend preparation could not be started; the actual load remains retryable.", error); }
    }

    private static async Task ObservePrefetchAsync(Task task)
    {
        try { await task.ConfigureAwait(false); }
        catch (Exception error) { DiagnosticLog.Warning("Model.Prefetch", "failed", "Prefetch failed; the active view retains its existing failure handling.", error); }
    }

    private static FilamentModelData ReadModel(ArtworkFile file)
    {
        var expected = file.Sha256?.ToLowerInvariant();
        DiagnosticLog.Debug("Model.Read", "started", "Reading the model for digest and structure validation.", new { expectedSha256 = expected });
        using var stream = file.Open();
        if (stream.Length is <= 0 or > FilamentModelData.MaximumBytes)
            throw new InvalidDataException("Model size exceeds its limit.");
        var bytes = new byte[checked((int)stream.Length)];
        stream.ReadExactly(bytes);
        var model = FilamentModelData.CreateValidated(bytes, string.IsNullOrEmpty(expected) ? null : expected);
        DiagnosticLog.Info("Model.Read", "verified", "Model digest and structure validation completed.",
            new { expectedSha256 = expected, model.Sha256, bytes = bytes.Length });
        return model;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _attachedAt = Stopwatch.GetTimestamp();
        _hasLoadSinceAttach = false;
        _attached = true;
        ModelRenderingPreferences.Changed += OnRenderingPreferenceChanged;
        RenderDiagnostics.Changed += OnDiagnosticsChanged;
        try { _resourceLease = _visual.File.Acquire(); _resourceAvailable = true; }
        catch (ObjectDisposedException) { ShowFallback("三维预览暂不可用"); return; }
        _window = TopLevel.GetTopLevel(this) as Window;
        if (_window is not null)
        {
            _window.PropertyChanged += OnWindowPropertyChanged;
            _window.ScalingChanged += OnScalingChanged;
        }
        foreach (var ancestor in this.GetVisualAncestors())
        {
            ancestor.PropertyChanged += OnAncestorPropertyChanged;
            _visibilityAncestors.Add(ancestor);
        }
        ApplyRenderingPreference();
        UpdateDiagnostics();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        ModelRenderingPreferences.Changed -= OnRenderingPreferenceChanged;
        RenderDiagnostics.Changed -= OnDiagnosticsChanged;
        _attached = false;
        _resourceAvailable = false;
        _motionToRestore = null;
        ++_loadGeneration;
        _loadCancellation?.Cancel();
        _loading = false;
        if (_window is not null)
        {
            _window.PropertyChanged -= OnWindowPropertyChanged;
            _window.ScalingChanged -= OnScalingChanged;
        }
        _window = null;
        foreach (var ancestor in _visibilityAncestors) ancestor.PropertyChanged -= OnAncestorPropertyChanged;
        _visibilityAncestors.Clear();
        ReleasePointer();
        RetireRenderer();
        Interlocked.Exchange(ref _resourceLease, null)?.Dispose();
        base.OnDetachedFromVisualTree(e);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsVisibleProperty || change.Property.Name == "IsEffectivelyVisible") RefreshActivity();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var requested = new Size(Math.Min(UiMetrics.WideHeroMaxWidth, availableSize.Width),
            Math.Min(UiMetrics.WideHeroMaxHeight, availableSize.Height));
        base.MeasureOverride(requested);
        return requested;
    }

    private async Task LoadAsync(int generation, FilamentBackend backend, CancellationTokenSource cancellation, Task retirement)
    {
        using var operation = DiagnosticLog.BeginOperation();
        DiagnosticLog.Debug("Model.View", "loadStarted", "Starting a model view load generation.", new { generation, backend, modelSha256 = ModelDigest });
        var started = Stopwatch.GetTimestamp();
        var origin = _hasLoadSinceAttach ? started : _attachedAt;
        _hasLoadSinceAttach = true;
        _firstFrameTimingPending = false;
        _loadTimings = null;
        var timings = new ModelLoadTimings(generation, backend,
            Stopwatch.GetElapsedTime(origin, started).TotalMilliseconds, 0, 0, 0, false, 0, 0, 0, 0, 0, 0,
            FilamentProbeDisposition.Cached);
        IFilamentRenderer? renderer = null;
        IDisposable? loadingLease = null;
        Task<FilamentModelData>? modelTask = null;
        long readStarted = 0, readFinishedAt = 0, modelLoadedAt = 0;
        async Task<FilamentModelData> MeasureReadAsync(Task<FilamentModelData> pending)
        {
            try { return await pending.ConfigureAwait(false); }
            finally { readFinishedAt = Stopwatch.GetTimestamp(); }
        }
        try
        {
            loadingLease = _visual.File.Acquire();
            readStarted = Stopwatch.GetTimestamp();
            bool cacheHit = false;
            modelTask = MeasureReadAsync(_readModelOverride is null
                ? VerifiedModels.GetAsync(_visual.File, out cacheHit) : _readModelOverride(_visual.File));
            DiagnosticLog.Debug("Model.View", "cacheDecision", "Model reader selected.",
                new { generation, backend, cacheHit, readerOverride = _readModelOverride is not null, modelSha256 = ModelDigest });
            renderer = await Task.Run(async () =>
            {
                // Never create the replacement engine while the old engine is still draining readbacks.
                var stage = Stopwatch.GetTimestamp();
                await retirement.ConfigureAwait(false);
                timings = timings with { RetirementWaitMilliseconds = Stopwatch.GetElapsedTime(stage).TotalMilliseconds };
                stage = Stopwatch.GetTimestamp();
                await _lifetimeGate.WaitAsync(cancellation.Token).ConfigureAwait(false);
                timings = timings with { GateWaitMilliseconds = Stopwatch.GetElapsedTime(stage).TotalMilliseconds };
                try
                {
                    if (!IsCurrentLoad(generation)) return null;
                    // Observe an already failed read before allocating an engine. A later failure disposes it below.
                    if (modelTask.IsCompleted) await modelTask.ConfigureAwait(false);
                    if (!IsCurrentLoad(generation)) return null;
                    cancellation.Token.ThrowIfCancellationRequested();
                    var created = _factory.Create(backend, cancellation.Token);
                    try
                    {
                        var model = await modelTask.ConfigureAwait(false);
                        timings = timings with
                        {
                            ReadVerifyMilliseconds = Stopwatch.GetElapsedTime(readStarted, readFinishedAt).TotalMilliseconds,
                            CacheHit = cacheHit
                        };
                        if (!IsCurrentLoad(generation) || cancellation.IsCancellationRequested) { created.Dispose(); return null; }
                        stage = Stopwatch.GetTimestamp();
                        created.LoadModel(model);
                        modelLoadedAt = Stopwatch.GetTimestamp();
                        timings = timings with { ModelLoadMilliseconds = Stopwatch.GetElapsedTime(stage, modelLoadedAt).TotalMilliseconds };
                    }
                    catch { created.Dispose(); throw; }
                    if (IsCurrentLoad(generation)) return created;
                    created.Dispose();
                    return null;
                }
                finally { _lifetimeGate.Release(); }
            });
            if (!IsCurrentLoad(generation) || renderer is null) return;
            _renderer = renderer;
            renderer = null;
            _loading = false;
            LoadError = null;
            ModelReady = true;
            _lastDiagnostics = _renderer.GetDiagnostics();
            if (_lastDiagnostics.CreationTimings is { } creation)
                timings = timings with
                {
                    RuntimePreparationMilliseconds = creation.RuntimePreparationMilliseconds,
                    ProbeWaitMilliseconds = creation.ProbeWaitMilliseconds,
                    EngineCreateMilliseconds = creation.EngineCreateMilliseconds,
                    ProbeDisposition = creation.ProbeDisposition
                };
            _timingOriginAt = origin;
            _modelLoadedAt = modelLoadedAt;
            _loadTimings = timings;
            DiagnosticLog.Info("Model.View", "ready", "The selected renderer loaded the verified model; waiting for its first frame.",
                new { generation, backend, modelSha256 = ModelDigest, timings, diagnostics = _lastDiagnostics });
            _firstFrameTimingPending = true;
            _afterFrameId = 0;
            MarkFrameRequested();
            DirectCopyDisabled = false;
            _presentedFrames = 0;
            _lastPresentedAt = 0;
            _presentationIntervals.Clear();
            _fallback.IsVisible = false;
            Viewport.IsVisible = true;
            // The ring stays until the first frame is presented.
            _status.IsVisible = false;
            _loadingIndicator.IsVisible = true;
            FitCamera();
            RefreshActivity();
            if (_motionToRestore is { } motion)
            {
                Motion.RestoreMotion(motion);
                _motionToRestore = null;
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            DiagnosticLog.Debug("Model.View", "loadCancelled", "The model generation was cancelled.", new { generation, backend, modelSha256 = ModelDigest });
        }
        catch (Exception exception)
        {
            DiagnosticLog.Error("Model.View", "loadFailed", "A model load stage failed.", exception,
                new { generation, backend, modelSha256 = ModelDigest, currentGeneration = IsCurrentLoad(generation) });
            if (!IsCurrentLoad(generation)) return;
            // Preserve the serial loader's read-before-backend error priority when both fail.
            var failure = exception;
            if (modelTask is not null)
                try { await modelTask; }
                catch (Exception readFailure) { failure = readFailure; }
            if (!IsCurrentLoad(generation)) return;
            _loading = false;
            FailRenderer(failure);
        }
        finally
        {
            // Keep the load's lease until the read has actually finished, including cancelled/stale loads.
            if (modelTask is not null)
                try { await modelTask; }
                catch (Exception) { /* The active load reports it above; abandoned shared work is still observed. */ }
            loadingLease?.Dispose();
            if (renderer is not null) QueueRendererDisposal(renderer);
            if (ReferenceEquals(_loadCancellation, cancellation)) _loadCancellation = null;
            cancellation.Dispose();
        }
    }

    // Worker-side checks dispose cancelled loads even when the window's dispatcher has already stopped.
    private bool IsCurrentLoad(int generation) => Volatile.Read(ref _attached)
        && generation == Volatile.Read(ref _loadGeneration);

    private void OnRenderingPreferenceChanged(object? sender, EventArgs e) => ApplyRenderingPreference();
    private void ApplyRenderingPreference()
    {
        if (_framePacer.FrameRate != ModelRenderingPreferences.FrameRate)
            _framePacer.Reset(ModelRenderingPreferences.FrameRate);
        var backend = ModelRenderingPreferences.Backend;
        if (_requestedBackend != backend)
        {
            if (ModelReady && Motion.IsActive) _motionToRestore = Motion.CaptureMotion();
            _requestedBackend = backend;
            ++_loadGeneration;
            _loadCancellation?.Cancel();
            _loading = false;
            RetireRenderer();
            LoadError = null;
        }
        if (_attached && _renderer is null && !_loading && _resourceAvailable)
        {
            _loading = true;
            _fallback.IsVisible = false;
            _status.IsVisible = false;
            _loadingIndicator.IsVisible = true;
            var cancellation = _loadCancellation = new CancellationTokenSource();
            _ = LoadAsync(++_loadGeneration, backend, cancellation, _rendererRetirement);
        }
        RefreshActivity();
    }

    private void ShowFallback(string? status)
    {
        // Reserve a separate footer for failure guidance so it cannot cover the static artwork.
        Grid.SetRow(_status, 1);
        _fallback.Content ??= DeviceVisuals.DefaultTemplate.Build(_visual.Fallback) ?? DeviceVisuals.DefaultTemplate.Build(DeviceVisuals.GenericProduct);
        _fallback.IsVisible = true;
        Viewport.IsVisible = false;
        _loadingIndicator.IsVisible = false;
        _status.Text = status;
        _status.IsVisible = status is not null;
        ModelReady = false;
        RefreshActivity();
    }

    private void RetireRenderer()
    {
        var retired = _renderer;
        _renderer = null;
        ModelReady = false;
        RefreshActivity();
        Viewport.Source = null;
        _bitmap?.Dispose();
        _bitmap = null;
        _frameBuffer = null;
        _lastFrameInfo = default;
        if (retired is not null) QueueRendererDisposal(retired);
    }

    private void QueueRendererDisposal(IFilamentRenderer renderer)
    {
        var previous = _rendererRetirement;
        _rendererRetirement = Task.Run(async () =>
        {
            await previous.ConfigureAwait(false);
            await DisposeRendererAsync(renderer).ConfigureAwait(false);
        });
    }

    private Task DisposeRendererAsync(IFilamentRenderer renderer) => Task.Run(async () =>
    {
        await _lifetimeGate.WaitAsync().ConfigureAwait(false);
        try { renderer.Dispose(); }
        catch (Exception exception)
        {
            DiagnosticLog.Error("Model.View", "disposeFailed", "Native renderer disposal failed.", exception, new { modelSha256 = ModelDigest });
            Trace.WriteLine(exception);
        }
        finally { _lifetimeGate.Release(); }
    });

    private void FailRenderer(Exception exception)
    {
        _motionToRestore = null;
        LoadError = exception.ToString();
        DiagnosticLog.Error("Model.View", "rendererFailed", "The model renderer failed; the existing fallback presentation is used.", exception,
            new { modelSha256 = ModelDigest });
        Trace.WriteLine(LoadError);
        RetireRenderer();
        var message = exception switch
        {
            FilamentRendererException { Kind: FilamentFailureKind.BackendUnavailable, Backend: FilamentBackend.Vulkan }
                or FilamentRendererException { Kind: FilamentFailureKind.RuntimeFailure, Stage: FilamentFailureStage.Rendering,
                    Backend: FilamentBackend.Vulkan }
                => "Vulkan 三维预览不可用，可在应用设置的“3D模型渲染API”中选择 OpenGL。",
            FilamentRendererException { Kind: FilamentFailureKind.ComponentUnavailable }
                or DllNotFoundException or BadImageFormatException or EntryPointNotFoundException => "三维渲染组件不可用",
            FilamentRendererException { Kind: FilamentFailureKind.InvalidModel } or InvalidDataException => "三维模型资源无效",
            FilamentRendererException { Stage: FilamentFailureStage.ModelLoading } => "三维模型资源加载失败",
            _ => "三维预览暂不可用"
        };
        ShowFallback(message);
    }

    private void ApplyBackground()
    {
        var color = ActualThemeVariant == ThemeVariant.Dark ? Color.FromRgb(32, 32, 32) : Color.FromRgb(243, 243, 243);
        _root.Background = new SolidColorBrush(color);
        _configuration = _configuration with { ClearRed = color.R / 255f, ClearGreen = color.G / 255f, ClearBlue = color.B / 255f };
        ApplyCamera(force: true);
    }

    private void FitCamera()
    {
        var source = TopLevel.GetTopLevel(this);
        var size = Viewport.Bounds.Size;
        // An Image has no intrinsic size until its first frame. The host already has its layout allocation.
        if (size.Width <= 0 || size.Height <= 0) size = _root.Bounds.Size;
        if (_renderer is null || source is null || size.Width <= 0 || size.Height <= 0) return;
        var scaling = source.RenderScaling;
        var width = checked((int)Math.Ceiling(size.Width * scaling));
        var height = checked((int)Math.Ceiling(size.Height * scaling));
        if (width != _configuration.Width || height != _configuration.Height) _lastFrameInfo = default;
        _configuration = _configuration with
        {
            Width = width, Height = height, Zoom = 1, VerticalFovDegrees = 45,
            Exposure = 1, LightIntensity = 100000, MsaaSamples = 4,
            FitMargin = 1.08f
        };
        _appliedYaw = _appliedPitch = float.NaN;
        ApplyCamera(force: true);
    }

    private void OnWindowPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == Window.WindowStateProperty || e.Property == IsVisibleProperty) RefreshActivity();
    }
    private void OnScalingChanged(object? sender, EventArgs e) => FitCamera();

    private void OnAncestorPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property != IsVisibleProperty) return;
        RefreshActivity();
        if (_visibilityRefreshQueued) return;
        _visibilityRefreshQueued = true;
        // Allow Avalonia to propagate the ancestor visibility before deciding whether to resume.
        Dispatcher.UIThread.Post(() =>
        {
            _visibilityRefreshQueued = false;
            if (_attached) RefreshActivity();
        }, DispatcherPriority.Render);
    }

    private void RefreshActivity()
    {
        bool visible = _attached && IsEffectivelyVisible && _window?.IsVisible == true && _window.WindowState != WindowState.Minimized;
        if (!visible) _motionToRestore = null;
        var active = visible && ModelReady && _renderer is not null;
        Motion.SetActive(active);
        if (!active) { StopFrameLoop(); ReleasePointer(); return; }
        if (_loopActive || TopLevel.GetTopLevel(this) is not { } source) return;
        _frameSource = source;
        _loopActive = true;
        _lastFrame = null;
        _framePacer.Reset(ModelRenderingPreferences.FrameRate);
        RequestFrame(++_loopGeneration);
    }

    private void StopFrameLoop()
    {
        _loopActive = false;
        _frameSource = null;
        _framePacer.Reset(ModelRenderingPreferences.FrameRate);
        ++_loopGeneration;
    }
    private void RequestFrame(int generation) => _frameSource?.RequestAnimationFrame(time => OnFrame(time, generation));
    private void OnFrame(TimeSpan time, int generation)
    {
        if (generation != _loopGeneration || !_loopActive) return;
        if (!IsEffectivelyVisible) { RefreshActivity(); return; }
        ++AnimationCallbackCount;
        try
        {
            var dt = _lastFrame is { } last ? Math.Clamp((time - last).TotalSeconds, 0, .1) : 0;
            _lastFrame = time;
            Motion.Step(dt);
            bool yielding = AnimationYield.Active && Motion.Phase == ModelOrbitPhase.Idle;
            if (yielding != _yielding) { _yielding = yielding; _yieldPacer.Reset(ModelFrameRate.Fps30); }
            if ((yielding ? _yieldPacer : _framePacer).ShouldRender(time)) ApplyCamera();
            PresentLatestFrame();
        }
        catch (Exception exception) { FailRenderer(exception); return; }
        RequestFrame(generation);
    }

    private void ApplyCamera(bool force = false)
    {
        if (_renderer is null || _configuration.Width <= 0 || _configuration.Height <= 0) return;
        var yaw = (float)Motion.Yaw;
        var pitch = (float)Motion.Pitch;
        if (!force && yaw == _appliedYaw && pitch == _appliedPitch) return;
        _appliedYaw = yaw;
        _appliedPitch = pitch;
        _configuration = _configuration with { YawRadians = yaw, PitchRadians = pitch };
        try
        {
            _renderer.Configure(_configuration);
            _renderer.RequestFrame();
            MarkFrameRequested();
            ++CameraUpdateCount;
        }
        catch (Exception exception) { FailRenderer(exception); }
    }

    internal int BitmapLockCount { get; private set; }
    internal int DirectFrameCopyCount { get; private set; }
    internal int FallbackFrameCopyCount { get; private set; }

    private void MarkFrameRequested() { _frameRequested = true; _frameRequestedAt = _clock.Elapsed.TotalSeconds; }

    private void PresentLatestFrame()
    {
        if (_renderer is null || _configuration.Width <= 0 || _configuration.Height <= 0) return;
        if (!_frameRequested && _clock.Elapsed.TotalSeconds - _frameRequestedAt >= FramePollGraceSeconds) return;
        FilamentFrameInfo frame;
        switch (TryCopyDirect(out frame))
        {
            case DirectCopy.NoFrame: return;
            case DirectCopy.Presented: break;
            default:
                if (!CopyThroughBuffer(out frame)) return;
                break;
        }
        _afterFrameId = frame.FrameId;
        if (frame.Width != _configuration.Width || frame.Height != _configuration.Height) { _lastFrameInfo = default; return; }
        _frameRequested = false;
        _lastFrameInfo = frame;
        _loadingIndicator.IsVisible = false;
        Viewport.InvalidateVisual();
        CompleteLoadTiming();
        var now = _clock.Elapsed.TotalSeconds;
        if (_lastPresentedAt > 0)
        {
            _presentationIntervals.Enqueue((now - _lastPresentedAt) * 1000);
            while (_presentationIntervals.Count > 120) _presentationIntervals.Dequeue();
        }
        _lastPresentedAt = now;
        ++_presentedFrames;
        if (now - _diagnosticsAt >= .5) { _diagnosticsAt = now; _lastDiagnostics = _renderer.GetDiagnostics(); UpdateDiagnostics(); }
    }

    private enum DirectCopy { NoFrame, Presented, Unavailable }

    private void CompleteLoadTiming()
    {
        if (!_firstFrameTimingPending || _loadTimings is not { } timings || timings.Generation != _loadGeneration) return;
        _firstFrameTimingPending = false;
        var now = Stopwatch.GetTimestamp();
        timings = timings with
        {
            FirstFrameMilliseconds = Stopwatch.GetElapsedTime(_modelLoadedAt, now).TotalMilliseconds,
            TotalMilliseconds = Stopwatch.GetElapsedTime(_timingOriginAt, now).TotalMilliseconds
        };
        _loadTimings = timings;
        DiagnosticLog.Info("Model.View", "firstFrame", "The model's first frame was presented.", new { modelSha256 = ModelDigest, timings });
        if (RenderDiagnostics.Enabled)
            Trace.WriteLine(FormattableString.Invariant($"model_load generation={timings.Generation} backend={timings.Backend} total_ms={timings.TotalMilliseconds:F3} attach_ms={timings.AttachToLoadStartMilliseconds:F3} retirement_ms={timings.RetirementWaitMilliseconds:F3} gate_ms={timings.GateWaitMilliseconds:F3} read_ms={timings.ReadVerifyMilliseconds:F3} cache_hit={timings.CacheHit} runtime_ms={timings.RuntimePreparationMilliseconds:F3} probe={timings.ProbeDisposition} probe_ms={timings.ProbeWaitMilliseconds:F3} engine_ms={timings.EngineCreateMilliseconds:F3} model_ms={timings.ModelLoadMilliseconds:F3} first_frame_ms={timings.FirstFrameMilliseconds:F3}"));
        LoadMeasured?.Invoke(this, timings);
        UpdateDiagnostics();
    }

    /// <summary>Copies the renderer's frame straight into the locked bitmap when it already has the frame's size and stride.</summary>
    private unsafe DirectCopy TryCopyDirect(out FilamentFrameInfo frame)
    {
        frame = default;
        int width = _configuration.Width, height = _configuration.Height;
        if (DirectCopyDisabled || _renderer is null || _bitmap is null || _bitmap.PixelSize.Width != width || _bitmap.PixelSize.Height != height) return DirectCopy.Unavailable;
        ++BitmapLockCount;
        using var buffer = _bitmap.Lock();
        if (buffer.RowBytes != width * 4) return DirectCopy.Unavailable;
        var destination = new Span<byte>((void*)buffer.Address, buffer.RowBytes * height);
        try
        {
            if (!_renderer.TryCopyLatestFrame(_afterFrameId, destination, out frame)) return DirectCopy.NoFrame;
        }
        // A renderer whose stride exceeds the bitmap's rejects the smaller destination; the managed path takes over for good.
        catch (Exception) { DirectCopyDisabled = true; return DirectCopy.Unavailable; }
        // A frame of another size is not laid out like the bitmap; the caller discards it.
        if (frame.Width != width || frame.Height != height) return DirectCopy.Presented;
        if (frame.RowBytes != buffer.RowBytes) { DirectCopyDisabled = true; return DirectCopy.Unavailable; }
        ++DirectFrameCopyCount;
        return DirectCopy.Presented;
    }

    /// <summary>Managed fallback: renderer to _frameBuffer, then row by row into a (re)created bitmap.</summary>
    private bool CopyThroughBuffer(out FilamentFrameInfo frame)
    {
        var required = checked(_configuration.Width * _configuration.Height * 4);
        if (_frameBuffer is null || _frameBuffer.Length < required) _frameBuffer = new byte[required];
        if (!_renderer!.TryCopyLatestFrame(_afterFrameId, _frameBuffer, out frame)) return false;
        if (frame.Width != _configuration.Width || frame.Height != _configuration.Height) return true;
        if (frame.RowBytes < frame.Width * 4 || (long)frame.RowBytes * frame.Height > _frameBuffer.Length)
            throw new InvalidDataException("The renderer returned an invalid frame stride.");
        if (_bitmap is null || _bitmap.PixelSize.Width != frame.Width || _bitmap.PixelSize.Height != frame.Height)
        {
            Viewport.Source = null;
            _bitmap?.Dispose();
            _bitmap = new WriteableBitmap(new PixelSize(frame.Width, frame.Height), new Vector(96, 96),
                PixelFormats.Rgba8888, AlphaFormat.Opaque);
            Viewport.Source = _bitmap;
        }
        ++BitmapLockCount; ++FallbackFrameCopyCount;
        using (var buffer = _bitmap.Lock())
            for (var y = 0; y < frame.Height; y++)
                Marshal.Copy(_frameBuffer, y * frame.RowBytes, buffer.Address + y * buffer.RowBytes, frame.Width * 4);
        return true;
    }

    /// <summary>Copies the last frame presented by Avalonia, for focused native-render validation.</summary>
    public byte[]? CaptureLatestFrame(out FilamentFrameInfo frame)
    {
        frame = _lastFrameInfo;
        if (frame.FrameId == 0 || _bitmap is null) return null;
        // Rows are tightly packed in the bitmap; the result uses the renderer's reported stride like the managed copy did.
        var result = new byte[checked(frame.RowBytes * frame.Height)];
        using var buffer = _bitmap.Lock();
        for (var y = 0; y < frame.Height; y++)
            Marshal.Copy(buffer.Address + y * buffer.RowBytes, result, y * frame.RowBytes, frame.Width * 4);
        return result;
    }

    private void OnDiagnosticsChanged(object? sender, EventArgs e) => UpdateDiagnostics();
    private void UpdateDiagnostics()
    {
        _diagnostics.IsVisible = RenderDiagnostics.Enabled;
        if (!_diagnostics.IsVisible) return;
        var intervals = _presentationIntervals.Order().ToArray();
        var fps = intervals.Length == 0 ? 0 : 1000 / intervals.Average();
        var p95 = intervals.Length == 0 ? 0 : intervals[Math.Min(intervals.Length - 1, (int)Math.Ceiling(intervals.Length * .95) - 1)];
        _diagnostics.Text = $"3D {fps:0} fps · P95 {p95:0.0} ms · 渲染 {_lastFrameInfo.RenderMilliseconds:0.0} ms"
            + $" · 回读 {_lastFrameInfo.ReadbackMilliseconds:0.0} ms · {_lastDiagnostics.Backend} · MSAA {_lastDiagnostics.MsaaSamples}×"
            + $" · 已呈现 {_presentedFrames} 帧";
        if (_loadTimings is { } loading)
            _diagnostics.Text += $"\n加载 {loading.TotalMilliseconds:0} ms（探测 {loading.ProbeWaitMilliseconds:0}／引擎 {loading.EngineCreateMilliseconds:0}／模型 {loading.ModelLoadMilliseconds:0}／首帧 {loading.FirstFrameMilliseconds:0}）";
    }

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!ModelReady || !Motion.IsActive || !e.GetCurrentPoint(Viewport).Properties.IsLeftButtonPressed) return;
        var point = e.GetPosition(Viewport);
        _pointer = e.Pointer;
        Motion.BeginDrag(point.X, point.Y, _clock.Elapsed.TotalSeconds);
        e.Pointer.Capture(_root);
        e.Handled = true;
    }
    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_pointer != e.Pointer) return;
        var point = e.GetPosition(Viewport);
        Motion.MoveDrag(point.X, point.Y, _clock.Elapsed.TotalSeconds);
        e.Handled = true;
    }
    private void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_pointer != e.Pointer) return;
        Motion.EndDrag(_clock.Elapsed.TotalSeconds);
        ReleasePointer();
        e.Handled = true;
    }
    private void ReleasePointer()
    {
        var pointer = _pointer;
        _pointer = null;
        pointer?.Capture(null);
        Motion.CancelDrag();
    }
}

/// <summary>Compatibility viewport for the separately selectable original Free4 preview.</summary>
public sealed class EncoFree4ModelView : DeviceModelView
{
    public const string ModelSha256 = "c49540d3e5afd7af8bb5aa562d3dc00e35246361e6efc14fa142a749b7d22bcc";
    private static readonly DeviceModelVisual Bundled = new(new ArtworkFile(
        new Uri("avares://HeyMelody/Assets/Devices/EncoFree4/model.glb"), "", ModelSha256), DeviceVisuals.GenericProduct);
    public EncoFree4ModelView() : base(Bundled) { }
    public static Task<FilamentModelData> LoadModelAsync() => DeviceModelView.LoadModelAsync(Bundled);
}
