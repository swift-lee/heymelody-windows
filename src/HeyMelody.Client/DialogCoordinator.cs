namespace HeyMelodyWindows.App;

public interface IConfirmationService
{
    Task<bool> ConfirmAsync(string title, string message, CancellationToken cancellationToken);
    Task<bool> ConfirmFindHeadphonesToneAsync(string message, CancellationToken cancellationToken)
        => ConfirmAsync("⚠️警告：可能会造成听力受损！", message, cancellationToken);
    Task<bool> ConfirmRetryAsync(string title, string message, CancellationToken cancellationToken)
        => ConfirmAsync(title, message, cancellationToken);
    Task AlertAsync(string title, string message, CancellationToken cancellationToken)
        => Task.CompletedTask;
}

/// <summary>One dialog owner for settings, confirmations and failure notifications.</summary>
public sealed class DialogCoordinator(CancellationToken lifetime)
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<T> RunAsync<T>(Func<CancellationToken, Task<T>> action, CancellationToken token = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(lifetime, token);
        await _gate.WaitAsync(linked.Token);
        try { linked.Token.ThrowIfCancellationRequested(); return await action(linked.Token); }
        finally { _gate.Release(); }
    }

    public Task RunAsync(Func<CancellationToken, Task> action, CancellationToken token = default)
        => RunAsync(async cancellation => { await action(cancellation); return true; }, token);
}
