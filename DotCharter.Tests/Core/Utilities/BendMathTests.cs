namespace DotCharter.Tests.Core.Utilities;

public class BendMathTests
{
    private static BendValue Bend(FractionalPosition pos) => new(pos, 1.0);

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
}
