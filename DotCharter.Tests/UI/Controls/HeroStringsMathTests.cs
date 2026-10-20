using DotCharter.UI.Controls;

namespace DotCharter.Tests.UI.Controls;

/// <summary>
///     The motion behind Home's strummed-strings hero (<see cref="HeroStringsBackdrop" />): a real
///     neck's fret spacing, a strum from low E up, strings that ring out and then hold still, so
///     the backdrop can stop repainting between strums.
/// </summary>
public sealed class HeroStringsMathTests
{
    [Fact]
    public void TheTwelfthFret_HalvesTheString_AndFretsCloseUp()
    {
        Assert.Equal(50, HeroStringsMath.FretPosition(12, 100), 6);
        var first = HeroStringsMath.FretPosition(1, 100) - HeroStringsMath.FretPosition(0, 100);
        var twelfth = HeroStringsMath.FretPosition(12, 100) - HeroStringsMath.FretPosition(11, 100);
        Assert.True(twelfth < first, $"fret 12 is {twelfth:0.00} wide against fret 1's {first:0.00}");
    }

    [Fact]
    public void AStrum_GoesFromLowEUp()
    {
        var t = 0.02;
        Assert.NotNull(HeroStringsMath.SincePluck(0, t));
        Assert.Null(HeroStringsMath.SincePluck(5, t));
        Assert.True(HeroStringsMath.Envelope(0, 0.3) > HeroStringsMath.Envelope(5, 0.3));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    public void AStringsEnds_NeverMove(int index)
    {
        for (var t = 0.0; t < 2; t += 0.037)
        {
            Assert.Equal(0, HeroStringsMath.Displacement(index, 0, t), 9);
            Assert.Equal(0, HeroStringsMath.Displacement(index, 1, t), 9);
        }
    }

    [Fact]
    public void TheStrings_RingOut_BeforeTheNextStrum_AndStayWithinTheirSwing()
    {
        var justBefore = HeroStringsMath.StrumPeriod - 0.01;
        Assert.True(HeroStringsMath.IsSettled(justBefore), "the strings are still ringing when the next strum comes");
        Assert.False(HeroStringsMath.IsSettled(HeroStringsMath.StrumPeriod + 0.3));

        for (var t = 0.0; t < HeroStringsMath.StrumPeriod * 2; t += 0.011)
        {
            for (var i = 0; i < HeroStringsMath.StringCount; i++)
                Assert.True(Math.Abs(HeroStringsMath.Displacement(i, 0.5, t)) <= HeroStringsMath.PeakAmplitude + 1e-9);
        }
    }
}
