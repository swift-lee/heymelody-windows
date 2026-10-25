using System.Text.Json;
using HeyMelody.Diagnostics;

namespace HeyMelodyWindows.App;

/// <summary>Reads the ordinary UI renderer preference before any native window is initialized.</summary>
public static class UiRenderingPreferenceReader
{
    public const string PreferenceName = "uiBasicRenderMode";

    public static bool Load(string? settingsPath = null)
    {
        try
        {
            settingsPath ??= Path.Combine(HeyMelody.Platform.ApplicationPaths.DataRoot, "preferences.json");
            using var document = JsonDocument.Parse(File.ReadAllText(settingsPath));
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty(PreferenceName, out var value)
                && value.ValueKind == JsonValueKind.True;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException
            or ArgumentException or NotSupportedException)
        {
            if (error is FileNotFoundException or DirectoryNotFoundException)
                DiagnosticLog.Debug("Settings", "uiRendering.missing", "Preferences file does not exist; GPU rendering default applies", new { path = settingsPath });
            else
                DiagnosticLog.Warning("Settings", "uiRendering.loadFailed", "UI rendering preference could not be read; GPU default applies", error, new { path = settingsPath });
            return false;
        }
    }
}
