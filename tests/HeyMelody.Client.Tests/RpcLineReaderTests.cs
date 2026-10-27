using System.IO.Pipes;
using System.Text;
using HeyMelodyWindows.Host;

// RpcLineReader replaced the per-character RpcWire.ReadLineAsync on the wire. The old algorithm is kept below as the oracle:
// it reads the text one character per call, the reader under test reads the UTF-8 bytes of the same text through streams that
// return 1..N bytes per call (splitting multi-byte sequences anywhere), and both must agree call by call.
internal static class RpcLineReaderTests
{
    private const int Max = RpcWire.MaximumLineCharacters;
    private static readonly int[] ChunkSizes = [1, 2, 3, 5, 7, 64, 4095, 4096, 4097, 5000, 100000];

    public static async Task RunAsync(Func<string, Func<Task>, Task> test)
    {
        await test("RPC line reader matches the per-character reader on CR, EOF and record layouts at every chunk size", Layouts);
        await test("RPC line reader matches the per-character reader on random streams and random read sizes", Fuzz);
        await test("RPC line reader matches the per-character reader at the size limit and one past it", Limit);
        await test("RPC line reader returns a record when its newline arrives, keeps the rest buffered and never waits to fill", Incremental);
        await test("RPC line reader honors a cancelled token on entry without losing buffered records", Cancelled);
        await test("RPC line reader decodes multi-byte characters split across reads and matches a one-shot decode on invalid UTF-8", Utf8);
        await test("RPC line reader returns complete records when the available data ends exactly on a read boundary", ReadBoundary);
        await test("RPC line reader returns complete records from a real pipe without further data", RealPipe);
    }

    private static async Task Layouts()
    {
        string[] texts =
        [
            "", "\r", "\r\r\r", "\n", "\r\n", "\n\n", "\n\r", "\r\n\r\n", "\r\r\n", "abc", "abc\r", "abc\n", "abc\r\n", "abc\n\r",
            "a\rb\n", "\ra\n", "a\r\r\n", "a\n\n\nb\n", "ab\ncd", "ab\ncd\r", "ab\ncd\r\n", "ab\r\ncd\r\nef\r\n", "{}", "{\"a\":\"界\"}\n{\"b\":1}\r\n",
            new string('a', 4095) + "\n", new string('a', 4096) + "\n", new string('a', 4097) + "\n", new string('a', 4096) + "\r\n" + new string('b', 4096) + "\n",
            new string('a', 4095) + "\r" + "\n" + "x\n", new string('a', 8192) + "\r", "\r" + new string('a', 4095) + "\n",
            "界\n", "界\r\n界\n", "\r界\r\n", "é😀\n😀\r", "a😀b界c\r\nd", Repeat("界", 4095) + "\n", Repeat("界", 4096) + "\n", Repeat("😀", 4096) + "\r\n" + Repeat("é", 4097) + "\n",
            Repeat("😀", 5000) + "x\n", new string('a', 4093) + "😀" + "\n", new string('a', 4094) + "😀" + "\r\n", new string('a', 4095) + "界" + "\nz"
        ];
        foreach (string text in texts)
        {
            var expected = await Expected(text);
            foreach (int size in ChunkSizes) Same(expected, await Actual(text, () => size), Preview(text), "fixed " + size);
        }
    }

    private static async Task Fuzz()
    {
        var random = new Random(20261022);
        for (int round = 0; round < 2500; round++)
        {
            string text = Generate(random);
            var expected = await Expected(text);
            for (int variant = 0; variant < 3; variant++)
            {
                int top = variant switch { 0 => 3, 1 => 40, _ => 6000 };
                // The last variant also completes some reads asynchronously.
                Same(expected, await Actual(text, () => random.Next(1, top), yields: variant == 2, strict: variant == 1), Preview(text), $"round {round} random 1..{top}");
            }
            int fixedSize = ChunkSizes[random.Next(ChunkSizes.Length)];
            Same(expected, await Actual(text, () => fixedSize), Preview(text), $"round {round} fixed {fixedSize}");
        }
    }

    private static async Task Limit()
    {
        var random = new Random(524288);
        foreach (int length in new[] { Max - 1, Max, Max + 1 })
        {
            string line = new('a', length);
            string[] texts =
            [
                line + "\n", line + "\r\n", line + "\r\r\r\n", line + "\rb\n", line, line + "\r", line + "\nb\n", "\r\r" + line + "\n",
                string.Concat(Enumerable.Repeat("a\r", length)) + "\n"
            ];
            foreach (string text in texts)
            {
                var expected = await Expected(text);
                foreach (int size in new[] { 1, 4095, 4096, 4097, 100000 }) Same(expected, await Actual(text, () => size), Preview(text), $"{length} fixed {size}");
                Same(expected, await Actual(text, () => random.Next(1, 9999)), Preview(text), $"{length} random");
            }
        }
        // The limit counts characters, not bytes: 3-byte, 2-byte and surrogate-pair content hits it at the same character count.
        foreach (string unit in new[] { "界", "é", "😀" })
            foreach (int length in new[] { Max - 1, Max, Max + 1 })
            {
                string line = Repeat(unit, length);
                string[] texts = [line + "\n", line + "\r\n", line, "\r" + line + "\rb\n"];
                foreach (string text in texts)
                {
                    var expected = await Expected(text);
                    foreach (int size in new[] { 1, 4096, 100000 }) Same(expected, await Actual(text, () => size), Preview(text), $"{unit} {length} fixed {size}");
                    Same(expected, await Actual(text, () => random.Next(1, 9999)), Preview(text), $"{unit} {length} random");
                }
            }
        // Carriage returns never accumulate: millions of them without a newline are not a record.
        foreach (string text in new[] { new string('\r', 4 * Max + 100), new string('\r', 4 * Max + 100) + "a\n", new string('\r', 4 * Max) + "\n", "a" + new string('\r', 3 * Max) })
            Same(await Expected(text), await Actual(text, () => 100000), Preview(text), "carriage returns");
        // A record without a newline is rejected as soon as it exceeds the limit, not at EOF or when a newline arrives.
        var early = new RpcLineReader(new ScriptedStream(Encoding.UTF8.GetBytes(new string('a', Max)), [(byte)'a']));
        try { await early.ReadLineAsync(default).AsTask().WaitAsync(TimeSpan.FromSeconds(5)); throw new InvalidOperationException("An oversized record without newline was waited for."); }
        catch (InvalidDataException error) when (error.Message == "RPC record exceeds the size limit.") { }
        // The boundaries stated outright, not only against the oracle.
        var exact = await Actual(new string('a', Max) + "\n", () => 4096);
        if (exact.Count != 2 || exact[0].Value?.Length != Max || exact[1].Value is not null) throw new InvalidOperationException("The exact maximum was not accepted.");
        var over = await Actual(new string('a', Max + 1) + "\n", () => 4096);
        if (over.Count != 1 || over[0].Error != "InvalidDataException: RPC record exceeds the size limit.") throw new InvalidOperationException("One past the maximum: " + Show(over[^1]));
        var cut = await Actual("a\r", () => 1);
        if (cut.Count != 1 || cut[0].Error != "InvalidDataException: Incomplete RPC record.") throw new InvalidOperationException("EOF inside a record: " + Show(cut[^1]));
    }

    private static async Task Incremental()
    {
        var reader = new ScriptedStream("ab"u8.ToArray(), "c\nde"u8.ToArray(), "f\r\ngh\r"u8.ToArray());
        var lines = new RpcLineReader(reader);
        if (await lines.ReadLineAsync(default).AsTask().WaitAsync(TimeSpan.FromSeconds(5)) != "abc") throw new InvalidOperationException("First record");
        if (await lines.ReadLineAsync(default).AsTask().WaitAsync(TimeSpan.FromSeconds(5)) != "def") throw new InvalidOperationException("Second record");
        if (reader.Reads != 3) throw new InvalidOperationException("Reads: " + reader.Reads);
        using var cancel = new CancellationTokenSource();
        var pending = lines.ReadLineAsync(cancel.Token).AsTask();
        await Task.Delay(100);
        if (pending.IsCompleted) throw new InvalidOperationException("An unfinished record completed early.");
        cancel.Cancel();
        try { await pending.WaitAsync(TimeSpan.FromSeconds(5)); throw new InvalidOperationException("Cancellation was ignored."); }
        catch (OperationCanceledException) { }
    }

    private static async Task Cancelled()
    {
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        var lines = new RpcLineReader(new ChunkedStream("a\nb\nc"u8.ToArray(), () => 100));
        if (await lines.ReadLineAsync(default) != "a") throw new InvalidOperationException("First record");
        try { await lines.ReadLineAsync(cancelled.Token); throw new InvalidOperationException("A cancelled token was accepted."); }
        catch (OperationCanceledException) { }
        if (await lines.ReadLineAsync(default) != "b") throw new InvalidOperationException("Buffered record lost after cancellation");
        try { await lines.ReadLineAsync(default); throw new InvalidOperationException("Incomplete tail was accepted."); }
        catch (InvalidDataException) { }
        var old = await Capture(() => OldReadLineAsync(new ChunkedReader("a\n", () => 1), cancelled.Token));
        var current = await Capture(() => new RpcLineReader(new ChunkedStream("a\n"u8.ToArray(), () => 1)).ReadLineAsync(cancelled.Token).AsTask());
        if (old != current || old.Error != "canceled") throw new InvalidOperationException($"{Show(old)} vs {Show(current)}");
    }

    // Multi-byte sequences cut at every position; invalid input is compared with a one-shot decode of the whole stream.
    private static async Task Utf8()
    {
        var one = new RpcLineReader(new ScriptedStream([0xE7], [0x95], [0x8C, 0x0A, 0xF0, 0x9F], [0x98, 0x80, 0x0D, 0x0A, 0xC3], [0xA9, 0x0A]));
        foreach (string expected in new[] { "界", "😀", "é" })
            if (await one.ReadLineAsync(default).AsTask().WaitAsync(TimeSpan.FromSeconds(5)) != expected) throw new InvalidOperationException("Split sequence " + expected);
        // Invalid sequences, truncated ones, stray continuation bytes, a CR inside one and a sequence cut off by EOF or by a newline.
        byte[][] pieces = [[0x80], [0xBF], [0xC0, 0xAF], [0xE2, 0x82], [0xF0, 0x9F, 0x98], [0xFF], [0xED, 0xA0, 0x80], [0xE2, 0x0D, 0x82], [0xE2, 0x0A], [0x0D], [0x0A], "a"u8.ToArray(), "界"u8.ToArray(), "😀"u8.ToArray(), "\n"u8.ToArray()];
        var random = new Random(8192);
        for (int round = 0; round < 2500; round++)
        {
            var bytes = new List<byte>();
            for (int count = random.Next(0, 14); count > 0; count--) bytes.AddRange(pieces[random.Next(pieces.Length)]);
            var data = bytes.ToArray();
            var oracle = new ChunkedReader(Encoding.UTF8.GetString(data), () => 1);
            var expected = await Collect(() => OldReadLineAsync(oracle, default));
            foreach (int size in new[] { 1, 2, 3, 4096 }) Same(expected, await Actual(data, () => size), Convert.ToHexString(data), "invalid fixed " + size);
            Same(expected, await Actual(data, () => random.Next(1, 5), yields: round % 2 == 0), Convert.ToHexString(data), "invalid random");
        }
        // A strict reader refuses invalid bytes but still accepts valid text cut anywhere.
        var strict = await Actual([0x61, 0xFF, 0x0A], () => 1, strict: true);
        if (strict.Count != 1 || strict[0].Error is not { } error || !error.StartsWith("DecoderFallbackException")) throw new InvalidOperationException("Strict reader: " + Show(strict[^1]));
        var valid = await Actual("界😀é\n", () => 1, strict: true);
        if (valid.Count != 2 || valid[0].Value != "界😀é" || valid[1].Value is not null) throw new InvalidOperationException("Strict reader on valid text: " + Show(valid[0]));
    }

    // StreamReader.ReadAsync keeps reading while characters are still requested and its last stream read filled the byte buffer,
    // so a complete record whose data ended exactly on a full read (or held multi-byte characters) waited for unrelated data.
    private static async Task ReadBoundary()
    {
        foreach (int total in new[] { 4096, 8192, 4097, 12288 })
        {
            var data = FourRecords(total);
            var lines = new RpcLineReader(new ScriptedStream(data));
            for (int index = 0; index < 4; index++)
            {
                string? line = await lines.ReadLineAsync(default).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
                if (line is null || !line.StartsWith("{\"" + (char)('a' + index) + "\"")) throw new InvalidOperationException($"{total} bytes, record {index}: {line?.Length}");
            }
            await PendsUntilCancelled(lines, $"{total} bytes");
        }
    }

    private static async Task RealPipe()
    {
        foreach (int total in new[] { 4096, 8192 })
        {
            var data = FourRecords(total);
            using var server = new AnonymousPipeServerStream(PipeDirection.Out);
            using var client = new AnonymousPipeClientStream(PipeDirection.In, server.ClientSafePipeHandle);
            var lines = new RpcLineReader(client);
            // The pipe can hold less than the full payload, so consume while the producer writes.
            var writing = Task.Run(async () =>
            {
                await server.WriteAsync(data);
                await server.FlushAsync();
            });
            var verified = false;
            try
            {
                // The writer stays open and silent: every record must arrive anyway.
                for (int index = 0; index < 4; index++)
                {
                    string? line = await lines.ReadLineAsync(default).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
                    if (line is null || !line.StartsWith("{\"" + (char)('a' + index) + "\"")) throw new InvalidOperationException($"pipe {total} bytes, record {index}: {line?.Length}");
                }
                await writing;
                var next = lines.ReadLineAsync(default).AsTask();
                await Task.Delay(100);
                if (next.IsCompleted) throw new InvalidOperationException("A record appeared without data.");
                server.Dispose();
                if (await next.WaitAsync(TimeSpan.FromSeconds(5)) is not null) throw new InvalidOperationException("Closed pipe did not end the stream.");
                verified = true;
            }
            finally
            {
                // Release a blocked producer before observing it; preserve the original test failure.
                client.Dispose();
                server.Dispose();
                try { await writing; }
                catch (Exception) when (!verified) { }
            }
        }
    }

    private static async Task PendsUntilCancelled(RpcLineReader lines, string context)
    {
        using var cancel = new CancellationTokenSource();
        var next = lines.ReadLineAsync(cancel.Token).AsTask();
        await Task.Delay(100);
        if (next.IsCompleted) throw new InvalidOperationException(context + ": a record appeared without data.");
        cancel.Cancel();
        try { await next.WaitAsync(TimeSpan.FromSeconds(5)); throw new InvalidOperationException(context + ": cancellation was ignored."); }
        catch (OperationCanceledException) { }
    }

    // Four JSON records "a".."d" (the first with 3-byte, the third with 4-byte characters), the last padded so that the data is exactly
    // this many bytes and ends with its newline.
    private static byte[] FourRecords(int total)
    {
        string head = $"{{\"a\":\"{Repeat("界", 300)}\"}}\n{{\"b\":1}}\n{{\"c\":\"{Repeat("😀", 200)}\"}}\n";
        int padding = total - Encoding.UTF8.GetByteCount(head) - "{\"d\":\"\"}\n".Length;
        var data = Encoding.UTF8.GetBytes(head + "{\"d\":\"" + new string('a', padding) + "\"}\n");
        if (data.Length != total) throw new InvalidOperationException("Layout is " + data.Length + " bytes");
        return data;
    }

    // The unit repeated up to exactly this many characters; an odd remainder of a two-character unit becomes 'a'.
    private static string Repeat(string unit, int characters)
    {
        var text = new StringBuilder(characters);
        while (text.Length + unit.Length <= characters) text.Append(unit);
        return text.Append('a', characters - text.Length).ToString();
    }

    private sealed record Outcome(string? Value, string? Error);

    // The previous RpcWire.ReadLineAsync, verbatim: one ReadAsync per character.
    private static async Task<string?> OldReadLineAsync(TextReader reader, CancellationToken token)
    {
        var text = new StringBuilder();
        var next = new char[1];
        while (true)
        {
            int count = await reader.ReadAsync(next.AsMemory(), token);
            if (count == 0)
                return text.Length == 0 ? null : throw new InvalidDataException("Incomplete RPC record.");
            if (next[0] == '\n') return text.ToString();
            if (next[0] == '\r') continue;
            if (text.Length >= Max) throw new InvalidDataException("RPC record exceeds the size limit.");
            text.Append(next[0]);
        }
    }

    private static Task<List<Outcome>> Expected(string text)
    { var reader = new ChunkedReader(text, () => 1); return Collect(() => OldReadLineAsync(reader, default)); }

    private static Task<List<Outcome>> Actual(string text, Func<int> chunk, bool yields = false, bool strict = false)
        => Actual(Encoding.UTF8.GetBytes(text), chunk, yields, strict);

    private static Task<List<Outcome>> Actual(byte[] bytes, Func<int> chunk, bool yields = false, bool strict = false)
    { var lines = new RpcLineReader(new ChunkedStream(bytes, chunk, yields), strict); return Collect(() => lines.ReadLineAsync(default).AsTask()); }

    // Reads until EOF or the first failure; the reader's position after a failure is not part of the contract.
    private static async Task<List<Outcome>> Collect(Func<Task<string?>> next)
    {
        var outcomes = new List<Outcome>();
        while (true)
        {
            var outcome = await Capture(next); outcomes.Add(outcome);
            if (outcome.Error is not null || outcome.Value is null) return outcomes;
        }
    }

    private static async Task<Outcome> Capture(Func<Task<string?>> read)
    {
        try { return new(await read(), null); }
        catch (OperationCanceledException) { return new(null, "canceled"); }
        catch (Exception error) { return new(null, error.GetType().Name + ": " + error.Message); }
    }

    private static void Same(List<Outcome> expected, List<Outcome> actual, string text, string how)
    {
        for (int call = 0; call < Math.Max(expected.Count, actual.Count); call++)
        {
            if (call < expected.Count && call < actual.Count && expected[call] == actual[call]) continue;
            throw new InvalidOperationException($"{how}, call {call} of \"{text}\": expected {(call < expected.Count ? Show(expected[call]) : "none")}, got {(call < actual.Count ? Show(actual[call]) : "none")}");
        }
    }

    // Texts reach megabytes; failures must stay short.
    private static string Preview(string text) => text.Length <= 40 ? Escape(text) : $"{text.Length} chars from {Escape(text[..16])}";
    private static string Escape(string text) => text.Replace("\r", "\\r").Replace("\n", "\\n");
    private static string Show(Outcome outcome) => outcome.Error ?? (outcome.Value is null ? "EOF"
        : $"{outcome.Value.Length} chars {Escape(outcome.Value[..Math.Min(24, outcome.Value.Length)])}");

    // Lines around the 4096-byte buffer edges, with CR runs, CRLF, bare CR, 1-3 byte characters, surrogate pairs and an optionally missing final terminator.
    private static string Generate(Random random)
    {
        int[] edges = [0, 1, 2, 3, 4094, 4095, 4096, 4097, 4098, 8191, 8192, 8193];
        string[] endings = ["\n", "\n", "\r\n", "\r", "\n\n", "\r\r\n", ""];
        var text = new StringBuilder();
        for (int record = random.Next(0, 8); record > 0; record--)
        {
            int length = random.Next(3) == 0 ? edges[random.Next(edges.Length)] : random.Next(0, 40);
            for (int index = 0; index < length; index++)
                text.Append(random.Next(40) == 0 ? "\r" : random.Next(30) == 0 ? "界" : random.Next(40) == 0 ? "é" : random.Next(50) == 0 ? "😀" : ((char)('a' + random.Next(26))).ToString());
            text.Append(endings[random.Next(endings.Length)]);
        }
        return text.ToString();
    }

    // Returns 1..chunk() characters per call, never zero before the end; ReadBlockAsync would wait to fill the buffer, so it is refused.
    private sealed class ChunkedReader(string text, Func<int> chunk, bool yields = false) : TextReader
    {
        private int _position, _reads;
        public override ValueTask<int> ReadAsync(Memory<char> buffer, CancellationToken token = default)
        {
            if (token.IsCancellationRequested) return ValueTask.FromCanceled<int>(token);
            return yields && ++_reads % 4 == 0 ? Slow(buffer) : ValueTask.FromResult(Take(buffer));
        }
        private async ValueTask<int> Slow(Memory<char> buffer) { await Task.Yield(); return Take(buffer); }
        private int Take(Memory<char> buffer)
        {
            int count = Math.Min(Math.Min(buffer.Length, Math.Max(1, chunk())), text.Length - _position);
            text.AsSpan(_position, count).CopyTo(buffer.Span); _position += count;
            return count;
        }
        public override ValueTask<int> ReadBlockAsync(Memory<char> buffer, CancellationToken token = default) => throw new NotSupportedException();
    }

    // Returns 1..chunk() bytes per call, never zero before the end; synchronous reads are refused.
    private sealed class ChunkedStream(byte[] data, Func<int> chunk, bool yields = false) : ReadOnlyStream
    {
        private int _position, _reads;
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        {
            if (token.IsCancellationRequested) return ValueTask.FromCanceled<int>(token);
            return yields && ++_reads % 4 == 0 ? Slow(buffer) : ValueTask.FromResult(Take(buffer));
        }
        private async ValueTask<int> Slow(Memory<byte> buffer) { await Task.Yield(); return Take(buffer); }
        private int Take(Memory<byte> buffer)
        {
            int count = Math.Min(Math.Min(buffer.Length, Math.Max(1, chunk())), data.Length - _position);
            data.AsSpan(_position, count).CopyTo(buffer.Span); _position += count;
            return count;
        }
    }

    // Hands out its chunks (splitting one only when the caller's buffer is smaller), then waits for cancellation like an idle pipe.
    private sealed class ScriptedStream(params byte[][] chunks) : ReadOnlyStream
    {
        private int _next, _offset;
        public int Reads { get; private set; }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested(); Reads++;
            if (_next == chunks.Length) { await Task.Delay(Timeout.Infinite, token); return 0; }
            var chunk = chunks[_next].AsSpan(_offset);
            int count = Math.Min(chunk.Length, buffer.Length);
            chunk[..count].CopyTo(buffer.Span);
            if (count == chunk.Length) { _next++; _offset = 0; } else _offset += count;
            return count;
        }
    }

    private abstract class ReadOnlyStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override abstract ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default);
    }
}
