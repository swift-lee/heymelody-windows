using HeyMelodyWindows.Devices;
using HeyMelodyWindows.Protocol;

using HeyMelody.Diagnostics;

namespace HeyMelodyWindows.Control;

public static class HiResWorkflow
{
    public static Task<HiResWorkflowResult> EnableAsync(IControlConnector connector, CancellationToken token)
        => EnableAsync(connector, new HiResWorkflowOptions(), token);

    public static async Task<HiResWorkflowResult> EnableAsync(IControlConnector connector, HiResWorkflowOptions options, CancellationToken token)
    {
        using var operation = DiagnosticLog.BeginOperation(DiagnosticLog.CurrentCorrelationId);
        DiagnosticLog.Info("Control", "hires.started", "The explicitly requested Hi-Res workflow started.", options);
        ArgumentNullException.ThrowIfNull(connector);
        ArgumentNullException.ThrowIfNull(options);
        if (options.RequestTimeout <= TimeSpan.Zero || options.VerificationBudget <= TimeSpan.Zero
            || options.RetryInterval < TimeSpan.Zero || options.InitialObservationDelay < TimeSpan.Zero || options.StableObservationDelay < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(options));
        var result = new HiResWorkflowResult();
        int connectionNumber = 0;
        try
        {
            result.Phase = "baseline";
            await using (var session = new ControlSession(await connector.OpenAsync(token), ++connectionNumber, options, result))
            {
                result.Before = await ReadSnapshotAsync(session, result, true, token);
                var before = result.Before;
                if (before.HiRes is null) throw new InvalidDataException("Baseline HiRes state is unknown.");
                if (before.SpaceMode is null) throw new InvalidDataException("Baseline spatial state is unknown; no setter is permitted.");
                if (before.HiRes == true)
                {
                    if (before.SpatialHiResMutex == true && before.SpaceMode != 0)
                        throw new InvalidDataException("HiRes is already on but the mutually exclusive spatial state is active.");
                }
                else
                {
                    if (before.SpatialHiResMutex == true && before.SpaceMode > 0)
                    {
                        result.Phase = "closing-space";
                        ushort command = before.SpaceRoute == "new" ? (ushort)0x0422 : (ushort)0x0403;
                        byte[] payload = before.SpaceRoute == "new" ? [0] : [0x1B, 0];
                        var close = await session.RequestAsync(command, payload, token);
                        if (!close.Action.Success) throw new InvalidDataException("Spatial close ACK failed; HiRes was not submitted. Spatial close may have taken effect.");
                        if (await ReadSpaceAsync(session, before.SpaceRoute!, token) != 0)
                            throw new InvalidDataException("Spatial close readback failed; HiRes was not submitted. Spatial close may have taken effect.");
                    }
                    token.ThrowIfCancellationRequested();
                    result.Phase = "enabling-hires";
                    var set = await session.RequestAsync(0x0403, [0x18, 1], token);
                    result.HiResAckAccepted = set.Action.ResponseMatched && set.Action.Status.HasValue ? set.Action.Status == 0 : null;
                    if (!set.Action.Success) result.Warnings.Add("HiRes SET ACK was rejected or unavailable; only independent GET verification follows. No SET is replayed.");
                }
            }

            result.Phase = "fresh-session-verification";
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(token);
            budget.CancelAfter(options.VerificationBudget);
            await Task.Delay(options.InitialObservationDelay, budget.Token);
            while (true)
            {
                budget.Token.ThrowIfCancellationRequested();
                try
                {
                    await using var fresh = new ControlSession(await connector.OpenAsync(budget.Token).WaitAsync(budget.Token), ++connectionNumber, options, result);
                    var after = await ReadSnapshotAsync(fresh, result, true, budget.Token);
                    result.After = after;
                    result.FreshSessionVerified = true;
                    // Observe again on the fresh session: an immediate successful reopen is not evidence of an ACL reconnect.
                    await Task.Delay(options.StableObservationDelay, budget.Token);
                    after = await ReadSnapshotAsync(fresh, result, true, budget.Token);
                    result.After = after;
                    result.FinalHiResVerified = after.HiRes == true;
                    if (after.HiRes == true && (after.SpatialHiResMutex != true || after.SpaceMode == 0))
                    {
                        result.Success = result.HiResAckAccepted != false;
                        result.Phase = !result.Success ? "hires-verified-ack-rejected"
                            : result.HiResSetSubmitted && result.HiResAckAccepted is null ? "complete-ack-unknown" : "complete";
                        if (!result.Success) result.Error = "HiRes readback is on, but the submitted SET did not have an accepted ACK.";
                        return result;
                    }
                }
                catch (InvalidDataException) { throw; }
                catch (OperationCanceledException) when (budget.IsCancellationRequested) { throw; }
                catch (Exception error)
                {
                    DiagnosticLog.Warning("Control", "hires.verification-read-failed", "Fresh GET verification was unavailable.", error, new { connectionNumber, result.Phase });
                    result.Warnings.Add("Fresh control session or GET verification was unavailable; retrying GETs on a new session only.");
                }
                await Task.Delay(options.RetryInterval, budget.Token);
            }
        }
        catch (OperationCanceledException error)
        {
            DiagnosticLog.Warning("Control", "hires.cancelled", "The Hi-Res workflow was cancelled or reached its verification deadline.", error, new { connectionNumber, result.Phase });
            result.Error = token.IsCancellationRequested ? "Workflow cancelled; submitted settings may have taken effect." : "Fresh-session verification budget expired; submitted settings may have taken effect.";
        }
        catch (InvalidDataException exception) { DiagnosticLog.Error("Control", "hires.invalid-response", "The Hi-Res workflow received invalid state.", exception, new { connectionNumber, result.Phase }); result.Error = exception.Message; }
        catch (Exception error) { DiagnosticLog.Error("Control", "hires.failed", "The Hi-Res workflow failed with possibly submitted settings.", error, new { connectionNumber, result.Phase }); result.Error = "Control workflow failed; submitted settings may have taken effect."; }
        result.Success = false;
        return result;
    }

    private static async Task<Snapshot> ReadSnapshotAsync(ControlSession session, HiResWorkflowResult result, bool includeMetadata, CancellationToken token)
    {
        var snapshot = new Snapshot { ControlConnectionNumber = session.Number, ObservedAtUtc = DateTimeOffset.UtcNow };
        var identity = await GetAsync(session, 0x0103, [], token);
        var parsedId = QueryParsers.Parse(0x0103, identity);
        if (!parsedId.Success || !parsedId.Data.TryGetValue("productId", out var id) || id is not string productId || productId != "06EC10")
            throw new InvalidDataException("Fresh product identity is not exactly 06EC10; control is stopped.");
        snapshot.ProductId = productId;
        var profile = DeviceModelResolver.FindByProductId(productId) ?? throw new InvalidDataException("Missing exact X4 profile.");
        if (profile.Provenance.Sha256 != X4Profile.AssetSha256 || profile.ProductId != productId || profile.HighToneQuality != 1
            || profile.SpatialHiResMutex is not (0 or 1)) throw new InvalidDataException("X4 profile provenance or feature declarations failed verification.");
        snapshot.ProfileVerified = true;
        snapshot.ProfileSha256 = profile.Provenance.Sha256;
        snapshot.SpatialHiResMutex = profile.SpatialHiResMutex == 1;
        var caps = await GetAsync(session, 0x0100, [], token);
        if (caps.Length < 10 || caps.Length > 513) throw new InvalidDataException("Capability response does not contain the complete 69-bit map.");
        session.Capabilities = CommandCapabilities.Parse(caps.AsSpan(1));
        if (!session.Capabilities.Supports(0x0403)) throw new InvalidDataException("Capabilities do not support feature SET 0403; no HiRes SET is permitted.");
        snapshot.KnownCapabilityBitCount = CommandCapabilities.KnownBitCount;
        snapshot.CapabilityBitmapHex = Convert.ToHexString(caps.AsSpan(1));
        snapshot.SupportedCommands = session.Capabilities.SupportedCommands.Select(c => $"0x{c:X4}").ToList();
        snapshot.UnknownCapabilitySetBits = session.Capabilities.UnknownSetBits.ToList();
        snapshot.SpaceRoute = session.Capabilities.Supports(0x012A) && session.Capabilities.Supports(0x0422) ? "new" : "legacy";
        snapshot.HiResRaw = await ReadFeatureAsync(session, 0x18, token);
        snapshot.HiRes = snapshot.HiResRaw switch { 0 => false, 1 => true, _ => null };
        snapshot.SpaceMode = await ReadSpaceAsync(session, snapshot.SpaceRoute, token);
        if (includeMetadata)
        {
            if (session.Capabilities.Supports(0x0105))
            {
                var firmware = QueryParsers.Parse(0x0105, await GetAsync(session, 0x0105, [], token));
                if (!firmware.Success) throw new InvalidDataException("Invalid firmware query layout.");
                snapshot.FirmwareEntries = (List<FirmwareEntry>)firmware.Data["firmwareEntries"]!;
            }
            if (session.Capabilities.Supports(0x0114))
            {
                var codec = QueryParsers.Parse(0x0114, await GetAsync(session, 0x0114, [], token));
                if (!codec.Success) throw new InvalidDataException("Invalid codec query layout.");
                snapshot.CodecRaw = (byte)codec.Data["codecRaw"]!;
            }
        }
        return snapshot;
    }

    private static async Task<byte[]> GetAsync(ControlSession session, ushort command, byte[] payload, CancellationToken token)
    {
        var response = await session.RequestAsync(command, payload, token);
        if (!response.Action.Success || response.Payload is null)
            throw new IOException("GET response was rejected or unavailable.");
        return response.Payload;
    }

    private static async Task<byte?> ReadFeatureAsync(ControlSession session, byte feature, CancellationToken token)
    {
        var bytes = await GetAsync(session, 0x010D, [1, feature], token);
        var parsed = QueryParsers.Parse(0x010D, bytes);
        if (!parsed.Success) throw new InvalidDataException("Invalid feature query layout.");
        var states = (Dictionary<string, byte>)parsed.Data["featureValues"]!;
        return states.TryGetValue(feature.ToString("X2"), out byte state) ? state : null;
    }

    private static async Task<int?> ReadSpaceAsync(ControlSession session, string route, CancellationToken token)
    {
        if (route == "legacy")
        {
            byte? state = await ReadFeatureAsync(session, 0x1B, token);
            return state is 0 or 1 ? state : null;
        }
        // APK commands/m.smali pswitch_a: successful status at byte 0, headset spatial type at byte 1.
        byte[] bytes = await GetAsync(session, 0x012A, [], token);
        return bytes.Length >= 2 && bytes[1] <= 2 ? bytes[1] : null;
    }
}
