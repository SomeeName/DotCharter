using DotCharter.Presentation.Screens.Dialogs;
using DotCharter.Services.Editing.Events;

namespace DotCharter.Tests.Presentation.Dialogs;

/// <summary>#450: every edit in the Tone designer is its own undo step as it is made, a drag or typing is ONE step, and the
///     designer's lists follow an undo or redo while it is open. No Avalonia instance.</summary>
public sealed partial class ToneDesignerViewModelTests
{
    private static (ChartProject Project, Harness Harness, ToneDesignerViewModel Vm) OpenWithClean()
    {
        var project = CreateProject();
        project.ToneDefinitions.Add(AuthoredTone("clean", "Clean"));
        var harness = new Harness(project);
        return (project, harness, harness.Open());
    }

    [Fact]
    public void AKnobDrag_IsRecordedAtOnce_AsOneStep_AndUndoRedoRestoreIt()
    {
        var (project, harness, vm) = OpenWithClean();
        var original = project.ToneDefinitions[0].Json;
        var depth = harness.History.GetUndoSteps().Count;
        var knob = vm.Editor.GearSlots[0].Knobs.First(k => Math.Abs(k.Knob.DefaultValue - k.Maximum) > 0.01);
        var key = knob.Knob.Key;

        knob.Value = knob.Maximum / 2;
        knob.Value = knob.Maximum;
        var edited = project.ToneDefinitions[0].Json;
        Assert.NotEqual(original, edited);   // recorded while the dialog is open, not on close
        Assert.Equal(depth + 1, harness.History.GetUndoSteps().Count);

        vm.EndEditGesture();
        vm.UndoCommand.Execute(null);
        Assert.Equal(original, project.ToneDefinitions[0].Json);
        Assert.Equal(knob.Knob.DefaultValue, vm.Editor.GearSlots[0].Knobs.First(k => k.Knob.Key == key).Value, 3);
        vm.RedoCommand.Execute(null);
        Assert.Equal(edited, project.ToneDefinitions[0].Json);
    }

    [Fact]
    public void ALaterGestureOnTheSameKnob_IsANewStep()
    {
        var (_, harness, vm) = OpenWithClean();
        var depth = harness.History.GetUndoSteps().Count;
        var knob = vm.Editor.GearSlots[0].Knobs.First(k => Math.Abs(k.Knob.DefaultValue - k.Maximum) > 0.01);

        knob.Value = knob.Maximum;
        vm.EndEditGesture();
        knob.Value = knob.Minimum;

        Assert.Equal(depth + 2, harness.History.GetUndoSteps().Count);
    }

    [Fact]
    public void TwoGearSwaps_AreTwoSteps_EvenWithoutAGestureEnd()
    {
        var (project, harness, vm) = OpenWithClean();
        var depth = harness.History.GetUndoSteps().Count;
        var slot = vm.Editor.GearSlots[0];
        var first = slot.Options.First(o => o.Gear is { } g && g.Key != slot.SelectedOption.Gear!.Key);
        var second = slot.Options.First(o => o.Gear is { } g && g.Key != slot.SelectedOption.Gear!.Key && g.Key != first.Gear!.Key);
        var original = project.ToneDefinitions[0].Json;

        slot.SelectedOption = first;
        var afterFirst = project.ToneDefinitions[0].Json;
        slot.SelectedOption = second;

        Assert.Equal(depth + 2, harness.History.GetUndoSteps().Count);
        vm.UndoCommand.Execute(null);
        Assert.Equal(afterFirst, project.ToneDefinitions[0].Json);
        vm.UndoCommand.Execute(null);
        Assert.Equal(original, project.ToneDefinitions[0].Json);
    }

    [Fact]
    public void ANameEdit_IsRecordedAtOnce_AndUndoRefreshesTheListAndTheEditor()
    {
        var (project, harness, vm) = OpenWithClean();

        vm.Editor.EditName = "Clean edited";
        Assert.Equal("Clean edited", project.ToneDefinitions[0].Name);
        Assert.Equal("Clean edited", vm.ProjectTones[0].Name);

        vm.UndoCommand.Execute(null);
        Assert.Equal("Clean", project.ToneDefinitions[0].Name);
        Assert.Equal("Clean", vm.ProjectTones[0].Name);
        Assert.Equal("Clean", vm.Editor.EditName);
        Assert.False(vm.Editor.IsDirty);

        vm.RedoCommand.Execute(null);
        Assert.Equal("Clean edited", vm.ProjectTones[0].Name);
        Assert.Equal("Clean edited", vm.Editor.EditName);
        Assert.Single(harness.History.GetUndoSteps());
    }

    [Fact]
    public void AnUndoFromOutsideTheDesigner_RefreshesItsLists()
    {
        var (project, harness, vm) = OpenWithClean();
        vm.AddToneCommand.Execute(null);
        Assert.Equal(2, vm.ProjectTones.Count);

        harness.History.Undo();   // the main window's chord, or any other writer
        Assert.Single(project.ToneDefinitions);
        Assert.Single(vm.ProjectTones);
        Assert.Equal("clean", vm.SelectedTone!.Key);

        harness.History.Redo();
        Assert.Equal(2, vm.ProjectTones.Count);
    }

    [Fact]
    public void ASlotEdit_IsRecordedAtOnce_AndUndoRestoresTheRowAndTheArrangement()
    {
        var (project, harness, vm) = OpenWithClean();
        var lead = project.Arrangements[0];
        var row = vm.Arrangements.First(r => ReferenceEquals(r.Arrangement, lead));

        row.ToneC = "solo";
        Assert.Equal("solo", lead.ToneC);   // not only on close

        vm.UndoCommand.Execute(null);
        Assert.Equal("", lead.ToneC);
        Assert.Equal("", vm.Arrangements.First(r => ReferenceEquals(r.Arrangement, lead)).ToneC);

        vm.RedoCommand.Execute(null);
        Assert.Equal("solo", lead.ToneC);
        Assert.Equal("solo", vm.Arrangements.First(r => ReferenceEquals(r.Arrangement, lead)).ToneC);
        Assert.Single(harness.History.GetUndoSteps());
    }

    [Fact]
    public void ATypedKey_IsRecordedWhenTheFieldIsLeft_AsOneStepThatUndoesTheSlotsToo()
    {
        var (project, harness, vm) = OpenWithClean();
        var lead = project.Arrangements[0];

        vm.Editor.EditKey = "clean_v";
        vm.Editor.EditKey = "clean_v2";
        Assert.Equal("clean", project.ToneDefinitions[0].Key);   // still being typed
        vm.EndEditGesture(true);
        Assert.Equal("clean_v2", project.ToneDefinitions[0].Key);
        Assert.Equal("clean_v2", lead.ToneBase);
        Assert.Single(harness.History.GetUndoSteps());

        vm.UndoCommand.Execute(null);
        Assert.Equal("clean", project.ToneDefinitions[0].Key);
        Assert.Equal("clean", lead.ToneBase);
        Assert.Equal("clean", vm.ProjectTones[0].Key);
        Assert.Equal("clean", vm.Arrangements.First(r => ReferenceEquals(r.Arrangement, lead)).ToneBase);

        vm.RedoCommand.Execute(null);
        Assert.Equal("clean_v2", vm.ProjectTones[0].Key);
        Assert.Equal("clean_v2", lead.ToneBase);
    }

    [Fact]
    public void EveryEdit_UndoneAndRedone_ReturnsTheExactProjectState()
    {
        var (project, harness, vm) = OpenWithClean();
        var lead = project.Arrangements[0];
        var start = (project.ToneDefinitions[0].Json, lead.ToneC, project.ToneDefinitions.Count);

        vm.Editor.EditName = "Renamed";
        vm.EndEditGesture();
        vm.AddToneCommand.Execute(null);
        vm.Arrangements.First(r => ReferenceEquals(r.Arrangement, lead)).ToneC = "solo";
        vm.EndEditGesture();
        vm.DeleteToneCommand.Execute(null);
        var steps = harness.History.GetUndoSteps().Count;
        var end = (project.ToneDefinitions.Select(d => d.Json + d.Key).ToList(), lead.ToneC);

        for (var i = 0; i < steps; i++) vm.UndoCommand.Execute(null);
        Assert.Equal(start, (project.ToneDefinitions[0].Json, lead.ToneC, project.ToneDefinitions.Count));
        Assert.Equal(["clean"], vm.ProjectTones.Select(t => t.Key));

        for (var i = 0; i < steps; i++) vm.RedoCommand.Execute(null);
        Assert.Equal(end.Item1.Count, project.ToneDefinitions.Count);
        Assert.Equal(end.Item1.Select(x => x.Length), project.ToneDefinitions.Select(d => (d.Json + d.Key).Length));
        Assert.Equal(end.Item1, project.ToneDefinitions.Select(d => d.Json + d.Key));
        Assert.Equal(end.Item2, lead.ToneC);
        Assert.Equal(project.ToneDefinitions.Select(d => d.Key), vm.ProjectTones.Select(t => t.Key));
    }

    // ---- tone changes on the timeline and in the Song tab ---------------------

    [Fact]
    public void ToneChangeAddRenameAndDelete_AreEachOneUndoableStep()
    {
        var project = CreateProject();
        var lead = project.Arrangements[0];
        var history = new HistoryService(new FakeAppLogger());
        var at = TimelineMath.PositionFromMs(1000, project.Beats);

        void Apply(string tone)
        {
            var actions = new List<IChartAction>();
            EventMarkerEdit.AppendActions(lead, at, 1000, project.Beats, new EventMarkerFields("", "", default, "", false, tone), actions);
            history.Do(new CompositeAction(actions));
        }

        Apply("clean");
        Assert.Equal(["clean"], lead.ToneChanges.Select(t => t.ToneName));
        Apply("dist");
        Assert.Equal(["dist"], lead.ToneChanges.Select(t => t.ToneName));
        Apply("");
        Assert.Empty(lead.ToneChanges);

        history.Undo();
        Assert.Equal(["dist"], lead.ToneChanges.Select(t => t.ToneName));
        history.Undo();
        Assert.Equal(["clean"], lead.ToneChanges.Select(t => t.ToneName));
        history.Undo();
        Assert.Empty(lead.ToneChanges);
        history.Redo();
        history.Redo();
        history.Redo();
        Assert.Empty(lead.ToneChanges);
    }
}
