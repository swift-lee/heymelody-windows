using System.Runtime.InteropServices;
using System.Text.Json;
using HeyMelody.Platform;

namespace HeyMelody.MacOS;

/// <summary>CoreAudio device identities remain in the native device worker.</summary>
public static class MacAudioOutput
{
    public static IReadOnlyList<AudioOutputDevice> EnumerateOutputs()
    {
        if (!OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException();
        var pointer = Native.List();
        if (pointer == IntPtr.Zero) throw new IOException("无法读取蓝牙音频输出，请检查系统声音设置。");
        try
        {
            using var json = JsonDocument.Parse(Marshal.PtrToStringUTF8(pointer) ?? "[]");
            return json.RootElement.EnumerateArray().Select(item => new AudioOutputDevice(
                item.GetProperty("uid").GetString()!, item.GetProperty("name").GetString()!)).ToArray();
        }
        finally { Native.Free(pointer); }
    }

    internal static class Native
    {
        private const string Library = "HeyMelodyMac";
        [DllImport(Library, EntryPoint = "hm_audio_list")]
        internal static extern IntPtr List();
        [DllImport(Library, EntryPoint = "hm_audio_free_string")]
        internal static extern void Free(IntPtr pointer);
        [DllImport(Library, EntryPoint = "hm_audio_create")]
        internal static extern IntPtr Create([MarshalAs(UnmanagedType.LPUTF8Str)] string uid,
            short[] samples, int sampleCount, uint sampleRate);
        [DllImport(Library, EntryPoint = "hm_audio_play")]
        internal static extern int Play(IntPtr stream);
        [DllImport(Library, EntryPoint = "hm_audio_pause")]
        internal static extern int Pause(IntPtr stream);
        [DllImport(Library, EntryPoint = "hm_audio_frame")]
        internal static extern long Frame(IntPtr stream);
        [DllImport(Library, EntryPoint = "hm_audio_failed")]
        internal static extern int Failed(IntPtr stream);
        [DllImport(Library, EntryPoint = "hm_audio_destroy")]
        internal static extern void Destroy(IntPtr stream);
    }
}

public sealed class MacAudioQueue : IAudioPlayback
{
    private readonly object _sync = new();
    private IntPtr _stream;
    private bool _disposed;
    public void Prepare(string deviceId, short[] interleavedStereoSamples, uint sampleRate)
    {
        if (!OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException();
        if (string.IsNullOrWhiteSpace(deviceId) || interleavedStereoSamples.Length == 0
            || interleavedStereoSamples.Length % 2 != 0 || sampleRate is < 8000 or > 192000)
            throw new ArgumentException("音频输出或试听音乐格式无效。");
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_stream != IntPtr.Zero) throw new InvalidOperationException("音频输出已准备。");
            _stream = MacAudioOutput.Native.Create(deviceId, interleavedStereoSamples, interleavedStereoSamples.Length, sampleRate);
            if (_stream == IntPtr.Zero) throw new InvalidOperationException("所选蓝牙音频输出不可用，请重新选择。不会改用其他输出。");
        }
    }
    public void Play()
    {
        lock (_sync)
            if (_stream == IntPtr.Zero || MacAudioOutput.Native.Play(_stream) != 0)
                throw new InvalidOperationException("蓝牙音频输出无法播放，请检查连接并重新选择。");
    }
    public void Pause()
    {
        lock (_sync)
            if (_stream != IntPtr.Zero && MacAudioOutput.Native.Pause(_stream) != 0)
                throw new IOException("蓝牙音频输出已不可用。");
    }
    public long PlaybackFrame { get { lock (_sync) return _stream == IntPtr.Zero ? 0 : MacAudioOutput.Native.Frame(_stream); } }
    public bool HasFailed { get { lock (_sync) return _stream != IntPtr.Zero && MacAudioOutput.Native.Failed(_stream) != 0; } }
    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            if (_stream != IntPtr.Zero) MacAudioOutput.Native.Destroy(_stream);
            _stream = IntPtr.Zero;
        }
    }
}
