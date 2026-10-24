using HeyMelodyWindows.Protocol;
using System.Text;
using System.Text.Json;

if (args.Contains("--diagnostics-tests"))
{
    Console.WriteLine($"{DiagnosticLoggingTests.Run()} protocol diagnostic tests passed.");
    return;
}

int passed = 0;
void Check(bool condition) { if (!condition) throw new Exception("Offline assertion failed."); }
void Test(string name, Action test) { test(); passed++; Console.WriteLine("PASS " + name); }
void Malformed(byte[] bytes)
{
    var decoder = new PacketDecoder();
    try { decoder.Feed(bytes); throw new Exception("Malformed input accepted."); }
    catch (FormatException) { }
    Check(decoder.Feed(PacketEncoder.Encode(0x0103, 55, [0, 0x10, 0xEC, 6])).Single().Sequence == 55);
}
byte[] Body(string text, byte count = 1) => new byte[] { 0, count }.Concat(Encoding.UTF8.GetBytes(text)).ToArray();

Test("Known empty-query wire fixture", () => Check(PacketEncoder.Encode(0x0103, 0, []).SequenceEqual(new byte[] { 0xAA, 7, 0, 0, 3, 1, 0, 0, 0 })));
Test("ULEB128 127 and 128 boundary", () =>
{
    var below = PacketEncoder.Encode(0x0100, 255, new byte[120]);
    var above = PacketEncoder.Encode(0x0100, 255, new byte[121]);
    Check(below[1] == 127 && above[1] == 128 && above[2] == 1);
    Check(new PacketDecoder().Feed(above).Single().Payload.Length == 121);
});
Test("Every possible split including ULEB128", () =>
{
    var frame = PacketEncoder.Encode(0x0100, 231, new byte[140]);
    for (int split = 0; split <= frame.Length; split++)
    {
        var decoder = new PacketDecoder();
        var packets = decoder.Feed(frame.AsSpan(0, split)).Concat(decoder.Feed(frame.AsSpan(split))).ToArray();
        Check(packets.Length == 1 && packets[0].Sequence == 231 && packets[0].Payload.Length == 140);
    }
});
Test("One-byte incremental fragmented message", () =>
{
    byte[] payload = Enumerable.Range(0, 2000).Select(i => (byte)i).ToArray();
    var encoded = PacketEncoder.Encode(0x8105, 253, payload, 128);
    var decoder = new PacketDecoder(); var packets = new List<Packet>();
    foreach (byte value in encoded) packets.AddRange(decoder.Feed(new byte[] { value }));
    Check(packets.Count == 1 && packets[0].Command == 0x8105 && packets[0].Sequence == 253 && packets[0].Payload.SequenceEqual(payload));
});
Test("Concatenated frames and reset partial state", () =>
{
    var frame = PacketEncoder.Encode(0x0103, 1, [0, 1, 2, 3]);
    var decoder = new PacketDecoder();
    Check(decoder.Feed(frame.Concat(frame).ToArray()).Count == 2);
    Check(decoder.Feed(frame.AsSpan(0, 3)).Count == 0); decoder.Reset();
    Check(decoder.Feed(frame).Count == 1);
});
Test("Reject noise, noncanonical and oversized lengths", () =>
{
    Malformed([0x11]); Malformed([0xAA, 0x87, 0]); Malformed([0xAA, 0xFF, 0x7F]);
    Malformed([0xAA, 0x80, 0x80, 1]); Malformed([0xAA, 0]);
});
Test("Reject inner length mismatch without truncation", () =>
{
    var encoded = PacketEncoder.Encode(0x0103, 0, [0, 1, 2, 3]); encoded[7] = 3; Malformed(encoded);
});
Test("Reject missing, duplicate, out of order and orphan FSN", () =>
{
    var encoded = PacketEncoder.Encode(0x0105, 4, new byte[45], 20);
    var duplicate = encoded.ToArray(); duplicate[24] = 0; Malformed(duplicate);
    var skip = encoded.ToArray(); skip[24] = 2; Malformed(skip);
    var wrongStart = encoded.ToArray(); wrongStart[4] = 1; Malformed(wrongStart);
    Malformed(encoded.AsSpan(20).ToArray());
});
Test("Bound messages and prevent FSN wrap", () =>
{
    var encoded = PacketEncoder.Encode(0x0105, 99, new byte[ushort.MaxValue]);
    Check(new PacketDecoder().Feed(encoded).Single().Payload.Length == ushort.MaxValue);
    try { PacketEncoder.Encode(1, 1, new byte[ushort.MaxValue + 1]); throw new Exception(); } catch (ArgumentOutOfRangeException) { }
    try { PacketEncoder.Encode(1, 1, new byte[2000], 10); throw new Exception(); } catch (ArgumentOutOfRangeException) { }
});
Test("All nine ACK failures ignore trailing bytes", () =>
{
    foreach (ushort command in new ushort[] { 0x0100, 0x0101, 0x0102, 0x0103, 0x0104, 0x0105, 0x010B, 0x0114, 0x010D })
        foreach (byte status in new byte[] { 1, 128, 255 })
        {
            var reply = QueryParsers.Parse(command, [status, 1, 0x18, 1]);
            Check(!reply.Success && reply.Status == status && reply.Data.Count == 0 && reply.Error != null);
        }
});
Test("Caps foundations and only declared optional GETs", () =>
{
    var reply = QueryParsers.Parse(0x0100, [0, 1, 0, 2]);
    Check(reply.Success && ((ushort[])reply.Data["supportedCommands"]!).SequenceEqual(new ushort[] { 0x0100, 0x0101, 0x0102, 0x0103, 0x0104, 0x0105, 0x010D, 0x0114 }));
    var emptyBitmap = QueryParsers.Parse(0x0100, [0, 0]);
    Check(emptyBitmap.Success && ((ushort[])emptyBitmap.Data["supportedCommands"]!).Length == 6);
});
Test("Future capability bytes are preserved without unknown command inference", () =>
{
    byte[] extended = new byte[512]; extended[1] = 1; extended[3] = 2; Array.Fill(extended, (byte)255, 10, 502);
    var reply = QueryParsers.Parse(0x0100, extended);
    Check(reply.Success && (int)reply.Data["capabilityExtraBytes"]! == 502 && ((string)reply.Data["capabilityBitmapHex"]!).Length == 1022);
    Check(((ushort[])reply.Data["supportedCommands"]!).Length == 8);
    Check(!QueryParsers.Parse(0x0100, new byte[513]).Success && !QueryParsers.Parse(0x0100, [0]).Success);
});
Test("Raw product IDs remain distinct across models", () =>
{
    foreach (int product in new[] { 0x06EC10, 0x100100, 0x100102, 0x108200, 0xFFFFFF })
    {
        var reply = QueryParsers.Parse(0x0103, new byte[] { 0, (byte)product, (byte)(product >> 8), (byte)(product >> 16) });
        Check(reply.Success && (string)reply.Data["productId"]! == product.ToString("X6"));
    }
    Check(!QueryParsers.Parse(0x0103, [0, 1, 2]).Success && !QueryParsers.Parse(0x0103, [0, 1, 2, 3, 4]).Success);
});
Test("MTU vendor endian and exact lengths", () =>
{
    Check((int)QueryParsers.Parse(0x0101, [0, 0x94, 2]).Data["remoteMtu"]! == 660);
    Check((int)QueryParsers.Parse(0x0102, [0, 0x34, 0x12]).Data["vendorId"]! == 0x1234);
    Check(!QueryParsers.Parse(0x0101, [0, 1]).Success && !QueryParsers.Parse(0x0102, [0, 1, 2, 3]).Success);
});
Test("Protocol text strict UTF8 and control rejection", () =>
{
    Check((string)QueryParsers.Parse(0x0104, [0, 49, 46, 48]).Data["protocolVersion"]! == "1.0");
    Check(!QueryParsers.Parse(0x0104, [0, 0xFF]).Success && !QueryParsers.Parse(0x0104, [0, 1]).Success && !QueryParsers.Parse(0x0104, [0]).Success);
});
Test("Three-byte binary protocol version is preserved without invented meaning", () =>
{
    foreach (byte[] body in new byte[][] { [0, 1, 2, 3], [0, 255, 254, 253], [0, 0, 0, 0], [0, 10, 13, 9] })
    {
        var reply = QueryParsers.Parse(0x0104, body);
        Check(reply.Success && reply.Data["protocolVersion"] is null && !(bool)reply.Data["protocolVersionInterpretationVerified"]!);
        Check((string)reply.Data["protocolVersionRawHex"]! == Convert.ToHexString(body.AsSpan(1)));
    }
    var text = QueryParsers.Parse(0x0104, [0, 49, 46, 48]);
    Check(text.Success && (string)text.Data["protocolVersion"]! == "1.0" && (bool)text.Data["protocolVersionInterpretationVerified"]!);
    var failed = QueryParsers.Parse(0x0104, [1, 1, 2, 3]);
    Check(!failed.Success && failed.Data.Count == 0);
});
Test("Firmware triples preserve versions and reject malformed counts", () =>
{
    var reply = QueryParsers.Parse(0x0105, Body("1,2,123,2,3,124", 2));
    Check(reply.Success && ((List<FirmwareEntry>)reply.Data["firmwareEntries"]!).Count == 2 && reply.Data["firmwareVersion"] is null);
    Check((string)QueryParsers.Parse(0x0105, Body("1,2,1.2.3")).Data["firmwareVersion"]! == "1.2.3");
    Check(!QueryParsers.Parse(0x0105, Body("1,2,123", 2)).Success && !QueryParsers.Parse(0x0105, Body("x,2,123")).Success && !QueryParsers.Parse(0x0105, [0, 1, 0xFF]).Success);
});
Test("Color reads payload byte one unsigned and rejects a missing value", () =>
{
    foreach (byte color in new byte[] { 0, 3, 200, 255 })
    {
        var reply = QueryParsers.Parse(0x010B, [0, color, 7]);
        Check(reply.Success && (byte)reply.Data["color"]! == color);
    }
    Check(!QueryParsers.Parse(0x010B, [0]).Success && !QueryParsers.Parse(0x010B, []).Success);
});
Test("Codec byte has no cross-layer invented mapping", () =>
{
    var reply = QueryParsers.Parse(0x0114, [0, 9]); Check(reply.Success && (byte)reply.Data["codecRaw"]! == 9 && reply.Data["codecName"] is null);
    Check(!QueryParsers.Parse(0x0114, [0]).Success);
});
Test("HiRes 0 1 and unknown values with raw preservation", () =>
{
    foreach (byte state in new byte[] { 0, 1, 2, 255 })
    {
        var reply = QueryParsers.Parse(0x010D, [0, 1, 0x18, state]);
        bool? expected = state switch { 0 => false, 1 => true, _ => null };
        Check(reply.Success && (bool?)reply.Data["hiRes"] == expected && ((Dictionary<string, byte>)reply.Data["featureValues"]!)["18"] == state);
    }
    Check(QueryParsers.Parse(0x010D, [0, 1, 6, 1]).Data["hiRes"] is null);
});
Test("Feature count truncation and duplicates reject", () =>
{
    foreach (byte[] payload in new byte[][] { [0], [0, 0], [0, 2, 0x18, 1], [0, 2, 0x18, 1, 0x18, 0] })
        Check(!QueryParsers.Parse(0x010D, payload).Success);
});
Test("Future codec and feature extension tails", () =>
{
    var codec = QueryParsers.Parse(0x0114, [0, 9, 1, 2]);
    Check(codec.Success && (byte)codec.Data["codecRaw"]! == 9 && (int)codec.Data["extensionBytesLength"]! == 2);
    var feature = QueryParsers.Parse(0x010D, [0, 1, 0x18, 1, 255]);
    Check(feature.Success && (bool)feature.Data["hiRes"]! && (int)feature.Data["extensionBytesLength"]! == 1);
    Check(!QueryParsers.Parse(0x0114, new byte[513]).Success && !QueryParsers.Parse(0x010D, new byte[513]).Success);
});
Test("Confirmed X4 stage-one fixture and capability subset metadata", () =>
{
    // Root-observed read-only session 2026-10-05 15:58 UTC; no Bluetooth calls here.
    var caps = QueryParsers.Parse(0x0100, Convert.FromHexString("00FF775AEEEE8FA65F1700000000000000"));
    Check(caps.Success && ((ushort[])caps.Data["supportedCommands"]!).Length == 8);
    Check((string)caps.Data["supportedCommandsScope"]! == "stage-one-query-subset"
        && (bool)caps.Data["foundationIncluded"]! && ((int[])caps.Data["knowncapbits"]!).SequenceEqual(new[] { 0, 17 }));
    var protocol = QueryParsers.Parse(0x0104, Convert.FromHexString("00010000"));
    Check(protocol.Success && protocol.Data["protocolVersion"] is null
        && (string)protocol.Data["protocolVersionRawHex"]! == "010000" && !(bool)protocol.Data["protocolVersionInterpretationVerified"]!);
    Check((string)QueryParsers.Parse(0x0103, [0, 0x10, 0xEC, 6]).Data["productId"]! == "06EC10");
    Check((int)QueryParsers.Parse(0x0101, [0, 0xF8, 0x11]).Data["remoteMtu"]! == 4600);
    Check((int)QueryParsers.Parse(0x0102, [0, 0x9A, 7]).Data["vendorId"]! == 1946);
    var firmware = QueryParsers.Parse(0x0105, Body("1,2,116,2,2,116,3,2,100", 3));
    Check(firmware.Success && ((List<FirmwareEntry>)firmware.Data["firmwareEntries"]!).SequenceEqual(new[]
        { new FirmwareEntry(1, 2, "116"), new FirmwareEntry(2, 2, "116"), new FirmwareEntry(3, 2, "100") }));
    Check((byte)QueryParsers.Parse(0x0114, [0, 8]).Data["codecRaw"]! == 8);
    Check((bool?)QueryParsers.Parse(0x010D, [0, 1, 0x18, 0]).Data["hiRes"] == false);
});
Test("Default DTO safety and JSON excludes raw protocol frames", () =>
{
    Check(new QueryReply().Data.Count == 0 && !new QueryReply().Success);
    var json = JsonSerializer.Serialize(QueryParsers.Parse(0x0114, [0, 9]));
    Check(!json.Contains("Payload") && !json.Contains("AA") && json.Contains("codecRaw"));
    Check(!QueryParsers.Parse(0x020D, [0]).Success);
});
Test("Full X4 APK capability golden set includes hostcap as metadata", () =>
{
    // Literal root-observed bitmap; expected commands come from jadx Protocol.java and
    // independently checked smali_classes3/i8.1/a.smali array construction (all 69 entries).
    byte[] bitmap = Convert.FromHexString("FF775AEEEE8FA65F1700000000000000");
    ushort[] expected =
    [
        0x0014, 0x0022, 0x0023, 0x0024, 0x0100, 0x0101, 0x0102, 0x0103,
        0x0104, 0x0105, 0x0106, 0x0107, 0x0108, 0x0109, 0x010B, 0x010C,
        0x010D, 0x010F, 0x0112, 0x0114, 0x0115, 0x0116, 0x0117, 0x0118,
        0x011A, 0x011C, 0x011E, 0x011F, 0x0121, 0x0122, 0x0123, 0x0124,
        0x0125, 0x0126, 0x0127, 0x0129, 0x012A, 0x012B, 0x012E, 0x012F,
        0x0130, 0x0131, 0x0132, 0x0134, 0x0136, 0x0205, 0x0400, 0x0401,
        0x0402, 0x0403, 0x0404, 0x0405, 0x0406, 0x0408, 0x0409, 0x040B,
        0x040D, 0x040E, 0x040F, 0x0411, 0x0412, 0x0413, 0x0415, 0x0416,
        0x0417, 0x0418, 0x0419, 0x041A, 0x041B, 0x041C, 0x041D, 0x041E,
        0x041F, 0x0421, 0x0422, 0x0423, 0x0425, 0x0426, 0x0427, 0x0428,
        0x0429, 0x042E, 0x0431, 0x0433, 0x0F00, 0x0F03, 0x0F04, 0xEF01,
        0xEF02, 0xEF03, 0xEF08, 0xEF09
    ];
    var caps = CommandCapabilities.Parse(bitmap);
    Check(CommandCapabilities.KnownBitCount == 69 && expected.Length == 92);
    Check(caps.SupportedCommands.SequenceEqual(expected) && caps.UnknownSetBits.Count == 0);
    Check(caps.Supports(0x0419) && !caps.Supports(0x0110) && !caps.Supports(0xFFFF));
    Check(caps.RawBitmap.SequenceEqual(bitmap));
    var reply = QueryParsers.Parse(0x0100, new byte[] { 0 }.Concat(bitmap).ToArray());
    Check(reply.Success && ((ushort[])reply.Data["allSupportedCommands"]!).SequenceEqual(expected));
    Check((string)reply.Data["allSupportedCommandsScope"]! == "apk-69-bit-map-and-foundation");
    Check((int)reply.Data["knownCapabilityBitCount"]! == 69 && ((int[])reply.Data["unknownCapabilitySetBits"]!).Length == 0);
    Check(((ushort[])reply.Data["supportedCommands"]!).SequenceEqual(new ushort[]
        { 0x0100, 0x0101, 0x0102, 0x0103, 0x0104, 0x0105, 0x010D, 0x0114 }));
});
Test("Complement X4 fixture covers every remaining known APK mapping", () =>
{
    // Complement of the observed bitmap within its 69 known bits, with the tail clear.
    // Together with the real X4 fixture this exercises every known mapping entry.
    var caps = CommandCapabilities.Parse(Convert.FromHexString("0088A511117059A008"));
    ushort[] expected =
    [
        0x001C, 0x0100, 0x0101, 0x0102, 0x0103, 0x0104, 0x0106, 0x010B,
        0x010D, 0x0119, 0x011D, 0x0133, 0x0135, 0x0407, 0x0410, 0x0414,
        0x0420, 0x0424, 0x042D, 0x0432, 0x0435, 0x0F00, 0x0F03, 0x0F04,
        0xEF04, 0xEF06, 0xEF07
    ];
    Check(caps.SupportedCommands.SequenceEqual(expected) && caps.UnknownSetBits.Count == 0);
    Check(!caps.Supports(0x0419) && !caps.Supports(0x0F01));
});
Test("Foundation is always present and separate APK category is not foundation", () =>
{
    ushort[] foundation = [0x0100, 0x0101, 0x0102, 0x0103, 0x0104, 0x0106, 0x010B, 0x010D, 0x0F00, 0x0F03, 0x0F04];
    var caps = CommandCapabilities.Parse([]);
    Check(caps.SupportedCommands.SequenceEqual(foundation) && caps.UnknownSetBits.Count == 0);
    foreach (ushort command in foundation) Check(caps.Supports(command));
    foreach (ushort command in new ushort[] { 0x0105, 0x0114, 0x0405, 0x040D, 0x040E, 0x0411, 0x0415, 0x0417, 0x0121, 0x0F01, 0x0F02, 0x0F05 })
        Check(!caps.Supports(command));
    foreach (int bit in new[] { 12, 15, 16, 18, 24, 28, 32, 46, 50, 53, 54 })
    {
        byte[] bitmap = new byte[9]; bitmap[bit / 8] = (byte)(1 << (bit % 8));
        var emptyEntry = CommandCapabilities.Parse(bitmap);
        Check(emptyEntry.SupportedCommands.SequenceEqual(foundation) && emptyEntry.UnknownSetBits.Count == 0);
    }
});
Test("Capability last known bit and all unknown tail bits never infer commands", () =>
{
    var baseline = CommandCapabilities.Parse([]);
    byte[] bitmap = new byte[16]; bitmap[8] = 0xF0; bitmap[15] = 0x80;
    var caps = CommandCapabilities.Parse(bitmap);
    Check(caps.Supports(0x0117) && caps.SupportedCommands.Count == baseline.SupportedCommands.Count + 1);
    Check(caps.UnknownSetBits.SequenceEqual(new[] { 69, 70, 71, 127 }));
    bitmap[8] = 0xE0;
    var onlyUnknown = CommandCapabilities.Parse(bitmap);
    Check(onlyUnknown.SupportedCommands.SequenceEqual(baseline.SupportedCommands));
    Check(onlyUnknown.UnknownSetBits.SequenceEqual(new[] { 69, 70, 71, 127 }));
    var reply = QueryParsers.Parse(0x0100, new byte[] { 0 }.Concat(bitmap).ToArray());
    Check(reply.Success && ((int[])reply.Data["unknownCapabilitySetBits"]!).SequenceEqual(new[] { 69, 70, 71, 127 }));
    Check(((ushort[])reply.Data["supportedCommands"]!).Length == 6);
    Check(((ushort[])reply.Data["allSupportedCommands"]!).SequenceEqual(baseline.SupportedCommands));
});
Test("Capability mappings with shared commands deduplicate and preserve input snapshot", () =>
{
    byte[] bitmap = new byte[9]; bitmap[2] = 0x08; bitmap[3] = 0x80;
    var caps = CommandCapabilities.Parse(bitmap);
    Check(caps.Supports(0x040D) && caps.Supports(0x040E) && caps.Supports(0x0115) && caps.Supports(0x0116));
    Check(caps.SupportedCommands.Count == 15 && caps.SupportedCommands.Count(x => x == 0x040D) == 1);
    Array.Clear(bitmap);
    Check(caps.RawBitmap[2] == 0x08 && caps.RawBitmap[3] == 0x80 && caps.Supports(0x0115));
    Check(caps.SupportedCommands is IList<ushort> { IsReadOnly: true });
    Check(caps.UnknownSetBits is IList<int> { IsReadOnly: true });
});
Test("Sequence allocator emits 0 through 255 once without reset or reuse", () =>
{
    var allocator = new SequenceAllocator(); Check(allocator.Remaining == 256);
    for (int expected = 0; expected < 256; expected++)
    {
        Check(allocator.TryTake(out byte sequence) && sequence == expected);
        Check(allocator.Remaining == 255 - expected);
    }
    for (int attempt = 0; attempt < 1024; attempt++) Check(!allocator.TryTake(out _) && allocator.Remaining == 0);
    Check(new SequenceAllocator().TryTake(out byte firstOnNewConnection) && firstOnNewConnection == 0);
});
Test("Concurrent sequence allocation has exactly 256 unique successes then refuses exhaustion", () =>
{
    var allocator = new SequenceAllocator();
    var taken = new System.Collections.Concurrent.ConcurrentBag<byte>();
    int rejected = 0;
    Parallel.For(0, 4096, _ =>
    {
        if (allocator.TryTake(out byte sequence)) taken.Add(sequence);
        else Interlocked.Increment(ref rejected);
        Check(allocator.Remaining is >= 0 and <= 256);
    });
    Check(taken.Count == 256 && rejected == 3840 && allocator.Remaining == 0);
    Check(taken.Order().Select(x => (int)x).SequenceEqual(Enumerable.Range(0, 256)));
    Parallel.For(0, 1024, attempt => Check(!allocator.TryTake(out _) && allocator.Remaining == 0));
});
Test("Response key requires exact response bit command and current sequence", () =>
{
    var key = new RequestKey(0x0103, 10);
    Check(key.MatchesResponse(new Packet(0x8103, 10, [0, 0x10, 0xEC, 6])));
    Check(!key.MatchesResponse(new Packet(0x0103, 10, [])));
    Check(!key.MatchesResponse(new Packet(0x8102, 10, [])));
    Check(!key.MatchesResponse(new Packet(0xC103, 10, [])));
    Check(!key.MatchesResponse(new Packet(0x8103, 9, [])));
    Check(!key.MatchesResponse(new Packet(0x8103, 11, [])));
    Check(!new RequestKey(0x8103, 10).MatchesResponse(new Packet(0x8103, 10, [])));
    Check(!key.MatchesResponse(null!));
});
Test("Independent product ACK wire fixture supports every split and exact correlation", () =>
{
    // Hand-specified outer length, little-endian command, sequence, inner length and payload;
    // decoder expected data is independent of PacketEncoder.
    byte[] wire = Convert.FromHexString("AA0B000003810A04000010EC06");
    for (int split = 0; split <= wire.Length; split++)
    {
        var decoder = new PacketDecoder();
        var packets = decoder.Feed(wire.AsSpan(0, split)).Concat(decoder.Feed(wire.AsSpan(split))).ToArray();
        Check(packets.Length == 1 && packets[0].Command == 0x8103 && packets[0].Sequence == 10);
        Check(packets[0].Payload.SequenceEqual(new byte[] { 0, 0x10, 0xEC, 6 }));
        Check(new RequestKey(0x0103, 10).MatchesResponse(packets[0]));
        Check(!decoder.HasIncompleteMessage); decoder.Complete(); decoder.Complete();
    }
    Check(PacketEncoder.Encode(0x8103, 10, [0, 0x10, 0xEC, 6]).SequenceEqual(wire));
});
Test("Independent two-byte ULEB wire fixture", () =>
{
    byte[] payload = Enumerable.Range(0, 121).Select(x => (byte)x).ToArray();
    byte[] wire = Convert.FromHexString("AA800100000081E77900").Concat(payload).ToArray();
    var decoder = new PacketDecoder();
    Check(decoder.Feed(wire.AsSpan(0, 2)).Count == 0 && decoder.HasIncompleteMessage);
    var packet = decoder.Feed(wire.AsSpan(2)).Single();
    Check(packet.Command == 0x8100 && packet.Sequence == 231 && packet.Payload.SequenceEqual(payload));
    Check(PacketEncoder.Encode(0x8100, 231, payload).SequenceEqual(wire));
    decoder.Complete(); Check(!decoder.HasIncompleteMessage);
});
Test("EOF rejects every truncated independent ACK prefix and resets", () =>
{
    byte[] wire = Convert.FromHexString("AA0B000003810A04000010EC06");
    for (int length = 1; length < wire.Length; length++)
    {
        var decoder = new PacketDecoder();
        Check(decoder.Feed(wire.AsSpan(0, length)).Count == 0 && decoder.HasIncompleteMessage);
        try { decoder.Complete(); throw new Exception("Truncated stream accepted at EOF."); }
        catch (FormatException error) { Check(error.Message == "Malformed protocol frame or fragment sequence."); }
        Check(!decoder.HasIncompleteMessage);
        Check(decoder.Feed(wire).Single().Sequence == 10); decoder.Complete();
    }
    var clean = new PacketDecoder(); clean.Complete(); Check(!clean.HasIncompleteMessage);
    byte[] twoByteLength = Convert.FromHexString("AA8001");
    for (int length = 1; length <= twoByteLength.Length; length++)
    {
        var decoder = new PacketDecoder(); decoder.Feed(twoByteLength.AsSpan(0, length));
        try { decoder.Complete(); throw new Exception("Partial ULEB frame accepted at EOF."); }
        catch (FormatException) { }
        Check(!decoder.HasIncompleteMessage);
    }
});
Test("Independent fragment fixture completes only after final FSN and rejects EOF tails", () =>
{
    // Three fragments split inside the inner header, including a middle fragment;
    // none of these wire bytes comes from PacketEncoder.
    byte[] first = Convert.FromHexString("AA0601000003817C");
    byte[] middle = Convert.FromHexString("AA06020001040000");
    byte[] last = Convert.FromHexString("AA0603000210EC06");
    var decoder = new PacketDecoder();
    Check(decoder.Feed(first).Count == 0 && decoder.HasIncompleteMessage);
    Check(decoder.Feed(middle).Count == 0 && decoder.HasIncompleteMessage);
    var packet = decoder.Feed(last).Single();
    Check(packet.Command == 0x8103 && packet.Sequence == 124 && packet.Payload.SequenceEqual(new byte[] { 0, 0x10, 0xEC, 6 }));
    decoder.Complete(); Check(!decoder.HasIncompleteMessage);
    byte[] wire = first.Concat(middle).Concat(last).ToArray();
    for (int length = 1; length < wire.Length; length++)
    {
        decoder = new PacketDecoder(); Check(decoder.Feed(wire.AsSpan(0, length)).Count == 0);
        Check(decoder.HasIncompleteMessage);
        try { decoder.Complete(); throw new Exception("Missing last fragment accepted at EOF."); }
        catch (FormatException error) { Check(error.Message == "Malformed protocol frame or fragment sequence."); }
        Check(!decoder.HasIncompleteMessage && decoder.Feed(wire).Single().Sequence == 124);
        decoder.Complete();
    }
});
Test("Decoder refuses independent fragment FSN wrap and resets", () =>
{
    var wire = new List<byte>();
    byte[] innerPrefix = [0x05, 0x81, 0x63, 0xFF, 0xFF];
    for (int fsn = 0; fsn < 256; fsn++)
        wire.AddRange(new byte[] { 0xAA, 4, fsn == 0 ? (byte)1 : (byte)2, 0, (byte)fsn, fsn < innerPrefix.Length ? innerPrefix[fsn] : (byte)0 });
    var decoder = new PacketDecoder();
    Check(decoder.Feed(wire.ToArray()).Count == 0 && decoder.HasIncompleteMessage);
    try { decoder.Feed(new byte[] { 0xAA, 4, 3, 0, 0, 0 }); throw new Exception("Wrapped FSN accepted."); }
    catch (FormatException) { }
    Check(!decoder.HasIncompleteMessage);
    Check(decoder.Feed(Convert.FromHexString("AA0700000301000000")).Single().Command == 0x0103);
    decoder.Complete();
});
Test("notification envelopes exclude event IDs and legacy response status", () =>
{
    Check(NotificationParsers.TryParse(new(0x0204,5,[1,1,1,42]), out var current)
        && current!.Kind == DeviceNotificationKind.Battery && current.Data.SequenceEqual(new byte[]{1,1,42}));
    Check(NotificationParsers.TryParse(new(0x8202,5,[0,2,1,3,4]), out var legacy)
        && legacy!.Kind == DeviceNotificationKind.Placement && legacy.Data.SequenceEqual(new byte[]{1,3,4}));
    Check(!NotificationParsers.TryParse(new(0x0204,5,[240,1]),out _));
    Check(!NotificationParsers.IsNotificationCommand(0x8406));
});
Test("notification capability and component lists reject malformed length and duplicate IDs", () =>
{
    Check(NotificationParsers.ParseSupportedEvents([0,3,1,2,15]).SequenceEqual(new byte[]{1,2,15}));
    Check(NotificationParsers.ParseComponentPairs([2,1,42,2,61])[2] == 61);
    foreach(byte[] data in new byte[][]{[0],[1,0],[0,2,1],[0,2,1,1]})
    {
        bool rejected=false;try{NotificationParsers.ParseSupportedEvents(data);}catch(FormatException){rejected=true;}Check(rejected);
    }
    foreach(byte[] data in new byte[][]{[],[2,1,42],[2,1,42,1,61]})
    {
        bool rejected=false;try{NotificationParsers.ParseComponentPairs(data);}catch(FormatException){rejected=true;}Check(rejected);
    }
});
Test("component ignore is opt-in and retains envelope and other duplicate checks", () =>
{
    Check(NotificationParsers.ParseComponentPairs([1,0x1C,255])[0x1C]==255);
    var retained=NotificationParsers.ParseComponentPairs([4,0x1C,0,0x0B,1,0x1C,255,0xFE,7],ignoredId:0x1C);
    Check(retained.Count==2&&retained[0x0B]==1&&retained[0xFE]==7&&!retained.ContainsKey(0x1C));
    Check(NotificationParsers.ParseComponentPairs([2,0x1C,0,0x1C,255],ignoredId:0x1C).Count==0);
    bool duplicateRejected=false;
    try { NotificationParsers.ParseComponentPairs([2,0x1C,0,0x1C,255]); }
    catch (FormatException) { duplicateRejected=true; }
    Check(duplicateRejected);
    foreach(byte[] data in new byte[][]{[],[1,0x1C],[0,0x1C,0],[3,0x1C,0,0x0B,1,0x0B,0]})
    {
        bool rejected=false;try{NotificationParsers.ParseComponentPairs(data,ignoredId:0x1C);}catch(FormatException){rejected=true;}Check(rejected);
    }
});
Test("setting synchronization commands are notification-only, spatial requests invalidate state", () =>
{
    foreach(var entry in new (ushort Command,DeviceNotificationKind Kind)[]{(0x0503,DeviceNotificationKind.Features),
        (0x0504,DeviceNotificationKind.Preset),(0x0506,DeviceNotificationKind.CustomEqualizers),(0x051D,DeviceNotificationKind.SpatialInvalidated)})
        Check(NotificationParsers.TryParse(new(entry.Command,7,[]),out var parsed) && parsed!.Kind==entry.Kind);
    Check(!new RequestKey(0x0406,7).MatchesResponse(new(0x0504,7,[0]))
        && !new RequestKey(0x0403,7).MatchesResponse(new(0x0503,7,[0]))
        && !new RequestKey(0x0418,7).MatchesResponse(new(0x0506,7,[0])));
});
OppoAdvertisementTests.Run(Test);
CalibrationCodecTests.Run(Test);
PacketFramingCompatibilityTests.Run(Test);
Console.WriteLine($"{passed} offline protocol tests passed.");
