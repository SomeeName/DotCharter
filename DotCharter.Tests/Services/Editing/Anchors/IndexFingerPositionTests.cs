using DotCharter.Services.Editing.Anchors;

namespace DotCharter.Tests.Services.Editing.Anchors;

/// <summary>
///     The one derivation shared by inspector rule E04 (which REPORTS a
///     fingering/FHP disagreement) and the optional corrector (which FIXES it).
///     <para>
///         They were one inlined loop and one new call site away from being able
///         to disagree, and a fixer that "corrects" an anchor the rule still flags
///         — or flags one it just wrote — is worse than having neither.
///     </para>
/// </summary>
public sealed class IndexFingerPositionTests
{
    private static ChordTemplate Shape(
        (int stringIndex, int fret)[] frets, (int stringIndex, int finger)[] fingers)
    {
        var t = new ChordTemplate { Id = 1 };
        foreach (var (s, f) in frets) t.Frets[s] = f;
        foreach (var (s, f) in fingers) t.Fingers[s] = f;
        return t;
    }

    [Fact]
    public void TheIndexFingersFretIsFound()
    {
        var shape = Shape([(1, 7), (2, 9)], [(1, 1), (2, 3)]);
        Assert.Equal(7, IndexFingerPosition.Of(shape));
    }

    /// <summary>An authored finger 1 is the answer even where another finger
    ///     would infer a different fret.</summary>
    [Fact]
    public void AnAuthoredFingerOneWinsOverTheOtherFingers()
    {
        var shape = Shape([(1, 7), (2, 7)], [(1, 1), (2, 2)]);
        var index = IndexFingerPosition.Find(shape);
        Assert.Equal(new IndexFret(7, 7, 1, 7), index);
        Assert.False(index!.Value.Inferred);
    }

    // ─── #452: no finger 1 authored — the index is inferred ────────────────

    /// <summary>The owner's Am7: fingers 2-3-4 on fret 5, no finger 1. Finger
    ///     2 on fret 5 puts the index on fret 4.</summary>
    [Fact]
    public void WithNoFingerOneTheIndexIsInferredFromTheLowestFinger()
    {
        var shape = Shape([(2, 5), (3, 5), (4, 5)], [(2, 2), (3, 3), (4, 4)]);
        var index = IndexFingerPosition.Find(shape);
        Assert.Equal(new IndexFret(4, 4, 2, 5), index);
        Assert.True(index!.Value.Inferred);
        Assert.Equal(4, IndexFingerPosition.Of(shape));
    }

    /// <summary>The LOWEST finger decides, not the lowest fret: in a stretch
    ///     the finger nearest the index is the one least bent away from it.</summary>
    [Fact]
    public void TheLowestFingerDecidesNotTheLowestFret()
    {
        // finger 2 on 7 → index 6; finger 4 on 8 would say 5.
        var shape = Shape([(1, 7), (2, 8)], [(1, 2), (2, 4)]);
        Assert.Equal(6, IndexFingerPosition.Of(shape));
    }

    [Fact]
    public void ALowestFingerOnTwoFretsTakesTheLowerFret()
    {
        var shape = Shape([(1, 9), (2, 8), (3, 10)], [(1, 3), (2, 3), (3, 4)]);
        Assert.Equal(new IndexFret(6, 7, 3, 8), IndexFingerPosition.Find(shape));
    }

    [Fact]
    public void AThumbAloneStatesNothing() => Assert.Null(IndexFingerPosition.Of(Shape([(5, 3)], [(5, 0)])));

    /// <summary>The thumb is not a finger of the fretting hand's span: it is
    ///     ignored, and the next finger infers.</summary>
    [Fact]
    public void TheThumbIsIgnoredWhenInferring()
    {
        var shape = Shape([(5, 3), (2, 5)], [(5, 0), (2, 3)]);
        Assert.Equal(3, IndexFingerPosition.Of(shape));
    }

    [Fact]
    public void OpenStringsAloneStateNothing() =>
        Assert.Null(IndexFingerPosition.Of(Shape([(1, 0), (2, 0)], [(1, 2), (2, 3)])));

    [Fact]
    public void TheCapoIsRespectedWhenInferring()
    {
        // capo 3: the finger on fret 3 is on an open string; finger 3 on 7 infers 5.
        var shape = Shape([(1, 3), (2, 7)], [(1, 2), (2, 3)]);
        Assert.Equal(5, IndexFingerPosition.Of(shape, capo: 3));
        // Everything at or below the capo: nothing to infer from.
        Assert.Null(IndexFingerPosition.Of(Shape([(1, 3), (2, 2)], [(1, 2), (2, 3)]), capo: 3));
    }

    /// <summary>Finger 2 on the capo's first fret would put the index ON the
    ///     capo — no fret an anchor can take (the floor is capo + 1).</summary>
    [Fact]
    public void AnInferenceAtOrBelowTheCapoStatesNothing()
    {
        Assert.Null(IndexFingerPosition.Of(Shape([(1, 1)], [(1, 2)])));
        Assert.Null(IndexFingerPosition.Of(Shape([(1, 3)], [(1, 2)]), capo: 2));
        Assert.Equal(3, IndexFingerPosition.Of(Shape([(1, 4)], [(1, 2)]), capo: 2));
        // A ring-finger range is cut at the floor rather than dropped: 2..3 → 3.
        Assert.Equal(new IndexFret(3, 3, 3, 4), IndexFingerPosition.Find(Shape([(1, 4)], [(1, 3)]), capo: 2));
    }

    /// <summary>
    ///     From the ring finger the index may sit one fret per finger below it
    ///     (x − 2) or squeezed one fret below (x − 1): a 3-4 grip on fret 9 is
    ///     played with the index on 7 or 8, and the corpus charters use both.
    /// </summary>
    [Fact]
    public void FromTheRingFingerTheIndexIsARange()
    {
        var shape = Shape([(3, 9), (4, 9)], [(3, 3), (4, 4)]);
        var index = IndexFingerPosition.Find(shape)!.Value;
        Assert.Equal(new IndexFret(7, 8, 3, 9), index);
        Assert.Equal(7, IndexFingerPosition.Of(shape));
        Assert.False(IndexFingerPosition.Disagrees(shape, new FretAnchor { Fret = 7, Width = 4 }));
        Assert.False(IndexFingerPosition.Disagrees(shape, new FretAnchor { Fret = 8, Width = 4 }));
        Assert.True(IndexFingerPosition.Disagrees(shape, new FretAnchor { Fret = 9, Width = 4 }));
        Assert.True(IndexFingerPosition.Disagrees(shape, new FretAnchor { Fret = 6, Width = 4 }));
        Assert.Equal(8, index.Nearest(12));
        Assert.Equal(7, index.Nearest(2));
        Assert.Equal("fret 7 or 8", index.Frets);
    }

    [Fact]
    public void FromTheLittleFingerAloneTheRangeIsThreeFrets()
    {
        var index = IndexFingerPosition.Find(Shape([(0, 8)], [(0, 4)]))!.Value;
        Assert.Equal(new IndexFret(5, 7, 4, 8), index);
        Assert.Equal("frets 5 to 7", index.Frets);
    }

    [Fact]
    public void TheDescriptionSaysWhenTheFretWasInferred()
    {
        var inferred = IndexFingerPosition.Find(Shape([(2, 5)], [(2, 2)]))!.Value;
        Assert.Equal("no finger 1 is authored, but finger 2 on fret 5 puts the index finger on fret 4 (inferred)",
            inferred.Describe());
        var authored = IndexFingerPosition.Find(Shape([(2, 5)], [(2, 1)]))!.Value;
        Assert.Equal("the index finger is on fret 5", authored.Describe());
    }

    /// <summary>A finger 1 on an OPEN string is still an authored finger 1 —
    ///     malformed (TemplateAndOverlapRule reports it), not absent, so the
    ///     other fingers do not infer past it.</summary>
    [Fact]
    public void AFingerOneOnAnOpenStringIsNotReplacedByAnInference()
    {
        var shape = Shape([(1, 0), (2, 7)], [(1, 1), (2, 3)]);
        Assert.Null(IndexFingerPosition.Of(shape));
    }

    [Fact]
    public void AnUnfingeredShapeStatesNothing() => Assert.Null(IndexFingerPosition.Of(Shape([(1, 7)], [])));

    [Fact]
    public void ANullTemplateStatesNothing() => Assert.Null(IndexFingerPosition.Of(null));

    /// <summary>
    ///     An open string is stopped by the capo, not by a finger, so it has no
    ///     position to contradict. EOF treats a finger on an open string as its
    ///     own separate fault.
    /// </summary>
    [Fact]
    public void AnOpenStringCarriesNoIndexPosition() => Assert.Null(IndexFingerPosition.Of(Shape([(1, 0)], [(1, 1)])));

    [Fact]
    public void AFretAtOrBelowTheCapoIsOpen()
    {
        var shape = Shape([(1, 2), (2, 9)], [(1, 1), (2, 3)]);
        Assert.Null(IndexFingerPosition.Of(shape, capo: 2));
    }

    /// <summary>
    ///     A barre lies flat across ONE fret, so a template naming two different
    ///     frets for finger 1 is already malformed. The lowest is where the hand
    ///     actually sits.
    /// </summary>
    [Fact]
    public void ABarreTakesTheLowestFret()
    {
        var shape = Shape([(0, 5), (1, 5), (2, 3)], [(0, 1), (1, 1), (2, 1)]);
        Assert.Equal(3, IndexFingerPosition.Of(shape));
    }

    [Fact]
    public void DisagreesIsFalseWhenTheAnchorMatches()
    {
        var shape = Shape([(1, 7)], [(1, 1)]);
        var anchor = new FretAnchor { Fret = 7, Width = 4 };
        Assert.False(IndexFingerPosition.Disagrees(shape, anchor));
    }

    [Fact]
    public void DisagreesIsTrueWhenTheAnchorIsElsewhere()
    {
        var shape = Shape([(1, 7)], [(1, 1)]);
        var anchor = new FretAnchor { Fret = 12, Width = 4 };
        Assert.True(IndexFingerPosition.Disagrees(shape, anchor));
    }

    /// <summary>No anchor means no claim to contradict.</summary>
    [Fact]
    public void DisagreesIsFalseWithNoAnchor() => Assert.False(IndexFingerPosition.Disagrees(Shape([(1, 7)], [(1, 1)]), null));

    /// <summary>An unfingered shape cannot disagree with any anchor.</summary>
    [Fact]
    public void DisagreesIsFalseWhenNothingIsFingered()
    {
        var anchor = new FretAnchor { Fret = 12, Width = 4 };
        Assert.False(IndexFingerPosition.Disagrees(Shape([(1, 7)], []), anchor));
    }

    /// <summary>#452: the detector and the corrector read the inferred fret
    ///     too — finger 2 on fret 5 disagrees with FHP 5 and agrees with FHP 4.</summary>
    [Fact]
    public void DisagreesReadsTheInferredIndex()
    {
        var shape = Shape([(2, 5), (3, 5), (4, 5)], [(2, 2), (3, 3), (4, 4)]);
        Assert.True(IndexFingerPosition.Disagrees(shape, new FretAnchor { Fret = 5, Width = 4 }));
        Assert.False(IndexFingerPosition.Disagrees(shape, new FretAnchor { Fret = 4, Width = 4 }));
    }
}
