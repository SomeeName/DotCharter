using System.Numerics;

namespace DotCharter.Tests.Core.Utilities;

public class FretboardMathTests
{
    [Fact]
    public void GetFretPosition_Zero_IsZero() => Assert.Equal(0f, FretboardMath.GetFretPosition(0));

    [Fact]
    public void GetFretPosition_First_IsFirstFretDistance() => Assert.Equal(FretboardMath.FirstFretDistance, FretboardMath.GetFretPosition(1), 3);

    [Fact]
    public void GetFretPosition_IsStrictlyIncreasing()
    {
        var prev = FretboardMath.GetFretPosition(0);
        for (var i = 1; i <= 24; i++)
        {
            var next = FretboardMath.GetFretPosition(i);
            Assert.True(next > prev, $"Fret {i} ({next}) not greater than fret {i - 1} ({prev}).");
            prev = next;
        }
    }

    [Fact]
    public void GetFretPosition_SpacingNarrowsTowardsBridge()
    {
        // Physical guitars: higher frets are closer together.
        for (var i = 2; i <= 20; i++)
        {
            var prevGap = FretboardMath.GetFretPosition(i - 1) - FretboardMath.GetFretPosition(i - 2);
            var gap = FretboardMath.GetFretPosition(i) - FretboardMath.GetFretPosition(i - 1);
            Assert.True(gap < prevGap, $"Fret gap did not narrow at fret {i}.");
        }
    }

    [Fact]
    public void GetFretPosition_ClampsAboveMax() => Assert.Equal(FretboardMath.GetFretPosition(24), FretboardMath.GetFretPosition(99));

    [Fact]
    public void GetFretPosition_NegativeClampsToZero() => Assert.Equal(FretboardMath.GetFretPosition(0), FretboardMath.GetFretPosition(-5));

    [Fact]
    public void GetFretMiddle_IsBetweenAdjacentFrets()
    {
        for (var i = 1; i <= 24; i++)
        {
            var mid = FretboardMath.GetFretMiddle(i);
            Assert.InRange(mid, FretboardMath.GetFretPosition(i - 1), FretboardMath.GetFretPosition(i));
        }
    }

    [Fact]
    public void GetFretMiddle_AtZero_IsHalfOfFretZero() => Assert.Equal(0f, FretboardMath.GetFretMiddle(0));

    [Fact]
    public void GetFlatNoteModelMatrix_EncodesTranslation()
    {
        var m = FretboardMath.GetFlatNoteModelMatrix(new Vector3(1f, 2f, 3f));
        Assert.Equal(1f, m.Translation.X);
        Assert.Equal(2f, m.Translation.Y);
        Assert.Equal(3f, m.Translation.Z);
    }
}
