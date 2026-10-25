using System.Text.Json;
using System.Text.Json.Nodes;
using HeyMelody.Diagnostics;

namespace HeyMelodyWindows.App;

internal static class ThemePreferences
{
    internal static string? SettingsPathOverride { get; set; }
    private static string SettingsPath => SettingsPathOverride ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HeyMelody", "preferences.json");

    public static string Load()
    {
        var value = Read()["theme"];
        return value?.GetValueKind() == JsonValueKind.String && value.GetValue<string>() is { } theme
            && theme is "light" or "dark" ? theme : "system";
    }

    // Only the JSON boolean true enables compatibility. Legacy GPU settings have no meaning here.
    internal static bool LoadModelCompatibilityMode() => Read()["modelCompatibilityMode"]?.GetValueKind() == JsonValueKind.True;

    internal static bool LoadUiBasicRenderMode() => UiRenderingPreferenceReader.Load(SettingsPath);

    internal static ModelFrameRate LoadModelFrameRate() => ParseModelFrameRate(Read()["modelFrameRate"]);

    // The former "display" and "120" choices, missing values and malformed values use the unlimited default.
    private static ModelFrameRate ParseModelFrameRate(JsonNode? value)
    {
        return value?.GetValueKind() == JsonValueKind.String ? value.GetValue<string>() switch
        {
            "60" => ModelFrameRate.Fps60,
            "30" => ModelFrameRate.Fps30,
            _ => ModelFrameRate.Unlimited
        } : ModelFrameRate.Unlimited;
    }

    internal static string Serialize(ModelFrameRate rate) => rate switch
    {
        ModelFrameRate.Fps60 => "60",
        ModelFrameRate.Fps30 => "30",
        _ => "unlimited"
    };

    public static bool Save(string theme, bool? modelCompatibilityMode = null, ModelFrameRate? modelFrameRate = null,
        bool? uiBasicRenderMode = null)
    {
        string? temporary = null;
        try
        {
            var preferences = Read();
            preferences["theme"] = theme;
            preferences.Remove("modelGpuAcceleration");
            if (modelCompatibilityMode is { } enabled) preferences["modelCompatibilityMode"] = enabled;
            if (uiBasicRenderMode is { } basicRender) preferences[UiRenderingPreferenceReader.PreferenceName] = basicRender;
            preferences["modelFrameRate"] = Serialize(modelFrameRate ?? ParseModelFrameRate(preferences["modelFrameRate"]));
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            temporary = SettingsPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(temporary, preferences.ToJsonString());
            File.Move(temporary, SettingsPath, overwrite: true);
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            DiagnosticLog.Warning("Settings", "preferences.saveFailed", "Preferences could not be saved", error, new { path = SettingsPath });
            return false;
        }
        finally
        {
            if (temporary is not null)
                try { File.Delete(temporary); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                { DiagnosticLog.Debug("Settings", "preferences.tempDeleteFailed", "Temporary preferences file could not be deleted", new { path = temporary, failure = error.Message }); }
        }
    }

    private static JsonObject Read()
    {
        try { return JsonNode.Parse(File.ReadAllText(SettingsPath)) as JsonObject ?? new(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            if (error is FileNotFoundException or DirectoryNotFoundException)
                DiagnosticLog.Debug("Settings", "preferences.missing", "Preferences file does not exist; defaults apply", new { path = SettingsPath });
            else
                DiagnosticLog.Warning("Settings", "preferences.loadFailed", "Preferences could not be read; defaults apply", error, new { path = SettingsPath });
            return new();
        }
    }
}
