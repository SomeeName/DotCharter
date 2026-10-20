using DotCharter.UI.Controls;

namespace DotCharter.Tests.UI.Controls;

/// <summary>
///     The length box's text ↔ beats conversion (04-inspector-redesign.md §6.2).
///     Grid-relative, never milliseconds — owner-confirmed.
/// </summary>
public class FractionalLengthFormatTests
{
    [Theory]
    [InlineData(0.0, null)]
    [InlineData(0.00001, null)]
    // A beat IS a quarter note, so half a beat is an eighth.
    [InlineData(0.5, "1/8")]
    [InlineData(0.25, "1/16")]
    [InlineData(0.75, "3/16")]
    [InlineData(0.125, "1/32")]
    // From one beat up we speak in beats: that is what the grid divisor and
    // ] / [ actually step in.
    [InlineData(1.0, "1")]
    [InlineData(2.0, "2")]
    [InlineData(1.5, "1.5")]
    public void FormatsGridRelative(double beats, string? expected) =>
        Assert.Equal(expected, FractionalLengthFormat.Format(beats));

    [Fact]
    public void TripletsGetAThreeBasedDenominatorRatherThanADecimal() =>
        // A third of a beat is a twelfth note. Falling back to "0.333" here
        // would print a length no charter could type back in.
        Assert.Equal("1/12", FractionalLengthFormat.Format(1.0 / 3.0));

    [Theory]
    [InlineData("1/8", 0.5)]
    [InlineData("3/16", 0.75)]
    [InlineData("1/4", 1.0)]
    [InlineData("2", 2.0)]
    [InlineData("1.5", 1.5)]
    [InlineData("", 0.0)]
    [InlineData("   ", 0.0)]
    [InlineData("0", 0.0)]
    public void ParsesWhatItPrints(string text, double expected)
    {
        Assert.True(FractionalLengthFormat.TryParse(text, out var beats));
        Assert.Equal(expected, beats, 5);
    }

    [Fact]
    public void TheUnitWordIsToleratedInEitherLanguage()
    {
        Assert.True(FractionalLengthFormat.TryParse("2 beats", out var en));
        Assert.Equal(2.0, en, 5);
        Assert.True(FractionalLengthFormat.TryParse("2 Schläge", out var de));
        Assert.Equal(2.0, de, 5);
    }

    [Fact]
    public void AGermanDecimalCommaIsAccepted()
    {
        // The box prints invariant, but a German keyboard produces a comma and
        // refusing it would read as a silent no-op.
        Assert.True(FractionalLengthFormat.TryParse("1,5", out var beats));
        Assert.Equal(1.5, beats, 5);
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("1/0")]
    [InlineData("-2")]
    [InlineData("1/-4")]
    public void GarbageIsRefusedRatherThanSilentlyReadAsZero(string text) =>
        Assert.False(FractionalLengthFormat.TryParse(text, out _));

    [Fact]
    public void EveryFormattedValueRoundTrips()
    {
        foreach (var beats in new[] { 0.125, 0.25, 0.5, 0.75, 1.0, 1.5, 2.0, 3.0, 4.0 })
        {
            var text = FractionalLengthFormat.Format(beats);
            Assert.NotNull(text);
            Assert.True(FractionalLengthFormat.TryParse(text, out var parsed),
                $"'{text}' does not parse back.");
            Assert.Equal(beats, parsed, 5);
        }
    }
}
