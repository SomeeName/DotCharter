using DotCharter.Services.Editing.Selection;

namespace DotCharter.Tests.Services.Editing.Selection;

/// <summary>
///     Finding the grip a single-note bar is really part of.
///     <para>
///         The gesture: generating a hand shape over one note builds a one-string
///         shape, the charter adds the rest of the grip by hand, and the next
///         occurrence seeds the same one string — which no longer matches the
///         finished shape, so the whole grip is rebuilt. The owner's 2026-07-28
///         session built one identical grip seven times, minting templates 95, 96,
///         98, 100, 101, 102 and 103.
///     </para>
/// </summary>
public sealed class ChordShapeSupersetTests
{
    private static ChordTemplate Shape(int id, params (int stringIndex, int fret)[] frets)
    {
        var t = new ChordTemplate { Id = id };
        foreach (var (s, f) in frets) t.Frets[s] = f;
        return t;
    }

    /// <summary>The exact case from the log: {s3:12} is part of {s3:12, s4:15}.</summary>
    [Fact]
    public void AOneStringSeedIsContainedByTheGripItBelongsTo()
    {
        var seed = Shape(0, (3, 12));
        var grip = Shape(1, (3, 12), (4, 15));

        Assert.True(ChordShapeSuperset.Contains(seed, grip));
    }

    [Fact]
    public void ADifferentFretOnTheSharedStringIsNotContainment()
    {
        var seed = Shape(0, (3, 12));
        var other = Shape(1, (3, 14), (4, 15));

        Assert.False(ChordShapeSuperset.Contains(seed, other));
    }

    [Fact]
    public void AShapeMissingTheSeedsStringIsNotContainment()
    {
        var seed = Shape(0, (3, 12));
        var other = Shape(1, (4, 15), (5, 17));

        Assert.False(ChordShapeSuperset.Contains(seed, other));
    }

    /// <summary>
    ///     Same strings is either the same shape — already handled by the
    ///     find-or-create key — or a different one. Neither is worth offering.
    /// </summary>
    [Fact]
    public void AnEqualShapeIsNotASuperset()
    {
        var seed = Shape(0, (3, 12));
        var twin = Shape(1, (3, 12));

        Assert.False(ChordShapeSuperset.Contains(seed, twin));
    }

    [Fact]
    public void ASmallerShapeIsNotASuperset()
    {
        var seed = Shape(0, (3, 12), (4, 15));
        var smaller = Shape(1, (3, 12));

        Assert.False(ChordShapeSuperset.Contains(seed, smaller));
    }

    [Fact]
    public void AShapeIsNotItsOwnSuperset()
    {
        var seed = Shape(0, (3, 12));
        Assert.False(ChordShapeSuperset.Contains(seed, seed));
    }

    [Fact]
    public void AnEmptySeedContainsNothing() => Assert.False(ChordShapeSuperset.Contains(Shape(0), Shape(1, (3, 12))));

    /// <summary>
    ///     An arpeggio is a different KIND of shape, not a bigger one — the flag
    ///     drives the export and draws a bracket instead of a bar.
    /// </summary>
    [Fact]
    public void AnArpeggioDoesNotContainAPlainShape()
    {
        var seed = Shape(0, (3, 12));
        var arp = Shape(1, (3, 12), (4, 15));
        arp.Arpeggio = true;

        Assert.False(ChordShapeSuperset.Contains(seed, arp));
    }

    [Fact]
    public void TheClosestFitIsOfferedFirst()
    {
        var arr = new Arrangement { Name = "Lead" };
        var seed = Shape(0, (3, 12));
        var big = Shape(1, (3, 12), (4, 15), (5, 17), (2, 10));
        var pair = Shape(2, (3, 12), (4, 15));
        foreach (var t in new[] { seed, big, pair }) arr.ChordTemplates.Add(t);

        var found = ChordShapeSuperset.FindContaining(arr, seed);

        Assert.Equal(2, found.Count);
        Assert.Same(pair, found[0]);
        Assert.Same(big, found[1]);
    }

    /// <summary>Stable between selections rather than reshuffling per enumeration.</summary>
    [Fact]
    public void EqualFitsAreOrderedByTemplateId()
    {
        var arr = new Arrangement { Name = "Lead" };
        var seed = Shape(0, (3, 12));
        var later = Shape(9, (3, 12), (4, 15));
        var earlier = Shape(4, (3, 12), (5, 17));
        foreach (var t in new[] { seed, later, earlier }) arr.ChordTemplates.Add(t);

        var found = ChordShapeSuperset.FindContaining(arr, seed);

        Assert.Same(earlier, found[0]);
        Assert.Same(later, found[1]);
    }

    [Fact]
    public void TheOfferedRowIsCapped()
    {
        var arr = new Arrangement { Name = "Lead" };
        var seed = Shape(0, (3, 12));
        arr.ChordTemplates.Add(seed);
        for (var i = 1; i <= 12; i++)
            arr.ChordTemplates.Add(Shape(i, (3, 12), (4, i)));

        Assert.Equal(ChordShapeSuperset.MaxSuggestions, ChordShapeSuperset.FindContaining(arr, seed).Count);
    }

    [Fact]
    public void NothingContainingMeansNothingOffered()
    {
        var arr = new Arrangement { Name = "Lead" };
        var seed = Shape(0, (3, 12));
        arr.ChordTemplates.Add(seed);
        arr.ChordTemplates.Add(Shape(1, (1, 5), (2, 7)));

        Assert.Empty(ChordShapeSuperset.FindContaining(arr, seed));
    }
}
