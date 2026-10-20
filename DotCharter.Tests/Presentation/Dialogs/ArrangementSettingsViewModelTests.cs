using DotCharter.Presentation.Contracts.Dialogs;
using DotCharter.Presentation.Screens.Dialogs;

namespace DotCharter.Tests.Presentation.Dialogs;

/// <summary>
///     Arrangement ▸ Settings over a real chart editor: a tuning the five presets cannot
///     name survives a Save that never touched the picker, hand-edited offsets make the
///     name CUSTOM, the derived technique flags are written only while pinned, and a new
///     arrangement is one undo step.
/// </summary>
public sealed class ArrangementSettingsViewModelTests
{
    [Fact]
    public void An_untouched_picker_keeps_a_tuning_the_presets_cannot_name()
    {
        var chart = new ChartEditHarness();
        var lead = chart.Project.Arrangements[0];
        Assert.True(TuningUtilities.TryGetOffsetsForTuningType("DADGAD", 6, out var dadgad));
        lead.TuningType = "DADGAD";
        lead.TuningPitches.Clear();
        foreach (var step in dadgad) lead.TuningPitches.Add(new TuningOffset(step));

        var vm = Open(chart);
        Assert.Equal("Custom", vm.TuningPreset);
        vm.Capo = 3;
        vm.SaveCommand.Execute(null);

        Assert.Equal("DADGAD", lead.TuningType);
        Assert.Equal(dadgad, lead.TuningPitches.Select(t => t.Step));
        Assert.Equal(3, lead.Capo);
    }

    [Fact]
    public void Picking_a_preset_rewrites_the_offsets_and_its_name_is_written()
    {
        var chart = new ChartEditHarness();
        var vm = Open(chart);

        vm.TuningPreset = "Drop D";
        Assert.Equal(-2, vm.TuningOffsets[0].Step);
        vm.SaveCommand.Execute(null);

        Assert.Equal("E_DROP_D", chart.Project.Arrangements[0].TuningType);
    }

    [Fact]
    public void Offsets_edited_by_hand_under_a_named_preset_are_saved_as_custom()
    {
        var chart = new ChartEditHarness();
        var vm = Open(chart);

        vm.TuningPreset = "Drop D";
        vm.TuningOffsets[5].Step = 3;
        vm.SaveCommand.Execute(null);

        Assert.Equal("CUSTOM", chart.Project.Arrangements[0].TuningType);
    }

    [Fact]
    public void Changing_the_string_count_adds_and_drops_offset_rows()
    {
        var vm = Open(new ChartEditHarness());

        vm.Strings = 7;
        Assert.Equal(7, vm.TuningOffsets.Count);
        vm.Strings = 4;
        Assert.Equal(4, vm.TuningOffsets.Count);
    }

    [Fact]
    public void Derived_flags_are_written_only_while_pinned()
    {
        var chart = new ChartEditHarness();
        var lead = chart.Project.Arrangements[0];

        var unpinned = Open(chart);
        unpinned.DerivedFlags[0].IsSet = true; // Bends, with no bend in the chart
        unpinned.SaveCommand.Execute(null);
        Assert.Equal(0, lead.Properties.Bends);
        Assert.False(lead.Properties.ManualOverride);

        var pinned = Open(chart);
        pinned.OverrideDerivedFlags = true;
        pinned.DerivedFlags[0].IsSet = true;
        pinned.SaveCommand.Execute(null);
        Assert.Equal(1, lead.Properties.Bends);
        Assert.True(lead.Properties.ManualOverride);
    }

    [Fact]
    public void Picked_bass_is_dropped_once_the_arrangement_is_not_a_bass()
    {
        var chart = new ChartEditHarness();
        var vm = Open(chart);

        vm.Type = ArrangementType.Bass;
        Assert.True(vm.IsBass);
        vm.PickedBass = true;
        vm.Type = ArrangementType.Rhythm;
        vm.SaveCommand.Execute(null);

        Assert.False(chart.Project.Arrangements[0].PickedBass);
    }

    [Fact]
    public void A_save_is_one_undo_step_restored_in_place()
    {
        var chart = new ChartEditHarness();
        var lead = chart.Project.Arrangements[0];
        var vm = Open(chart);

        vm.Name = "Rhythm";
        vm.SaveCommand.Execute(null);
        Assert.True(ChartEditHarness.Answer(vm.Result));

        chart.History.Undo();
        Assert.Same(lead, chart.Project.Arrangements[0]);
        Assert.Equal("Lead", lead.Name);
    }

    [Fact]
    public void Cancel_answers_false_and_writes_nothing()
    {
        var chart = new ChartEditHarness();
        var vm = Open(chart);

        vm.Name = "Rhythm";
        vm.CancelCommand.Execute(null);

        Assert.False(ChartEditHarness.Answer(vm.Result));
        Assert.Equal("Lead", chart.Project.Arrangements[0].Name);
        Assert.False(chart.History.CanUndo);
    }

    /// <summary>
    ///     #217: the tuning row named no strings and no notes, so "-2 0 0 0 0 0" had to be
    ///     decoded in your head. Each box now carries its open string (lowest first) and
    ///     the note it ends up at, and a screen reader hears a different name per box.
    /// </summary>
    [Fact]
    public void Each_tuning_box_names_its_string_and_the_note_it_ends_up_at()
    {
        var vm = Open(new ChartEditHarness());
        vm.TuningPreset = "Drop D";

        Assert.Equal(["E", "A", "D", "G", "B", "e"], vm.TuningOffsets.Select(t => t.StringLabel));
        Assert.Equal(["D", "A", "D", "G", "B", "E"], vm.TuningOffsets.Select(t => t.ResultingNote));
        Assert.Equal(6, vm.TuningOffsets.Select(t => t.AccessibleName).Distinct().Count());
        Assert.All(vm.TuningOffsets, t => Assert.Contains(t.StringLabel, t.AccessibleName, StringComparison.Ordinal));
    }

    [Fact]
    public void Editing_a_step_renames_its_note_and_a_new_string_count_relabels_the_row()
    {
        var vm = Open(new ChartEditHarness());
        var changed = new List<string?>();
        vm.TuningOffsets[0].PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        vm.TuningOffsets[0].Step = -5;

        Assert.Equal("B", vm.TuningOffsets[0].ResultingNote);
        Assert.Contains(nameof(ITuningStep.ResultingNote), changed);

        vm.Strings = 4;
        Assert.Equal(["E", "A", "D", "G"], vm.TuningOffsets.Select(t => t.StringLabel));
        vm.Strings = 7;
        Assert.Equal(["B", "E", "A", "D", "G", "B", "E"], vm.TuningOffsets.Select(t => t.StringLabel));
    }

    [Fact]
    public void Conforms_to_its_contract() => Assert.Empty(ContractConformanceTests.Violations(typeof(IArrangementSettingsScreen), Open(new ChartEditHarness())));

    private static ArrangementSettingsViewModel Open(ChartEditHarness chart, bool createNew = false)
    {
        var vm = new ArrangementSettingsViewModel(chart.Services);
        vm.Configure(new ArrangementSettingsRequest(createNew));
        return vm;
    }
}
