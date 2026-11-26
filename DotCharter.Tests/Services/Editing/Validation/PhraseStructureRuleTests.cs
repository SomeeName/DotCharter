using DotCharter.Services.Editing.Validation.Rules;

namespace DotCharter.Tests.Services.Editing.Validation;

public sealed class PhraseStructureRuleTests
{
    // Beats every 500 ms, a bar line on every fourth beat: measure m starts at beat 4m.
    private static FractionalPosition Bar(int measure) => new(measure * 4);

    [Fact]
    public void Four_measure_phrases_on_bar_lines_with_their_sections_are_clean()
    {
        var project = CreateProject();
        AddPhrase(project, Bar(0), "COUNT");
        for (var m = 1; m < 13; m += 4) AddPhrase(project, Bar(m), $"p{m}", "riff");
        AddPhrase(project, Bar(13), "END", "noguitar");
        AddNotes(project, Bar(1), Bar(13));

        Assert.Empty(Findings(project));
    }

    [Fact]
    public void A_phrase_of_sixteen_measures_is_not_too_long()
    {
        var project = Framed(Bar(1), Bar(17), Bar(19));

        Assert.Empty(Findings(project));
    }

    [Fact]
    public void A_phrase_over_sixteen_measures_is_too_long()
    {
        var project = Framed(Bar(1), Bar(18), Bar(20));

        var finding = Assert.Single(Findings(project));
        Assert.Equal(ArrangementIssueType.PhraseTooLong, finding.Type);
        Assert.Equal("D80", finding.Type.GetCode());
        Assert.Equal(new object[] { "p1", 17, 16 }, finding.MessageArgs);
    }

    [Fact]
    public void A_phrase_under_two_measures_is_too_short()
    {
        var project = Framed(Bar(1), Bar(5), Bar(6), Bar(10));

        var finding = Assert.Single(Findings(project));
        Assert.Equal(ArrangementIssueType.PhraseTooShort, finding.Type);
        Assert.Equal("D81", finding.Type.GetCode());
        Assert.Equal(Bar(5).GetPositionMs(project.Beats), finding.TimeMs);
    }

    [Fact]
    public void A_phrase_and_a_section_off_the_bar_line_are_no_finding()
    {
        // Charters place a phrase off the bar line on purpose (EOF's moveR); no editor flags it.
        var project = Framed(new FractionalPosition(2), Bar(2), new FractionalPosition(18), Bar(8));

        Assert.Empty(Findings(project));
    }

    [Fact]
    public void A_section_with_no_phrase_at_its_position_is_reported()
    {
        var project = Framed(Bar(1), Bar(5), Bar(9));
        project.Arrangements[0].EventPoints.Add(new EventPoint { Position = Bar(7), Section = "chorus" });

        var finding = Assert.Single(Findings(project));
        Assert.Equal(ArrangementIssueType.SectionWithoutPhrase, finding.Type);
        Assert.Equal("D83", finding.Type.GetCode());
        Assert.Equal(new object[] { "chorus" }, finding.MessageArgs);
    }

    [Fact]
    public void A_silence_split_into_two_noguitar_sections_is_reported_once()
    {
        var project = CreateProject();
        AddPhrase(project, Bar(0), "COUNT");
        AddPhrase(project, Bar(1), "p1", "riff");
        AddPhrase(project, Bar(5), "ng1", "noguitar");
        AddPhrase(project, Bar(7), "ng2", "noguitar");
        AddPhrase(project, Bar(9), "p2", "riff");
        AddPhrase(project, Bar(13), "END", "noguitar");
        AddNotes(project, Bar(1), Bar(5));
        AddNotes(project, Bar(9), Bar(13));

        var finding = Assert.Single(Findings(project));
        Assert.Equal(ArrangementIssueType.NoGuitarSectionSplit, finding.Type);
        Assert.Equal("D84", finding.Type.GetCode());
        Assert.Equal(Bar(7).GetPositionMs(project.Beats), finding.TimeMs);
    }

    [Fact]
    public void Two_noguitar_sections_with_music_between_them_are_not_a_split()
    {
        var project = Framed(Bar(1), Bar(5), Bar(9));
        project.Arrangements[0].EventPoints.Single(e => e.Position == Bar(5)).Section = "noguitar";
        project.Arrangements[0].EventPoints.Add(new EventPoint { Position = Bar(7), Phrase = "x", Section = "noguitar" });
        AddNotes(project, Bar(6), Bar(7));

        Assert.DoesNotContain(Findings(project), f => f.Type == ArrangementIssueType.NoGuitarSectionSplit);
    }

    [Fact]
    public void A_beat_map_without_bar_lines_is_not_judged_on_measures()
    {
        var project = Framed(Bar(1), Bar(12));
        foreach (var beat in project.Beats) beat.IsMeasure = false;

        Assert.Empty(Findings(project));
    }

    [Fact]
    public void A_generated_structure_is_clean()
    {
        var project = CreateProject();
        AddNotes(project, new FractionalPosition(6), Bar(30));

        Assert.True(DotCharter.Services.Export.PhraseSectionGenerator.GenerateIfAbsent(
            project.Arrangements[0], project.Beats, 70000));

        Assert.Empty(Findings(project));
        Assert.DoesNotContain(new ArrangementValidationService(new FakeAppLogger())
                .Validate(project.Arrangements[0], project.Beats, null, project),
            f => f.Type is ArrangementIssueType.SustainCrossesBoundary or ArrangementIssueType.HandShapeCrossesBoundary);
    }

    /// <summary>COUNT on bar 0, a riff phrase at each start, the last start is END.</summary>
    private static ChartProject Framed(params FractionalPosition[] starts)
    {
        var project = CreateProject();
        AddPhrase(project, Bar(0), "COUNT");
        for (var i = 0; i < starts.Length - 1; i++)
            AddPhrase(project, starts[i], $"p{starts[i].BeatId / 4}", "riff");
        AddPhrase(project, starts[^1], "END", "noguitar");
        AddNotes(project, starts[0], starts[^1]);
        return project;
    }

    private static IEnumerable<ArrangementIssue> Findings(ChartProject project) =>
        new ArrangementValidationService(new FakeAppLogger(), [new PhraseStructureRule()])
            .Validate(project.Arrangements[0], project.Beats, null, project);

    private static ChartProject CreateProject()
    {
        var project = new ChartProject();
        for (var i = 0; i < 160; i++)
            project.Beats.Add(new BeatMarker { Time = i * 500, IsMeasure = i % 4 == 0 });
        project.Arrangements.Add(new Arrangement());
        project.Arrangements[0].Levels.Add(new ChartLevel());
        return project;
    }

    private static void AddPhrase(ChartProject project, FractionalPosition at, string phrase, string section = "") =>
        project.Arrangements[0].EventPoints.Add(new EventPoint { Position = at, Phrase = phrase, Section = section });

    private static void AddNotes(ChartProject project, FractionalPosition from, FractionalPosition to)
    {
        for (var beat = from.BeatId; beat < to.BeatId; beat++)
        {
            project.Arrangements[0].Levels[0].Notes.Add(
                new GuitarNote { Position = new FractionalPosition(beat), Fret = 5, StringIndex = 2 });
        }
    }
}
