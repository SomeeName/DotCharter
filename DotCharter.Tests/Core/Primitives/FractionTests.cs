namespace DotCharter.Tests.Core.Primitives;

public class FractionTests
{
    [Fact]
    public void Constructor_ReducesToCanonicalForm()
    {
        var f = new Fraction(4, 16);
        Assert.Equal(1, f.Numerator);
        Assert.Equal(4, f.Denominator);
    }

    [Fact]
    public void Constructor_NormalizesNegativeDenominator()
    {
        var f = new Fraction(1, -2);
        Assert.Equal(-1, f.Numerator);
        Assert.Equal(2, f.Denominator);
    }

    [Fact]
    public void Constructor_ZeroDenominator_Throws() => Assert.Throws<DivideByZeroException>(() => new Fraction(1, 0));

    [Fact]
    public void Equals_TreatsEquivalentFractionsAsEqual()
    {
        Assert.Equal(new Fraction(1, 2), new Fraction(2, 4));
        Assert.True(new Fraction(1, 2) == new Fraction(3, 6));
    }

    [Fact]
    public void HashCode_EqualFractionsHaveEqualHashCodes() => Assert.Equal(new Fraction(1, 2).GetHashCode(), new Fraction(4, 8).GetHashCode());

    [Theory]
    [InlineData(1, 2, 1, 3, 5, 6)]
    [InlineData(1, 4, 1, 4, 1, 2)]
    [InlineData(-1, 2, 1, 2, 0, 1)]
    public void Addition_WorksCorrectly(int aN, int aD, int bN, int bD, int expectedN, int expectedD)
    {
        var result = new Fraction(aN, aD) + new Fraction(bN, bD);
        Assert.Equal(new Fraction(expectedN, expectedD), result);
    }

    /// <summary>
    ///     The denominator product used to be computed in unchecked int, so the
    ///     widening inside the constructor happened one step too late to see it
    ///     overflow. <c>536870912 * 8 == 2^32</c> wraps to <b>0</b> — and the
    ///     constructor throws on a zero denominator, so adding two perfectly
    ///     ordinary fractions raised <see cref="DivideByZeroException" /> with no
    ///     zero anywhere in the input.
    ///     <para>
    ///         Reachable from ONE attribute of a <c>.rscp</c>:
    ///         <c>&lt;vocal p="0 1/536870912" lyric="x"/&gt;</c> with no <c>ep</c>,
    ///         where the parser adds a hard-coded <c>1/8</c>. That file arrives
    ///         from disk and over the unauthenticated LAN session transfer.
    ///     </para>
    /// </summary>
    [Fact]
    public void Addition_WithALargeDenominator_DoesNotWrapThroughZero()
    {
        var result = new Fraction(1, 536870912) + new Fraction(1, 8);

        // 8/2^32 + 536870912/2^32 reduces by 8 and fits an int comfortably.
        Assert.Equal(new Fraction(67108865, 536870912), result);
    }

    /// <summary>
    ///     The same overflow in the multiply operators, and the OTHER half of the
    ///     failure: a product that wraps to a merely-wrong value produces a
    ///     silently wrong fraction instead of throwing. <c>65521 * 65519</c> is
    ///     4,292,788,799, which as an int reads back as −2,178,497 — so the
    ///     unfixed code answered <c>-1/2178497</c>, quietly, with the wrong sign
    ///     and three orders of magnitude off.
    /// </summary>
    [Fact]
    public void Multiplication_PastTheIntRange_ThrowsInsteadOfWrapping()
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() =>
            new Fraction(1, 65521) * new Fraction(1, 65519));

        Assert.Contains("does not fit in a Fraction", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    ///     Scaling by a plain int wrapped too, and this is the quietest shape of
    ///     all: <c>1073741824 * 4</c> is exactly 2^32, which as an int is
    ///     <b>zero</b> — so the fraction did not throw, it became <c>0/1</c>.
    /// </summary>
    [Fact]
    public void MultiplicationByAnInt_PastTheIntRange_ThrowsInsteadOfCollapsingToZero()
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => new Fraction(1073741824, 3) * 4);

        Assert.Contains("does not fit in a Fraction", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>Division multiplies crosswise, so it overflows the same way:
    /// <c>a.Denominator * b.Numerator</c> wrapped to exactly 0 and the
    /// constructor reported a zero denominator for an input that had none.</summary>
    [Fact]
    public void Division_PastTheIntRange_ThrowsInsteadOfWrappingToAZeroDenominator()
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() =>
            new Fraction(1, 536870912) / new Fraction(8, 1));

        Assert.Contains("does not fit in a Fraction", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Subtraction_WorksCorrectly() => Assert.Equal(new Fraction(1, 6), new Fraction(1, 2) - new Fraction(1, 3));

    [Fact]
    public void Multiplication_WithInteger_WorksCorrectly()
    {
        Assert.Equal(new Fraction(3, 4), new Fraction(1, 4) * 3);
        Assert.Equal(new Fraction(3, 4), 3 * new Fraction(1, 4));
    }

    [Fact]
    public void Division_WorksCorrectly() => Assert.Equal(new Fraction(3, 2), new Fraction(3, 4) / new Fraction(1, 2));

    [Fact]
    public void Comparison_OrdersByValue()
    {
        Assert.True(new Fraction(1, 3) < new Fraction(1, 2));
        Assert.True(new Fraction(3, 4) > new Fraction(2, 3));
    }

    [Fact]
    public void Negate_InvertsSign()
    {
        var f = new Fraction(3, 4).Negate();
        Assert.Equal(-3, f.Numerator);
        Assert.True(f.IsNegative);
    }

    [Fact]
    public void Absolute_MakesPositive()
    {
        Assert.Equal(new Fraction(3, 4), new Fraction(-3, 4).Absolute());
        Assert.Equal(new Fraction(3, 4), new Fraction(3, 4).Absolute());
    }

    [Fact]
    public void DoubleValue_ReturnsCorrectDouble()
    {
        Assert.Equal(0.25, new Fraction(1, 4).DoubleValue, 10);
        Assert.Equal(-0.5, new Fraction(-1, 2).DoubleValue, 10);
    }

    [Theory]
    [InlineData("1/4", 1, 4, true)]
    [InlineData(" 3 / 8 ", 3, 8, true)]
    [InlineData("", 0, 1, false)]
    [InlineData("not a fraction", 0, 1, false)]
    [InlineData("1/0", 0, 1, false)]
    public void TryParse_HandlesVariousInputs(string input, int expectedN, int expectedD, bool expectedSuccess)
    {
        var success = Fraction.TryParse(input, out var result);
        Assert.Equal(expectedSuccess, success);
        if (expectedSuccess)
            Assert.Equal(new Fraction(expectedN, expectedD), result);
    }

    /// <summary>
    ///     A <c>Try*</c> method NEVER throws — including for a value that parses
    ///     as two ints but does not FIT once reduced. Both of these threw
    ///     <see cref="ArgumentOutOfRangeException" /> straight out of
    ///     <c>bool</c>-returning <c>TryParse</c>, and they are reachable from a
    ///     hostile <c>.rscp</c> position attribute — off disk AND over the
    ///     unauthenticated LAN session transfer — where it aborted the load.
    /// </summary>
    [Theory]
    [InlineData("1/-2147483648")]   // reduces to -1/2147483648: the denominator overflows
    [InlineData("-2147483648/-1")]  // reduces to 2147483648/1: the numerator overflows
    [InlineData("-2147483648/2")]   // …and one that reduces IN range must still parse
    public void TryParse_NeverThrows_ForAnOutOfRangeReducedValue(string input)
    {
        var thrown = Record.Exception(() => Fraction.TryParse(input, out _));
        Assert.Null(thrown);
    }

    [Theory]
    [InlineData("1/-2147483648", false)]
    [InlineData("-2147483648/-1", false)]
    [InlineData("-2147483648/2", true)]  // -1073741824/1 fits
    public void TryParse_ReportsWhetherTheReducedValueFits(string input, bool expected) => Assert.Equal(expected, Fraction.TryParse(input, out _));

    [Theory]
    [InlineData(1L, 0L, false)]
    [InlineData(1L, int.MinValue, false)]
    [InlineData((long)int.MinValue, -1L, false)]
    [InlineData(2L, 4L, true)]
    public void TryCreate_IsTheNonThrowingConstructor(long numerator, long denominator, bool expected)
    {
        var thrown = Record.Exception(() => Fraction.TryCreate(numerator, denominator, out _));
        Assert.Null(thrown);
        Assert.Equal(expected, Fraction.TryCreate(numerator, denominator, out var result));
        if (expected) Assert.Equal(new Fraction(numerator, denominator), result);
    }

    /// <summary>The THROWING entry points keep throwing — the fix is scoped to
    /// the <c>Try*</c> contract, not a loosening of the range invariant.</summary>
    [Fact]
    public void TheConstructor_StillRefusesAnOutOfRangeReducedValue()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new Fraction(int.MinValue, -1));
        Assert.Throws<DivideByZeroException>(() => new Fraction(1, 0));
    }

    [Fact]
    public void Parse_InvalidString_Throws()
    {
        Assert.Throws<FormatException>(() => Fraction.Parse("abc"));
        Assert.Throws<FormatException>(() => Fraction.Parse("-2147483648/-1"));
    }

    [Fact]
    public void ImplicitConversion_FromInt_Works()
    {
        Fraction f = 7;
        Assert.Equal(7, f.Numerator);
        Assert.Equal(1, f.Denominator);
    }

    [Fact]
    public void ToString_UsesSlashNotation() => Assert.Equal("3/4", new Fraction(3, 4).ToString());

    [Fact]
    public void MinMax_ReturnCorrectValues()
    {
        var a = new Fraction(1, 3);
        var b = new Fraction(1, 2);
        Assert.Equal(a, Fraction.Min(a, b));
        Assert.Equal(b, Fraction.Max(a, b));
    }
}
