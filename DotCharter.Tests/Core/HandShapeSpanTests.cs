namespace DotCharter.Tests.Core;

/// <summary>
///     A hand shape's span is half-open: Start inclusive, End EXCLUSIVE. Shapes
///     abut (every tab importer's per-chord shapes park one shape's end on the
///     next one's start), so this is the only reading under which each chord
///     belongs to exactly one shape.
/// </summary>
public class HandShapeSpanTests
{
    private static HandShape Shape(int startBeat, int endBeat) => new()
    {
        Position = new FractionalPosition(startBeat),
        EndPosition = new FractionalPosition(endBeat)
    };

    [Fact]
    public void TheStartIsInside() => Assert.True(Shape(0, 4).Covers(new FractionalPosition(0)));

    [Fact]
    public void APositionInTheMiddleIsInside() => Assert.True(Shape(0, 4).Covers(new FractionalPosition(2)));

    [Fact]
    public void TheEndIsOUTSIDE() =>
        // The chord sitting exactly on the end belongs to the NEXT shape.
        Assert.False(Shape(0, 4).Covers(new FractionalPosition(4)));

    [Fact]
    public void AbuttingShapes_EachClaimTheirOwnChordAndNoOther()
    {
        var first = Shape(0, 4);
        var second = Shape(4, 8);
        var boundary = new FractionalPosition(4);

        Assert.False(first.Covers(boundary));
        Assert.True(second.Covers(boundary));
    }

    [Fact]
    public void BeforeTheStartIsOutside() => Assert.False(Shape(4, 8).Covers(new FractionalPosition(3)));
}
