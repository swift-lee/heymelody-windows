using HeyMelodyWindows.App;
using HeyMelodyWindows.Daily;

namespace HeyMelodyWindows.Host;

public sealed partial class NativeHeadsetService
{
    internal async Task<UiFindHeadphonesToneResult> SetFindHeadphonesToneAsync(string identity, long generation,
        FindHeadphonesToneArgs args, CancellationToken token)
    {
        if (identity != _selected || generation != _generation || args.Play is not { } play || _client is not { } client)
            return new(false, null, "耳机控制连接已变化，未提交指令。");
        var result = await client.SetFindHeadphonesToneAsync(args.Session, play, args.Confirmed, args.ConfirmedWorn, token);
        return new(result.Submitted, result.AckStatus, result.Message, result.After is { } after ? Map(after) : null);
    }

    private static UiFindHeadphonesToneState? MapFindHeadphonesTone(DailySnapshot snapshot, bool valid)
    {
        var phase = snapshot.FindHeadphonesTonePhase switch
        {
            DailyFindHeadphonesTonePhase.Starting => FindHeadphonesTonePhase.Starting,
            DailyFindHeadphonesTonePhase.Ringing => FindHeadphonesTonePhase.Ringing,
            DailyFindHeadphonesTonePhase.Stopping => FindHeadphonesTonePhase.Stopping,
            DailyFindHeadphonesTonePhase.Unconfirmed => FindHeadphonesTonePhase.Unconfirmed,
            _ => FindHeadphonesTonePhase.Idle
        };
        if (phase == FindHeadphonesTonePhase.Idle && (!valid || !snapshot.Capabilities.FindHeadphonesTone)) return null;
        return new(snapshot.FindHeadphonesToneSession, phase,
            valid && snapshot.Capabilities.FindHeadphonesTone && phase == FindHeadphonesTonePhase.Idle, valid && snapshot.FindHeadphonesWorn);
    }
}
