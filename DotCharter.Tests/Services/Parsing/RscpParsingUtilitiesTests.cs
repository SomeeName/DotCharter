using DotCharter.Services.Parsing.Rscp;

namespace DotCharter.Tests.Services.Parsing;

public class RscpParsingUtilitiesTests
{
    [Theory]
    [InlineData("2147483647 0/1")]
    [InlineData("-2147483648 0/1")]
    [InlineData("1000001 0/1")]
    [InlineData("-1000001 0/1")]
    public void ParseFraction_PositionOutsideImportRange_ReturnsZero(string text)
        => Assert.Equal(FractionalPosition.Zero, RscpParsingUtilities.ParseFraction(text));

    [Theory]
    [InlineData("1000000 0/1", 1000000)]
    [InlineData("-1000000 0/1", -1000000)]
    [InlineData("-4 0/1", -4)]
    public void ParseFraction_PositionInsideImportRange_IsPreserved(string text, int expectedBeat)
        => Assert.Equal(new FractionalPosition(expectedBeat), RscpParsingUtilities.ParseFraction(text));

    [Fact]
    public void ParseFraction_ValidString_ReturnsPosition()
    {
        var p = RscpParsingUtilities.ParseFraction("3 1/2");
        Assert.Equal(3, p.BeatId);
        Assert.True(Math.Abs(p.DoubleValue - 3.5) < 1e-9);
    }

    [Fact]
    public void ParseFraction_Null_ReturnsZero() => Assert.Equal(FractionalPosition.Zero, RscpParsingUtilities.ParseFraction(null));

    [Fact]
    public void ParseFraction_Garbage_ReturnsZero() => Assert.Equal(FractionalPosition.Zero, RscpParsingUtilities.ParseFraction("not-a-fraction"));

    [Fact]
    public void ParseFraction_Empty_ReturnsZero() => Assert.Equal(FractionalPosition.Zero, RscpParsingUtilities.ParseFraction(""));
}
