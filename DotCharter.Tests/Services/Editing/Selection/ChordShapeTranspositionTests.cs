using DotCharter.Services.Editing.Selection;

namespace DotCharter.Tests.Services.Editing.Selection;

/// <summary>
///     Recognising the same grip at another position — the owner's "some of the
///     edited chords are also very similar, only all moved 1 fret up or down".
///     <para>
///         The rule must agree with <c>FretRule.Transposable</c> in the pattern
///         matcher, or "select alike" and "finger alike" would disagree about what
///         alike means.
///     </para>
/// </summary>
public sealed class ChordShapeTranspositionTests
{
    private static ChordTemplate Shape(int id, params (int stringIndex, int fret)[] frets)
    {
        var t = new ChordTemplate { Id = id };
        foreach (var (s, f) in frets) t.Frets[s] = f;
        return t;
    }

    [Fact]
    public void TheSameGripOneFretHigherIsATransposition()
    {
        var a = Shape(0, (1, 5), (2, 7), (3, 7));
        var b = Shape(1, (1, 6), (2, 8), (3, 8));

        Assert.True(ChordShapeTransposition.IsTransposition(a, b, 0, out var shift));
        Assert.Equal(1, shift);
    }

    [Fact]
    public void MovingDownReportsANegativeShift()
    {
        var a = Shape(0, (1, 7), (2, 9));
        var b = Shape(1, (1, 4), (2, 6));

        Assert.True(ChordShapeTransposition.IsTransposition(a, b, 0, out var shift));
        Assert.Equal(-3, shift);
    }

    [Fact]
    public void ADifferentShapeIsNotATransposition()
    {
        var a = Shape(0, (1, 5), (2, 7));
        var b = Shape(1, (1, 6), (2, 9)); // intervals differ

        Assert.False(ChordShapeTransposition.IsTransposition(a, b, 0, out _));
    }

    /// <summary>
    ///     An open string is stopped by the nut, not the hand, so it cannot move
    ///     with the grip. Same rule as <c>FretRule.Transposable</c>.
    /// </summary>
    [Fact]
    public void AnOpenStringNeverTransposes()
    {
        var a = Shape(0, (1, 0), (2, 5));
        var b = Shape(1, (1, 1), (2, 6)); // tried to move the open string too

        Assert.False(ChordShapeTransposition.IsTransposition(a, b, 0, out _));
    }

    [Fact]
    public void AnOpenStringThatStaysOpenIsFine()
    {
        var a = Shape(0, (1, 0), (2, 5), (3, 5));
        var b = Shape(1, (1, 0), (2, 8), (3, 8));

        Assert.True(ChordShapeTransposition.IsTransposition(a, b, 0, out var shift));
        Assert.Equal(3, shift);
    }

    [Fact]
    public void AShapeUsingDifferentStringsIsNotATransposition()
    {
        var a = Shape(0, (1, 5), (2, 7));
        var b = Shape(1, (2, 6), (3, 8));

        Assert.False(ChordShapeTransposition.IsTransposition(a, b, 0, out _));
    }

    [Fact]
    public void AShapeWithMoreStringsIsNotATransposition()
    {
        var a = Shape(0, (1, 5), (2, 7));
        var b = Shape(1, (1, 6), (2, 8), (3, 8));

        Assert.False(ChordShapeTransposition.IsTransposition(a, b, 0, out _));
    }

    /// <summary>Under a capo, a fret at or below it sounds as the open string.</summary>
    [Fact]
    public void TheCapoDecidesWhatCountsAsOpen()
    {
        var a = Shape(0, (1, 2), (2, 5));
        var b = Shape(1, (1, 2), (2, 8));

        Assert.True(ChordShapeTransposition.IsTransposition(a, b, 2, out var shift));
        Assert.Equal(3, shift);
    }

    /// <summary>
    ///     An arpeggio is a different KIND of shape, not another position of one —
    ///     the flag is load-bearing on export.
    /// </summary>
    [Fact]
    public void AnArpeggioIsNotATranspositionOfAPlainChord()
    {
        var a = Shape(0, (1, 5), (2, 7));
        var b = Shape(1, (1, 6), (2, 8));
        b.Arpeggio = true;

        Assert.False(ChordShapeTransposition.IsTransposition(a, b, 0, out _));
    }

    [Fact]
    public void AShapeIsNeverATranspositionOfItself()
    {
        var a = Shape(0, (1, 5), (2, 7));
        Assert.False(ChordShapeTransposition.IsTransposition(a, a, 0, out _));
    }

    /// <summary>
    ///     An all-open shape has nothing that could move, so it is not a
    ///     transposition of anything — including another all-open shape.
    /// </summary>
    [Fact]
    public void AnAllOpenShapeIsNotATransposition()
    {
        var a = Shape(0, (1, 0), (2, 0));
        var b = Shape(1, (1, 0), (2, 0));

        Assert.False(ChordShapeTransposition.IsTransposition(a, b, 0, out _));
    }

    [Fact]
    public void FindTransposedReturnsNearestPositionsFirst()
    {
        var arr = new Arrangement { Name = "Lead" };
        var source = Shape(0, (1, 5), (2, 7));
        var farAway = Shape(1, (1, 12), (2, 14));
        var nextDoor = Shape(2, (1, 6), (2, 8));
        var unrelated = Shape(3, (1, 6), (2, 10));
        foreach (var t in new[] { source, farAway, nextDoor, unrelated })
            arr.ChordTemplates.Add(t);

        var found = ChordShapeTransposition.FindTransposed(arr, source);

        Assert.Equal(2, found.Count);
        Assert.Same(nextDoor, found[0]);
        Assert.Same(farAway, found[1]);
    }

    /// <summary>
    ///     A SEPARATE template at the same frets — which the chart mints whenever a
    ///     chord is re-fingered privately — is exactly what the charter wants
    ///     unified, so shift 0 counts.
    /// </summary>
    [Fact]
    public void ASeparateTemplateAtTheSameFretsIsIncluded()
    {
        var arr = new Arrangement { Name = "Lead" };
        var source = Shape(0, (1, 5), (2, 7));
        var twin = Shape(1, (1, 5), (2, 7));
        arr.ChordTemplates.Add(source);
        arr.ChordTemplates.Add(twin);

        Assert.Same(twin, Assert.Single(ChordShapeTransposition.FindTransposed(arr, source)));
    }
}
