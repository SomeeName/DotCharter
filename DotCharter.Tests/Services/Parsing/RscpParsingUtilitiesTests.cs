using DotCharter.Services.Parsing.Rscp;

namespace DotCharter.Tests.Services.Parsing;

public class RscpParsingUtilitiesTests
{
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
