namespace DotCharter.Tests.Services.Parsing;

/// <summary>The tests of <see cref="ScrollSpeedRoundTripTests" /> that need no package reader; its fixtures are in <c>ScrollSpeedRoundTripTests.Fixtures.cs</c>.</summary>
public sealed partial class ScrollSpeedRoundTripTests
{
    /// <summary>The cloner is load-bearing: every save, undo/redo and crash-recovery
    /// snapshot goes through it, so a field missing there vanishes on save.</summary>
    [Fact]
    public void TheCloner_CarriesScrollSpeed()
    {
        var source = new Arrangement { ScrollSpeed = 2.4 };
        Assert.Equal(2.4, ChartProjectCloner.CloneArrangement(source).ScrollSpeed);

        var unset = new Arrangement();
        Assert.Null(ChartProjectCloner.CloneArrangement(unset).ScrollSpeed);
    }
}
