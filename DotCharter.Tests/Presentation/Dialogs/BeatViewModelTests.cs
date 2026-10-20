using DotCharter.Presentation.Contracts.Dialogs;
using DotCharter.Presentation.Screens.Dialogs;

namespace DotCharter.Tests.Presentation.Dialogs;

/// <summary>
///     The Beat dialog is the only place a charter edits one beat's tempo,
///     time signature and anchor, and its two time-moving cases are exact
///     opposites — which is why they belong in one file.
///     <list type="bullet">
///         <item>Editing the BPM must NOT move the beat: the tempo on a beat governs what follows it.</item>
///         <item>
///             Clearing the ANCHOR must move it — un-anchoring says "the tempo
///             map, not me, decides where this beat sits".
///         </item>
///     </list>
///     Both are only visible on a grid whose spacing does not already match its
///     printed BPM, which is why the fixtures move beat 4 off the grid first.
/// </summary>
public sealed class BeatViewModelTests
{
    private static BeatViewModel Open(ChartEditHarness h, int index)
    {
        var vm = new BeatViewModel(h.Services, h.Tempo);
        vm.Configure(new BeatRequest(h.Project.Beats[index], index));
        return vm;
    }

    [Fact]
    public void EditingTheBpm_DoesNotMoveTheEditedBeat()
    {
        var h = new ChartEditHarness();
        h.Project.Beats[4].Time = 2300;

        var vm = Open(h, 4);
        vm.Bpm = 90;
        vm.SaveCommand.Execute(null);

        Assert.True(ChartEditHarness.Answer(vm.Result));
        Assert.Equal(2300.0, h.Project.Beats[4].Time, 6);
        Assert.Equal(90.0, h.Project.Beats[4].Bpm, 6);
        Assert.Equal(2300.0 + 60000.0 / 90.0, h.Project.Beats[5].Time, 6);
    }

    [Fact]
    public void ClearingTheAnchor_MovesTheBeatBackOntoTheGrid()
    {
        var h = new ChartEditHarness();
        h.Project.Beats[4].Time = 2300;
        h.Project.Beats[4].IsAnchor = true;

        var vm = Open(h, 4);
        vm.IsAnchor = false;
        vm.SaveCommand.Execute(null);

        Assert.False(h.Project.Beats[4].IsAnchor);
        Assert.Equal(2000.0, h.Project.Beats[4].Time, 6);
        Assert.Equal(2500.0, h.Project.Beats[5].Time, 6);
    }

    [Fact]
    public void SettingTheAnchor_LeavesTheBeatWhereItIs()
    {
        var h = new ChartEditHarness();
        h.Project.Beats[4].Time = 2300;

        var vm = Open(h, 4);
        vm.IsAnchor = true;
        vm.SaveCommand.Execute(null);

        Assert.True(h.Project.Beats[4].IsAnchor);
        Assert.Equal(2300.0, h.Project.Beats[4].Time, 6);
    }

    /// <summary>A signature runs from the edited beat to the next signature change, and re-flags the downbeats.</summary>
    [Fact]
    public void ChangingTheSignature_PropagatesItAndMovesTheDownbeats()
    {
        var h = new ChartEditHarness();

        var vm = Open(h, 0);
        vm.BeatsInMeasure = 3;
        vm.SaveCommand.Execute(null);

        Assert.All(h.Project.Beats, b => Assert.Equal(3, b.BeatsInMeasure));
        Assert.True(h.Project.Beats[3].IsMeasure);
        Assert.False(h.Project.Beats[4].IsMeasure);
        h.History.Undo();
        Assert.All(h.Project.Beats, b => Assert.Equal(4, b.BeatsInMeasure));
    }

    [Fact]
    public void Open_ShowsTheBeatItWasGiven()
    {
        var h = new ChartEditHarness();
        h.Project.Beats[2].Bpm = 97;
        h.Project.Beats[2].IsAnchor = true;

        var vm = Open(h, 2);

        Assert.Equal(97, vm.Bpm);
        Assert.Equal(4, vm.BeatsInMeasure);
        Assert.Equal(4, vm.NoteDenominator);
        Assert.True(vm.IsAnchor);
    }

    [Fact]
    public void Save_OnALockedBeatMap_ChangesNothing_AndAnswersFalse()
    {
        var h = new ChartEditHarness();
        h.Project.IsBeatMapLocked = true;

        var vm = Open(h, 4);
        vm.Bpm = 60;
        vm.SaveCommand.Execute(null);

        Assert.False(ChartEditHarness.Answer(vm.Result));
        Assert.Equal(120, h.Project.Beats[4].Bpm);
    }

    [Fact]
    public void Esc_AnswersFalse_AndTouchesNothing()
    {
        var h = new ChartEditHarness();

        var vm = Open(h, 4);
        vm.Bpm = 60;
        Assert.True(vm.TryCancel());

        Assert.False(ChartEditHarness.Answer(vm.Result));
        Assert.False(h.History.CanUndo);
    }

    [Fact]
    public void Conforms_to_its_contract() =>
        Assert.Empty(ContractConformanceTests.Violations(typeof(IBeatScreen), Open(new ChartEditHarness(), 4)));
}
