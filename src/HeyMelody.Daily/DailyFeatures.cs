using System.Text.Json;
using HeyMelodyWindows.Devices;
using HeyMelodyWindows.Protocol;

using HeyMelody.Diagnostics;

namespace HeyMelodyWindows.Daily;

public sealed partial class DailyClient
{
    // OEM commands.n.m @ 57C574 is the source of these feature IDs. Version 1
    // means binary in T.f; wear detection explicitly also accepts version 2.
    private sealed record BinaryFeature(string Id, string Title, byte ProtocolId, string Function);
    private static readonly BinaryFeature[] BinaryFeatures = [
        new("wear", "入耳检测", 0x04, "wearDetection"),
        new("hearing-enabled", "黄金听感", 0x0B, "hearingEnhancement"),
        new("vocal-enhance", "人声增强", 0x09, "vocalEnhance"),
        new("long-power", "长续航模式", 0x17, "longPowerMode"),
        new("smart-call", "智能通话", 0x15, "smartCall"),
        new("bass-enabled", "低音增强", 0x1D, "bassEngineSupport"),
        new("adaptive-volume", "自适应音量", 0x30, "adaptiveVolume"),
        new("adaptive-ear", "自适应听感", 0x31, "adaptiveEar"),
        new("speech-perception", "人声感知", 0x32, "speechPerception"),
        new("long-press-volume", "长按调节音量", 0x35, "longPressVolume"),
        new("hearing-optimize", "听感优化", 0x38, "hearingOptimize"),
        new("incoming-call", "来电控制", 0x39, "incomingCallControl"),
        new("sleep-detection", "睡眠检测", 0x3A, "sleepDetection")
    ];
    private bool BinaryDeclared(BinaryFeature feature) => feature.Id switch
    {
        "wear" => _profile?.FunctionVersion(feature.Function) is 1 or 2,
        "hearing-enabled" => _profile?.FunctionVersion("hearingEnhancement") == 1 || _profile?.FunctionVersion("hearingEnhancementNew") == 1,
        _ => _profile?.FunctionVersion(feature.Function) == 1
    };
    private byte[] FeatureQuery()
    {
        var ids = new List<byte> { 0x11, 0x06, 0x28, 0x18, 0x1B };
        ids.AddRange(BinaryFeatures.Where(BinaryDeclared).Select(x => x.ProtocolId));
        if (_profile?.GameSoundModes.Count > 0) ids.Add(0x27);
        return [(byte)ids.Distinct().Count(), .. ids.Distinct()];
    }
    private bool GeneralAllowed(DailySnapshot s) => s.ProfileVerified && s.Connected &&
        _profile?.Conditions.EvaluateForControl(new(s.FirmwareVersion, _appVersionMarker)) == VersionConditionStatus.Pass;
    private bool GeneralCommands(ushort read, ushort write) => _commands?.Supports(read) == true && _commands.Supports(write);
    private static int? BinaryValue(DailySnapshot s, byte id) => s.RawFeatures.TryGetValue(id, out byte raw) && raw <= 1 ? raw : null;
    private static int? JsonInt(JsonElement source, string name) => source.ValueKind == JsonValueKind.Object && source.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int number) ? number : null;
    private (int Min, int Max)? PromptBounds()
    {
        int minimum = 1, maximum = 10; // PromptVolumeBarPreference OEM constructors @ 29F59C/29F5DC/29F61C.
        if (_profile?.RawFunctions.TryGetProperty("promptVolumeRange", out var range) == true)
        {
            if (range.ValueKind != JsonValueKind.Object) return null;
            int? min = JsonInt(range, "min"), max = JsonInt(range, "max");
            if (range.TryGetProperty("min", out _) && !min.HasValue || range.TryGetProperty("max", out _) && !max.HasValue) return null;
            if (min.HasValue && min != -1) minimum = min.Value;
            if (max.HasValue && max != -1) maximum = max.Value;
        }
        return minimum >= 0 && maximum <= 127 && minimum <= maximum ? (minimum, maximum) : null;
    }
    private void BuildFeatureSections(DailySnapshot s)
    {
        if (_profile is null || _commands is null || !s.ProfileVerified) { s.FeatureSections = []; return; }
        bool allowed = GeneralAllowed(s);
        var sound = new List<DailyFeatureControl>(); var behavior = new List<DailyFeatureControl>(); var gestures = new List<DailyFeatureControl>();
        void Switch(List<DailyFeatureControl> section, string id, string title, int? value, bool writable) =>
            section.Add(new(id, title, "switch", value, null, [], 0, 1, allowed && writable && value.HasValue));
        if (_profile.FunctionVersion("multiDevicesConnect") > 0) Switch(behavior, "dual", "双设备连接", s.DualConnection is null ? null : s.DualConnection == true ? 1 : 0, s.Capabilities.DualConnection);
        if (_profile.FunctionVersion("gameMode") > 0) Switch(sound, "game", "游戏低延迟", s.GameMode is null ? null : s.GameMode == true ? 1 : 0, s.Capabilities.GameMode);
        if (_profile.FunctionVersion("highToneQuality") > 0) Switch(sound, "hires", "Hi-Res 高音质", s.HiRes is null ? null : s.HiRes == true ? 1 : 0, s.Capabilities.HiRes);
        if (_profile.SpatialTypes.Count > 0)
        {
            var options = _profile.SpatialTypes.Where(x => x is >= 0 and <= 2 && (s.Capabilities.SpatialRoute != "legacy" || x <= 1))
                .Distinct().Select(x => new DailyFeatureOption(x, x switch { 0 => "关闭", 1 => "固定空间音效", 2 => "头部跟踪", _ => $"模式 {x}" })).ToArray();
            sound.Add(new("spatial", "空间音效", "radio", s.SpatialMode, null, options, null, null, allowed && s.Capabilities.SpatialMode && options.Any(x => x.Value == s.SpatialMode)));
        }
        foreach (var feature in BinaryFeatures.Where(x => x.Id != "hearing-enabled" && BinaryDeclared(x)))
            Switch(feature.Id is "wear" or "long-power" or "smart-call" or "long-press-volume" or "incoming-call" or "sleep-detection" ? behavior : sound,
                feature.Id, feature.Title, BinaryValue(s, feature.ProtocolId), GeneralCommands(0x010D, 0x0403));
        if (_profile.FunctionVersion("bassEngineSupport") == 1)
            sound.Add(new("bass", "低音强度", "slider", s.BassEngine?.Current, null, [], s.BassEngine?.Minimum, s.BassEngine?.Maximum,
                allowed && GeneralCommands(0x0124, 0x041B) && s.BassEngine is not null));
        if (_profile.FunctionVersion("promptVolume") == 1)
        {
            var bounds = PromptBounds();
            behavior.Add(new("prompt-volume", "提示音音量", "slider", s.PromptVolume, null, [], bounds?.Min, bounds?.Max,
                allowed && GeneralCommands(0x0130, 0x0427) && bounds.HasValue && s.PromptVolume >= bounds.Value.Min && s.PromptVolume <= bounds.Value.Max));
        }
        if (_profile.GameSoundModes.Count > 0)
        {
            var versions = new DeviceVersionContext(s.FirmwareVersion, _appVersionMarker);
            var options = _profile.GameSoundModes.Where(x => x.Type is >= 0 and <= 255 && x.Conditions.EvaluateForControl(versions) == VersionConditionStatus.Pass
                && s.GameSound?.SupportedTypes.Contains((byte)x.Type) == true).Select(x => new DailyFeatureOption(x.Type,
                    x.Type switch { 0 => "关闭", 1 => "和平精英职业联赛游戏音效", 3 => "射击类游戏音效", _ => $"游戏音效 {x.Type}" })).DistinctBy(x => x.Value).ToArray();
            // GameSoundMutexes describes prompts on other settings in the official UI,
            // not a prerequisite for sending a game sound command. The boolean and type
            // have separate commands and can be offered independently.
            sound.Add(new("game-sound-enabled", "游戏音效", "switch", BinaryValue(s, 0x27), null, [], 0, 1,
                allowed && BinaryValue(s, 0x27).HasValue && GeneralCommands(0x010D, 0x0403)));
            sound.Add(new("game-sound", "游戏音效类型", "combo", s.GameSound?.SelectedType, null, options, null, null,
                allowed && s.GameSound is not null && options.Length > 0 && GeneralCommands(0x012B, 0x0423)));
        }
        foreach (var assignment in s.Gestures)
        {
            var options = GestureOptions(assignment, s);
            if (options.Count == 0) continue;
            string ear = assignment.Device switch { 1 => "左耳", 2 => "右耳", _ => $"组件 {assignment.Device}" };
            string action = assignment.Action switch { 1 => "单击", 2 => "双击", 3 => "三击", 4 => "长按", 5 => "滑动", 6 => "长按并保持", _ => $"操作 {assignment.Action}" };
            gestures.Add(new(GestureId(assignment), $"{ear} · {action}", "combo", assignment.Function, null, options, null, null,
                allowed && GeneralCommands(0x0108, 0x0401)));
        }
        var sections = new List<DailyFeatureSection>();
        if (sound.Count > 0) sections.Add(new("audio-features", "声音", false, sound.AsReadOnly()));
        if (behavior.Count > 0) sections.Add(new("device-features", "耳机设置", false, behavior.AsReadOnly()));
        if (gestures.Count > 0) sections.Add(new("gesture-features", "操控", false, gestures.AsReadOnly()));
        s.FeatureSections = sections.AsReadOnly();
    }
    private static string GestureId(GestureAssignment x) => $"gesture:{x.Device}:{x.Button}:{x.Action}";
    // OEM repository L.d @ 2212B0 translates source gesture styles to firmware actions.
    private static int ProtocolAction(int action) => action switch
    {
        11 or 19 or 20 or 27 => 4, 22 or 31 or 35 => 6,
        16 or 28 or 32 => 1, 17 or 26 or 29 or 33 or 36 => 2, 18 or 30 or 34 => 3, _ => action
    };
    // OEM F9/a.a @ 138894: source support bits are NOT firmware function indices.
    // Phone-dependent assistant/translation choices are omitted until a Windows route exists.
    private static readonly (int Function, uint Mask)[] NativeGestureFunctions = [
        (0,0x200), (1,4), (5,0x20), (6,0x40), (7,0x400), (8,0x80), (10,0x800), (11,8), (12,16),
        (13,0x1000), (17,0x2000), (22,0x8000), (26,0x100000), (27,0x200000), (29,0x80000)
    ];
    private IReadOnlyList<DailyFeatureOption> GestureOptions(GestureAssignment assignment, DailySnapshot s)
    {
        if (_profile is null || assignment.Device is not (1 or 2)) return [];
        var raw = _profile.RawFunctions; var versions = new DeviceVersionContext(s.FirmwareVersion, _appVersionMarker);
        var controls = new List<JsonElement>();
        if (raw.TryGetProperty("control", out var baseControls) && baseControls.ValueKind == JsonValueKind.Array) controls.AddRange(baseControls.EnumerateArray());
        uint mask = 0;
        foreach (var control in controls)
        {
            int? action = JsonInt(control, "action");
            if (!action.HasValue || ProtocolAction(action.Value) != assignment.Action || (JsonInt(control, "button") ?? 0) != assignment.Button
                || VersionConditions.FromSource(control).EvaluateForControl(versions) != VersionConditionStatus.Pass) continue;
            int? support = JsonInt(control, "support"); if (support.HasValue) mask |= unchecked((uint)support.Value);
        }
        bool onePlusLegacy = _profile.Name is "OnePlus Buds" or "OnePlus Buds Z"; // B1/b.J @ 19E6EC.
        return NativeGestureFunctions.Where(x => (mask & (x.Function == 5 && onePlusLegacy ? 0x40 : x.Mask)) != 0)
            .Select(x => new DailyFeatureOption(x.Function, x.Function switch {
                0 => "无操作", 1 => "播放 / 暂停", 5 => "上一首", 6 => "下一首", 7 => "降噪开 / 关",
                8 => "音量调节", 10 => "歌曲切换", 11 => "增大音量", 12 => "减小音量", 13 => "设备切换",
                17 => "游戏模式", 22 => "耳机专注", 26 => "拒接来电", 27 => "拒接来电 / 挂断电话", 29 => "接听 / 挂断电话",
                _ => $"功能 {x.Function}" })).ToArray();
    }

    public async Task<DailyApplyResult> ApplyFeatureAsync(string id, int value, string? text = null, CancellationToken token = default)
    {
        // Feature 0x1C is disabled for every model, including callers with an old control ID.
        if (id == "auto-volume") return Reject(new(), "此功能已停用，没有发送设置。", DailyApplyFailure.StateUnavailable);
        if (id is "dual" or "game" or "hires")
        {
            if (value is not (0 or 1)) return Reject(new(), "开关值无效。");
            return await ApplyCoreAsync(id, (byte)value, 0, null, false, token);
        }
        if (id == "spatial")
        {
            if (value is < 0 or > 2) return Reject(new(), "空间模式无效。");
            return await ApplyCoreAsync(id, (byte)value, 0, null, false, token);
        }
        var result = new DailyApplyResult(); await _gate.WaitAsync(token);
        try
        {
            if (_disposed) return Reject(result, "客户端已关闭。");
            var before = await ReadFreshAsync(token); result.Before = before;
            var control = before.FeatureSections.SelectMany(x => x.Controls).SingleOrDefault(x => x.Id == id);
            if (control is null || !control.CanWrite || !control.Value.HasValue || !GeneralAllowed(before))
                return Reject(result, control?.Text ?? "该设置的型号配置、命令能力或当前回读未通过校验，没有发送设置。", DailyApplyFailure.StateUnavailable);
            if (text is not null || control.Minimum.HasValue && value < control.Minimum || control.Maximum.HasValue && value > control.Maximum
                || control.Options.Count > 0 && !control.Options.Any(x => x.Value == value))
                return Reject(result, "设置值不在当前设备允许的范围内。");
            if (value == control.Value)
            {
                result.Success = true; result.ReadbackMatches = true; result.After = before;
                result.Message = "当前回读已经符合请求，没有重复发送设置。"; return result;
            }
            ushort command; byte[] payload; Func<DailySnapshot, bool> target;
            var binary = BinaryFeatures.SingleOrDefault(x => x.Id == id && BinaryDeclared(x));
            if (binary is not null)
            {
                if (value is not (0 or 1)) return Reject(result, "开关值无效。");
                command = 0x0403; payload = [binary.ProtocolId, (byte)value]; target = s => BinaryValue(s, binary.ProtocolId) == value;
            }
            else if (id == "bass")
            {
                command = 0x041B; payload = DailyFeatureCodecs.Bass(before.BassEngine!, value); target = s => s.BassEngine?.Current == value;
            }
            else if (id == "prompt-volume")
            {
                command = 0x0427; payload = DailyFeatureCodecs.PromptVolume(value); target = s => s.PromptVolume == value;
            }
            else if (id == "game-sound-enabled")
            {
                command = 0x0403; payload = [0x27, (byte)value];
                target = s => BinaryValue(s, 0x27) == value;
            }
            else if (id == "game-sound")
            {
                byte type = checked((byte)value);
                command = 0x0423; payload = DailyFeatureCodecs.GameSoundType(type);
                target = s => s.GameSound?.SelectedType == type;
            }
            else if (id.StartsWith("gesture:", StringComparison.Ordinal))
            {
                var assignment = before.Gestures.Single(x => GestureId(x) == id);
                command = 0x0401; payload = DailyFeatureCodecs.Gesture(assignment, checked((byte)value));
                target = s => s.Gestures.Any(x => GestureId(x) == id && x.Function == value);
            }
            else return Reject(result, "未知设置请求。");
            await SendAndObserveAsync(command, payload, target, result, token); return Finish(result);
        }
        catch (Exception error)
        {
            DiagnosticLog.Error("Daily", "feature.apply-failed", "The feature setting operation failed without replay.", error,
                new { id, value, text, result.SubmissionAttempted });
            result.FailureKind = DailyApplyFailure.TransportUnknown;
            result.Error = "设置过程未完整确认；已提交的请求不会自动重发。"; return Finish(result);
        }
        finally { _gate.Release(); }
    }
}
