using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using HeyMelodyWindows.Host;

internal static class GeneratedWasapiInteropTests
{
    internal static async Task RunAsync(Func<string, Func<Task>, Task> test)
    {
#if HEYMELODY_WINDOWS
        await test("generated WASAPI wrapper calls the native ABI and releases its unique COM references", () =>
        {
            VerifyRenderClient();
            return Task.CompletedTask;
        });
#else
        await Task.CompletedTask;
#endif
    }

#if HEYMELODY_WINDOWS
    private static unsafe void VerifyRenderClient()
    {
        // This is a synthetic IRenderClient vtable, not an audio endpoint; no OS device is opened.
        using var native = new SyntheticRenderClient();
        // Activate/GetService return IUnknown-shaped objects, then use generated interface dispatch.
        var wrapper = (WasapiNative.IRenderClient)UniqueComInterfaceMarshaller<object>.ConvertToManaged(native.Instance)!;
        UniqueComInterfaceMarshaller<object>.Free(native.Instance);
        try
        {
            WasapiNative.Check(wrapper.GetBuffer(37, out var buffer));
            if (buffer != (nint)native.Instance || native.Instance->Frames != 37)
                throw new InvalidOperationException("Generated GetBuffer changed the native signature or vtable slot.");
            WasapiNative.Check(wrapper.ReleaseBuffer(37, WasapiNative.Silent));
            if (native.Instance->ReleasedFrames != 37 || native.Instance->Flags != WasapiNative.Silent)
                throw new InvalidOperationException("Generated ReleaseBuffer changed the native signature or vtable slot.");
        }
        finally { WasapiNative.Release(wrapper); }
        if (native.Instance->References != 0)
            throw new InvalidOperationException("The unique generated COM wrapper leaked native references.");
    }

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct NativeInstance
    {
        internal nint* Vtable;
        internal int References;
        internal uint Frames;
        internal uint ReleasedFrames;
        internal uint Flags;
    }

    private sealed unsafe class SyntheticRenderClient : IDisposable
    {
        internal NativeInstance* Instance { get; }
        private readonly nint* _vtable;

        internal SyntheticRenderClient()
        {
            _vtable = (nint*)NativeMemory.AllocZeroed(5, (nuint)sizeof(nint));
            _vtable[0] = (nint)(delegate* unmanaged[Stdcall]<NativeInstance*, Guid*, nint*, int>)&QueryInterface;
            _vtable[1] = (nint)(delegate* unmanaged[Stdcall]<NativeInstance*, uint>)&AddRef;
            _vtable[2] = (nint)(delegate* unmanaged[Stdcall]<NativeInstance*, uint>)&Release;
            _vtable[3] = (nint)(delegate* unmanaged[Stdcall]<NativeInstance*, uint, nint*, int>)&GetBuffer;
            _vtable[4] = (nint)(delegate* unmanaged[Stdcall]<NativeInstance*, uint, uint, int>)&ReleaseBuffer;
            Instance = (NativeInstance*)NativeMemory.AllocZeroed((nuint)sizeof(NativeInstance));
            Instance->Vtable = _vtable;
            Instance->References = 1;
        }

        public void Dispose()
        {
            NativeMemory.Free(Instance);
            NativeMemory.Free(_vtable);
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static int QueryInterface(NativeInstance* instance, Guid* iid, nint* result)
        {
            if (*iid != new Guid("00000000-0000-0000-C000-000000000046") && *iid != WasapiNative.RenderClientId)
            {
                *result = 0;
                return unchecked((int)0x80004002);
            }
            Interlocked.Increment(ref instance->References);
            *result = (nint)instance;
            return 0;
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static uint AddRef(NativeInstance* instance) => (uint)Interlocked.Increment(ref instance->References);

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static uint Release(NativeInstance* instance) => (uint)Interlocked.Decrement(ref instance->References);

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static int GetBuffer(NativeInstance* instance, uint frames, nint* data)
        {
            instance->Frames = frames;
            *data = (nint)instance;
            return 0;
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static int ReleaseBuffer(NativeInstance* instance, uint frames, uint flags)
        {
            instance->ReleasedFrames = frames;
            instance->Flags = flags;
            return 0;
        }
    }
#endif
}
