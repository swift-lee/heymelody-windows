using HeyMelodyWindows.Devices;
using HeyMelodyWindows.Protocol;
using HeyMelodyWindows.Control;

using HeyMelody.Diagnostics;

namespace HeyMelodyWindows.Daily;

public sealed partial class DailyClient
{
    private DailySession? _session;
    private CommandCapabilities? _commands;
    private DeviceProfile? _profile;
    private DailySnapshot? _latest;
    private readonly Dictionary<DailyRefreshScope, List<string>> _readWarnings = [];
    private string? _colorProduct;
    private byte? _color;
    private DateTimeOffset? _colorObservedAtUtc;
    private int _freshReadDepth;
    private sealed record CodecRecoveryIdentity(string ProductId, SourceProvenance Provenance);
    private sealed record CodecRecovery(CodecRecoveryIdentity Identity, string Warning);

    private async Task DropSessionAsync()
    {
        DailySession? previous;
        lock (_stateGate)
        {
            previous = _session; _session = null; _commands = null; _profile = null;
            _registeredSession = null; _monitoringWarning = null; _changes.Clear(); _invalidated.Clear();
            _registeredEvents.Clear(); _advertisedEvents.Clear();
        }
        if (previous is not null) await previous.DisposeAsync();
    }
    private async Task EnsureSessionAsync(CancellationToken token)
    {
        if (_session is { Usable: true, Remaining: >= 32 }) return;
        if (_calibrationActive)
        {
            if (_session is { Usable: true, Remaining: > 8 }) return;
            throw new IOException("测试期间不会自动重建控制连接，请结束测试后重新连接。");
        }
        await DropSessionAsync();
        var session = new DailySession(await _connector.OpenAsync(token));
        lock (_stateGate) _session = session;
        session.Notification += packet => OnNotification(session, packet);
        session.Ended += failure => OnSessionEnded(session, failure);
        session.StartReader();
    }
    private async Task<DailySnapshot> RefreshCoreAsync(DailyRefreshScope scope, CancellationToken token)
    {
        if (!Enum.IsDefined(scope)) throw new ArgumentOutOfRangeException(nameof(scope));
        await _gate.WaitAsync(token);
        try
        {
            if (_disposed) return new() { Error = "客户端已关闭。" };
            return await ReadFreshAsync(token, scope);
        }
        finally { _gate.Release(); }
    }
    private async Task<DailySnapshot> ReadFreshAsync(CancellationToken token, DailyRefreshScope scope = DailyRefreshScope.Full)
    {
        Interlocked.Increment(ref _freshReadDepth);
        try { return await ReadFreshCoreAsync(token, scope); }
        finally { Interlocked.Decrement(ref _freshReadDepth); }
    }
    private async Task<DailySnapshot> ReadFreshCoreAsync(CancellationToken token, DailyRefreshScope scope)
    {
        string failure = "设备状态读取失败";
        CodecRecovery? codecRecovery = null;
        for (int attempt = 0; attempt < 2; attempt++)
        {
            string phase = "建立 SPP 控制连接";
            CodecRecoveryIdentity? verifiedIdentity = null;
            try
            {
                token.ThrowIfCancellationRequested();
                _session?.SetCodecReplyCompatibilityAllowed(false);
                await EnsureSessionAsync(token);
                phase = "校验设备身份与能力";
                long startingVersion = NotificationVersion;
                var readVersions = new Dictionary<string, long>();
                // New transports require identity/capability verification. Returned results do
                // not share mutable state with either the cache or previously returned results.
                bool initialize = _commands is null || _latest is not { Connected: true };
                DailySnapshot s;
                lock (_stateGate) s = codecRecovery is not null || scope == DailyRefreshScope.Full || _latest is not { Connected: true }
                    ? new DailySnapshot() : Clone(_latest);
                s.Connected = true; if (s.ProfileVerified) s.Error = null; s.Warnings = [];
                if (codecRecovery is not null || scope == DailyRefreshScope.Full || _latest is not { Connected: true }) _readWarnings.Clear();
                if (codecRecovery is not null || initialize || scope is DailyRefreshScope.Full or DailyRefreshScope.Identity)
                    s = await ReadIdentityAsync(s, token);
                if (s.ProfileVerified && _profile is not null)
                {
                    verifiedIdentity = new(s.ProductId!, _profile.Provenance);
                    _session!.SetCodecReplyCompatibilityAllowed(_commands!.Supports(0x0114));
                }
                if (codecRecovery is not null)
                {
                    if (verifiedIdentity != codecRecovery.Identity)
                        throw new IOException("编码查询恢复时设备身份或配置来源已变化。");
                    _session!.SuppressCodecQuery(codecRecovery.Warning);
                }
                if (s.ProfileVerified && scope != DailyRefreshScope.Identity)
                {
                    if (codecRecovery is not null || scope == DailyRefreshScope.Full)
                    {
                        foreach (var section in new[] { DailyRefreshScope.Information, DailyRefreshScope.Features,
                            DailyRefreshScope.Battery, DailyRefreshScope.Anc, DailyRefreshScope.Equalizer })
                        {
                            phase = "读取设备状态";
                            await ReadSectionAsync(s, section, token, readVersions);
                        }
                    }
                    else { phase = "读取设备状态"; await ReadSectionAsync(s, scope, token, readVersions); }
                }
                phase = "注册状态通知";
                await RegisterMonitoringAsync(token);
                s.Warnings = _readWarnings.Values.SelectMany(x => x).Distinct().ToList();
                if (_monitoringWarning is not null) s.Warnings.Add(_monitoringWarning);
                lock (_stateGate)
                {
                    if (_session is not { Usable: true }) throw new IOException("Session ended during refresh.");
                    foreach (var change in _changes)
                    {
                        string group = change.Key.StartsWith("battery:") ? "battery:" :
                            change.Key.StartsWith("feature:") ? "feature:" :
                            change.Key.StartsWith("placement:") ? "placement:" : change.Key;
                        long readVersion = readVersions.GetValueOrDefault(group, startingVersion);
                        if (change.Value.Version > readVersion) change.Value.Apply(s);
                    }
                    ResolveCapabilities(s);
                    s.ObservedAtUtc = DateTimeOffset.UtcNow; s.Revision = ++_revision;
                    _latest = Clone(s);
                }
                Publish(s);
                return s;
            }
            catch (Exception) when (_calibrationActive) { throw; }
            catch (Exception ex) when (!token.IsCancellationRequested)
            {
                DiagnosticLog.Error("Daily", "refresh.failed", "A daily refresh attempt failed.", ex, new { phase, attempt, scope, verifiedIdentity });
                failure = DescribeReadFailure(ex, phase);
                if (attempt == 0 && verifiedIdentity is not null && ex is DailyReadException read && read.CanRecoverCodec)
                {
                    DiagnosticLog.Warning("Daily", "codec.recovery-planned", "The codec query qualifies for one fresh verified read without repeating the codec query.", ex,
                        new { phase, attempt, scope, verifiedIdentity, read.Reason });
                    codecRecovery = new(verifiedIdentity,
                        $"音频编码查询 0x0114 暂不可用（{read.Reason}），已跳过该查询；其他功能按各自能力和状态使用。重新连接可重试。");
                }
                await DropSessionAsync();
                if (attempt == 0) continue;
            }
            catch (Exception error) { DiagnosticLog.Warning("Daily", "refresh.cancelled", "The daily refresh was cancelled.", error, new { phase, attempt, scope }); failure = "设备状态读取已取消"; await DropSessionAsync(); break; }
        }
        return PublishReadFailure(failure);
    }
    private static string DescribeReadFailure(Exception failure, string phase) => failure switch
    {
        DailyReadException read => read.Message,
        ControlTransportUnavailableException { Reason: ControlTransportFailure.ServiceNotFound }
            => "未发现所选配置的 SPP 控制服务",
        ControlTransportUnavailableException { Reason: ControlTransportFailure.AmbiguousServices }
            => "发现多个 SPP 控制服务，无法唯一确定控制通道",
        OperationCanceledException => $"{phase}超时",
        _ => $"{phase}失败（{DailySession.DescribeFailure(failure)}）"
    };
    private DailySnapshot PublishReadFailure(string failure)
    {
        _readWarnings.Clear();
        DailySnapshot failed;
        lock (_stateGate)
        {
            _latest = new() { ObservedAtUtc = DateTimeOffset.UtcNow, Revision = ++_revision,
                Error = $"{failure}。请刷新或重新连接。", Connected = false };
            failed = Clone(_latest);
        }
        Publish(failed); return failed;
    }
    private async Task<byte[]?> GetAsync(ushort command, byte[] payload, DailySnapshot s, CancellationToken token)
    {
        if (_calibrationActive && _session!.Remaining <= 8)
            throw new IOException("本次连接的控制次数已用完，请结束测试并重新连接后重试。");
        var reply = await _session!.RequestAsync(command, payload, token);
        if (!reply.Matched || !_session.Usable)
            throw new DailyReadException(command, reply.Failure ?? "未收到匹配回执", reply);
        if (reply.Status != 0)
        {
            s.Warnings.Add($"查询 0x{command:X4} 被设备拒绝。");
            return null;
        }
        if (reply.CodecReplyCompatibilityUsed)
            s.Warnings.Add("音频编码回复使用兼容格式，当前固件的实际效果尚需验证。");
        return reply.Payload;
    }
    private async Task<DailySnapshot> ReadIdentityAsync(DailySnapshot s, CancellationToken token)
    {
        byte[] identity = await GetAsync(0x0103, [], s, token) ?? throw new DailyReadException(0x0103, "设备拒绝查询");
        var product = QueryParsers.Parse(0x0103, identity);
        if (!product.Success) throw new DailyReadException(0x0103, "身份数据格式无效");
        string productId = (string)product.Data["productId"]!;
        if (s.ProductId is not null && s.ProductId != productId)
        {
            s = new() { Connected = true };
            _readWarnings.Clear();
            lock (_stateGate) _changes.Clear();
        }
        s.ProductId = productId;
        _profile = _profileResolver(productId);
        byte[] cap = await GetAsync(0x0100, [], s, token) ?? throw new DailyReadException(0x0100, "设备拒绝查询");
        if (cap.Length < 2 || cap.Length > 512) throw new DailyReadException(0x0100, "能力数据格式无效");
        _commands = CommandCapabilities.Parse(cap.AsSpan(1));
        await ReadColorAsync(s, productId, token);
        if (_profile is null || !_profile.SupportSpp || _profile.Uuid == Guid.Empty ||
            _profile.Conditions.EvaluateForControl(new(AndroidAppVersionMarker: _appVersionMarker)) == VersionConditionStatus.Fail)
        {
            s.ProfileVerified = false;
            s.Error = "当前型号尚无已核实的配置，日常设置不可用。";
            return s;
        }
        s.ProfileVerified = true; s.DeviceName = _profile.Name;
        s.CustomEqualizerCapacity = _profile.CustomEqualizerCapacity;
        s.EqualizerFrequencies = _profile.CustomEqFrequencies.Select(x => checked((ushort)x)).ToArray();
        ResolvePresetOptions(s);
        s.AncOptions = _profile.NoiseReductionModes.SelectMany(p => (p.Children.Count == 0 ? new[] {p} : p.Children).Select(c =>
            new AncOption((byte)p.ProtocolIndex, (byte)c.ProtocolIndex, c.ModeType, AncOptionName(p, c)))).ToList();
        _readWarnings[DailyRefreshScope.Identity] = s.Warnings.ToList();
        return s;
    }
    private static string AncOptionName(NoiseReductionMode parent, NoiseReductionMode option) => parent.Children.Count == 0
        ? option.ModeType switch
        {
            1 => "关闭降噪", 2 => "通透", 3 => "弱降噪", 4 => "强降噪", 5 => "降噪", 6 => "聆听",
            7 => "智能降噪", 10 => "自适应", _ => $"降噪档位 {option.ProtocolIndex}"
        }
        : option.ModeType switch
        {
            1 => "关闭降噪", 2 => "通透", 6 when parent.ModeType == 2 => "强化人声", 7 => "智能切换",
            4 => "深度降噪", 8 => "中度降噪", 3 => "轻度降噪", 10 => "自适应",
            11 => "更少噪音", 12 => "标准", 13 => "更多噪音", _ => $"降噪档位 {option.ProtocolIndex}"
        };
    private sealed class DailyReadException(ushort command, string reason, DailyResponse? response = null)
        : IOException($"查询 0x{command:X4} 失败（{reason}）")
    {
        public string Reason => reason;
        public bool CanRecoverCodec => command == 0x0114 && response is { FailureRequestPending: true }
            && (response.FailureKind == DailyReadFailureKind.Timeout
                || response.FailureKind == DailyReadFailureKind.ProtocolFormat && response.FailureCommand == 0x8114
                    && response.Sequence.HasValue && response.FailureSequence == response.Sequence);
    }
    /// <summary>
    /// Both official apps read GET 0x010B (a foundation command) and use payload[1] as the artwork color.
    /// The color is fixed hardware metadata, so this client reads it once per product. Rejection or
    /// silence leaves it unknown without a user warning or a dropped session.
    /// </summary>
    private async Task ReadColorAsync(DailySnapshot s, string productId, CancellationToken token)
    {
        if (_colorProduct != productId && _commands!.Supports(0x010B))
        {
            var reply = await _session!.RequestAsync(0x010B, [], token, TimeSpan.FromSeconds(2));
            if (!_session.Usable) throw new IOException();
            _colorProduct = productId; _color = null; _colorObservedAtUtc = null;
            if (reply is { Matched: true, Status: 0, Payload: { } payload } && QueryParsers.Parse(0x010B, payload) is { Success: true } parsed)
            { _color = (byte)parsed.Data["color"]!; _colorObservedAtUtc = DateTimeOffset.UtcNow; }
        }
        if (_colorProduct == productId) { s.Color = _color; s.ColorObservedAtUtc = _colorObservedAtUtc; }
    }
    private async Task ReadSectionAsync(DailySnapshot s, DailyRefreshScope scope, CancellationToken token,
        Dictionary<string, long> readVersions)
    {
        s.Warnings = [];
        async Task Parse(ushort command, byte[] request, Action<byte[]> parse)
        {
            if (!_commands!.Supports(command)) return;
            readVersions[ReadGroup(command)] = NotificationVersion;
            byte[]? payload = await GetAsync(command, request, s, token);
            if (payload is null) return;
            try { parse(payload); }
            catch (Exception ex) when (ex is FormatException or ArgumentException or OverflowException)
            {
                DiagnosticLog.Bytes("Daily", "query.invalid-fields.payload", payload, new { command = $"0x{command:X4}", scope });
                DiagnosticLog.Error("Daily", "query.invalid-fields", "The decoded response fields did not pass their query parser.", ex,
                    new { command = $"0x{command:X4}", scope, payloadLength = payload.Length });
                s.Warnings.Add($"查询 0x{command:X4} 的布局未通过校验，状态保留未知。");
            }
        }
        switch (scope)
        {
            case DailyRefreshScope.Information:
                s.FirmwareVersion = null; s.FirmwareDisplayVersion = null; s.CodecRaw = null;
                await Parse(0x0105, [], data => {
                    var reply = QueryParsers.Parse(0x0105, data);
                    if (!reply.Success) throw new FormatException();
                    var entries = (List<FirmwareEntry>)reply.Data["firmwareEntries"]!;
                    s.FirmwareVersion = entries
                        .FirstOrDefault(x => x.DeviceType == 1 && x.VersionType == 2)?.Version;
                    string? productType = _profile?.RawSource.TryGetProperty("type", out var type) == true
                        && type.ValueKind == System.Text.Json.JsonValueKind.String ? type.GetString() : null;
                    s.FirmwareDisplayVersion = FormatFirmwareDisplay(entries, productType);
                });
                if (_session!.CodecQuerySuppressionWarning is { } codecWarning) s.Warnings.Add(codecWarning);
                else await Parse(0x0114, [], data => {
                        var reply = QueryParsers.Parse(0x0114, data);
                        if (!reply.Success) throw new FormatException();
                        s.CodecRaw = (byte)reply.Data["codecRaw"]!;
                    });
                await Parse(0x0109, [], data => {
                    if (data.Length < 1 || data[0] != 0) throw new FormatException();
                    DateTimeOffset observed = DateTimeOffset.UtcNow;
                    foreach (var pair in NotificationParsers.ParseComponentPairs(data.AsSpan(1)))
                        ApplyPlacement(s, pair.Key, pair.Value, observed);
                });
                break;
            case DailyRefreshScope.Features:
                s.RawFeatures = []; s.SpatialMode = null;
                s.BassEngine = null; s.PromptVolume = null; s.GameSound = null; s.Gestures = [];
                // State reads never generate setters; subscriptions are a separate explicit opt-in.
                await Parse(0x010D, FeatureQuery(), data => s.RawFeatures = DailyParsers.ParseFeatures(data));
                bool legacy = s.RawFeatures.TryGetValue(0x1B, out byte legacySpatial) && legacySpatial <= 1;
                if (_commands!.Supports(0x012A) && (_commands.Supports(0x0422) || !legacy))
                    await Parse(0x012A, [], data => s.SpatialMode = DailyParsers.ParseSpatial(data));
                else if (legacy) s.SpatialMode = legacySpatial;
                if (_profile!.FunctionVersion("bassEngineSupport") == 1)
                    await Parse(0x0124, [], data => s.BassEngine = DailyFeatureCodecs.ParseBass(data));
                if (_profile.FunctionVersion("promptVolume") == 1)
                    await Parse(0x0130, [], data => s.PromptVolume = DailyFeatureCodecs.ParsePromptVolume(data));
                if (_profile.GameSoundModes.Count > 0)
                    await Parse(0x012B, [], data => s.GameSound = DailyFeatureCodecs.ParseGameSound(data));
                if (_profile.RawFunctions.TryGetProperty("control", out var controls) && controls.ValueKind == System.Text.Json.JsonValueKind.Array && controls.GetArrayLength() > 0)
                    await Parse(0x0108, DailyFeatureCodecs.GestureQuery(), data => s.Gestures = DailyFeatureCodecs.ParseGestures(data));
                break;
            case DailyRefreshScope.Battery:
                s.Battery = null;
                s.LeftBatteryObservedAtUtc = s.RightBatteryObservedAtUtc = s.CaseBatteryObservedAtUtc = null;
                await Parse(0x0106, [], data => {
                    s.Battery = DailyParsers.ParseBattery(data);
                    DateTimeOffset observed = DateTimeOffset.UtcNow;
                    if (s.Battery.Left is not null) s.LeftBatteryObservedAtUtc = observed;
                    if (s.Battery.Right is not null) s.RightBatteryObservedAtUtc = observed;
                    if (s.Battery.Case is not null) s.CaseBatteryObservedAtUtc = observed;
                });
                break;
            case DailyRefreshScope.Anc:
                s.Anc = null;
                await Parse(0x010C, [1, 1], data => s.Anc = DailyParsers.ParseAnc(data, _profile!));
                break;
            case DailyRefreshScope.Equalizer:
                s.PresetId = null; s.CustomEqualizers = []; s.CustomEqualizersRead = false;
                await Parse(0x010F, [], data => s.PresetId = DailyParsers.ParsePreset(data));
                await Parse(0x0122, [], data => { s.CustomEqualizers = DailyParsers.ParseEqualizers(data); s.CustomEqualizersRead = true; });
                break;
        }
        _readWarnings[scope] = s.Warnings.ToList();
    }
    private void ResolveCapabilities(DailySnapshot s)
    {
        if (_profile is null || _commands is null || !s.ProfileVerified) { s.Capabilities = new(); s.FeatureSections = []; return; }
        bool? Feature(byte id) => s.RawFeatures.TryGetValue(id, out byte value) ? value switch { 0 => false, 1 => true, _ => null } : null;
        bool legacy = s.RawFeatures.TryGetValue(0x1B, out byte value) && value <= 1;
        bool modern = _commands.Supports(0x012A) && (_commands.Supports(0x0422) || !legacy);
        if (!modern) s.SpatialMode = legacy ? value : null;
        var versions = new DeviceVersionContext(s.FirmwareVersion, _appVersionMarker);
        var resolved = CapabilityResolver.Resolve(_profile, _commands, s.RawFeatures, versions);
        bool Declared(string name) => _profile.FunctionVersion(name) > 0;
        bool allowed = _profile.Conditions.EvaluateForControl(versions) == VersionConditionStatus.Pass;
        ResolvePresetOptions(s);
        s.AncOptions = s.AncOptions.Where(x => _profile.NoiseReductionModes.Any(parent => parent.ProtocolIndex == x.Primary
            && parent.Conditions.EvaluateForControl(versions) == VersionConditionStatus.Pass
            && (parent.Children.Count == 0 ? parent.ProtocolIndex == x.Level : parent.Children.Any(child => child.ProtocolIndex == x.Level
                && child.Conditions.EvaluateForControl(versions) == VersionConditionStatus.Pass)))).ToList();
        s.DualConnection = Feature(0x11); s.HiRes = Feature(0x18);
        s.GameMode = resolved.GameMode.FeatureId.HasValue ? Feature(resolved.GameMode.FeatureId.Value) : null;
        bool Commands(ushort read, ushort write) => _commands.Supports(read) && _commands.Supports(write);
        s.Capabilities = new()
        {
            Battery = s.Battery is not null,
            Anc = allowed && _profile.NoiseReductionModes.Count > 0 && Commands(0x010C, 0x0404) && s.Anc?.Primary is not null,
            Preset = allowed && Declared("equalizer") && Commands(0x010F, 0x0406) && s.PresetId is not null,
            CustomEqualizer = allowed && Declared("customEqualizer") && Commands(0x0122, 0x0418) && s.CustomEqualizersRead,
            DualConnection = allowed && Declared("multiDevicesConnect") && Commands(0x010D, 0x0403) && s.DualConnection is not null,
            GameMode = allowed && Declared("gameMode") && Commands(0x010D, 0x0403) && s.GameMode is not null,
            HiRes = allowed && Declared("highToneQuality") && Commands(0x010D, 0x0403) && s.HiRes is not null,
            SpatialMode = allowed && _profile.SpatialTypes.Count > 0 && s.SpatialMode is not null && (modern ? Commands(0x012A, 0x0422) : Commands(0x010D, 0x0403)),
            SpatialHiResMutex = _profile.SpatialHiResMutex == 1,
            GameFeatureId = resolved.GameMode.FeatureId,
            SpatialRoute = modern ? "modern" : legacy ? "legacy" : "unavailable"
        };
        BuildFeatureSections(s);
        RefreshFirmwareWarnings(s);
    }
    private void RefreshFirmwareWarnings(DailySnapshot s) => s.Warnings = s.Warnings
        .Where(warning => !warning.StartsWith("固件兼容提示：", StringComparison.Ordinal))
        .Concat(CollectFirmwareCompatibilityWarnings(s)).Distinct().ToList();
    private static DailySnapshot Clone(DailySnapshot s) => new()
    {
        Revision = s.Revision, ObservedAtUtc = s.ObservedAtUtc, Connected = s.Connected, ProfileVerified = s.ProfileVerified,
        CaseOpen = s.CaseOpen, LeftInCase = s.LeftInCase, RightInCase = s.RightInCase,
        LeftInEar = s.LeftInEar, RightInEar = s.RightInEar, RawStatusComponents = new(s.RawStatusComponents),
        CaseObservedAtUtc = s.CaseObservedAtUtc, LeftPlacementObservedAtUtc = s.LeftPlacementObservedAtUtc,
        RightPlacementObservedAtUtc = s.RightPlacementObservedAtUtc,
        LeftBatteryObservedAtUtc = s.LeftBatteryObservedAtUtc, RightBatteryObservedAtUtc = s.RightBatteryObservedAtUtc,
        CaseBatteryObservedAtUtc = s.CaseBatteryObservedAtUtc,
        ProductId = s.ProductId, Color = s.Color, ColorObservedAtUtc = s.ColorObservedAtUtc,
        DeviceName = s.DeviceName, FirmwareVersion = s.FirmwareVersion, FirmwareDisplayVersion = s.FirmwareDisplayVersion, CodecRaw = s.CodecRaw,
        CustomEqualizerCapacity = s.CustomEqualizerCapacity,
        Battery = s.Battery is null ? null : new() { Left = s.Battery.Left, Right = s.Battery.Right,
            Case = s.Battery.Case, RawComponents = new(s.Battery.RawComponents) },
        Anc = s.Anc is null ? null : new() { Kind = s.Anc.Kind, ModeType = s.Anc.ModeType,
            RawMask = s.Anc.RawMask.ToArray(), Primary = s.Anc.Primary, Level = s.Anc.Level },
        PresetId = s.PresetId, Presets = s.Presets.ToList(), AncOptions = s.AncOptions.ToList(),
        CustomEqualizers = s.CustomEqualizers.Select(x => new CustomEqualizer { Id = x.Id, Name = x.Name,
            Selected = x.Selected, MinimumGain = x.MinimumGain, MaximumGain = x.MaximumGain, Bands = x.Bands.ToList() }).ToList(),
        CustomEqualizersRead = s.CustomEqualizersRead, EqualizerFrequencies = s.EqualizerFrequencies.ToArray(),
        DualConnection = s.DualConnection, GameMode = s.GameMode, SpatialMode = s.SpatialMode, HiRes = s.HiRes,
        RawFeatures = new(s.RawFeatures), Warnings = s.Warnings.ToList(), Error = s.Error,
        BassEngine = s.BassEngine, PromptVolume = s.PromptVolume,
        GameSound = s.GameSound is null ? null : new(s.GameSound.SelectedType, Array.AsReadOnly(s.GameSound.SupportedTypes.ToArray())),
        Gestures = Array.AsReadOnly(s.Gestures.ToArray()),
        FeatureSections = Array.AsReadOnly(s.FeatureSections.Select(x => x with { Controls = Array.AsReadOnly(x.Controls.Select(c => c with {
            Options = Array.AsReadOnly(c.Options.ToArray()) }).ToArray()) }).ToArray()),
        Capabilities = new() { Battery = s.Capabilities.Battery, Anc = s.Capabilities.Anc, Preset = s.Capabilities.Preset,
            CustomEqualizer = s.Capabilities.CustomEqualizer, DualConnection = s.Capabilities.DualConnection,
            GameMode = s.Capabilities.GameMode, SpatialMode = s.Capabilities.SpatialMode, HiRes = s.Capabilities.HiRes,
            SpatialHiResMutex = s.Capabilities.SpatialHiResMutex, GameFeatureId = s.Capabilities.GameFeatureId,
            SpatialRoute = s.Capabilities.SpatialRoute }
    };
}
