namespace DotCharter.Tone.Audio.ChartSynth;

/// <summary>
///     Keeps the engine's chart-synth notes in step with the chart: called from the
///     transport tick, it fingerprints the active arrangement at most every
///     <see cref="CheckIntervalMs" /> and re-uploads only when the fingerprint moved
///     (an edit, an arrangement or level switch, a tuning change, the song's playing
///     style) or the engine no
///     longer holds it (a rebuilt engine replays the last upload itself, so this
///     is only a backstop). The upload is the ONE time the chart crosses to the
///     engine; the notes are then scheduled natively against the song clock — no
///     per-tick note traffic. The clock is injected, so the gate pins the cadence
///     without sleeping.
///     <para>
///         <b>One upload at a time.</b> The position timer that ticks this is a
///         periodic <c>System.Threading.Timer</c>, which runs a late callback
///         concurrently with the next one. An upload decodes the voice
///         zones the new chart can play before it publishes the notes, so enabling
///         a large pack takes hundreds of milliseconds; a tick that arrives while
///         an upload is still running returns at once instead of starting a
///         second one beside it.
///     </para>
/// </summary>
public sealed class ChartSynthFeeder
{
    /// <summary>How often a running feeder looks for a chart change.</summary>
    public const long CheckIntervalMs = 250;

    private long _lastCheckMs;
    private bool _checkedOnce;
    private int _ticking;

    /// <summary>Uploads made by this feeder (diagnostics and tests).</summary>
    public int Uploads { get; private set; }

    /// <summary>
    ///     One tick. Returns true when it uploaded. Does nothing while the synth is
    ///     off or there is no arrangement, and forgets its cadence then, so turning
    ///     the synth on uploads on the very next tick.
    /// </summary>
    public bool Tick(long nowMs, IChartSynthEngine engine, Arrangement? arrangement, IList<GuitarNote> notes,
        IList<Chord> chords, IList<BeatMarker> beats, IList<HandShape>? handShapes, double bpm,
        ChartSynthPlayStyle? style = null)
    {
        ArgumentNullException.ThrowIfNull(engine);
        if (Interlocked.CompareExchange(ref _ticking, 1, 0) != 0) return false;
        try
        {
            return TickCore(nowMs, engine, arrangement, notes, chords, beats, handShapes, bpm, style);
        }
        finally
        {
            Volatile.Write(ref _ticking, 0);
        }
    }

    private bool TickCore(long nowMs, IChartSynthEngine engine, Arrangement? arrangement, IList<GuitarNote> notes,
        IList<Chord> chords, IList<BeatMarker> beats, IList<HandShape>? handShapes, double bpm,
        ChartSynthPlayStyle? style)
    {
        if (!engine.IsChartSynthEnabled || arrangement == null)
        {
            _checkedOnce = false;
            return false;
        }

        if (_checkedOnce && nowMs - _lastCheckMs < CheckIntervalMs) return false;
        _checkedOnce = true;
        _lastCheckMs = nowMs;

        var fingerprint = ChartNoteEventBuilder.Fingerprint(arrangement, notes, chords, beats, handShapes, bpm, style);
        if (engine.UploadedChartFingerprint == fingerprint) return false;

        var upload = ChartNoteEventBuilder.Build(arrangement, notes, chords, beats, handShapes, bpm, style);
        if (!engine.UploadChartNotes(upload)) return false;
        Uploads++;
        return true;
    }
}
