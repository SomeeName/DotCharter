using DotCharter.Services.Editing;

namespace DotCharter.Tests.UI;

/// <summary>
///     <see cref="HandShapeSplitter.ComputeSplits" /> — the pure half of the
///     split-hand-shape verb (owner request 2026-08-21): select the sound where
///     the fret hand moves, and the bar covering it becomes two.
///     <para>
///     The GAP between the two halves is deliberately not computed here.
///     <c>ChartSpacingEnforcer</c> runs on every committed highway edit and
///     applies the same minimum distance every other shape gets; a second answer
///     to that question is how two of them come apart.
///     </para>
/// </summary>
public sealed class HandShapeSplitterTests
{
    private static HandShape Shape(int start, int end, int templateId = 7) => new()
    {
        Position = new FractionalPosition(start),
        EndPosition = new FractionalPosition(end),
        TemplateId = templateId
    };

    private static FractionalPosition At(int beat) => new(beat);

    [Fact]
    public void APositionInsideAShape_SplitsItInTwo()
    {
        var shape = Shape(0, 8);
        var split = Assert.Single(HandShapeSplitter.ComputeSplits([shape], [At(4)]));

        Assert.Same(shape, split.Shape);
        Assert.Equal(At(4), split.TrimmedEnd);
        var span = Assert.Single(split.NewSpans);
        Assert.Equal(At(4), span.Start);
        Assert.Equal(At(8), span.End);
    }

    [Fact]
    public void SeveralPositionsInsideOneShape_CutItIntoSeveralPieces()
    {
        var shape = Shape(0, 8);
        var split = Assert.Single(HandShapeSplitter.ComputeSplits([shape], [At(6), At(2), At(4)]));

        Assert.Equal(At(2), split.TrimmedEnd);
        Assert.Equal(
            [(At(2), At(4)), (At(4), At(6)), (At(6), At(8))],
            split.NewSpans);
    }

    [Fact]
    public void APositionAtTheShapesOwnStart_IsANoOp() =>
        // Splitting there would leave a zero-width head and change nothing a
        // charter can see, so it must not produce an edit (or an undo node).
        Assert.Empty(HandShapeSplitter.ComputeSplits([Shape(0, 8)], [At(0)]));

    [Fact]
    public void APositionAtTheShapesEnd_IsANoOp() =>
        // End is EXCLUSIVE — nothing inside the bar sits there.
        Assert.Empty(HandShapeSplitter.ComputeSplits([Shape(0, 8)], [At(8)]));

    [Fact]
    public void APositionOutsideEveryShape_IsANoOp()
        => Assert.Empty(HandShapeSplitter.ComputeSplits([Shape(0, 4)], [At(6)]));

    [Fact]
    public void OnlyTheShapesThatActuallyCoverASelectionAreSplit()
    {
        var covered = Shape(0, 8);
        var untouched = Shape(8, 16);

        var splits = HandShapeSplitter.ComputeSplits([covered, untouched], [At(4)]);

        Assert.Same(covered, Assert.Single(splits).Shape);
    }

    [Fact]
    public void OneSelectionInsideTwoOverlappingShapes_SplitsBoth()
    {
        // ClampHandShapes normally prevents overlap, but nothing stops a charter
        // authoring one — so the rule is "every shape it falls inside", not
        // "the first".
        var splits = HandShapeSplitter.ComputeSplits([Shape(0, 8), Shape(2, 10)], [At(4)]);
        Assert.Equal(2, splits.Count);
    }

    [Fact]
    public void TheSplitPiecesTileTheOriginalSpanExactly()
    {
        // No gap and no overlap here on purpose: the head ends exactly where the
        // tail begins, and the spacing enforcer opens the gap afterwards.
        var shape = Shape(1, 9);
        var split = Assert.Single(HandShapeSplitter.ComputeSplits([shape], [At(3), At(7)]));

        // The head is the piece Split does not describe: it runs from the
        // ORIGINAL start to TrimmedEnd, so the tiling chain below is only closed
        // at both ends if this edge is pinned too. This line read
        // `Assert.Equal(shape.Position, shape.Position)` until 2026-08-29 — a
        // literal tautology over a plain POCO property, unlike the superficially
        // similar double-evaluation checks elsewhere, which re-read a COMPUTED
        // memo and do catch something.
        Assert.Equal(At(1), shape.Position);
        Assert.True(shape.Position.CompareTo(split.TrimmedEnd) < 0,
            "the head must keep real width — a zero-width head is the no-op case, not a split.");
        Assert.Equal(At(3), split.TrimmedEnd);
        Assert.Equal(split.TrimmedEnd, split.NewSpans[0].Start);
        for (var i = 1; i < split.NewSpans.Count; i++)
            Assert.Equal(split.NewSpans[i - 1].End, split.NewSpans[i].Start);
        Assert.Equal(shape.EndPosition, split.NewSpans[^1].End);
    }

    [Fact]
    public void NoSelectionOrNoShapes_ProducesNothing()
    {
        Assert.Empty(HandShapeSplitter.ComputeSplits([Shape(0, 8)], []));
        Assert.Empty(HandShapeSplitter.ComputeSplits([], [At(4)]));
    }
}
