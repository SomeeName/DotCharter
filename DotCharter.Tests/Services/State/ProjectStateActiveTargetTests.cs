namespace DotCharter.Tests.Services.State;

/// <summary>
///     <see cref="ProjectState.ActiveArrangement" /> owns <see cref="ProjectState.ActiveLevel" />:
///     everything that edits a chart (ChartDocumentState.PlacedNotes/Chords/…,
///     IEditingScope) resolves off the level, so a level left over from the
///     PREVIOUS arrangement silently redirects every subsequent edit.
/// </summary>
public class ProjectStateActiveTargetTests
{
    private static Arrangement WithLevel(out ChartLevel level)
    {
        var arrangement = new Arrangement();
        level = new ChartLevel();
        arrangement.Levels.Add(level);
        return arrangement;
    }

    [Fact]
    public void SettingAnArrangement_AdoptsItsFirstLevel()
    {
        var state = new ProjectState();
        var arrangement = WithLevel(out var level);

        state.ActiveArrangement = arrangement;

        Assert.Same(level, state.ActiveLevel);
    }

    /// <summary>
    ///     On a chart with dynamic difficulty, <c>Levels[0]</c> is the THINNEST
    ///     beginner reduction — so adopting it dropped the editor onto a chart
    ///     with most of its notes missing every time an imported RS/psarc
    ///     arrangement was opened or switched to. The hardest level is the whole
    ///     chart, and it is what every other level-picking site in the app
    ///     already chooses.
    /// </summary>
    [Fact]
    public void SettingALeveledArrangement_AdoptsTheHardestLevel()
    {
        var state = new ProjectState();
        var arrangement = new Arrangement();
        for (var i = 0; i < 5; i++) arrangement.Levels.Add(new ChartLevel { DifficultyIndex = i });

        state.ActiveArrangement = arrangement;

        Assert.Same(arrangement.Levels[4], state.ActiveLevel);
    }

    /// <summary>The answer comes from <c>DifficultyIndex</c>, not from the
    /// collection's order — nothing in the model promises the two agree.</summary>
    [Fact]
    public void SettingALeveledArrangement_ReadsTheDifficultyIndex_NotThePosition()
    {
        var state = new ProjectState();
        var arrangement = new Arrangement();
        var hardest = new ChartLevel { DifficultyIndex = 2 };
        // Neither the first nor the last position holds the hardest level, so
        // this fails for Levels[0] and for Levels[^1] alike.
        arrangement.Levels.Add(new ChartLevel { DifficultyIndex = 1 });
        arrangement.Levels.Add(hardest);
        arrangement.Levels.Add(new ChartLevel { DifficultyIndex = 0 });

        state.ActiveArrangement = arrangement;

        Assert.Same(hardest, state.ActiveLevel);
    }

    [Fact]
    public void ClearingTheArrangement_ClearsTheLevel()
    {
        var state = new ProjectState { ActiveArrangement = WithLevel(out _) };

        state.ActiveArrangement = null;

        Assert.Null(state.ActiveLevel);
        Assert.Empty(state.PlacedNotes);
    }

    [Fact]
    public void SwitchingToALevellessArrangement_DoesNotKeepThePreviousLevel()
    {
        var state = new ProjectState { ActiveArrangement = WithLevel(out var firstLevel) };
        firstLevel.Notes.Add(new GuitarNote { Fret = 3 });

        state.ActiveArrangement = new Arrangement(); // no levels at all

        Assert.Null(state.ActiveLevel);
        Assert.Empty(state.PlacedNotes);
    }
}
