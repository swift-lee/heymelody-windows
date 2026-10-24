using HeyMelodyWindows.Protocol;
using HeyMelody.Diagnostics;

namespace HeyMelodyWindows.Control;

internal sealed class ControlSession(IControlTransport transport, int number, HiResWorkflowOptions options,
    HiResWorkflowResult result) : IAsyncDisposable
{
    private readonly SequenceAllocator _sequences = new();
    private readonly PacketDecoder _decoder = new();
    private bool _usable = true;
    public int Number => number;
    public CommandCapabilities? Capabilities { get; set; }

    public async Task<(ControlAction Action, byte[]? Payload)> RequestAsync(ushort command, byte[] payload, CancellationToken token)
    {
        using var operation = DiagnosticLog.BeginOperation(DiagnosticLog.CurrentCorrelationId);
        if (!ControlPolicy.IsAllowed(command, payload)) throw new InvalidOperationException("Outside enable-only control policy.");
        var action = new ControlAction { CommandHex = $"0x{command:X4}", PayloadHex = Convert.ToHexString(payload), ControlConnectionNumber = number };
        result.Actions.Add(action);
        if (!_usable || !_sequences.TryTake(out byte sequence))
        {
            action.Disposition = "session-unavailable";
            return (action, null);
        }
        action.Sequence = sequence;
        var key = new RequestKey(command, sequence);
        byte[] frame = PacketEncoder.Encode(command, sequence, payload);
        var context = new { connectionNumber = number, command = $"0x{command:X4}", sequence };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(options.RequestTimeout);
        try
        {
            // A send can fail after partial transmission. Record submission before entering the transport.
            action.Submitted = true;
            if (command is 0x0403 or 0x0422)
            {
                result.SettingsAttempted++;
                if (command == 0x0403 && payload[0] == 0x18) result.HiResSetSubmitted = true;
            }
            DiagnosticLog.Bytes("Control", "request.tx", frame, context);
            await transport.SendAsync(frame, timeout.Token).WaitAsync(timeout.Token);
            DiagnosticLog.Debug("Control", "request.sent", "The control transport completed the write.", context);
            var buffer = new byte[4096];
            while (true)
            {
                int count = await transport.ReadAsync(buffer, timeout.Token).WaitAsync(timeout.Token);
                if (count == 0) { _decoder.Complete(); action.Disposition = "disconnected"; break; }
                if (count < 0 || count > buffer.Length) throw new FormatException();
                DiagnosticLog.Bytes("Control", "transport.rx", buffer.AsSpan(0, count), context);
                Packet? matched = _decoder.Feed(buffer.AsSpan(0, count)).FirstOrDefault(key.MatchesResponse);
                if (matched is null) continue;
                action.ResponseMatched = true;
                action.Status = matched.Payload.Length > 0 ? matched.Payload[0] : null;
                action.Success = action.Status == 0;
                action.Disposition = action.Status is null ? "missing-status" : action.Success ? "response" : "device-rejected";
                DiagnosticLog.Info("Control", "request.response", "The control request received its response.",
                    new { connectionNumber = number, command = $"0x{command:X4}", sequence, action.Status, action.Disposition, payloadLength = matched.Payload.Length });
                if (action.Status is null) _usable = false;
                return (action, matched.Payload);
            }
        }
        catch (OperationCanceledException error)
        {
            action.Disposition = token.IsCancellationRequested ? "cancelled" : "timeout";
            DiagnosticLog.Warning("Control", "request.cancelled", action.Disposition, error, context);
        }
        catch (FormatException error) { action.Disposition = "malformed-frame"; DiagnosticLog.Error("Control", "request.malformed", "Control response decoding failed.", error, context); }
        catch (Exception error) { action.Disposition = "transport-failure"; DiagnosticLog.Error("Control", "request.failed", "Control transport failed.", error, context); }
        _usable = false;
        _decoder.Reset();
        action.Error = "Control response unavailable; this submission must not be replayed.";
        return (action, null);
    }

    public async ValueTask DisposeAsync()
    {
        _usable = false;
        try { await transport.DisposeAsync(); }
        catch (Exception error) { result.Warnings.Add("Control transport disposal failed."); DiagnosticLog.Error("Control", "session.dispose-failed", "Control transport disposal failed.", error, new { connectionNumber = number }); }
    }
}
