using DotCharter.Services.Audio.LivePlay;

namespace DotCharter.Tests.Services.Audio.LivePlay;

/// <summary>#295 step 2: a one-level chart's dynamic-difficulty run judges a ladder generated in
/// memory; a levelled chart judges its own levels; the chart itself is never touched.</summary>
public sealed class DynamicDifficultyLadderTests
{
    private static List<BeatMarker> Beats() =>
    [
        .. Enumerable.Range(0, 64).Select(i => new BeatMarker
        {
            Time = i * 500, Bpm = 120, BeatsInMeasure = 4, NoteDenominator = 4, IsMeasure = i % 4 == 0
        })
    ];

    private static Arrangement OneLevel()
    {
        var level = new ChartLevel();
        for (var beat = 0; beat < 48; beat++)
            level.Notes.Add(new GuitarNote { Position = new FractionalPosition(beat), StringIndex = beat % 6, Fret = 3 + beat % 5 });
        var arrangement = new Arrangement { Name = "Lead" };
        arrangement.Levels.Add(level);
        return arrangement;
    }

    [Fact]
    public void AOneLevelChart_GetsAGeneratedLadder_AndKeepsItsOwnLevel()
    {
        var arrangement = OneLevel();
        var level = arrangement.Levels[0];
        var notes = level.Notes.ToList();

        var ladder = DynamicDifficultyLadder.For(arrangement, Beats());

        Assert.NotSame(arrangement, ladder);
        Assert.Equal(DynamicDifficultyLadder.GeneratedLevels, ladder.Levels.Count);
        Assert.True(DynamicDifficultyEngine.CanLevel(ladder));
        Assert.Same(level, Assert.Single(arrangement.Levels));
        Assert.Equal(notes, level.Notes);
        Assert.True(ladder.Levels.OrderBy(l => l.DifficultyIndex).First().Notes.Count < notes.Count);
    }

    /// <summary>The ladder is one a run can move on: every phrase of the session reaches past
    /// level 0, where a one-level chart's phrases all stop at 0 and the ladder was inert (#323).</summary>
    [Fact]
    public void ASessionOnTheGeneratedLadder_CanMovePhrasesBetweenLevels()
    {
        var arrangement = OneLevel();
        arrangement.Phrases.AddRange(["verse", "chorus"]);
        arrangement.PhraseIterations.Add(new PhraseIteration { Position = FractionalPosition.Zero, PhraseId = 0 });
        arrangement.PhraseIterations.Add(new PhraseIteration { Position = new FractionalPosition(24), PhraseId = 1 });
        var beats = Beats();
        var options = new LivePlaySessionOptions { DynamicDifficulty = true };

        var raw = new LivePlaySessionService();
        raw.Start(arrangement, null, beats, options);
        var generated = new LivePlaySessionService();
        generated.Start(DynamicDifficultyLadder.For(arrangement, beats), null, beats, options);

        Assert.All(raw.PhraseSpans, s => Assert.Equal(0, s.MaxDifficulty));
        Assert.NotEmpty(generated.PhraseSpans);
        Assert.All(generated.PhraseSpans, s => Assert.Equal(DynamicDifficultyLadder.GeneratedLevels - 1, s.MaxDifficulty));
    }

    [Fact]
    public void ALevelledChart_IsJudgedOnItsOwnLevels()
    {
        var arrangement = OneLevel();
        arrangement.Levels.Add(new ChartLevel { DifficultyIndex = 1 });

        Assert.Same(arrangement, DynamicDifficultyLadder.For(arrangement, Beats()));
        Assert.False(DynamicDifficultyLadder.IsGenerated(arrangement));
    }

    [Fact]
    public void AChartWithNoNotes_HasNothingToLadder()
    {
        var arrangement = new Arrangement { Name = "Lead" };
        arrangement.Levels.Add(new ChartLevel());

        Assert.False(DynamicDifficultyLadder.CanLadder(arrangement));
        Assert.Same(arrangement, DynamicDifficultyLadder.For(arrangement, Beats()));
    }
}
