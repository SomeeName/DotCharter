namespace DotCharter.Services.Parsing.Midi;

/// <summary>
/// Piecewise-linear tick→time conversion built from a MIDI file's tempo
/// events, plus a beat-grid suggestion (one <see cref="BeatMarker"/> per
/// beat of each time signature) for the chart editor. Before the first tempo event the SMF
/// default of 500 000 µs per quarter note (120 BPM) applies.
/// </summary>
public sealed class MidiTempoMap
{
    private const int DefaultMicrosecondsPerQuarterNote = 500_000;

    /// <summary>Upper bound on the markers <see cref="BuildBeatGrid" /> lays.</summary>
    private const int MaxGridBeats = 100_000;

    private readonly int _ticksPerQuarterNote;
    private readonly List<Segment> _segments;

    public MidiTempoMap(int ticksPerQuarterNote, IEnumerable<MidiTempoEvent> tempoEvents)
    {
        if (ticksPerQuarterNote <= 0) throw new ArgumentOutOfRangeException(nameof(ticksPerQuarterNote));
        _ticksPerQuarterNote = ticksPerQuarterNote;

        _segments =
        [
            new(0, 0.0, DefaultMicrosecondsPerQuarterNote)
        ];

        foreach (var tempo in tempoEvents.Where(t => t.Ticks >= 0 && t.MicrosecondsPerQuarterNote > 0)
                     .OrderBy(t => t.Ticks))
        {
            var previous = _segments[^1];
            if (tempo.Ticks == previous.StartTicks)
            {
                // Replaces the tempo at the same tick (last one wins).
                _segments[^1] = previous with { MicrosecondsPerQuarterNote = tempo.MicrosecondsPerQuarterNote };
                continue;
            }

            var startMs = previous.StartMs + TicksToMsWithin(previous, tempo.Ticks - previous.StartTicks);
            _segments.Add(new Segment(tempo.Ticks, startMs, tempo.MicrosecondsPerQuarterNote));
        }
    }

    public double TicksToMs(long ticks)
    {
        if (ticks <= 0) return 0.0;
        var segment = SegmentAt(ticks);
        return segment.StartMs + TicksToMsWithin(segment, ticks - segment.StartTicks);
    }

    public int MicrosecondsPerQuarterNoteAt(long ticks) => SegmentAt(ticks).MicrosecondsPerQuarterNote;

    /// <summary>The <see cref="MidiBeatGrid.Beats" /> of <see cref="BuildBeatGrid" />.</summary>
    public List<BeatMarker> SuggestBeatGrid(long endTicks,
        IReadOnlyList<MidiTimeSignatureEvent>? timeSignatures = null) =>
        BuildBeatGrid(endTicks, timeSignatures).Beats;

    /// <summary>
    /// A beat grid from tick 0 through at least <paramref name="endTicks"/>
    /// (always at least one beat past it, minimum two beats so the grid is
    /// usable), laid in each time signature's OWN beat unit: one marker per
    /// eighth note in 7/8, per quarter in 4/4, per half in 2/2 — the shape the
    /// Guitar Pro import (<c>GpBeatMapBuilder</c>) lays and every other reader of
    /// <see cref="BeatMarker.BeatsInMeasure" /> assumes: it is the number of
    /// markers in the bar, and <see cref="BeatMarker.NoteDenominator" /> is
    /// the note value of one marker.
    /// <para>
    /// The grid is not one marker per QUARTER whatever the meter, with the
    /// bar rounded to whole quarters: 7/8 would get a bar line every 4 quarters
    /// instead of 3.5 and 5/8 every 2 instead of 2.5, so the bar lines would drift
    /// half a beat per bar and every beat would claim /4. A quarter grid
    /// cannot put a bar line half-way through a beat.
    /// </para>
    /// <para>
    /// A time signature that falls INSIDE a beat (a malformed file, or one whose
    /// previous bar was cut short) cuts that beat short, so the change always
    /// starts on a marker and a bar line.
    /// <see cref="BeatMarker.Bpm" /> stays the quarter-note tempo at the beat.
    /// </para>
    /// </summary>
    public MidiBeatGrid BuildBeatGrid(long endTicks,
        IReadOnlyList<MidiTimeSignatureEvent>? timeSignatures = null)
    {
        var signatures = (timeSignatures ?? Array.Empty<MidiTimeSignatureEvent>())
            .OrderBy(t => t.Ticks)
            .ToList();

        var beats = new List<BeatMarker>();
        var beatTicks = new List<long>();
        var beatsInMeasure = 4;
        var denominator = 4;
        var signatureStartTicks = 0L;
        var beatInSignature = 0L;
        var beatsSinceMeasureStart = 0;
        var signatureIndex = 0;
        var tick = 0L;

        while (beats.Count < MaxGridBeats)
        {
            while (signatureIndex < signatures.Count && signatures[signatureIndex].Ticks <= tick)
            {
                var sig = signatures[signatureIndex];
                beatsInMeasure = Math.Max(1, sig.Numerator);
                denominator = BeatUnitDenominator(sig.Denominator);
                signatureStartTicks = tick;
                beatInSignature = 0;
                beatsSinceMeasureStart = 0;
                signatureIndex++;
            }

            beats.Add(new BeatMarker
            {
                Time = TicksToMs(tick),
                Bpm = 60_000_000.0 / MicrosecondsPerQuarterNoteAt(tick),
                IsMeasure = beatsSinceMeasureStart == 0,
                BeatsInMeasure = beatsInMeasure,
                NoteDenominator = denominator
            });
            beatTicks.Add(tick);
            if (tick > endTicks && beats.Count >= 2) break;

            beatsSinceMeasureStart = (beatsSinceMeasureStart + 1) % beatsInMeasure;
            beatInSignature++;
            // From the signature's start, not from the previous beat, so a beat
            // unit that is not a whole number of ticks does not accumulate drift.
            var next = signatureStartTicks + beatInSignature * 4L * _ticksPerQuarterNote / denominator;
            if (signatureIndex < signatures.Count && signatures[signatureIndex].Ticks > tick &&
                signatures[signatureIndex].Ticks < next)
            {
                next = signatures[signatureIndex].Ticks;
            }

            tick = next;
        }

        return new MidiBeatGrid(beats, beatTicks);
    }

    /// <summary>
    /// The signature's denominator, halved only while one beat of it would be
    /// shorter than a tick — a 1/64 beat at 8 ticks per quarter has no tick to
    /// sit on, and two markers on one tick would make a zero-length beat.
    /// </summary>
    private int BeatUnitDenominator(int signatureDenominator)
    {
        var denominator = Math.Max(1, signatureDenominator);
        while (denominator > 1 && 4L * _ticksPerQuarterNote < denominator) denominator /= 2;
        return denominator;
    }

    /// <summary>
    ///     The last segment starting at or before <paramref name="ticks" />.
    ///     <para>
    ///     BINARY, not a backward linear scan.
    ///     <c>BuildBeatGrid</c> calls it twice per beat for up to 100 000
    ///     beats and <c>MidiToDraftConverter</c> once per note, while
    ///     <c>_segments</c> grows one entry per tempo meta event — bounded only
    ///     by the file size, roughly one per ten bytes. A 10 MB <c>.mid</c> full
    ///     of tempo events was ~2×10^11 comparisons: the import hung on a file
    ///     that PARSES in under a second.
    ///     </para>
    ///     <para>
    ///     The ctor appends in ascending tick order and collapses a repeat at
    ///     the same tick, so the list is strictly increasing and a plain upper
    ///     bound gives the identical answer.
    ///     </para>
    /// </summary>
    private Segment SegmentAt(long ticks)
    {
        var lo = 1;
        var hi = _segments.Count; // first index known to start AFTER `ticks`
        while (lo < hi)
        {
            var mid = lo + ((hi - lo) >> 1);
            if (_segments[mid].StartTicks <= ticks) lo = mid + 1;
            else hi = mid;
        }

        return _segments[lo - 1];
    }

    private double TicksToMsWithin(Segment segment, long ticksIntoSegment) => ticksIntoSegment * (segment.MicrosecondsPerQuarterNote / 1000.0) / _ticksPerQuarterNote;

    private readonly record struct Segment(long StartTicks, double StartMs, int MicrosecondsPerQuarterNote);
}

/// <summary>
/// The beat grid <see cref="MidiTempoMap.BuildBeatGrid" /> laid, with each
/// marker's MIDI tick, so a tick lands on it as an exact rational.
/// </summary>
public sealed class MidiBeatGrid
{
    private readonly List<long> _beatTicks;

    internal MidiBeatGrid(List<BeatMarker> beats, List<long> beatTicks)
    {
        Beats = beats;
        _beatTicks = beatTicks;
    }

    public List<BeatMarker> Beats { get; }

    /// <summary>
    /// <paramref name="ticks" /> as a position on <see cref="Beats" />: the beat
    /// it falls in plus the exact fraction of that beat's ticks. Past the last
    /// marker it extrapolates with the last beat's length; a negative tick is
    /// position zero.
    /// </summary>
    public FractionalPosition PositionFromTicks(long ticks)
    {
        if (ticks <= 0 || _beatTicks.Count < 2) return FractionalPosition.Zero;

        var index = _beatTicks.BinarySearch(ticks);
        if (index < 0) index = ~index - 1;

        long beatId;
        long remainder;
        long span;
        if (index < _beatTicks.Count - 1)
        {
            beatId = index;
            remainder = ticks - _beatTicks[index];
            span = _beatTicks[index + 1] - _beatTicks[index];
        }
        else
        {
            span = _beatTicks[^1] - _beatTicks[^2];
            var past = ticks - _beatTicks[^1];
            beatId = _beatTicks.Count - 1 + past / span;
            remainder = past % span;
        }

        return beatId > int.MaxValue
            ? new FractionalPosition(int.MaxValue)
            : new FractionalPosition((int)beatId, new Fraction(remainder, span));
    }
}
