using HeyMelodyWindows.App;
using HeyMelodyWindows.Calibration;

namespace HeyMelodyWindows.Host;

public sealed partial class NativeHeadsetService
{
    internal async Task<IReadOnlyList<UiPreviewAudioOutput>> GetPreviewAudioOutputsAsync(string handle, CancellationToken token)
    {
        await _auditionGate.WaitAsync(token);
        try
        {
            var state = GetCalibration(handle);
            if (!state.PreviewAudioOutputSelectionRequired || state.Workflow != "hearing") return [];
            if (state.Stage is "Cancelled" or "Failed" || _auditionClosing) throw new InvalidOperationException("此试听会话已结束。");
            _auditionPlayer ??= CreateAuditionPlayer();
            if (_auditionPlayer is not ISelectableCalibrationAuditionPlayer selectable) return [];
            var outputs = selectable.GetOutputs();
            if (state.SelectedPreviewAudioOutputToken != selectable.SelectedOutputToken)
            {
                _calibrationState = state with { SelectedPreviewAudioOutputToken = selectable.SelectedOutputToken, Revision = ++_calibrationRevision };
                CalibrationChanged?.Invoke(_calibrationState);
            }
            return outputs;
        }
        finally { _auditionGate.Release(); }
    }
    private async Task SelectPreviewOutputAsync(string handle, string outputToken, CancellationToken token)
    {
        var state = GetCalibration(handle);
        if (!state.PreviewAudioOutputSelectionRequired || !state.CanPreview || !state.FiltersReady)
            throw new InvalidOperationException("当前步骤不能选择试听输出。");
        await PreviewAuditionAsync(CalibrationPreviewMode.Stopped, token);
        await _auditionGate.WaitAsync(token);
        try
        {
            _ = GetCalibration(handle);
            _auditionPlayer ??= CreateAuditionPlayer();
            if (_auditionPlayer is not ISelectableCalibrationAuditionPlayer selectable)
                throw new InvalidOperationException("此平台不提供试听输出选择。");
            await selectable.SelectOutputAsync(outputToken, token);
            PublishAuditionState(false);
        }
        finally { _auditionGate.Release(); }
    }
}
