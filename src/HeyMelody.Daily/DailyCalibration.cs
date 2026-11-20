using HeyMelodyWindows.Calibration;
using HeyMelodyWindows.Protocol;
using HeyMelodyWindows.Devices;

namespace HeyMelodyWindows.Daily;

public sealed partial class DailyClient : ICalibrationTransport
{
    private readonly HashSet<byte> _registeredEvents = [];
    private readonly HashSet<byte> _advertisedEvents = [];
    private volatile bool _calibrationActive;
    /// <summary>Keep the shared reader until this workflow ends; reserve sequences for STOP.</summary>
    public void SetCalibrationActive(bool active) => _calibrationActive = active;
    public bool IsConnected => _session is { Usable: true } && _latest is { Connected: true, ProfileVerified: true };
    public event Action<Packet>? Notification;
    public event Action? Disconnected;
    public CalibrationDeviceContext CalibrationContext(string stableKey, long generation)
    {
        lock (_stateGate)
        {
            bool allowed = _profile?.Conditions.EvaluateForControl(new(_latest?.FirmwareVersion, _appVersionMarker)) == VersionConditionStatus.Pass;
            int Version(string name) => allowed ? _profile?.FunctionVersion(name) ?? 0 : 0;
            bool hearing = Version("hearingEnhancement") > 0 || Version("hearingEnhancementNew") > 0;
            bool earScan = Version("earScan") > 0;
            return new() { StableKey = stableKey, Generation = generation,
                ProductId = _profile?.ProductId ?? "", HearingJudgeDb = Version("hearingJudgeDb"),
                Commands = (_commands?.SupportedCommands ?? []).ToHashSet(), Events = _advertisedEvents.ToHashSet(),
                Fit = Version("fitDetection") > 0,
                HearingV1 = hearing && !earScan,
                HearingV2 = hearing && earScan,
                EarScan = earScan };
        }
    }
    public async Task<Packet> RequestAsync(ushort command, byte[] payload, CancellationToken token)
    {
        if (command == 0x0403 && payload is [0x1C, ..])
            throw new InvalidOperationException("此功能已停用，没有发送设置。");
        await _gate.WaitAsync(token);
        try
        {
            if (command == FindHeadphonesToneCodec.Command || FindHeadphonesToneActive)
                throw new InvalidOperationException("查找提示音须通过独立操作；播放期间不能提交测试指令。");
            if (!IsConnected || _commands?.Supports(command) != true)
                throw new InvalidOperationException("耳机连接或能力已变化。");
            bool cleanup = command switch
            {
                0x0405 => payload is [0],
                0x040D => payload.Length >= 2 && payload[1] == 0,
                0x040E => payload is [2] or [4],
                0x0415 => payload is [2],
                0x0403 => payload is [0x0B, 0],
                _ => false
            };
            if (_calibrationActive && !cleanup && _session!.Remaining <= 8)
                throw new InvalidOperationException("本次连接的控制次数已用完，请结束测试并重新连接后重试。");
            if (_calibrationActive && command == 0x0411 && _session!.Remaining < 16)
                throw new InvalidOperationException("本次连接的控制次数不足以确认完整保存，请结束测试并重新连接后重试。");
            if (command is 0x0405 or 0x040D or 0x040E or 0x0411 or 0x0415 or 0x0403 && !cleanup
                && _profile?.Conditions.EvaluateForControl(new(_latest?.FirmwareVersion, _appVersionMarker)) != VersionConditionStatus.Pass)
                throw new InvalidOperationException("当前应用版本未通过此测试的配置条件，未提交设置。");
            var reply = await _session!.RequestAsync(command, payload, token);
            if (!reply.Matched || reply.Payload is null || !_session.Usable)
                throw new IOException("设备响应未确认，请重新读取状态。");
            return new((ushort)(command | 0x8000), reply.Sequence ?? throw new IOException("设备响应关联未确认。"), reply.Payload);
        }
        finally { _gate.Release(); }
    }
}
