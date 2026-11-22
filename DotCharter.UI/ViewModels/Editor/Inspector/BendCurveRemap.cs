namespace DotCharter.UI.ViewModels.Editor.Inspector;

/// <summary>
///     A bend curve written on one sound and copied onto others. Bend points hold absolute chart
///     positions, so each target gets the curve moved onto ITS onset and stretched over ITS sustain.
/// </summary>
internal static class BendCurveRemap
{
    /// <summary>Snap for a ratio whose exact fraction does not fit an int (odd imported sustains).</summary>
    private const int FallbackGrid = 960;

    /// <summary>
    ///     The curve moved from the source's span onto the target's: each point keeps its fraction of
    ///     the sustain, clamped into the target span. The source itself (same onset and sustain) keeps
    ///     the curve exactly as drawn. A zero-length source moves by the onset only. A
    ///     zero-length target holds ONE point, the curve's peak step (largest absolute, first on ties),
    ///     on its onset: several points at one position are an overlapping bend.
    ///     Always new <see cref="BendValue" /> instances.
    /// </summary>
    internal static IReadOnlyList<BendValue> Remap(IReadOnlyList<BendValue> values, FractionalPosition sourceOnset,
        FractionalPosition sourceSustain, FractionalPosition targetOnset, FractionalPosition targetSustain)
    {
        if (values.Count == 0) return [];
        if (targetOnset == sourceOnset && targetSustain == sourceSustain)
            return values.Select(v => new BendValue(v.Position, v.Step)).ToArray();
        if (targetSustain <= FractionalPosition.Zero) return [new BendValue(targetOnset, PeakStep(values))];
        return values
            .Select(v => new BendValue(Place(v.Position, sourceOnset, sourceSustain, targetOnset, targetSustain), v.Step))
            .ToArray();
    }

    private static double PeakStep(IReadOnlyList<BendValue> values) =>
        values.Aggregate((best, v) => Math.Abs(v.Step) > Math.Abs(best.Step) ? v : best).Step;

    /// <summary>Exact when the fractions fit an int; otherwise in doubles, which cannot throw.</summary>
    private static FractionalPosition Place(FractionalPosition position, FractionalPosition sourceOnset,
        FractionalPosition sourceSustain, FractionalPosition targetOnset, FractionalPosition targetSustain)
    {
        try
        {
            return targetOnset + Offset(Length(position - sourceOnset), Length(sourceSustain), Length(targetSustain));
        }
        catch (ArgumentOutOfRangeException)
        {
            return Approximate(position, sourceOnset, sourceSustain, targetOnset, targetSustain);
        }
    }

    private static Fraction Offset(Fraction offset, Fraction sourceLength, Fraction targetLength)
    {
        if (sourceLength <= Fraction.Zero) return Clamp(offset, targetLength);
        var t = offset / sourceLength;
        if (t <= Fraction.Zero) return Fraction.Zero;
        return t >= Fraction.One ? targetLength : t * targetLength;
    }

    /// <summary>
    ///     The same placement in doubles, snapped to a 1/<see cref="FallbackGrid" /> beat grid and
    ///     clamped to [onset, onset + sustain]; builds no fraction that could overflow.
    /// </summary>
    private static FractionalPosition Approximate(FractionalPosition position, FractionalPosition sourceOnset,
        FractionalPosition sourceSustain, FractionalPosition targetOnset, FractionalPosition targetSustain)
    {
        var sourceLength = InBeats(sourceSustain);
        var targetLength = InBeats(targetSustain);
        var offset = InBeats(position) - InBeats(sourceOnset);
        var scaled = sourceLength <= 0 ? offset : Math.Clamp(offset / sourceLength, 0, 1) * targetLength;
        if (scaled <= 0) return targetOnset;
        if (scaled >= targetLength) return targetOnset + targetSustain;
        var ticks = (long)Math.Round((InBeats(targetOnset) + scaled) * FallbackGrid);
        var beat = Math.DivRem(ticks, FallbackGrid, out var tick);
        var snapped = new FractionalPosition((int)beat, new Fraction(tick, FallbackGrid));
        var end = targetOnset + targetSustain;
        return snapped < targetOnset ? targetOnset : snapped > end ? end : snapped;
    }

    private static Fraction Clamp(Fraction offset, Fraction length) =>
        offset <= Fraction.Zero ? Fraction.Zero : offset >= length ? length : offset;

    private static Fraction Length(FractionalPosition span) => span.Fraction + span.BeatId;

    private static double InBeats(FractionalPosition p) => p.BeatId + p.Fraction.DoubleValue;

    /// <summary>
    ///     What one chord-bend commit does to each chord it reaches: replaces the curve on the
    ///     string at <paramref name="stringIndex" />, and on no other; a chord without that string
    ///     is left as it is (<c>ChordBendApplyToTests</c>).
    /// </summary>
    internal static Action<Chord> ChordWrite(int stringIndex, IReadOnlyList<BendValue> values,
        FractionalPosition sourceOnset, FractionalPosition sourceSustain) =>
        chord =>
        {
            var note = chord.ChordNotes.FirstOrDefault(n => n.StringIndex == stringIndex);
            if (note == null) return;
            Replace(note.BendValues, Remap(values, sourceOnset, sourceSustain, chord.Position, note.Sustain));
        };

    /// <summary>What one note-bend commit does to each selected note.</summary>
    internal static Action<GuitarNote> NoteWrite(IReadOnlyList<BendValue> values, FractionalPosition sourceOnset,
        FractionalPosition sourceSustain) =>
        note => Replace(note.BendValues, Remap(values, sourceOnset, sourceSustain, note.Position, note.Sustain));

    private static void Replace(ICollection<BendValue> target, IReadOnlyList<BendValue> values)
    {
        target.Clear();
        foreach (var v in values) target.Add(v);
    }
}
