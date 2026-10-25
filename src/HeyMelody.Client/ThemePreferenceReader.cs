using System.Text.Json;
using HeyMelody.Diagnostics;

namespace HeyMelodyWindows.App;

/// <summary>The read-only theme preference shared by the main app and device manager.</summary>
public static class ThemePreferenceReader
{
    public static string Load()
    {
        string? path = null;
        try
        {
            path = Path.Combine(HeyMelody.Platform.ApplicationPaths.DataRoot, "preferences.json");
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("theme", out var value)
                || value.ValueKind != JsonValueKind.String) return "system";
            return value.GetString() is "light" or "dark" ? value.GetString()! : "system";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            if (error is FileNotFoundException or DirectoryNotFoundException)
                DiagnosticLog.Debug("Settings", "theme.missing", "Preferences file does not exist; system theme applies", new { path });
            else
                DiagnosticLog.Warning("Settings", "theme.loadFailed", "Theme preference could not be read; system theme applies", error, new { path });
            return "system";
        }
    }
}
