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
