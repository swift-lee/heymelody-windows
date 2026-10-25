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
            {
                DiagnosticLog.Info("client.lifecycle", "ownedComponentRefused", "Refused to start a component after the backend ended",
                    new { start.FileName, retired = RetiredPipes.ContainsKey(BackendEndpoint.PipeName), expired = DiagnosticRuntime.IsBoundLaunchExpired });
                throw new OperationCanceledException("后台已经结束，不能再启动此周期的组件。", token);
            }
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
                {
                    DiagnosticLog.Info("client.lifecycle", "backendStartRefused", "Refused to restart the backend while exiting",
                        new { _disposed, _stopping, exitPending = ExitPendingLocked, retired = RetiredPipes.ContainsKey(_pipeName), boundLaunch = DiagnosticRuntime.HasBoundLaunch });
                    throw new IOException("后台正在退出，不能重新启动。");
                }
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
        if (retired is not null)
        {
            DiagnosticLog.Info("client.lifecycle", "retiredPipeReplay", "Replaying the retired pipe's terminal state", new { retired.ExitRequestId, retired.BackendInstanceId });
            CommitStopping(retired, propagate: false);
        }
        if (DiagnosticRuntime.HasBoundLaunch)
        {
            DiagnosticRuntime.BoundOwnerExited += BoundOwnerEnded;
            if (DiagnosticRuntime.IsBoundLaunchExpired) BoundOwnerEnded();
        }
    }

    private void BoundOwnerEnded()
    {
        DiagnosticLog.Info("client.lifecycle", "boundOwnerEnded", "The bound launch owner ended; committing stopping", new { _backendInstanceId });
        CommitStopping(new() { Kind = "stopping", BackendInstanceId = _backendInstanceId ?? "" });
    }

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
            DiagnosticLog.Info("client.lifecycle", "commitStopping", "Committing the terminal stopping state",
                new { notification.ExitRequestId, notification.BackendInstanceId, propagate, peers = others.Length,
                    alreadyStopping = _stopping, ackConnection = connection is not null, fromSource = sourceConnection is not null });
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
        string? skipped = connection.Closed ? "connectionClosed" : string.IsNullOrEmpty(notification.ExitRequestId) ? "noExitRequestId"
            : Interlocked.Exchange(ref connection.StopAckSent, 1) != 0 ? "alreadySent" : null;
        if (skipped is not null)
        {
            DiagnosticLog.Debug("client.lifecycle", "stoppingAckSkipped", "Terminal acknowledgement not sent", new { reason = skipped, notification.ExitRequestId });
            return;
        }
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
        { DiagnosticLog.Warning("client.lifecycle", "stopAckEnded", "Terminal acknowledgement ended on the original pipe", error); }
    }

    private void ReceivePrepareLocked(BackendEvent notification, Connection connection)
    {
        if (_stopping)
        {
            DiagnosticLog.Debug("client.lifecycle", "prepareDropped", "Ignored requestExit after stopping", new { notification.ExitRequestId });
            return;
        }
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
            || notification.BackendInstanceId != _exitRequest.BackendInstanceId)
        {
            DiagnosticLog.Debug("client.lifecycle", "exitCancelledDropped", "Ignored an exitCancelled that does not match the current request",
                new { notification.ExitRequestId, current = _exitRequest?.ExitRequestId, _stopping });
            return;
        }
        DiagnosticLog.Info("client.lifecycle", "exitCancelled", "Exit request cancelled by the Host", new { notification.ExitRequestId });
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
            if (_stopping)
            {
                DiagnosticLog.Debug("client.lifecycle", "voteSkipped", "Exit vote skipped because the bridge is already stopping", new { exitRequestId, approved });
                return;
            }
            if (_exitRequest is not { } current || current.ExitRequestId != exitRequestId || _exitConnection is null)
                throw new OperationCanceledException("退出请求已取消或后台已变化。");
            request = current;
            connection = _exitConnection;
            _localExitVote = approved;
        }
        DiagnosticLog.Info("client.lifecycle", "exitVote", "Voting on the original pipe", new { exitRequestId, approved, connectionClosed = connection.Closed });
        if (connection.Closed && approved)
        {
            DiagnosticLog.Info("client.lifecycle", "stoppingSynthesized", "Original pipe closed; synthesizing stopping for an approving vote", new { exitRequestId });
            CommitStopping(new() { Kind = "stopping", BackendInstanceId = request.BackendInstanceId, ExitRequestId = exitRequestId }, sourceConnection: connection);
            return;
        }
        var settled = false;
        try
        {
            _ = await RequestAsync<bool>(new() { Operation = "exitVote", ExitRequestId = exitRequestId,
                Approved = approved, BackendInstanceId = request.BackendInstanceId }, TimeSpan.FromSeconds(15), token,
                boundConnection: connection);
            settled = true;
        }
        catch (BackendOperationException error)
        { throw new OperationCanceledException("退出投票已取消或被后台拒绝。", error); }
        catch (IOException) when (approved && (_stopping || connection.Closed))
        {
            settled = true;
            DiagnosticLog.Info("client.lifecycle", "stoppingSynthesized", "Original pipe closed during the vote; synthesizing stopping", new { exitRequestId });
            CommitStopping(new() { Kind = "stopping", BackendInstanceId = request.BackendInstanceId, ExitRequestId = exitRequestId }, sourceConnection: connection);
        }
        finally
        {
            if (!settled && approved)
            {
                // The approving vote did not reach the Host: the local decision is not recorded as made, so a later retry or
                // a new requestExit starts from a clean state. A denial keeps its local state so this window stays usable.
                lock (_stateGate)
                    if (_exitRequest?.ExitRequestId == exitRequestId && _localExitVote == approved && !_stopping)
                    {
                        _localExitVote = null;
                        DiagnosticLog.Warning("client.lifecycle", "exitVoteUndelivered", "Approving vote was not delivered; local vote reset", data: new { exitRequestId });
                    }
            }
            if (!approved)
            {
                bool reconnect;
                lock (_stateGate) reconnect = !_disposed && !_stopping && (_connection is null || _connection.Closed);
                if (reconnect) _ = ReconnectAsync();
            }
        }
    }
}
