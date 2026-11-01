using HeyMelodyWindows.App;

namespace HeyMelodyWindows.Host;

internal interface ISelectableCalibrationAuditionPlayer
{
    string? SelectedOutputToken { get; }
    IReadOnlyList<UiPreviewAudioOutput> GetOutputs();
    Task SelectOutputAsync(string token, CancellationToken cancellationToken);
    void ResetSelection();
}
