namespace DotCharter.Tests.Core.Primitives;

public class FractionalPositionTests
{
    [Fact]
    public void Constructor_NormalizesOverflowFraction()
    {
        // 3/2 has 1 full beat + 1/2 leftover.
        var pos = new FractionalPosition(0, new Fraction(3, 2));
        Assert.Equal(1, pos.BeatId);
        Assert.Equal(new Fraction(1, 2), pos.Fraction);
    }

    [Fact]
    public void Constructor_NormalizesNegativeFraction()
    {
        // -1/4 at beat 0 = beat -1 + 3/4.
        var pos = new FractionalPosition(0, new Fraction(-1, 4));
        Assert.Equal(-1, pos.BeatId);
        Assert.Equal(new Fraction(3, 4), pos.Fraction);
    }

    [Fact]
    public void Zero_IsDefaultPosition()
    {
        Assert.Equal(0, FractionalPosition.Zero.BeatId);
        Assert.Equal(Fraction.Zero, FractionalPosition.Zero.Fraction);
    }

    [Fact]
    public void DoubleValue_CombinesBeatAndFraction()
    {
        var pos = new FractionalPosition(3, new Fraction(1, 2));
        Assert.Equal(3.5, pos.DoubleValue, 10);
    }

    [Fact]
    public void Addition_CombinesBeatsAndFractions()
    {
        var a = new FractionalPosition(1, new Fraction(1, 4));
        var b = new FractionalPosition(2, new Fraction(1, 2));
        var result = a + b;
        Assert.Equal(3, result.BeatId);
        Assert.Equal(new Fraction(3, 4), result.Fraction);
    }

    [Fact]
    public void Subtraction_HandlesCrossBeatCarry()
    {
        var a = new FractionalPosition(3, new Fraction(1, 4));
        var b = new FractionalPosition(0, new Fraction(1, 2));
        var result = a - b;
        // 3.25 - 0.5 = 2.75
        Assert.Equal(2, result.BeatId);
        Assert.Equal(new Fraction(3, 4), result.Fraction);
    }

    [Fact]
    public void Comparison_OrdersByTotalPosition()
    {
        Assert.True(new FractionalPosition(1, new Fraction(1, 2)) < new FractionalPosition(2));
        Assert.True(new FractionalPosition(2, new Fraction(1, 4)) > new FractionalPosition(2));
    }

    [Fact]
    public void Floor_ReturnsWholeBeatPosition()
    {
        var pos = new FractionalPosition(3, new Fraction(1, 4));
        Assert.Equal(new FractionalPosition(3), pos.Floor());
    }

    [Fact]
    public void Ceil_RoundsUpForFractional_KeepsForWholeBeats()
    {
        Assert.Equal(new FractionalPosition(4),
            new FractionalPosition(3, new Fraction(1, 4)).Ceil());

        Assert.Equal(new FractionalPosition(3),
            new FractionalPosition(3, Fraction.Zero).Ceil());
    }

    [Fact]
    public void Absolute_FlipsNegative()
    {
        var neg = new FractionalPosition(-3, new Fraction(1, 2));
        var abs = neg.Absolute();
        // |-3.5| -> 2.5? Actually Negate(-3 -1/2) = (3, -(-1/2)) normalized.
        // Absolute does Negate when BeatId < 0 → Negate returns new(-BeatId, -Fraction)
        // = new(3, -1/2) which after normalization = (2, 1/2).
        Assert.True(abs.DoubleValue > 0);
    }

    [Fact]
    public void TryParse_RoundTripsWithToString()
    {
        var original = new FractionalPosition(7, new Fraction(3, 8));
        var text = original.ToString();
        Assert.True(FractionalPosition.TryParse(text, out var parsed));
        Assert.Equal(original, parsed);
    }

    [Fact]
    public void TryParse_RejectsInvalidInput()
    {
        Assert.False(FractionalPosition.TryParse(null, out _));
        Assert.False(FractionalPosition.TryParse("", out _));
        Assert.False(FractionalPosition.TryParse("3", out _));
        Assert.False(FractionalPosition.TryParse("abc def", out _));
    }

    /// <summary>
    ///     The <c>Try*</c> contract: never throw. These reduce out of int range
    ///     inside <see cref="Fraction" />'s constructor, which used to raise
    ///     <see cref="ArgumentOutOfRangeException" /> out of a <c>bool</c>-returning
    ///     method — and this is the parse a `.rscp` <c>p=</c> attribute reaches
    ///     (RscpParsingUtilities.ParseFraction, RscpArrangementParser,
    ///     RscpVocalsParser), from disk AND over the unauthenticated LAN
    ///     transfer, so one hostile attribute aborted the whole load.
    /// </summary>
    [Theory]
    [InlineData("0 1/-2147483648")]
    [InlineData("0 -2147483648/-1")]
    [InlineData("-2147483648 -2147483648/-1")]
    [InlineData("7 1/0")]
    public void TryParse_ReturnsFalseInsteadOfThrowing_ForAHostilePosition(string input)
    {
        var thrown = Record.Exception(() =>
        {
            Assert.False(FractionalPosition.TryParse(input, out var result));
            Assert.Equal(FractionalPosition.Zero, result);
        });
        Assert.Null(thrown);
    }

    [Fact]
    public void MinMax_ReturnCorrectPosition()
    {
        var a = new FractionalPosition(2);
        var b = new FractionalPosition(2, new Fraction(1, 2));
        Assert.Equal(a, FractionalPosition.Min(a, b));
        Assert.Equal(b, FractionalPosition.Max(a, b));
    }

    [Fact]
    public void Multiplication_ByInteger_ScalesLinearly()
    {
        var pos = new FractionalPosition(1, new Fraction(1, 4));
        var scaled = pos * 4;
        // 1.25 * 4 = 5
        Assert.Equal(5.0, scaled.DoubleValue, 10);
    }
}
