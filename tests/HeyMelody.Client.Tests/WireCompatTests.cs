using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using HeyMelodyWindows.App;
using HeyMelodyWindows.Host;

// RpcWire.Json combines the generated RpcJsonContext with the reflection resolver; the three option sets below must agree
// on every byte, so switching resolvers can never change what the Host, workers and clients put on the wire.
internal static class WireCompatTests
{
    private static readonly Type[] Roots =
    [
        typeof(BackendRequest), typeof(BackendMessage), typeof(BackendEvent), typeof(BackendSubscription),
        typeof(RpcRequest), typeof(RpcResponse), typeof(UiSnapshot), typeof(UiApplyResult), typeof(UiAction), typeof(UiAncMode),
        typeof(UiCalibrationInput), typeof(UiCalibrationState), typeof(UiCalibrationSpectrum), typeof(UiResourceSettings),
        typeof(UiResourceRedownloadResult), typeof(UiDeviceCatalog), typeof(UiWhitelistSaveResult), typeof(UiArtwork),
        typeof(bool), typeof(UiDevice[]), typeof(List<UiDevice>), typeof(UiHearingProfile[]), typeof(List<UiHearingProfile>),
        typeof(HearingSelectionArgs), typeof(CalibrationCreateArgs), typeof(ArtworkAcquireArgs), typeof(ArtworkReleaseArgs),
        typeof(ArtworkLeaseReply), typeof(UiLocationPermission), typeof(UiRecordedLocation),
        typeof(UiFindHeadphonesDeviceState), typeof(UiFindHeadphonesState), typeof(FindHeadphonesEnabledArgs),
        typeof(FindHeadphonesToneArgs), typeof(UiFindHeadphonesToneResult)
    ];

    // Sparse or legacy inputs per root; "{}" is always added. Names are case-sensitive on the wire.
    private static readonly Dictionary<Type, string[]> Sparse = new()
    {
        [typeof(UiAncMode)] = ["{\"key\":2,\"name\":\"通透\",\"iconKey\":\"transparency\",\"options\":[{\"key\":52,\"name\":\"自适应\"}]}",
            "{\"key\":2,\"name\":\"x\",\"iconKey\":\"y\",\"options\":[],\"description\":null,\"toggle\":null}"],
        [typeof(UiSnapshot)] = ["{\"anc\":2829,\"canSetAnc\":true}", "{\"Identity\":\"x\",\"warnings\":null,\"unknown\":{\"a\":[1,2]}}"],
        [typeof(RpcRequest)] = ["{\"id\":1,\"operation\":\"x\",\"action\":null}"],
        [typeof(RpcResponse)] = ["{\"data\":null}", "{\"id\":3,\"success\":true,\"data\":{\"a\":1}}"],
        [typeof(BackendRequest)] = ["{\"id\":1,\"operation\":\"x\",\"action\":null,\"role\":\"main\"}", "{\"schemaVersion\":4,\"refreshScope\":2}"],
        [typeof(BackendMessage)] = ["{\"kind\":\"event\",\"event\":{\"kind\":\"snapshot\"},\"data\":null}",
            "{\"schemaVersion\":5,\"kind\":\"event\",\"event\":{\"kind\":\"findHeadphones\",\"findHeadphones\":{\"enabled\":false,\"devices\":[],\"revision\":2}}}"],
        [typeof(BackendSubscription)] = ["{\"devices\":null,\"snapshots\":[{}]}",
            "{\"findHeadphones\":{\"enabled\":true,\"permission\":{\"status\":\"allowed\",\"message\":\"\"},\"devices\":[{\"identity\":\"offline\",\"enabled\":true,\"location\":null,\"status\":\"idle\",\"message\":\"\"}],\"revision\":9}}"],
        [typeof(UiRecordedLocation)] = ["{\"latitude\":22.3,\"longitude\":114.2,\"accuracyMeters\":80,\"disconnectedAt\":\"2026-11-14T01:02:03+00:00\",\"positionTimestamp\":\"2026-11-14T01:02:02+00:00\"}"],
        [typeof(UiFindHeadphonesDeviceState)] = ["{\"identity\":\"offline\",\"enabled\":false,\"location\":null,\"status\":\"idle\",\"message\":\"\"}"],
        [typeof(UiFindHeadphonesState)] = ["{\"enabled\":false,\"permission\":null,\"devices\":[],\"revision\":0}"],
        [typeof(UiLocationPermission)] = ["{\"status\":\"systemDisabled\",\"message\":\"定位已关闭\"}"],
        [typeof(FindHeadphonesEnabledArgs)] = ["{\"enabled\":true}"],
        [typeof(UiAction)] = ["{\"kind\":\"Anc\",\"value\":5}"],
        [typeof(HearingSelectionArgs)] = ["{\"uid\":null}", "{\"Uid\":3}"],
        [typeof(CalibrationCreateArgs)] = ["{\"Kind\":\"hearing\"}", "{\"kind\":\"hearing\"}"],
        [typeof(ArtworkAcquireArgs)] = ["{\"productId\":\"06C010\",\"role\":\"hero\"}"],
        [typeof(ArtworkLeaseReply)] = ["{\"artwork\":null}"],
        [typeof(UiDevice[])] = ["[]", "[{\"identity\":\"a\",\"name\":\"b\",\"connected\":true}]"],
        [typeof(List<UiDevice>)] = ["[]", "[{\"identity\":\"a\",\"name\":\"b\",\"connected\":true,\"color\":3}]"],
        [typeof(UiHearingProfile[])] = ["[{\"uid\":null,\"name\":\"x\"}]"],
        [typeof(List<UiHearingProfile>)] = ["[{\"uid\":null,\"name\":\"x\"}]"]
    };

    private sealed record Unlisted(int X, string? Y);
    // Shaped like the Host's private WorkerMessage: unlisted container, listed members served by the generated context.
    private sealed class Envelope
    {
        public string Kind { get; set; } = "response";
        public JsonElement? Data { get; set; }
        public UiSnapshot? Snapshot { get; set; }
        public UiCalibrationState? Calibration { get; set; }
    }

    private static readonly JsonSerializerOptions Plain = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private enum Mode { Reflection, ContextOnly, Wire }
    private static readonly Mode[] Modes = Enum.GetValues<Mode>();

    private static byte[] Typed(Mode mode, object? value, Type type) => mode switch
    {
        Mode.Reflection => JsonSerializer.SerializeToUtf8Bytes(value, type, Plain),
        Mode.ContextOnly => JsonSerializer.SerializeToUtf8Bytes(value, type, RpcJsonContext.Default),
        _ => JsonSerializer.SerializeToUtf8Bytes(value, type, RpcWire.Json)
    };
    private static byte[] AsObject(Mode mode, object? value) => mode switch
    {
        Mode.Reflection => JsonSerializer.SerializeToUtf8Bytes<object?>(value, Plain),
        Mode.ContextOnly => JsonSerializer.SerializeToUtf8Bytes(value, RpcJsonContext.Default.Object),
        _ => JsonSerializer.SerializeToUtf8Bytes<object?>(value, RpcWire.Json)
    };
    private static object? Decode(Mode mode, byte[] json, Type type) => mode switch
    {
        Mode.Reflection => JsonSerializer.Deserialize(json, type, Plain),
        Mode.ContextOnly => JsonSerializer.Deserialize(json, type, RpcJsonContext.Default),
        _ => JsonSerializer.Deserialize(json, type, RpcWire.Json)
    };

    // Either the canonical bytes or the exception type, so inputs that throw must throw everywhere.
    private static string Outcome(Mode decoder, Mode encoder, byte[] json, Type type)
    {
        try { return Convert.ToHexString(Typed(encoder, Decode(decoder, json, type), type)); }
        catch (Exception error) when (error is JsonException or InvalidOperationException or NotSupportedException or ArgumentException)
        { return "EXC:" + error.GetType().Name; }
    }

    internal static async Task RunAsync(Func<string, Func<Task>, Task> test)
    {
        await test("wire options: the reflection baseline has exactly RpcWire.Json's settings and RpcWire.Json uses the generated context", () =>
        {
            var skipped = new HashSet<string> { nameof(JsonSerializerOptions.TypeInfoResolver), nameof(JsonSerializerOptions.TypeInfoResolverChain),
                nameof(JsonSerializerOptions.Converters), nameof(JsonSerializerOptions.IsReadOnly) };
            foreach (var property in typeof(JsonSerializerOptions).GetProperties(BindingFlags.Public | BindingFlags.Instance))
                if (!skipped.Contains(property.Name) && property.GetIndexParameters().Length == 0)
                    Check(Equals(property.GetValue(Plain), property.GetValue(RpcWire.Json)), "Option " + property.Name + " differs from RpcWire.Json.");
            Check(Plain.Converters.Count == 0 && RpcWire.Json.Converters.Count == 0 && Plain.Encoder is null && RpcWire.Json.Encoder is null);
            Check(RpcWire.Json.GetTypeInfo(typeof(UiSnapshot)) is JsonTypeInfo<UiSnapshot> { SerializeHandler: not null }, "UiSnapshot is not served by the generated context.");
            Check(RpcWire.Json.GetTypeInfo(typeof(Unlisted)) is JsonTypeInfo<Unlisted> { SerializeHandler: null }, "Unlisted types must fall back to reflection.");
            Check(JsonSerializer.Serialize(new Unlisted(1, null), RpcWire.Json) == JsonSerializer.Serialize(new Unlisted(1, null), Plain));
            var envelope = new Envelope { Kind = "snapshot", Snapshot = FixtureFiller.Full<UiSnapshot>(), Calibration = FixtureFiller.Sparse<UiCalibrationState>(),
                Data = JsonSerializer.SerializeToElement(FixtureFiller.Full<UiApplyResult>(), RpcWire.Json) };
            string envelopeJson = JsonSerializer.Serialize(envelope, Plain);
            Check(JsonSerializer.Serialize(envelope, RpcWire.Json) == envelopeJson, "A reflection-only container with generated members changed its bytes.");
            Check(JsonSerializer.Serialize(JsonSerializer.Deserialize<Envelope>(envelopeJson, RpcWire.Json), RpcWire.Json)
                == JsonSerializer.Serialize(JsonSerializer.Deserialize<Envelope>(envelopeJson, Plain), Plain));
            try { JsonSerializer.Serialize(new Unlisted(1, null), typeof(Unlisted), RpcJsonContext.Default); Check(false, "Strict mode accepted an unlisted type."); }
            catch (InvalidOperationException error) when (error.Message.Contains("metadata for type")) { }
            return Task.CompletedTask;
        });

        await test("wire bytes: every root type serializes identically by reflection, by context only and through RpcWire.Json", () =>
        {
            foreach (var type in Roots)
            {
                foreach (var (value, _) in Fixtures(type))
                {
                    var expected = Typed(Mode.Reflection, value, type);
                    foreach (var mode in Modes)
                    {
                        Check(expected.AsSpan().SequenceEqual(Typed(mode, value, type)), type.Name + " typed bytes differ in " + mode);
                        Check(expected.AsSpan().SequenceEqual(AsObject(mode, value)), type.Name + " object bytes differ in " + mode);
                    }
                    Check(JsonSerializer.SerializeToElement(value, type, RpcWire.Json).GetRawText() == Encoding.UTF8.GetString(expected), type.Name + " element text differs.");
                    Check(JsonSerializer.SerializeToElement<object?>(value, RpcWire.Json).GetRawText() == Encoding.UTF8.GetString(expected), type.Name + " object element text differs.");
                }
            }
            Check(Encoding.UTF8.GetString(Typed(Mode.Wire, FixtureFiller.Full<UiSnapshot>(), typeof(UiSnapshot))).Contains("\\u8033\\u673A"),
                "Non-ASCII text stays escaped like the reflection writer.");
            return Task.CompletedTask;
        });

        await test("wire decode: decoding then re-encoding agrees across every decoder and encoder, and full fixtures round trip", () =>
        {
            foreach (var type in Roots)
                foreach (var (value, full) in Fixtures(type))
                {
                    var bytes = Typed(Mode.Reflection, value, type);
                    string canonical = Outcome(Mode.Reflection, Mode.Reflection, bytes, type);
                    Check(!canonical.StartsWith("EXC:"), type.Name + " fixture failed to decode.");
                    foreach (var decoder in Modes) foreach (var encoder in Modes)
                        Check(Outcome(decoder, encoder, bytes, type) == canonical, type.Name + " decode " + decoder + " / encode " + encoder + " differs.");
                    if (full) Check(canonical == Convert.ToHexString(bytes), type.Name + " full fixture did not round trip.");
                }
            return Task.CompletedTask;
        });

        await test("wire decode: sparse, legacy and unknown-member JSON decodes to identical state in every mode", () =>
        {
            foreach (var type in Roots)
            {
                var inputs = new List<string> { type.IsArray || type.IsGenericType ? "[]" : "{}" };
                if (Sparse.TryGetValue(type, out var extra)) inputs.AddRange(extra);
                foreach (var input in inputs)
                {
                    var bytes = Encoding.UTF8.GetBytes(input);
                    string canonical = Outcome(Mode.Reflection, Mode.Reflection, bytes, type);
                    foreach (var decoder in Modes) foreach (var encoder in Modes)
                        Check(Outcome(decoder, encoder, bytes, type) == canonical, type.Name + " " + input + " decode " + decoder + " / encode " + encoder + " differs.");
                }
            }
            var legacy = (UiAncMode)Decode(Mode.Wire, Encoding.UTF8.GetBytes(Sparse[typeof(UiAncMode)][0]), typeof(UiAncMode))!;
            Check(legacy.Description == "" && legacy.Toggle is null && legacy.Options.Count == 1, "An older UiAncMode payload keeps an empty description.");
            return Task.CompletedTask;
        });

        await test("wire decode: malformed records fail with the same exception type in every mode", () =>
        {
            string[] malformed = ["{\"id\":\"abc\"}", "{\"success\":\"yes\"}", "[1,2]", "{\"identity\":5}", "nul", "{\"a\":", "{\"anc\":99999999999}",
                "{\"snapshot\":[]}", "{\"data\":{", "\"text\"", "null", "{\"refreshScope\":\"Full\",\"deviceGeneration\":1.5}"];
            foreach (var type in Roots)
                foreach (var input in malformed)
                {
                    var bytes = Encoding.UTF8.GetBytes(input);
                    string canonical = Outcome(Mode.Reflection, Mode.Reflection, bytes, type);
                    foreach (var decoder in Modes)
                        Check(Outcome(decoder, Mode.Reflection, bytes, type) == canonical, type.Name + " " + input + " decodes differently in " + decoder + ".");
                }
            return Task.CompletedTask;
        });

        await test("wire payloads: named request records are byte-identical to the anonymous objects they replaced", () =>
        {
            foreach (int? uid in new int?[] { 7, null })
                Check(Bytes(new HearingSelectionArgs(uid)) == Bytes(new { uid }));
            Check(Bytes(new CalibrationCreateArgs("hearing")) == Bytes(new { kind = "hearing" }));
            foreach (int? color in new int?[] { 3, null })
                Check(Bytes(new ArtworkAcquireArgs("06C010", color, "模型")) == Bytes(new { productId = "06C010", color, role = "模型" }));
            Check(Bytes(new ArtworkReleaseArgs("lease\"1")) == Bytes(new { leaseId = "lease\"1" }));
            var artwork = FixtureFiller.Full<UiArtwork>();
            Check(Bytes(new ArtworkLeaseReply(artwork)) == Bytes(new { artwork }) && Bytes(new ArtworkLeaseReply(null)) == Bytes(new { artwork = (UiArtwork?)null }));
            var request = new BackendRequest();
            Check(request.Action.ValueKind == JsonValueKind.Null && request.Action.GetRawText() == "null"
                && request.Action.GetRawText() == JsonSerializer.SerializeToElement<object?>(null).GetRawText());
            Check(JsonSerializer.Serialize(request, RpcWire.Json).Contains("\"action\":null"));
            return Task.CompletedTask;
        });
    }

    // Full fixtures must round trip exactly; sparse ones (nulls in non-nullable members) only have to agree across modes.
    private static (object Value, bool Full)[] Fixtures(Type type) => type == typeof(bool)
        ? [(true, true), (false, true)]
        : [(FixtureFiller.Full(type), true), (FixtureFiller.Sparse(type), false), (FixtureFiller.Full(type), true)];

    private static string Bytes(object value) => JsonSerializer.Serialize(value, RpcWire.Json);
    private static void Check(bool value, string reason = "Wire compatibility assertion failed.") { if (!value) throw new InvalidOperationException(reason); }
}
