using HeyMelody.Diagnostics;

namespace HeyMelodyWindows.Daily;

public sealed partial class DailyClient
{
    private async Task<DailyApplyResult> ApplyCoreAsync(string kind, byte value, byte level,
        EqualizerEdit? suppliedEdit, bool allowMutexChange, CancellationToken token)
    {
        using var operation = DiagnosticLog.BeginOperation(DiagnosticLog.CurrentCorrelationId);
        DiagnosticLog.Info("Daily", "apply.started", "A user-requested daily setting operation started.", new { kind, value, level, allowMutexChange, suppliedEdit });
        // Copy mutable GUI input before any await; future slider changes cannot alter this submission.
        EqualizerEdit? edit = suppliedEdit is null ? null : new()
        { Id = suppliedEdit.Id, Name = suppliedEdit.Name, MinimumGain = suppliedEdit.MinimumGain,
          MaximumGain = suppliedEdit.MaximumGain, Bands = suppliedEdit.Bands?.ToList() ?? [], Activate = suppliedEdit.Activate };
        var result = new DailyApplyResult();
        await _gate.WaitAsync(token);
        try
        {
            if (_disposed) return Reject(result, "客户端已关闭。");
            DailySnapshot before = await ReadFreshAsync(token); result.Before = before;
            if (!before.Connected || !before.ProfileVerified || before.Error is not null)
                return Reject(result, "新鲜设备状态不可用，没有发送设置。", DailyApplyFailure.StateUnavailable);
            var caps = before.Capabilities;
            ushort cmd; byte[] payload; Func<DailySnapshot, bool> target;
            Func<DailySnapshot, bool>? targetKnown = null;
            switch (kind)
            {
                case "anc-mode":
                    if (!caps.Anc || !before.AncOptions.Any(x => x.Primary == value))
                        return Reject(result, "该降噪模式未通过当前型号和状态校验。");
                    // The main selector sends the configured parent, leaving its fine adjustment to the headset.
                    cmd = 0x0404; payload = DailyParsers.BuildAnc(value);
                    target = s => s.Anc?.Primary == value;
                    targetKnown = s => s.Anc?.Primary is not null;
                    break;
                case "anc":
                    if (!caps.Anc || !before.AncOptions.Any(x => x.Primary == value && x.Level == level))
                        return Reject(result, "该降噪档位未通过当前型号和状态校验。");
                    cmd = 0x0404; payload = DailyParsers.BuildAnc(level);
                    target = s => s.Anc?.Primary == value && s.Anc.Level == level;
                    break;
                case "preset":
                    if (!caps.Preset || !before.Presets.Any(x => x.ProtocolId == value))
                        return Reject(result, "该预设编号不在型号配置中。");
                    cmd = 0x0406; payload = [value]; target = s => s.PresetId == value; break;
                case "custom-select":
                    if (!caps.CustomEqualizer || !before.CustomEqualizersRead || value == 0 ||
                        !before.CustomEqualizers.Any(x => x.Id == value))
                        return Reject(result, "没有回读到该自定义 EQ 条目。");
                    var selected = before.CustomEqualizers.Single(x => x.Id == value);
                    // Official APK Ab/Z.smali:548-583 selects with action=2 and the complete EqInfo.
                    // Built-in 0406 IDs and custom EqInfo IDs are separate namespaces.
                    var selection = new EqualizerEdit { Id = selected.Id, Name = selected.Name,
                        MinimumGain = selected.MinimumGain, MaximumGain = selected.MaximumGain,
                        Bands = selected.Bands.ToList() };
                    cmd = 0x0418; payload = DailyParsers.BuildEqualizer(selection, before.EqualizerFrequencies, preserveReportedName: true);
                    target = s => s.CustomEqualizersRead && s.CustomEqualizers.Any(x => x.Id == selected.Id && x.Selected &&
                        x.Name == selected.Name && x.MinimumGain == selected.MinimumGain &&
                        x.MaximumGain == selected.MaximumGain && x.Bands.SequenceEqual(selected.Bands));
                    break;
                case "custom-save": return await SaveCoreAsync(edit, result, token);
                case "dual":
                    if (!caps.DualConnection) return Reject(result, "双连状态未知或不可设置。");
                    cmd = 0x0403; payload = [0x11, value]; target = s => s.DualConnection == (value == 1); break;
                case "game":
                    if (!caps.GameMode || caps.GameFeatureId is not (0x06 or 0x28))
                        return Reject(result, "设备未报告可验证的游戏模式开关。");
                    cmd = 0x0403; payload = [caps.GameFeatureId.Value, value];
                    target = s => s.Capabilities.GameFeatureId == caps.GameFeatureId && s.GameMode == (value == 1); break;
                case "spatial":
                    if (!caps.SpatialMode || value > 2 || !_profile!.SpatialTypes.Contains(value) ||
                        caps.SpatialRoute == "legacy" && value > 1)
                        return Reject(result, "当前空间模式不在已核实的协议能力中。");
                    cmd = caps.SpatialRoute == "modern" ? (ushort)0x0422 : (ushort)0x0403;
                    payload = caps.SpatialRoute == "modern" ? [value] : [0x1B, value];
                    target = s => s.SpatialMode == value; break;
                case "hires":
                    if (!caps.HiRes) return Reject(result, "Hi-Res 状态未知或不可设置。");
                    cmd = 0x0403; payload = [0x18, value]; target = s => s.HiRes == (value == 1); break;
                default: return Reject(result, "未知设置请求。");
            }
            DailySnapshot latest = result.After ?? before;
            if (target(latest))
            {
                result.ReadbackMatches = true; result.Success = true; result.After = latest;
                result.Message = "当前回读已经符合请求，没有重复发送设置。"; return result;
            }
            await SendAndObserveAsync(cmd, payload, target, result, token, targetKnown: targetKnown);
            return Finish(result);
        }
        catch (Exception error)
        {
            DiagnosticLog.Error("Daily", "apply.failed", "The daily setting operation failed; submitted settings will not be replayed.", error,
                new { kind, value, level, result.SubmissionAttempted, result.TargetSubmitted, result.PrerequisiteSubmitted });
            result.FailureKind = DailyApplyFailure.TransportUnknown;
            result.Error = token.IsCancellationRequested ? "操作已取消；已提交的设置不会自动重发。" :
                "设置过程未完成；已提交的设置不会自动重发。";
            if (result.SubmissionAttempted) result.PartialEffects.Add("本次已提交设置，最终状态需要再次读取确认。");
            return Finish(result);
        }
        finally { _gate.Release(); }
    }
    private async Task SendAndObserveAsync(ushort command, byte[] payload, Func<DailySnapshot, bool> target,
        DailyApplyResult result, CancellationToken token,
        Func<DailySnapshot, bool>? targetKnown = null)
    {
        var reply = await _session!.RequestAsync(command, payload, token);
        result.TargetSubmitted |= reply.Submitted; result.AckStatus = reply.Status;
        MergeAck(result, reply);
        // Reads may reconnect; a setter is never retried, including partial-send failures.
        if (_session is not { Usable: true }) await DropSessionAsync();
        result.After = await ReadFreshAsync(token);
        bool identityMatches = result.After.ProfileVerified && result.After.ProductId == result.Before!.ProductId;
        result.ReadbackMatches = identityMatches && (targetKnown?.Invoke(result.After) ?? TargetKnown(command, payload, result.After))
            ? target(result.After) : null;
        result.Success = result.AckAccepted == true && result.ReadbackVerified;
        result.FailureKind = reply.ConnectionInterrupted ? DailyApplyFailure.Disconnected :
            result.Success ? DailyApplyFailure.None : result.AckAccepted == false ? DailyApplyFailure.PeerRejected :
            result.AckAccepted is null ? DailyApplyFailure.TransportUnknown :
            result.ReadbackMatches is null ? DailyApplyFailure.ReadbackUnavailable : DailyApplyFailure.ReadbackMismatch;
    }
    private static bool TargetKnown(ushort command, byte[] payload, DailySnapshot snapshot) => command switch
    {
        0x0404 => snapshot.Anc?.Primary is not null && snapshot.Anc.Level is not null,
        0x0406 => snapshot.PresetId is not null,
        0x0418 => snapshot.CustomEqualizersRead,
        0x0422 => snapshot.SpatialMode is not null,
        0x0403 => snapshot.RawFeatures.TryGetValue(payload[0], out byte value) && value <= 1,
        0x041B => snapshot.BassEngine is not null,
        0x0427 => snapshot.PromptVolume is not null,
        0x0423 => snapshot.GameSound is not null,
        0x0401 => payload.Length == 5 && snapshot.Gestures.Any(x => x.Device == payload[1] && x.Button == payload[2] && x.Action == payload[3]),
        _ => false
    };
    private static void MergeAck(DailyApplyResult result, DailyResponse response)
    {
        bool first = !result.SubmissionAttempted;
        result.SubmissionAttempted |= response.Submitted;
        result.ResponseMatched = first ? response.Matched : result.ResponseMatched && response.Matched;
        bool? accepted = response.Status is null ? null : response.Status == 0;
        result.AckAccepted = first ? accepted : result.AckAccepted == false || accepted == false ? false :
            result.AckAccepted == true && accepted == true ? true : null;
    }
    private static DailyApplyResult Reject(DailyApplyResult result, string text, DailyApplyFailure failure = DailyApplyFailure.Validation)
    { result.Error = text; result.Message = text; result.FailureKind = failure; DiagnosticLog.Warning("Daily", "apply.rejected", text, data: result); return result; }
    private static DailyApplyResult Finish(DailyApplyResult result)
    {
        if (result.Success) result.Message = "设备已确认设置，状态回读一致。";
        else if (result.Error is not null) result.Message = result.Error;
        else if (result.FailureKind == DailyApplyFailure.Disconnected)
            result.Message = "设置时控制连接已中断，已尝试重新读取当前状态；不会自动重发。";
        else if (result.AckAccepted == false) result.Message = "设备拒绝了设置；请查看重新读取的状态。";
        else if (result.SubmissionAttempted && result.AckAccepted is null)
            result.Message = result.ReadbackVerified ? "状态回读符合请求，但设置响应未完整确认；不会自动重发。" :
                "设置已提交，响应或回读未确认；不会自动重发。";
        else result.Message = "设置响应与状态回读未同时确认，请刷新状态。";
        DiagnosticLog.Info("Daily", "apply.completed", "The daily setting operation returned its acknowledgement/readback outcome.", result);
        return result;
    }
    private async Task<DailyApplyResult> SaveCoreAsync(EqualizerEdit? edit, DailyApplyResult result, CancellationToken token)
    {
        var before = result.Before!;
        if (edit is null || !before.Capabilities.CustomEqualizer || !before.CustomEqualizersRead)
            return Reject(result, "自定义 EQ 编辑数据或设备列表不可用。");
        var existing = before.CustomEqualizers.FirstOrDefault(x => x.Id == edit.Id && edit.Id != 0);
        if (edit.Id != 0 && existing is null) return Reject(result, "该 EQ 条目已不存在。");
        if (before.CustomEqualizers.Any(x => x.Id != edit.Id && x.Name == edit.Name))
            return Reject(result, "该名称已经存在，请使用另一个名称。");
        if (existing is not null && (existing.MinimumGain != edit.MinimumGain || existing.MaximumGain != edit.MaximumGain))
            return Reject(result, "编辑范围与该条目刚回读的增益范围不一致。");
        if (existing is null)
        {
            if (before.CustomEqualizers.Count >= before.CustomEqualizerCapacity)
                return Reject(result, "设备的自定义 EQ 槽位已满，请删除一个方案后再创建。");
            // Official APK Ab/Z$i.smali:115/123 creates action=1 with +6/-6 dB. It is source policy,
            // not a guessed device-reported range. Updates still require the exact returned entry limits.
            if (edit.MinimumGain != -6 || edit.MaximumGain != 6)
                return Reject(result, "新建 EQ 需要使用已核实的官方默认范围 -6 至 +6 dB。");
        }
        if (!edit.Activate)
            return Reject(result, "本客户端仅提供保存并选用；不会自动补发选用请求。");
        byte[] payload;
        try { payload = DailyParsers.BuildEqualizer(edit, before.EqualizerFrequencies,
            preserveReportedName: existing is not null && existing.Name == edit.Name); }
        catch (Exception error) { DiagnosticLog.Error("Daily", "equalizer.validation-failed", "The equalizer edit did not pass validation.", error, edit); return Reject(result, "EQ 名称、频率或增益未通过校验。"); }
        if (existing is { Selected: true } && existing.Name == edit.Name &&
            existing.MinimumGain == edit.MinimumGain && existing.MaximumGain == edit.MaximumGain &&
            existing.Bands.SequenceEqual(edit.Bands))
        {
            result.Success = true; result.ReadbackMatches = true; result.After = before;
            result.ObservedEqualizerId = existing.Id;
            result.Message = "当前 EQ 已符合请求，没有重复发送设置。";
            return result;
        }
        bool Match(CustomEqualizer x) => x.Name == edit.Name && x.MinimumGain == edit.MinimumGain &&
            x.MaximumGain == edit.MaximumGain && x.Bands.SequenceEqual(edit.Bands) &&
            (edit.Id != 0 ? x.Id == edit.Id : !before.CustomEqualizers.Any(old => old.Id == x.Id));
        await SendAndObserveAsync(0x0418, payload, s => s.CustomEqualizersRead &&
            s.CustomEqualizers.Count(x => Match(x) && x.Selected) == 1, result, token);
        if (result.After is { CustomEqualizersRead: true } observed)
        {
            var matches = observed.CustomEqualizers.Where(Match).ToArray();
            if (matches.Length == 1) result.ObservedEqualizerId = matches[0].Id;
        }
        if (!result.Success)
        {
            if (result.After is { CustomEqualizersRead: true } after && after.CustomEqualizers.Count(Match) == 1)
                result.PartialEffects.Add("自定义 EQ 内容已回读匹配，选中状态或设置响应尚未完整确认。");
            return Finish(result);
        }
        result.PartialEffects.Add("自定义 EQ 已保存并选用设备回读的条目。");
        return Finish(result);
    }
}
