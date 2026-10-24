using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using HeyMelody.Diagnostics;

namespace HeyMelody.Rendering.Filament;

/// <summary>Loads the native runtime on demand when a model backend is requested.</summary>
public sealed class NativeFilamentRendererFactory : IFilamentRendererFactory
{
    public static NativeFilamentRendererFactory Instance { get; } = new();
    public IFilamentRenderer Create(FilamentBackend backend, CancellationToken cancellationToken = default)
    {
        FilamentBackendPolicy.Validate(backend);
        cancellationToken.ThrowIfCancellationRequested();
        using var operation = DiagnosticLog.BeginOperation(DiagnosticLog.CurrentCorrelationId);
        DiagnosticLog.Debug("Filament.Renderer", "createRequested", "Creating the explicitly selected model renderer.", new { backend });
        var runtimeStarted = Stopwatch.GetTimestamp();
        var (api, payload) = PrepareRuntime(backend);
        var runtimeMilliseconds = Stopwatch.GetElapsedTime(runtimeStarted).TotalMilliseconds;
        var probe = NativeBackendProbe.Shared.EnsureAvailable(payload, backend, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var engineStarted = Stopwatch.GetTimestamp();
            var renderer = new NativeFilamentRenderer(api, payload, backend)
            {
                CreationTimings = new(runtimeMilliseconds, probe.WaitMilliseconds,
                    Stopwatch.GetElapsedTime(engineStarted).TotalMilliseconds, probe.Disposition)
            };
            if (cancellationToken.IsCancellationRequested)
            {
                renderer.Dispose();
                cancellationToken.ThrowIfCancellationRequested();
            }
            DiagnosticLog.Info("Filament.Renderer", "created", "Native model renderer created.",
                new { backend, timings = renderer.CreationTimings });
            return renderer;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error)
        {
            DiagnosticLog.Error("Filament.Renderer", "createFailed", "The selected native renderer failed; no backend is substituted.", error, new { backend });
            NativeBackendProbe.Shared.Invalidate(payload, backend);
            throw;
        }
    }

    /// <summary>Checks the runtime and shares its backend probe without creating an in-process Engine.</summary>
    public async Task PrepareBackendAsync(FilamentBackend backend, CancellationToken cancellationToken = default)
    {
        FilamentBackendPolicy.Validate(backend);
        cancellationToken.ThrowIfCancellationRequested();
        var prepared = await Task.Run(() => PrepareRuntime(backend), cancellationToken).ConfigureAwait(false);
        await NativeBackendProbe.Shared.EnsureAvailableAsync(prepared.Payload, backend, cancellationToken).ConfigureAwait(false);
    }

    private static (NativeBindings Api, NativeProbePayload Payload) PrepareRuntime(FilamentBackend backend)
    {
        try
        {
            // Recheck the current payload at both preparation and Create. A previous preparation
            // cannot vouch for runtime files replaced between these two entry points.
            var api = NativeBindings.Instance;
            return (api, api.GetProbePayload());
        }
        catch (Exception error) when (error is DllNotFoundException or BadImageFormatException or EntryPointNotFoundException
            or PlatformNotSupportedException or IOException or UnauthorizedAccessException)
        {
            DiagnosticLog.Error("Filament.Runtime", "unavailable", "The packaged native runtime could not be prepared.", error, new { backend });
            throw new FilamentRendererException("The packaged Filament rendering components are missing or incompatible.",
                FilamentFailureKind.ComponentUnavailable, FilamentFailureStage.RuntimeLoading, backend, innerException: error);
        }
    }
}

internal sealed unsafe class NativeFilamentRenderer : IFilamentRenderer
{
    private readonly object _gate = new();
    private readonly NativeBindings _api;
    private readonly RendererHandle _handle;
    private readonly NativeProbePayload _payload;
    private readonly FilamentBackend _backend;
    private bool _disposed;
    internal FilamentCreationTimings? CreationTimings { get; init; }

    internal NativeFilamentRenderer(NativeBindings api, NativeProbePayload payload, FilamentBackend backend)
    {
        _api = api;
        _payload = payload;
        _backend = backend;
        var options = new NativeCreateOptions
        {
            StructSize = (uint)Marshal.SizeOf<NativeCreateOptions>(), Width = 1, Height = 1, RequestedBackend = (uint)backend
        };
        var result = api.Create(in options, out var handle);
        if (result < 0 || handle == 0)
        {
            if (handle != 0) api.Destroy(handle);
            throw new FilamentRendererException(api.Error(0), FilamentFailureKind.BackendUnavailable,
                FilamentFailureStage.Creation, backend, result);
        }
        _handle = new RendererHandle(handle, api.Destroy);
        try { _ = GetDiagnostics(); }
        catch { _handle.Dispose(); throw; }
    }

    public void LoadModel(FilamentModelData model)
    {
        ArgumentNullException.ThrowIfNull(model);
        DiagnosticLog.Debug("Filament.Renderer", "modelLoadStarted", "Loading the verified model into the native renderer.",
            new { backend = _backend, model.Sha256, bytes = model.Bytes.Length });
        lock (_gate)
        {
            ThrowIfDisposed();
            using var pin = model.Bytes.Pin();
            Check(_api.LoadModel(_handle, (nint)pin.Pointer, (ulong)model.Bytes.Length, model.Sha256), FilamentFailureStage.ModelLoading);
        }
        DiagnosticLog.Info("Filament.Renderer", "modelLoaded", "Native model loading completed.",
            new { backend = _backend, model.Sha256, bytes = model.Bytes.Length });
    }

    public void Configure(FilamentRenderConfiguration configuration)
    {
        configuration.Validate();
        var value = new NativeConfiguration
        {
            StructSize = (uint)Marshal.SizeOf<NativeConfiguration>(), Width = (uint)configuration.Width,
            Height = (uint)configuration.Height, MsaaSamples = (uint)configuration.MsaaSamples,
            Yaw = configuration.YawRadians, Pitch = configuration.PitchRadians, Zoom = configuration.Zoom,
            Fov = configuration.VerticalFovDegrees, ClearRed = configuration.ClearRed,
            ClearGreen = configuration.ClearGreen, ClearBlue = configuration.ClearBlue,
            Exposure = configuration.Exposure, LightIntensity = configuration.LightIntensity,
            FitMargin = configuration.FitMargin
        };
        lock (_gate) { ThrowIfDisposed(); Check(_api.Configure(_handle, in value)); }
    }

    public void RequestFrame()
    {
        lock (_gate) { ThrowIfDisposed(); Check(_api.Render(_handle)); }
    }

    public bool TryCopyLatestFrame(ulong afterFrameId, Span<byte> destination, out FilamentFrameInfo frame)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            var info = new NativeFrameInfo { StructSize = (uint)Marshal.SizeOf<NativeFrameInfo>() };
            int result;
            fixed (byte* buffer = destination)
                result = _api.TryCopy(_handle, afterFrameId, (nint)buffer, (ulong)destination.Length, ref info);
            Check(result);
            frame = new((int)info.Width, (int)info.Height, (int)info.StrideBytes, info.FrameId,
                info.Generation, info.RenderMilliseconds, info.ReadbackMilliseconds);
            if (result == 0) return false;
            if (info.Width == 0 || info.Height == 0 || info.StrideBytes < (ulong)info.Width * 4
                || (ulong)info.StrideBytes * info.Height > (ulong)destination.Length)
                throw RuntimeError("The native renderer returned an invalid frame layout.");
            return true;
        }
    }

    public FilamentDiagnostics GetDiagnostics()
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            var info = new NativeDiagnostics { StructSize = (uint)Marshal.SizeOf<NativeDiagnostics>() };
            Check(_api.GetDiagnostics(_handle, ref info));
            if (info.State == (uint)FilamentRendererState.Failed)
                throw RuntimeError(_api.Error(_handle.DangerousGetHandle()));
            try { FilamentBackendPolicy.ValidateDiagnostics(_backend, info.RequestedBackend, info.Backend, FilamentFailureStage.Rendering); }
            catch { NativeBackendProbe.Shared.Invalidate(_payload, _backend); throw; }
            return new((FilamentRendererState)info.State,
                info.Backend switch { 1 => "Filament OpenGL", 2 => "Filament Metal", 3 => "Filament Vulkan", _ => "Unavailable" },
                (int)info.MsaaSamples, (int)info.Width, (int)info.Height, (int)info.Meshes, (int)info.Nodes,
                (int)info.Primitives, (int)info.Materials, (int)info.PatchedMaterials, info.Triangles,
                info.FrameId, info.Generation, new Vector3(info.MinX, info.MinY, info.MinZ),
                new Vector3(info.MaxX, info.MaxY, info.MaxZ))
            { RequestedBackend = _backend, ActualBackend = (FilamentBackend)info.Backend, CreationTimings = CreationTimings };
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            // hm_destroy joins the worker and drains outstanding GPU readback callbacks.
            _handle.Dispose();
            DiagnosticLog.Debug("Filament.Renderer", "disposed", "The native renderer worker and readback callbacks have drained.", new { backend = _backend });
        }
    }

    private void Check(int result, FilamentFailureStage stage = FilamentFailureStage.Rendering)
    {
        if (result >= 0) return;
        NativeBackendProbe.Shared.Invalidate(_payload, _backend);
        var message = _api.Error(_handle.DangerousGetHandle());
        DiagnosticLog.Error("Filament.Renderer", "nativeResultFailed", message, data: new { backend = _backend, stage, result });
        throw new FilamentRendererException(message,
            result == -5 ? FilamentFailureKind.InvalidModel : FilamentFailureKind.RuntimeFailure, stage, _backend, result);
    }
    private FilamentRendererException RuntimeError(string message)
    {
        NativeBackendProbe.Shared.Invalidate(_payload, _backend);
        DiagnosticLog.Error("Filament.Renderer", "runtimeFailed", message, data: new { backend = _backend });
        return new(message, FilamentFailureKind.RuntimeFailure, FilamentFailureStage.Rendering, _backend);
    }
    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}

internal sealed class RendererHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    private readonly NativeBindings.DestroyDelegate _destroy;
    internal RendererHandle(nint handle, NativeBindings.DestroyDelegate destroy) : base(true)
    {
        _destroy = destroy;
        SetHandle(handle);
    }
    protected override bool ReleaseHandle() { _destroy(handle); return true; }
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeCreateOptions { public uint StructSize, Width, Height, RequestedBackend; }
[StructLayout(LayoutKind.Sequential)]
internal struct NativeConfiguration
{
    public uint StructSize, Width, Height, MsaaSamples;
    public float Yaw, Pitch, Zoom, Fov, ClearRed, ClearGreen, ClearBlue, Exposure, LightIntensity, FitMargin, Reserved1, Reserved2;
}
[StructLayout(LayoutKind.Sequential)]
internal struct NativeFrameInfo
{
    public uint StructSize, Width, Height, StrideBytes;
    public ulong FrameId, Generation;
    public double RenderMilliseconds, ReadbackMilliseconds;
}
[StructLayout(LayoutKind.Sequential)]
internal struct NativeDiagnostics
{
    public uint StructSize, State, Backend, MsaaSamples, Width, Height, Meshes, Nodes, Primitives, Materials, PatchedMaterials, RequestedBackend;
    public ulong Triangles, FrameId, Generation;
    public float MinX, MinY, MinZ, MaxX, MaxY, MaxZ;
}

internal sealed class NativeBindings
{
    internal const uint RequiredAbiVersion = 2;
    private static readonly object InitializationGate = new();
    private static NativeBindings? _instance;
    internal static NativeBindings Instance
    {
        get
        {
            lock (InitializationGate) return _instance ??= new NativeBindings();
        }
    }
    // The module remains loaded for the process lifetime so SafeHandle finalizers always have a valid destroy entrypoint.
    private readonly nint _module;
    private readonly string _libraryPath;
    private readonly string _libraryHash;
    internal readonly CreateDelegate Create;
    internal readonly LoadModelDelegate LoadModel;
    internal readonly ConfigureDelegate Configure;
    internal readonly RenderDelegate Render;
    internal readonly TryCopyDelegate TryCopy;
    internal readonly DiagnosticsDelegate GetDiagnostics;
    internal readonly DestroyDelegate Destroy;
    private readonly ErrorDelegate _getError;
    // Native callbacks can outlive a renderer and must stay rooted for this module's process lifetime.
    private static readonly NativeLogDelegate NativeLogConsumer = ReceiveNativeLog;

    private NativeBindings()
    {
        if (Marshal.SizeOf<NativeCreateOptions>() != 16 || Marshal.SizeOf<NativeConfiguration>() != 64
            || Marshal.SizeOf<NativeFrameInfo>() != 48 || Marshal.SizeOf<NativeDiagnostics>() != 96)
            throw new InvalidDataException("Filament managed/native structure layout mismatch.");
        var runtime = NativeRuntimeResolver.ResolveCurrent();
        _libraryPath = NativeRuntimeResolver.FindLibraryPath(AppContext.BaseDirectory, runtime);
        _libraryHash = NativeProbePayload.HashFile(_libraryPath);
        DiagnosticLog.Debug("Filament.Runtime", "loading", "Loading the exact packaged native runtime.",
            new { path = _libraryPath, sha256 = _libraryHash, runtime });
        _module = NativeLibrary.Load(_libraryPath);
        try
        {
            var abi = Export<AbiDelegate>("hm_get_abi_version")();
            ValidateAbi(abi);
            Create = Export<CreateDelegate>("hm_create");
            LoadModel = Export<LoadModelDelegate>("hm_load_model");
            Configure = Export<ConfigureDelegate>("hm_configure");
            Render = Export<RenderDelegate>("hm_render");
            TryCopy = Export<TryCopyDelegate>("hm_try_copy_latest_frame");
            GetDiagnostics = Export<DiagnosticsDelegate>("hm_get_diagnostics");
            Destroy = Export<DestroyDelegate>("hm_destroy");
            _getError = Export<ErrorDelegate>("hm_get_last_error");
            var setLogCallback = Export<SetLogCallbackDelegate>("hm_set_log_callback");
            setLogCallback(DiagnosticLog.IsEnabled ? NativeLogConsumer : null, 0);
            DiagnosticLog.Info("Filament.Runtime", "loaded", "Native ABI and required logging export verified.",
                new { path = _libraryPath, sha256 = _libraryHash, abi, logCallbackExport = "hm_set_log_callback" });
        }
        catch (Exception error)
        {
            DiagnosticLog.Error("Filament.Runtime", "loadFailed", "Native runtime initialization or required export resolution failed.", error,
                new { path = _libraryPath, sha256 = _libraryHash });
            NativeLibrary.Free(_module);
            throw;
        }
    }

    internal static void ValidateAbi(uint abi)
    {
        if (abi != RequiredAbiVersion)
            throw new InvalidDataException($"Unsupported Filament native ABI {abi}; expected {RequiredAbiVersion}.");
    }

    internal NativeProbePayload GetProbePayload()
    {
        if (NativeProbePayload.HashFile(_libraryPath) != _libraryHash)
            throw new InvalidDataException("The Filament native runtime changed while loaded; restart the application.");
        var probePath = NativeRuntimeResolver.FindProbePath(_libraryPath);
        var manifest = NativeRuntimeResolver.FindManifestPath(_libraryPath);
        var manifestHash = NativeProbePayload.HashFile(manifest);
        var probeHash = NativeProbePayload.HashFile(probePath);
        DiagnosticLog.Debug("Filament.Runtime", "payloadVerified", "Native library, probe and manifest identities verified.",
            new { library = _libraryPath, librarySha256 = _libraryHash, probePath, probeSha256 = probeHash, manifest, manifestSha256 = manifestHash });
        return new(probePath, string.Join(':', _libraryPath, _libraryHash, probeHash, manifestHash));
    }

    internal static unsafe void ReceiveNativeLog(uint level, nint message, uint length, nint user)
    {
        try
        {
            if (!DiagnosticLog.IsEnabled || message == 0 || length > int.MaxValue) return;
            var bytes = new ReadOnlySpan<byte>((void*)message, (int)length);
            var severity = level switch
            {
                1 => DiagnosticLevel.Information, 2 => DiagnosticLevel.Warning,
                3 => DiagnosticLevel.Error, 4 => DiagnosticLevel.Critical, _ => DiagnosticLevel.Debug
            };
            // Both strings are copied now: Filament frees the source message as soon as its callback returns.
            DiagnosticLog.Write(severity, "Filament.Native", "nativeLog", Encoding.UTF8.GetString(bytes),
                new { nativeLevel = level, byteLength = length, utf8Hex = Convert.ToHexString(bytes) });
        }
        catch { /* A diagnostic consumer must never throw back into unmanaged render or driver threads. */ }
    }

    private T Export<T>(string name) where T : Delegate => Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(_module, name));
    internal string Error(nint renderer)
    {
        var length = Math.Clamp(_getError(renderer, null, 0), 1u, 16384u);
        var bytes = new byte[length];
        _getError(renderer, bytes, length);
        var end = Array.IndexOf(bytes, (byte)0);
        return Encoding.UTF8.GetString(bytes, 0, end < 0 ? bytes.Length : end);
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate uint AbiDelegate();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate void NativeLogDelegate(uint level, nint message, uint length, nint user);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate void SetLogCallbackDelegate(NativeLogDelegate? callback, nint user);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int CreateDelegate(in NativeCreateOptions options, out nint renderer);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int LoadModelDelegate(RendererHandle renderer, nint glb, ulong length, [MarshalAs(UnmanagedType.LPUTF8Str)] string sha256);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int ConfigureDelegate(RendererHandle renderer, in NativeConfiguration configuration);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int RenderDelegate(RendererHandle renderer);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int TryCopyDelegate(RendererHandle renderer, ulong after, nint buffer, ulong capacity, ref NativeFrameInfo info);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int DiagnosticsDelegate(RendererHandle renderer, ref NativeDiagnostics info);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate uint ErrorDelegate(nint renderer, [Out] byte[]? destination, uint capacity);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate void DestroyDelegate(nint renderer);
}
