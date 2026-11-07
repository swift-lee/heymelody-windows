using System.Numerics;
using HeyMelody.Rendering.Filament;
using HeyMelody.Rendering.Tests;

/// <summary>A labelled test renderer. It never stands in for native rendering receipts.</summary>
internal sealed class RecordingFilamentRendererFactory : IFilamentRendererFactory
{
    public List<RecordingFilamentRenderer> Instances { get; } = [];
    public bool FailCreate { get; set; }
    public bool FailLoad { get; set; }
    public bool FailFrame { get; set; }
    public bool FailBackend { get; set; }
    public bool FailBackendFrame { get; set; }
    public bool FailNativeModel { get; set; }
    public ManualResetEventSlim? CreateBarrier { get; set; }
    public bool ObserveCancellation { get; set; } = true;
    public bool CreatedBeforePreviousDisposed { get; private set; }
    private int _createCalls;
    public int CreateCalls => Volatile.Read(ref _createCalls);
    public IFilamentRenderer Create(FilamentBackend backend, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _createCalls);
        CreateBarrier?.Wait(ObserveCancellation ? cancellationToken : CancellationToken.None);
        if (FailCreate) throw new DllNotFoundException("Deliberate unavailable native renderer fixture.");
        if (FailBackend) throw new FilamentRendererException("Deliberate unavailable backend fixture.",
            FilamentFailureKind.BackendUnavailable, FilamentFailureStage.Creation, backend);
        var renderer = new RecordingFilamentRenderer(this, backend);
        lock (Instances)
        {
            CreatedBeforePreviousDisposed |= Instances.Any(instance => !instance.Disposed);
            Instances.Add(renderer);
        }
        return renderer;
    }
}

internal sealed class RecordingFilamentRenderer(RecordingFilamentRendererFactory owner, FilamentBackend backend) : IFilamentRenderer
{
    private FilamentDiagnostics _diagnostics = FilamentDiagnostics.Empty;
    private FilamentRenderConfiguration _configuration = new(280, 240);
    private byte[]? _pixels;
    private FilamentFrameInfo _frame;
    public bool Disposed { get; private set; }
    public FilamentBackend Backend => backend;
    public int FrameRequests { get; private set; }
    public int CopyCalls { get; private set; }
    public int PeekCalls { get; private set; }
    public bool HoldFrameCompletion { get; set; }
    public bool DropFrameBeforeNextCopy { get; set; }
    public FilamentRenderConfiguration Configuration => _configuration;
    public void LoadModel(FilamentModelData model)
    {
        ObjectDisposedException.ThrowIf(Disposed, this);
        if (owner.FailLoad) throw new InvalidDataException("Deliberate model-load failure fixture.");
        if (owner.FailNativeModel) throw new FilamentRendererException("Deliberate native model-load failure fixture.",
            FilamentFailureKind.RuntimeFailure, FilamentFailureStage.ModelLoading, backend);
        var source = GlbSource.Inspect(model.Bytes.Span);
        _diagnostics = new(FilamentRendererState.Ready, "TestFake", 4, 0, 0, source.Meshes, source.Nodes,
            source.Primitives, source.Materials, model.MaterialProfile == "opaque-1001" ? 1 : 0, source.Triangles,
            0, 1, new Vector3(-1), new Vector3(1)) { RequestedBackend = backend, ActualBackend = backend };
    }
    public void Configure(FilamentRenderConfiguration configuration)
    {
        ObjectDisposedException.ThrowIf(Disposed, this);
        configuration.Validate();
        if (_configuration.Width != configuration.Width || _configuration.Height != configuration.Height)
        {
            _diagnostics = _diagnostics with { Generation = _diagnostics.Generation + 1 };
            _pixels = null;
        }
        _configuration = configuration;
    }
    public void RequestFrame()
    {
        ObjectDisposedException.ThrowIf(Disposed, this);
        if (owner.FailBackendFrame) throw new FilamentRendererException("Deliberate backend-frame failure fixture.",
            FilamentFailureKind.RuntimeFailure, FilamentFailureStage.Rendering, backend);
        if (owner.FailFrame) throw new IOException("Deliberate worker-frame failure fixture.");
        FrameRequests++;
        if (!HoldFrameCompletion) CompletePendingFrame();
    }
    public void CompletePendingFrame()
    {
        ObjectDisposedException.ThrowIf(Disposed, this);
        int width = _configuration.Width, height = _configuration.Height;
        _pixels = new byte[checked(width * height * 4)];
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        {
            int offset = (y * width + x) * 4;
            bool inside = x > width / 4 && x < width * 3 / 4 && y > height / 4 && y < height * 3 / 4;
            _pixels[offset] = inside ? (byte)130 : (byte)Math.Round(_configuration.ClearRed * 255);
            _pixels[offset + 1] = inside ? (byte)150 : (byte)Math.Round(_configuration.ClearGreen * 255);
            _pixels[offset + 2] = inside ? (byte)180 : (byte)Math.Round(_configuration.ClearBlue * 255);
            _pixels[offset + 3] = 255;
        }
        _diagnostics = _diagnostics with { Width = width, Height = height, FrameId = _diagnostics.FrameId + 1 };
        _frame = new(width, height, width * 4, _diagnostics.FrameId, _diagnostics.Generation, .1, .05);
    }
    public bool TryGetLatestFrameInfo(ulong afterFrameId, out FilamentFrameInfo frame)
    {
        ObjectDisposedException.ThrowIf(Disposed, this);
        PeekCalls++;
        frame = default;
        if (_pixels is null || afterFrameId >= _frame.FrameId) return false;
        frame = _frame;
        return true;
    }
    public bool TryCopyLatestFrame(ulong afterFrameId, Span<byte> destination, out FilamentFrameInfo frame)
    {
        ObjectDisposedException.ThrowIf(Disposed, this);
        CopyCalls++;
        if (DropFrameBeforeNextCopy)
        {
            DropFrameBeforeNextCopy = false;
            _pixels = null;
        }
        frame = _frame;
        if (_pixels is null || afterFrameId >= _frame.FrameId) return false;
        if (destination.Length < _pixels.Length) throw new ArgumentException("Frame destination is too small.");
        _pixels.CopyTo(destination);
        return true;
    }
    public FilamentDiagnostics GetDiagnostics() => _diagnostics;
    public void Dispose() { Disposed = true; _pixels = null; }
}
