using System.ComponentModel;
using HeyMelody.Diagnostics;
using HeyMelodyWindows.Control;
using HeyMelodyWindows.Protocol;

namespace HeyMelodyWindows.Daily;

internal enum DailyReadFailureKind { None, ProtocolFormat, Timeout, Cancelled, TransportInterrupted, Other }
internal sealed record DailyResponse(bool Submitted, bool Matched, byte? Status, byte[]? Payload,
    bool ConnectionInterrupted = false, byte? Sequence = null, string? Failure = null,
    DailyReadFailureKind FailureKind = DailyReadFailureKind.None, ushort? FailureCommand = null,
    byte? FailureSequence = null, bool CodecReplyCompatibilityUsed = false, bool FailureRequestPending = false);
internal sealed class DailySession : IAsyncDisposable
{
    private readonly IControlTransport _transport;
    private readonly SequenceAllocator _sequences = new();
    private readonly PacketDecoder _decoder;
    private readonly string _diagnosticId = Guid.NewGuid().ToString("N");
    private string? _pendingCorrelationId;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _pendingGate = new();
    private readonly Dictionary<ushort, byte> _filterSequences = [];
    private Task _reader = Task.CompletedTask;
    private RequestKey? _key;
    private TaskCompletionSource<Packet>? _reply;
    private Exception? _failure;
    private DailyReadFailureKind _failureKind;
    private bool _failureRequestPending, _replyDecoded;
    private bool _codecReadRecoveryPending;
    internal bool CodecReadRecoveryPending => Volatile.Read(ref _codecReadRecoveryPending);
    private bool _allowCodecReplyCompatibility, _codecReplyCompatibilityUsed;
    internal string? CodecQuerySuppressionWarning { get; private set; }
    private int _usable = 1, _disposed, _readerStarted;
    public event Action<Packet>? Notification;
    public event Action<Exception>? Ended;
    public bool Usable => Volatile.Read(ref _usable) != 0;
    public int Remaining => _sequences.Remaining;
    public DailySession(IControlTransport transport)
    {
        _transport = transport;
        _decoder = new PacketDecoder(AllowCodecReplyCompatibility, RecordDecodedReply);
        DiagnosticLog.Info("Daily", "session.created", "Created a daily control session.", DiagnosticContext());
    }
    internal void SetCodecReplyCompatibilityAllowed(bool allowed)
    {
        lock (_pendingGate) _allowCodecReplyCompatibility = allowed;
    }
    internal void SuppressCodecQuery(string warning) => CodecQuerySuppressionWarning = warning;
    private void RecordDecodedReply(Packet packet)
    {
        lock (_pendingGate)
            if (packet.Payload.Length > 0 && _key is RequestKey key && key.MatchesResponse(packet)) _replyDecoded = true;
    }
    private bool AllowCodecReplyCompatibility(byte sequence)
    {
        lock (_pendingGate)
        {
            if (!_allowCodecReplyCompatibility || _codecReplyCompatibilityUsed || _replyDecoded || !Usable || _reply is null
                || _reply.Task.IsCompleted || _key is not { Command: 0x0114 } key || key.Sequence != sequence) return false;
            _codecReplyCompatibilityUsed = true;
            return true;
        }
    }
    public void StartReader()
    {
        if (Interlocked.Exchange(ref _readerStarted, 1) != 0) return;
        // Wire consumers before starting so the first unsolicited packet cannot be lost.
        _reader = Task.Run(ReadPumpAsync);
    }
    /// <param name="optionalTimeout">Set only for reads whose silence means "unknown"; such a timeout keeps the session.</param>
    public async Task<DailyResponse> RequestAsync(ushort command, byte[] payload, CancellationToken token, TimeSpan? optionalTimeout = null)
    {
        using var operation = DiagnosticLog.BeginOperation(DiagnosticLog.CurrentCorrelationId);
        if (!Usable || !_sequences.TryTake(out byte seq)) throw new IOException("Daily session unavailable.");
        var completion = new TaskCompletionSource<Packet>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_pendingGate)
        {
            if (_reply is not null) throw new InvalidOperationException("Requests must be serialized.");
            if (!Usable) throw new IOException("Daily session unavailable.");
            _key = new(command, seq); _reply = completion;
            _pendingCorrelationId = DiagnosticLog.CurrentCorrelationId;
            _codecReplyCompatibilityUsed = false;
            _replyDecoded = false;
            // A hearing-filter request begins/retries the entire pair of filter loads.
            if (command == 0x0116) _filterSequences.Remove(0x011F);
            if (command is 0x0116 or 0x011F) _filterSequences[command] = seq;
        }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime.Token);
        timeout.CancelAfter(optionalTimeout ?? TimeSpan.FromSeconds(4));
        bool submitted = false, sent = false;
        try
        {
            submitted = true;
            byte[] frame = PacketEncoder.Encode(command, seq, payload);
            DiagnosticLog.Bytes("Daily", "request.tx", frame, DiagnosticContext());
            await _transport.SendAsync(frame, timeout.Token).WaitAsync(timeout.Token);
            sent = true;
            DiagnosticLog.Debug("Daily", "request.sent", "The transport completed the request write.", DiagnosticContext());
            Packet packet = await completion.Task.WaitAsync(timeout.Token);
            byte? status = packet.Payload.Length == 0 ? null : packet.Payload[0];
            DiagnosticLog.Info("Daily", "request.response", "Received the matched response.",
                new { sessionId = _diagnosticId, command = $"0x{command:X4}", sequence = seq, responseCommand = $"0x{packet.Command:X4}",
                    status, payloadLength = packet.Payload.Length, codecCompatibilityUsed = _codecReplyCompatibilityUsed });
            if (status is null) Stop(new FormatException("Missing response status."),
                DailyReadFailureKind.ProtocolFormat, matchedMissingStatus: true);
            return new(submitted, true, status, packet.Payload, Sequence: packet.Sequence,
                Failure: status is null ? "回执缺少状态字节" : null,
                FailureKind: status is null ? DailyReadFailureKind.ProtocolFormat : DailyReadFailureKind.None,
                FailureCommand: status is null ? packet.Command : null,
                FailureSequence: status is null ? packet.Sequence : null,
                CodecReplyCompatibilityUsed: _codecReplyCompatibilityUsed, FailureRequestPending: status is null);
        }
        catch (OperationCanceledException) when (optionalTimeout is not null && sent && Usable
            && !token.IsCancellationRequested && !_lifetime.IsCancellationRequested)
        {
            DiagnosticLog.Warning("Daily", "request.optional-timeout", "An optional query timed out and leaves its state unknown.", data: DiagnosticContext());
            // A late reply cannot match a later request key and is not a notification command.
            return new(submitted, false, null, null, Sequence: seq, Failure: "等待回执超时",
                FailureKind: DailyReadFailureKind.Timeout, FailureRequestPending: true);
        }
        catch (Exception failure)
        {
            Exception actual = _failure ?? failure;
            DiagnosticLog.Error("Daily", "request.failed", "The daily request failed.", actual, DiagnosticContext());
            var kind = _failure is not null ? _failureKind : failure is OperationCanceledException
                && !timeout.IsCancellationRequested ? DailyReadFailureKind.Other : ClassifyFailure(actual, token);
            Stop(actual, kind);
            return new(submitted, false, null, null, IsConnectionInterrupted(actual), Sequence: seq,
                Failure: DescribeFailure(actual), FailureKind: kind,
                FailureCommand: actual.Data["HeyMelody.Protocol.ResponseCommand"] is ushort responseCommand ? responseCommand : null,
                FailureSequence: actual.Data["HeyMelody.Protocol.ResponseSequence"] is byte responseSequence ? responseSequence : null,
                FailureRequestPending: _failureRequestPending);
        }
        finally
        {
            lock (_pendingGate)
            {
                if (ReferenceEquals(_reply, completion)) { _reply = null; _key = null; _pendingCorrelationId = null; }
            }
        }
    }
    private async Task ReadPumpAsync()
    {
        // A reader outlives individual RPC operations; raw chunks may contain several request/notification frames.
        using var readerOperation = DiagnosticLog.SuspendOperation();
        var buffer = new byte[4096];
        try
        {
            while (true)
            {
                int count = await _transport.ReadAsync(buffer, _lifetime.Token);
                if (count > 0 && count <= buffer.Length)
                    DiagnosticLog.Bytes("Daily", "transport.rx", buffer.AsSpan(0, count), DiagnosticContext());
                if (count == 0)
                {
                    DiagnosticLog.Warning("Daily", "transport.eof", "The daily transport reached EOF.", data: DiagnosticContext());
                    // EOF itself establishes a broken connection even if the last frame was partial.
                    try { _decoder.Complete(); }
                    catch (FormatException error) { DiagnosticLog.Error("Daily", "transport.incomplete-eof", "EOF interrupted a protocol message.", error, DiagnosticContext()); }
                    throw new EndOfStreamException("RFCOMM closed.");
                }
                if (count < 0 || count > buffer.Length) throw new FormatException("Invalid transport read size.");
                foreach (Packet packet in _decoder.Feed(buffer.AsSpan(0, count)))
                {
                    bool matched, currentFilter; string? correlation;
                    bool filter = packet.Command is 0x8116 or 0x811F;
                    lock (_pendingGate)
                    {
                        matched = _key is RequestKey key && key.MatchesResponse(packet);
                        correlation = matched ? _pendingCorrelationId : null;
                        if (matched) _reply!.TrySetResult(packet);
                        currentFilter = filter && _filterSequences.TryGetValue((ushort)(packet.Command & 0x7FFF), out byte sequence)
                            && sequence == packet.Sequence;
                    }
                    using var packetOperation = matched && correlation is not null
                        ? DiagnosticLog.BeginOperation(correlation) : DiagnosticLog.SuspendOperation();
                    DiagnosticLog.Bytes("Daily", matched ? "response.matched" : currentFilter ? "filter.received" : "packet.unmatched", packet.Payload,
                        new { sessionId = _diagnosticId, command = $"0x{packet.Command:X4}", packet.Sequence, matched, currentFilter });
                    if (currentFilter || !filter && !matched && NotificationParsers.IsNotificationCommand(packet.Command))
                        Notification?.Invoke(packet);
                }
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        { DiagnosticLog.Debug("Daily", "reader.cancelled", "The daily reader stopped with its session.", DiagnosticContext()); }
        catch (Exception failure)
        { DiagnosticLog.Error("Daily", "reader.failed", "The daily read pump failed.", failure, DiagnosticContext()); Stop(failure); }
    }
    private void Stop(Exception failure, DailyReadFailureKind? kind = null, bool matchedMissingStatus = false)
    {
        if (Interlocked.Exchange(ref _usable, 0) == 0) return;
        DiagnosticLog.Error("Daily", "session.stopping", "Stopping the daily session after a read/request failure.", failure, DiagnosticContext());
        TaskCompletionSource<Packet>? pendingReply;
        lock (_pendingGate)
        {
            _failureRequestPending = _reply is not null && !_replyDecoded
                && (!_reply.Task.IsCompleted || matchedMissingStatus);
            // The reader has no per-query deadline. Its unsolicited cancellation must not
            // be mistaken for a codec timeout and suppress a real connection failure.
            _failureKind = kind ?? (failure is OperationCanceledException ? DailyReadFailureKind.Other
                : ClassifyFailure(failure, CancellationToken.None));
            bool codecRequest = _allowCodecReplyCompatibility && _failureRequestPending && _key is { Command: 0x0114 };
            bool correlatedFormat = matchedMissingStatus || _key is { } key
                && failure.Data["HeyMelody.Protocol.ResponseCommand"] is ushort command && command == 0x8114
                && failure.Data["HeyMelody.Protocol.ResponseSequence"] is byte sequence && sequence == key.Sequence;
            Volatile.Write(ref _codecReadRecoveryPending, codecRequest && (_failureKind == DailyReadFailureKind.Timeout
                || _failureKind == DailyReadFailureKind.ProtocolFormat && correlatedFormat));
            _failure = failure; pendingReply = _reply;
        }
        // Notify the owner before waking the failed request: otherwise its retry can
        // detach this session before a genuine offline event reaches the Host.
        try { Ended?.Invoke(failure); }
        finally { pendingReply?.TrySetException(failure); _lifetime.Cancel(); }
    }
    private static bool IsConnectionInterrupted(Exception failure) => failure switch
    {
        // A bad frame or a deadline establishes an unknown response, not a broken
        // Bluetooth connection. The native RFCOMM adapter reports Winsock errors
        // as Win32Exception, so retain only errors that establish a failed channel.
        IOException when failure is not InvalidDataException => true,
        Win32Exception socket => socket.NativeErrorCode is 10050 or 10051 or 10052 or 10053 or 10054
            or 10057 or 10058 or 10064 or 10065,
        _ => false
    };
    private static DailyReadFailureKind ClassifyFailure(Exception failure, CancellationToken token) => failure switch
    {
        OperationCanceledException => token.IsCancellationRequested ? DailyReadFailureKind.Cancelled : DailyReadFailureKind.Timeout,
        FormatException or InvalidDataException => DailyReadFailureKind.ProtocolFormat,
        _ => IsConnectionInterrupted(failure) ? DailyReadFailureKind.TransportInterrupted : DailyReadFailureKind.Other
    };
    // Only fixed descriptions and numeric native error codes leave the transport.
    // Exception messages can contain peer addresses or received data.
    internal static string DescribeFailure(Exception failure) => failure switch
    {
        OperationCanceledException => "等待回执超时",
        FormatException => DescribeFormatFailure(failure),
        InvalidDataException => "控制数据格式无效",
        Win32Exception socket => $"Windows 蓝牙错误 {socket.NativeErrorCode}",
        IOException => "控制通道已中断",
        _ => "控制请求失败"
    };
    private static string DescribeFormatFailure(Exception failure)
    {
        string? reason = failure.Data["HeyMelody.Protocol.FrameFailure"] switch
        {
            "frame-start" => "帧起始标记无效",
            "frame-length" => "帧长度无效",
            "fragment-layout" => "分片布局无效",
            "fragment-order" => "分片顺序无效",
            "fragment-sequence" => "分片序号无效",
            "inner-length" => "消息长度不匹配",
            "incomplete-message" => "消息未完整接收",
            _ => null
        };
        return reason is null ? "控制数据格式无效" : $"控制数据格式无效（{reason}）";
    }
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        Interlocked.Exchange(ref _usable, 0); _lifetime.Cancel();
        lock (_pendingGate) _reply?.TrySetCanceled(_lifetime.Token);
        try { await _transport.DisposeAsync(); }
        catch (Exception error) { DiagnosticLog.Error("Daily", "session.dispose-failed", "Daily transport disposal failed.", error, DiagnosticContext()); }
        await _reader;
        _lifetime.Dispose();
        DiagnosticLog.Info("Daily", "session.closed", "The daily session and reader have closed.", new { sessionId = _diagnosticId });
    }
    private object DiagnosticContext()
    {
        lock (_pendingGate) return new { sessionId = _diagnosticId, transport = _transport.GetType().FullName,
            pendingCommand = _key is RequestKey key ? $"0x{key.Command:X4}" : null, pendingSequence = _key?.Sequence,
            pendingCorrelationId = _pendingCorrelationId, usable = Usable, remainingSequences = Remaining,
            codecCompatibilityAllowed = _allowCodecReplyCompatibility, codecCompatibilityUsed = _codecReplyCompatibilityUsed };
    }
}
