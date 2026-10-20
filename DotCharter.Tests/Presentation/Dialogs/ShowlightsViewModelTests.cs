using DotCharter.Localization;
using DotCharter.Presentation.Contracts.Dialogs;
using DotCharter.Presentation.Screens.Dialogs;

namespace DotCharter.Tests.Presentation.Dialogs;

/// <summary>
///     Tools ▸ Showlights edits the fog / beams / lasers cue at one point of the
///     song: it opens on the playhead snapped to the grid, saving replaces the
///     KNOWN cues there as one undo step (a cue the editor does not know is
///     left alone), and picking a listed point moves the target there.
/// </summary>
public sealed class ShowlightsViewModelTests
{
    private const string UnknownCue = "SOMETHING_ELSE";

    private static (ShowlightsViewModel Vm, ChartEditHarness H) Open(double playheadMs, params (double Ms, string Type)[] cues)
    {
        var h = new ChartEditHarness();
        foreach (var (ms, type) in cues)
            h.Project.Showlights.Add(new Showlight { Position = h.Editor.SnapToGrid(ms), Type = type, Note = type == UnknownCue ? 99 : 0 });
        h.State.CurrentTimeMs = playheadMs;
        return (new ShowlightsViewModel(h.Services), h);
    }

    private static int IndexOf(IReadOnlyList<string> options, string displayName) =>
        options.ToList().IndexOf(displayName);

    [Fact]
    public void It_opens_on_the_playhead_snapped_to_the_grid_with_the_cues_there_loaded()
    {
        var (vm, _) = Open(1010, (1000, "FOG_GREEN"), (1000, "LASERS_ON"), (4000, "BEAMS_OFF"));

        Assert.Equal("1000 ms", vm.TargetMsText);
        Assert.Equal(IndexOf(vm.FogOptions, "Green"), vm.SelectedFogIndex);
        Assert.Equal(0, vm.SelectedBeamsIndex); // none
        Assert.Equal(IndexOf(vm.LasersOptions, "On"), vm.SelectedLasersIndex);
        Assert.True(vm.HasShowlightsAtTarget);
        Assert.Equal(2, vm.ExistingPositions.Count);
        Assert.Equal(0, vm.SelectedExistingIndex);
        Assert.Equal(LocalizationService.Instance["Dialog_Showlights_None"], vm.FogOptions[0]);
    }

    [Fact]
    public void Save_replaces_the_known_cues_as_one_undo_step_and_leaves_an_unknown_one_alone()
    {
        var (vm, h) = Open(1000, (1000, "FOG_GREEN"), (1000, UnknownCue));
        vm.SelectedFogIndex = IndexOf(vm.FogOptions, "Red");
        vm.SelectedBeamsIndex = IndexOf(vm.BeamsOptions, "Off");

        vm.SaveCommand.Execute(null);

        Assert.True(ChartEditHarness.Answer(vm.Result));
        Assert.Equal(["BEAMS_OFF", "FOG_RED", UnknownCue], h.Project.Showlights.Select(s => s.Type).Order());
        Assert.All(h.Project.Showlights, s => Assert.Equal(h.Editor.SnapToGrid(1000), s.Position));

        h.History.Undo();
        Assert.Equal(["FOG_GREEN", UnknownCue], h.Project.Showlights.Select(s => s.Type).Order());
        Assert.False(h.History.CanUndo);
    }

    [Fact]
    public void Saving_what_is_already_there_writes_nothing_and_answers_false()
    {
        var (vm, h) = Open(1000, (1000, "FOG_GREEN"));

        vm.SaveCommand.Execute(null);

        Assert.False(ChartEditHarness.Answer(vm.Result));
        Assert.False(h.History.CanUndo);
    }

    [Fact]
    public void Delete_removes_every_cue_at_the_target_as_one_undo_step()
    {
        var (vm, h) = Open(1000, (1000, "FOG_GREEN"), (1000, UnknownCue), (4000, "BEAMS_OFF"));

        vm.DeleteAtPositionCommand.Execute(null);

        Assert.True(ChartEditHarness.Answer(vm.Result));
        Assert.Equal(["BEAMS_OFF"], h.Project.Showlights.Select(s => s.Type));
        h.History.Undo();
        Assert.Equal(3, h.Project.Showlights.Count);
    }

    [Fact]
    public void Choosing_a_listed_point_moves_the_target_there_and_loads_its_cues()
    {
        var (vm, h) = Open(0, (1000, "FOG_GREEN"), (4000, "BEAMS_OFF"));
        Assert.False(vm.HasShowlightsAtTarget);
        Assert.Equal(-1, vm.SelectedExistingIndex);

        vm.SelectedExistingIndex = 1;

        Assert.Equal("4000 ms", vm.TargetMsText);
        Assert.Equal(IndexOf(vm.BeamsOptions, "Off"), vm.SelectedBeamsIndex);
        Assert.Equal(0, vm.SelectedFogIndex);
        Assert.True(vm.HasShowlightsAtTarget);
        Assert.Equal(4000, h.State.CurrentTimeMs, 3);
    }

    [Fact]
    public void Cancel_answers_false_and_writes_nothing()
    {
        var (vm, h) = Open(1000, (1000, "FOG_GREEN"));
        vm.SelectedFogIndex = IndexOf(vm.FogOptions, "Red");

        vm.CancelCommand.Execute(null);

        Assert.False(ChartEditHarness.Answer(vm.Result));
        Assert.Equal(["FOG_GREEN"], h.Project.Showlights.Select(s => s.Type));
    }

    [Fact]
    public void Conforms_to_its_contract()
    {
        var (vm, _) = Open(1000, (1000, "FOG_GREEN"));
        Assert.Empty(ContractConformanceTests.Violations(typeof(IShowlightsScreen), vm));
    }
}
