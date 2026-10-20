using DotCharter.Core.Domain.Events;
using DotCharter.Core.Domain.Guitar;
using DotCharter.Core.Domain.Primitives;
using DotCharter.Core.Utilities;

namespace DotCharter.Analysis.AudioToChart;

/// <summary>Where the draft's pitches come from.</summary>
public enum AudioToChartPitchStage
{
    /// <summary>The event detector's own pitch candidates.</summary>
    Detector,

    /// <summary>
    ///     The constant-Q verifier asked at each event over the arrangement's playable
    ///     range (<see cref="CqtPitchEvidence" />), falling back to the detector's
    ///     pitches where it confirms none. Needs the WHOLE clip, so only the buffer
    ///     overload of <see cref="AudioToChartAnalyzer.Analyze(ReadOnlyMemory{float}, int, IList{BeatMarker}, AudioToChartOptions?)" />
    ///     can run it.
    /// </summary>
    ConstantQ
}

public sealed class AudioToChartOptions
{
    public static readonly IReadOnlyList<int> StandardGuitarMidi = new[] { 40, 45, 50, 55, 59, 64 };
    public static readonly IReadOnlyList<int> StandardBassMidi = new[] { 28, 33, 38, 43 };

    /// <summary>Snap grid within a beat (12 = 16ths + triplets).</summary>
    public int GridDenominator { get; init; } = 12;

    /// <summary>Candidates below this confidence are dropped from the draft.</summary>
    public float MinConfidence { get; init; } = 0.10f;

    public int MaxFret { get; init; } = 24;

    /// <summary>
    ///     Lowest fret a note may be placed on — the arrangement's CAPO, because
    ///     the capo stops every string and no lower fret can sound. A non-zero
    ///     fret below it is the malformed shape inspector rule L01 /
    ///     <c>ChartExportValidator.KeyNoteBelowCapo</c> exists to flag, so the
    ///     analyzer must not mint one. At this fret the string is OPEN (its
    ///     sounding offset is <c>max(fret, capo)</c>), which is also why it costs
    ///     nothing to reach and does not move the reference hand position.
    ///     0 — no capo — is a strict no-op.
    /// </summary>
    public int MinFret { get; init; }

    /// <summary>How many simultaneous pitches of one onset become draft notes.</summary>
    public int MaxNotesPerOnset { get; init; } = 3;

    /// <summary>Open-string MIDI pitches, lowest string first. Defaults to
    /// standard 6-string guitar tuning.</summary>
    public IReadOnlyList<int> OpenStringMidi { get; init; } = StandardGuitarMidi;

    /// <summary>Where the pitches come from; the detector's own by default.</summary>
    public AudioToChartPitchStage PitchStage { get; init; } = AudioToChartPitchStage.Detector;

    /// <summary>Lowest sounding pitch this arrangement can play: its lowest open string at the capo.</summary>
    internal int LowestPlayableMidi => OpenStringMidi.Min() + MinFret;

    /// <summary>Highest sounding pitch this arrangement can play: its highest string at <see cref="MaxFret" />.</summary>
    internal int HighestPlayableMidi => OpenStringMidi.Max() + MaxFret;
}

/// <summary>A draft note candidate: the chartable note plus the raw detection
/// info a review UI needs (time, pitch, confidence).</summary>
public sealed class DraftNote
{
    public DraftNote(GuitarNote note, double timeSeconds, int midi, float frequencyHz, float confidence)
    {
        Note = note;
        TimeSeconds = timeSeconds;
        Midi = midi;
        FrequencyHz = frequencyHz;
        Confidence = confidence;
    }

    public GuitarNote Note { get; }
    public double TimeSeconds { get; }
    public int Midi { get; }
    public float FrequencyHz { get; }

    /// <summary>0..1 — how sure the analyzer is that this note was played.</summary>
    public float Confidence { get; }
}

public sealed class AudioToChartDraft
{
    public AudioToChartDraft(IReadOnlyList<DraftNote> notes) => Notes = notes;

    /// <summary>Draft candidates in time order. Never auto-committed — callers
    /// present these for accept/reject.</summary>
    public IReadOnlyList<DraftNote> Notes { get; }
}

/// <summary>
///     Offline audio → draft chart analysis: feeds mono samples through an
///     <see cref="IGuitarEventDetector"/> in bounded chunks and converts the
///     detected events into <see cref="GuitarNote"/> candidates on the given
///     beat grid, with a per-note confidence in 0..1.
///     Deterministic: the same samples, beat grid and options always produce
///     the same draft. Memory is bounded by the chunk size and the detector's
///     internal ring — the input is never buffered whole.
/// </summary>
public sealed class AudioToChartAnalyzer
{
    /// <summary>Chunk size used when slicing a contiguous buffer.</summary>
    public const int ChunkSize = 8192;

    // Trailing silence pushed after the input so the detector finalizes
    // events still waiting for their analysis lookahead (~4096 samples +
    // 80 ms decay probe).
    private const double FlushSeconds = 0.25;

    // Confidence shaping.
    private const float LegatoFactor = 0.75f;
    private const float GridPenaltyMax = 0.3f;

    private readonly Func<int, IGuitarEventDetector> _detectorFactory;

    public AudioToChartAnalyzer() : this(static rate => new JuceGuitarEventDetector(rate))
    {
    }

    internal AudioToChartAnalyzer(Func<int, IGuitarEventDetector> detectorFactory) => _detectorFactory = detectorFactory;

    /// <summary>The whole clip in memory — the one entry point that can run the
    /// <see cref="AudioToChartPitchStage.ConstantQ" /> pitch stage, which needs it.</summary>
    public AudioToChartDraft Analyze(ReadOnlyMemory<float> monoSamples, int sampleRate,
        IList<BeatMarker> beats, AudioToChartOptions? options = null)
    {
        options ??= new AudioToChartOptions();
        if (options.PitchStage != AudioToChartPitchStage.ConstantQ)
            return Analyze(SliceChunks(monoSamples), sampleRate, beats, options);

        ArgumentNullException.ThrowIfNull(beats);
        if (sampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRate));
        var events = DetectEvents(SliceChunks(monoSamples), sampleRate);
        return DraftFromEvents(events, beats, options, CqtPitchEvidence.FromWholeClip(monoSamples.Span, sampleRate));
    }

    /// <summary>Streaming entry point: chunks are consumed one at a time, so a
    /// caller can decode a long file incrementally without holding it all.</summary>
    public AudioToChartDraft Analyze(IEnumerable<ReadOnlyMemory<float>> monoChunks, int sampleRate,
        IList<BeatMarker> beats, AudioToChartOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(monoChunks);
        ArgumentNullException.ThrowIfNull(beats);
        if (sampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRate));
        options ??= new AudioToChartOptions();
        if (options.OpenStringMidi.Count == 0)
            throw new ArgumentException("OpenStringMidi must not be empty.", nameof(options));
        // Rejected here rather than inside the snap: 0 reaches Fraction's ctor as
        // a DivideByZeroException thrown out of a primitive with no zero in it,
        // and a NEGATIVE denominator is worse — Fraction normalizes the sign, so
        // every note silently lands at its own MIRROR position with no error.
        if (options.GridDenominator <= 0)
            throw new ArgumentException("GridDenominator must be positive.", nameof(options));
        // The draft is defined ON a beat grid, and with no grid there is nothing
        // to snap to: TimelineMath.TryBeatOffsetFromMs reports failure for every
        // event, SnapToGrid's failure arm returns (FractionalPosition.Zero, 0.0),
        // and a grid error of 0 is the HIGHEST confidence there is. So an empty
        // grid did not produce an empty draft — it produced the whole song piled
        // onto beat 0 at maximum confidence, one note per string, which then
        // sailed past MinConfidence ahead of every honestly-placed note. That is
        // the same collapse the clamped beat fraction would cause, arriving
        // through the other door; the caller is rejected here instead.
        if (beats.Count == 0)
            throw new ArgumentException("A beat grid is required — the draft is defined on it.", nameof(beats));
        if (options.PitchStage == AudioToChartPitchStage.ConstantQ)
        {
            throw new ArgumentException("The constant-Q pitch stage needs the whole clip: use the buffer overload.",
                nameof(options));
        }

        return BuildDraft(DetectEvents(monoChunks, sampleRate), beats, options, null);
    }

    /// <summary>
    ///     The detection half of <see cref="Analyze(IEnumerable{ReadOnlyMemory{float}}, int, IList{BeatMarker}, AudioToChartOptions?)" />:
    ///     the chunked push plus the trailing-silence flush, i.e. everything that
    ///     depends on the AUDIO and nothing that depends on the beat grid or the
    ///     arrangement. Split out so the corpus harness
    ///     (<c>AutoChartCorpusProbeTests</c>) can detect a song once and draft each
    ///     of its arrangements from the same events — the expensive half — instead
    ///     of re-running the detector per arrangement. <see cref="Analyze(IEnumerable{ReadOnlyMemory{float}}, int, IList{BeatMarker}, AudioToChartOptions?)" />
    ///     is exactly <c>DraftFromEvents(DetectEvents(…))</c>, pinned by
    ///     <c>AudioToChartAnalyzerTests.DetectThenDraft_IsTheSameDraftAsAnalyze</c>.
    /// </summary>
    internal List<GuitarAudioEvent> DetectEvents(ReadOnlyMemory<float> monoSamples, int sampleRate)
        => DetectEvents(SliceChunks(monoSamples), sampleRate);

    /// <summary>The drafting half: validates the grid and options exactly as
    /// <c>Analyze</c> does, then turns detected events into the draft.
    /// <paramref name="pitchEvidence" /> is the clip's CQT, required when the options ask
    /// for the <see cref="AudioToChartPitchStage.ConstantQ" /> stage and unused otherwise.</summary>
    internal static AudioToChartDraft DraftFromEvents(IReadOnlyList<GuitarAudioEvent> events,
        IList<BeatMarker> beats, AudioToChartOptions? options = null, CqtPitchEvidence? pitchEvidence = null)
    {
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(beats);
        options ??= new AudioToChartOptions();
        if (options.OpenStringMidi.Count == 0)
            throw new ArgumentException("OpenStringMidi must not be empty.", nameof(options));
        if (options.GridDenominator <= 0)
            throw new ArgumentException("GridDenominator must be positive.", nameof(options));
        if (beats.Count == 0)
            throw new ArgumentException("A beat grid is required — the draft is defined on it.", nameof(beats));
        if (options.PitchStage == AudioToChartPitchStage.ConstantQ && pitchEvidence == null)
            throw new ArgumentException("The constant-Q pitch stage needs the clip's CQT.", nameof(pitchEvidence));

        return BuildDraft(events, beats, options,
            options.PitchStage == AudioToChartPitchStage.ConstantQ ? pitchEvidence : null);
    }

    private List<GuitarAudioEvent> DetectEvents(IEnumerable<ReadOnlyMemory<float>> monoChunks, int sampleRate)
    {
        if (sampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRate));
        var detector = _detectorFactory(sampleRate);
        var events = new List<GuitarAudioEvent>();

        foreach (var chunk in monoChunks)
        {
            var span = chunk.Span;
            var offset = 0;
            while (offset < span.Length)
            {
                var n = Math.Min(ChunkSize, span.Length - offset);
                events.AddRange(detector.Push(span.Slice(offset, n)));
                offset += n;
            }
        }

        // Flush: silence long enough to finalize every pending event.
        var flush = new float[Math.Min(ChunkSize, Math.Max(1, (int)(FlushSeconds * sampleRate)))];
        var flushRemaining = (int)(FlushSeconds * sampleRate);
        while (flushRemaining > 0)
        {
            var n = Math.Min(flush.Length, flushRemaining);
            events.AddRange(detector.Push(flush.AsSpan(0, n)));
            flushRemaining -= n;
        }

        return events;
    }

    private static IEnumerable<ReadOnlyMemory<float>> SliceChunks(ReadOnlyMemory<float> samples)
    {
        var offset = 0;
        while (offset < samples.Length)
        {
            var n = Math.Min(ChunkSize, samples.Length - offset);
            yield return samples.Slice(offset, n);
            offset += n;
        }
    }

    private static AudioToChartDraft BuildDraft(IReadOnlyList<GuitarAudioEvent> events, IList<BeatMarker> beats,
        AudioToChartOptions options, CqtPitchEvidence? pitchEvidence)
    {
        var notes = new List<DraftNote>();
        var lastFretByString = new int[options.OpenStringMidi.Count];
        Array.Fill(lastFretByString, -1);
        var referenceFret = 3; // neutral hand position before the first fretted note

        // The used-string mask belongs to the GRID SLOT, not to the event. Two
        // onsets 20 ms apart snap to the SAME FractionalPosition, and a per-event
        // mask let both take the same string — two DraftNotes at one
        // position+string, which the GP path calls physically impossible and
        // scrubs with GpPostProcessor.RemoveDuplicateSounds. The value is the
        // index into `notes` of the sound holding each string at that position,
        // so a later, HIGHER-confidence candidate can take the slot over.
        // SortedDictionary rather than Dictionary on purpose: nothing here
        // enumerates it today, and AnalysisDeterminismTests must stay immune to
        // anyone who later does.
        var slotOwners = new SortedDictionary<FractionalPosition, int[]>();

        foreach (var ev in events)
        {
            // A non-finite number from the detector must never reach the draft.
            // NaN loses EVERY comparison, so the MinConfidence gate below — the
            // one thing that drops a bad candidate — is false for it and the note
            // survives; and an infinite Strength is worse still, because the
            // Math.Clamp that shapes confidence turns +inf into 1.0, i.e. the
            // HIGHEST confidence there is. Either way one bad analysis frame
            // outranks every honestly-scored note in the draft, which is exactly
            // the failure a clamped grid fraction would produce.
            if (!double.IsFinite(ev.TimeSeconds) || !float.IsFinite(ev.Strength)) continue;

            var (position, gridError) = SnapToGrid(ev.TimeSeconds * 1000.0, beats, options.GridDenominator);
            var pitches = ev.Pitches;
            if (pitchEvidence != null &&
                pitchEvidence.PitchesAt(ev.TimeSeconds, options.LowestPlayableMidi, options.HighestPlayableMidi,
                    options.MaxNotesPerOnset) is { Count: > 0 } confirmed)
            {
                pitches = confirmed;
            }

            var takeCount = Math.Min(options.MaxNotesPerOnset, pitches.Count);

            if (!slotOwners.TryGetValue(position, out var ownerByString))
            {
                ownerByString = new int[options.OpenStringMidi.Count];
                Array.Fill(ownerByString, -1);
                slotOwners[position] = ownerByString;
            }

            for (var i = 0; i < takeCount; i++)
            {
                var pitch = pitches[i];
                if (!float.IsFinite(pitch.Salience) || !float.IsFinite(pitch.FrequencyHz)) continue;

                // The confidence does not depend on the string, so it is scored
                // BEFORE assignment — the collision arm below needs it to decide
                // who keeps the slot.
                var confidence = ev.Strength * Math.Clamp(pitch.Salience, 0f, 1f);
                confidence *= 1f - GridPenaltyMax * (float)(gridError * 2.0);
                if (ev.IsLegato) confidence *= LegatoFactor;
                confidence = Math.Clamp(confidence, 0f, 1f);
                // Written as `!(x >= min)` on purpose: `x < min` is FALSE for NaN,
                // so the naive spelling admits exactly the value it must reject.
                if (!(confidence >= options.MinConfidence)) continue;

                var usedStrings = UsedStringMask(ownerByString);
                if (!TryAssignString(pitch.Midi, options, usedStrings, referenceFret,
                        out var stringIndex, out var fret))
                {
                    // Every string this pitch can play is already taken at this
                    // grid slot. Ask again with a clear mask to learn WHICH one
                    // it wanted, and take it over only on strictly higher
                    // confidence — otherwise the incumbent stands.
                    if (!TryAssignString(pitch.Midi, options, 0, referenceFret, out stringIndex, out fret))
                        continue;

                    var incumbent = ownerByString[stringIndex];
                    if (incumbent < 0 || confidence <= notes[incumbent].Confidence) continue;

                    var replacement = new GuitarNote
                    {
                        Position = position,
                        StringIndex = stringIndex,
                        Fret = fret
                    };
                    notes[incumbent] =
                        new DraftNote(replacement, ev.TimeSeconds, pitch.Midi, pitch.FrequencyHz, confidence);
                    lastFretByString[stringIndex] = fret;
                    if (fret > options.MinFret) referenceFret = fret;
                    continue;
                }

                var note = new GuitarNote
                {
                    Position = position,
                    StringIndex = stringIndex,
                    Fret = fret
                };

                if (ev.IsLegato && lastFretByString[stringIndex] >= 0 && i == 0)
                    note.Hopo = fret >= lastFretByString[stringIndex] ? HopoType.HammerOn : HopoType.PullOff;

                ownerByString[stringIndex] = notes.Count;
                lastFretByString[stringIndex] = fret;
                // An open (or capo-stopped) string does not move the fret hand.
                if (fret > options.MinFret) referenceFret = fret;

                notes.Add(new DraftNote(note, ev.TimeSeconds, pitch.Midi, pitch.FrequencyHz, confidence));
            }
        }

        return new AudioToChartDraft(notes);
    }

    /// <summary>How far outside the beat grid an event may be placed, in grid
    /// slots. Real onsets come from decoded audio, so this only bounds a
    /// nonsense timestamp — it must never truncate an ordinary song's tail.</summary>
    private const double MaxGridSlots = 12.0 * 1_000_000.0;

    /// <summary>Snap a time to the beat grid. Returns the snapped position and
    /// the snap error as a fraction of one grid step (0 = exactly on grid,
    /// 0.5 = half-way between two grid slots).
    /// <para>
    /// The ms→position conversion is <see cref="TimelineMath" />'s, not a local
    /// one. A hand-rolled version clamped the beat fraction into <c>[0, 1]</c>,
    /// which past the last beat mapped EVERY event onto one and the same
    /// position — and, since a clamped fraction lands exactly on a slot,
    /// awarded all of them a grid error of 0, i.e. the HIGHEST confidence, so
    /// they survived <see cref="AudioToChartOptions.MinConfidence" />. It needs
    /// no exotic input to reach: a beat map generated at one tempo and then
    /// re-tempoed covers only part of the song, because recomputing beat TIMES
    /// never adds beats.
    /// </para></summary>
    private static (FractionalPosition Position, double GridError) SnapToGrid(double timeMs,
        IList<BeatMarker> beats, int gridDenominator)
    {
        if (!TimelineMath.TryBeatOffsetFromMs(timeMs, beats, out var beatIdx, out var offsetBeats))
            return (FractionalPosition.Zero, 0.0);

        // Un-clamped, so an event past the grid is priced by how far it really
        // sits from the extrapolated slot rather than being handed a free 0.
        var scaled = Math.Clamp(offsetBeats * gridDenominator, -MaxGridSlots, MaxGridSlots);
        var slot = Math.Round(scaled);
        var gridError = Math.Abs(scaled - slot);

        // A slot outside [0, den) is how extrapolation is expressed: the
        // FractionalPosition ctor carries the whole (and negative) beats into
        // BeatId and leaves Fraction in [0, 1).
        return (new FractionalPosition(beatIdx, new Fraction((long)slot, gridDenominator)), gridError);
    }

    /// <summary>The bitmask <see cref="TryAssignString" /> wants, from the
    /// per-slot owner table (-1 = the string is free at this position).</summary>
    private static int UsedStringMask(int[] ownerByString)
    {
        var mask = 0;
        for (var s = 0; s < ownerByString.Length; s++)
        {
            if (ownerByString[s] >= 0)
                mask |= 1 << s;
        }

        return mask;
    }

    private static bool TryAssignString(int midi, AudioToChartOptions options, int usedStrings,
        int referenceFret, out int stringIndex, out int fret)
    {
        stringIndex = -1;
        fret = -1;
        var bestCost = int.MaxValue;

        for (var s = 0; s < options.OpenStringMidi.Count; s++)
        {
            if ((usedStrings & (1 << s)) != 0) continue;
            var f = midi - options.OpenStringMidi[s];
            // MinFret is the capo: no lower fret on this string can sound, and
            // writing one would mint the malformed shape rule L01 flags.
            if (f < options.MinFret || f > options.MaxFret) continue;

            // Stay near the current hand position; playing above the 12th
            // fret when a lower voicing exists is penalized. At MinFret the
            // string is OPEN (or capo-stopped), so it costs nothing.
            var cost = (f == options.MinFret ? 0 : Math.Abs(f - referenceFret)) + (f > 12 ? 3 : 0);
            if (cost < bestCost || (cost == bestCost && f < fret))
            {
                bestCost = cost;
                stringIndex = s;
                fret = f;
            }
        }

        return stringIndex >= 0;
    }
}
