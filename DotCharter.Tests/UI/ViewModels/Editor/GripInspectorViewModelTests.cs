using DotCharter.UI.ViewModels.Editor;
using DotCharter.UI.ViewModels.Panels;

namespace DotCharter.Tests.UI.ViewModels.Editor;

/// <summary>
///     The Shape page's grip editor (#751) over a real chart editor and undo history: a grip edit never adds a
///     note (the owner's trap), a ghosted edit on Every use reaches every bar (bug b), the page's scope IS the
///     footer's Apply-to rung, Undo save takes back exactly the save, the keyboard walks rows and fingers, and
///     the double-stop page keeps the chosen pair.
/// </summary>
public sealed class GripInspectorViewModelTests
{
    private static readonly int Ghost = ChordTemplate.GhostBit(2);

    [Fact]
    public void TypingGripFrets_WithAChordAndItsHandShapeSelected_NeverAddsNotes()
    {
        var rig = new GripInspectorRig();
        var grip = rig.Grip(new() { [3] = 7 });
        var shape = rig.Shape(grip, 0, 4);
        var chord = new Chord { Position = new(1), TemplateId = grip.Id };
        chord.ChordNotes.Add(new ChordNote { StringIndex = 3, Fret = 7 });
        rig.Level.Chords.Add(chord);
        rig.Select(SoundSelectionKind.ShapeAndSounds, [shape], [chord]);

        rig.Row(1).GripFretText = "5";
        rig.Row(2).GripFretText = "5";
        rig.Vm.SaveFingeringCommand.Execute(null);

        var note = Assert.Single(chord.ChordNotes);
        Assert.Equal((3, 7), (note.StringIndex, note.Fret));
        var saved = rig.Arrangement.FindChordTemplate(shape.TemplateId)!;
        Assert.Equal(5, saved.GetFret(1));
        Assert.Equal(5, saved.GetFret(2));
        Assert.Equal(7, saved.GetFret(3));
    }

    [Fact]
    public void AGhostedFingerEdit_OnEveryUse_ReachesEveryBar_AndEachKeepsItsMask()
    {
        var rig = new GripInspectorRig();
        var grip = rig.Grip(new() { [2] = 5, [3] = 5, [4] = 7 });
        var first = rig.Shape(grip, 0, 2, Ghost);
        var second = rig.Shape(grip, 4, 6, Ghost);
        rig.Select(SoundSelectionKind.HandShape, [first], []);
        rig.Vm.ScopeIndex = 3;

        rig.Vm.SetFinger(3, 2);
        rig.Vm.SaveFingeringCommand.Execute(null);

        Assert.Equal(first.TemplateId, second.TemplateId);
        Assert.Equal(2, rig.Arrangement.FindChordTemplate(first.TemplateId)!.GetFinger(3));
        Assert.Equal(Ghost, first.GetGhostMask(rig.Arrangement.FindChordTemplate(first.TemplateId)!));
        Assert.Equal(Ghost, second.GetGhostMask(rig.Arrangement.FindChordTemplate(second.TemplateId)!));
    }

    [Fact]
    public void ThePagesScope_IsTheFootersApplyToRung()
    {
        var rig = new GripInspectorRig();
        rig.Vm.ScopeIndex = 3;
        Assert.Equal(ChordApplyRung.EveryUse, rig.Ladder.ApplyRung);

        rig.Ladder.ApplyRung = ChordApplyRung.ThisChord;
        Assert.Equal(0, rig.Vm.ScopeIndex);
    }

    [Fact]
    public void UndoSave_TakesBackExactlyTheSave()
    {
        var rig = new GripInspectorRig();
        var grip = rig.Grip(new() { [3] = 7 });
        var shape = rig.Shape(grip, 0, 4);
        rig.Select(SoundSelectionKind.HandShape, [shape], []);
        Assert.False(rig.Vm.CanUndoSave);

        rig.Row(1).GripFretText = "5";
        rig.Vm.SaveFingeringCommand.Execute(null);
        Assert.True(rig.Vm.CanUndoSave);
        Assert.Equal(5, rig.Arrangement.FindChordTemplate(shape.TemplateId)!.GetFret(1));

        rig.Vm.UndoSaveCommand.Execute(null);
        Assert.Equal(grip.Id, shape.TemplateId);
        Assert.Equal(-1, grip.GetFret(1));
        Assert.False(rig.Vm.CanUndoSave);
    }

    [Fact]
    public void TheKeyboard_WalksRows_AndStepsTheFingerOfTheKeyRow()
    {
        var rig = new GripInspectorRig();
        var grip = rig.Grip(new() { [0] = 3, [1] = 5 });
        var shape = rig.Shape(grip, 0, 4);
        rig.Select(SoundSelectionKind.HandShape, [shape], []);
        var target = rig.Vm.Rows.ToList().FindIndex(row => row.StringIndex == 1);

        rig.Vm.MoveRow(target);
        Assert.True(rig.Vm.Rows[target].IsKeyRow);
        rig.Vm.StepFinger(1);
        rig.Vm.StepFinger(1);
        Assert.Equal(1, rig.Vm.Rows[target].Finger);
        rig.Vm.StepFinger(-1);
        Assert.Equal(0, rig.Vm.Rows[target].Finger);

        rig.Vm.MoveRow(-99);
        Assert.Equal(0, rig.Vm.FocusedRow);
        Assert.True(rig.Vm.Rows[0].IsKeyRow);
        Assert.False(rig.Vm.Rows[target].IsKeyRow);
    }

    [Fact]
    public void UseDoubleStopPair_KeepsTheTwoStrings_AndMarksTheGrip()
    {
        var rig = new GripInspectorRig();
        var grip = rig.Grip(new() { [1] = 7, [2] = 7, [3] = 6 });
        var shape = rig.Shape(grip, 0, 4);
        rig.Select(SoundSelectionKind.HandShape, [shape], []);
        Assert.Equal(3, rig.Vm.PairOptions.Count);

        rig.Vm.PairFirst = 1;
        rig.Vm.PairSecond = 2;
        rig.Vm.UseDoubleStopPairCommand.Execute(null);
        rig.Vm.SaveFingeringCommand.Execute(null);

        var saved = rig.Arrangement.FindChordTemplate(shape.TemplateId)!;
        Assert.True(saved.NopInRs);
        Assert.Equal(2, saved.Frets.Count);
    }
}
