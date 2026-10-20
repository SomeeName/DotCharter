namespace DotCharter.Tests.Core;

/// <summary>
///     The FEHLER tab's location cell: <c>M19.3 · 02:13.16</c>.
///     <para>
///         Every one of these is a claim the row makes to a charter about WHERE
///         to look, and the two that matter most are the negatives: a chart with
///         no measure markers must not be given a bar number, and an issue with
///         no time must not be given a position at all. Both used to be the kind
///         of thing a UI invents rather than admits.
///     </para>
/// </summary>
public sealed class ChartPositionLabelTests
{
    /// <summary>120 BPM, 4/4 — a beat every 500 ms, a bar every four.</summary>
    private static List<BeatMarker> Grid(int beats = 600, bool withMeasures = true) =>
        Enumerable.Range(0, beats)
            .Select(i => new BeatMarker
            {
                Time = i * 500.0,
                Bpm = 120,
                IsMeasure = withMeasures && i % 4 == 0
            })
            .ToList();

    [Fact]
    public void ABarAndABeatAreCountedTheWayTheStatusBarCountsThem()
    {
        var beats = Grid();

        // Beat 0 is bar 1, beat 1 of that bar.
        Assert.Equal("M1.1", ChartPositionLabel.Measure(beats, 0));
        Assert.Equal("M1.4", ChartPositionLabel.Measure(beats, 1_500));
        // The next measure marker opens bar 2.
        Assert.Equal("M2.1", ChartPositionLabel.Measure(beats, 2_000));
        // 133_160 ms is beat 266 → bar 1 + 266/4 = 67, third beat of it.
        Assert.Equal("M67.3", ChartPositionLabel.Measure(beats, 133_160));
    }

    /// <summary>A position before the first beat belongs to the first bar — it
    /// is the pickup, not bar 0 and not "no position".</summary>
    [Fact]
    public void APreRollPositionIsTheFirstBar() => Assert.Equal("M1.1", ChartPositionLabel.Measure(Grid(), -900));

    /// <summary>
    ///     A beat map with no measure marker anywhere cannot be barred, so it
    ///     gets NO bar token — not "M1.57", which is what a running counter
    ///     produces and what a reader would act on.
    /// </summary>
    [Fact]
    public void AChartWithNoMeasureMarkerIsNeverGivenABarNumber()
    {
        Assert.Null(ChartPositionLabel.Measure(Grid(withMeasures: false), 133_160));
        Assert.Null(ChartPositionLabel.Measure(new List<BeatMarker>(), 133_160));
        Assert.Null(ChartPositionLabel.Measure(null, 133_160));

        // …and the joined readout degrades to the time alone rather than to "".
        Assert.Equal("02:13.16", ChartPositionLabel.Format(Grid(withMeasures: false), 133_160));
    }

    [Fact]
    public void TheTimeIsTheEditorsOwnMmSsFf()
    {
        Assert.Equal("02:13.16", ChartPositionLabel.Time(133_160));
        Assert.Equal("00:00.00", ChartPositionLabel.Time(0));
        // 999.9 must not round UP into a second the playhead has not reached.
        Assert.Equal("00:00.99", ChartPositionLabel.Time(999.9));
        // A negative time clamps rather than printing a negative clock.
        Assert.Equal("00:00.00", ChartPositionLabel.Time(-4_000));
    }

    [Fact]
    public void TheJoinedReadoutIsBarThenTime() => Assert.Equal("M67.3 · 02:13.16", ChartPositionLabel.Format(Grid(), 133_160));

    /// <summary>
    ///     An issue with no time (an arrangement-wide finding like
    ///     <c>NoEndPhrase</c>) gets an EMPTY cell. The mockup prints a phrase
    ///     name there; <c>ArrangementIssue</c> carries no such field, and a
    ///     blank is the honest answer.
    /// </summary>
    [Fact]
    public void NoTimeMeansNoLocation() => Assert.Equal(string.Empty, ChartPositionLabel.Format(Grid(), null));
}
