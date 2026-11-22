namespace DotCharter.Tests.Services.Editing.Validation;

public sealed class ShowlightRuleTests
{
    [Theory]
    [InlineData(24, "Validation_ShowlightsMissingBeam")]
    [InlineData(42, "Validation_ShowlightsMissingFog")]
    public void MissingLane_WarnsAtTheFirstShowlight(int note, string key)
    {
        var project = CreateProject();
        project.Showlights.Add(new Showlight { Note = note, Position = new FractionalPosition(4) });
        project.Showlights.Add(new Showlight { Note = 66, Position = new FractionalPosition(2) });

        var finding = Assert.Single(Validate(project), issue => issue.MessageKey == key);
        Assert.Equal(ArrangementIssueSeverity.Warning, finding.Severity);
        Assert.Equal(1000, finding.TimeMs);
    }

    [Fact]
    public void AListWithoutFogOrBeams_WarnsForBothLanes()
    {
        var project = CreateProject();
        project.Showlights.Add(new Showlight { Note = 66, Position = new FractionalPosition(2) });

        var keys = Validate(project).Select(issue => issue.MessageKey).ToArray();

        Assert.Contains("Validation_ShowlightsMissingFog", keys);
        Assert.Contains("Validation_ShowlightsMissingBeam", keys);
    }

    [Fact]
    public void NoAuthoredShowlights_UsesExportDefaultsWithoutWarnings() => Assert.Empty(ShowlightFindings(CreateProject()));

    [Theory]
    [InlineData(null)]
    [InlineData(5500.0)]
    public void MissingClosingFogChange_WarnsAtTheLastFog(double? audioLengthMs)
    {
        var project = CreateProject();
        project.Showlights.Add(new Showlight { Type = "FOG_BLUE", Position = new FractionalPosition(6) });
        project.Showlights.Add(new Showlight { Note = 24, Position = new FractionalPosition(1) });
        project.Showlights.Add(new Showlight { Note = 42, Position = new FractionalPosition(11) });
        project.Showlights.Add(new Showlight { Note = 28, Position = new FractionalPosition(11) });

        var finding = Assert.Single(ShowlightFindings(project, audioLengthMs));
        Assert.Equal("Validation_ShowlightsClosingFog", finding.MessageKey);
        Assert.Equal(ArrangementIssueSeverity.Warning, finding.Severity);
        Assert.Equal(3000, finding.TimeMs);
    }

    [Fact]
    public void AClosingFogColourChange_UsesTheAttachedAudioEnd()
    {
        var project = CreateProject();
        project.Showlights.Add(new Showlight { Note = 24, Position = new FractionalPosition(6) });
        project.Showlights.Add(new Showlight { Type = "BEAMS_OFF", Position = new FractionalPosition(1) });
        project.Showlights.Add(new Showlight { Type = "FOG_BLUE", Position = new FractionalPosition(10) });

        Assert.Empty(ShowlightFindings(project, 5000));
    }

    private static IReadOnlyList<ArrangementIssue> Validate(ChartProject project, double? audioLengthMs = null) =>
        new ArrangementValidationService(new FakeAppLogger())
            .Validate(project.Arrangements[0], project.Beats, audioLengthMs, project);

    private static IEnumerable<ArrangementIssue> ShowlightFindings(ChartProject project, double? audioLengthMs = null) =>
        Validate(project, audioLengthMs).Where(issue =>
            issue.MessageKey?.StartsWith("Validation_Showlights", StringComparison.Ordinal) == true);

    private static ChartProject CreateProject()
    {
        var project = new ChartProject();
        for (var i = 0; i < 12; i++)
            project.Beats.Add(new BeatMarker { Time = i * 500 });
        project.Arrangements.Add(new Arrangement());
        return project;
    }
}
