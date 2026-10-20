using DotCharter.Services.Editing.Fixing;

namespace DotCharter.Tests.Services.Editing.Validation;

/// <summary>
///     E06 — an anchor may not simply span a phrase boundary. Ported from
///     Editor on Fire's <c>eof_enforce_rs_phrase_begin_with_fret_hand_position</c>
///     (<c>EOF_SRC/rs.c:5035</c>); see <c>PhraseAnchorRule</c> for why Rocksmith
///     does not carry such an anchor into the phrase.
/// </summary>
public sealed class PhraseAnchorRuleTests
{
    private static List<BeatMarker> Beats(int count = 32) =>
        [.. Enumerable.Range(0, count).Select(i => new BeatMarker { Time = i * 500.0, Bpm = 120 })];

    private static IReadOnlyList<ArrangementIssue> Violations(Arrangement arr) =>
        new ArrangementValidationService(new FakeAppLogger())
            .Validate(arr, Beats())
            .Where(i => i.Type == ArrangementIssueType.AnchorMissingAtPhraseStart)
            .ToList();

    /// <summary>Phrases at beats 0 and 8; one level.</summary>
    private static Arrangement Build(
        (int Beat, string Phrase)[] phrases,
        (int Beat, int Fret)[] anchors,
        (int Beat, int Fret)[] notes)
    {
        var arr = new Arrangement();
        var level = new ChartLevel();
        arr.Levels.Add(level);

        foreach (var (beat, phrase) in phrases)
            arr.EventPoints.Add(new EventPoint { Position = new FractionalPosition(beat), Phrase = phrase });

        foreach (var (beat, fret) in anchors)
            level.Anchors.Add(new FretAnchor(new FractionalPosition(beat), fret));

        foreach (var (beat, fret) in notes)
        {
            level.Notes.Add(new GuitarNote
            {
                Position = new FractionalPosition(beat), StringIndex = 0, Fret = fret
            });
        }

        return arr;
    }

    [Fact]
    public void APhraseWhoseNotesAreGovernedByAnEarlierPhrasesAnchorIsReported()
    {
        var arr = Build(
            [(0, "riff1"), (8, "riff2")],
            [(0, 12)],
            [(1, 12), (9, 13)]);

        var issue = Assert.Single(Violations(arr));
        Assert.Equal("E06", issue.Code);
        Assert.Contains("riff2", issue.Message);
        Assert.Contains("fret 12", issue.Message);
    }

    [Fact]
    public void AnAnchorExactlyOnThePhraseStartSatisfiesIt()
    {
        var arr = Build(
            [(0, "riff1"), (8, "riff2")],
            [(0, 12), (8, 12)],
            [(1, 12), (9, 13)]);

        Assert.Empty(Violations(arr));
    }

    /// <summary>
    ///     EOF searches the whole phrase-start → first-note range, not just the
    ///     boundary. An anchor placed a beat into the phrase but still before its
    ///     first note is correct and must not be reported.
    /// </summary>
    [Fact]
    public void AnAnchorAfterThePhraseStartButBeforeItsFirstNoteSatisfiesIt()
    {
        var arr = Build(
            [(0, "riff1"), (8, "riff2")],
            [(0, 12), (9, 12)],
            [(1, 12), (10, 13)]);

        Assert.Empty(Violations(arr));
    }

    /// <summary>
    ///     …but an anchor placed AFTER the phrase's first note does not, because
    ///     that note is still played with no hand position.
    /// </summary>
    [Fact]
    public void AnAnchorAfterThePhrasesFirstNoteDoesNotSatisfyIt()
    {
        var arr = Build(
            [(0, "riff1"), (8, "riff2")],
            [(0, 12), (11, 12)],
            [(1, 12), (10, 13)]);

        Assert.Single(Violations(arr));
    }

    /// <summary>
    ///     The check is per DIFFICULTY. A phrase this level does not play asks
    ///     nothing of the hand, so demanding an anchor there would fire on every
    ///     sparse low level in every chart.
    /// </summary>
    [Fact]
    public void APhraseWithNoSoundsInThisLevelIsNotReported()
    {
        var arr = Build(
            [(0, "riff1"), (8, "riff2")],
            [(0, 12)],
            [(1, 12)]);

        Assert.Empty(Violations(arr));
    }

    [Theory]
    [InlineData("COUNT")]
    [InlineData("END")]
    [InlineData("noguitar")]
    [InlineData("ng")]
    public void StructuralPhrasesAreNotAskedForAnAnchor(string phrase)
    {
        var arr = Build(
            [(0, "riff1"), (8, phrase)],
            [(0, 12)],
            [(1, 12), (9, 13)]);

        Assert.Empty(Violations(arr));
    }

    /// <summary>
    ///     <b>The bug this rule shipped with for one measurement round.</b> An
    ///     imported chart stores its phrases as
    ///     <c>Phrases</c>/<c>PhraseIterations</c>, not as
    ///     <c>EventPoint.Phrase</c> — so keyed on the editor store alone the rule
    ///     found zero phrases in all 89 hand-finished exports and reported every
    ///     one of them clean. The probe said "phrase markers=0" and that is the
    ///     only reason it was caught.
    /// </summary>
    [Fact]
    public void AnImportedChartsPhrasesAreCheckedToo()
    {
        var arr = new Arrangement();
        var level = new ChartLevel();
        arr.Levels.Add(level);

        arr.Phrases.AddRange(["riff1", "riff2"]);
        arr.PhraseIterations.Add(new PhraseIteration(new FractionalPosition(0), 0));
        arr.PhraseIterations.Add(new PhraseIteration(new FractionalPosition(8), 1));

        level.Anchors.Add(new FretAnchor(new FractionalPosition(0), 12));
        level.Notes.Add(new GuitarNote { Position = new FractionalPosition(1), StringIndex = 0, Fret = 12 });
        level.Notes.Add(new GuitarNote { Position = new FractionalPosition(9), StringIndex = 0, Fret = 13 });

        var issue = Assert.Single(Violations(arr));
        Assert.Contains("riff2", issue.Message);
    }

    [Fact]
    public void AChordCountsAsThePhrasesFirstSound()
    {
        var arr = Build([(0, "riff1"), (8, "riff2")], [(0, 12)], [(1, 12)]);
        arr.Levels[0].Chords.Add(new Chord { Position = new FractionalPosition(9), TemplateId = -1 });

        Assert.Single(Violations(arr));
    }

    /// <summary>
    ///     The auto-fixer's job: restate the SAME fret at the phrase start, so
    ///     the chart is unchanged musically and the anchor simply stops crossing.
    /// </summary>
    [Fact]
    public void TheAutoFixerRestatesTheInheritedFretAtThePhraseStartAndClearsTheIssue()
    {
        var arr = Build(
            [(0, "riff1"), (8, "riff2")],
            [(0, 12)],
            [(1, 12), (9, 13)]);

        new ArrangementAutoFixService().Fix(arr, new ArrangementFixResult());

        var added = arr.Levels[0].Anchors.Single(a => a.Position == new FractionalPosition(8));
        Assert.Equal(12, added.Fret);
        Assert.Empty(Violations(arr));
    }

    /// <summary>
    ///     The fixer used to insert at EVERY phrase of any level holding notes
    ///     anywhere, so it littered anchors into phrases the level never plays.
    /// </summary>
    [Fact]
    public void TheAutoFixerLeavesPhrasesThisLevelDoesNotPlayAlone()
    {
        var arr = Build(
            [(0, "riff1"), (8, "riff2")],
            [(0, 12)],
            [(1, 12)]);

        new ArrangementAutoFixService().Fix(arr, new ArrangementFixResult());

        Assert.DoesNotContain(arr.Levels[0].Anchors, a => a.Position == new FractionalPosition(8));
    }

    /// <summary>…and it used to add a redundant anchor whenever an existing one
    ///     sat just after the boundary but still before the first note.</summary>
    [Fact]
    public void TheAutoFixerDoesNotDuplicateAnAnchorThatAlreadyCoversThePhrase()
    {
        var arr = Build(
            [(0, "riff1"), (8, "riff2")],
            [(0, 12), (9, 12)],
            [(1, 12), (10, 13)]);

        new ArrangementAutoFixService().Fix(arr, new ArrangementFixResult());

        Assert.Equal(2, arr.Levels[0].Anchors.Count);
    }
}
