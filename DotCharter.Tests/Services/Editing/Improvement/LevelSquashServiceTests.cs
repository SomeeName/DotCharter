namespace DotCharter.Tests.Services.Editing.Improvement;

public sealed class LevelSquashServiceTests
{
    [Fact]
    public void TrySquash_MergesPhraseSegmentsFromConfiguredMaxDifficultyLevels()
    {
        var arrangement = new Arrangement { Name = "Lead" };

        var level0 = new ChartLevel { DifficultyIndex = 0 };
        level0.Notes.Add(new GuitarNote { Position = new FractionalPosition(0), Fret = 3, StringIndex = 0 });
        level0.Notes.Add(new GuitarNote { Position = new FractionalPosition(2), Fret = 5, StringIndex = 0 });
        level0.Notes.Add(new GuitarNote { Position = new FractionalPosition(4), Fret = 7, StringIndex = 0 });

        var level1 = new ChartLevel { DifficultyIndex = 1 };
        level1.Notes.Add(new GuitarNote { Position = new FractionalPosition(2), Fret = 9, StringIndex = 1 });
        level1.Chords.Add(new Chord
        {
            Position = new FractionalPosition(4),
            ChordNotes = { new ChordNote { StringIndex = 0, Fret = 7 } }
        });
        level1.Anchors.Add(new FretAnchor(new FractionalPosition(2), 5));
        level1.HandShapes.Add(new HandShape
        {
            Position = new FractionalPosition(4),
            EndPosition = new FractionalPosition(5),
            TemplateId = 0
        });
        level1.FretHandMutes.Add(new FretHandMute(new FractionalPosition(4)));

        arrangement.Levels.Add(level0);
        arrangement.Levels.Add(level1);
        arrangement.EventPoints.Add(new EventPoint { Position = new FractionalPosition(2), Phrase = "verse" });
        arrangement.EventPoints.Add(new EventPoint { Position = new FractionalPosition(4), Phrase = "chorus" });
        arrangement.PhraseInfos["verse"] = new PhraseInfo(1, false);
        arrangement.PhraseInfos["chorus"] = new PhraseInfo(1, true);

        var changed = new LevelSquashService().TrySquash(arrangement);

        Assert.True(changed);
        var squashed = Assert.Single(arrangement.Levels);
        Assert.Equal(0, squashed.DifficultyIndex);

        Assert.Collection(squashed.Notes.OrderBy(n => n.Position).ThenBy(n => n.StringIndex),
            note =>
            {
                Assert.Equal(new FractionalPosition(0), note.Position);
                Assert.Equal(3, note.Fret);
                Assert.Equal(0, note.StringIndex);
            },
            note =>
            {
                Assert.Equal(new FractionalPosition(2), note.Position);
                Assert.Equal(9, note.Fret);
                Assert.Equal(1, note.StringIndex);
            });

        var chord = Assert.Single(squashed.Chords);
        Assert.Equal(new FractionalPosition(4), chord.Position);
        Assert.Single(squashed.Anchors);
        Assert.Single(squashed.HandShapes);
        Assert.Single(squashed.FretHandMutes);
        Assert.All(arrangement.PhraseInfos.Values, info => Assert.Equal(0, info.MaxDifficulty));
        Assert.True(arrangement.PhraseInfos["chorus"].Solo);
    }

    [Fact]
    public void TrySquash_ReturnsFalse_WhenArrangementAlreadyHasOneLevel()
    {
        var arrangement = new Arrangement();
        arrangement.Levels.Add(new ChartLevel { DifficultyIndex = 0 });

        var changed = new LevelSquashService().TrySquash(arrangement);

        Assert.False(changed);
        Assert.Single(arrangement.Levels);
    }

    [Fact]
    public void TrySquash_ReadsPhrasesFromPhraseIterations_NotOnlyEventPoints()
    {
        // An IMPORTED chart spells its phrases as Phrases + PhraseIterations and
        // carries no EventPoint.Phrase at all (measured over TestData/EOF: 0
        // markers via EventPoint, 2,596 via PhraseIterations). Keyed on the
        // EventPoint store alone this saw ZERO phrases, collapsed the whole song
        // into the leading segment, resolved that to level 0 -- and squashing
        // threw the hardest level away and kept the EASIEST one.
        var arrangement = ImportedTwoPhraseArrangement();

        Assert.True(new LevelSquashService().TrySquash(arrangement));

        var squashed = Assert.Single(arrangement.Levels);
        Assert.Equal([9, 11], squashed.Notes.OrderBy(n => n.Position).Select(n => n.Fret));
    }

    [Fact]
    public void TrySquash_UsesTheHardestLevel_ForAPhraseWithNoPhraseInfo()
    {
        // "No phrase metadata" is not "difficulty 0" -- the convention
        // LiveExpectedTimeline.BuildPhraseSpans already spells out. Answering 0 here
        // silently dropped an un-annotated phrase to the easiest level.
        var arrangement = ImportedTwoPhraseArrangement();
        arrangement.PhraseInfos.Clear();

        Assert.True(new LevelSquashService().TrySquash(arrangement));

        var squashed = Assert.Single(arrangement.Levels);
        Assert.Equal([9, 11], squashed.Notes.OrderBy(n => n.Position).Select(n => n.Fret));
    }

    [Fact]
    public void TrySquash_UsesTheHardestLevel_WhenTheArrangementHasNoPhrasesAtAll()
    {
        // With no markers the leading segment IS the whole song, so it cannot
        // take the lead-in's level 0.
        var arrangement = ImportedTwoPhraseArrangement();
        arrangement.Phrases.Clear();
        arrangement.PhraseIterations.Clear();
        arrangement.PhraseInfos.Clear();

        Assert.True(new LevelSquashService().TrySquash(arrangement));

        var squashed = Assert.Single(arrangement.Levels);
        Assert.Equal([9, 11], squashed.Notes.OrderBy(n => n.Position).Select(n => n.Fret));
    }

    /// <summary>Two levels, two phrases spelled the RS-XML/psarc way: the hard
    /// level plays frets 9 and 11, the easy one 3 and 5 at the same positions.
    /// A correct squash keeps 9 and 11.</summary>
    private static Arrangement ImportedTwoPhraseArrangement()
    {
        var arrangement = new Arrangement { Name = "Lead" };

        var level0 = new ChartLevel { DifficultyIndex = 0 };
        level0.Notes.Add(new GuitarNote { Position = new FractionalPosition(0), Fret = 3, StringIndex = 0 });
        level0.Notes.Add(new GuitarNote { Position = new FractionalPosition(4), Fret = 5, StringIndex = 0 });

        var level1 = new ChartLevel { DifficultyIndex = 1 };
        level1.Notes.Add(new GuitarNote { Position = new FractionalPosition(0), Fret = 9, StringIndex = 0 });
        level1.Notes.Add(new GuitarNote { Position = new FractionalPosition(4), Fret = 11, StringIndex = 0 });

        arrangement.Levels.Add(level0);
        arrangement.Levels.Add(level1);

        arrangement.Phrases.Add("verse");
        arrangement.Phrases.Add("chorus");
        arrangement.PhraseIterations.Add(new PhraseIteration { Position = new FractionalPosition(0), PhraseId = 0 });
        arrangement.PhraseIterations.Add(new PhraseIteration { Position = new FractionalPosition(4), PhraseId = 1 });
        arrangement.PhraseInfos["verse"] = new PhraseInfo(1, false);
        arrangement.PhraseInfos["chorus"] = new PhraseInfo(1, false);
        return arrangement;
    }
}
