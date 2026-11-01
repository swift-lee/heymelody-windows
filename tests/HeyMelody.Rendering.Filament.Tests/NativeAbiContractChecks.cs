using System.Text.Json;
using HeyMelody.Rendering.Filament;

namespace HeyMelody.Rendering.Tests;

internal static class NativeAbiContractChecks
{
    internal static void Run(string fixtureRoot, Action<bool, string> check)
    {
        var directory = Path.Combine(fixtureRoot, "native-abi-contract");
        Directory.CreateDirectory(directory);
        var runtime = NativeRuntimeResolver.ResolveCurrent();
        var library = Path.Combine(directory, runtime.LibraryFileName);
        var manifest = Path.Combine(directory, "filament-runtime.json");
        File.WriteAllText(library, "portable ABI fixture; never loaded as native code");
        WriteManifest();

        var fake = new FakeModule();
        var report = new NativeAbiChecks.Receipt();
        NativeAbiChecks.Execute(directory, report, path =>
        {
            check(path == library && Path.IsPathFullyQualified(path), "Native ABI mode loads only the exact absolute library path.");
            return fake;
        });
        check(report.Success && report.NativeLibraryLoaded && report.AbiVersion == 2
            && report.ExportsVerified == NativeAbiChecks.RequiredExports.Length && fake.Disposed
            && fake.VersionCalls == 1 && fake.Exports.SequenceEqual(NativeAbiChecks.RequiredExports),
            "Native ABI mode checks all exports, invokes only the ABI getter, and unloads the module.");
        check(report.NativeSha256 == NativeAbiChecks.HashFile(library)
            && report.ManifestSha256 == NativeAbiChecks.HashFile(manifest) && !report.GpuProbeExecuted
            && report.NativeBluetoothCalls == 0, "ABI receipts bind both source hashes without GPU or device claims.");

        Reject(() => NativeAbiChecks.Execute(".", new(), _ => throw new Exception("Unexpected native load.")),
            "Native ABI mode rejects relative native directories.");
        File.Move(library, library + ".saved");
        Reject(() => NativeAbiChecks.Execute(directory, new(), _ => throw new Exception("Unexpected native load.")),
            "Missing exact native libraries fail before native loading.");
        File.Move(library + ".saved", library);
        File.Move(manifest, manifest + ".saved");
        Reject(() => NativeAbiChecks.Execute(directory, new(), _ => throw new Exception("Unexpected native load.")),
            "Missing exact manifests fail before native loading.");
        File.Move(manifest + ".saved", manifest);
        WriteManifest(hash: new string('0', 64));
        Reject(() => NativeAbiChecks.Execute(directory, new(), _ => throw new Exception("Unexpected native load.")),
            "Native source checksum mismatch fails before native loading.");
        WriteManifest(rid: "wrong-rid");
        Reject(() => NativeAbiChecks.Execute(directory, new(), _ => throw new Exception("Unexpected native load.")),
            "Native manifests for another process RID fail before native loading.");
        WriteManifest(abi: 1);
        Reject(() => NativeAbiChecks.Execute(directory, new(), _ => throw new Exception("Unexpected native load.")),
            "Legacy native manifest ABIs fail before native loading.");
        WriteManifest(duplicate: true);
        Reject(() => NativeAbiChecks.Execute(directory, new(), _ => throw new Exception("Unexpected native load.")),
            "Duplicate native library declarations fail before native loading.");
        WriteManifest();

        fake = new FakeModule { Abi = 1 };
        Reject(() => NativeAbiChecks.Execute(directory, new(), _ => fake), "An incorrect loaded ABI is a failure.");
        check(fake.Disposed, "An incorrect loaded ABI still unloads its module.");
        fake = new FakeModule { MissingExport = "hm_render" };
        Reject(() => NativeAbiChecks.Execute(directory, new(), _ => fake), "Missing required native exports are a failure.");
        check(fake.Disposed && fake.VersionCalls == 0, "Export rejection unloads without invoking any entrypoint.");
        fake = new FakeModule { OnVersion = () => File.AppendAllText(library, "changed") };
        Reject(() => NativeAbiChecks.Execute(directory, new(), _ => fake), "Native input changes during verification invalidate the receipt.");
        check(fake.Disposed, "Input changes do not leave the ABI test module loaded.");
        WriteManifest();
        fake = new FakeModule { OnVersion = () => File.AppendAllText(manifest, " ") };
        Reject(() => NativeAbiChecks.Execute(directory, new(), _ => fake), "Manifest changes during verification invalidate the receipt.");
        check(fake.Disposed, "Manifest changes do not leave the ABI test module loaded.");
        var originalOutput = Console.Out;
        using var capturedOutput = new StringWriter();
        int exitCode;
        try
        {
            Console.SetOut(capturedOutput);
            exitCode = NativeAbiChecks.Run(Path.Combine(directory, "absent"), Path.Combine(directory, "failure-receipt"));
        }
        finally { Console.SetOut(originalOutput); }
        using var failure = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "failure-receipt", "native-abi-report.json")));
        check(exitCode != 0 && !failure.RootElement.GetProperty("success").GetBoolean()
            && !failure.RootElement.GetProperty("nativeLibraryLoaded").GetBoolean(),
            "Native ABI mode exits nonzero and records failure when the exact runtime is absent.");

        void WriteManifest(string? hash = null, string? rid = null, uint abi = 2, bool duplicate = false)
        {
            var file = new { path = runtime.LibraryFileName, sha256 = hash ?? NativeAbiChecks.HashFile(library) };
            File.WriteAllText(manifest, JsonSerializer.Serialize(new
            {
                schemaVersion = 2, abiVersion = abi, rid = rid ?? runtime.RuntimeIdentifier,
                logCallbackExport = "hm_set_log_callback", files = duplicate ? new[] { file, file } : new[] { file }
            }));
        }
        void Reject(Action action, string reason)
        {
            try { action(); }
            catch (Exception error) when (error is ArgumentException or IOException or InvalidDataException or EntryPointNotFoundException)
            { check(true, reason); return; }
            throw new InvalidOperationException(reason);
        }
    }
    private sealed class FakeModule : NativeAbiChecks.INativeModule
    {
        internal uint Abi { get; init; } = 2;
        internal string? MissingExport { get; init; }
        internal Action? OnVersion { get; init; }
        internal List<string> Exports { get; } = [];
        internal int VersionCalls { get; private set; }
        internal bool Disposed { get; private set; }
        public void RequireExport(string name)
        {
            if (name == MissingExport) throw new EntryPointNotFoundException(name);
            Exports.Add(name);
        }
        public uint GetAbiVersion() { VersionCalls++; OnVersion?.Invoke(); return Abi; }
        public void Dispose() => Disposed = true;
    }
}
