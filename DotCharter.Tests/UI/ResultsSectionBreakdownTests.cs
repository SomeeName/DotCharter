using DotCharter.UI.Converters;
using DotCharter.UI.ViewModels.PlayMode;

namespace DotCharter.Tests.UI;

/// <summary>
///     The results screen's per-section accuracy bar chart
///     (<c>Results.dc.html</c>): the banding, the bar length, and which rows
///     offer to practise.
/// </summary>
public sealed class ResultsSectionBreakdownTests
{
    private static SectionRow Row(double accuracy, int misses = 0, bool played = true) =>
        new("chorus", "0:30", $"{accuracy * 100:0}%", misses, played, 30_000)
        {
            Accuracy = accuracy,
            Localization = FakeLocalization.English()
        };

    /// <summary>The three bands are exclusive — a section is exactly one
    /// colour. Overlapping thresholds would light two bars at once, which is how
    /// a stacked-bar version of this would have failed silently.</summary>
    [Theory]
    [InlineData(1.00)]
    [InlineData(0.90)]
    [InlineData(0.895)]
    [InlineData(0.75)]
    [InlineData(0.74)]
    [InlineData(0.0)]
    public void EverySectionLandsInExactlyOneBand(double accuracy)
    {
        var row = Row(accuracy);
        var lit = new[] { row.IsStrong, row.IsFair, row.IsWeak }.Count(b => b);
        Assert.Equal(1, lit);
    }

    [Theory]
    [InlineData(0.97, true, false, false)]
    [InlineData(0.90, true, false, false)]
    [InlineData(0.88, false, true, false)]
    [InlineData(0.75, false, true, false)]
    [InlineData(0.71, false, false, true)]
    public void TheBandsMatchTheMockupsThresholds(
        double accuracy, bool strong, bool fair, bool weak)
    {
        var row = Row(accuracy);
        Assert.Equal(strong, row.IsStrong);
        Assert.Equal(fair, row.IsFair);
        Assert.Equal(weak, row.IsWeak);
    }

    /// <summary>The brush the bar is painted with agrees with the row's own
    /// flags. They are computed in two different places, and a chart whose
    /// colour disagrees with its own legend is worse than no colour.</summary>
    [Theory]
    [InlineData(0.97, "SuccessColor")]
    [InlineData(0.90, "SuccessColor")]
    [InlineData(0.82, "WarningColor")]
    [InlineData(0.75, "WarningColor")]
    [InlineData(0.40, "DangerColor")]
    public void TheBarBrushAgreesWithTheRowsBand(double accuracy, string expectedKey)
    {
        Assert.Equal(expectedKey, SectionAccuracyBrushConverter.ResourceKeyFor(accuracy));

        var row = Row(accuracy);
        var fromRow = row.IsStrong ? "SuccessColor" : row.IsFair ? "WarningColor" : "DangerColor";
        Assert.Equal(expectedKey, fromRow);
    }

    /// <summary>
    ///     An UNPLAYED section is not a zero-scoring one. It gets no band, no
    ///     bar and no practice offer — "you stopped before here" and "you played
    ///     here and hit nothing" are different facts and the old row printed
    ///     them the same.
    /// </summary>
    [Fact]
    public void AnUnplayedSectionHasNoBandAndNoBar()
    {
        var row = Row(0.0, misses: 0, played: false);

        Assert.False(row.IsStrong);
        Assert.False(row.IsFair);
        Assert.False(row.IsWeak);
        Assert.Equal(0, row.BarFraction);
        Assert.False(row.CanPractise);
        Assert.Equal("", row.MissesLabel);
    }

    /// <summary>The bar is the accuracy, clamped. A judging bug that produced
    /// 1.2 must not draw a bar past its track.</summary>
    [Theory]
    [InlineData(0.5, 0.5)]
    [InlineData(1.0, 1.0)]
    [InlineData(1.4, 1.0)]
    [InlineData(-0.2, 0.0)]
    public void TheBarLengthIsTheClampedAccuracy(double accuracy, double expected) => Assert.Equal(expected, Row(accuracy).BarFraction, 6);

    /// <summary>
    ///     "Practice" is offered on the rows that dropped notes, and only those.
    ///     Offering it on a clean section is offering to drill a solved thing;
    ///     the mockup puts the link on exactly the weak rows.
    /// </summary>
    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(21, true)]
    public void PractiseIsOfferedOnlyWhereNotesWereDropped(int misses, bool expected) => Assert.Equal(expected, Row(0.8, misses).CanPractise);

    /// <summary>A clean section says so in words rather than printing "0".</summary>
    [Fact]
    public void ACleanSectionSaysCleanAndAMissedOneCounts()
    {
        Assert.DoesNotContain("0", Row(1.0).MissesLabel, StringComparison.Ordinal);
        Assert.Contains("7", Row(0.8, 7).MissesLabel, StringComparison.Ordinal);
    }
}
