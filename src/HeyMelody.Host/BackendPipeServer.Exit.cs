using HeyMelody.Diagnostics;
using HeyMelodyWindows.App;

namespace HeyMelodyWindows.Host;

internal sealed partial class BackendPipeServer
{
    private enum ExitPhase { Idle, Voting, Stopping }
    private sealed record StoppingClient(Client Client, Task Flushed, TaskCompletionSource Acknowledged);
    private ExitPhase _exitPhase;
    private int _disposeStarted;
    private readonly HashSet<string> _preparedClients = [];
    private readonly Dictionary<string, StoppingClient> _stoppingClients = [];
    private TaskCompletionSource _exitChanged = NewSignal();
    private bool IsStopping { get { lock (_voteSync) return _exitPhase == ExitPhase.Stopping; } }
    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static bool IsVotingRole(string? role) => role is "main" or "manager" or "calibration" or "preview";

    private void ExitChangedLocked()
    {
        var previous = _exitChanged; _exitChanged = NewSignal(); previous.TrySetResult();
    }
    private BackendEvent ExitEvent(string kind) => new()
        { Kind = kind, BackendInstanceId = supervisor.InstanceId, ExitRequestId = _exitId };

    private void PrepareClientLocked(Client client)
    {
        if (IsVotingRole(client.Role) && !_votes.ContainsKey(client.Key))
        {
            _votes[client.Key] = new(TaskCreationOptions.RunContinuationsAsynchronously);
            ExitChangedLocked();
        }
        if (_preparedClients.Add(client.Key)) client.SendEvent(ExitEvent("requestExit"));
    }

    private void ClientAcceptedDuringExit(Client client)
    {
        lock (_voteSync)
        {
            if (_exitPhase == ExitPhase.Voting) PrepareClientLocked(client);
            else if (_exitPhase == ExitPhase.Stopping) StopClientLocked(client);
        }
    }

    private void SubscribeDuringExit(Client client, BackendRequest request, BackendSubscription subscription)
    {
        // The caller holds the supervisor's baseline lock; no exit path takes that lock while holding _voteSync.
        lock (_voteSync)
        {
            if (_exitPhase == ExitPhase.Stopping)
            {
                client.Reply(request.Id, null, "stopping", "后台正在退出。"); StopClientLocked(client); return;
            }
            client.Role = request.Role; client.Reply(request.Id, subscription);
            if (_exitPhase == ExitPhase.Voting) PrepareClientLocked(client);
        }
    }

    private bool RejectStoppingOperation(Client client, BackendRequest request)
    {
        if (request.Operation is "exitVote" or "stoppingAck") return false;
        lock (_voteSync)
        {
            if (_exitPhase == ExitPhase.Idle || _exitPhase == ExitPhase.Voting && request.Operation is "subscribe" or "cancelCalibration") return false;
            if (_exitPhase == ExitPhase.Voting)
            {
                PrepareClientLocked(client); client.Reply(request.Id, null, "exitPending", "后台正在确认退出，暂时不能开始新操作。");
            }
            else
            {
                client.Reply(request.Id, null, "stopping", "后台正在退出。"); StopClientLocked(client);
            }
            return true;
        }
    }

    private void ReceiveExitVote(Client client, BackendRequest request)
    {
        lock (_voteSync)
        {
            if (_exitPhase != ExitPhase.Voting || !IsVotingRole(client.Role) || request.BackendInstanceId != supervisor.InstanceId
                || request.ExitRequestId != _exitId || request.Approved is not { } approved || !_votes.TryGetValue(client.Key, out var vote))
                throw new InvalidOperationException("退出请求已变化。");
            client.Reply(request.Id, true); vote.TrySetResult(approved); ExitChangedLocked();
        }
    }

    private void ReceiveStoppingAcknowledgement(Client client, BackendRequest request)
    {
        lock (_voteSync)
        {
            if (_exitPhase != ExitPhase.Stopping || request.BackendInstanceId != supervisor.InstanceId
                || request.ExitRequestId != _exitId || !_stoppingClients.TryGetValue(client.Key, out var stopping))
                throw new InvalidOperationException("退出请求已变化。");
            stopping.Acknowledged.TrySetResult(); ExitChangedLocked();
        }
        // One-way acknowledgement: the client receive pump must never wait for a response to itself.
    }

    private void StopClientLocked(Client client)
    {
        if (client.Lifetime.IsCancellationRequested || _stoppingClients.ContainsKey(client.Key)) return;
        var flushed = client.SendFlushedAsync(ExitEvent("stopping"));
        _stoppingClients[client.Key] = new(client, flushed, NewSignal());
        _ = ObserveTerminalWriteAsync(client, flushed);
        ExitChangedLocked();
    }
    private async Task ObserveTerminalWriteAsync(Client client, Task flushed)
    {
        try { await flushed; }
        catch (Exception error)
        {
            if (!client.Lifetime.IsCancellationRequested)
                DiagnosticLog.Warning("host.lifecycle", "stoppingWriteFailed", "Could not deliver terminal backend state", error, new { client.Key });
            client.Dispose();
        }
        finally { lock (_voteSync) ExitChangedLocked(); }
    }
    private void ClientDetachedDuringExit(Client client)
    {
        lock (_voteSync)
        {
            if (_votes.TryGetValue(client.Key, out var vote)) vote.TrySetResult(true);
            if (_stoppingClients.TryGetValue(client.Key, out var stopping)) stopping.Acknowledged.TrySetResult();
            ExitChangedLocked();
        }
    }

    private async Task<bool> WaitForExitVotesAsync()
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        budget.CancelAfter(exitVoteBudget ?? TimeSpan.FromSeconds(60));
        try
        {
            while (true)
            {
                Task changed;
                lock (_voteSync)
                {
                    if (_votes.Values.Any(vote => vote.Task.IsCompletedSuccessfully && !vote.Task.Result)) return false;
                    if (_votes.Values.All(vote => vote.Task.IsCompletedSuccessfully)) { _exitPhase = ExitPhase.Stopping; return true; }
                    changed = _exitChanged.Task;
                }
                await changed.WaitAsync(budget.Token);
            }
        }
        catch (OperationCanceledException) { return false; }
    }

    private async Task DrainStoppingClientsAsync()
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        budget.CancelAfter(stoppingBudget ?? TimeSpan.FromSeconds(3));
        StoppingClient[] pending = [];
        try
        {
            while (true)
            {
                Task changed;
                lock (_voteSync)
                {
                    foreach (var client in _clients.Values) StopClientLocked(client);
                    pending = _stoppingClients.Values.Where(value => !value.Client.Lifetime.IsCancellationRequested
                        && (!value.Flushed.IsCompletedSuccessfully || !value.Acknowledged.Task.IsCompletedSuccessfully)).ToArray();
                    if (pending.Length == 0) return;
                    changed = _exitChanged.Task;
                }
                await changed.WaitAsync(budget.Token);
            }
        }
        catch (OperationCanceledException)
        {
            DiagnosticLog.Warning("host.lifecycle", "stoppingDeadline", "Terminal delivery deadline reached; disconnecting remaining clients",
                data: new { pending = pending.Select(value => new { value.Client.Key, value.Client.Role, flushed = value.Flushed.IsCompletedSuccessfully }).ToArray() });
            foreach (var value in pending) value.Client.Dispose();
        }
    }

    public async Task RequestExitAsync()
    {
        if (!await _exitGate.WaitAsync(0)) return;
        try
        {
            lock (_voteSync)
            {
                if (_exitPhase != ExitPhase.Idle || _lifetime.IsCancellationRequested) return;
                _exitPhase = ExitPhase.Voting; _exitId = Guid.NewGuid().ToString("N");
                _votes.Clear(); _preparedClients.Clear();
                foreach (var client in _clients.Values.Where(client => !client.Lifetime.IsCancellationRequested)) PrepareClientLocked(client);
            }
            DiagnosticLog.Info("host.lifecycle", "exitRequested", "Host exit requested", new { _exitId });
            if (!await WaitForExitVotesAsync())
            {
                Task[] deliveries;
                lock (_voteSync)
                {
                    var cancelled = ExitEvent("exitCancelled"); cancelled.Message = "退出已取消。";
                    _exitPhase = ExitPhase.Idle;
                    deliveries = _clients.Values.Select(client => client.SendFlushedAsync(cancelled)).ToArray();
                }
                try { await Task.WhenAll(deliveries).WaitAsync(stoppingBudget ?? TimeSpan.FromSeconds(3), _lifetime.Token); }
                catch (Exception error) { DiagnosticLog.Warning("host.lifecycle", "cancellationDeliveryFailed", "Exit cancellation could not reach every client", error); }
                return;
            }
            DiagnosticLog.Info("host.lifecycle", "exitApproved", "Host exit approved", new { _exitId });
            await DrainStoppingClientsAsync();
            try { await supervisor.StopGracefullyAsync(); }
            catch (Exception error)
            {
                DiagnosticLog.Error("host.lifecycle", "stopFailed", "Graceful backend cleanup failed; finishing Host exit", error);
                try { await supervisor.DisposeAsync(); }
                catch (Exception cleanupError) { DiagnosticLog.Error("host.lifecycle", "disposeFailed", "Backend fallback cleanup failed", cleanupError); }
            }
            foreach (Func<Task> handler in ExitApproved?.GetInvocationList() ?? [])
                try { await handler(); }
                catch (Exception error) { DiagnosticLog.Error("host.lifecycle", "exitCallbackFailed", "Host exit callback failed", error); }
        }
        finally
        {
            lock (_voteSync)
                if (_exitPhase != ExitPhase.Stopping) { _votes.Clear(); _preparedClients.Clear(); _exitId = null; }
            _exitGate.Release();
        }
    }
}
