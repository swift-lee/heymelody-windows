using System.Text;
using System.Threading.Channels;
using HeyMelodyWindows.Control;
using HeyMelodyWindows.Daily;
using HeyMelodyWindows.Devices;
using HeyMelodyWindows.Protocol;

if (args.Contains("--diagnostics-tests"))
{
    Console.WriteLine($"{await DiagnosticLoggingTests.RunAsync()} daily diagnostic tests passed.");
    return;
}

int passed = 0;
void Check(bool test, string message = "assertion") { if (!test) throw new Exception(message); }
void Bad(Action test) { try { test(); } catch (FormatException) { return; } throw new Exception("expected strict rejection"); }
async Task Test(string name, Func<Task> action)
{ await action(); passed++; Console.WriteLine("PASS " + name); }
await Test("battery component pairs and charging bit", () =>
{
    var b = DailyParsers.ParseBattery([0, 4, 1, 0xE4, 2, 54, 3, 0, 8, 17]);
    Check(b.Left?.Percent == 100 && b.Left.Charging == true && b.Right?.Percent == 54 && b.Case?.Raw == 0 && b.Case.Percent is null);
    Bad(() => DailyParsers.ParseBattery([0, 4, 90, 0, 80, 0, 60, 1]));
    Bad(() => DailyParsers.ParseBattery([0, 2, 1, 50, 1, 60]));
    Bad(() => DailyParsers.ParseBattery([1, 1, 1, 50])); return Task.CompletedTask;
});
await Test("battery unrecognized values retain raw and never become 127 percent", () =>
{ var b = DailyParsers.ParseBattery([0, 1, 1, 0xFF]); Check(b.Left?.Raw == 255 && b.Left.Percent is null); return Task.CompletedTask; });
await Test("ANC protocol tree preserves children and bitmap indices", () =>
{
    var p = X4Profile.Load();
    foreach (var (primary, level) in new (byte,byte)[] { (0,3),(2,8),(1,4),(1,5),(1,6),(1,7),(11,12),(11,13),(11,14) })
    {
        var s = DailyParsers.ParseAnc(Fake.AncReply(level), p);
        Check(s.Primary == primary && s.Level == level);
    }
    Check(Convert.ToHexString(DailyParsers.BuildAnc(14)) == "01010040");
    var unknown = DailyParsers.ParseAnc([0, 1, 1, 0x10, 0x80], p);
    Check(unknown.Primary is null && unknown.RawMask.Length == 2);
    Bad(() => DailyParsers.ParseAnc([0, 1, 1], p)); return Task.CompletedTask;
});
await Test("feature and spatial unknown states stay unknown", () =>
{
    Check(DailyParsers.ParseSpatial([0, 7]) is null);
    Check(DailyParsers.ParseFeatures([0, 1, 0x28, 7])[0x28] == 7);
    Bad(() => DailyParsers.ParseFeatures([0, 2, 0x18, 1, 0x18, 0]));
    Bad(() => DailyParsers.ParsePreset([0])); return Task.CompletedTask;
});
await Test("ten-band EQ independent exact write golden", () =>
{
    var eq = new EqualizerEdit { Id = 40, Name = "EQ1", MinimumGain = -6, MaximumGain = 6,
        Bands = Fake.Frequencies.Select((f,i) => new EqualizerBand(f,(sbyte)(i-5))).ToList() };
    string golden = "02FA0628034551310A1F00FB3E00FC7D00FDFA00FEF401FFE80300D00701A00F02401F03803E04";
    Check(Convert.ToHexString(DailyParsers.BuildEqualizer(eq, Fake.Frequencies)) == golden);
    var decoded = DailyParsers.ParseEqualizers(Fake.EncodeEntries([Fake.FromEdit(eq)]));
    Check(decoded.Single().Bands.SequenceEqual(eq.Bands));
    eq.Bands[0] = new(32, 0); Bad(() => DailyParsers.BuildEqualizer(eq, Fake.Frequencies)); return Task.CompletedTask;
});
await Test("EQ truncation duplicate ID invalid UTF8 range and selected-state rejection", () =>
{
    var eq = Fake.Entry(); byte[] wire = Fake.EncodeEntries([eq]);
    for (int length = 0; length < wire.Length; length++) { int n = length; Bad(() => DailyParsers.ParseEqualizers(wire.AsSpan(0,n))); }
    Bad(() => DailyParsers.ParseEqualizers(Fake.EncodeEntries([eq,eq])));
    byte[] invalid = wire.ToArray(); invalid[7] = 0xFF; Bad(() => DailyParsers.ParseEqualizers(invalid));
    invalid = wire.ToArray(); invalid[2] = 3; Bad(() => DailyParsers.ParseEqualizers(invalid));
    var e2 = Fake.Entry(); e2.Id = 41; e2.Selected = true; eq.Selected = true;
    Bad(() => DailyParsers.ParseEqualizers(Fake.EncodeEntries([eq,e2]))); return Task.CompletedTask;
});
await Test("refresh is GET only and preserves explicit preset IDs", async () =>
{
    var f = new Fake(); await using var c = new DailyClient(f);
    var s = await c.RefreshAsync(); Check(s.ProfileVerified && s.Battery?.Left?.Percent == 75);
    Check(s.Presets.Select(x => x.ProtocolId).SequenceEqual(new byte[] {0,7,2,1,3}));
    Check(s.Capabilities.GameFeatureId == 0x28 && s.GameMode == false && s.HiRes == true);
    Check(f.Writes.Count == 0 && s.CustomEqualizersRead && s.EqualizerFrequencies.Length == 10);
});
await Test("preset uses requested protocol ID and verifies matching ACK", async () =>
{
    var f = new Fake { SendOldSequenceFirst = true }; await using var c = new DailyClient(f);
    var r = await c.ApplyPresetAsync(7); Check(r.Success && r.ResponseMatched && r.ReadbackVerified);
    Check(f.Writes.Single().Command == 0x0406 && f.Writes[0].Payload.SequenceEqual(new byte[] {7}));
});
await Test("ANC leaf payload and exact readback", async () =>
{
    var f = new Fake(); await using var c = new DailyClient(f);
    var r = await c.ApplyAncAsync(11,14); Check(r.Success && Convert.ToHexString(f.Writes.Single().Payload) == "01010040");
    r = await c.ApplyAncAsync(11,4); Check(!r.SubmissionAttempted && f.Writes.Count == 1);
});
await Test("modern game route is selected only from reported feature", async () =>
{
    var f = new Fake(); await using var c = new DailyClient(f);
    Check((await c.SetGameModeAsync(true)).Success && f.Writes.Single().Payload.SequenceEqual(new byte[] {0x28,1}));
    f = new Fake { ReportModern = false }; await using var legacy = new DailyClient(f);
    Check((await legacy.SetGameModeAsync(true)).Success && f.Writes.Single().Payload.SequenceEqual(new byte[] {6,1}));
});
await Test("dual toggle verifies feature 11 without retry", async () =>
{
    var f = new Fake(); await using var c = new DailyClient(f);
    Check((await c.SetDualConnectionAsync(true)).Success && f.Writes.Single().Payload[0] == 0x11);
});
await Test("unknown feature state blocks setter", async () =>
{
    var f = new Fake(); f.Features[0x11] = 2; await using var c = new DailyClient(f);
    var r = await c.SetDualConnectionAsync(true); Check(!r.SubmissionAttempted && f.Writes.Count == 0);
});
await Test("spatial submits only its mode while HiRes remains enabled", async () =>
{
    var f = new Fake(); await using var c = new DailyClient(f);
    var r = await c.SetSpatialModeAsync(2); Check(r.Success && r.PartialEffects.Count == 0 && !r.PrerequisiteSubmitted);
    Check(f.Writes.Single().Command == 0x0422 && f.Writes.Single().Payload.SequenceEqual(new byte[] {2})
        && r.After?.HiRes == true && r.After.SpatialMode == 2);
});
await Test("HiRes submits only feature18 while spatial remains enabled", async () =>
{
    var f = new Fake { Spatial = 1 }; f.Features[0x18] = 0; await using var c = new DailyClient(f);
    var r = await c.SetHiResAsync(true); Check(r.Success && !r.PrerequisiteSubmitted && r.PartialEffects.Count == 0);
    Check(f.Writes.Single().Command == 0x0403 && f.Writes.Single().Payload.SequenceEqual(new byte[] {0x18,1})
        && r.After?.HiRes == true && r.After.SpatialMode == 1);
});
await Test("spatial rejection submits one target even with compatibility consent", async () =>
{
    var f = new Fake { SetterStatus = 1 }; await using var c = new DailyClient(f);
    var r = await c.SetSpatialModeAsync(1,true);
    Check(!r.Success && r.TargetSubmitted && !r.PrerequisiteSubmitted && r.AckAccepted == false
        && f.Writes.Single().Command == 0x0422 && r.PartialEffects.Count == 0);
});
await Test("compatibility consent does not disable HiRes before spatial", async () =>
{
    var f = new Fake { HideSpatialAfterHiResOff = true }; await using var c = new DailyClient(f);
    var r = await c.SetSpatialModeAsync(2,true); Check(r.Success && f.Writes.Single().Command == 0x0422
        && r.After?.HiRes == true && r.After.SpatialMode == 2 && r.PartialEffects.Count == 0);
});
await Test("partial transport send is never replayed", async () =>
{
    var f = new Fake { FailSetSend = true }; await using var c = new DailyClient(f);
    var r = await c.ApplyPresetAsync(7); Check(!r.Success && r.SubmissionAttempted && r.AckAccepted is null && f.Writes.Count == 1 && f.Opens == 2);
    Check(r.FailureKind == DailyApplyFailure.Disconnected && r.After?.Connected == true);
});
await Test("SET EOF remains a disconnection after successful read-only reconnect", async () =>
{
    foreach (bool partialFrame in new[] { false, true })
    {
        var f = new Fake { SetReadEof = true, TruncatedSetFrame = partialFrame }; await using var c = new DailyClient(f);
        var r = await c.ApplyPresetAsync(7);
        Check(r.FailureKind == DailyApplyFailure.Disconnected && !r.Success && r.TargetSubmitted
            && r.AckAccepted is null && r.ReadbackMatches == true && r.After?.Connected == true
            && f.Writes.Count == 1 && f.Opens == 2);
    }
});
await Test("SET IO and socket reset differ from timeout or malformed response", async () =>
{
    foreach (var (failure, expected) in new (Exception, DailyApplyFailure)[]
    {
        (new IOException(), DailyApplyFailure.Disconnected),
        (new System.ComponentModel.Win32Exception(10054), DailyApplyFailure.Disconnected),
        (new System.Net.Sockets.SocketException(10053), DailyApplyFailure.Disconnected),
        (new System.ComponentModel.Win32Exception(10060), DailyApplyFailure.TransportUnknown),
        (new TimeoutException(), DailyApplyFailure.TransportUnknown),
        (new TaskCanceledException(), DailyApplyFailure.TransportUnknown),
        (new FormatException(), DailyApplyFailure.TransportUnknown),
        (new InvalidDataException(), DailyApplyFailure.TransportUnknown)
    })
    {
        // Unix maps SocketException codes to errno, so this Winsock value is meaningful only on Windows.
        if (failure is System.Net.Sockets.SocketException && !OperatingSystem.IsWindows()) continue;
        var f = new Fake { SetReadFailure = failure }; await using var c = new DailyClient(f);
        var r = await c.ApplyPresetAsync(7);
        Check(r.FailureKind == expected && !r.Success && r.AckAccepted is null
            && r.ReadbackMatches == true && r.After?.Connected == true && f.Writes.Count == 1 && f.Opens == 2,
            failure.GetType().Name);
    }
});
await Test("empty ACK stays unknown even with matching readback", async () =>
{
    var f = new Fake { EmptySetAck = true }; await using var c = new DailyClient(f);
    var r = await c.ApplyPresetAsync(7); Check(!r.Success && r.AckAccepted is null && r.ResponseMatched && r.ReadbackVerified && f.Writes.Count == 1);
});
await Test("explicit rejected ACK never becomes success from state", async () =>
{
    var f = new Fake { SetterStatus = 1 }; await using var c = new DailyClient(f);
    var r = await c.ApplyPresetAsync(7); Check(!r.Success && r.AckAccepted == false && r.ReadbackVerified);
});
await Test("custom update uses complete action2 and selected entry independent of builtin ID", async () =>
{
    var f = new Fake(); await using var c = new DailyClient(f);
    var edit = Fake.Edit(); edit.Bands[0] = new(31,3);
    var r = await c.SaveEqualizerAsync(edit); Check(r.Success && f.Preset == 0 && f.Entries.Single().Bands[0].Gain == 3 && f.Entries.Single().Selected);
    Check(f.Writes.Single().Command == 0x0418 && f.Writes.Single().Payload[0] == 2);
});
await Test("custom create gets allocated ID from complete matching entry", async () =>
{
    var f = new Fake(); await using var c = new DailyClient(f);
    var edit = Fake.Edit(); edit.Id = 0; edit.Name = "新建1";
    var r = await c.SaveEqualizerAsync(edit); Check(r.Success && f.Preset == 0 && f.Entries.Single(x=>x.Name=="新建1").Id==51 && f.Writes.Single().Command==0x0418);
});
await Test("empty EQ list creates with official APK default and returned allocation", async () =>
{
    var f = new Fake(); f.Entries.Clear(); await using var c = new DailyClient(f);
    var edit = Fake.Edit(); edit.Id = 0;
    var r = await c.SaveEqualizerAsync(edit); Check(r.Success && f.Preset == 0 && f.Entries.Single().Id == 51 && f.Entries.Single().Selected && f.Writes.Count == 1);
});
await Test("custom update changed range and duplicate name rejected before SET", async () =>
{
    var f = new Fake(); await using var c = new DailyClient(f);
    var edit = Fake.Edit(); edit.MinimumGain = -5;
    Check(!(await c.SaveEqualizerAsync(edit)).SubmissionAttempted);
    edit = Fake.Edit(); edit.Id = 0;
    Check(!(await c.SaveEqualizerAsync(edit)).SubmissionAttempted && f.Writes.Count == 0);
});
await Test("custom save uncertain ACK never follows with activation", async () =>
{
    var f = new Fake { EmptySetAck = true }; await using var c = new DailyClient(f);
    var edit = Fake.Edit(); edit.Bands[0] = new(31,2);
    var r = await c.SaveEqualizerAsync(edit); Check(!r.Success && r.ReadbackVerified && f.Writes.Count == 1);
});
await Test("settings already matched are not resent", async () =>
{
    var f = new Fake(); await using var c = new DailyClient(f);
    var r = await c.ApplyPresetAsync(0); Check(r.Success && !r.SubmissionAttempted && f.Writes.Count == 0);
});
await Test("unsupported fresh product blocks writes", async () =>
{
    var f = new Fake { UnsupportedProduct = true }; await using var c = new DailyClient(f);
    var r = await c.SetDualConnectionAsync(true); Check(!r.SubmissionAttempted && f.Writes.Count == 0);
});
await Test("sequence exhaustion rotates connection without reusing sequence in a session", async () =>
{
    var f = new Fake(); await using var c = new DailyClient(f);
    for (int n = 0; n < 60; n++) Check((await c.RefreshAsync()).ProfileVerified);
    Check(f.Opens >= 3 && f.SequenceSets.All(x => x.Value.Count == x.Value.Distinct().Count()));
});
await Test("concurrent operations are serialized and hold device responses separately", async () =>
{
    var f = new Fake(); await using var c = new DailyClient(f);
    var results = await Task.WhenAll(c.ApplyPresetAsync(7),c.SetDualConnectionAsync(true),c.SetGameModeAsync(true));
    Check(results.All(x => x.Success) && f.Writes.Count == 3 && f.MaximumConcurrentSend == 1);
});
await Test("smart live levels cannot invent off transparency or adaptive mode as smart", () =>
{
    var profile=X4Profile.Load();
    foreach(byte level in new byte[]{3,8,12,13,14})
    {
        var encoded=Fake.AncReply(level).AsSpan(1).ToArray();encoded[0]=4;
        var state=DailyParsers.ParseAnc([0,..encoded],profile);
        Check(state.Primary is null && state.Level is null && state.Kind==4 && state.RawMask.Length>0);
    }
    foreach(byte level in new byte[]{4,5,6,7})
    {
        var encoded=Fake.AncReply(level).AsSpan(1).ToArray();encoded[0]=4;
        var state=DailyParsers.ParseAnc([0,..encoded],profile);Check(state.Primary==1 && state.Level==7);
    }
    return Task.CompletedTask;
});
await Test("official new EQ range rejects invented wider or narrower bounds before submission", async () =>
{
    var f=new Fake();f.Entries.Clear();await using var c=new DailyClient(f);
    var edit=Fake.Edit();edit.Id=0;edit.MinimumGain=-12;edit.MaximumGain=12;
    Check(!(await c.SaveEqualizerAsync(edit)).SubmissionAttempted && f.Writes.Count==0);
});
await Test("preset and adaptive labels are attached to original protocol IDs", async () =>
{
    var f=new Fake();await using var c=new DailyClient(f);var s=await c.RefreshAsync();
    Check(s.Presets.Single(x=>x.ProtocolId==7).Name=="丹拿高解析");
    Check(s.AncOptions.Single(x=>x.Primary==11 && x.Level==12).Name=="更少噪音");
    Check(s.AncOptions.Single(x=>x.Primary==11 && x.Level==13).Name=="标准");
    Check(s.AncOptions.Single(x=>x.Primary==11 && x.Level==14).Name=="更多噪音");
});
await Test("unknown modern game status never silently falls back to legacy route", async () =>
{
    var f=new Fake();f.Features[0x28]=3;await using var c=new DailyClient(f);
    var s=await c.RefreshAsync();Check(s.GameMode is null && s.Capabilities.GameFeatureId==0x28);
    Check(!(await c.SetGameModeAsync(true)).SubmissionAttempted && f.Writes.Count==0);
});
await Test("spatial legacy fallback requires actual feature and never offers tracked mode", async () =>
{
    var f=new Fake { NoModernSpatial=true };f.Features[0x18]=0;await using var c=new DailyClient(f);
    var s=await c.RefreshAsync();Check(s.Capabilities.SpatialRoute=="legacy" && s.SpatialMode==0);
    Check((await c.SetSpatialModeAsync(1)).Success && f.Writes.Single().Payload.SequenceEqual(new byte[]{0x1B,1}));
    Check(!(await c.SetSpatialModeAsync(2)).SubmissionAttempted && f.Writes.Count==1);
});
await Test("custom selection independent complete action2 golden and unchanged builtin state", async () =>
{
    var f=new Fake();await using var c=new DailyClient(f);
    var r=await c.SelectCustomEqualizerAsync(40);
    string golden="02FA0628034551310A1F00003E00007D0000FA0000F40100E80300D00700A00F00401F00803E00";
    Check(r.Success && f.Writes.Single().Command==0x0418 &&
        Convert.ToHexString(f.Writes.Single().Payload)==golden && f.Preset==0 && f.Entries.Single().Selected);
    Check(!(await c.SelectCustomEqualizerAsync(40)).SubmissionAttempted && f.Writes.Count==1);
});
await Test("custom Save without activation cannot pretend unsupported protocol operation", async () =>
{
    var f=new Fake();await using var c=new DailyClient(f);var edit=Fake.Edit();edit.Activate=false;
    Check(!(await c.SaveEqualizerAsync(edit)).SubmissionAttempted && f.Writes.Count==0);
});
await Test("factory custom selection roundtrips real UTF8 spaces using complete action2 golden", async () =>
{
    var f=new Fake();f.Entries[0].Id=4;f.Entries[0].Name="哈曼特调 Sound by Harman";await using var c=new DailyClient(f);
    var r=await c.SelectCustomEqualizerAsync(4);
    string golden="02FA06041CE59388E69BBCE789B9E8B08320536F756E64206279204861726D616E0A1F00003E00007D0000FA0000F40100E80300D00700A00F00401F00803E00";
    Check(r.Success && f.Writes.Single().Command==0x0418 && Convert.ToHexString(f.Writes.Single().Payload)==golden);
});
await Test("factory custom update preserves unchanged spaced name without applying new-name rule", async () =>
{
    var f=new Fake();f.Entries[0].Name="哈曼特调 Sound by Harman";await using var c=new DailyClient(f);
    var edit=Fake.Edit();edit.Name=f.Entries[0].Name;edit.Bands[0]=new(31,2);
    Check((await c.SaveEqualizerAsync(edit)).Success && f.Entries[0].Name==edit.Name && f.Writes.Count==1);
});
await Test("saved unselected custom EQ is reported as partial without guessing activation command", async () =>
{
    var f=new Fake { SelectCustomOnWrite=false };await using var c=new DailyClient(f);
    var edit=Fake.Edit();edit.Bands[0]=new(31,2);var r=await c.SaveEqualizerAsync(edit);
    Check(!r.Success && !r.ReadbackVerified && r.AckAccepted==true && r.PartialEffects.Count==1 && f.Writes.Count==1);
});
await Test("scoped polling queries its section and retains other confirmed state", async () =>
{
    var f = new Fake(); await using var c = new DailyClient(f);
    var before = await c.RefreshAsync(); f.Requests.Clear();
    f.Preset = 7;
    var battery = await c.RefreshAsync(DailyRefreshScope.Battery);
    Check(f.Requests.Select(x => x.Command).SequenceEqual(new ushort[] { 0x0106 }));
    Check(battery.PresetId == before.PresetId && battery.Anc?.Level == before.Anc?.Level && battery.Capabilities.GameMode);
    f.Requests.Clear();
    var eq = await c.RefreshAsync(DailyRefreshScope.Equalizer);
    Check(eq.PresetId == 7 && f.Requests.Select(x => x.Command).SequenceEqual(new ushort[] { 0x010F, 0x0122 }));
    Check(f.Writes.Count == 0);
});
await Test("a scoped read on a new session first verifies identity and commands", async () =>
{
    var f = new Fake(); await using var c = new DailyClient(f);
    var s = await c.RefreshAsync(DailyRefreshScope.Anc);
    Check(s.ProfileVerified && s.Capabilities.Anc && s.Battery is null && !s.Capabilities.CustomEqualizer);
    Check(f.Requests.Select(x => x.Command).SequenceEqual(new ushort[] { 0x0103, 0x0100, 0x010B, 0x010C }));
});
await Test("color is read once per product and reaches the snapshot without a warning", async () =>
{
    var f = new Fake { Color = 3 }; await using var c = new DailyClient(f);
    var s = await c.RefreshAsync();
    Check(s.Color == 3 && s.ColorObservedAtUtc is not null && s.Warnings.Count == 0 && f.Requests.Count(x => x.Command == 0x010B) == 1);
    var again = await c.RefreshAsync(); var scoped = await c.RefreshAsync(DailyRefreshScope.Battery);
    Check(again.Color == 3 && scoped.Color == 3 && f.Requests.Count(x => x.Command == 0x010B) == 1 && f.Writes.Count == 0);
});
await Test("a rejected color read stays unknown and keeps the profile usable", async () =>
{
    var f = new Fake(); f.ReadReplies[0x010B] = [1]; await using var c = new DailyClient(f);
    var s = await c.RefreshAsync();
    Check(s.Color is null && s.ProfileVerified && s.Warnings.Count == 0 && s.Capabilities.Anc && f.Opens == 1);
});
await Test("an unanswered color read keeps the session and the following reads", async () =>
{
    var f = new Fake(); f.Silent.Add(0x010B); await using var c = new DailyClient(f);
    var s = await c.RefreshAsync();
    Check(s.Color is null && s.ProfileVerified && s.Connected && s.Error is null && f.Opens == 1 && s.Capabilities.Anc && s.Battery is not null);
    var again = await c.RefreshAsync(DailyRefreshScope.Anc);
    Check(again.Capabilities.Anc && f.Opens == 1 && f.Requests.Count(x => x.Command == 0x010B) == 1);
});
await Test("returned mutable snapshots cannot corrupt the scoped cache", async () =>
{
    var f = new Fake(); await using var c = new DailyClient(f);
    var s = await c.RefreshAsync();
    s.RawFeatures[0x18] = 0; s.CustomEqualizers.Clear(); s.Anc!.Level = 3; s.Capabilities.Preset = false;
    var after = await c.RefreshAsync(DailyRefreshScope.Battery);
    Check(after.HiRes == true && after.CustomEqualizers.Count == 1 && after.Anc?.Level == f.AncLevel && after.Capabilities.Preset);
});
await Test("scoped refresh merges the most recent setter readback", async () =>
{
    var f = new Fake(); await using var c = new DailyClient(f);
    await c.RefreshAsync(); Check((await c.ApplyPresetAsync(7)).Success);
    var s = await c.RefreshAsync(DailyRefreshScope.Battery);
    Check(s.PresetId == 7 && s.Capabilities.Preset);
});
await Test("malformed scoped state disables only the queried capability and recovers", async () =>
{
    var f = new Fake(); await using var c = new DailyClient(f);
    await c.RefreshAsync(); f.ReadReplies[0x010C] = [0, 1];
    var s = await c.RefreshAsync(DailyRefreshScope.Anc);
    Check(s.Connected && s.Anc is null && !s.Capabilities.Anc && s.Capabilities.Preset && s.HiRes == true && s.Warnings.Count == 1);
    f.ReadReplies.Clear(); s = await c.RefreshAsync(DailyRefreshScope.Anc);
    Check(s.Capabilities.Anc && s.Warnings.Count == 0);
});
await Test("rejected scoped state stays unknown without invalidating unrelated controls", async () =>
{
    var f = new Fake(); await using var c = new DailyClient(f);
    await c.RefreshAsync(); f.ReadReplies[0x0106] = [1];
    var s = await c.RefreshAsync(DailyRefreshScope.Battery);
    Check(s.Battery is null && !s.Capabilities.Battery && s.Capabilities.Anc && s.Capabilities.CustomEqualizer && s.Connected);
});
await Test("rotated scoped sessions retain state after fresh identity verification", async () =>
{
    var f = new Fake(); await using var c = new DailyClient(f); await c.RefreshAsync();
    for (int n = 0; n < 250; n++)
    {
        var s = await c.RefreshAsync(DailyRefreshScope.Battery);
        Check(s.ProfileVerified && s.Capabilities.Anc && s.CustomEqualizersRead && s.HiRes == true);
    }
    Check(f.Opens >= 2 && f.SequenceSets.All(x => x.Value.Distinct().Count() == x.Value.Count));
});
await Test("a changed scoped product invalidates cached capabilities", async () =>
{
    var f = new Fake(); await using var c = new DailyClient(f); await c.RefreshAsync(); f.UnsupportedProduct = true;
    var s = await c.RefreshAsync(DailyRefreshScope.Identity);
    Check(!s.ProfileVerified && !s.Capabilities.Preset && s.Anc is null && s.CustomEqualizers.Count == 0 && s.Error is not null);
});
await Test("missing readback differs from an observed mismatch in the apply receipt", async () =>
{
    var f = new Fake { ThrowReadsAfterWrite = true }; await using var c = new DailyClient(f);
    var r = await c.ApplyPresetAsync(7);
    Check(r.TargetSubmitted && !r.PrerequisiteSubmitted && r.AckStatus == 0 && r.ReadbackMatches is null &&
        r.FailureKind == DailyApplyFailure.ReadbackUnavailable && f.Writes.Count == 1);
});
await Test("spatial rejection receipt refers to the requested setting", async () =>
{
    var f = new Fake { SetterStatus = 1 }; await using var c = new DailyClient(f);
    var r = await c.SetSpatialModeAsync(2, true);
    Check(!r.PrerequisiteSubmitted && r.TargetSubmitted && r.AckStatus == 1 && r.ReadbackMatches == true &&
        r.FailureKind == DailyApplyFailure.PeerRejected && f.Writes.Count == 1);
});
await Test("existing selected matching EQ saves no duplicate SET", async () =>
{
    var f = new Fake(); f.Entries[0].Selected = true; await using var c = new DailyClient(f);
    var r = await c.SaveEqualizerAsync(Fake.Edit());
    Check(r.Success && !r.TargetSubmitted && r.ReadbackMatches == true && r.ObservedEqualizerId == 40 && f.Writes.Count == 0);
});
await Test("created exact-match EQ exposes its observed ID even with unknown ACK", async () =>
{
    var f = new Fake { EmptySetAck = true }; await using var c = new DailyClient(f);
    var edit = Fake.Edit(); edit.Id = 0; edit.Name = "New1";
    var r = await c.SaveEqualizerAsync(edit);
    Check(!r.Success && r.TargetSubmitted && !r.PrerequisiteSubmitted && r.AckAccepted is null &&
        r.ReadbackMatches == true && r.ObservedEqualizerId == 51 && f.Writes.Count == 1);
});
await Test("monitoring subscribes only offered supported events and chooses capability-gated multi registration", async () =>
{
    foreach (bool legacy in new[] { false, true })
    {
        var f = new Fake { LegacyRegistration = legacy }; await using var c = new DailyClient(f);
        await c.RefreshAsync(); Check(!f.Requests.Any(x => x.Command is >= 0x0200 and <= 0x0205));
        await c.StartMonitoringAsync(); await c.StartMonitoringAsync();
        Check(f.Requests.Count(x => x.Command == 0x0200) == 1);
        byte[] ids = [1,2,3,5,6,15];
        if (legacy) Check(f.Requests.Where(x => x.Command == 0x0201).Select(x => x.Payload.Single()).SequenceEqual(ids));
        else Check(f.Requests.Single(x => x.Command == 0x0205).Payload.SequenceEqual(new byte[] {6,1,2,3,5,6,15}));
        Check(f.Writes.Count == 0 && !f.Requests.Any(x => x.Command == 0x0202));
    }
});
await Test("notification rejection classification preserves daily failures across refreshes and resets with sessions", async () =>
{
    foreach (bool legacy in new[] { false, true })
    foreach (byte[] ids in new byte[][] { [4,8,14], [1,4,2,8,3,14,5,6,15], [14,15,8,6,4,5,3,2,1] })
    foreach (bool dailyFailure in new[] { false, true })
    {
        var f = new Fake { LegacyRegistration = legacy };
        f.ReadReplies[0x0200] = [0,(byte)ids.Length,..ids];
        foreach (byte id in ids) f.RegistrationReplies[id] = [(byte)(id is 4 or 8 or 14 || dailyFailure && id == 1 ? 2 : 0)];
        f.ReadReplies[0x0205] = [0,(byte)ids.Length,..ids.SelectMany(id => new byte[] {id,f.RegistrationReplies[id][0]})];
        await using var c = new DailyClient(f); await c.RefreshAsync(); await c.StartMonitoringAsync();
        bool expected = dailyFailure && ids.Contains((byte)1);
        foreach (var scope in new[] { DailyRefreshScope.Full, DailyRefreshScope.Battery, DailyRefreshScope.Anc })
            Check((await c.RefreshAsync(scope)).Warnings.Count != 0 == expected);
        Check(c.CalibrationContext("device", 1).Events.Contains(4) && f.Writes.Count == 0);
        // Renew the transport using the existing safe sequence exhaustion path.
        f.ReadReplies[0x0205] = [0,(byte)ids.Length,..ids.SelectMany(id => new byte[] {id,0})];
        f.RegistrationReplies.Clear();
        for (int n = 0; n < 245; n++) await c.RefreshAsync(DailyRefreshScope.Battery);
        Check(f.Opens >= 2 && (await c.RefreshAsync()).Warnings.Count == 0);
    }
});
await Test("unconfirmed calibration registration and capability failures remain visible", async () =>
{
    foreach (byte[] reply in new byte[][] { [2], [], [0], [0,3,4,0,8,2], [0,3,4,0,4,2,14,2], [0,3,4,0,8,2,99,2] })
    {
        var f = new Fake(); f.ReadReplies[0x0200] = [0,3,4,8,14]; f.ReadReplies[0x0205] = reply;
        await using var c = new DailyClient(f); await c.RefreshAsync();
        try { await c.StartMonitoringAsync(); } catch (IOException) { }
        var s = await c.RefreshAsync(); Check(s.Warnings.Count != 0 || s.Error is not null);
    }
    var legacy = new Fake { LegacyRegistration = true }; legacy.ReadReplies[0x0200] = [0,1,4]; legacy.RegistrationReplies[4] = [];
    await using (var c = new DailyClient(legacy))
    {
        await c.RefreshAsync(); try { await c.StartMonitoringAsync(); } catch (IOException) { }
        var s = await c.RefreshAsync(); Check(s.Warnings.Count != 0 || s.Error is not null);
    }
    foreach (byte[] reply in new byte[][] { [2], [], [0,2,4], [0,2,4,4] })
    {
        var f = new Fake(); f.ReadReplies[0x0200] = reply;
        await using var c = new DailyClient(f); await c.RefreshAsync();
        try { await c.StartMonitoringAsync(); } catch (FormatException) { } catch (IOException) { }
        var s = await c.RefreshAsync(); Check(s.Warnings.Count != 0 || s.Error is not null);
    }
    var reads = new Fake(); reads.ReadReplies[0x0200] = [0,1,4]; reads.ReadReplies[0x0205] = [0,1,4,2];
    await using (var c = new DailyClient(reads))
    {
        await c.RefreshAsync(); await c.StartMonitoringAsync(); reads.ReadReplies[0x010C] = [1];
        Check((await c.RefreshAsync(DailyRefreshScope.Anc)).Warnings.Count != 0);
    }
});
await Test("persistent idle receive waits, publishes pushed partial battery and placement, never sends ACK", async () =>
{
    var f = new Fake(); await using var c = new DailyClient(f); DailySnapshot initial = await c.RefreshAsync();
    await c.StartMonitoringAsync(); int requests = f.Requests.Count;
    var seen = new TaskCompletionSource<DailySnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
    c.SnapshotChanged += s => { if(s.CaseOpen == true && s.Battery?.Left?.Percent == 42)seen.TrySetResult(s); };
    f.Push(0x0204,[1,1,1,42]); f.Push(0x8202,[0,2,3,1,3,2,2,3,4]);
    DailySnapshot next = await seen.Task.WaitAsync(TimeSpan.FromSeconds(2));
    Check(next.Revision > initial.Revision && next.LeftInCase == false && next.LeftInEar == true
        && next.RightInCase == true && next.RightInEar == true && next.Battery?.Right?.Percent == 80
        && next.RightBatteryObservedAtUtc == initial.RightBatteryObservedAtUtc
        && next.CaseBatteryObservedAtUtc == initial.CaseBatteryObservedAtUtc
        && next.LeftBatteryObservedAtUtc >= initial.LeftBatteryObservedAtUtc);
    await Task.Delay(30); Check(f.Requests.Count == requests && f.MaximumReaders == 1);
    next.Battery!.RawComponents[1] = 99;
    var ancRefresh = await c.RefreshAsync(DailyRefreshScope.Anc);
    Check(ancRefresh.Battery?.Left?.Raw == 42 && ancRefresh.CaseObservedAtUtc == next.CaseObservedAtUtc
        && ancRefresh.LeftPlacementObservedAtUtc == next.LeftPlacementObservedAtUtc
        && ancRefresh.LeftBatteryObservedAtUtc == next.LeftBatteryObservedAtUtc);
});
await Test("notification received after query starts wins over delayed stale query result", async () =>
{
    var f = new Fake(); await using var c = new DailyClient(f); await c.RefreshAsync();
    f.NotificationBeforeNextBatteryReply = new(0x0204,240,[1,1,1,43]);
    DailySnapshot latest = await c.RefreshAsync(DailyRefreshScope.Battery);
    Check(latest.Battery?.Left?.Percent == 43 && latest.Battery.Right?.Percent == 80);
});
await Test("SET ACK and later notification in the same read are both retained without treating push as ACK", async () =>
{
    var f = new Fake { EmptySetAck = true, NotificationWithNextSetReply = new(0x0504,240,[7]) };
    await using var c = new DailyClient(f); var seen = new TaskCompletionSource<DailySnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
    c.SnapshotChanged += s => { if(s.PresetId == 7) seen.TrySetResult(s); };
    var result = await c.ApplyPresetAsync(7);
    await seen.Task.WaitAsync(TimeSpan.FromSeconds(2));
    Check(!result.Success && result.AckAccepted is null && result.ReadbackVerified && f.Writes.Count == 1);
});
await Test("0503 feature and 0506 full EQ state push preserve independent settings", async () =>
{
    var f = new Fake(); await using var c = new DailyClient(f); await c.RefreshAsync();
    var seen = new TaskCompletionSource<DailySnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
    c.SnapshotChanged += s => { if(s.DualConnection == true && s.CustomEqualizers.Any(x=>x.Name=="PushEQ")) seen.TrySetResult(s); };
    f.Push(0x0503,[1,0x11,1]); var eq = Fake.Entry(); eq.Name="PushEQ";
    f.Push(0x0506,Fake.EncodeEntries([eq]).AsSpan(1).ToArray());
    DailySnapshot next = await seen.Task.WaitAsync(TimeSpan.FromSeconds(2));
    Check(next.HiRes == true && next.PresetId == 0 && next.CustomEqualizersRead && f.Writes.Count == 0);
});
await Test("burst spatial invalidations coalesce into one scoped read after current operation", async () =>
{
    var f = new Fake(); await using var c = new DailyClient(f); await c.RefreshAsync(); await c.StartMonitoringAsync();
    f.PauseNextRequest = 0x0106; Task<DailySnapshot> read = c.RefreshAsync(DailyRefreshScope.Battery);
    await f.RequestPaused.Task.WaitAsync(TimeSpan.FromSeconds(2));
    int featureReads = f.Requests.Count(x=>x.Command==0x010D);
    var marker = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var refreshed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    c.SnapshotChanged += s => { if(s.CaseOpen == true)marker.TrySetResult();if(s.SpatialMode == 2)refreshed.TrySetResult(); };
    for(int n=0;n<20;n++)f.Push(0x051D,[]);
    f.Push(0x0204,[2,1,3,4]); await marker.Task.WaitAsync(TimeSpan.FromSeconds(2));
    f.Spatial=2; f.ReleaseRequest.TrySetResult(); await read;
    await refreshed.Task.WaitAsync(TimeSpan.FromSeconds(2));
    Check(f.Requests.Count(x=>x.Command==0x010D)==featureReads+1 && f.Writes.Count==0);
});
await Test("unknown 0503 values never authorize features or guess a legacy fallback", async () =>
{
    var f = new Fake(); await using var c = new DailyClient(f); await c.RefreshAsync();
    var seen = new TaskCompletionSource<DailySnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
    c.SnapshotChanged += s => { if(s.RawFeatures.GetValueOrDefault((byte)0x28)==7)seen.TrySetResult(s); };
    f.Push(0x0503,[3,0x28,7,0x06,1,0xFE,1]);
    DailySnapshot next = await seen.Task.WaitAsync(TimeSpan.FromSeconds(2));
    Check(next.GameMode is null && !next.Capabilities.GameMode && next.Capabilities.GameFeatureId==0x28
        && next.RawFeatures[0xFE]==1 && next.Capabilities.HiRes && f.Writes.Count==0);
});
await Test("legacy spatial pushes resolve from feature state and unknown values disable its capability", async () =>
{
    var f = new Fake { NoModernSpatial=true }; await using var c = new DailyClient(f); await c.RefreshAsync();
    var valid = new TaskCompletionSource<DailySnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
    var unknown = new TaskCompletionSource<DailySnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
    c.SnapshotChanged += s => {
        if(s.RawFeatures.GetValueOrDefault((byte)0x1B)==1)valid.TrySetResult(s);
        if(s.RawFeatures.GetValueOrDefault((byte)0x1B)==7)unknown.TrySetResult(s);
    };
    f.Push(0x0503,[1,0x1B,1]); var next=await valid.Task.WaitAsync(TimeSpan.FromSeconds(2));
    Check(next.SpatialMode==1 && next.Capabilities.SpatialMode && next.Capabilities.SpatialRoute=="legacy");
    f.Push(0x0503,[1,0x1B,7]); next=await unknown.Task.WaitAsync(TimeSpan.FromSeconds(2));
    Check(next.SpatialMode is null && !next.Capabilities.SpatialMode && f.Writes.Count==0);
});
await Test("monitoring re-registers after safe sequence exhaustion without sequence reuse", async () =>
{
    var f = new Fake(); await using var c = new DailyClient(f); await c.RefreshAsync(); await c.StartMonitoringAsync();
    for(int n=0;n<245;n++)await c.RefreshAsync(DailyRefreshScope.Battery);
    Check(f.Opens >= 2 && f.Requests.Count(x=>x.Command==0x0200)==f.Opens
        && f.SequenceSets.All(x=>x.Value.Distinct().Count()==x.Value.Count));
});
await Test("disposing idle persistent reader cancels the pending read", async () =>
{
    var f = new Fake(); var c = new DailyClient(f); await c.RefreshAsync();
    await c.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
    Check(f.ActiveReaders==0);
});
await DailyFeatureTests.Run(Test, (value, message) => Check(value, message));
await AudioFeatureCompatibilityTests.Run(Test, (value, message) => Check(value, message));
await ResourceContextTests.RunAsync(Test);
await AncModeTests.RunAsync(Test);
await CalibrationTransportTests.RunAsync(Test);
await FirmwareCompatibilityTests.RunAsync(Test);
await CodecCompatibilityRecoveryTests.RunAsync(Test);
await CodecRecoveryPublicationTests.RunAsync(Test);
await ConnectionDiagnosticTests.RunAsync(Test);
await Air4ProProfileTests.Run(Test, (value, message) => Check(value, message));
Console.WriteLine($"Daily tests: {passed} passed; no native devices accessed.");

sealed class Fake : IControlConnector
{
    public List<Packet> Requests = [];
    public Dictionary<ushort, byte[]> ReadReplies = [];
    public Dictionary<byte, byte[]> RegistrationReplies = [];
    public bool ThrowReadsAfterWrite;
    public bool SetReadEof, TruncatedSetFrame;
    public Exception? SetReadFailure;
    public static readonly ushort[] Frequencies = [31,62,125,250,500,1000,2000,4000,8000,16000];
    public Dictionary<byte,byte> Features = new() { [0x11]=0,[0x06]=0,[0x28]=0,[0x18]=1,[0x1B]=0 };
    public BassEngineRange Bass = new(-6, 6, 0);
    public byte PromptVolume = 6, GameSoundType;
    public byte? GameSoundFeatureAfterTypeSet;
    public string ProductId = "06EC10";
    public byte[] GameSoundTypes = [0,3];
    public List<GestureAssignment> Gestures = [new(1,0,2,6), new(2,0,2,1)];
    public byte[]? CapabilityOverride;
    public List<CustomEqualizer> Entries = [Entry()];
    public List<Packet> Writes = [];
    public int Opens, MaximumConcurrentSend;
    private int _activeSend;
    public Dictionary<int,List<byte>> SequenceSets = [];
    private Transport? _currentTransport;
    public void Push(ushort command, byte[] payload, byte sequence = 240) => _currentTransport!.Push(command, payload, sequence);
    public Packet? NotificationBeforeNextBatteryReply;
    public Packet? NotificationWithNextSetReply;
    public bool LegacyRegistration;
    public ushort PauseNextRequest;
    public TaskCompletionSource RequestPaused = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource ReleaseRequest = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public int ActiveReaders, MaximumReaders;
    public byte AncLevel = 3, Preset, Spatial, SetterStatus;
    public byte? AncAfterSet;
    public byte[]? AncResponseAfterSet;
    public bool ReportModern = true, SendOldSequenceFirst, EmptySetAck, FailSetSend, HideSpatialAfterHiResOff, UnsupportedProduct, NoModernSpatial;
    public bool SelectCustomOnWrite = true;
    public byte? Color = 2;
    public HashSet<ushort> Silent = [];
    public static CustomEqualizer Entry() => new() { Id=40,Name="EQ1",MinimumGain=-6,MaximumGain=6,Bands=Frequencies.Select(x=>new EqualizerBand(x,0)).ToList() };
    public static EqualizerEdit Edit() => new() { Id=40,Name="EQ1",MinimumGain=-6,MaximumGain=6,Bands=Frequencies.Select(x=>new EqualizerBand(x,0)).ToList() };
    public static CustomEqualizer FromEdit(EqualizerEdit edit) => new() { Id=edit.Id,Name=edit.Name,MinimumGain=edit.MinimumGain,MaximumGain=edit.MaximumGain,Bands=edit.Bands.ToList() };
    // Independent wire fixtures: status, kind, bitmap type, mask. Never use the production ANC encoder here.
    public static byte[] AncReply(byte index) => Convert.FromHexString(index switch
    {
        0 => "00010101", 1 => "00010102", 2 => "00010104", 3 => "00010108",
        4 => "00010110", 5 => "00010120", 6 => "00010140", 7 => "00010180",
        8 => "0001010001", 11 => "0001010008", 12 => "0001010010",
        13 => "0001010020", 14 => "0001010040", 17 => "000101000002", 18 => "000101000004",
        _ => throw new InvalidOperationException("No independent ANC fixture for this index.")
    });
    public Task<IControlTransport> OpenAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); int id=++Opens; SequenceSets[id]=[];
        _currentTransport = new Transport(this,id);
        return Task.FromResult<IControlTransport>(_currentTransport);
    }
    public static byte[] EncodeEntries(List<CustomEqualizer> entries)
    {
        var bytes = new List<byte> {0,(byte)entries.Count};
        foreach(var eq in entries)
        {
            var name=Encoding.UTF8.GetBytes(eq.Name); bytes.AddRange([(byte)(eq.Selected?1:0),unchecked((byte)eq.MinimumGain),
                unchecked((byte)eq.MaximumGain),eq.Id,(byte)name.Length]); bytes.AddRange(name);bytes.Add((byte)eq.Bands.Count);
            foreach(var b in eq.Bands) bytes.AddRange([(byte)b.Frequency,(byte)(b.Frequency>>8),unchecked((byte)b.Gain)]);
        }
        return bytes.ToArray();
    }
    private byte[] Response(Packet p)
    {
        Requests.Add(p);
        if(p.Command>=0x0400)
        {
            Writes.Add(p);
            if(FailSetSend) throw new IOException();
            switch(p.Command)
            {
                case 0x0403: Features[p.Payload[0]]=p.Payload[1];break;
                case 0x0422: Spatial=p.Payload[0];break;
                case 0x041B: Bass = new(unchecked((sbyte)p.Payload[2]),unchecked((sbyte)p.Payload[1]),unchecked((sbyte)p.Payload[0]));break;
                case 0x0427: PromptVolume = p.Payload[0];break;
                case 0x0423:
                    if (p.Payload.Length != 2 || p.Payload[1] != 1) throw new Exception("Type assignment requires its enable byte");
                    GameSoundType = p.Payload[0];
                    if (GameSoundFeatureAfterTypeSet is { } feature) Features[0x27] = feature;
                    break;
                case 0x0401:
                    var gesture = new GestureAssignment(p.Payload[1],p.Payload[2],p.Payload[3],p.Payload[4]);
                    Gestures.RemoveAll(x=>x.Device==gesture.Device&&x.Button==gesture.Button&&x.Action==gesture.Action);Gestures.Add(gesture);break;
                case 0x0406: Preset=p.Payload[0];foreach(var e in Entries)e.Selected=e.Id==Preset;break;
                case 0x0404:
                    AncLevel=AncAfterSet ?? (byte)Enumerable.Range(0,(p.Payload.Length-2)*8).Single(n=>(p.Payload[2+n/8]&(1<<(n%8)))!=0);break;
                case 0x0418:
                    var encoded=p.Payload.ToArray(); if(encoded[3]==0)encoded[3]=51;
                    var parsed=DailyParsers.ParseEqualizers([0,1,0,..encoded.AsSpan(1).ToArray()]).Single();
                    foreach(var e in Entries)e.Selected=false;parsed.Selected=SelectCustomOnWrite;
                    Entries.RemoveAll(x=>x.Id==parsed.Id);Entries.Add(parsed);break;
            }
            return EmptySetAck?[]:[SetterStatus];
        }
        if (ThrowReadsAfterWrite && Writes.Count != 0) throw new IOException();
        if (p.Command == 0x010C && Writes.Count != 0 && AncResponseAfterSet is { } ancReply) return ancReply;
        if (ReadReplies.TryGetValue(p.Command, out var overridden)) return overridden;
        if (p.Command == 0x0201 && RegistrationReplies.TryGetValue(p.Payload[0], out var registration)) return registration;
        return p.Command switch
        {
            0x0103 => UnsupportedProduct?[0,1,2,3]:[0,..Convert.FromHexString(ProductId).Reverse()],
            0x010B => Color is { } color ? [0,color] : [1],
            0x0100 => [0,..Capabilities()],
            0x0105 => [0,3,..Encoding.UTF8.GetBytes("1,2,116,2,2,116,3,2,100")],
            0x0114 => [0,8],
            0x010D => FeatureReply(p.Payload),
            0x0106 => [0,3,1,75,2,80,3,90],
            0x0107 => [0,2,1,3,2,0], // OTA support, never placement.
            0x0109 => [0,3,1,0,2,0,3,0],
            0x0200 => [0,8,1,2,3,5,6,15,240,241],
            0x0205 => [0,p.Payload[0],..p.Payload.Skip(1).SelectMany(id => new byte[] {id,0})],
            0x0201 => [0],
            0x010C => AncReply(AncLevel),
            0x010F => [0,Preset],
            0x0122 => p.Payload.Length==0 ? EncodeEntries(Entries) : throw new Exception("GET 0122 requires empty official payload"),
            0x012A => [0,HideSpatialAfterHiResOff&&Features[0x18]==0?(byte)9:Spatial],
            0x0124 => [0,unchecked((byte)(sbyte)Bass.Minimum),unchecked((byte)(sbyte)Bass.Maximum),unchecked((byte)(sbyte)Bass.Current)],
            0x0130 => [0,PromptVolume],
            0x012B => [0,GameSoundType,(byte)GameSoundTypes.Length,..GameSoundTypes],
            0x0108 => p.Payload.SequenceEqual(new byte[]{3,2,3,1}) ? [0,(byte)Gestures.Count,..Gestures.SelectMany(x=>new byte[]{x.Device,x.Button,x.Action,x.Function})] : throw new Exception("incorrect gesture query"),
            _ => throw new Exception("unexpected opcode")
        };
    }
    private byte[] FeatureReply(byte[] request)
    {
        var entries=request.Skip(1).Where(x=>Features.ContainsKey(x)&&(ReportModern||x!=0x28)).ToArray();
        return [0,(byte)entries.Length,..entries.SelectMany(x=>new byte[]{x,Features[x]})];
    }
    private byte[] Capabilities()
    {
        if (CapabilityOverride is not null) return CapabilityOverride;
        var bitmap=Convert.FromHexString("FF775AEEEE8FA65F1700000000000000");
        if(NoModernSpatial)bitmap[5]&=0x7F;
        if(LegacyRegistration)bitmap[2]&=0xBF;return bitmap;
    }
    private sealed class Transport(Fake owner,int id):IControlTransport
    {
        private readonly Channel<byte[]> _received=Channel.CreateUnbounded<byte[]>();
        public void Push(ushort command, byte[] payload, byte sequence = 240) => _received.Writer.TryWrite(PacketEncoder.Encode(command, sequence, payload));
        public async Task SendAsync(ReadOnlyMemory<byte> bytes,CancellationToken token)
        {
            token.ThrowIfCancellationRequested();int concurrent=Interlocked.Increment(ref owner._activeSend);
            owner.MaximumConcurrentSend=Math.Max(owner.MaximumConcurrentSend,concurrent);
            try
            {
                await Task.Yield();var p=new PacketDecoder().Feed(bytes.Span).Single();
                owner.SequenceSets[id].Add(p.Sequence);var response=owner.Response(p);
                if(owner.Silent.Contains(p.Command))return;
                if(owner.PauseNextRequest==p.Command)
                {owner.PauseNextRequest=0;owner.RequestPaused.TrySetResult();await owner.ReleaseRequest.Task.WaitAsync(token);}
                if (p.Command >= 0x0400)
                {
                    if (owner.SetReadFailure is { } failure) { _received.Writer.TryComplete(failure); return; }
                    if (owner.SetReadEof)
                    { if (owner.TruncatedSetFrame) _received.Writer.TryWrite([0xAA]); _received.Writer.TryComplete(); return; }
                }
                if(owner.SendOldSequenceFirst)_received.Writer.TryWrite(PacketEncoder.Encode((ushort)(p.Command|0x8000),unchecked((byte)(p.Sequence-1)),[0]));
                byte[] encoded = PacketEncoder.Encode((ushort)(p.Command|0x8000),p.Sequence,response);
                if(p.Command==0x0106 && owner.NotificationBeforeNextBatteryReply is { } notification)
                {
                    owner.NotificationBeforeNextBatteryReply=null;
                    encoded=[..PacketEncoder.Encode(notification.Command,notification.Sequence,notification.Payload),..encoded];
                }
                if(p.Command>=0x0400 && owner.NotificationWithNextSetReply is { } following)
                {
                    owner.NotificationWithNextSetReply=null;
                    encoded=[..encoded,..PacketEncoder.Encode(following.Command,following.Sequence,following.Payload)];
                }
                _received.Writer.TryWrite(encoded);
            }
            finally{Interlocked.Decrement(ref owner._activeSend);}
        }
        public async Task<int> ReadAsync(Memory<byte> buffer,CancellationToken token)
        {
            int readers=Interlocked.Increment(ref owner.ActiveReaders);owner.MaximumReaders=Math.Max(owner.MaximumReaders,readers);
            try
            {
                if (!await _received.Reader.WaitToReadAsync(token)) return 0;
                var data=await _received.Reader.ReadAsync(token);data.CopyTo(buffer);return data.Length;
            }
            finally{Interlocked.Decrement(ref owner.ActiveReaders);}
        }
        public ValueTask DisposeAsync(){_received.Writer.TryComplete();return ValueTask.CompletedTask;}
    }
}
