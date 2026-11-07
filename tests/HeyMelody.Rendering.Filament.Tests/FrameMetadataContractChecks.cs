using System.Runtime.InteropServices;
using System.Text;
using HeyMelody.Rendering.Filament;

namespace HeyMelody.Rendering.Tests;

internal static class FrameMetadataContractChecks
{
    internal static void Run(Action<bool, string> check)
    {
        using var fixture = new FrameFixture();
        var renderer = fixture.Renderer;
        var valid = fixture.Frame;
        check(renderer.TryGetLatestFrameInfo(20, out var metadata)
            && metadata == new FilamentFrameInfo(2, 2, 8, 21, 7, 1.25, 2.5),
            "Metadata queries translate the complete newer-frame ABI metadata.");
        check(fixture.LastDestination == 0 && fixture.LastCapacity == 0 && fixture.LastAfterFrameId == 20
            && fixture.LastStructSize == Marshal.SizeOf<NativeFrameInfo>() && fixture.CopyCalls == 0,
            "Metadata queries use null/zero destination with the exact ABI structure and do not copy pixels.");
        check(renderer.TryGetLatestFrameInfo(20, out var repeated) && repeated == metadata
            && fixture.CopyCalls == 0,
            "Repeated metadata queries neither consume a frame nor advance the caller's frame ID.");
        check(!renderer.TryGetLatestFrameInfo(21, out var absent) && absent == default,
            "Metadata queries report no newer frame after the latest frame ID.");

        fixture.Frame = valid with { FrameId = 22 };
        var destination = new byte[16];
        check(renderer.TryCopyLatestFrame(20, destination, out var copied)
            && copied.FrameId == 22 && destination.SequenceEqual(FrameFixture.Pixels)
            && fixture.CopyCalls == 1,
            "The copy after a metadata query uses its own newer frame metadata and still copies pixels.");
        RejectNative(() => renderer.TryCopyLatestFrame(20, new byte[4], out _), -3,
            "Buffer-too-small remains a native failure for pixel copies.");

        fixture.ResultOverride = 0;
        check(!renderer.TryGetLatestFrameInfo(20, out absent) && absent == default,
            "A no-frame result discards any metadata unexpectedly written by the native delegate.");
        foreach (var result in new[] { -1, -2, -4, -5, -99 })
        {
            fixture.ResultOverride = result;
            RejectNative(() => renderer.TryGetLatestFrameInfo(20, out _), result,
                $"Metadata queries preserve native failure result {result}.");
        }
        foreach (var result in new[] { 1, 2 })
        {
            fixture.ResultOverride = result;
            RejectNative(() => renderer.TryGetLatestFrameInfo(20, out _), null,
                "A positive copy result is not accepted as a successful null-destination metadata query.");
        }

        fixture.ResultOverride = -3;
        var invalid = new[]
        {
            valid with { StructSize = 0 },
            valid with { Width = 0 },
            valid with { Height = 0 },
            valid with { StrideBytes = 7 },
            valid with { Width = uint.MaxValue },
            valid with { Height = uint.MaxValue },
            valid with { StrideBytes = uint.MaxValue },
            valid with { FrameId = 20 },
            valid with { FrameId = 0 },
            valid with { RenderMilliseconds = double.NaN },
            valid with { RenderMilliseconds = -1 },
            valid with { ReadbackMilliseconds = double.PositiveInfinity },
            valid with { ReadbackMilliseconds = -1 }
        };
        foreach (var frame in invalid)
        {
            fixture.Frame = frame;
            RejectNative(() => renderer.TryGetLatestFrameInfo(20, out _), null,
                "Buffer-too-small with invalid frame metadata fails closed.");
        }

        renderer.Dispose();
        int queryCalls = fixture.QueryCalls;
        bool disposed = false;
        try { renderer.TryGetLatestFrameInfo(20, out _); }
        catch (ObjectDisposedException) { disposed = true; }
        check(disposed && fixture.QueryCalls == queryCalls && fixture.DestroyCalls == 1,
            "Metadata queries on a disposed renderer never call its native delegate.");

        void RejectNative(Action action, int? result, string reason)
        {
            try { action(); }
            catch (FilamentRendererException error)
            {
                check(error.Stage == FilamentFailureStage.Rendering && error.Backend == FilamentBackend.Vulkan
                    && error.Kind == (result == -5 ? FilamentFailureKind.InvalidModel : FilamentFailureKind.RuntimeFailure)
                    && (result is null || error.NativeResult == result), reason);
                return;
            }
            throw new InvalidOperationException(reason);
        }
    }

    private sealed class FrameFixture : IDisposable
    {
        internal static readonly byte[] Pixels = [1, 2, 3, 255, 4, 5, 6, 255, 7, 8, 9, 255, 10, 11, 12, 255];
        private static readonly byte[] ErrorBytes = Encoding.UTF8.GetBytes("Managed native ABI fixture failure.\0");
        internal NativeFrameInfo Frame = new()
        {
            StructSize = (uint)Marshal.SizeOf<NativeFrameInfo>(), Width = 2, Height = 2, StrideBytes = 8,
            FrameId = 21, Generation = 7, RenderMilliseconds = 1.25, ReadbackMilliseconds = 2.5
        };
        internal NativeFilamentRenderer Renderer { get; }
        internal int? ResultOverride { get; set; }
        internal int QueryCalls { get; private set; }
        internal int CopyCalls { get; private set; }
        internal int DestroyCalls { get; private set; }
        internal nint LastDestination { get; private set; }
        internal ulong LastCapacity { get; private set; }
        internal ulong LastAfterFrameId { get; private set; }
        internal uint LastStructSize { get; private set; }

        internal FrameFixture()
        {
            var bindings = new NativeBindings(Create,
                (_, _, _, _) => throw new InvalidOperationException("Unexpected model load."),
                (RendererHandle _, in NativeConfiguration _) => throw new InvalidOperationException("Unexpected configure."),
                _ => throw new InvalidOperationException("Unexpected render."),
                TryCopy, GetDiagnostics, _ => DestroyCalls++, GetError);
            Renderer = new NativeFilamentRenderer(bindings,
                new NativeProbePayload("managed-frame-fixture", Guid.NewGuid().ToString("N")), FilamentBackend.Vulkan);
        }

        private static int Create(in NativeCreateOptions options, out nint renderer)
        {
            renderer = 1234;
            return 0;
        }

        private int TryCopy(RendererHandle renderer, ulong after, nint destination, ulong capacity, ref NativeFrameInfo info)
        {
            QueryCalls++;
            LastDestination = destination;
            LastCapacity = capacity;
            LastAfterFrameId = after;
            LastStructSize = info.StructSize;
            if (ResultOverride is { } result)
            {
                info = Frame;
                return result;
            }
            if (Frame.FrameId <= after) return 0;
            info = Frame;
            if (destination == 0 || capacity < (ulong)Pixels.Length) return -3;
            Marshal.Copy(Pixels, 0, destination, Pixels.Length);
            CopyCalls++;
            return 1;
        }

        private static int GetDiagnostics(RendererHandle renderer, ref NativeDiagnostics info)
        {
            info.State = (uint)FilamentRendererState.Ready;
            info.RequestedBackend = info.Backend = (uint)FilamentBackend.Vulkan;
            return 0;
        }

        private static uint GetError(nint renderer, byte[]? destination, uint capacity)
        {
            if (destination is not null)
                ErrorBytes.AsSpan(0, Math.Min(ErrorBytes.Length, (int)capacity)).CopyTo(destination);
            return (uint)ErrorBytes.Length;
        }

        public void Dispose() => Renderer.Dispose();
    }
}
