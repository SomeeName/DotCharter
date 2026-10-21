using DotCharter.Services.Editing.Catalog;
using DotCharter.Services.Editing.Selection;
using DotCharter.Services.Editing.Sync;
using DotCharter.Services.Editing.Tracks;
using DotCharter.UI.ViewModels.Editor;

namespace DotCharter.Tests.Services.Editing.Maintenance;

/// <summary>
///     #80: code that read <c>Levels[0]</c> / <c>Levels.FirstOrDefault()</c> as "the chart" got the
///     EASIEST level of a levelled arrangement. Every fixture here is a two-level arrangement where
///     ONLY the hardest level holds the content under test, so a regression to level 0 sees nothing.
/// </summary>
public sealed class HardestLevelRegressionTests
{
    /// <summary>Level 0 (easiest) empty; level 1 (hardest) holds <paramref name="notes" /> notes.</summary>
    private static Arrangement TwoLevels(string name, int notes, MuteType mute = MuteType.None)
    {
        var arrangement = new Arrangement { Name = name, ArrangementType = ArrangementType.Lead };
        arrangement.Levels.Add(new ChartLevel { DifficultyIndex = 0 });
        var hardest = new ChartLevel { DifficultyIndex = 1 };
        for (var i = 0; i < notes; i++)
        {
            hardest.Notes.Add(new GuitarNote
            {
                Position = new FractionalPosition(i), StringIndex = 1, Fret = 3 + i, Mute = mute
            });
        }

        arrangement.Levels.Add(hardest);
        return arrangement;
    }

    [Fact]
    public void Sync_ReadsAndWritesTheHardestLevel()
    {
        var source = TwoLevels("Lead", 6, MuteType.Palm);
        var target = TwoLevels("Rhythm", 6);

        var summary = ArrangementSoundSync.Apply(source, target);

        Assert.Equal(6, summary.SharedSounds);
        Assert.Equal(6, summary.SoundsChanged);
        Assert.All(target.Levels[1].Notes, n => Assert.Equal(MuteType.Palm, n.Mute));
        Assert.Empty(target.Levels[0].Notes);
    }

    [Fact]
    public void SyncArrangement_RegeneratesTheEasierLevelsFromTheSyncedHardest()
    {
        var h = new ChartPowerToolsTests.Harness();
        var project = h.State.ActiveProject!;
        var source = TwoLevels("Lead2", 6, MuteType.Palm);
        var target = TwoLevels("Rhythm", 6);
        project.Arrangements.Add(source);
        project.Arrangements.Add(target);
        Assert.Empty(target.Levels[0].Notes);

        var summary = h.Maintenance.SyncArrangement(source, target);

        Assert.True(summary.WouldChangeAnything);
        Assert.Equal(2, target.Levels.Count);
        Assert.NotEmpty(target.Levels[0].Notes);
        Assert.All(target.Levels[1].Notes, n => Assert.Equal(MuteType.Palm, n.Mute));
    }

    [Fact]
    public void UndoWithNoActiveLevel_LandsOnTheHardestLevel()
    {
        var h = new ChartPowerToolsTests.Harness();
        var arrangement = h.State.ActiveArrangement!;
        var hardest = new ChartLevel { DifficultyIndex = 1 };
        arrangement.Levels.Add(hardest);
        h.State.ActiveLevel = null;

        Assert.True(h.Maintenance.RunAsSingleUndoStep(() => { }));
        h.History.Undo();

        Assert.Equal(1, h.State.ActiveLevel?.DifficultyIndex);
        Assert.Same(ChartLevels.Hardest(h.State.ActiveArrangement), h.State.ActiveLevel);
    }

    [Fact]
    public void FocusedTrack_FallsBackToTheHardestLevel_WhenThereIsNoActiveLevel()
    {
        var state = new ProjectState();
        using var registry = new TrackContextRegistry(state, new SelectionService());
        var project = new ChartProject();
        var arrangement = TwoLevels("Lead", 3);
        project.Arrangements.Add(arrangement);
        state.ActiveProject = project;
        state.ActiveArrangement = arrangement;
        registry.Focus(arrangement);
        state.ActiveLevel = arrangement.Levels[0];
        Assert.Same(arrangement.Levels[0], registry.FocusedTrack!.Level);

        state.ActiveLevel = null;

        Assert.Same(arrangement.Levels[1], registry.FocusedTrack!.Level);
    }

    [Fact]
    public void SoundQueue_FallsBackToTheHardestLevel_WhenTheAmbientLevelIsForeign()
    {
        var arrangement = TwoLevels("Lead", 4);
        var foreign = new ChartLevel();
        var vm = new SoundQueueViewModel(
            () => arrangement, () => foreign, () => [], () => [], new SelectionService(), _ => { },
            FakeLocalization.English());

        Assert.Equal(4, vm.Matches(SoundQueueFilter.AnySound).Count);
    }

    [Fact]
    public void PhraseFragmentRead_ReadsTheHardestLevel()
    {
        var project = new ChartProject();
        project.Arrangements.Add(TwoLevels("Fragment", 3));

        var entry = PhraseCatalogFragment.Read("x", project, DateTime.UtcNow);

        Assert.NotNull(entry);
        Assert.Equal(3, entry!.Payload.Notes.Count);
    }
}
