namespace DotCharter.Services.Parsing.GuitarPro;

/// <summary>
///     Pure conversions between an AUTHORED score rhythm — a base note value plus
///     augmentation dots and an optional tuplet — and the single quarter-note-unit
///     <see cref="Fraction"/> that the notation-hint model stores
///     (<see cref="Core.Domain.Guitar.GuitarNote.NotationDuration"/> /
///     <see cref="Core.Domain.Guitar.Chord.NotationDuration"/>).
///     <para>
///     <b>Fold</b> (import direction) is what every score importer already does
///     inline — the GP legacy binary reader and the gpif <c>Rhythms</c> parser
///     both fold NoteValue + dots + tuplet into one fraction. <b>Unfold</b>
///     (export direction) is the inverse the Guitar Pro exporter uses to write a
///     beat's duration / dots / tuplet from the stored hint. Both directions are
///     covered by <c>GpRhythmFractionTests</c>.
///     </para>
///     <para>
///     Ambiguity, by construction: a single-dotted triplet is
///     <c>base × 3/2 × 2/3 = base</c>, i.e. IDENTICAL to the plain base value once
///     folded, so the fraction cannot record which spelling produced it.
///     <see cref="TryUnfold"/> therefore always returns the SIMPLEST spelling
///     (no tuplet before tuplet, fewer dots first, larger base first). A fraction
///     that no supported (base, dots∈0..2, tuplet) combination produces has no
///     clean representation and <see cref="TryUnfold"/> returns <c>false</c>; the
///     exporter then falls back to greedy tick decomposition on its note grid.
///     </para>
/// </summary>
public static class GpRhythmFraction
{
    /// <summary>Base note values in quarter-note units, longest first.</summary>
    private static readonly Fraction[] BaseValues =
    [
        new(4, 1),  // whole
        new(2, 1),  // half
        new(1, 1),  // quarter
        new(1, 2),  // eighth
        new(1, 4),  // 16th
        new(1, 8),  // 32nd
        new(1, 16), // 64th
        new(1, 32)  // 128th
    ];

    /// <summary>
    ///     Supported tuplets as (num : den) — "num notes in the time of den". The
    ///     first entry (1:1) is "no tuplet". Ordered so the simplest spellings are
    ///     preferred by <see cref="TryUnfold"/>.
    /// </summary>
    private static readonly (int Num, int Den)[] Tuplets =
    [
        (1, 1),
        (3, 2),
        (5, 4),
        (6, 4),
        (7, 4),
        (9, 8)
    ];

    /// <summary>
    ///     Folds a base note value (in quarter-note units), augmentation
    ///     <paramref name="dots"/> and an optional <paramref name="tupletNum"/> :
    ///     <paramref name="tupletDen"/> tuplet into a single quarter-note-unit
    ///     fraction. A <paramref name="tupletNum"/> ≤ 1 (or non-positive den)
    ///     means "no tuplet". Each dot adds half the previous augmentation
    ///     (1 dot = ×3/2, 2 dots = ×7/4). The tuplet multiplies by
    ///     <c>den/num</c> (triplet 3:2 → ×2/3), matching the gpif
    ///     <c>PrimaryTuplet</c> and legacy <c>pow/n</c> reads.
    /// </summary>
    public static Fraction Fold(Fraction baseValue, int dots, int tupletNum = 0, int tupletDen = 0)
    {
        var value = baseValue;
        var add = baseValue;
        for (var i = 0; i < dots; i++)
        {
            add = new Fraction(add.Numerator, add.Denominator * 2);
            value += add;
        }

        if (tupletNum > 1 && tupletDen > 0)
            value *= new Fraction(tupletDen, tupletNum);

        return value;
    }

    /// <summary>
    ///     Inverts <see cref="Fold"/>: finds the simplest (base value, dots,
    ///     tuplet) spelling whose fold equals <paramref name="value"/>. Returns
    ///     <c>false</c> when no supported combination matches — see the type
    ///     remarks for the exporter's fallback in that case.
    /// </summary>
    public static bool TryUnfold(Fraction value, out RhythmSpelling spelling)
    {
        foreach (var (num, den) in Tuplets)
        {
            foreach (var dots in DotCounts)
            {
                foreach (var baseValue in BaseValues)
                {
                    if (Fold(baseValue, dots, num, den) == value)
                    {
                        spelling = new RhythmSpelling(baseValue, dots, num > 1 ? num : 0, num > 1 ? den : 0);
                        return true;
                    }
                }
            }
        }

        spelling = default;
        return false;
    }

    private static readonly int[] DotCounts = [0, 1, 2];
}

/// <summary>
///     A resolved authored-rhythm spelling: the base note value (quarter-note
///     units), augmentation dot count and an optional tuplet. <see cref="TupletNum"/>
///     is 0 when the value is not tupleted.
/// </summary>
public readonly record struct RhythmSpelling(Fraction BaseValue, int Dots, int TupletNum, int TupletDen)
{
    public bool HasTuplet => TupletNum > 1 && TupletDen > 0;
}
