using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace HeyMelody.Diagnostics;

/// <summary>Replaces credentials with a length and hash prefix so a log can show whether a key was set or changed.</summary>
internal static class DiagnosticRedaction
{
    // Property names are matched case-insensitively at any depth, including inside embedded JSON strings such as raw IPC lines.
    private static readonly HashSet<string> SensitiveNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "heyMelodyKey", "cacheEncryptionKey", "cloudKey", "sign"
    };

    internal static bool IsSensitive(string name) => SensitiveNames.Contains(name);

    internal static JsonNode Describe(string? value)
    {
        if (string.IsNullOrEmpty(value)) return new JsonObject { ["redacted"] = true, ["length"] = 0 };
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant()[..12];
        return new JsonObject { ["redacted"] = true, ["length"] = value.Length, ["sha256Prefix"] = hash };
    }

    /// <summary>Returns true when <paramref name="node"/> or one of its descendants was rewritten.</summary>
    internal static bool Redact(JsonNode? node, int depth = 0)
    {
        if (node is null || depth > 64) return false;
        bool changed = false;
        switch (node)
        {
            case JsonObject value:
                foreach (var (name, child) in value.ToArray())
                {
                    if (IsSensitive(name) && child is not null && child.GetValueKind() is not JsonValueKind.Object and not JsonValueKind.Array)
                    {
                        value[name] = Describe(child.GetValueKind() == JsonValueKind.String ? child.GetValue<string>() : child.ToJsonString());
                        changed = true;
                    }
                    else if (TryRedactChild(child, depth, out var replacement))
                    {
                        if (replacement is not null) value[name] = replacement;
                        changed = true;
                    }
                }
                break;
            case JsonArray array:
                for (int index = 0; index < array.Count; index++)
                    if (TryRedactChild(array[index], depth, out var replacement))
                    {
                        if (replacement is not null) array[index] = replacement;
                        changed = true;
                    }
                break;
        }
        return changed;
    }

    private static bool TryRedactChild(JsonNode? child, int depth, out JsonNode? replacement)
    {
        replacement = null;
        if (child is JsonObject or JsonArray) return Redact(child, depth + 1);
        if (child is not JsonValue leaf || leaf.GetValueKind() != JsonValueKind.String) return false;
        var text = leaf.GetValue<string>();
        if (!LooksLikeJson(text)) return false;
        JsonNode? embedded;
        try { embedded = JsonNode.Parse(text); }
        catch (JsonException) { return false; }
        // Rewrite embedded text only when it held a credential, so ordinary raw lines stay byte-for-byte original.
        if (!Redact(embedded, depth + 1)) return false;
        replacement = JsonValue.Create(embedded!.ToJsonString());
        return true;
    }

    private static bool LooksLikeJson(string text)
    {
        var trimmed = text.AsSpan().Trim();
        return trimmed.Length >= 2 && ((trimmed[0] == '{' && trimmed[^1] == '}') || (trimmed[0] == '[' && trimmed[^1] == ']'));
    }
}
