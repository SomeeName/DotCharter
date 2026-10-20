using DotCharter.Tone.UI.Controls.Fire;

namespace DotCharter.Tests.UI.Controls;

/// <summary>
///     The two forms of the Fire progress bar: one fill width, or N discrete
///     steps. The step count is also the segmented variant's repaint gate, so
///     it has to be exact — "session 2 of 5" must fill two, not one and not
///     three.
/// </summary>
public sealed class FireProgressGeometryTests
{
    [Theory]
    [InlineData(0.0, 0)]
    [InlineData(0.2, 1)]
    [InlineData(0.4, 2)]
    [InlineData(0.6, 3)]
    [InlineData(1.0, 5)]
    public void ARatioOfNOverFive_FillsExactlyNOfFive(double value, int expected)
        => Assert.Equal(expected, FireProgressGeometry.FilledSegments(value, 5));

    [Theory]
    [InlineData(-1.0, 0)]
    [InlineData(2.5, 5)]
    [InlineData(double.NaN, 0)]
    public void AnOutOfRangeRatio_ClampsRatherThanOverflowing(double value, int expected)
        => Assert.Equal(expected, FireProgressGeometry.FilledSegments(value, 5));

    [Theory]
    [InlineData(0.30, 1)]  // 1.2 of 4 — a ceiling would light two
    [InlineData(0.40, 2)]  // 1.6 of 4 — a floor would light one
    [InlineData(0.12, 0)]  // 0.48 of 4 — a ceiling would light one
    [InlineData(0.13, 1)]  // 0.52 of 4 — a floor would light none
    public void ThePartialStepRoundsToTheNearest_NotUpAndNotDown(double value, int expected)
        => Assert.Equal(expected, FireProgressGeometry.FilledSegments(value, 4));

    [Fact]
    public void AValueMovingInsideOneStep_NeverChangesTheFilledCount()
    {
        // The segmented bar's repaint gate. 0.41 → 0.49 of five steps is still
        // two lit dashes, and a bar bound to a live figure must not invalidate
        // for it.
        Assert.Equal(FireProgressGeometry.FilledSegments(0.41, 5),
            FireProgressGeometry.FilledSegments(0.49, 5));
        Assert.NotEqual(FireProgressGeometry.FilledSegments(0.49, 5),
            FireProgressGeometry.FilledSegments(0.71, 5));
    }

    [Fact]
    public void ZeroSegments_IsTheContinuousBarAndFillsNothing()
        => Assert.Equal(0, FireProgressGeometry.FilledSegments(0.8, 0));

    [Theory]
    [InlineData(0.0, 0.0)]
    [InlineData(0.5, 100.0)]
    [InlineData(1.0, 200.0)]
    [InlineData(1.6, 200.0)]
    [InlineData(-0.3, 0.0)]
    public void TheContinuousFillIsTheRatioOfTheTrack(double value, double expected)
        => Assert.Equal(expected, FireProgressGeometry.FillWidth(value, 200), 9);

    [Fact]
    public void SegmentsShareTheLeftoverWidthAfterTheGaps()
    {
        const double total = 100.0;
        const int count = 5;
        const double gap = FireProgressGeometry.SegmentGap;

        var rects = Enumerable.Range(0, count)
            .Select(i => FireProgressGeometry.SegmentBounds(i, count, total, 3, gap))
            .ToArray();

        var span = (total - (count - 1) * gap) / count;
        foreach (var rect in rects) Assert.Equal(span, rect.Width, 9);

        Assert.Equal(0.0, rects[0].X, 9);
        Assert.Equal(total, rects[^1].Right, 6);

        // Every gap is exactly the declared one — a segmented bar whose steps
        // touch reads as one bar with lighter parts.
        for (var i = 1; i < count; i++)
            Assert.Equal(gap, rects[i].X - rects[i - 1].Right, 6);
    }

    [Fact]
    public void TheSegmentedVariantIsThreeTallWithFivePixelGaps()
    {
        Assert.Equal(3.0, FireProgressGeometry.SegmentHeight);
        Assert.Equal(5.0, FireProgressGeometry.SegmentGap);
    }

    [Fact]
    public void TheContinuousTrackIsInsideTheThreeToSixPixelBand()
    {
        Assert.InRange(FireProgressGeometry.TrackHeight, 3.0, 6.0);
        Assert.InRange(FireProgressGeometry.ThinTrackHeight, 3.0, 6.0);
    }

    [Fact]
    public void ADegenerateRow_ReturnsAnEmptyRectRatherThanThrowing()
        => Assert.Equal(default, FireProgressGeometry.SegmentBounds(0, 0, 100, 3, 5));
}
