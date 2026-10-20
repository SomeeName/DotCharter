using DotCharter.Services.Parsing.GuitarPro;

namespace DotCharter.Tests.Services.Parsing.GuitarPro;

/// <summary>
/// The dot/tuplet ↔ <see cref="Fraction"/> folding math, both directions.
/// <see cref="GpRhythmFraction.Fold"/> is the AUTHORED-rhythm capture every score
/// importer runs (GP legacy binary + gpif); <see cref="GpRhythmFraction.TryUnfold"/>
/// is the inverse the GP exporter uses. The base-value / dot / tuplet semantics
/// mirror the gpif <c>Rhythms</c> parser and PyGuitarPro / TuxGuitar.
/// </summary>
public sealed class GpRhythmFractionTests
{
    // ---- Fold (import direction) ----------------------------------------

    [Fact]
    public void Fold_PlainNoteValues_AreQuarterNoteUnits()
    {
        Assert.Equal(new Fraction(4, 1), GpRhythmFraction.Fold(new Fraction(4, 1), 0)); // whole
        Assert.Equal(new Fraction(1, 1), GpRhythmFraction.Fold(new Fraction(1, 1), 0)); // quarter
        Assert.Equal(new Fraction(1, 2), GpRhythmFraction.Fold(new Fraction(1, 2), 0)); // eighth
        Assert.Equal(new Fraction(1, 4), GpRhythmFraction.Fold(new Fraction(1, 4), 0)); // 16th
    }

    [Fact]
    public void Fold_Dots_AddHalfEachTime()
    {
        // 1 dot = ×3/2, 2 dots = ×7/4.
        Assert.Equal(new Fraction(3, 2), GpRhythmFraction.Fold(new Fraction(1, 1), 1)); // dotted quarter
        Assert.Equal(new Fraction(7, 4), GpRhythmFraction.Fold(new Fraction(1, 1), 2)); // double-dotted quarter
        Assert.Equal(new Fraction(3, 4), GpRhythmFraction.Fold(new Fraction(1, 2), 1)); // dotted eighth
    }

    [Fact]
    public void Fold_Triplet_MultipliesByTwoThirds()
    {
        // Eighth triplet: 1/2 × 2/3 = 1/3 of a quarter note.
        Assert.Equal(new Fraction(1, 3), GpRhythmFraction.Fold(new Fraction(1, 2), 0, 3, 2));
        // Quarter triplet: 1 × 2/3 = 2/3.
        Assert.Equal(new Fraction(2, 3), GpRhythmFraction.Fold(new Fraction(1, 1), 0, 3, 2));
    }

    [Fact]
    public void Fold_Quintuplet_MultipliesByFourFifths() =>
        // 16th quintuplet: 1/4 × 4/5 = 1/5.
        Assert.Equal(new Fraction(1, 5), GpRhythmFraction.Fold(new Fraction(1, 4), 0, 5, 4));

    [Fact]
    public void Fold_DottedTriplet_CollapsesToPlainBase()
    {
        // AMBIGUOUS by construction: a single-dotted triplet is base × 3/2 × 2/3
        // = base. The folded fraction cannot record that it was authored as a
        // dotted triplet — it is identical to the plain base value.
        Assert.Equal(new Fraction(1, 2), GpRhythmFraction.Fold(new Fraction(1, 2), 1, 3, 2));
        Assert.Equal(new Fraction(1, 1), GpRhythmFraction.Fold(new Fraction(1, 1), 1, 3, 2));
        Assert.Equal(GpRhythmFraction.Fold(new Fraction(1, 2), 0),
            GpRhythmFraction.Fold(new Fraction(1, 2), 1, 3, 2));
    }

    // ---- Unfold (export direction) --------------------------------------

    [Theory]
    [InlineData(4, 1, 4, 1, 0, false)] // whole
    [InlineData(1, 1, 1, 1, 0, false)] // quarter
    [InlineData(1, 2, 1, 2, 0, false)] // eighth
    [InlineData(3, 2, 1, 1, 1, false)] // dotted quarter
    [InlineData(7, 4, 1, 1, 2, false)] // double-dotted quarter
    [InlineData(1, 3, 1, 2, 0, true)]  // eighth triplet
    [InlineData(2, 3, 1, 1, 0, true)]  // quarter triplet
    public void Unfold_RecoversTheSimplestSpelling(int valNum, int valDen, int baseNum, int baseDen,
        int expectedDots, bool expectedTuplet)
    {
        Assert.True(GpRhythmFraction.TryUnfold(new Fraction(valNum, valDen), out var s));
        Assert.Equal(new Fraction(baseNum, baseDen), s.BaseValue);
        Assert.Equal(expectedDots, s.Dots);
        Assert.Equal(expectedTuplet, s.HasTuplet);
    }

    [Fact]
    public void Unfold_PrefersPlainSpelling_ForAmbiguousDottedTriplet()
    {
        // Fold(1/2, dots:1, triplet) == 1/2, so unfolding 1/2 must return the
        // SIMPLEST spelling — a plain eighth, no dot, no tuplet — never the
        // dotted-triplet that also produces it.
        Assert.True(GpRhythmFraction.TryUnfold(new Fraction(1, 2), out var s));
        Assert.Equal(new Fraction(1, 2), s.BaseValue);
        Assert.Equal(0, s.Dots);
        Assert.False(s.HasTuplet);
    }

    [Fact]
    public void Unfold_ReturnsFalse_WhenNoCleanRepresentationExists() =>
        // 5/16 of a quarter note is not reachable by any (base, dots∈0..2,
        // supported tuplet) combination. The exporter's documented fallback for
        // this case is greedy tick decomposition on its 48-tpb note grid.
        Assert.False(GpRhythmFraction.TryUnfold(new Fraction(5, 16), out _));

    [Fact]
    public void FoldThenUnfold_RoundTrips_ForCleanSpellings()
    {
        (Fraction Base, int Dots, int Num, int Den)[] cases =
        [
            (new Fraction(1, 1), 0, 0, 0),
            (new Fraction(1, 2), 1, 0, 0),
            (new Fraction(1, 4), 2, 0, 0),
            (new Fraction(1, 2), 0, 3, 2),
            (new Fraction(1, 4), 0, 5, 4)
        ];

        foreach (var c in cases)
        {
            var folded = GpRhythmFraction.Fold(c.Base, c.Dots, c.Num, c.Den);
            Assert.True(GpRhythmFraction.TryUnfold(folded, out var s), $"no unfold for {folded}");
            // Re-folding the recovered spelling reproduces the same fraction.
            Assert.Equal(folded, GpRhythmFraction.Fold(s.BaseValue, s.Dots, s.TupletNum, s.TupletDen));
        }
    }
}
