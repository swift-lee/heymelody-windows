using HeyMelodyWindows.App;

namespace HeyMelodyWindows.Host;

internal interface ICalibrationPeerWorker
{
    event Action<UiCalibrationState>? CalibrationChanged;
    event Action<UiCalibrationSpectrum>? CalibrationSpectrumChanged;
    Task<UiCalibrationState> CreateCalibrationAsync(string handle, string kind, CancellationToken token);
    Task<UiCalibrationState> GetCalibrationAsync(string handle, CancellationToken token);
    Task<IReadOnlyList<UiPreviewAudioOutput>> GetPreviewAudioOutputsAsync(string handle, CancellationToken token) =>
        Task.FromResult<IReadOnlyList<UiPreviewAudioOutput>>([]);
    Task<UiCalibrationState> AdvanceCalibrationAsync(string handle, long revision, UiCalibrationInput input, CancellationToken token);
    Task<UiCalibrationState> CancelCalibrationAsync(string handle, CancellationToken token);
    Task<IReadOnlyList<UiHearingProfile>> HearingProfilesAsync(CancellationToken token);
    Task<UiApplyResult> SelectHearingAsync(int? uid, CancellationToken token);
}
internal sealed partial class PeerWorker
{
    private string? _catalogGzip;
    private string _stableKey = "";
    private long _resourceRevision, _appMarker = 17006004, _generation;
    private string? _observedProductId;
    private byte? _observedColor;
    public event Action<UiCalibrationState>? CalibrationChanged;
    public event Action<UiCalibrationSpectrum>? CalibrationSpectrumChanged;
    public void ConfigureResources(string catalogGzip, long revision, long appMarker, string stableKey, long generation,
        string? observedProductId, byte? observedColor)
    { _catalogGzip = catalogGzip; _resourceRevision = revision; _appMarker = appMarker; _stableKey = stableKey; _generation = generation;
        _observedProductId = observedProductId; _observedColor = observedColor; }
    public Task<UiSnapshot> SetObservedColorAsync(string productId, byte color, CancellationToken token) =>
        RequestAsync<UiSnapshot>(new() { Operation = "observedColor", ObservedProductId = productId, ObservedColor = color }, token);
    public Task<UiCalibrationState> CreateCalibrationAsync(string handle, string kind, CancellationToken token) =>
        RequestAsync<UiCalibrationState>(new() { Operation = "createCalibration", Handle = handle, CalibrationKind = kind }, token);
    public Task<UiCalibrationState> GetCalibrationAsync(string handle, CancellationToken token) =>
        RequestAsync<UiCalibrationState>(new() { Operation = "getCalibration", Handle = handle }, token);
    public async Task<IReadOnlyList<UiPreviewAudioOutput>> GetPreviewAudioOutputsAsync(string handle, CancellationToken token) =>
        await RequestAsync<UiPreviewAudioOutput[]>(new() { Operation = "calibrationAudioOutputs", Handle = handle }, token);
    public Task<UiCalibrationState> AdvanceCalibrationAsync(string handle, long revision, UiCalibrationInput input, CancellationToken token) =>
        RequestAsync<UiCalibrationState>(new() { Operation = "advanceCalibration", Handle = handle, Revision = revision, Input = input }, token);
    public Task<UiCalibrationState> CancelCalibrationAsync(string handle, CancellationToken token) =>
        RequestAsync<UiCalibrationState>(new() { Operation = "cancelCalibration", Handle = handle }, token);
    public async Task<IReadOnlyList<UiHearingProfile>> HearingProfilesAsync(CancellationToken token) =>
        await RequestAsync<UiHearingProfile[]>(new() { Operation = "hearingProfiles" }, token);
    public Task<UiApplyResult> SelectHearingAsync(int? uid, CancellationToken token) =>
        RequestAsync<UiApplyResult>(new() { Operation = "selectHearing", HearingUid = uid }, token);
}
