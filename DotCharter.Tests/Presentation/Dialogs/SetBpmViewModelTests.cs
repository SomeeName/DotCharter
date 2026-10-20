using DotCharter.Presentation.Contracts.Dialogs;
using DotCharter.Presentation.Screens.Dialogs;

namespace DotCharter.Tests.Presentation.Dialogs;

/// <summary>
///     "Set BPM for all measures" writes a NOMINAL tempo beside the grid it
///     rewrites — <c>ChartProject.Bpm</c> and the transport's BPM — and those two
///     are not part of the <c>MutateBeatsAction</c> the undo history records. So
///     the pair used to survive a Ctrl+Z that put every beat back: the dialog's
///     own re-seed then offered the DISCARDED tempo as "the current one", and
///     pressing Save on it re-applied the value the user had just undone.
/// </summary>
public sealed class SetBpmViewModelTests
{
    private static SetBpmViewModel Open(ChartEditHarness h) => new(h.Services, h.Tempo, h.Transport);

    /// <summary>The load-bearing case: undo restores the grid, and the nominal tempo comes back with it.</summary>
    [Fact]
    public void Undo_RestoresTheNominalTempoBesideTheGrid()
    {
        var h = new ChartEditHarness();

        var vm = Open(h);
        vm.Bpm = 200;
        vm.SaveCommand.Execute(null);

        Assert.Equal(200, h.Editor.Beats[0].Bpm, 3);
        Assert.Equal(200, h.Project.Bpm, 3);
        Assert.Equal(200, h.Transport.Bpm, 3);

        h.History.Undo();

        Assert.Equal(120, h.Editor.Beats[0].Bpm, 3);
        Assert.Equal(120, h.Project.Bpm, 3);
        Assert.Equal(120, h.Transport.Bpm, 3);
    }

    [Fact]
    public void Redo_ReappliesTheNominalTempo()
    {
        var h = new ChartEditHarness();

        var vm = Open(h);
        vm.Bpm = 200;
        vm.SaveCommand.Execute(null);
        h.History.Undo();
        h.History.Redo();

        Assert.Equal(200, h.Editor.Beats[0].Bpm, 3);
        Assert.Equal(200, h.Project.Bpm, 3);
        Assert.Equal(200, h.Transport.Bpm, 3);
    }

    /// <summary>
    ///     Reopen after an undo and it must offer the tempo the chart HAS. The
    ///     view model is built per open, so "reopen" is a second instance.
    /// </summary>
    [Fact]
    public void Open_SeedsFromTheGrid_NotAStaleNominalTempo()
    {
        var h = new ChartEditHarness();

        var first = Open(h);
        first.Bpm = 200;
        first.SaveCommand.Execute(null);
        h.History.Undo();
        h.Project.Bpm = 200;

        Assert.Equal(120, Open(h).Bpm, 3);
    }

    /// <summary>The control: an undisturbed apply records exactly one node and answers true.</summary>
    [Fact]
    public void Save_AppliesTheUniformTempoAsOneUndoNode_AndAnswersTrue()
    {
        var h = new ChartEditHarness();

        var vm = Open(h);
        vm.Bpm = 90;
        vm.SaveCommand.Execute(null);

        Assert.True(ChartEditHarness.Answer(vm.Result));
        Assert.All(h.Editor.Beats, b => Assert.Equal(90, b.Bpm, 3));
        Assert.True(h.History.CanUndo);
        h.History.Undo();
        Assert.False(h.History.CanUndo);
    }

    [Fact]
    public void Save_OnALockedBeatMap_ChangesNothing_SaysSo_AndAnswersFalse()
    {
        var h = new ChartEditHarness();
        h.Project.IsBeatMapLocked = true;

        var vm = Open(h);
        vm.Bpm = 90;
        vm.SaveCommand.Execute(null);

        Assert.False(ChartEditHarness.Answer(vm.Result));
        Assert.All(h.Editor.Beats, b => Assert.Equal(120, b.Bpm, 3));
        Assert.False(h.History.CanUndo);
    }

    [Fact]
    public void Cancel_AnswersFalse_AndTouchesNothing()
    {
        var h = new ChartEditHarness();

        var vm = Open(h);
        vm.Bpm = 90;
        vm.CancelCommand.Execute(null);

        Assert.False(ChartEditHarness.Answer(vm.Result));
        Assert.False(h.History.CanUndo);
    }

    [Fact]
    public void Conforms_to_its_contract() =>
        Assert.Empty(ContractConformanceTests.Violations(typeof(ISetBpmScreen), Open(new ChartEditHarness())));
}
