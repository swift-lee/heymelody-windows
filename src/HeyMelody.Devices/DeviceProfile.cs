using System.Text.Json;

namespace HeyMelodyWindows.Devices;

public sealed class DeviceProfile : SourceMetadata
{
    internal DeviceProfile(JsonElement source, SourceProvenance provenance) : base(source)
    {
        ProductId = ValidProductId(source);
        Name = ProfileJson.Text(source, "name") ?? ProductId;
        Aliases = Array.AsReadOnly(ProfileJson.Array(source, "children").Select(x => ProfileJson.Text(x, "name")).OfType<string>().Where(x => x.Length > 0).ToArray());
        Uuid = Guid.TryParse(ProfileJson.Text(source, "uuid"), out var parsed) ? parsed : Guid.Empty;
        SupportSpp = ProfileJson.Property(source, "supportSpp", out var spp) && spp.ValueKind == JsonValueKind.True;
        Provenance = provenance;
        Conditions = ProfileJson.Conditions(source);
        using var emptyFunctions = JsonDocument.Parse("{}");
        var functions = ProfileJson.Property(source, "function", out var f) && f.ValueKind == JsonValueKind.Object ? f : emptyFunctions.RootElement;
        _rawFunctions = functions.Clone();
        HighToneQuality = ProfileJson.Int(functions, "highToneQuality");
        HighAudio = ProfileJson.Int(functions, "highAudio");
        SpatialHiResMutex = ProfileJson.Int(functions, "spatialHiResMutex");
        SpatialTypes = ProfileJson.Ints(functions, "spatialTypes");
        var frequencies = ProfileJson.Array(functions, "customEqFrequency").ToArray();
        CustomEqFrequencies = frequencies.All(x => x.ValueKind == JsonValueKind.Number && x.TryGetInt32(out int n) && n > 0 && n <= ushort.MaxValue)
            ? Array.AsReadOnly(frequencies.Select(x => x.GetInt32()).ToArray()) : Array.AsReadOnly(Array.Empty<int>());
        if (!ProfileJson.Property(functions, "customEqFrequency", out _) && ProfileJson.Int(functions, "customEqualizer") > 0)
            CustomEqFrequencies = Array.AsReadOnly(new[] { 62, 250, 1000, 4000, 8000, 16000 });
        CustomEqualizerCapacity = !ProfileJson.Property(functions, "customEqMax", out _) ? 3
            : ProfileJson.Int(functions, "customEqMax") is >= 0 and <= byte.MaxValue ? ProfileJson.Int(functions, "customEqMax")!.Value : 0;
        GameMode = ProfileJson.Int(functions, "gameMode");
        GameSoundMutexes = ProfileJson.Ints(functions, "gameSoundMutexes");
        MultiDevicesConnect = ProfileJson.Int(functions, "multiDevicesConnect");
        NoiseReductionModes = Array.AsReadOnly(ProfileJson.NoiseModes(functions, "noiseReductionMode").ToArray());
        EqualizerModes = ProfileJson.EqualizerModes(functions, "equalizerMode");
        EqualizerModeCompat = ProfileJson.EqualizerModes(functions, "equalizerModeCompat");
        EqualizerModeByVersion = ProfileJson.EqualizerModes(functions, "equalizerModeByVersion");
        EqualizerModeByColor = ProfileJson.EqualizerModes(functions, "equalizerModeByColor");
        GameSoundModes = Array.AsReadOnly(ProfileJson.Array(functions, "gameSoundList").Where(x => ProfileJson.Int(x, "type") is >= 0 and <= byte.MaxValue).Select(x => new GameSoundMode(x,
            ProfileJson.Int(x, "type")!.Value, ProfileJson.Array(x,"packageNameList").Where(n => n.ValueKind == JsonValueKind.String).Select(n=>n.GetString()!), ProfileJson.Conditions(x))).ToArray());
        MultiConnectFunctions = Array.AsReadOnly(ProfileJson.Array(functions, "multiConnectFunctions").Where(x => ProfileJson.Text(x, "functionType") is { Length: > 0 }).Select(x => new MultiConnectFunction(x,
            ProfileJson.Text(x, "functionType")!, ProfileJson.Conditions(x))).ToArray());
    }
    private readonly JsonElement _rawFunctions;
    public string ProductId { get; }
    public string Name { get; }
    public IReadOnlyList<string> Aliases { get; }
    public Guid Uuid { get; }
    public bool SupportSpp { get; }
    public SourceProvenance Provenance { get; }
    public VersionConditions Conditions { get; }
    public long? MinimumAndroidAppVersionMarker => Conditions.MinimumAndroidAppMarker;
    public JsonElement RawFunctions => _rawFunctions.Clone();
    public int? HighToneQuality { get; }
    public int? HighAudio { get; }
    public int? SpatialHiResMutex { get; }
    public IReadOnlyList<int> SpatialTypes { get; }
    public IReadOnlyList<int> CustomEqFrequencies { get; }
    public int CustomEqualizerCapacity { get; }
    public int? GameMode { get; }
    public IReadOnlyList<int> GameSoundMutexes { get; }
    public int? MultiDevicesConnect { get; }
    public IReadOnlyList<NoiseReductionMode> NoiseReductionModes { get; }
    public IReadOnlyList<EqualizerMode> EqualizerModes { get; }
    public IReadOnlyList<EqualizerMode> EqualizerModeCompat { get; }
    public IReadOnlyList<EqualizerMode> EqualizerModeByVersion { get; }
    public IReadOnlyList<EqualizerMode> EqualizerModeByColor { get; }
    public IReadOnlyList<GameSoundMode> GameSoundModes { get; }
    public IReadOnlyList<MultiConnectFunction> MultiConnectFunctions { get; }
    public EqualizerMode? FindEqualizerByProtocolId(int protocolId) => EqualizerModes.FirstOrDefault(x => x.ProtocolIndex == protocolId);
    /// <summary>Preserve APK append order, stable sorting and App/color selection; firmware requirements remain diagnostics.</summary>
    public IReadOnlyList<EqualizerMode> GetEffectiveEqualizerModes(DeviceVersionContext? versions = null, byte? color = null) => Array.AsReadOnly(
        EqualizerModeCompat.Concat(EqualizerModeByColor.Where(x => color.HasValue && x.ColorId == color))
            .Concat(EqualizerModeByVersion).Concat(EqualizerModes)
            .Where(x => x.EvaluateConditionsForControl(versions) == VersionConditionStatus.Pass).OrderBy(x => x.Order).ToArray());
    public NoiseReductionMode? FindNoiseReductionByProtocolId(int protocolId) => NoiseReductionModes
        .SelectMany(Flatten).FirstOrDefault(x => x.ProtocolIndex == protocolId);
    public static DeviceProfile FromResource(JsonElement source, string sha256 = "")
        => new(source, new("HeyMelody", ValidProductId(source), sha256));
    public int? FunctionVersion(string name) => ProfileJson.Int(_rawFunctions, name);
    private static IEnumerable<NoiseReductionMode> Flatten(NoiseReductionMode mode)
    { yield return mode; foreach (var child in mode.Children.SelectMany(Flatten)) yield return child; }
    private static string ValidProductId(JsonElement source)
    {
        string? id = ProfileJson.Text(source, "id");
        return id is { Length: 6 } && id.All(c => c is >= '0' and <= '9' or >= 'A' and <= 'F') ? id
            : throw new InvalidDataException("Device profile requires an exact six-digit uppercase product identifier.");
    }
}
