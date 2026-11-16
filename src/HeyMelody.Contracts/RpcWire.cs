using System.Text;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace HeyMelodyWindows.Host;

public sealed class RpcRequest
{
    public int SchemaVersion { get; set; } = 1;
    public long Id { get; set; }
    public string Operation { get; set; } = "";
    public string? Identity { get; set; }
    public string[]? DeviceIdentities { get; set; }
    public HeyMelodyWindows.App.RefreshScope? RefreshScope { get; set; }
    public JsonElement Action { get; set; }
}

public sealed class RpcResponse
{
    public int SchemaVersion { get; set; } = 1;
    public long Id { get; set; }
    public bool Success { get; set; }
    public string? Error { get; set; }
    public JsonElement? Data { get; set; }
}

public static class RpcWire
{
    public const int MaximumLineCharacters = 524288;
    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        TypeInfoResolver = CreateResolver()
    };

    public static JsonTypeInfo<T> TypeInfo<T>() => (JsonTypeInfo<T>)Json.GetTypeInfo(typeof(T));

    [UnconditionalSuppressMessage("Trimming", "IL2026",
        Justification = "Native AOT sets IsReflectionEnabledByDefault=false and removes this reflection-only JIT compatibility branch.")]
    [UnconditionalSuppressMessage("Aot", "IL3050",
        Justification = "Native AOT sets IsReflectionEnabledByDefault=false and removes this reflection-only JIT compatibility branch.")]
    private static IJsonTypeInfoResolver CreateResolver() => JsonSerializer.IsReflectionEnabledByDefault
        ? JsonTypeInfoResolver.Combine(RpcJsonContext.Default, new DefaultJsonTypeInfoResolver())
        : RpcJsonContext.Default;
}

/// <summary>
/// Buffered line reader for RPC records over a byte stream. Records are UTF-8 without a byte-order mark (every writer uses
/// <c>UTF8Encoding(false)</c> and none writes a BOM; a BOM is not interpreted) separated by '\n'. '\r' is dropped, a record may hold
/// at most <see cref="RpcWire.MaximumLineCharacters"/> other characters (checked while it accumulates, before deserialization; a read
/// deadline is enforced by the parent process), a clean EOF yields null and EOF inside a record is an error. Bytes are decoded as they
/// arrive, so a record never holds more than the limit and a stream without newlines cannot grow memory.
/// It reads bytes, not characters, on purpose: Stream.ReadAsync returns as soon as any data is available, while StreamReader.ReadAsync
/// keeps reading when the last stream read filled its byte buffer exactly and would hold back a complete record until unrelated data arrived.
/// Use one instance per connection as the only consumer of its stream; data after a record stays buffered for the next call.
/// </summary>
/// <param name="stream">The stream to read; nothing else may read from it (nor from a reader wrapped around it).</param>
/// <param name="strictUtf8">Whether invalid UTF-8 throws instead of becoming U+FFFD.</param>
public sealed class RpcLineReader(Stream stream, bool strictUtf8 = false)
{
    private readonly Decoder _decoder = new UTF8Encoding(false, strictUtf8).GetDecoder();
    private readonly byte[] _bytes = new byte[4096];
    // A decoded read holds at most one character per byte, plus one for a sequence the decoder held back earlier.
    private readonly char[] _buffer = new char[4096 + 2];
    private int _start, _end;
    private bool _eof;

    public async ValueTask<string?> ReadLineAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        StringBuilder? text = null;
        while (true)
        {
            if (_start < _end && TakeLine(ref text) is { } line) return line;
            if (_eof) return text is { Length: > 0 } ? throw new InvalidDataException("Incomplete RPC record.") : null;
            // Reset first: a cancelled or failed read must not leave stale buffered characters behind.
            _start = _end = 0;
            int read = await stream.ReadAsync(_bytes.AsMemory(), token).ConfigureAwait(false);
            // A read may end inside a multi-byte character and decode to nothing yet; only 0 bytes is the end of the stream.
            if (read == 0) { _eof = true; _end = _decoder.GetChars(ReadOnlySpan<byte>.Empty, _buffer, flush: true); }
            else _end = _decoder.GetChars(_bytes.AsSpan(0, read), _buffer, flush: false);
        }
    }

    // Consumes buffered characters up to and including the next '\n'; returns the record, or null when the buffer ran out first.
    private string? TakeLine(ref StringBuilder? text)
    {
        var chunk = _buffer.AsSpan(_start, _end - _start);
        int newline = chunk.IndexOf('\n');
        var part = newline < 0 ? chunk : chunk[..newline];
        _start += newline < 0 ? chunk.Length : newline + 1;
        // Whole record in the buffer and no '\r': at most 4097 characters, far below the limit.
        if (text is null && newline >= 0 && part.IndexOf('\r') < 0) return new string(part);
        text ??= new StringBuilder();
        while (!part.IsEmpty)
        {
            int cr = part.IndexOf('\r');
            var run = cr < 0 ? part : part[..cr];
            if (text.Length + run.Length > RpcWire.MaximumLineCharacters) throw SizeLimit();
            text.Append(run);
            part = cr < 0 ? default : part[(cr + 1)..];
        }
        return newline < 0 ? null : text.ToString();
    }

    private static InvalidDataException SizeLimit() => new("RPC record exceeds the size limit.");
}
