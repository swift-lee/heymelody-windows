using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using HeyMelodyWindows.Calibration;

using HeyMelody.Diagnostics;

namespace HeyMelodyWindows.Host;

internal interface ICalibrationAuditionPlayer : IAsyncDisposable
{
    event Action<IReadOnlyList<double>>? Spectrum;
    event Action? Failed;
    Task PrepareAsync(ulong address, CancellationToken token);
    Task PauseAsync(CancellationToken token);
    Task PlayAsync(CancellationToken token);
    Task StopAsync();
}

internal static partial class AuditionEndpointAddress
{
    [GeneratedRegex(@"(?:^|&)([0-9A-Fa-f]{12})(?:_C[0-9A-Fa-f]{8})?$", RegexOptions.CultureInvariant)]
    private static partial Regex AddressTail();
    internal static bool TryParse(string? id, out ulong address)
    {
        address = 0;
        if (id is null || !id.StartsWith(@"BTHENUM\{0000110b-0000-1000-8000-00805f9b34fb}_", StringComparison.OrdinalIgnoreCase)) return false;
        string[] segments = id.Split('\\');
        if (segments.Length != 3) return false;
        var match = AddressTail().Match(segments[2]);
        return match.Success && ulong.TryParse(match.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out address)
            && address is > 0 and <= 0xFFFFFFFFFFFF;
    }
}

internal sealed class AuditionWave
{
    internal short[] Samples { get; }
    internal int Frames => Samples.Length / 2;
    private AuditionWave(short[] samples) => Samples = samples;
    internal static AuditionWave Read(Stream stream)
    {
        if (stream.CanSeek) return Read(stream, Math.Max(0, stream.Length - stream.Position));
        // Chunk lengths are validated against the total size, so a stream that cannot report it is buffered first.
        using var memory = new MemoryStream(); stream.CopyTo(memory); memory.Position = 0;
        return Read(memory, memory.Length);
    }
    // Validation order and messages follow the chunk walk of a fully buffered file; only the PCM payload is allocated.
    private static AuditionWave Read(Stream source, long total)
    {
        try
        {
            Span<byte> head = stackalloc byte[12], chunk = stackalloc byte[16];
            if (total is < 44 or > 32_000_000) throw new InvalidDataException("试听音频不可用。");
            source.ReadExactly(head);
            if (!head[..4].SequenceEqual("RIFF"u8) || !head[8..].SequenceEqual("WAVE"u8) || BinaryPrimitives.ReadUInt32LittleEndian(head[4..]) != total - 8)
                throw new InvalidDataException("试听音频不可用。");
            bool format = false; short[]? samples = null;
            long offset = 12;
            for (; offset + 8 <= total;)
            {
                source.ReadExactly(head[..8]);
                uint length = BinaryPrimitives.ReadUInt32LittleEndian(head[4..]);
                long available = total - offset - 8;
                if (length > available || ((length & 1) != 0 && length == available)) throw new InvalidDataException("试听音频不可用。");
                long skip = length + (length & 1);
                if (head[..4].SequenceEqual("fmt "u8))
                {
                    if (format || length < 16) throw new InvalidDataException("试听音频格式不可用。");
                    source.ReadExactly(chunk);
                    if (BinaryPrimitives.ReadUInt16LittleEndian(chunk) != 1
                        || BinaryPrimitives.ReadUInt16LittleEndian(chunk[2..]) != 2 || BinaryPrimitives.ReadUInt32LittleEndian(chunk[4..]) != 48000
                        || BinaryPrimitives.ReadUInt32LittleEndian(chunk[8..]) != 192000 || BinaryPrimitives.ReadUInt16LittleEndian(chunk[12..]) != 4
                        || BinaryPrimitives.ReadUInt16LittleEndian(chunk[14..]) != 16) throw new InvalidDataException("试听音频格式不可用。");
                    format = true; skip -= 16;
                }
                else if (head[..4].SequenceEqual("data"u8))
                {
                    if (samples is not null || length == 0 || length % 4 != 0) throw new InvalidDataException("试听音频不可用。");
                    samples = new short[length / 2];
                    source.ReadExactly(MemoryMarshal.AsBytes(samples.AsSpan()));
                    if (!BitConverter.IsLittleEndian)
                        for (int i = 0; i < samples.Length; i++) samples[i] = BinaryPrimitives.ReverseEndianness(samples[i]);
                    skip = 0;
                }
                if (skip != 0) source.Seek(skip, SeekOrigin.Current);
                offset += 8 + length + (length & 1);
            }
            if (offset != total || !format || samples is null) throw new InvalidDataException("试听音频不可用。");
            return new(samples);
        }
        catch (EndOfStreamException) { throw new InvalidDataException("试听音频不可用。"); }
    }
}
