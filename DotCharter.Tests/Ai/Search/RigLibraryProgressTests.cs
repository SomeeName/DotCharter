using DotCharter.Services.Ai.Match.Search;

namespace DotCharter.Tests.Ai.Search;

/// <summary>
///     The status lines the library reports while it builds. "Building tone
///     library: 1/4000 rigs rendered" was true and useless (owner, 2026-09-14);
///     every line now carries progress AND an estimate.
/// </summary>
public sealed class RigLibraryProgressTests
{
    [Fact]
    public void ABuildLineCarriesProgressAndAnEstimate()
    {
        Assert.Equal("Building tone library: 12/96 · ~3 min left",
            RigLibraryProgress.Building(12, 96, TimeSpan.FromSeconds(170)));
        Assert.Equal("Building tone library: 1/96 · ~45 s left",
            RigLibraryProgress.Building(1, 96, TimeSpan.FromSeconds(44.2)));
        Assert.Equal("Building tone library: 100/4000 · ~2 h 46 min left",
            RigLibraryProgress.Building(100, 4000, TimeSpan.FromSeconds(3900 * 2.55)));
    }

    /// <summary>Before the first render there is no rate, so no clause — never
    /// a made-up number; and on the LAST row no clause either, because
    /// "3/3 · ~1 s left" is a lie by rounding.</summary>
    [Fact]
    public void NoEstimateBeforeTheFirstRender_AndNoneOnTheLastRow()
    {
        Assert.Equal("Building tone library: 0/96", RigLibraryProgress.Building(0, 96, null));
        Assert.Equal("Building tone library: 96/96", RigLibraryProgress.Building(96, 96, TimeSpan.FromSeconds(1)));
    }

    [Theory]
    [InlineData(0.0, "1 s")]
    [InlineData(44.2, "45 s")]
    [InlineData(59.9, "60 s")]
    [InlineData(60.0, "1 min")]
    [InlineData(170.0, "3 min")]
    [InlineData(3599.0, "60 min")]
    [InlineData(3600.0, "1 h")]
    [InlineData(4800.0, "1 h 20 min")]
    [InlineData(9945.0, "2 h 46 min")]
    public void ADurationReadsTheWayAPersonSaysIt(double seconds, string expected) => Assert.Equal(expected, RigLibraryProgress.Duration(TimeSpan.FromSeconds(seconds)));

    /// <summary>The panel reads its progress bar's fraction back OUT of the
    /// line, so the parser is the inverse of the formatter and refuses every
    /// other line.</summary>
    [Theory]
    [InlineData("Building tone library: 12/96 · ~3 min left", true, 12, 96)]
    [InlineData("Building tone library: 96/96", true, 96, 96)]
    [InlineData("Building tone library: 0/4000", true, 0, 4000)]
    [InlineData("Tone library: 96/4000 rigs (growing in the background)", false, 0, 0)]
    [InlineData("Searching for a rig…", false, 0, 0)]
    [InlineData("Building tone library: 97/96", false, 0, 0)]
    [InlineData("Building tone library: x/96", false, 0, 0)]
    [InlineData("", false, 0, 0)]
    [InlineData(null, false, 0, 0)]
    public void ABuildLineParsesBackToItsCounts(string? line, bool expected, int done, int total)
    {
        Assert.Equal(expected, RigLibraryProgress.TryParseBuilding(line, out var d, out var t));
        Assert.Equal(done, d);
        Assert.Equal(total, t);
    }

    [Fact]
    public void FormatAndParseAreInverses()
    {
        var line = RigLibraryProgress.Building(37, 96, TimeSpan.FromSeconds(150));
        Assert.True(RigLibraryProgress.TryParseBuilding(line, out var done, out var total));
        Assert.Equal((37, 96), (done, total));
    }

    [Fact]
    public void TheOtherLinesNameTheRowCounts()
    {
        Assert.Equal("Tone library: 4000 rigs from cache", RigLibraryProgress.FromCache(4000));
        Assert.Equal("Tone library: 96/4000 rigs (growing in the background)",
            RigLibraryProgress.Partial(96, 4000));
        Assert.Equal("Tone library: checking an older library (96 rigs)…", RigLibraryProgress.CheckingLegacy(96));
    }

    /// <summary>The estimator is an average over every render so far — after
    /// four renders in ten seconds, 92 more are ~230 s — and says nothing
    /// before the first.</summary>
    [Fact]
    public void TheEstimatorAveragesOverEveryRenderSoFar()
    {
        var elapsed = TimeSpan.Zero;
        var estimator = new RigLibraryProgress.Estimator(() => elapsed);

        Assert.Null(estimator.Remaining(96));

        for (var i = 0; i < 4; i++)
        {
            elapsed += TimeSpan.FromSeconds(2.5);
            estimator.Tick();
        }

        Assert.Equal(TimeSpan.FromSeconds(230), estimator.Remaining(92));
        Assert.Equal(TimeSpan.Zero, estimator.Remaining(0));
        Assert.Equal("Building tone library: 4/96 · ~4 min left",
            RigLibraryProgress.Building(4, 96, estimator.Remaining(92)));
    }
}
