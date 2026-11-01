using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using HeyMelody.Rendering.Filament;

namespace HeyMelody.Rendering.Tests;

internal static class NativeAbiChecks
{
    internal static readonly string[] RequiredExports =
    [
        "hm_get_abi_version", "hm_set_log_callback", "hm_create", "hm_load_model",
        "hm_set_environment", "hm_configure", "hm_render", "hm_try_copy_latest_frame",
        "hm_get_diagnostics", "hm_get_last_error", "hm_destroy"
    ];
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    internal static int Run(string directory, string outputDirectory)
    {
        var receipt = new Receipt();
        try { Execute(directory, receipt, path => new NativeModule(path)); }
        catch (Exception error) { receipt.Error = $"{error.GetType().Name}: {error.Message}"; }
        try
        {
            var output = Path.GetFullPath(outputDirectory);
            Directory.CreateDirectory(output);
            var json = JsonSerializer.Serialize(receipt, JsonOptions);
            using (var stream = new FileStream(Path.Combine(output, "native-abi-report.json"), FileMode.CreateNew))
            using (var writer = new StreamWriter(stream)) writer.Write(json);
            Console.WriteLine(json);
        }
        catch (Exception error)
        {
            Console.Error.WriteLine($"Cannot write native ABI receipt: {error.Message}");
            return 1;
        }
        return receipt.Success ? 0 : 1;
    }

    internal static void Execute(string directory, Receipt receipt, Func<string, INativeModule> load)
    {
        if (!Path.IsPathFullyQualified(directory))
            throw new ArgumentException("The native runtime directory must be an absolute path.", nameof(directory));
        var runtime = NativeRuntimeResolver.ResolveCurrent();
        receipt.RuntimeIdentifier = runtime.RuntimeIdentifier;
        receipt.NativeLibraryPath = Path.Combine(Path.GetFullPath(directory), runtime.LibraryFileName);
        receipt.ManifestPath = Path.Combine(Path.GetFullPath(directory), "filament-runtime.json");
        RequireFile(receipt.ManifestPath);
        RequireFile(receipt.NativeLibraryPath);
        byte[] manifestBytes = File.ReadAllBytes(receipt.ManifestPath);
        receipt.ManifestSha256 = Hash(manifestBytes);
        receipt.NativeSha256 = HashFile(receipt.NativeLibraryPath);
        using var document = JsonDocument.Parse(manifestBytes);
        var manifest = document.RootElement;
        if (manifest.GetProperty("schemaVersion").GetInt32() != 2
            || manifest.GetProperty("abiVersion").GetUInt32() != NativeBindings.RequiredAbiVersion
            || manifest.GetProperty("rid").GetString() != runtime.RuntimeIdentifier
            || manifest.GetProperty("logCallbackExport").GetString() != "hm_set_log_callback")
            throw new InvalidDataException("The native manifest schema, ABI, RID or logging export does not match this process.");
        var entries = manifest.GetProperty("files").EnumerateArray()
            .Where(file => file.GetProperty("path").GetString() == runtime.LibraryFileName).ToArray();
        if (entries.Length != 1 || entries[0].GetProperty("sha256").GetString() != receipt.NativeSha256)
            throw new InvalidDataException("The exact native library must have one matching SHA256 declaration in the manifest.");

        // Resolve every ABI entrypoint, but invoke only the version getter: no renderer, GPU or Bluetooth calls.
        using (var module = load(receipt.NativeLibraryPath))
        {
            receipt.NativeLibraryLoaded = true;
            foreach (var export in RequiredExports)
            {
                module.RequireExport(export);
                receipt.ExportsVerified++;
            }
            receipt.AbiVersion = module.GetAbiVersion();
            NativeBindings.ValidateAbi(receipt.AbiVersion.Value);
        }
        if (HashFile(receipt.NativeLibraryPath) != receipt.NativeSha256
            || HashFile(receipt.ManifestPath) != receipt.ManifestSha256)
            throw new InvalidDataException("The native library or manifest changed during ABI verification.");
        receipt.Success = true;
    }

    private static void RequireFile(string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("The exact native runtime file was not found.", path);
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Native ABI inputs cannot be symbolic links.");
    }
    internal static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    internal interface INativeModule : IDisposable
    {
        void RequireExport(string name);
        uint GetAbiVersion();
    }
    private sealed class NativeModule : INativeModule
    {
        private readonly nint _handle;
        internal NativeModule(string absoluteLibraryPath) => _handle = NativeLibrary.Load(absoluteLibraryPath);
        public void RequireExport(string name) => _ = NativeLibrary.GetExport(_handle, name);
        public uint GetAbiVersion() => Marshal.GetDelegateForFunctionPointer<AbiGetter>(
            NativeLibrary.GetExport(_handle, "hm_get_abi_version"))();
        public void Dispose() => NativeLibrary.Free(_handle);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate uint AbiGetter();
    }
    internal sealed class Receipt
    {
        public int SchemaVersion { get; } = 1;
        public bool Success { get; set; }
        public bool NativeRequested { get; } = true;
        public bool NativeLibraryLoaded { get; set; }
        public string? RuntimeIdentifier { get; set; }
        public uint ExpectedAbiVersion { get; } = NativeBindings.RequiredAbiVersion;
        public uint? AbiVersion { get; set; }
        public string? NativeLibraryPath { get; set; }
        public string? NativeSha256 { get; set; }
        public string? ManifestPath { get; set; }
        public string? ManifestSha256 { get; set; }
        public IReadOnlyList<string> RequiredExports { get; } = NativeAbiChecks.RequiredExports;
        public int ExportsVerified { get; set; }
        public bool GpuProbeExecuted { get; } = false;
        public int NativeBluetoothCalls { get; } = 0;
        public string? Error { get; set; }
        public string Scope { get; } = "Exact same-RID native library load, required exports and ABI getter only; no renderer, GPU, UI, Bluetooth or device operations.";
    }
}
