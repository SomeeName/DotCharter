using DotCharter.Services.Editing.Validation.Rules;

namespace DotCharter.Tests.Services.Editing.Validation;

public sealed class DifficultyLevelRuleTests
{
    private const int BeatMs = 500;

    [Fact]
    public void RepeatsWithDifferentLevelCounts_WarnOnceAtTheFirstOddRepeat()
    {
        var arrangement = CreateArrangement(8, ("verse", 4), ("verse", 4), ("verse", 6), ("verse", 6), ("verse", 6));
        AddEnd(arrangement, 20);

        var finding = Assert.Single(Findings(arrangement));
        Assert.Equal("Validation_PhraseLevelsMismatch", finding.MessageKey);
        Assert.Equal(ArrangementIssueType.DifficultyLevelCount, finding.Type);
        Assert.Equal(ArrangementIssueSeverity.Warning, finding.Severity);
        Assert.Equal(0, finding.TimeMs);
        Assert.Equal(new object[] { "verse", 4, 6 }, finding.MessageArgs);
    }

    [Fact]
    public void RepeatsWithTheSameLevelCount_AreClean()
    {
        var arrangement = CreateArrangement(8, ("verse", 5), ("chorus", 7), ("verse", 5), ("chorus", 7));
        AddEnd(arrangement, 20);

        Assert.Empty(Findings(arrangement));
    }

    [Fact]
    public void APlayablePhraseWithOneLevel_Warns()
    {
        var arrangement = CreateArrangement(4, ("intro", 3), ("riff", 1), ("riff", 1));
        AddEnd(arrangement, 20);

        var finding = Assert.Single(Findings(arrangement));
        Assert.Equal("Validation_PhraseLevelsTooFew", finding.MessageKey);
        Assert.Equal(4 * BeatMs, finding.TimeMs);
        Assert.Equal(new object[] { "riff", 1 }, finding.MessageArgs);
    }

    [Fact]
    public void ExactlyTwoLevels_IsEnough()
    {
        var arrangement = CreateArrangement(4, ("riff", 2), ("riff", 2));
        AddEnd(arrangement, 20);

        Assert.Empty(Findings(arrangement));
    }

    [Fact]
    public void EmptyPhrases_AreExemptFromTheMinimum()
    {
        var arrangement = CreateArrangement(4, ("COUNT", 0), ("noguitar", 0), ("riff", 3), ("bridge", 0));
        AddEnd(arrangement, 20);

        Assert.Empty(Findings(arrangement));
    }

    [Fact]
    public void APhraseOverThirtyLevels_Warns()
    {
        var arrangement = CreateArrangement(32, ("solo", 31), ("outro", 3));
        AddEnd(arrangement, 20);

        var finding = Assert.Single(Findings(arrangement));
        Assert.Equal("Validation_PhraseLevelsTooMany", finding.MessageKey);
        Assert.Equal(0, finding.TimeMs);
        Assert.Equal(new object[] { "solo", 31 }, finding.MessageArgs);
    }

    [Fact]
    public void ExactlyThirtyLevels_IsAllowed()
    {
        var arrangement = CreateArrangement(32, ("solo", 30), ("outro", 3));
        AddEnd(arrangement, 20);

        Assert.Empty(Findings(arrangement));
    }

    [Fact]
    public void AChartWithOneLevel_HasNoLadderToCheck()
    {
        var arrangement = CreateArrangement(1, ("riff", 1), ("riff", 1));
        AddEnd(arrangement, 20);

        Assert.Empty(Findings(arrangement));
    }

    [Fact]
    public void ImportedPhraseIterations_AreCheckedToo()
    {
        var arrangement = CreateArrangement(6, ("verse", 3), ("verse", 5), ("verse", 3));
        foreach (var point in arrangement.EventPoints)
        {
            var id = arrangement.Phrases.IndexOf(point.Phrase);
            if (id < 0)
            {
                id = arrangement.Phrases.Count;
                arrangement.Phrases.Add(point.Phrase);
            }

            arrangement.PhraseIterations.Add(new PhraseIteration(point.Position, id));
        }

        arrangement.EventPoints.Clear();

        var finding = Assert.Single(Findings(arrangement));
        Assert.Equal("Validation_PhraseLevelsMismatch", finding.MessageKey);
        Assert.Equal(4 * BeatMs, finding.TimeMs);
        Assert.Equal(new object[] { "verse", 5, 3 }, finding.MessageArgs);
    }

    [Fact]
    public void AnEarlyAnchorChange_DoesNotHideALateNoteChange()
    {
        var arrangement = CreateArrangement(32, ("solo", 31), ("outro", 3));
        for (var j = 1; j < 32; j++)
            arrangement.Levels[j].Anchors.Add(new FretAnchor { Position = FractionalPosition.Zero, Fret = 1 });

        var finding = Assert.Single(Findings(arrangement));
        Assert.Equal("Validation_PhraseLevelsTooMany", finding.MessageKey);
        Assert.Equal(new object[] { "solo", 31 }, finding.MessageArgs);
    }

    [Fact]
    public void AHandShapeRemovedOnAHigherLevel_IsALevelOfItsOwn()
    {
        var arrangement = CreateArrangement(3, ("riff", 1));
        for (var j = 0; j < 2; j++)
        {
            arrangement.Levels[j].HandShapes.Add(new HandShape
            {
                Position = FractionalPosition.Zero, EndPosition = new FractionalPosition(1)
            });
        }

        Assert.Empty(Findings(arrangement));
    }

    [Fact]
    public void AnAuthoredMaximum_IsTheFloor()
    {
        var arrangement = CreateArrangement(5, ("a", 1), ("b", 1));
        arrangement.PhraseInfos["a"].MaxDifficulty = 3;
        arrangement.PhraseInfos.Remove("b"); // partially authored: "b" is fully levelled

        Assert.Empty(Findings(arrangement));
    }

    [Fact]
    public void ASparsePhrase_IsPlayableAtItsOwnMaximum()
    {
        var arrangement = CreateArrangement(4, ("low", 1), ("high", 4));
        foreach (var level in arrangement.Levels.Skip(1))
            level.Notes.RemoveAt(0); // "low" has notes on level 0 only, as a sparse file holds it

        var finding = Assert.Single(Findings(arrangement));
        Assert.Equal("Validation_PhraseLevelsTooFew", finding.MessageKey);
        Assert.Equal(new object[] { "low", 1 }, finding.MessageArgs);
    }

    [Fact]
    public void NamesThatDifferOnlyByCase_AreOnePhrase()
    {
        var arrangement = CreateArrangement(6, ("Verse", 3), ("verse", 5), ("Verse", 3));

        var finding = Assert.Single(Findings(arrangement));
        Assert.Equal("Validation_PhraseLevelsMismatch", finding.MessageKey);
        Assert.Equal(new object[] { "Verse", 5, 3 }, finding.MessageArgs);
    }

    [Fact]
    public void AnImportedIteration_OwnsItsStartOverAnEditorMarker()
    {
        var arrangement = CreateArrangement(3, ("a", 1));
        arrangement.Phrases.AddRange(["a", "END"]);
        arrangement.PhraseIterations.Add(new PhraseIteration(FractionalPosition.Zero, 0));
        arrangement.PhraseIterations.Add(new PhraseIteration(new FractionalPosition(20), 1));
        arrangement.EventPoints[0].Phrase = "b";
        arrangement.PhraseInfos["b"] = new PhraseInfo(0, false);

        var finding = Assert.Single(Findings(arrangement));
        Assert.Equal(new object[] { "a", 1 }, finding.MessageArgs);
    }

    [Fact]
    public void TheMismatchMessage_NamesBothCountsWithoutClaimingAMajority()
    {
        var arrangement = CreateArrangement(6, ("verse", 3), ("verse", 5));

        var finding = Assert.Single(Findings(arrangement));
        Assert.Equal("Difficulty levels in this repeat of the phrase “verse”: 3; in its other repeats: 5. "
                     + "Give every repeat of a phrase the same number of levels.", finding.Message);
    }

    /// <summary>
    ///     A dense chart: each phrase starts on its own beat (four beats apart) and its note
    ///     changes fret up to level <c>levels - 1</c>; every level above holds a copy of that one.
    ///     Zero levels means the phrase holds no notes. Every phrase is authored at max 0, as an
    ///     imported ladder whose content decides each count.
    /// </summary>
    private static Arrangement CreateArrangement(int levelCount, params (string Name, int Levels)[] phrases)
    {
        var arrangement = new Arrangement();
        for (var j = 0; j < levelCount; j++)
            arrangement.Levels.Add(new ChartLevel { DifficultyIndex = j });

        for (var i = 0; i < phrases.Length; i++)
        {
            var (name, levels) = phrases[i];
            var position = new FractionalPosition(i * 4);
            arrangement.EventPoints.Add(new EventPoint { Phrase = name, Position = position });
            arrangement.PhraseInfos[name] = new PhraseInfo(0, false);
            if (levels == 0) continue;
            for (var j = 0; j < levelCount; j++)
            {
                var fret = Math.Min(j, levels - 1) + 1;
                arrangement.Levels[j].Notes.Add(new GuitarNote { Position = position, Fret = fret });
            }
        }

        return arrangement;
    }

    private static void AddEnd(Arrangement arrangement, int beat) =>
        arrangement.EventPoints.Add(new EventPoint { Phrase = "END", Position = new FractionalPosition(beat) });

    private static List<ArrangementIssue> Findings(Arrangement arrangement)
    {
        var beats = new List<BeatMarker>();
        for (var i = 0; i < 24; i++)
            beats.Add(new BeatMarker { Time = i * BeatMs });

        var issues = new List<ArrangementIssue>();
        new DifficultyLevelRule().Validate(ArrangementValidationContext.Build(arrangement, beats, null), issues);
        return issues;
    }
}
