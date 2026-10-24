using System.Text.Json;
using System.Threading.Channels;
using System.Diagnostics;
using HeyMelody.Diagnostics;
using HeyMelodyWindows.Host;

namespace HeyMelodyWindows.App;

public sealed partial class ClientBridge
{
    // Control messages must not compete with device state, or disappear when their pipe closes.
    private readonly Channel<QueuedEvent> _controlEvents = Channel.CreateUnbounded<QueuedEvent>(new()
        { SingleReader = true, SingleWriter = false });
    private readonly Dictionary<string, QueuedEvent> _overflowState = [];
    private readonly Queue<QueuedEvent> _overflowEvents = [];
    private readonly CancellationTokenSource _stopConnecting = new();
    private Action<BackendEvent>? _eventReceived;
    private BackendEvent? _exitRequest, _terminalExit;
    private Connection? _exitConnection;
    private bool? _localExitVote;
    private bool _preparePublished;
    private static readonly object BridgesGate = new();
    private static readonly Dictionary<string, List<WeakReference<ClientBridge>>> Bridges = [];
    private static readonly Dictionary<string, BackendEvent> RetiredPipes = [];

    public bool IsStopping => _stopping;
    public event Action<BackendEvent>? EventReceived
    {
        add
        {
            if (value is null) return;
            lock (_stateGate)
            {
                _eventReceived += value;
                var replay = _terminalExit ?? (_localExitVote != false && _exitConnection?.Subscribed == true ? _exitRequest : null);
                if (replay is not null)
                    foreach (Action<BackendEvent> handler in value.GetInvocationList())
                        _controlEvents.Writer.TryWrite(new(replay, null, handler));
            }
        }
        remove { lock (_stateGate) _eventReceived -= value; }
    }

    private bool ExitPendingLocked => _exitRequest is not null && _localExitVote != false;
    private bool CanReconnect { get { lock (_stateGate) return !_disposed && !_stopping && !ExitPendingLocked && !_versionMismatch; } }

    public static Process StartOwnedComponent(ProcessStartInfo start, CancellationToken token)
    {
        lock (BridgesGate)
        {
            token.ThrowIfCancellationRequested();
            if (RetiredPipes.ContainsKey(BackendEndpoint.PipeName) || DiagnosticRuntime.IsBoundLaunchExpired)
                throw new OperationCanceledException("后台已经结束，不能再启动此周期的组件。", token);
            DiagnosticRuntime.BindChildStart(start);
            token.ThrowIfCancellationRequested();
            return Process.Start(start) ?? throw new IOException("无法启动独立组件。");
        }
    }

    private void StartBackendIfAllowed()
    {
        lock (_stateGate)
        {
            // The same lock linearizes process-wide retirement and launch, before peer propagation completes.
            lock (BridgesGate)
            {
                if (_disposed || _stopping || ExitPendingLocked || RetiredPipes.ContainsKey(_pipeName)
                    || DiagnosticRuntime.HasBoundLaunch)
                    throw new IOException("后台正在退出，不能重新启动。");
                _startBackend?.Invoke();
            }
        }
    }

    private void RegisterBridge()
    {
        BackendEvent? retired;
        lock (BridgesGate)
        {
            if (!Bridges.TryGetValue(_pipeName, out var peers)) Bridges[_pipeName] = peers = [];
            peers.RemoveAll(item => !item.TryGetTarget(out _));
            peers.Add(new(this));
            RetiredPipes.TryGetValue(_pipeName, out retired);
        }
        if (retired is not null) CommitStopping(retired, propagate: false);
        if (DiagnosticRuntime.HasBoundLaunch)
        {
            DiagnosticRuntime.BoundOwnerExited += BoundOwnerEnded;
            if (DiagnosticRuntime.IsBoundLaunchExpired) BoundOwnerEnded();
        }
    }

    private void BoundOwnerEnded() => CommitStopping(new() { Kind = "stopping", BackendInstanceId = _backendInstanceId ?? "" });

    private void UnregisterBridge()
    {
        DiagnosticRuntime.BoundOwnerExited -= BoundOwnerEnded;
        lock (BridgesGate)
            if (Bridges.TryGetValue(_pipeName, out var peers))
                peers.RemoveAll(item => !item.TryGetTarget(out var peer) || ReferenceEquals(peer, this));
    }

    private void CommitStopping(BackendEvent notification, bool propagate = true, Connection? sourceConnection = null)
    {
        Connection? connection;
        ClientBridge[] others = [];
        lock (_stateGate)
        {
            if (_disposed) return;
            // An acknowledgement belongs to the pipe that delivered this decision, never a replacement pipe.
            connection = sourceConnection ?? (_connection?.BackendInstanceId == notification.BackendInstanceId ? _connection : null);
            if (propagate)
            {
                lock (BridgesGate)
                {
                    RetiredPipes[_pipeName] = notification;
                    others = Bridges.TryGetValue(_pipeName, out var peers)
                        ? peers.Select(item => item.TryGetTarget(out var peer) ? peer : null)
                            .OfType<ClientBridge>().Where(peer => !ReferenceEquals(peer, this)).ToArray() : [];
                }
            }
            if (!_stopping)
            {
                _stopping = true;
                _terminalExit = notification;
                _overflowState.Clear();
                _overflowEvents.Clear();
                _controlEvents.Writer.TryWrite(new(notification, null));
            }
        }
        // Cancel connection attempts without cancelling the old pipe's terminal acknowledgement.
        try { _stopConnecting.Cancel(); } catch (ObjectDisposedException) { }
        foreach (var peer in others) peer.CommitStopping(notification, propagate: false);
        if (connection is not null) _ = RunBackground(() => SendStoppingAckAsync(connection, notification));
    }

    private async Task SendStoppingAckAsync(Connection connection, BackendEvent notification)
    {
        if (connection.Closed || string.IsNullOrEmpty(notification.ExitRequestId)
            || Interlocked.Exchange(ref connection.StopAckSent, 1) != 0) return;
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        budget.CancelAfter(TimeSpan.FromSeconds(2));
        try
        {
            await connection.WriteGate.WaitAsync(budget.Token);
            try
            {
                var ack = new BackendRequest { Operation = "stoppingAck", ExitRequestId = notification.ExitRequestId,
                    BackendInstanceId = notification.BackendInstanceId, Id = Interlocked.Increment(ref _nextId) };
                var record = JsonSerializer.Serialize(ack, RpcWire.Json);
                DiagnosticLog.Info("client.ipc", "send", "Sending terminal acknowledgement on the original pipe", new { record });
                await connection.Writer.WriteLineAsync(record.AsMemory(), budget.Token);
                await connection.Writer.FlushAsync(budget.Token);
                DiagnosticLog.Debug("client.lifecycle", "stoppingAckSent", "Terminal state acknowledged", new { ack.Id, ack.ExitRequestId });
            }
            finally { connection.WriteGate.Release(); }
        }
        catch (Exception error) when (error is IOException or OperationCanceledException or ObjectDisposedException)
        { DiagnosticLog.Debug("client.lifecycle", "stopAckEnded", "Terminal acknowledgement ended on the original pipe", new { error = error.ToString() }); }
    }

    private void ReceivePrepareLocked(BackendEvent notification, Connection connection)
    {
        if (_stopping) return;
        if (_exitRequest?.ExitRequestId != notification.ExitRequestId || _exitRequest?.BackendInstanceId != notification.BackendInstanceId)
        { _localExitVote = null; _preparePublished = false; }
        _exitRequest = notification;
        _exitConnection = connection;
        PublishPrepareLocked();
    }

    private void PublishPrepareLocked()
    {
        if (_stopping || _localExitVote == false || _preparePublished || _exitRequest is null || _exitConnection?.Subscribed != true) return;
        _preparePublished = true;
        _controlEvents.Writer.TryWrite(new(_exitRequest, null));
    }

    private void ReceiveExitCancelledLocked(BackendEvent notification)
    {
        if (_stopping || _exitRequest is null || notification.ExitRequestId != _exitRequest.ExitRequestId
            || notification.BackendInstanceId != _exitRequest.BackendInstanceId) return;
        _exitRequest = null; _exitConnection = null; _localExitVote = null; _preparePublished = false;
        _controlEvents.Writer.TryWrite(new(notification, null));
    }

    private void QueueStateLocked(BackendEvent notification, Connection connection)
    {
        if (_stopping) return;
        var item = new QueuedEvent(notification, connection);
        if (notification.Kind is not ("catalog" or "snapshot" or "health" or "resources" or "calibration"))
        {
            // Rare, non-coalescible messages remain ordered, but never block the pipe reader or terminal state.
            if (_overflowEvents.Count != 0 || !_events.Writer.TryWrite(item)) _overflowEvents.Enqueue(item);
            return;
        }
        var key = notification.Kind + ":" + (notification.Snapshot?.Identity ?? notification.Calibration?.Handle ?? "");
        if (_overflowState.ContainsKey(key) || !_events.Writer.TryWrite(item)) _overflowState[key] = item;
    }

    private void DrainOverflowLocked()
    {
        while (_overflowEvents.TryPeek(out var pending))
        {
            if (!_events.Writer.TryWrite(pending)) return;
            _overflowEvents.Dequeue();
        }
        foreach (var (key, item) in _overflowState.ToArray())
        {
            if (item.Source is { } source && (!ReferenceEquals(_connection, source) || source.Closed))
            { _overflowState.Remove(key); continue; }
            if (!_events.Writer.TryWrite(item)) break;
            _overflowState.Remove(key);
        }
    }

    private async Task VoteOnOriginalConnectionAsync(string exitRequestId, bool approved, CancellationToken token)
    {
        Connection connection;
        BackendEvent request;
        lock (_stateGate)
        {
            if (_stopping) return;
            if (_exitRequest is not { } current || current.ExitRequestId != exitRequestId || _exitConnection is null)
                throw new OperationCanceledException("退出请求已取消或后台已变化。");
            request = current;
            connection = _exitConnection;
            _localExitVote = approved;
        }
        if (connection.Closed && approved)
        {
            CommitStopping(new() { Kind = "stopping", BackendInstanceId = request.BackendInstanceId, ExitRequestId = exitRequestId }, sourceConnection: connection);
            return;
        }
        try
        {
            _ = await RequestAsync<bool>(new() { Operation = "exitVote", ExitRequestId = exitRequestId,
                Approved = approved, BackendInstanceId = request.BackendInstanceId }, TimeSpan.FromSeconds(15), token,
                boundConnection: connection);
        }
        catch (BackendOperationException error)
        { throw new OperationCanceledException("退出投票已取消或被后台拒绝。", error); }
        catch (IOException) when (approved && (_stopping || connection.Closed))
        { CommitStopping(new() { Kind = "stopping", BackendInstanceId = request.BackendInstanceId, ExitRequestId = exitRequestId }, sourceConnection: connection); }
        finally
        {
            if (!approved)
            {
                bool reconnect;
                lock (_stateGate) reconnect = !_disposed && !_stopping && (_connection is null || _connection.Closed);
                if (reconnect) _ = ReconnectAsync();
            }
        }
    }
}
