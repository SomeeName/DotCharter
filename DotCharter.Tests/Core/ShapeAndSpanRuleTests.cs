namespace DotCharter.Tests.Core;

public sealed class ShapeAndSpanRuleTests
{
    private static readonly FractionalPosition Bar1 = new(4);
    private static readonly FractionalPosition Bar2 = new(8);

    [Fact]
    public void SameEntries_IgnoresInsertionOrder()
    {
        var left = new Dictionary<int, int> { [0] = 3, [1] = 5 };
        var right = new Dictionary<int, int> { [1] = 5, [0] = 3 };

        Assert.True(MapEquality.SameEntries(left, right));
    }

    [Theory]
    [InlineData(new[] { 0, 3, 1, 5, 2, 5 })]
    [InlineData(new[] { 0, 3, 1, 6 })]
    [InlineData(new[] { 0, 3, 2, 5 })]
    [InlineData(new int[0])]
    public void SameEntries_RejectsAnotherCountAValueOrAKey(int[] pairs)
    {
        var left = new Dictionary<int, int> { [0] = 3, [1] = 5 };
        var right = new Dictionary<int, int>();
        for (var i = 0; i < pairs.Length; i += 2)
        {
            right[pairs[i]] = pairs[i + 1];
        }

        Assert.False(MapEquality.SameEntries(left, right));
        Assert.False(MapEquality.SameEntries(right, left));
    }

    [Fact]
    public void HasFrets_ComparesFretsOnly()
    {
        var template = Template("C", frets: [(1, 3), (2, 2)], fingers: [(1, 3), (2, 2)]);

        Assert.True(template.HasFrets(new Dictionary<int, int> { [2] = 2, [1] = 3 }));
        Assert.False(template.HasFrets(new Dictionary<int, int> { [1] = 3 }));
        Assert.False(template.HasFrets(new Dictionary<int, int> { [1] = 3, [2] = 4 }));
    }

    [Fact]
    public void HasSameShapeAs_NeedsTheSameFingersButNotTheSameNameOrFlags()
    {
        var shape = Template("C", frets: [(1, 3), (2, 2)], fingers: [(1, 3), (2, 2)]);
        var renamedArpeggio = Template("Cadd", frets: [(1, 3), (2, 2)], fingers: [(1, 3), (2, 2)]);
        renamedArpeggio.Arpeggio = true;
        var otherFingers = Template("C", frets: [(1, 3), (2, 2)], fingers: [(1, 4), (2, 2)]);

        Assert.True(shape.HasSameShapeAs(renamedArpeggio));
        Assert.False(shape.HasSameShapeAs(otherFingers));
    }

    [Fact]
    public void Equals_StillNeedsTheNameAndBothArpeggioFlags()
    {
        var shape = Template("C", frets: [(1, 3)], fingers: [(1, 3)]);
        var same = Template("C", frets: [(1, 3)], fingers: [(1, 3)]);
        var renamed = Template("D", frets: [(1, 3)], fingers: [(1, 3)]);
        var arpeggio = Template("C", frets: [(1, 3)], fingers: [(1, 3)]);
        arpeggio.Arpeggio = true;
        var forced = Template("C", frets: [(1, 3)], fingers: [(1, 3)]);
        forced.ForceArpeggioInRs = true;

        Assert.Equal(shape, same);
        Assert.Equal(shape.GetHashCode(), same.GetHashCode());
        Assert.NotEqual(shape, renamed);
        Assert.NotEqual(shape, arpeggio);
        Assert.NotEqual(shape, forced);
    }

    [Fact]
    public void IsWithin_IsHalfOpen()
    {
        Assert.True(Bar1.IsWithin(Bar1, Bar2));
        Assert.True(new FractionalPosition(7, new Fraction(1, 2)).IsWithin(Bar1, Bar2));
        Assert.False(Bar2.IsWithin(Bar1, Bar2));
        Assert.False(new FractionalPosition(3).IsWithin(Bar1, Bar2));
    }

    [Fact]
    public void IsWithin_WithNoEnd_RunsToTheEndOfTheChart()
    {
        Assert.True(new FractionalPosition(10_000).IsWithin(Bar1, null));
        Assert.False(new FractionalPosition(3).IsWithin(Bar1, null));
    }

    [Fact]
    public void AbuttingHandShapes_NeverBothCoverTheSeam()
    {
        var first = new HandShape { Position = Bar1, EndPosition = Bar2 };
        var second = new HandShape { Position = Bar2, EndPosition = new FractionalPosition(12) };

        Assert.False(first.Covers(Bar2));
        Assert.True(second.Covers(Bar2));
    }

    private static ChordTemplate Template(string name, (int String, int Fret)[] frets, (int String, int Finger)[] fingers)
    {
        var template = new ChordTemplate { Name = name };
        foreach (var (stringIndex, fret) in frets)
        {
            template.Frets[stringIndex] = fret;
        }

        foreach (var (stringIndex, finger) in fingers)
        {
            template.Fingers[stringIndex] = finger;
        }

        return template;
    }
}
