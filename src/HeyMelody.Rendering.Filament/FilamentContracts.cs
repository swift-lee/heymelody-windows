using System.Buffers.Binary;
using System.Numerics;
using System.Security.Cryptography;

namespace HeyMelody.Rendering.Filament;

/// <summary>Original, integrity-checked GLB bytes. No model data is rewritten by this component.</summary>
public sealed record FilamentModelData
{
    public const int MaximumBytes = 64 * 1024 * 1024;
    public ReadOnlyMemory<byte> Bytes { get; }
    public string Sha256 { get; }
    public string MaterialProfile => FilamentModelProfiles.IsAuditedOpaque1001(Sha256) ? "opaque-1001" : "authored";

    public FilamentModelData(ReadOnlyMemory<byte> bytes, string sha256)
    {
        ValidateHeader(bytes.Span);
        if (sha256 is null || sha256.Length != 64 || !sha256.All(Uri.IsHexDigit))
            throw new ArgumentException("A SHA256 digest must contain 64 hexadecimal characters.", nameof(sha256));
        Sha256 = sha256.ToLowerInvariant();
        if (!string.Equals(Convert.ToHexString(SHA256.HashData(bytes.Span)), Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Model integrity check failed.");
        Bytes = bytes;
    }

    private FilamentModelData(ReadOnlyMemory<byte> bytes, VerifiedDigest digest)
    {
        Bytes = bytes;
        Sha256 = digest.Value;
    }

    public static FilamentModelData CreateValidated(ReadOnlyMemory<byte> bytes, string? expectedSha256 = null)
    {
        if (expectedSha256 is not null) return new(bytes, expectedSha256);
        ValidateHeader(bytes.Span);
        var digest = Convert.ToHexString(SHA256.HashData(bytes.Span)).ToLowerInvariant();
        return new(bytes, new VerifiedDigest(digest));
    }

    private readonly record struct VerifiedDigest(string Value);

    private static void ValidateHeader(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length is < 20 or > MaximumBytes)
            throw new InvalidDataException("Model size exceeds its limit or is not a GLB.");
        if (BinaryPrimitives.ReadUInt32LittleEndian(bytes) != 0x46546c67
            || BinaryPrimitives.ReadUInt32LittleEndian(bytes[4..]) != 2
            || BinaryPrimitives.ReadUInt32LittleEndian(bytes[8..]) != bytes.Length)
            throw new InvalidDataException("The model must be a complete glTF 2.0 binary file.");
    }
}

public static class FilamentModelProfiles
{
    public static bool IsAuditedOpaque1001(string verifiedSha256) => verifiedSha256.ToLowerInvariant() is
        "c49540d3e5afd7af8bb5aa562d3dc00e35246361e6efc14fa142a749b7d22bcc"
        or "7f85c66d397ee45163246ff745c37e52a70059e739be0af2283a30bfb689c2f6"
        or "6f6ab09e3b5067d3f1d4fac732243bb5fe723e3eb982d9b84e629560e5813363"
        or "b2d5d94fabee06718eaf7378fe6bdbf3db2824db23656a080251df92a04a5060";
}

public enum FilamentRendererState { Empty, Loading, Ready, Failed, Stopping }

/// <summary>Explicit native backend. Values are shared with C ABI 2; zero never selects an automatic fallback.</summary>
public enum FilamentBackend { OpenGL = 1, Metal = 2, Vulkan = 3 }

public enum FilamentProbeDisposition { Cached, Started, Joined }

public readonly record struct FilamentCreationTimings(
    double RuntimePreparationMilliseconds, double ProbeWaitMilliseconds,
    double EngineCreateMilliseconds, FilamentProbeDisposition ProbeDisposition);

public enum FilamentFailureKind { ComponentUnavailable, BackendUnavailable, InvalidModel, RuntimeFailure }
public enum FilamentFailureStage { RuntimeLoading, Probe, Creation, ModelLoading, Rendering }

public sealed class FilamentRendererException(
    string message, FilamentFailureKind kind, FilamentFailureStage stage,
    FilamentBackend backend, int nativeResult = -2, Exception? innerException = null)
    : InvalidOperationException(message, innerException)
{
    public FilamentFailureKind Kind { get; } = kind;
    public FilamentFailureStage Stage { get; } = stage;
    public FilamentBackend Backend { get; } = backend;
    public int NativeResult { get; } = nativeResult;
}

public readonly record struct FilamentRenderConfiguration(
    int Width, int Height, float YawRadians = 0, float PitchRadians = 0, float Zoom = 1,
    float VerticalFovDegrees = 45, float ClearRed = .12549f, float ClearGreen = .12549f,
    float ClearBlue = .12549f, float Exposure = 1, float LightIntensity = 100000,
    float FitMargin = 1.08f, int MsaaSamples = 4)
{
    public void Validate()
    {
        if (Width <= 0 || Height <= 0 || (long)Width * Height > int.MaxValue / 4)
            throw new ArgumentOutOfRangeException(nameof(Width), "Physical dimensions must fit a complete RGBA8 frame.");
        if (MsaaSamples is not (1 or 4)) throw new ArgumentOutOfRangeException(nameof(MsaaSamples));
        if (!float.IsFinite(YawRadians) || !float.IsFinite(PitchRadians) || Math.Abs(PitchRadians) >= MathF.PI / 2
            || !float.IsFinite(Zoom)
            || !float.IsFinite(VerticalFovDegrees) || !float.IsFinite(ClearRed) || !float.IsFinite(ClearGreen)
            || !float.IsFinite(ClearBlue) || !float.IsFinite(Exposure) || !float.IsFinite(LightIntensity)
            || !float.IsFinite(FitMargin) || Zoom <= 0 || Exposure <= 0 || LightIntensity < 0
            || FitMargin < 1 || VerticalFovDegrees is <= 0 or >= 175
            || ClearRed is < 0 or > 1 || ClearGreen is < 0 or > 1 || ClearBlue is < 0 or > 1)
            throw new ArgumentOutOfRangeException(nameof(YawRadians), "Rendering parameters must be finite and in range.");
    }
}

public readonly record struct FilamentFrameInfo(int Width, int Height, int RowBytes, ulong FrameId,
    ulong Generation, double RenderMilliseconds, double ReadbackMilliseconds);

public sealed record FilamentDiagnostics(FilamentRendererState State, string Backend, int MsaaSamples,
    int Width, int Height, int SourceMeshes, int SourceNodes, int SourcePrimitives, int SourceMaterials,
    int PatchedMaterials, ulong SourceTriangles, ulong FrameId, ulong Generation, Vector3 BoundsMin, Vector3 BoundsMax)
{
    public FilamentBackend? RequestedBackend { get; init; }
    public FilamentBackend? ActualBackend { get; init; }
    public FilamentCreationTimings? CreationTimings { get; init; }
    public static FilamentDiagnostics Empty { get; } = new(FilamentRendererState.Empty, "Unavailable", 0,
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, default, default);
}

public interface IFilamentRendererFactory
{
    IFilamentRenderer Create(FilamentBackend backend, CancellationToken cancellationToken = default);
}

/// <summary>Native renderer commands are serialized by its own worker; no native callback enters managed code.</summary>
public interface IFilamentRenderer : IDisposable
{
    void LoadModel(FilamentModelData model);
    void Configure(FilamentRenderConfiguration configuration);
    void RequestFrame();
    /// <summary>Reads newer-frame metadata without copying pixels or consuming the frame.</summary>
    /// <remarks>A later copy can return a newer frame; its own metadata remains authoritative.</remarks>
    bool TryGetLatestFrameInfo(ulong afterFrameId, out FilamentFrameInfo frame);
    bool TryCopyLatestFrame(ulong afterFrameId, Span<byte> destination, out FilamentFrameInfo frame);
    FilamentDiagnostics GetDiagnostics();
}
