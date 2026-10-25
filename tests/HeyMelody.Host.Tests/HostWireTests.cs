using HeyMelody.Platform;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using System.Text.RegularExpressions;
using HeyMelodyWindows.App;
using HeyMelodyWindows.Host;

// HostWire.Json serves the worker stdio types from generated metadata; the pure-reflection copy is the byte baseline of the
// previous implementation. Every instance and input below must give identical bytes through both resolvers.
internal static class HostWireTests
{
    private static readonly JsonSerializerOptions Reflection = new(RpcWire.Json) { TypeInfoResolver = new DefaultJsonTypeInfoResolver() };
    private static readonly JsonSerializerOptions[] Both = [HostWire.Json, Reflection];
    private static readonly Type[] Roots =
        [typeof(WorkerRequest), typeof(WorkerMessage), typeof(List<BluetoothCandidate>), typeof(IReadOnlyList<BluetoothCandidate>)];

    internal static async Task RunAsync(Func<string, Func<Task>, Task> test)
    {
        await test("worker wire serves its types from generated metadata while the reflection copy stays a real baseline", () =>
        {
            var host = HostWire.Json;
            Check(host.PropertyNamingPolicy == JsonNamingPolicy.CamelCase && host.DefaultIgnoreCondition == JsonIgnoreCondition.WhenWritingNull
                && !host.PropertyNameCaseInsensitive && host.Converters.Count == 0 && host.Encoder is null && !host.WriteIndented && !host.IncludeFields
                && host.NumberHandling == JsonNumberHandling.Strict && host.UnmappedMemberHandling == JsonUnmappedMemberHandling.Skip,
                "Host options no longer equal the generation options, so the generated writers would be bypassed");
            Check(host.PropertyNamingPolicy == RpcWire.Json.PropertyNamingPolicy && host.DefaultIgnoreCondition == RpcWire.Json.DefaultIgnoreCondition
                && host.PropertyNameCaseInsensitive == RpcWire.Json.PropertyNameCaseInsensitive, "Host options diverged from RpcWire.Json");
            var generated = HostJsonContext.Default.Options;
            Check(generated.PropertyNamingPolicy == host.PropertyNamingPolicy && generated.DefaultIgnoreCondition == host.DefaultIgnoreCondition
                && generated.PropertyNameCaseInsensitive == host.PropertyNameCaseInsensitive && generated.NumberHandling == host.NumberHandling
                && generated.IncludeFields == host.IncludeFields && generated.WriteIndented == host.WriteIndented, "HostJsonContext generation options differ from the runtime options");
            foreach (var type in Roots)
            {
                Check(ReferenceEquals(host.GetTypeInfo(type).OriginatingResolver, HostJsonContext.Default), type.Name + " is not served by HostJsonContext");
                Check(Reflection.GetTypeInfo(type).OriginatingResolver is DefaultJsonTypeInfoResolver, type.Name + " baseline is not pure reflection");
            }
            foreach (var type in new[] { typeof(UiSnapshot), typeof(UiCalibrationState), typeof(UiCalibrationSpectrum), typeof(UiAction), typeof(UiCalibrationInput) })
                Check(ReferenceEquals(host.GetTypeInfo(type).OriginatingResolver, RpcJsonContext.Default), type.Name + " is not shared with RpcJsonContext");
            Check(((JsonTypeInfo<WorkerRequest>)host.GetTypeInfo(typeof(WorkerRequest))).SerializeHandler is not null
                && ((JsonTypeInfo<WorkerMessage>)host.GetTypeInfo(typeof(WorkerMessage))).SerializeHandler is not null
                && ((JsonTypeInfo<WorkerRequest>)Reflection.GetTypeInfo(typeof(WorkerRequest))).SerializeHandler is null, "Generated fast path is not available");
            return Task.CompletedTask;
        });
        await test("worker wire bytes equal the pure-reflection baseline for full, sparse and mixed instances", () =>
        {
            foreach (var (name, type, value) in Instances())
            {
                byte[] baseline = Write(value, type, Reflection);
                Same(name + " generated", Write(value, type, HostWire.Json), baseline);
                Check(JsonSerializer.Serialize(value, type, HostWire.Json) == Show(baseline), name + ": string output differs");
                Check(JsonSerializer.SerializeToElement<object>(value, HostWire.Json).GetRawText() == Show(baseline), name + ": object-typed output differs");
                Check(JsonSerializer.SerializeToElement<object>(value, Reflection).GetRawText() == Show(baseline), name + ": reflection object-typed output differs");
            }
            // The fixtures must exercise what the comparison is for: escapes, non-ASCII text, non-zero offsets and nested payloads.
            string full = Show(Write(FixtureFiller.Full<WorkerMessage>(), typeof(WorkerMessage), HostWire.Json));
            Check(full.Length > 4000 && full.All(c => c < 0x80), "Full message fixture is too small or leaves raw non-ASCII");
            foreach (string escape in new[] { "\\u8033", "\\u0022", "\\\\", "\\u003C", "\\u0026", "\\u0027", "\\u0001", "\\uD83D\\uDE00", "\\u2028", "\\n" })
                Check(full.Contains(escape, StringComparison.Ordinal), "Full message fixture lacks " + escape);
            Check(Regex.Matches(full, "T[0-9:.]+([+-][0-9]{2}:[0-9]{2})\"").Select(match => match.Groups[1].Value).Where(offset => offset != "+00:00").Distinct().Count() >= 3,
                "Full message fixture lacks distinct non-zero offsets");
            foreach (string section in new[] { "\"snapshot\":{", "\"calibration\":{", "\"calibrationSpectrum\":{", "\"data\":{", "\"fitResults\":[{", "\"equalizerEntries\":[{", "\"featureSections\":[{" })
                Check(full.Contains(section, StringComparison.Ordinal), "Full message fixture lacks " + section);
            string sparse = Show(Write(FixtureFiller.Sparse<WorkerMessage>(), typeof(WorkerMessage), HostWire.Json));
            Check(!sparse.Contains("\"snapshot\"", StringComparison.Ordinal) && !sparse.Contains(":null", StringComparison.Ordinal), "Sparse message fixture still carries nested or null members");
            return Task.CompletedTask;
        });
        await test("worker wire decodes and re-encodes like the reflection baseline for populated, sparse and legacy input", () =>
        {
            foreach (var (name, type, value) in Instances())
            {
                byte[] original = Write(value, type, Reflection);
                byte[] expected = Write(JsonSerializer.Deserialize(original, type, Reflection)!, type, Reflection);
                if (name.StartsWith("full", StringComparison.Ordinal)) Same(name + " lossless round trip", expected, original);
                foreach (var decoder in Both)
                    Reencode(name, type, original, decoder, expected);
            }
            var inputs = new (Type Type, string[] Json)[]
            {
                (typeof(WorkerRequest),
                [
                    "{}", "{\"id\":7,\"operation\":\"apply\",\"scope\":2,\"action\":{\"kind\":\"Anc\",\"value\":5},\"unknown\":{\"a\":[1]},\"Identity\":\"ignored\"}",
                    "{\"id\":5,\"id\":6,\"catalogGzip\":null,\"input\":null,\"hearingUid\":null,\"observedColor\":255}",
                    "{\"input\":{\"action\":\"x\",\"heard\":true},\"action\":{\"kind\":\"k\",\"value\":1,\"equalizerEntry\":{\"id\":1,\"name\":\"n\",\"minimumGain\":-6,\"maximumGain\":6,\"selected\":true,\"bands\":[{\"frequencyHz\":100,\"gain\":1}]}}}"
                ]),
                (typeof(WorkerMessage),
                [
                    "{}", "{\"kind\":\"event\",\"id\":3,\"snapshot\":{\"anc\":2829,\"warnings\":null,\"unknown\":[1]},\"data\":null,\"error\":\"错误\\\"x\\\"\"}",
                    "{\"calibration\":{},\"calibrationSpectrum\":{\"levels\":[1,2.5]},\"data\":{\"a\":[1,2,{\"b\":null}]}}",
                    "{\"success\":true,\"snapshot\":{\"sampleTime\":\"2026-10-01T08:30:15.1234567+05:30\",\"presentationSampleTime\":null}}"
                ]),
                (typeof(List<BluetoothCandidate>), ["[]", "[{}]", "[{\"address\":1,\"name\":\"x\",\"connected\":true}]", "[null]", "[{\"Address\":1,\"authenticated\":true}]"]),
                (typeof(IReadOnlyList<BluetoothCandidate>), ["[]", "[{}]", "[{\"address\":18838586676582,\"name\":\"\\u8033\",\"classOfDevice\":4294967295}]", "[null]"])
            };
            foreach (var (type, texts) in inputs)
                foreach (string json in texts)
                {
                    byte[] bytes = Encoding.UTF8.GetBytes(json);
                    byte[] expected = Write(JsonSerializer.Deserialize(bytes, type, Reflection)!, type, Reflection);
                    foreach (var decoder in Both) Reencode(type.Name + " " + json, type, bytes, decoder, expected);
                }
            foreach (var decoder in Both)
            {
                var request = (WorkerRequest)JsonSerializer.Deserialize("{}", typeof(WorkerRequest), decoder)!;
                Check(request.AppMarker == 17006004 && request.Operation == "" && request.Name == "" && request.Action is null && request.HearingUid is null, "Sparse request lost its initializers");
                Check(((WorkerMessage)JsonSerializer.Deserialize("{}", typeof(WorkerMessage), decoder)!).Kind == "response", "Sparse message lost its Kind initializer");
                foreach (var type in Roots) Check(JsonSerializer.Deserialize("null", type, decoder) is null, type.Name + " did not decode null to null");
            }
            // Malformed or mistyped records must fail the same way through both resolvers.
            foreach (var (type, json) in new (Type, string)[]
            {
                (typeof(WorkerRequest), "{"), (typeof(WorkerRequest), "{\"id\":\"x\"}"), (typeof(WorkerRequest), "[]"), (typeof(WorkerMessage), "{\"success\":1}"),
                (typeof(WorkerMessage), "{\"snapshot\":[]}"), (typeof(WorkerMessage), "{\"calibrationSpectrum\":{\"levels\":\"x\"}}"),
                (typeof(List<BluetoothCandidate>), "{}"), (typeof(List<BluetoothCandidate>), "[1]"), (typeof(IReadOnlyList<BluetoothCandidate>), "[{\"address\":-1}]"),
                (typeof(IReadOnlyList<BluetoothCandidate>), "[{\"classOfDevice\":4294967296}]")
            })
                foreach (var decoder in Both)
                {
                    bool rejected = false;
                    try { JsonSerializer.Deserialize(json, type, decoder); } catch (JsonException) { rejected = true; }
                    Check(rejected, type.Name + " accepted " + json);
                }
            return Task.CompletedTask;
        });
        await test("worker wire keeps the documented byte shape", () =>
        {
            Check(Show(Write(new WorkerRequest { Id = 3, Operation = "refresh", Name = "耳机\"", Scope = RefreshScope.Anc, Action = new UiAction("Anc", 2829) }, typeof(WorkerRequest), HostWire.Json))
                == "{\"id\":3,\"operation\":\"refresh\",\"address\":0,\"identity\":\"\",\"name\":\"\\u8033\\u673A\\u0022\",\"scope\":3,\"action\":{\"kind\":\"Anc\",\"value\":2829,\"allowMutexChange\":false},"
                + "\"resourceRevision\":0,\"appMarker\":17006004,\"stableKey\":\"\",\"generation\":0,\"handle\":\"\",\"revision\":0,\"calibrationKind\":\"\"}", "WorkerRequest bytes changed");
            Check(Show(Write(new WorkerMessage { Kind = "event", Id = 4, Success = true, Error = "a<b" }, typeof(WorkerMessage), HostWire.Json))
                == "{\"kind\":\"event\",\"id\":4,\"success\":true,\"error\":\"a\\u003Cb\"}", "WorkerMessage bytes changed");
            Check(Show(Write(new List<BluetoothCandidate> { new(0x112233445566, "耳机", true, false, true, 0x240404) }, typeof(List<BluetoothCandidate>), HostWire.Json))
                == "[{\"address\":18838586676582,\"name\":\"\\u8033\\u673A\",\"connected\":true,\"remembered\":false,\"authenticated\":true,\"classOfDevice\":2360324}]", "Candidate list bytes changed");
            var snapshot = new UiSnapshot
            {
                SampleTime = new DateTimeOffset(2026, 10, 1, 8, 30, 15, TimeSpan.FromMinutes(330)).AddTicks(1230000),
                PresentationSampleTime = new DateTimeOffset(2026, 10, 2, 23, 0, 0, TimeSpan.FromHours(-3))
            };
            using var document = JsonDocument.Parse(Write(new WorkerMessage { Snapshot = snapshot }, typeof(WorkerMessage), HostWire.Json));
            var wire = document.RootElement.GetProperty("snapshot");
            Check(wire.GetProperty("sampleTime").GetString() == "2026-10-01T08:30:15.123+05:30" && wire.GetProperty("presentationSampleTime").GetString() == "2026-10-02T23:00:00-03:00"
                && !wire.TryGetProperty("observationTime", out _), "Offset or null handling changed");
            return Task.CompletedTask;
        });
    }

    private static IEnumerable<(string Name, Type Type, object Value)> Instances()
    {
        foreach (var type in Roots)
        {
            string label = type.IsInterface ? "IReadOnlyList" : type.Name;
            yield return ("full " + label, type, FixtureFiller.Full(type));
            yield return ("sparse " + label, type, FixtureFiller.Sparse(type));
        }
        yield return ("default request", typeof(WorkerRequest), new WorkerRequest());
        yield return ("default message", typeof(WorkerMessage), new WorkerMessage());
        yield return ("request with sparse action and input", typeof(WorkerRequest), new WorkerRequest
        {
            Id = 1, Operation = "apply", Name = "耳机", Scope = RefreshScope.Information, Action = FixtureFiller.Sparse<UiAction>(), Input = FixtureFiller.Sparse<UiCalibrationInput>()
        });
        yield return ("request with full action and input only", typeof(WorkerRequest), new WorkerRequest
        {
            Id = long.MaxValue, Address = ulong.MaxValue, Action = FixtureFiller.Full<UiAction>(), Input = FixtureFiller.Full<UiCalibrationInput>(), ObservedColor = 0
        });
        yield return ("message with sparse nested payloads", typeof(WorkerMessage), new WorkerMessage
        {
            Kind = "event", Id = 9, Success = true, Snapshot = FixtureFiller.Sparse<UiSnapshot>(),
            Calibration = FixtureFiller.Sparse<UiCalibrationState>(), CalibrationSpectrum = FixtureFiller.Sparse<UiCalibrationSpectrum>()
        });
        yield return ("message with full snapshot and escaped error", typeof(WorkerMessage), new WorkerMessage
        {
            Snapshot = FixtureFiller.Full<UiSnapshot>(), Error = "设备「x」\"失败\"\n<b>\u2028\u0001"
        });
        yield return ("message with only calibration state", typeof(WorkerMessage), new WorkerMessage { Calibration = FixtureFiller.Full<UiCalibrationState>() });
        yield return ("message with only spectrum", typeof(WorkerMessage), new WorkerMessage { Success = true, CalibrationSpectrum = FixtureFiller.Full<UiCalibrationSpectrum>() });
        yield return ("message with mixed data element", typeof(WorkerMessage), new WorkerMessage
        {
            Success = true, Data = JsonDocument.Parse("[true,null,1.50,{\"中\":\"\\u0000\\\"\\n\"}, \"x\" ]").RootElement.Clone()
        });
        yield return ("empty list", typeof(List<BluetoothCandidate>), new List<BluetoothCandidate>());
        yield return ("empty array as read-only list", typeof(IReadOnlyList<BluetoothCandidate>), Array.Empty<BluetoothCandidate>());
        yield return ("candidate with null name", typeof(List<BluetoothCandidate>), new List<BluetoothCandidate> { new(1, null!, false, false, false, 0) });
        yield return ("array as read-only list", typeof(IReadOnlyList<BluetoothCandidate>), new[]
        {
            new BluetoothCandidate(0x112233445566, "耳机 \"A\" <&>", true, false, true, 0x240404),
            new BluetoothCandidate(0, "", false, true, false, 0),
            new BluetoothCandidate(ulong.MaxValue, "😀\u2028\u0001", false, false, true, uint.MaxValue)
        });
    }

    private static byte[] Write(object value, Type type, JsonSerializerOptions options) => JsonSerializer.SerializeToUtf8Bytes(value, type, options);
    private static string Show(byte[] bytes) => Encoding.UTF8.GetString(bytes);

    // Decodes through byte, string and element input, then re-encodes through both resolvers.
    private static void Reencode(string name, Type type, byte[] input, JsonSerializerOptions decoder, byte[] expected)
    {
        using var document = JsonDocument.Parse(input);
        object?[] decoded = [JsonSerializer.Deserialize(input, type, decoder), JsonSerializer.Deserialize(Show(input), type, decoder), document.RootElement.Deserialize(type, decoder)];
        foreach (var value in decoded)
            foreach (var encoder in Both) Same(name + " re-encode", Write(value!, type, encoder), expected);
    }

    private static void Same(string name, byte[] actual, byte[] expected)
    {
        if (actual.AsSpan().SequenceEqual(expected)) return;
        int at = actual.AsSpan().CommonPrefixLength(expected), from = Math.Max(0, at - 40);
        string Around(byte[] bytes) => Show(bytes[from..Math.Min(bytes.Length, at + 60)]);
        throw new InvalidOperationException($"{name}: bytes differ at {at} (lengths {actual.Length}/{expected.Length}): ...{Around(actual)}... vs ...{Around(expected)}...");
    }

    private static void Check(bool value, string error) { if (!value) throw new InvalidOperationException(error); }
}
