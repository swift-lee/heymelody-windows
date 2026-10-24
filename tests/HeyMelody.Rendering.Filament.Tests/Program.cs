using System.Buffers.Binary;
using System.Runtime.InteropServices;
using HeyMelody.Rendering.Filament;
using HeyMelody.Rendering.Tests;

if (args is ["--backend", var fixtureBackend]) return BackendProbeChecks.RunFixture(fixtureBackend);
if (args is ["--native-driver-failure"])
{
    string? invalidIcd = Environment.GetEnvironmentVariable("VK_ICD_FILENAMES");
    if (string.IsNullOrEmpty(invalidIcd) || File.Exists(invalidIcd))
        throw new InvalidOperationException("This isolated validation requires VK_ICD_FILENAMES to name a nonexistent ICD JSON.");
    for (int attempt = 0; attempt < 2; attempt++)
    {
        bool backendFailure = false;
        try { using var unexpected = NativeFilamentRendererFactory.Instance.Create(FilamentBackend.Vulkan); }
        catch (FilamentRendererException error)
        {
            backendFailure = error.Kind == FilamentFailureKind.BackendUnavailable
                && error.Stage == FilamentFailureStage.Probe && error.Backend == FilamentBackend.Vulkan;
        }
        if (!backendFailure) throw new InvalidOperationException("An unavailable Vulkan driver must fail in the child without selecting another backend.");
    }
    FilamentDiagnostics manualDiagnostics;
    using (var manual = NativeFilamentRendererFactory.Instance.Create(FilamentBackend.OpenGL))
        manualDiagnostics = manual.GetDiagnostics();
    if (manualDiagnostics.RequestedBackend != FilamentBackend.OpenGL || manualDiagnostics.ActualBackend != FilamentBackend.OpenGL)
        throw new InvalidOperationException("An explicit manual OpenGL request must still work without Vulkan.");
    var runtime = NativeRuntimeResolver.ResolveCurrent();
    string library = NativeRuntimeResolver.FindLibraryPath(AppContext.BaseDirectory, runtime);
    var failureReport = new
    {
        success = true, passed = 3, nativeRequested = true, vulkanFailures = 2, failureKind = "BackendUnavailable", failureStage = "Probe",
        manualRequestedBackend = manualDiagnostics.RequestedBackend.ToString(), manualActualBackend = manualDiagnostics.ActualBackend.ToString(),
        nativeSha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(library))).ToLowerInvariant(),
        scope = "Real isolated Vulkan driver-initialization failure is contained in child probes and remains retryable; OpenGL works only when explicitly requested; no UI, model, Bluetooth or preferences."
    };
    var failureJson = System.Text.Json.JsonSerializer.Serialize(failureReport);
    File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "native-driver-failure-report.json"), failureJson);
    Console.WriteLine(failureJson);
    return 0;
}
if (args is ["--native-recovery", var nativeSource])
{
    if (!OperatingSystem.IsWindows() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
        throw new PlatformNotSupportedException("Native recovery validation currently requires Windows x64.");
    bool initialMissing = false;
    try { using var unexpected = NativeFilamentRendererFactory.Instance.Create(FilamentBackend.OpenGL); }
    catch (FilamentRendererException error) when (error.Kind == FilamentFailureKind.ComponentUnavailable) { initialMissing = true; }
    if (!initialMissing) throw new InvalidOperationException("Run native recovery in a fresh staged directory without a native payload.");
    var runtime = NativeRuntimeResolver.ResolveCurrent();
    string target = Path.Combine(AppContext.BaseDirectory, "runtimes", runtime.RuntimeIdentifier, "native", runtime.LibraryFileName);
    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
    string sourceDirectory = Directory.Exists(nativeSource) ? Path.GetFullPath(nativeSource) : Path.GetDirectoryName(Path.GetFullPath(nativeSource))!;
    foreach (var file in Directory.EnumerateFiles(sourceDirectory))
        File.Copy(file, Path.Combine(Path.GetDirectoryName(target)!, Path.GetFileName(file)), overwrite: false);
    await NativeFilamentRendererFactory.Instance.PrepareBackendAsync(FilamentBackend.OpenGL);
    FilamentDiagnostics recoveredDiagnostics;
    using (var recovered = NativeFilamentRendererFactory.Instance.Create(FilamentBackend.OpenGL))
        recoveredDiagnostics = recovered.GetDiagnostics();
    if (recoveredDiagnostics.RequestedBackend != FilamentBackend.OpenGL || recoveredDiagnostics.ActualBackend != FilamentBackend.OpenGL)
        throw new InvalidOperationException("The native recovery renderer must retain its explicitly requested OpenGL backend.");
    if (recoveredDiagnostics.CreationTimings is not { ProbeDisposition: FilamentProbeDisposition.Cached })
        throw new InvalidOperationException("Backend preparation must be reusable by Create without probing again.");
    var recoveryReport = new
    {
        success = true, passed = 4, nativeRequested = true, initialMissing,
        requestedBackend = recoveredDiagnostics.RequestedBackend.ToString(), actualBackend = recoveredDiagnostics.ActualBackend.ToString(),
        recovered = true, nativeSha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(target))).ToLowerInvariant(),
        scope = "Real native module discovery retries successfully after a missing payload is supplied; no UI, model, Bluetooth or preferences."
    };
    var recoveryJson = System.Text.Json.JsonSerializer.Serialize(recoveryReport);
    File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "native-recovery-report.json"), recoveryJson);
    Console.WriteLine(recoveryJson);
    return 0;
}
if (args.Length != 0) throw new ArgumentException("Usage: [--native-recovery <native-runtime-folder> | --native-driver-failure]");

int passed = 0;
void Check(bool condition, string reason)
{
    if (!condition) throw new InvalidOperationException(reason);
    passed++;
}
void Reject(Action action, string reason)
{
    try { action(); }
    catch (Exception error) when (error is ArgumentException or InvalidDataException)
    { passed++; return; }
    throw new InvalidOperationException(reason);
}

foreach (var fixture in new[]
{
    (Path: GlbSource.BundledModel("EncoFree4"), Hash: GlbSource.Free4Sha256, Meshes: 28, Triangles: 93_875UL),
    (Path: GlbSource.BundledModel("EncoFree4Dynaudio"), Hash: GlbSource.DynaudioSha256, Meshes: 20, Triangles: 94_927UL),
    (Path: Path.Combine(GlbSource.RepositoryRoot(), "src", "HeyMelody.Resources", "Bundles", "data", GlbSource.X3BlackSha256 + ".glb"),
        Hash: GlbSource.X3BlackSha256, Meshes: 31, Triangles: 98_367UL)
})
{
    var bytes = File.ReadAllBytes(fixture.Path);
    var source = GlbSource.Inspect(bytes);
    var model = new FilamentModelData(bytes, fixture.Hash.ToUpperInvariant());
    Check(source.Sha256 == fixture.Hash && source.Meshes == fixture.Meshes && source.Triangles == fixture.Triangles,
        "Source fixture measurements match the independently audited complete geometry.");
    Check(model.Sha256 == fixture.Hash && model.Bytes.Span.SequenceEqual(bytes), "Model validation preserves original bytes and normalizes SHA.");
    Check(model.MaterialProfile == "opaque-1001" && source.AlphaMode == "BLEND" && source.MaterialName == "1001",
        "An exact audited SHA chooses a correction without changing source transparency.");
    var computedModel = FilamentModelData.CreateValidated(bytes);
    Check(computedModel.Sha256 == fixture.Hash && computedModel.Bytes.Equals(new ReadOnlyMemory<byte>(bytes)),
        "Computed model validation preserves the original memory and agrees with expected-hash validation.");
    Check(FilamentModelData.CreateValidated(bytes, fixture.Hash.ToUpperInvariant()).Sha256 == fixture.Hash,
        "Expected-hash factory validation accepts normalized digest case.");
    Reject(() => FilamentModelData.CreateValidated(bytes, new string('0', 64)), "The model factory rejects a mismatched expected digest.");
    Reject(() => new FilamentModelData(bytes, new string('0', 64)), "A mismatched expected digest must be rejected.");
}

foreach (var hash in new[] { GlbSource.Free4Sha256, GlbSource.DynaudioSha256, GlbSource.X3BlackSha256, GlbSource.X3WhiteSha256 })
    Check(FilamentModelProfiles.IsAuditedOpaque1001(hash) && FilamentModelProfiles.IsAuditedOpaque1001(hash.ToUpperInvariant()),
        "All four exact audited SHA values select the opaque profile, including normalized hash case.");
Check(!FilamentModelProfiles.IsAuditedOpaque1001(new string('a', 64)), "An unknown digest cannot broaden a material correction.");
var minimal = new byte[20];
BinaryPrimitives.WriteUInt32LittleEndian(minimal, 0x46546c67);
BinaryPrimitives.WriteUInt32LittleEndian(minimal.AsSpan(4), 2);
BinaryPrimitives.WriteUInt32LittleEndian(minimal.AsSpan(8), 20);
Check(FilamentModelData.CreateValidated(minimal).MaterialProfile == "authored", "Unknown bytes never gain an opaque correction from their name or colour.");
Reject(() => FilamentModelData.CreateValidated(new byte[19]), "Truncated model headers must be rejected.");
var invalidMagic = minimal.ToArray(); invalidMagic[0] = 0;
Reject(() => FilamentModelData.CreateValidated(invalidMagic), "Invalid GLB magic must be rejected.");
var invalidVersion = minimal.ToArray(); invalidVersion[4] = 1;
Reject(() => FilamentModelData.CreateValidated(invalidVersion), "Unsupported GLB versions must be rejected.");
var invalidLength = minimal.ToArray(); invalidLength[8] = 24;
Reject(() => FilamentModelData.CreateValidated(invalidLength), "Declared-length mismatches must be rejected.");
Reject(() => new FilamentModelData(minimal, "not-a-digest"), "Malformed expected hashes must be rejected.");
new FilamentRenderConfiguration(320, 280).Validate();
Check(true, "A normal platform-neutral render configuration validates without loading a native library.");
foreach (var invalid in new[]
{
    new FilamentRenderConfiguration(0, 280), new FilamentRenderConfiguration(320, -1),
    new FilamentRenderConfiguration(320, 280, YawRadians: float.NaN),
    new FilamentRenderConfiguration(320, 280, PitchRadians: float.PositiveInfinity),
    new FilamentRenderConfiguration(320, 280, Zoom: 0),
    new FilamentRenderConfiguration(320, 280, VerticalFovDegrees: 180),
    new FilamentRenderConfiguration(320, 280, ClearRed: -1),
    new FilamentRenderConfiguration(320, 280, Exposure: 0),
    new FilamentRenderConfiguration(320, 280, MsaaSamples: 3)
}) Reject(invalid.Validate, "Invalid ABI rendering parameters must fail before native dispatch.");
foreach (var runtime in new[]
{
    (OSPlatform.Windows, Architecture.X64, "win-x64", "HeyMelody.Filament.Native.dll"),
    (OSPlatform.Windows, Architecture.Arm64, "win-arm64", "HeyMelody.Filament.Native.dll"),
    (OSPlatform.OSX, Architecture.X64, "osx-x64", "libHeyMelody.Filament.Native.dylib"),
    (OSPlatform.OSX, Architecture.Arm64, "osx-arm64", "libHeyMelody.Filament.Native.dylib"),
    (OSPlatform.Linux, Architecture.X64, "linux-x64", "libHeyMelody.Filament.Native.so"),
    (OSPlatform.Linux, Architecture.Arm64, "linux-arm64", "libHeyMelody.Filament.Native.so")
})
{
    var resolved = NativeRuntimeResolver.Resolve(runtime.Item1, runtime.Item2);
    Check(resolved.RuntimeIdentifier == runtime.Item3 && resolved.LibraryFileName == runtime.Item4,
        "Native payload mapping follows the requested operating system and architecture.");
}
foreach (var unsupported in new[] { (OSPlatform.Windows, Architecture.X86), (OSPlatform.Windows, Architecture.Arm), (OSPlatform.Linux, Architecture.Arm), (OSPlatform.Create("unknown"), Architecture.X64) })
{
    bool rejected = false;
    try { NativeRuntimeResolver.Resolve(unsupported.Item1, unsupported.Item2); }
    catch (PlatformNotSupportedException) { rejected = true; }
    Check(rejected, "Unsupported native combinations fail explicitly instead of loading a different RID.");
}
string runtimeFixture = Path.Combine(GlbSource.RepositoryRoot(), "artifacts", "filament-runtime-paths-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(runtimeFixture);
var windowsRuntime = NativeRuntimeResolver.Resolve(OSPlatform.Windows, Architecture.X64);
bool missing = false;
try { NativeRuntimeResolver.FindLibraryPath(runtimeFixture, windowsRuntime); }
catch (DllNotFoundException) { missing = true; }
Check(missing, "An absent packaged native payload fails without searching PATH or loading a library.");
string flat = Path.Combine(runtimeFixture, windowsRuntime.LibraryFileName);
File.WriteAllBytes(flat, [0]);
Check(NativeRuntimeResolver.FindLibraryPath(runtimeFixture, windowsRuntime) == flat,
    "An existing flat development payload resolves to its exact absolute path.");
bool missingProbe = false;
try { NativeRuntimeResolver.FindProbePath(flat); }
catch (FileNotFoundException) { missingProbe = true; }
Check(missingProbe, "The probe must exist beside the selected native library, without searching PATH.");
string flatProbe = Path.Combine(runtimeFixture, "hm_filament_probe.exe");
File.WriteAllBytes(flatProbe, [0]);
Check(NativeRuntimeResolver.FindProbePath(flat) == flatProbe, "Probe discovery uses the exact absolute sibling path.");
bool missingManifest = false;
try { NativeRuntimeResolver.FindManifestPath(flat); }
catch (FileNotFoundException) { missingManifest = true; }
Check(missingManifest, "A missing manifest fails as a component problem before starting a probe.");
string flatManifest = Path.Combine(runtimeFixture, "filament-runtime.json");
File.WriteAllText(flatManifest, "{}");
Check(NativeRuntimeResolver.FindManifestPath(flat) == flatManifest, "Manifest discovery uses the exact absolute native-library sibling path.");
string packaged = Path.Combine(runtimeFixture, "runtimes", windowsRuntime.RuntimeIdentifier, "native", windowsRuntime.LibraryFileName);
Directory.CreateDirectory(Path.GetDirectoryName(packaged)!);
File.WriteAllBytes(packaged, [0]);
Check(NativeRuntimeResolver.FindLibraryPath(runtimeFixture, windowsRuntime) == packaged,
    "A RID-specific packaged payload takes precedence over a flat development payload.");
var windowsArm64 = NativeRuntimeResolver.Resolve(OSPlatform.Windows, Architecture.Arm64);
File.Delete(flat);
bool wrongArchitectureMissing = false;
try { NativeRuntimeResolver.FindLibraryPath(runtimeFixture, windowsArm64); }
catch (DllNotFoundException) { wrongArchitectureMissing = true; }
Check(wrongArchitectureMissing, "An ARM64 process never resolves an x64 RID-specific payload as a fallback.");
string arm64Packaged = Path.Combine(runtimeFixture, "runtimes", windowsArm64.RuntimeIdentifier, "native", windowsArm64.LibraryFileName);
Directory.CreateDirectory(Path.GetDirectoryName(arm64Packaged)!);
File.WriteAllBytes(arm64Packaged, [0]);
Check(NativeRuntimeResolver.FindLibraryPath(runtimeFixture, windowsArm64) == arm64Packaged,
    "Windows ARM64 resolves its matching payload when an x64 payload is also present.");
await BackendProbeChecks.RunAsync(runtimeFixture, Check);
await DiagnosticChecks.RunAsync(runtimeFixture, Check);
var portableReport = new
{
    success = true, passed, nativeRequested = false, nativeLibraryLoaded = false,
    output = runtimeFixture,
    rendererAssemblySha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(typeof(FilamentModelData).Assembly.Location))).ToLowerInvariant(),
    scope = "Portable GLB integrity, exact-asset material policy, ABI/backend contracts, isolated child-process probe lifecycle, complete probe diagnostic output and copied native callback bytes; no native library, GPU, UI, Bluetooth or network."
};
var portableJson = System.Text.Json.JsonSerializer.Serialize(portableReport);
File.WriteAllText(Path.Combine(runtimeFixture, "report.json"), portableJson);
Console.WriteLine(portableJson);
return 0;
