using System.Collections;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;

namespace HeyMelody.Diagnostics;

/// <summary>Explicit diagnostic fields that can be captured without runtime reflection.</summary>
public static class DiagnosticData
{
    public static JsonNode? From<T>(T value, JsonTypeInfo<T> typeInfo)
    {
        if (!DiagnosticLog.IsEnabled) return null;
        try { return JsonSerializer.SerializeToNode(value, typeInfo); }
        catch (Exception error) { return Failure(value, error); }
    }

    public static JsonObject Object(params (string Name, object? Value)[] fields)
    {
        var result = new JsonObject();
        foreach (var (name, value) in fields)
        {
            try { result.Add(name, ToNode(value)); }
            catch (Exception error) { result[name] = Failure(value, error); }
        }
        return result;
    }

    internal static JsonNode? ToNode(object? value) => value switch
    {
        null => null,
        JsonNode node => node.DeepClone(),
        JsonElement element => JsonNode.Parse(element.GetRawText()),
        string item => JsonValue.Create(item),
        byte[] item => JsonValue.Create(Convert.ToBase64String(item)),
        ReadOnlyMemory<byte> item => JsonValue.Create(Convert.ToBase64String(item.Span)),
        Memory<byte> item => JsonValue.Create(Convert.ToBase64String(item.Span)),
        bool item => JsonValue.Create(item),
        byte item => JsonValue.Create(item),
        sbyte item => JsonValue.Create(item),
        short item => JsonValue.Create(item),
        ushort item => JsonValue.Create(item),
        int item => JsonValue.Create(item),
        uint item => JsonValue.Create(item),
        long item => JsonValue.Create(item),
        ulong item => JsonValue.Create(item),
        float item => JsonValue.Create(item),
        double item => JsonValue.Create(item),
        decimal item => JsonValue.Create(item),
        char item => JsonValue.Create(item),
        DateTime item => JsonValue.Create(item),
        DateTimeOffset item => JsonValue.Create(item),
        Guid item => JsonValue.Create(item),
        Uri item => JsonValue.Create(item.OriginalString),
        TimeSpan item => JsonValue.Create(item.ToString("c")),
        Enum item => EnumNode(item),
        IDictionary items => DictionaryNode(items),
        IEnumerable items => ArrayNode(items),
        _ => ReflectionNode(value)
    };

    private static JsonObject DictionaryNode(IDictionary items)
    {
        var result = new JsonObject();
        foreach (DictionaryEntry item in items)
        {
            if (item.Key is not string name) throw new ArgumentException("Diagnostic dictionary keys must be strings.");
            result.Add(name, ToNode(item.Value));
        }
        return result;
    }

    private static JsonArray ArrayNode(IEnumerable items)
    {
        var result = new JsonArray();
        foreach (var item in items) result.Add(ToNode(item));
        return result;
    }

    private static JsonNode EnumNode(Enum value)
    {
        try { return JsonValue.Create(Convert.ToInt64(value))!; }
        catch (OverflowException) { return JsonValue.Create(Convert.ToUInt64(value))!; }
    }

    [UnconditionalSuppressMessage("Trimming", "IL2026", Justification =
        "The reflection serializer is used only by legacy JIT callers. Native AOT replaces IsReflectionEnabledByDefault with false and removes this branch.")]
    [UnconditionalSuppressMessage("Aot", "IL3050", Justification =
        "The reflection serializer is used only by legacy JIT callers. Native AOT replaces IsReflectionEnabledByDefault with false and removes this branch.")]
    private static JsonNode? ReflectionNode(object value)
    {
        if (JsonSerializer.IsReflectionEnabledByDefault)
            return JsonSerializer.SerializeToNode(value, value.GetType());
        throw new NotSupportedException($"Diagnostic fields must have an explicit JSON representation: {value.GetType().FullName}");
    }

    private static JsonObject Failure(object? value, Exception error)
    {
        string detail;
        try { detail = error.ToString(); }
        catch { detail = error.GetType().FullName ?? "Diagnostic serialization failed"; }
        return new JsonObject { ["serializationError"] = detail, ["valueType"] = value?.GetType().FullName };
    }

    internal static JsonElement Element(JsonNode node)
        => JsonSerializer.SerializeToElement(node, DiagnosticJsonContext.Default.JsonNode);
}
