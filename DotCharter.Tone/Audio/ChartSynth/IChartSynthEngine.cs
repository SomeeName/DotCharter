namespace DotCharter.Tone.Audio.ChartSynth;

/// <summary>
///     The built-in CHART SYNTH (ABI v38): a note generator in the native engine
///     that plays the chart's notes as a DI signal THROUGH a seat's rig — pedals,
///     amp, cab, NAM, the song's tone — rendered against the song's own transport
///     clock, so it follows play, pause, seeks, loops and practice speed
///     sample-accurately. It is mixed into the rig AFTER the seat's capture ring:
///     note detection, scoring and the tuner never hear it.
///     <para>
///         Implemented by <c>JuceAudioPlaybackService</c> and reached with an
///         <c>as</c> cast like <c>IReampEngine</c>, so the null transport has none
///         and a UI shows the feature unavailable. Every setting is replayed onto
///         a rebuilt engine (device change, NAM 48 kHz request, watchdog recovery).
///     </para>
/// </summary>
public interface IChartSynthEngine
{
    /// <summary>True while an engine is up to render into.</summary>
    bool IsChartSynthAvailable { get; }

    /// <summary>Raised after an engine (re)build, which may have changed
    /// <see cref="IsChartSynthAvailable" />, on the thread that ran it.</summary>
    event EventHandler? ChartSynthAvailabilityChanged;

    /// <summary>Whether the generator renders into seat 0's rig.</summary>
    bool IsChartSynthEnabled { get; }

    /// <summary>Switches the generator on or off. Off at start.</summary>
    void SetChartSynthEnabled(bool enabled);

    /// <summary>The selected voice.</summary>
    ChartSynthVoice ChartSynthVoice { get; }

    /// <summary>Whether this build of the engine contains <paramref name="voice" />.
    /// A voice it lacks must be shown unavailable, never offered.</summary>
    bool IsChartSynthVoiceAvailable(ChartSynthVoice voice);

    /// <summary>Selects the voice; false (and no change) when it is unavailable.</summary>
    bool TrySetChartSynthVoice(ChartSynthVoice voice);

    /// <summary>The generator's level into the rig (linear, 0 to 4, 1 at start).</summary>
    float ChartSynthGain { get; set; }

    /// <summary>
    ///     Uploads the notes to render, replacing the previous upload. Built by
    ///     <see cref="ChartNoteEventBuilder" />; uploaded once per change, never per
    ///     tick. False when the engine rejected it (the previous notes stay).
    /// </summary>
    bool UploadChartNotes(ChartNoteUpload upload);

    /// <summary>The <see cref="ChartNoteUpload.Fingerprint" /> of the notes the engine
    /// holds, or null when it holds none.</summary>
    long? UploadedChartFingerprint { get; }

    /// <summary>
    ///     Whether the loaded VST INSTRUMENT can be routed into the rig. False while
    ///     VSTs run in the out-of-process sandbox: the sandbox returns the
    ///     instrument's audio after the rig has already run.
    /// </summary>
    bool CanRouteInstrumentToRig { get; }

    /// <summary>
    ///     Routes the in-process VST instrument into seat 0's rig input (after its
    ///     capture ring) instead of after the master. Has no effect, and reads
    ///     back false, while <see cref="CanRouteInstrumentToRig" /> is false.
    /// </summary>
    bool RouteInstrumentToRig { get; set; }

    /// <summary>A snapshot of the generator, for diagnostics and tests.</summary>
    bool TryGetChartSynthStatus(out ChartSynthStatus status);

    /// <summary>
    /// The built-in GUITAR's instrument: model, pickup, coil split. Played
    ///     by every guitar arrangement; replayed onto a rebuilt engine.
    /// </summary>
    ChartSynthInstrument GuitarInstrument { get; }

    /// <summary>The built-in BASS's instrument, played by every bass arrangement.</summary>
    ChartSynthInstrument BassInstrument { get; }

    /// <summary>
    ///     Selects the guitar's (a guitar model) or the bass's (a bass model)
    ///     instrument. False — and no change — when the pickup is not on the
    ///     model's selector. The engine loads the selection's sample pack OFF the
    ///     calling thread and cross-fades to it while it plays. WHICH instrument
    ///     to set, and saving it, is the caller's: DotCharter.Services'
    ///     <c>ChartSynthInstrumentCoordinator</c> sets the player profile's choice
    /// or a playing song tone's override. The service only holds the
    ///     request and replays it onto a rebuilt engine.
    /// </summary>
    bool TrySetChartSynthInstrument(ChartSynthInstrument instrument);

    /// <summary>What a model offers (its pickups, coil split, which positions are
    /// real recordings, whether its packs are installed), or null when this
    /// engine build does not know it. Reads the voice directory: not per frame.</summary>
    ChartSynthModelInfo? GetChartSynthModelInfo(ChartSynthModel model);
}

/// <summary>The built-in voices' instrument models. Ordinals mirror
/// <c>DCA_CHART_MODEL_*</c>; below 16 guitars, from 16 basses.</summary>
public enum ChartSynthModel
{
    /// <summary>Gretsch Anniversary semi-hollow (the recorded set the guitar voice shipped with).</summary>
    SemiHollow = 0,

    /// <summary>Stratocaster, three single coils, 5-way (real recordings in every position).</summary>
    StratSss = 1,

    /// <summary>Stratocaster with a bridge humbucker, 5-way, coil split.</summary>
    StratHss = 2,

    /// <summary>Telecaster, 3-way (simulated from the Strat).</summary>
    Telecaster = 3,

    /// <summary>Les Paul, two humbuckers, 3-way, coil split (simulated from the Strat).</summary>
    LesPaul = 4,

    /// <summary>Jazz bass: neck, both, bridge.</summary>
    JazzBass = 16,

    /// <summary>Precision bass: one split-coil pickup.</summary>
    PrecisionBass = 17,

    /// <summary>PJ bass: P, P + J, J.</summary>
    PjBass = 18,

    /// <summary>Hollow-body bass (Washburn AB-95), neck pickup.</summary>
    HollowBass = 19,
}

/// <summary>Pickup-selector positions. Ordinals mirror <c>DCA_CHART_PICKUP_*</c>.</summary>
public enum ChartSynthPickup
{
    /// <summary>The neck pickup.</summary>
    Neck = 0,

    /// <summary>Neck and middle together.</summary>
    NeckMiddle = 1,

    /// <summary>The middle pickup (a P bass's split coil).</summary>
    Middle = 2,

    /// <summary>Middle and bridge together.</summary>
    MiddleBridge = 3,

    /// <summary>The bridge pickup.</summary>
    Bridge = 4,

    /// <summary>Neck and bridge together.</summary>
    NeckBridge = 5,
}

/// <summary>An instrument selection: model, pickup selector position, coil split.</summary>
/// <param name="Model">The instrument model.</param>
/// <param name="Pickup">The selector position (one the model offers).</param>
/// <param name="CoilSplit">Humbuckers as one coil; no effect on a model without one.</param>
public readonly record struct ChartSynthInstrument(ChartSynthModel Model, ChartSynthPickup Pickup, bool CoilSplit)
{
    /// <summary>True for a bass model.</summary>
    public bool IsBass => (int)Model >= 16;
}

/// <summary>What <see cref="IChartSynthEngine.GetChartSynthModelInfo" /> reports.</summary>
/// <param name="Model">The model.</param>
/// <param name="IsBass">A bass model.</param>
/// <param name="Pickups">Its selector, neck first.</param>
/// <param name="DefaultPickup">The position a fresh selection uses.</param>
/// <param name="HasCoilSplit">The coil-split switch changes something.</param>
/// <param name="IsInstalled">Every sample pack it needs is installed.</param>
/// <param name="RecordedPickups">Positions whose recordings play unconverted.</param>
/// <param name="PartlyRecordedPickups">Positions where at least one articulation set does.</param>
public sealed record ChartSynthModelInfo(
    ChartSynthModel Model, bool IsBass, IReadOnlyList<ChartSynthPickup> Pickups, ChartSynthPickup DefaultPickup,
    bool HasCoilSplit, bool IsInstalled, IReadOnlyList<ChartSynthPickup> RecordedPickups,
    IReadOnlyList<ChartSynthPickup> PartlyRecordedPickups);

/// <summary>The chart synth's voices. Ordinals mirror <c>DCA_CHART_VOICE_*</c>.</summary>
public enum ChartSynthVoice
{
    /// <summary>A plucked-string PLACEHOLDER (Karplus-Strong), kept for comparison and tests.</summary>
    Placeholder = 0,

    /// <summary>
    ///     The built-in guitar: real recordings from the sample pack
    ///     beside the native DLL, a string model when it is missing. Requesting it
    ///     plays a bass chart with <see cref="Bass" /> (<see cref="ChartSynthVoiceSelection" />).
    /// </summary>
    Guitar = 1,

    /// <summary>The built-in bass (fingerstyle, pick, ghost, slap / pop), same as <see cref="Guitar" />.</summary>
    Bass = 2,
}

/// <summary>What <see cref="IChartSynthEngine.TryGetChartSynthStatus" /> reports.</summary>
/// <param name="IsEnabled">The generator renders into the rig.</param>
/// <param name="NoteCount">Notes in the engine's table.</param>
/// <param name="NotesStarted">Note-ons since the last upload.</param>
/// <param name="ActiveVoices">Notes sounding now.</param>
/// <param name="IsClockBound">The song is bound as the clock.</param>
/// <param name="IsClockPlaying">The song clock was playing in the last block the scheduler RAN. It runs only
/// while the synth is on natively with a voice, so before an enable's push lands (or after an off) this keeps
/// its last value — false at first — whatever the transport does.</param>
/// <param name="IsVoiceLoaded">A voice exists — false once the synth is off and its voice (and sample pack) released.</param>
/// <param name="IsSampled">The voice plays real recordings.</param>
/// <param name="PackBytes">Sample-pack memory the voice holds.</param>
/// <param name="Instrument">The playing family's instrument.</param>
public readonly record struct ChartSynthStatus(
    bool IsEnabled, int NoteCount, long NotesStarted, int ActiveVoices, bool IsClockBound, bool IsClockPlaying,
    bool IsVoiceLoaded = false, bool IsSampled = false, long PackBytes = 0, ChartSynthInstrument Instrument = default);
