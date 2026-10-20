using DotCharter.Services.Editing.Quality;

namespace DotCharter.Tests.Services.Editing.Quality;

/// <summary>
///     #690: the quality report checks a COPY of the chart off the UI thread and rebinds what it
///     found to the live chart by position. The check reads the copy and nothing else, an element
///     that moved since the copy is dropped from its finding, and a live chart whose arrangements no
///     longer line up gets no report.
/// </summary>
public sealed class QualityReportSnapshotTests
{
    private static readonly ChartQualityReporter Reporter = new(new ArrangementValidationService(new FakeAppLogger()));

    /// <summary>One arrangement over a 16-beat grid, two levels, a fret 25 on the hard one.</summary>
    private static (ChartProject Project, ChartLevel Hard, GuitarNote Bad) Chart()
    {
        var project = new ChartProject { Bpm = 120 };
        for (var i = 0; i < 16; i++)
            project.Beats.Add(new BeatMarker { Time = i * 500, Bpm = 120, BeatsInMeasure = 4, NoteDenominator = 4, IsMeasure = i % 4 == 0 });
        var arrangement = new Arrangement { Name = "Lead" };
        arrangement.Levels.Add(new ChartLevel { DifficultyIndex = 0 });
        var hard = new ChartLevel { DifficultyIndex = 1 };
        for (var beat = 4; beat < 12; beat++)
            hard.Notes.Add(new GuitarNote { Position = new FractionalPosition(beat), StringIndex = 1, Fret = 5 });
        hard.Notes[3].Fret = 25;
        arrangement.Levels.Add(hard);
        project.Arrangements.Add(arrangement);
        return (project, hard, hard.Notes[3]);
    }

    private static QualityFinding FretFinding(ChartQualityReport report) =>
        report.Findings.First(f => f.Rule?.Type == ArrangementIssueType.FretNumberMoreThan24);

    [Fact]
    public void TheCheck_ReadsTheCopy_AndTheReportLandsOnTheLiveChart()
    {
        var (live, hard, bad) = Chart();
        var snapshot = QualityReportSnapshot.Take(live, null);

        // Whatever happens to the live chart after the copy, the check sees the chart as copied.
        bad.Fret = 5;
        var check = snapshot.Check(Reporter, null);
        bad.Fret = 25;

        var copied = FretFinding(check.Report);
        Assert.NotSame(hard, copied.Level);
        Assert.Same(snapshot.Copy.Arrangements[0], copied.Arrangement);

        var report = snapshot.Rebind(check)!;
        var finding = FretFinding(report);
        Assert.Same(live.Arrangements[0], finding.Arrangement);
        Assert.Same(hard, finding.Level);
        Assert.Same(bad, finding.Element);
        Assert.Same(bad, finding.Rule!.Note);
        Assert.Same(live.Arrangements[0], Assert.Single(report.Arrangements).Arrangement);
    }

    [Fact]
    public void AnElementThatMoved_IsDroppedFromItsFinding_WhichKeepsItsTime()
    {
        var (live, hard, bad) = Chart();
        var snapshot = QualityReportSnapshot.Take(live, null);
        var check = snapshot.Check(Reporter, null);

        bad.Position = new FractionalPosition(13);
        var finding = FretFinding(snapshot.Rebind(check)!);

        Assert.Null(finding.Element);
        Assert.Null(finding.Rule!.Note);
        Assert.Same(hard, finding.Level);
        Assert.Equal(FretFinding(check.Report).TimeMs, finding.TimeMs);
    }

    [Fact]
    public void ALiveChartWhoseArrangementsChanged_GetsNoReport()
    {
        var (live, _, _) = Chart();
        var snapshot = QualityReportSnapshot.Take(live, null);
        var check = snapshot.Check(Reporter, null);

        live.Arrangements.Add(new Arrangement { Name = "Rhythm" });

        Assert.Null(snapshot.Rebind(check));
    }
}
