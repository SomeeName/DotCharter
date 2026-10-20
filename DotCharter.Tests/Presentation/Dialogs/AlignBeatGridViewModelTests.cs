using System.Globalization;
using DotCharter.Presentation.Contracts.Dialogs;
using DotCharter.Presentation.Screens.Dialogs;
using DotCharter.Tests.Services.Editing.Maintenance;
using DotCharter.Tests.Services.Tempo;

namespace DotCharter.Tests.Presentation.Dialogs;

/// <summary>
///     Tempo map ▸ Align beat grid to notes (#403): the dialog opens with the measured
///     offset in the box, says how far the notes sit from the grid now and after the
///     move the box holds, and Apply moves the grid through the maintenance verb.
/// </summary>
public sealed class AlignBeatGridViewModelTests
{
    [Fact]
    public void Opens_WithTheMeasuredOffset_AndBothReadouts()
    {
        var h = Build(gridErrorMs: -121);

        var vm = Open(h);

        Assert.True(vm.HasEstimate);
        Assert.Equal(121, vm.OffsetMs, 0.5);
        Assert.NotEqual(vm.OffNowText, vm.OffAfterText);
        Assert.Contains("121", vm.EstimateText, StringComparison.Ordinal);
        Assert.Contains(BeatGridFixtures.NotePositions().Count.ToString(CultureInfo.InvariantCulture), vm.EstimateText,
            StringComparison.Ordinal);
    }

    /// <summary>"After" follows the box: a zero move reads exactly what "now" reads.</summary>
    [Fact]
    public void TheAfterReadout_FollowsTheOffset()
    {
        var vm = Open(Build(gridErrorMs: -121));
        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        vm.OffsetMs = 0;

        Assert.Equal(vm.OffNowText, vm.OffAfterText);
        Assert.Contains(nameof(IAlignBeatGridScreen.OffAfterText), raised);
    }

    [Fact]
    public async Task Apply_MovesTheGrid_AsOneUndoStep_AndAnswersTrue()
    {
        var h = Build(gridErrorMs: -121);
        var firstBeat = h.State.ActiveProject!.Beats[0].Time;
        var vm = Open(h);

        vm.ApplyCommand.Execute(null);

        Assert.True(vm.Result.IsCompletedSuccessfully);
        Assert.True(await vm.Result);
        Assert.Equal(firstBeat + vm.OffsetMs, h.State.ActiveProject!.Beats[0].Time, 6);
        h.History.Undo();
        Assert.Equal(firstBeat, h.State.ActiveProject!.Beats[0].Time, 6);
    }

    [Fact]
    public async Task Apply_OnALockedBeatMap_AnswersFalse()
    {
        var h = Build(gridErrorMs: -121);
        h.State.ActiveProject!.IsBeatMapLocked = true;
        var vm = Open(h);

        vm.ApplyCommand.Execute(null);

        Assert.True(vm.Result.IsCompleted);
        Assert.False(await vm.Result);
        Assert.False(h.History.CanUndo);
    }

    [Fact]
    public void TooFewNotes_LeavesTheOffsetToTheUser()
    {
        var h = Build(gridErrorMs: -121);
        h.State.ActiveLevel!.Notes.Clear();

        var vm = Open(h);

        Assert.False(vm.HasEstimate);
        Assert.Equal(0, vm.OffsetMs);
        Assert.Equal(FakeLocalization.English()["Dialog_AlignBeatGrid_NoEstimate"], vm.EstimateText);
    }

    [Fact]
    public async Task Cancel_AnswersFalse_AndMovesNothing()
    {
        var h = Build(gridErrorMs: -121);
        var vm = Open(h);

        vm.CancelCommand.Execute(null);

        Assert.True(vm.Result.IsCompleted);
        Assert.False(await vm.Result);
        Assert.False(h.History.CanUndo);
    }

    [Fact]
    public void Conforms_to_its_contract() =>
        Assert.Empty(ContractConformanceTests.Violations(typeof(IAlignBeatGridScreen), Open(Build(gridErrorMs: -121))));

    private static AlignBeatGridViewModel Open(ChartPowerToolsTests.Harness h) =>
        new(h.State, h.Maintenance, FakeLocalization.English(), new FakeAppLogger());

    private static ChartPowerToolsTests.Harness Build(double gridErrorMs)
    {
        var h = new ChartPowerToolsTests.Harness();
        var chart = BeatGridFixtures.Chart(gridErrorMs);
        var project = h.State.ActiveProject!;
        project.Beats.Clear();
        foreach (var beat in chart.Beats) project.Beats.Add(beat);
        var level = h.State.ActiveLevel!;
        level.Notes.Clear();
        foreach (var note in chart.Arrangements[0].Levels[0].Notes) level.Notes.Add(note);
        return h;
    }
}
