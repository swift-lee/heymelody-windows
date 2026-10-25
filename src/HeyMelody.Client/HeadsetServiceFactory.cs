namespace HeyMelodyWindows.App;

public static class HeadsetServiceFactory
{
    public static IHeadsetService Create() => OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
        ? new ClientBridge() : new UnsupportedPlatformService();
    public static IHeadsetService CreatePreview() => new PreviewService();
    public static IHeadsetService CreateLivePreview() => OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
        ? new ReadOnlyPreviewService(new ClientBridge())
        : throw new PlatformNotSupportedException("当前平台尚未实现真实耳机只读预览。");
}

/// <summary>Explicit fallback for platforms without a Bluetooth backend.</summary>
public sealed class UnsupportedPlatformService : IHeadsetService
{
    public const string Message = "当前平台尚未实现蓝牙控制；可使用 --ui-preview 查看界面演示。";
    public Task<IReadOnlyList<UiDevice>> EnumerateAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<UiDevice>>([]);
    public Task<UiSnapshot> ConnectAsync(string identity, CancellationToken cancellationToken) =>
        Task.FromResult(new UiSnapshot { Identity = identity, Failure = Message });
    public Task DisconnectAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task<UiSnapshot> RefreshAsync(CancellationToken cancellationToken) =>
        Task.FromResult(new UiSnapshot { Failure = Message });
    public Task<UiSnapshot> RefreshAsync(RefreshScope scope, CancellationToken cancellationToken) => RefreshAsync(cancellationToken);
    public Task<UiApplyResult> ApplyAsync(UiAction action, CancellationToken cancellationToken) =>
        Task.FromResult(new UiApplyResult { BackendReceiptReceived = true, FailureKind = ApplyFailure.StateUnavailable,
            ConnectionState = ConnectionState.Unknown, Message = Message });
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
