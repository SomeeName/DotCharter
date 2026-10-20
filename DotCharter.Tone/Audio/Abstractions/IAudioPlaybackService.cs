using DotCharter.Tone.Audio.Engine;

namespace DotCharter.Tone.Audio.Abstractions;

/// <summary>
/// Public contract the rest of the application uses to drive audio playback.
/// <para>
/// It is the sum of five focused seams — <see cref="IPlaybackTransport"/>,
/// <see cref="IAudioDeviceControl"/>, <see cref="IStemMixer"/>,
/// <see cref="IVstChainHost"/> and <see cref="ILiveInputCapture"/> — plus what
/// only Services and the composition root need: the master bus (gains, pan,
/// pitch, the fixed filters, the parametric EQ), the MIDI chart preview,
/// waveform decoding and the engine's lifetime. A seam that has a consumer is
/// registered as a forwarding singleton onto the one instance, and the
/// Presentation layer may name nothing but the seams
/// (<c>BannedSymbols.Presentation.txt</c>; docs/agents/services/audio-engine.md,
/// "Focused interfaces for view models").
/// </para>
/// </summary>
public interface IAudioPlaybackService : IPlaybackTransport, IAudioDeviceControl, IStemMixer,
    IVstChainHost, ILiveInputCapture, IDisposable
{
    /// <summary>
    /// True when the implementation guarantees no third-party VST code runs
    /// inside this process during teardown (0.12.0 out-of-process hosting),
    /// so the app may take a clean shutdown instead of the TerminateNow
    /// fast-exit that works around in-proc plugin teardown crashes.
    /// </summary>
    bool SupportsCleanShutdown => false;

    double Volume { get; set; }
    double InputVolume { get; set; }
    double MonitorVolume { get; set; }

    double Pan { get; set; }
    double PitchShift { get; set; }

    double HighPassFreq { get; set; }
    double HighPassQ { get; set; }
    double LowPassFreq { get; set; }
    double LowPassQ { get; set; }
    double BandPassFreq { get; set; }
    double BandPassQ { get; set; }

    // ---- Parametric EQ (ABI v29) ------------------------------------------

    /// <summary>
    ///     True when the engine underneath actually HAS the six-band EQ.
    ///     <para>
    ///         An implementation that answers false must be believed by the UI:
    ///         the inspector's EQ module shows itself disabled with its reason
    ///         printed rather than drawing controls nothing honours. That is not
    ///         defensive coding, it is the rule this repo learned from
    ///         the (since removed) couch co-op rig applier — a control the engine cannot serve must
    ///         never look live.
    ///     </para>
    /// </summary>
    bool SupportsParametricEq => false;

    /// <summary>
    ///     Push a whole EQ section to the engine: every band, the master power,
    ///     the solo, the route and the trim.
    ///     <para>
    ///         Deliberately whole-section rather than per-property. The engine
    ///         republishes its coefficient set on any band write anyway, and a
    ///         property-at-a-time surface is how the three fixed filters ended up
    ///         with six setters that each had to remember to re-send the other
    ///         half of their pair.
    ///     </para>
    /// </summary>
    void ApplyEq(Equalizer.EqSection section) { }

    /// <summary>
    ///     How many bands the ENGINE reports it is running. −1 when there is no
    ///     engine to ask.
    ///     <para>
    ///         The badge reads this, not a managed count, so "4/6 active" is a
    ///         statement about the DSP rather than about the view model.
    ///     </para>
    ///     <para>
    ///         <b>−1 covers "no engine has ever been given this section" as well
    ///         as "no engine".</b> On a badge, an engine's honest zero and an
    ///         unanswerable question look identical and mean opposite things, and
    ///         only the caller's own section can tell them apart. An
    ///         implementation must not fold the second case into 0.
    ///     </para>
    /// </summary>
    int EngineEqActiveCount => -1;

    /// <summary>
    ///     Fill <paramref name="destinationDb" /> with the pre-EQ magnitude
    ///     spectrum of the bus the EQ is routed to, log-spaced 20 Hz … 20 kHz.
    /// </summary>
    /// <returns>How many bins were written; 0 when the engine cannot answer, in
    ///     which case the analyser draws nothing at all rather than a shape it
    ///     invented.</returns>
    int ReadEqSpectrum(float[] destinationDb) => 0;

    DeviceManager DeviceManager { get; }

    AudioDiagnostics Diagnostics { get; }

    Task InitializeAsync();
    Task ShutdownAsync();

    Task<WaveformSamples> GenerateWaveformAsync(string filePath);

    void SetMidiInstrument(int program);

    /// <summary>
    /// Drives the MIDI / VSTi chart preview: sends NoteOn for every chart sound the
    /// transport has just reached and NoteOff for every voice whose note has ended.
    ///
    /// <para><paramref name="rawPlayheadMs"/> must be <see cref="IPlaybackTransport.RawPlayheadMs"/>, NOT
    /// <see cref="IPlaybackTransport.CurrentTimeMs"/> / the <see cref="IPlaybackTransport.PositionChanged"/> argument. Both
    /// MIDI sinks are trigger-NOW (WinMM <c>midiOutShortMsg</c>, or the JUCE instrument
    /// route, which injects into the next render block) — so a NoteOn sent here lands in
    /// the same output block the engine is writing, i.e. the block carrying the song
    /// audio for <see cref="IPlaybackTransport.RawPlayheadMs"/>. Anchoring on the COMPENSATED playhead
    /// therefore fires the note when raw has already reached
    /// <c>chartTime + latency</c>, and the preview is heard
    /// <see cref="IAudioDeviceControl.EstimatedOutputLatencyMs"/> + <see cref="IAudioDeviceControl.AudioDelayMs"/> late against
    /// its own song — systematically, not as jitter. Same defect the metronome had;
    /// the raw axis makes output latency cancel by construction.</para>
    ///
    /// <para>The instrument plugin's OWN reported latency is handled inside the
    /// implementation (it fires that much earlier so the plugin's delayed output still
    /// lands on the beat); the caller only owes the correct transport axis.</para>
    ///
    /// <para>For the IN-PROCESS instrument the implementation goes one better than
    /// trigger-now: it re-reads the raw position paired with the engine's render frame and
    /// schedules each event at its exact sample a short lookahead ahead
    /// (<c>MidiPreviewScheduler</c>, <c>dca_engine_send_midi_at</c>). The axis rule is the
    /// same; <paramref name="rawPlayheadMs"/> remains what the WinMM and sandbox sinks use.</para>
    /// </summary>
    void UpdateMidiNotes(double rawPlayheadMs, IList<GuitarNote> activeChartNotes,
        IList<Chord> activeChartChords, Arrangement currentArrangement, double bpm, IList<BeatMarker> beats,
        IList<HandShape>? handShapes = null);
}
