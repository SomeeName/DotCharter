namespace DotCharter.Tests.Services.Editing.Improvement;

/// <summary>The ladder scorer behind the #295 measurement counts what it claims to.</summary>
public sealed class LevelLadderScoreTests
{
    private static ChartLevel LevelAt(params int[] beats)
    {
        var level = new ChartLevel();
        foreach (var beat in beats) level.Notes.Add(new GuitarNote { Position = new FractionalPosition(beat), Fret = 3 });
        return level;
    }

    [Fact]
    public void SharedOnsets_GivePrecisionRecallAndF1()
    {
        var top = LevelAt(0, 1, 2, 3, 4, 5, 6, 7);
        var official = new[] { LevelAt(0, 2, 4, 6), top };
        var generated = new[] { LevelAt(0, 1, 2), top };

        var score = Assert.Single(LevelLadderScore.Compare(official, generated));

        Assert.Equal((4, 3, 2, 8), (score.Official, score.Generated, score.Shared, score.Pool));
        Assert.Equal(2 / 3.0, score.Precision, 6);
        Assert.Equal(0.5, score.Recall, 6);
        Assert.Equal(4 / 7.0, score.F1, 6);
        Assert.Equal(0.75, score.CountRatio, 6);
        // Random: 2·4·3/8 / (4+3) = 3/7.
        Assert.Equal(3 / 7.0, score.RandomF1, 6);
    }

    [Fact]
    public void AChordAndANote_AtOnePosition_AreOneOnset()
    {
        var level = LevelAt(1);
        level.Chords.Add(new Chord { Position = new FractionalPosition(1) });
        level.Chords.Add(new Chord { Position = new FractionalPosition(2) });

        Assert.Equal(2, LevelLadderScore.Onsets(level).Count);
    }

    [Fact]
    public void TheTopLevel_IsNotScored_ItIsTheSourceOfBoth()
    {
        var top = LevelAt(0, 1, 2);
        Assert.Equal(2, LevelLadderScore.Compare([LevelAt(0), LevelAt(0, 1), top], [LevelAt(2), LevelAt(1, 2), top]).Count);
    }
}
