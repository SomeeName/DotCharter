using DotCharter.Services.Parsing.Xml;

namespace DotCharter.Tests.Services.Editing.Validation;

public sealed class SectionNameVarietyRuleTests
{
    private const string Key = "Validation_SectionNamesNotVaried";

    [Fact]
    public void ADominantName_WarnsOnceAtItsFirstSectionWithTheCount()
    {
        var project = CreateProject("verse", "intro", "intro", "intro", "chorus", "intro", "intro", "intro", "intro");

        var finding = Assert.Single(Findings(project));
        Assert.Equal(ArrangementIssueType.SectionNameVariety, finding.Type);
        Assert.Equal(ArrangementIssueSeverity.Warning, finding.Severity);
        Assert.Equal(1000, finding.TimeMs);
        Assert.Equal(new object[] { 7, 9, "intro" }, finding.MessageArgs);
        Assert.StartsWith("7 of 9 sections are named “intro”.", finding.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void NamesDifferingOnlyInCase_CountAsOneName()
    {
        var project = CreateProject("Intro", "INTRO", "intro", "verse", "intro");

        var finding = Assert.Single(Findings(project));
        Assert.Equal(new object[] { 4, 5, "Intro" }, finding.MessageArgs);
        Assert.Equal(0, finding.TimeMs);
    }

    [Theory]
    [InlineData("intro", "intro", "intro", "verse", "chorus", "solo")]
    [InlineData("chorus", "chorus", "chorus", "chorus", "verse", "verse")]
    public void AShareAtOrBelowTwoThirds_DoesNotWarn(params string[] names) =>
        Assert.Empty(Findings(CreateProject(names)));

    [Theory]
    [InlineData("intro")]
    [InlineData("intro", "intro", "intro")]
    [InlineData("riff", "riff", "riff", "noguitar")]
    public void AChartWithFewerThanFiveSections_DoesNotWarn(params string[] names) =>
        Assert.Empty(Findings(CreateProject(names)));

    [Fact]
    public void VariedNames_DoNotWarn() =>
        Assert.Empty(Findings(CreateProject("intro", "verse", "chorus", "verse", "chorus", "solo", "outro")));

    [Fact]
    public void EventPointsWithoutASection_AreNotCounted()
    {
        var project = CreateProject("intro", "intro", "intro", "verse", "intro");
        project.Arrangements[0].EventPoints.Add(new EventPoint { Phrase = "riff", Position = new FractionalPosition(11) });
        project.Arrangements[0].EventPoints.Add(new EventPoint { Phrase = "riff", Position = new FractionalPosition(13) });

        var finding = Assert.Single(Findings(project));
        Assert.Equal(new object[] { 4, 5, "intro" }, finding.MessageArgs);
    }

    [Fact]
    public void NoGuitarGaps_CountNeitherAsTheDominantNameNorInTheTotal()
    {
        Assert.Empty(Findings(CreateProject("noguitar", "intro", "noguitar", "verse", "noguitar", "noguitar", "noguitar")));

        var finding = Assert.Single(Findings(CreateProject("intro", "intro", "noguitar", "intro", "verse", "intro", "noguitar")));
        Assert.Equal(new object[] { 4, 5, "intro" }, finding.MessageArgs);
    }

    [Fact]
    public void AnXmlRoundTrip_AddingTheClosingNoGuitar_DoesNotStartAWarning()
    {
        var project = CreateProject("intro", "intro", "intro", "intro");
        var level = new ChartLevel();
        for (var beat = 0; beat < 8; beat++)
            level.Notes.Add(new GuitarNote { Position = new FractionalPosition(beat), Fret = 5, StringIndex = 2 });
        project.Arrangements[0].Levels.Add(level);
        Assert.Empty(Findings(project));

        var dir = Path.Combine(Path.GetTempPath(), "DotCharterSectionVariety", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "song.xml");
            var serializer = new XmlChartSerializer();
            serializer.Save(path, project);
            var loaded = serializer.Load(path);

            Assert.Contains(loaded.Arrangements[0].EventPoints, point => point.Section == "noguitar");
            Assert.Empty(Findings(loaded));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void NumberedNames_GroupUnderTheLabelTheGameShows_AndTheMessageKeepsTheNameAsWritten()
    {
        var finding = Assert.Single(Findings(CreateProject("verse", "chorus", "verse2", "Verse3", "verse")));

        Assert.Equal(new object[] { 4, 5, "verse" }, finding.MessageArgs);
    }

    [Fact]
    public void TheMessage_AsksToCheckTheLabel_RatherThanToRename()
    {
        var finding = Assert.Single(Findings(CreateProject("intro", "intro", "intro", "intro", "intro")));

        Assert.Contains("check that", finding.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("name each part", finding.Message, StringComparison.Ordinal);
    }

    private static IEnumerable<ArrangementIssue> Findings(ChartProject project) =>
        new ArrangementValidationService(new FakeAppLogger())
            .Validate(project.Arrangements[0], project.Beats, null, project)
            .Where(issue => issue.MessageKey == Key);

    private static ChartProject CreateProject(params string[] sectionNames)
    {
        var project = new ChartProject();
        for (var i = 0; i < 24; i++)
            project.Beats.Add(new BeatMarker { Time = i * 500 });
        var arrangement = new Arrangement();
        for (var i = 0; i < sectionNames.Length; i++)
        {
            arrangement.EventPoints.Add(new EventPoint
            {
                Phrase = "riff", Section = sectionNames[i], Position = new FractionalPosition(i * 2)
            });
        }

        project.Arrangements.Add(arrangement);
        return project;
    }
}
