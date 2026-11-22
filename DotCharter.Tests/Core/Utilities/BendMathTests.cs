namespace DotCharter.Tests.Core.Utilities;

public class BendMathTests
{
    private static BendValue Bend(FractionalPosition pos) => new(pos, 1.0);

    [Fact]
    public void RescalingRetainsAnExactlyRepresentableImportedFraction()
    {
        var start = new FractionalPosition(4, new Fraction(1, 65521));
        var bends = new[] { Bend(start + new Fraction(1, 2)) };

        BendMath.RescaleToSustain(bends, start, new FractionalPosition(1), new FractionalPosition(2));

        Assert.Equal(start + 1, bends[0].Position);
    }

    [Theory]
    [InlineData(65521)]
    [InlineData(1000000007)]
    public void FallbackResamplingRemainsCompatibleWithTheOnset(int onsetDenominator)
    {
        var start = new FractionalPosition(4, new Fraction(1, onsetDenominator));
        var oldSustain = new FractionalPosition(1, new Fraction(1, 65519));
        var bends = new[] { Bend(new FractionalPosition(4, new Fraction(1, 2))) };
        var expected = (bends[0].Position.DoubleValue - start.DoubleValue) * 2 / oldSustain.DoubleValue;

        BendMath.RescaleToSustain(bends, start, oldSustain, new FractionalPosition(2));

        var relative = bends[0].Position - start;
        Assert.InRange(Math.Abs(relative.DoubleValue - expected), 0, 0.000001);
        var target = new FractionalPosition(8);
        var moved = bends[0].Position + (target - start);
        Assert.Equal(relative, moved - target);
    }

    [Fact]
    public void ShorteningSustain_ScalesBendsProportionally()
    {
        // Note at beat 4, sustain 1; a bend at the halfway point (4.5). Shrink the
        // sustain to half a beat → the bend must move to the new halfway (4.25).
        var bends = new List<BendValue> { Bend(new FractionalPosition(4, new Fraction(1, 2))) };

        BendMath.RescaleToSustain(bends, new FractionalPosition(4),
            new FractionalPosition(1), new FractionalPosition(0, new Fraction(1, 2)));

        Assert.Equal(new FractionalPosition(4, new Fraction(1, 4)), bends[0].Position);
    }

    [Fact]
    public void LengtheningSustain_ScalesBendsProportionally()
    {
        // Halfway bend (4.5) on a 1-beat note stays halfway (5.0) when the note
        // grows to 2 beats.
        var bends = new List<BendValue> { Bend(new FractionalPosition(4, new Fraction(1, 2))) };

        BendMath.RescaleToSustain(bends, new FractionalPosition(4),
            new FractionalPosition(1), new FractionalPosition(2));

        Assert.Equal(new FractionalPosition(5), bends[0].Position);
    }

    [Fact]
    public void NoBendEverLandsPastTheNewEnd()
    {
        // A bend authored AT the old end must not exceed the new (shorter) end.
        var bends = new List<BendValue> { Bend(new FractionalPosition(5)) };

        BendMath.RescaleToSustain(bends, new FractionalPosition(4),
            new FractionalPosition(1), new FractionalPosition(0, new Fraction(1, 2)));

        Assert.True(bends[0].Position.CompareTo(new FractionalPosition(4, new Fraction(1, 2))) <= 0);
    }

    [Fact]
    public void ZeroOldSustain_ClampsInsteadOfScaling()
    {
        // No length to scale from → a stray point past the new end is clamped to it.
        var bends = new List<BendValue> { Bend(new FractionalPosition(9)) };

        BendMath.RescaleToSustain(bends, new FractionalPosition(4),
            FractionalPosition.Zero, new FractionalPosition(1));

        Assert.Equal(new FractionalPosition(5), bends[0].Position);
    }

    [Fact]
    public void UnchangedSustain_IsANoOp()
    {
        var pos = new FractionalPosition(4, new Fraction(3, 8));
        var bends = new List<BendValue> { Bend(pos) };

        BendMath.RescaleToSustain(bends, new FractionalPosition(4),
            new FractionalPosition(1), new FractionalPosition(1));

        Assert.Equal(pos, bends[0].Position);
    }

    [Fact]
    public void EmptyBends_DoesNotThrow()
    {
        BendMath.RescaleToSustain(new List<BendValue>(), new FractionalPosition(4),
            new FractionalPosition(1), new FractionalPosition(2));
    }

    [Fact]
    public void CoprimeImportedFractions_RescaleWithoutOverflowingThePosition()
    {
        var start = new FractionalPosition(4, new Fraction(1, 65521));
        var oldSustain = new FractionalPosition(1, new Fraction(1, 65519));
        var newSustain = new FractionalPosition(1, new Fraction(1, 65497));
        var bends = new List<BendValue>
        {
            Bend(start),
            Bend(new FractionalPosition(4, new Fraction(1, 2))),
            Bend(new FractionalPosition(5))
        };
        var originalPositions = bends.Select(b => b.Position.DoubleValue).ToArray();

        BendMath.RescaleToSustain(bends, start, oldSustain, newSustain);

        for (var i = 0; i < bends.Count; i++)
        {
            var expected = start.DoubleValue +
                           (originalPositions[i] - start.DoubleValue) * newSustain.DoubleValue / oldSustain.DoubleValue;
            Assert.InRange(Math.Abs(bends[i].Position.DoubleValue - expected), 0, 0.000001);
            Assert.InRange(bends[i].Position.DoubleValue, start.DoubleValue, start.DoubleValue + newSustain.DoubleValue);
            Assert.InRange(bends[i].Position.Fraction.Denominator, 1, 1000000);
        }
    }

    [Fact]
    public void RescalingKeepsAnOffGridPrebendAtTheExactOnset()
    {
        var start = new FractionalPosition(4, new Fraction(1, 3));
        var bends = new List<BendValue> { Bend(start) };

        BendMath.RescaleToSustain(bends, start, new FractionalPosition(1), new FractionalPosition(2));

        Assert.Equal(start, bends[0].Position);
    }
}
