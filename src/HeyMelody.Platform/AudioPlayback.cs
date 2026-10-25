namespace HeyMelody.Platform;

/// <summary>A native audio identity. It stays inside the device worker and is never an IPC identifier.</summary>
public sealed record AudioOutputDevice(string Id, string Name);

/// <summary>An explicitly routed PCM stream; implementations must never fall back to the default output.</summary>
public interface IAudioPlayback : IDisposable
{
    void Prepare(string deviceId, short[] interleavedStereoSamples, uint sampleRate);
    void Play();
    void Pause();
    long PlaybackFrame { get; }
    bool HasFailed { get; }
}
