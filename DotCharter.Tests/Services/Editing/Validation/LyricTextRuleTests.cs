using DotCharter.Core.Domain.Vocals;
using DotCharter.Services.Editing.Quality;
using DotCharter.Services.Editing.Validation.Rules;

namespace DotCharter.Tests.Services.Editing.Validation;

/// <summary>The D13 line and character findings and their undoable fixes. Lyrics are invented test lines.</summary>
public sealed class LyricTextRuleTests
{
    private const string Plain = "Validation_LyricPlainCharacters";
    private const string TooLong = "Validation_LyricLineTooLong";
    private const string TooShort = "Validation_LyricLineTooShort";
    private const string Syllables = "Validation_LyricSeveralSyllablesOnOneNote";
    private const string Backing = "Validation_LyricBackingVocalInLead";

    [Theory]
    [InlineData("Don’t")]
    [InlineData("“Run")]
    [InlineData("wait…")]
    [InlineData("go—")]
    [InlineData("Run🔥")]
    [InlineData("♪Run")]
    [InlineData("Run#")]
    public void AStraySymbolOrTypographicMark_IsNotPlain(string syllable)
    {
        var finding = Assert.Single(Findings(Project(Line(0, syllable, "and", "go"))), f => f.MessageKey == Plain);
        Assert.Equal(new LyricPlace(0, 0, 0), finding.Lyric);
    }

    [Fact]
    public void JapaneseLyrics_AreLegitimate_AndNeverFlagged()
    {
        // Kana, kanji, the long-vowel mark and Japanese punctuation are all plain; a script without
        // spaces between words has no word count, and its short syllables fit the length limit.
        var line = Line(0, "「さ", "く", "ら", "ー", "咲", "く、", "春", "。」");
        foreach (var lyric in line.Lyrics.Take(line.Lyrics.Count - 1)) lyric.IsWordPart = true;

        Assert.Empty(Findings(Project(line)));
    }

    [Theory]
    [InlineData(2, true)]
    [InlineData(3, false)]
    [InlineData(7, false)]
    [InlineData(8, true)]
    public void ALineHoldsThreeToSevenWords(int words, bool flagged)
    {
        var line = Line(0, [.. Enumerable.Range(0, words).Select(i => i == 0 ? "Go" : "go")]);

        var keys = Findings(Project(line)).Select(f => f.MessageKey).ToList();
        Assert.Equal(flagged, keys.Contains(words < 3 ? TooShort : TooLong));
        Assert.DoesNotContain(words < 3 ? TooLong : TooShort, keys);
    }

    [Theory]
    [InlineData(40, false)]
    [InlineData(41, true)]
    public void ALineHoldsAtMostFortyCharacters(int length, bool flagged)
    {
        // Three words, "Axx…", "bb" and "cc", joined by single spaces.
        var line = Line(0, "A" + new string('x', length - 7), "bb", "cc");
        Assert.Equal(length, string.Join(' ', line.Lyrics.Select(l => l.Text)).Length);

        Assert.Equal(flagged, Findings(Project(line)).Any(f => f.MessageKey == TooLong));
    }

    [Fact]
    public void AWordOfSeveralSyllablesOnOneNote_IsFlagged_AndHyphenatedSyllablesAreNot()
    {
        var finding = Assert.Single(Findings(Project(Line(0, "Hold", "yesterday", "close"))), f => f.MessageKey == Syllables);
        Assert.Equal(new LyricPlace(0, 0, 1), finding.Lyric);
        Assert.Equal("yesterday", finding.MessageArgs![0]);

        var split = Line(0, "Hold", "yes", "ter", "day", "close");
        split.Lyrics[1].IsWordPart = true;
        split.Lyrics[2].IsWordPart = true;
        Assert.DoesNotContain(Findings(Project(split)), f => f.MessageKey == Syllables);
    }

    [Fact]
    public void ABackingVocalInTheLeadTrack_IsFlagged_ButNotInAnotherTrack()
    {
        var project = Project(Line(0, "Hold", "on", "(ooh", "ooh)"));
        var finding = Assert.Single(Findings(project), f => f.MessageKey == Backing);
        Assert.Equal(new LyricPlace(0, 0, 2), finding.Lyric);

        var harmony = new VocalTrack { Name = "Harmony" };
        harmony.Phrases.Add(Line(0, "(Ooh", "ooh", "ooh)"));
        project.VocalTracks.Add(harmony);
        Assert.Single(Findings(project), f => f.MessageKey == Backing);
    }

    [Fact]
    public void TheIssuesTabAndQualityReport_ListTheLineFindings()
    {
        var project = Project(Line(0, "Hold", "on", "now"), Line(4, "Run🔥", "and", "go"));
        Assert.Contains(LyricStyleRule.CheckAll(project, project.Beats), f => f.MessageKey == Plain);

        var report = new ChartQualityReporter(new ArrangementValidationService(new FakeAppLogger())).Build(project);
        Assert.Contains(report.Findings, f => f.Rule?.MessageKey == Plain);
    }

    [Theory]
    [InlineData(Plain, "Don’t…", "Don't...")]
    [InlineData(Plain, "Run🔥", "Run")]
    public void ThePlainFix_RewritesOnlyThatSyllable_AsOneUndoStep(string key, string before, string after)
    {
        var project = Project(Line(0, before, "stop", "now"));
        var issue = Assert.Single(Findings(project), f => f.MessageKey == key);

        var history = Apply(issue, project);

        Assert.Equal(after, project.VocalTracks[0].Phrases[0].Lyrics[0].Text);
        Assert.DoesNotContain(Findings(project), f => f.MessageKey == key);
        history.Undo();
        Assert.Equal(before, project.VocalTracks[0].Phrases[0].Lyrics[0].Text);
    }

    [Fact]
    public void TheLowercaseAndPunctuationFixes_CapitaliseAndStripOnlyTheStrayMarks()
    {
        var project = Project(Line(0, "\"hold", "on,", "S.O.S."));
        var lower = Assert.Single(LyricStyleRule.Check(project, project.Beats), f => f.MessageKey == "Validation_LyricLineStartsLowercase");
        var comma = Assert.Single(LyricStyleRule.Check(project, project.Beats), f => f.MessageKey == "Validation_LyricStrayPunctuation");

        Apply(lower, project);
        Apply(comma, project);

        Assert.Equal(["\"Hold", "on", "S.O.S."], project.VocalTracks[0].Phrases[0].Lyrics.Select(l => l.Text));
        Assert.Empty(LyricStyleRule.Check(project, project.Beats));
    }

    [Fact]
    public void TheSplitFix_HalvesALongLine_AsOneUndoStep()
    {
        var project = Project(Line(0, "One", "two", "three", "four", "five", "six", "seven", "eight"));
        var issue = Assert.Single(Findings(project), f => f.MessageKey == TooLong);
        Assert.True(LyricStyleFix.For(issue, project)!.IsSplit);

        var history = Apply(issue, project);

        var phrases = project.VocalTracks[0].Phrases;
        Assert.Equal(2, phrases.Count);
        Assert.Equal(["One", "two", "three", "four"], phrases[0].Lyrics.Select(l => l.Text));
        Assert.DoesNotContain(Findings(project), f => f.MessageKey is TooLong or TooShort);
        history.Undo();
        Assert.Single(phrases);
        Assert.Equal(8, phrases[0].Lyrics.Count);
    }

    [Fact]
    public void FindingsWithoutASafeFix_OfferNone_AndNoFixRunsOnItsOwn()
    {
        var project = Project(Line(0, "Hold", "yesterday", "(ooh)"), Line(4, "🔥", "and", "go"));
        var findings = Findings(project);

        Assert.All(findings.Where(f => f.MessageKey is Syllables or Backing), f => Assert.Null(LyricStyleFix.For(f, project)));
        // A syllable that would be left empty keeps its text: no fix is offered.
        Assert.Null(LyricStyleFix.For(Assert.Single(findings, f => f.MessageKey == Plain), project));
        Assert.Equal("🔥", project.VocalTracks[0].Phrases[1].Lyrics[0].Text);
    }

    private static HistoryService Apply(ArrangementIssue issue, ChartProject project)
    {
        var history = new HistoryService(new FakeAppLogger());
        history.Do(LyricStyleFix.For(issue, project)!.Action);
        return history;
    }

    private static IReadOnlyList<ArrangementIssue> Findings(ChartProject project) => LyricTextRule.Check(project, project.Beats);

    private static LyricPhrase Line(int beat, params string[] syllables)
    {
        var phrase = new LyricPhrase { Position = new FractionalPosition(beat) };
        for (var i = 0; i < syllables.Length; i++)
            phrase.Lyrics.Add(new LyricEvent { Text = syllables[i], Position = new FractionalPosition(beat + i) });
        phrase.Lyrics[^1].IsPhraseEnd = true;
        return phrase;
    }

    private static ChartProject Project(params LyricPhrase[] lines)
    {
        var project = new ChartProject();
        for (var i = 0; i < 12; i++)
            project.Beats.Add(new BeatMarker { Time = i * 500 });
        project.Arrangements.Add(new Arrangement());
        var track = new VocalTrack();
        foreach (var line in lines) track.Phrases.Add(line);
        project.VocalTracks.Add(track);
        return project;
    }
}
