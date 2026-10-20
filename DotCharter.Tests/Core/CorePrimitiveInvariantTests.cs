namespace DotCharter.Tests.Core;

/// <summary>
///     <c>default(Fraction)</c> used to have <c>Denominator == 0</c>: the
///     constructor forbids it, but <c>default</c> bypasses the constructor, and
///     these are value types stored in arrays, dictionaries and
///     <c>[ThreadStatic]</c> scratch. The consequences were all silent —
///     <c>DivideByZeroException</c> on the first arithmetic, NaN from
///     <c>DoubleValue</c>, and <c>CompareTo</c> claiming equality with
///     everything while <c>Equals</c> disagreed, so a default-valued struct
///     sorted unpredictably and deduped wrongly.
/// </summary>
public sealed class FractionDefaultValueTests
{
    [Fact]
    public void DefaultFractionIsZeroOverOne_NotZeroOverZero()
    {
        var d = default(Fraction);

        Assert.Equal(0, d.Numerator);
        Assert.Equal(1, d.Denominator);
        Assert.Equal(Fraction.Zero, d);
    }

    [Fact]
    public void DefaultFractionArithmeticDoesNotThrow()
    {
        var d = default(Fraction);
        var half = new Fraction(1, 2);

        Assert.Equal(half, d + half);
        Assert.Equal(half.Negate(), d - half);
        Assert.Equal(Fraction.Zero, d * half);
    }

    [Fact]
    public void DefaultFractionHasASaneDoubleValue()
    {
        Assert.Equal(0.0, default(Fraction).DoubleValue);
        Assert.False(double.IsNaN(default(Fraction).DoubleValue));
    }

    [Fact]
    public void CompareToAndEqualsAgreeForTheDefaultValue()
    {
        // The old 0/0 compared "equal" to every fraction while Equals said no.
        var d = default(Fraction);
        var half = new Fraction(1, 2);

        Assert.True(d.CompareTo(half) < 0);
        Assert.True(half.CompareTo(d) > 0);
        Assert.Equal(0, d.CompareTo(Fraction.Zero));
        Assert.True(d.Equals(Fraction.Zero));
    }

    [Fact]
    public void DefaultFractionSortsWithTheOthers()
    {
        var values = new[] { new Fraction(3, 4), default, new Fraction(1, 2) };

        Array.Sort(values);

        Assert.Equal(new[] { Fraction.Zero, new Fraction(1, 2), new Fraction(3, 4) }, values);
    }

    [Theory]
    [InlineData(int.MinValue, 1)]
    [InlineData(1, int.MinValue)]
    [InlineData(int.MinValue, 2)]
    [InlineData(int.MaxValue, int.MinValue)]
    public void ExtremeInputsEitherReduceCleanlyOrFailLoudly(int numerator, int denominator)
    {
        // Previously: a MinValue numerator threw OverflowException out of
        // Math.Abs, and a MinValue DENOMINATOR silently kept a negative
        // denominator — breaking the invariant every comparison rests on.
        try
        {
            var f = new Fraction(numerator, denominator);
            Assert.True(f.Denominator > 0, "denominator must stay positive");
        }
        catch (ArgumentOutOfRangeException)
        {
            // Acceptable: an explicit, documented refusal.
        }
    }

    [Fact]
    public void DenominatorIsAlwaysPositiveAfterNormalization()
    {
        Assert.Equal(2, new Fraction(1, -2).Denominator);
        Assert.Equal(-1, new Fraction(1, -2).Numerator);
        Assert.Equal(2, new Fraction(-1, 2).Denominator);
    }
}
