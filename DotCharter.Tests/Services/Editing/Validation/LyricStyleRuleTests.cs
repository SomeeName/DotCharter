using DotCharter.Core.Domain.Vocals;
using DotCharter.Services.Editing.Quality;
using DotCharter.Services.Editing.Validation.Rules;

namespace DotCharter.Tests.Services.Editing.Validation;

public sealed class LyricStyleRuleTests
{
    private const string Lowercase = "Validation_LyricLineStartsLowercase";
    private const string Punctuation = "Validation_LyricStrayPunctuation";
    private const string Unsplit = "Validation_LyricUnsplitSyllable";

    [Fact]
    public void ALineStartingLowercase_WarnsAtItsFirstSyllable()
    {
        var project = CreateProject(Line(2, "hold", "on"), Line(6, "Hold", "on"));

        var finding = Assert.Single(LyricFindings(project));
        Assert.Equal(Lowercase, finding.MessageKey);
        Assert.Equal(ArrangementIssueType.LyricStyle, finding.Type);
        Assert.Equal(ArrangementIssueSeverity.Warning, finding.Severity);
        Assert.Equal(1000, finding.TimeMs);
        Assert.Equal(new LyricPlace(0, 0, 0), finding.Lyric);
    }

    [Fact]
    public void AFindingNamesItsTrackPhraseAndSyllable()
    {
        var project = CreateProject(Line(2, "Hold", "on"), Line(6, "Stay", "close,"));
        var second = new VocalTrack();
        second.Phrases.Add(Line(4, "Go", "on to"));
        project.VocalTracks.Add(second);

        Assert.Equal([new LyricPlace(0, 1, 1), new LyricPlace(1, 0, 1)], LyricFindings(project).Select(f => f.Lyric));
    }

    [Fact]
    public void ALineEndFlagInsideAPhrase_StartsANewLine()
    {
        var phrase = Line(2, "Hold", "on", "and", "go");
        phrase.Lyrics[1].IsPhraseEnd = true;

        var finding = Assert.Single(LyricFindings(CreateProject(phrase)));
        Assert.Equal(Lowercase, finding.MessageKey);
        Assert.Equal(2000, finding.TimeMs);
    }

    [Fact]
    public void ALineThatContinuesAWord_IsNotALineStart()
    {
        // The phrase is the line, so its last syllable needs no line-end flag to end it.
        var first = Line(2, "Won");
        first.Lyrics[0].IsPhraseEnd = false;
        first.Lyrics[0].IsWordPart = true;

        Assert.Empty(LyricFindings(CreateProject(first, Line(4, "der", "wall"))));

        first.Lyrics[0].IsWordPart = false;
        var finding = Assert.Single(LyricFindings(CreateProject(first, Line(4, "der", "wall"))));
        Assert.Equal(Lowercase, finding.MessageKey);
        Assert.Equal(2000, finding.TimeMs);
    }

    [Fact]
    public void AQuoteOnlySyllable_IsSkippedAndTheLowercaseLetterReported()
    {
        var finding = Assert.Single(LyricFindings(CreateProject(Line(2, "“", "oh", "yeah”"))));
        Assert.Equal(Lowercase, finding.MessageKey);
        Assert.Equal(1500, finding.TimeMs);
        Assert.Equal(new LyricPlace(0, 0, 1), finding.Lyric);
    }

    [Fact]
    public void JapaneseText_HasNoCaseToCheck() =>
        Assert.Empty(LyricFindings(CreateProject(Line(2, "こ", "ころ"), Line(6, "「あ", "い」"))));

    [Fact]
    public void ALineStartingWithAQuoteOrADigit_ChecksTheFirstLetter()
    {
        Assert.Empty(LyricFindings(CreateProject(Line(2, "\"Oh", "yeah\""), Line(6, "99", "Problems"))));
        Assert.Equal(Lowercase, Assert.Single(LyricFindings(CreateProject(Line(2, "(oh", "yeah)")))).MessageKey);
    }

    [Theory]
    [InlineData("night.")]
    [InlineData("night,")]
    [InlineData("night.\"")]
    [InlineData("L.A.,")]
    public void ATrailingPeriodOrCommaAtLineEnd_Warns(string last)
    {
        var finding = Assert.Single(LyricFindings(CreateProject(Line(2, "All", last))));
        Assert.Equal(Punctuation, finding.MessageKey);
        Assert.Equal(1500, finding.TimeMs);
    }

    [Theory]
    [InlineData("Stop,", "now")]
    [InlineData("Stop.", "Go")]
    [InlineData("Stop,now", "go")]
    public void AStrayMarkMidLine_Warns(string first, string second) =>
        Assert.Equal(Punctuation, Assert.Single(LyricFindings(CreateProject(Line(2, first, second)))).MessageKey);

    [Fact]
    public void AStrayMarkInsideASplitWord_WarnsAtTheSyllableThatCarriesIt()
    {
        var phrase = Line(2, "Wan", "na,", "go");
        phrase.Lyrics[0].IsWordPart = true;

        var finding = Assert.Single(LyricFindings(CreateProject(phrase)));
        Assert.Equal(Punctuation, finding.MessageKey);
        Assert.Equal(1500, finding.TimeMs);
    }

    [Theory]
    [InlineData("Send", "S.O.S.")]
    [InlineData("In", "L.A.")]
    [InlineData("In", "L.A.'s")]
    [InlineData("A", "Ph.D.")]
    [InlineData("Go", "L.A.-bound")]
    [InlineData("Mr.", "Jones")]
    [InlineData("Don't", "stop")]
    [InlineData("Rockin'", "on")]
    [InlineData("Oh...", "yeah")]
    [InlineData("Wait", "for…")]
    [InlineData("Version", "3.5")]
    [InlineData("Is", "it?")]
    public void AllowedMarks_DoNotWarn(string first, string second) =>
        Assert.Empty(LyricFindings(CreateProject(Line(2, first, second))));

    [Fact]
    public void EveryStrayMarkInASplitWord_IsReportedOncePerSyllable()
    {
        var phrase = Line(2, "Hel.", "lo,", "go");
        phrase.Lyrics[0].IsWordPart = true;

        var findings = LyricFindings(CreateProject(phrase)).ToList();
        Assert.All(findings, f => Assert.Equal(Punctuation, f.MessageKey));
        Assert.Equal([1000.0, 1500.0], findings.Select(f => f.TimeMs!.Value));
    }

    [Fact]
    public void AnInitialismSplitIntoSyllables_IsStillAllowed()
    {
        var phrase = Line(2, "Send", "S.", "O.", "S.");
        phrase.Lyrics[1].IsWordPart = true;
        phrase.Lyrics[2].IsWordPart = true;

        Assert.Empty(LyricFindings(CreateProject(phrase)));
    }

    [Fact]
    public void ASyllableHoldingSeveralWords_WarnsThatItShouldBeSplit()
    {
        var finding = Assert.Single(LyricFindings(CreateProject(Line(2, "Hold", "on to"))));
        Assert.Equal(Unsplit, finding.MessageKey);
        Assert.Equal(1500, finding.TimeMs);
        Assert.Equal("on to", Assert.Single(finding.MessageArgs!));
    }

    [Fact]
    public void AWholeLineInOneSyllable_WarnsOnce() =>
        Assert.Equal(Unsplit, Assert.Single(LyricFindings(CreateProject(Line(2, "Hold on to me ")))).MessageKey);

    [Fact]
    public void AOneWordSyllable_IsNotGuessedApart() =>
        Assert.Empty(LyricFindings(CreateProject(Line(2, "Everything"), Line(4, "Forever "))));

    [Fact]
    public void NoProjectOrNoVocals_ReportsNothing()
    {
        Assert.Empty(LyricFindings(CreateProject()));
        Assert.Empty(LyricStyleRule.Check(null, CreateProject().Beats));
    }

    [Fact]
    public void TheArrangementRules_LeaveLyricsToTheProjectScope()
    {
        var project = CreateProject(Line(2, "hold", "on."));
        Assert.DoesNotContain(new ArrangementValidationService(new FakeAppLogger())
            .Validate(project.Arrangements[0], project.Beats, project: project), issue => issue.Type == ArrangementIssueType.LyricStyle);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(3)]
    public void TheQualityReport_ListsEachLyricFindingOnce(int arrangements)
    {
        var project = CreateProject(Line(2, "hold", "on"));
        project.Arrangements.Clear();
        for (var i = 0; i < arrangements; i++) project.Arrangements.Add(new Arrangement { Name = $"Lead {i}" });

        var report = new ChartQualityReporter(new ArrangementValidationService(new FakeAppLogger())).Build(project);

        var finding = Assert.Single(report.Findings, f => f.Rule?.Type == ArrangementIssueType.LyricStyle);
        Assert.Null(finding.Arrangement);
        Assert.Equal(1000, finding.TimeMs);
    }

    private static LyricPhrase Line(int beat, params string[] syllables)
    {
        var phrase = new LyricPhrase { Position = new FractionalPosition(beat) };
        for (var i = 0; i < syllables.Length; i++)
            phrase.Lyrics.Add(new LyricEvent { Text = syllables[i], Position = new FractionalPosition(beat + i) });
        phrase.Lyrics[^1].IsPhraseEnd = true;
        return phrase;
    }

    private static IReadOnlyList<ArrangementIssue> LyricFindings(ChartProject project) =>
        LyricStyleRule.Check(project, project.Beats);

    private static ChartProject CreateProject(params LyricPhrase[] lines)
    {
        var project = new ChartProject();
        for (var i = 0; i < 12; i++)
            project.Beats.Add(new BeatMarker { Time = i * 500 });
        project.Arrangements.Add(new Arrangement());
        if (lines.Length == 0) return project;
        var track = new VocalTrack();
        foreach (var line in lines) track.Phrases.Add(line);
        project.VocalTracks.Add(track);
        return project;
    }
}
