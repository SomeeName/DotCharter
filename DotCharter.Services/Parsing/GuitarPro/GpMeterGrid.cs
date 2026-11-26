using DotCharter.Core.Linking;
using Arrangement = DotCharter.Core.Domain.Project.Arrangement;
using ChartProject = DotCharter.Core.Domain.Project.ChartProject;

namespace DotCharter.Services.Parsing.GuitarPro;

/// <summary>
///     Moves an import from the parsers' QUARTER-note positions onto the chart's beat grid.
///     <para>
///     Both GP parsers count positions in quarter notes: a bar advances by
///     <c>numerator × 4 / denominator</c> and every rhythm is a quarter-note fraction, which is
///     what the sustain and grace heuristics in <see cref="GpPostProcessor" /> are written
///     against. The chart counts a position in BEATS, and <see cref="GpBeatMapBuilder" /> gives
///     each bar one beat per time-signature unit — a 6/8 bar is six eighth-note beats. In any
///     bar whose denominator is not 4 the two disagree: left in quarters, the eighth notes of a
///     6/8 bar sit as if each beat were a quarter, so they play twice as fast and the rest of
///     the song drifts into the wrong bars (the corpus found it where Guitar Pro's repeat signs
///     say two bars are one written bar and the import disagreed).
///     </para>
///     <para>
///     This pass runs last, once per import, and is the identity for a score in x/4
///     throughout. With time-signature import OFF the builder lays four quarter beats per bar,
///     so quarter positions already are beats and nothing moves.
///     </para>
/// </summary>
internal static class GpMeterGrid
{
    /// <summary>Converts every position of <paramref name="result" />'s arrangements from
    /// quarter notes to beats of the time-signature grid its metadata describes.</summary>
    public static void ToBeatGrid(GpImportResult result, bool importTimeSignatures)
    {
        var bars = result.Metadata.Bars;
        if (!importTimeSignatures || bars.All(b => Denominator(b) == 4)) return;

        var map = QuarterToBeat(bars);
        foreach (var arrangement in result.Arrangements) Remap(arrangement, map);
    }

    /// <summary>The piecewise-linear map from a quarter-note position to a beat position:
    /// inside bar <c>i</c> one quarter is <c>denominator / 4</c> beats. Before the first bar
    /// and past the last, the nearest bar's rate extends.</summary>
    internal static Func<FractionalPosition, FractionalPosition> QuarterToBeat(IReadOnlyList<GpBarInfo> bars)
    {
        var quarterStarts = new Fraction[bars.Count];
        var beatStarts = new int[bars.Count];
        var rates = new Fraction[bars.Count];
        var (quarter, beat) = (Fraction.Zero, 0);
        for (var i = 0; i < bars.Count; i++)
        {
            var beats = bars[i].BeatsInMeasure > 0 ? bars[i].BeatsInMeasure : 4;
            var denominator = Denominator(bars[i]);
            quarterStarts[i] = quarter;
            beatStarts[i] = beat;
            rates[i] = new Fraction(denominator, 4);
            quarter += new Fraction(beats * 4, denominator);
            beat += beats;
        }

        return position =>
        {
            if (bars.Count == 0) return position;
            var q = position.BeatId + position.Fraction;
            var bar = BarAt(quarterStarts, q);
            return new FractionalPosition(beatStarts[bar], (q - quarterStarts[bar]) * rates[bar]);
        };
    }

    /// <summary>
    ///     Puts an import onto a beat map it did NOT build (the tempo map was kept, or is
    ///     locked): bar <c>i</c> of the score lands on measure <c>i</c> of <paramref name="beats" />.
    ///     <para>
    ///     Inside a bar a position keeps its FRACTION OF THE BAR, so when the score's beats per bar
    ///     differ from the map's (a 6/8 score on a 3/4 map, a 4/4 bar the user re-barred to 3/4) the
    ///     bar's notes are scaled into the map's bar instead of spilling over its barline, and every
    ///     later bar still starts on its own downbeat. A map's last measure, which the grid may cut
    ///     short, counts at its signature's length. Score bars past the map's last measure continue
    ///     beat for beat after it. The identity when the score's bars match the map's measures.
    ///     </para>
    ///     <paramref name="importTimeSignatures" /> is the flag the score was parsed with: off, its
    ///     positions are still quarter notes (see <see cref="ToBeatGrid" />).
    /// </summary>
    public static void OntoMeasures(IEnumerable<Arrangement> arrangements, IReadOnlyList<GpBarInfo> bars,
        IList<BeatMarker> beats, bool importTimeSignatures)
    {
        var measures = MeasureGrid.Build(beats);
        if (bars.Count == 0 || measures.Count == 0) return;
        var map = BarToMeasure(bars, measures, importTimeSignatures);
        foreach (var arrangement in arrangements) Remap(arrangement, map);
    }

    /// <summary>The piecewise-linear map of <see cref="OntoMeasures" />.</summary>
    internal static Func<FractionalPosition, FractionalPosition> BarToMeasure(IReadOnlyList<GpBarInfo> bars,
        IReadOnlyList<MeasureSpan> measures, bool importTimeSignatures)
    {
        var starts = new Fraction[bars.Count];
        var lengths = new Fraction[bars.Count];
        var start = Fraction.Zero;
        for (var i = 0; i < bars.Count; i++)
        {
            var beats = bars[i].BeatsInMeasure > 0 ? bars[i].BeatsInMeasure : 4;
            starts[i] = start;
            lengths[i] = importTimeSignatures ? new Fraction(beats, 1) : new Fraction(beats * 4, Denominator(bars[i]));
            start += lengths[i];
        }

        var last = measures[^1];
        return position =>
        {
            var q = position.BeatId + position.Fraction;
            var bar = BarAt(starts, q);
            if (bar >= measures.Count)
                return new FractionalPosition(Math.Max(last.EndBeat, last.StartBeat + last.BeatsInMeasure), q - starts[measures.Count]);
            var measure = measures[bar];
            var measureBeats = bar == measures.Count - 1 ? Math.Max(measure.BeatCount, measure.BeatsInMeasure) : measure.BeatCount;
            return new FractionalPosition(measure.StartBeat, (q - starts[bar]) * new Fraction(measureBeats, 1) / lengths[bar]);
        };
    }

    private static int Denominator(GpBarInfo bar) => bar.NoteDenominator > 0 ? bar.NoteDenominator : 4;

    /// <summary>The last bar starting at or before <paramref name="q" /> (bar 0 before the start).</summary>
    private static int BarAt(Fraction[] starts, Fraction q)
    {
        var (low, high) = (0, starts.Length - 1);
        while (low < high)
        {
            var mid = (low + high + 1) / 2;
            if (starts[mid] <= q) low = mid;
            else high = mid - 1;
        }

        return low;
    }

    private static void Remap(Arrangement arrangement, Func<FractionalPosition, FractionalPosition> map)
    {
        // Sustains are RELATIVE spans, which ChartProjectPositions deliberately leaves alone:
        // re-measure each between its mapped onset and its mapped end first, while both are
        // still in quarter notes. A sustain that crosses into a bar of another meter changes rate there.
        var levels = arrangement.Levels.Select(l => (l.Notes, l.Chords))
            .Concat(arrangement.NotationVoices.Select(v => (v.Notes, v.Chords)));
        foreach (var (notes, chords) in levels)
        {
            foreach (var note in notes) note.Sustain = Span(note.Position, note.Sustain, map);
            foreach (var chord in chords)
            {
                foreach (var chordNote in chord.ChordNotes)
                    chordNote.Sustain = Span(chord.Position, chordNote.Sustain, map);
            }
        }

        var shell = new ChartProject();
        shell.Arrangements.Add(arrangement);
        ChartProjectPositions.Remap(shell, map);
    }

    private static FractionalPosition Span(FractionalPosition start, FractionalPosition length,
        Func<FractionalPosition, FractionalPosition> map) =>
        length == FractionalPosition.Zero ? length : map(start + length) - map(start);
}
