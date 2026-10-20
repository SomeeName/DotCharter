using DotCharter.Core.Domain.Vocals;
using DotCharter.Presentation.Contracts.Dialogs;
using DotCharter.Presentation.Screens.Dialogs;

namespace DotCharter.Tests.Presentation.Dialogs;

/// <summary>
///     The vocal track's name and lyric colour: one Save is ONE undo node, a new
///     track's add and active-track switch are one node too, and the four
///     swatches write the very property the picker writes.
/// </summary>
public sealed class VocalTrackSettingsViewModelTests
{
    private static (ChartEditHarness H, VocalTrack Track) WithVocals()
    {
        var h = new ChartEditHarness();
        var track = new VocalTrack { Name = "Vocals", ColorR = 1, ColorG = 2, ColorB = 3, ColorA = 255 };
        h.Project.VocalTracks.Add(track);
        h.Editor.ActiveVocalTrack = track;
        return (h, track);
    }

    private static VocalTrackSettingsViewModel Open(ChartEditHarness h, bool createNew = false)
    {
        var vm = new VocalTrackSettingsViewModel(h.Editor, h.State, h.History, FakeLocalization.English());
        vm.Configure(new VocalTrackSettingsRequest(createNew));
        return vm;
    }

    [Fact]
    public void It_opens_on_the_active_tracks_name_and_colour()
    {
        var (h, _) = WithVocals();

        var vm = Open(h);

        Assert.Equal("Vocals", vm.Name);
        Assert.Equal("#010203", vm.TrackColor);
        Assert.DoesNotContain(vm.ColorSwatches, s => s.IsSelected);
    }

    [Fact]
    public void Save_is_one_undo_step_that_restores_name_and_colour()
    {
        var (h, track) = WithVocals();
        var vm = Open(h);
        vm.Name = "Backing";
        vm.TrackColor = "#C80A141E";

        vm.SaveCommand.Execute(null);

        Assert.True(ChartEditHarness.Answer(vm.Result));
        Assert.Equal("Backing", track.Name);
        Assert.Equal((10, 20, 30, 200), (track.ColorR, track.ColorG, track.ColorB, track.ColorA));

        h.History.Undo();
        Assert.Equal("Vocals", track.Name);
        Assert.Equal((1, 2, 3, 255), (track.ColorR, track.ColorG, track.ColorB, track.ColorA));
    }

    [Fact]
    public void A_new_track_is_one_undoable_add_and_undo_restores_the_active_pick()
    {
        var (h, previous) = WithVocals();
        var vm = Open(h, createNew: true);
        Assert.Equal("Vocals 2", vm.Name);
        vm.Name = "Harmony";

        vm.SaveCommand.Execute(null);

        Assert.Equal(2, h.Project.VocalTracks.Count);
        Assert.Equal("Harmony", h.Editor.ActiveVocalTrack?.Name);

        h.History.Undo();
        Assert.Single(h.Project.VocalTracks);
        Assert.Same(previous, h.Editor.ActiveVocalTrack);
    }

    /// <summary>A swatch click IS the colour edit, and exactly the matching swatch lights — alpha ignored, the picker owns it.</summary>
    [Fact]
    public void A_swatch_writes_the_colour_the_picker_writes_and_lights_alone()
    {
        var (h, _) = WithVocals();
        var vm = Open(h);
        Assert.Equal(4, vm.ColorSwatches.Count);

        var green = vm.ColorSwatches[2];
        green.PickCommand.Execute(null);

        Assert.Equal(green.Color, vm.TrackColor);
        Assert.Same(green, Assert.Single(vm.ColorSwatches, s => s.IsSelected));

        vm.TrackColor = "#804ADE80";
        Assert.True(green.IsSelected, "a translucent pick of the same colour still lights its swatch");
        Assert.All(vm.ColorSwatches, s => Assert.False(string.IsNullOrWhiteSpace(s.Name)));
    }

    [Fact]
    public void Cancel_answers_false_and_changes_nothing()
    {
        var (h, track) = WithVocals();
        var vm = Open(h);
        vm.Name = "Changed";

        vm.CancelCommand.Execute(null);

        Assert.False(ChartEditHarness.Answer(vm.Result));
        Assert.Equal("Vocals", track.Name);
        Assert.False(h.History.CanUndo);
    }

    [Fact]
    public void Conforms_to_its_contract()
    {
        var (h, _) = WithVocals();
        Assert.Empty(ContractConformanceTests.Violations(typeof(IVocalTrackSettingsScreen), Open(h)));
    }
}
