// Partial: the chart synth's seam (ABI v38/v40) — IChartSynthEngine forwarded to ChartSynthEngineDriver (the request, its push chain and its rebuild replay), the availability event, and the song clock.
using DotCharter.Tone.Audio.ChartSynth;
using JuceSharp;

namespace DotCharter.Tone.Audio;

public sealed partial class JuceAudioPlaybackService : IChartSynthEngine
{
    // The request and every engine write of it live in the driver; it reads the
    // engine through the same one volatile read of _engine this partial made, and
    // the instrument route through the sandbox flag. The rebuild replays it from
    // AttachPerPlayerStateToEngine (_chartSynth.ReplayTo).
    private readonly ChartSynthEngineDriver _chartSynth;

    /// <inheritdoc />
    public bool IsChartSynthAvailable => _engine != null;

    /// <inheritdoc />
    public event EventHandler? ChartSynthAvailabilityChanged;

    // ReplaceEngine's finally: every (re)build ends here, whether it published an
    // engine, failed, or returned early because the service is being disposed.
    private void RaiseChartSynthAvailabilityChanged() => ChartSynthAvailabilityChanged?.Invoke(this, EventArgs.Empty);

    /// <inheritdoc />
    public bool IsChartSynthEnabled => _chartSynth.IsEnabled;

    /// <inheritdoc />
    public void SetChartSynthEnabled(bool enabled) => _chartSynth.SetEnabled(enabled);

    /// <inheritdoc />
    public ChartSynthVoice ChartSynthVoice => _chartSynth.Voice;

    /// <inheritdoc />
    public bool IsChartSynthVoiceAvailable(ChartSynthVoice voice) => ChartSynthEngineDriver.IsVoiceAvailable(voice);

    /// <inheritdoc />
    public bool TrySetChartSynthVoice(ChartSynthVoice voice) => _chartSynth.TrySetVoice(voice);

    /// <inheritdoc />
    public ChartSynthInstrument GuitarInstrument => _chartSynth.GuitarInstrument;

    /// <inheritdoc />
    public ChartSynthInstrument BassInstrument => _chartSynth.BassInstrument;

    /// <inheritdoc />
    public bool TrySetChartSynthInstrument(ChartSynthInstrument instrument) => _chartSynth.TrySetInstrument(instrument);

    /// <inheritdoc />
    public ChartSynthModelInfo? GetChartSynthModelInfo(ChartSynthModel model) => _chartSynth.GetModelInfo(model);

    /// <inheritdoc />
    public float ChartSynthGain
    {
        get => _chartSynth.Gain;
        set => _chartSynth.Gain = value;
    }

    /// <inheritdoc />
    public long? UploadedChartFingerprint => _chartSynth.UploadedFingerprint;

    /// <inheritdoc />
    public bool UploadChartNotes(ChartNoteUpload upload) => _chartSynth.UploadNotes(upload);

    /// <inheritdoc />
    public bool CanRouteInstrumentToRig => !_outOfProcessVst;

    /// <inheritdoc />
    public bool RouteInstrumentToRig
    {
        get => _chartSynth.RouteInstrumentToRig;
        set => _chartSynth.RouteInstrumentToRig = value;
    }

    /// <inheritdoc />
    public bool TryGetChartSynthStatus(out ChartSynthStatus status) => _chartSynth.TryGetStatus(out status);

    /// <summary>
    ///     Blocks until every queued chart-synth push has reached the engine (60 s
    ///     cap — a deadlock detector). For tests and for a caller that must read
    ///     the engine state its own change produced.
    /// </summary>
    internal void WaitForChartSynthPushes() => _chartSynth.WaitForPushes();

    /// <summary>
    ///     Binds <paramref name="song" /> as the engine's song clock — the transport
    ///     the chart source follows. Called wherever the song source is PUBLISHED
    ///     (a load, and the rebuild's re-open), which is what keeps the clock bound
    ///     across both; a disposed source unbinds itself natively.
    /// </summary>
    private void BindSongClock(JuceAudioEngine engine, JuceAudioSource song)
    {
        try { engine.SetSongClockSource(song); }
        catch (Exception ex) when (ex is ObjectDisposedException or JuceAudioException or ArgumentException)
        {
            _logger?.Warning(AppLogCategory.Audio, $"Chart synth: binding the song clock failed: {ex.Message}");
        }
    }
}
