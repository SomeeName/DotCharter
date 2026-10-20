using DotCharter.UI.ViewModels.Editor;

namespace DotCharter.Tests.UI.ViewModels.Editor;

/// <summary>
///     #395: a Go to from the quality report or the Inspector can land the editor on an easier level,
///     and nothing led back. The selector steps through the levels by difficulty and returns to the
///     hardest, and says when the editor is below it.
/// </summary>
public sealed class DifficultyLevelSelectorTests
{
    /// <summary>Three levels stored out of order: list order is not difficulty order.</summary>
    private static (ProjectState State, ChartLevel Easy, ChartLevel Mid, ChartLevel Hard) ThreeLevels()
    {
        var easy = new ChartLevel { DifficultyIndex = 0 };
        var mid = new ChartLevel { DifficultyIndex = 1 };
        var hard = new ChartLevel { DifficultyIndex = 2 };
        var arrangement = new Arrangement { Name = "Lead" };
        arrangement.Levels.Add(mid);
        arrangement.Levels.Add(hard);
        arrangement.Levels.Add(easy);
        var state = new ProjectState { ActiveArrangement = arrangement };
        return (state, easy, mid, hard);
    }

    [Fact]
    public void An_arrangement_opens_on_its_hardest_level_with_nothing_to_flag()
    {
        var (state, _, _, hard) = ThreeLevels();
        var levels = new DifficultyLevelSelector(state);

        Assert.Same(hard, state.ActiveLevel);
        Assert.Equal((3, 3, true, false), (levels.LevelNumber, levels.LevelCount, levels.HasLevels, levels.IsBelowHardest));
        Assert.False(levels.HarderCommand.CanExecute(null));
        Assert.False(levels.HardestCommand.CanExecute(null));
    }

    [Fact]
    public void A_go_to_on_an_easier_level_is_flagged_and_Hardest_switches_back()
    {
        var (state, easy, _, hard) = ThreeLevels();
        var levels = new DifficultyLevelSelector(state);
        var raised = new List<string?>();
        levels.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        state.ActiveLevel = easy;

        Assert.Equal(1, levels.LevelNumber);
        Assert.True(levels.IsBelowHardest);
        Assert.Contains(nameof(DifficultyLevelSelector.IsBelowHardest), raised);
        Assert.True(levels.HardestCommand.CanExecute(null));
        Assert.False(levels.EasierCommand.CanExecute(null));

        levels.HardestCommand.Execute(null);

        Assert.Same(hard, state.ActiveLevel);
        Assert.False(levels.IsBelowHardest);
    }

    [Fact]
    public void Easier_and_Harder_step_by_difficulty_not_by_list_order()
    {
        var (state, easy, mid, hard) = ThreeLevels();
        var levels = new DifficultyLevelSelector(state);

        levels.EasierCommand.Execute(null);
        Assert.Same(mid, state.ActiveLevel);
        levels.EasierCommand.Execute(null);
        Assert.Same(easy, state.ActiveLevel);
        levels.HarderCommand.Execute(null);
        Assert.Same(mid, state.ActiveLevel);
        Assert.Equal(2, levels.LevelNumber);
        Assert.Same(hard, ChartLevels.Hardest(state.ActiveArrangement));
    }

    [Fact]
    public void A_one_level_arrangement_has_nothing_to_pick()
    {
        var arrangement = new Arrangement { Name = "Bass" };
        arrangement.Levels.Add(new ChartLevel());
        var levels = new DifficultyLevelSelector(new ProjectState { ActiveArrangement = arrangement });

        Assert.False(levels.HasLevels);
        Assert.False(levels.IsBelowHardest);
        Assert.False(levels.EasierCommand.CanExecute(null));
    }
}
