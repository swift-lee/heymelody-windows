namespace HeyMelodyWindows.App;

public sealed partial class ClientBridge
{
    public async Task RequestApplicationExitAsync(CancellationToken cancellationToken) =>
        _ = await RequestAsync<bool>(new() { Operation = "requestExit" }, TimeSpan.FromSeconds(10), cancellationToken);

    public async Task<IReadOnlyList<UiPreviewAudioOutput>> GetPreviewAudioOutputsAsync(string handle, CancellationToken token) =>
        await RequestAsync<List<UiPreviewAudioOutput>>(new() { Operation = "calibrationAudioOutputs", CalibrationHandle = handle },
            TimeSpan.FromSeconds(15), token);
}
