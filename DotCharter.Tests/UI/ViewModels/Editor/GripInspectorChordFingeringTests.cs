using DotCharter.Services.Editing.Linking;
using DotCharter.UI.ViewModels.Editor;

namespace DotCharter.Tests.UI.ViewModels.Editor;

/// <summary>
///     #890: the Note tab's Finger column and inspector rule E04 must read the same fingering. A finger edit
///     re-pointed only the HAND SHAPE to a template with the new fingering; the chord under it kept its old
///     template, so the Note tab showed 2/2 while E04 judged the chord's 1/1 ("index finger on fret 6" under
///     FHP 5). A saved finger now reaches the chords that strike the shape's grip, and a chord whose own
///     template still disagrees with its shape shows as mixed instead of hiding behind the shape's fingers.
/// </summary>
public sealed class GripInspectorChordFingeringTests
{
    /// <summary>A 6/6 power chord fingered 1/1 under its own hand shape and an FHP on fret 5.</summary>
    private static (GripInspectorRig Rig, HandShape Shape, Chord Chord) PowerChord()
    {
        var rig = new GripInspectorRig();
        var grip = rig.Grip(new() { [0] = 6, [1] = 6 });
        grip.Fingers = new() { [0] = 1, [1] = 1 };
        var shape = rig.Shape(grip, 0, 4);
        var chord = rig.Strum(new(1), grip);
        rig.Level.Anchors.Add(new FretAnchor(new(0), 5));
        return (rig, shape, chord);
    }

    private static int IndexFindings(GripInspectorRig rig) =>
        new ArrangementValidationService(new FakeAppLogger()).Validate(rig.Arrangement, rig.Project.Beats)
            .Count(i => i.Type == ArrangementIssueType.IndexFingerNotOnAnchorFret);

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    public void SavingFingersTwo_ReachesTheChord_SoE04AgreesWithTheNoteTab(int scope)
    {
        var (rig, shape, chord) = PowerChord();
        Assert.Equal(1, IndexFindings(rig));
        rig.Select(SoundSelectionKind.ShapeAndSounds, [shape], [chord]);
        rig.Vm.ScopeIndex = scope;

        rig.Vm.SetFinger(0, 2);
        rig.Vm.SetFinger(1, 2);
        rig.Vm.SaveFingeringCommand.Execute(null);

        Assert.Equal(shape.TemplateId, chord.TemplateId);
        var played = rig.Arrangement.FindChordTemplate(chord.TemplateId)!;
        Assert.Equal((2, 2), (played.GetFinger(0), played.GetFinger(1)));
        Assert.Equal(0, IndexFindings(rig));
        Assert.Equal(2, rig.Row(0).Finger);
    }

    [Fact]
    public void AChordWhoseTemplateDisagreesWithItsShape_ShowsMixedFingers()
    {
        var (rig, shape, chord) = PowerChord();
        var shapeGrip = rig.Grip(new() { [0] = 6, [1] = 6 });
        shapeGrip.Fingers = new() { [0] = 2, [1] = 2 };
        shape.TemplateId = shapeGrip.Id;

        rig.Select(SoundSelectionKind.ShapeAndSounds, [shape], [chord]);

        Assert.Equal(1, IndexFindings(rig));
        Assert.True(rig.Row(0).HasMixedFinger);
        Assert.Null(rig.Row(0).Finger);
    }

    /// <summary>A 6/6 hand shape over three identical 6/6 chords and one 8/8 chord.</summary>
    private static (GripInspectorRig Rig, ChordTemplate Grip, HandShape Shape, Chord[] Same, Chord Other) ShapeOverThree(int start = 0)
    {
        var rig = new GripInspectorRig();
        var grip = rig.Grip(new() { [0] = 6, [1] = 6 });
        grip.Fingers = new() { [0] = 1, [1] = 1 };
        var other = rig.Grip(new() { [0] = 8, [1] = 8 });
        other.Fingers = new() { [0] = 3, [1] = 3 };
        var shape = rig.Shape(grip, start, start + 4);
        Chord[] same = [rig.Strum(new(start), grip), rig.Strum(new(start + 1), grip), rig.Strum(new(start + 2), grip)];
        return (rig, grip, shape, same, rig.Strum(new(start + 3), other));
    }

    [Theory]
    [InlineData(SoundSelectionKind.ShapeAndSounds)]
    [InlineData(SoundSelectionKind.HandShape)]
    public void AFingerSave_GivesEveryChordPlayingTheShapesGrip_TheShapesNewTemplate_AsOneStep(SoundSelectionKind kind)
    {
        var (rig, grip, shape, same, other) = ShapeOverThree();
        var otherId = other.TemplateId;
        rig.Select(kind, [shape], kind == SoundSelectionKind.HandShape ? [] : [same[0]]);
        rig.Vm.ScopeIndex = 0;

        rig.Vm.SetFinger(0, 2);
        rig.Vm.SetFinger(1, 2);
        rig.Vm.SaveFingeringCommand.Execute(null);

        Assert.NotEqual(grip.Id, shape.TemplateId);
        Assert.All(same, c => Assert.Equal(shape.TemplateId, c.TemplateId));
        var played = rig.Arrangement.FindChordTemplate(shape.TemplateId)!;
        Assert.Equal((2, 2), (played.GetFinger(0), played.GetFinger(1)));
        Assert.Equal(otherId, other.TemplateId);
        Assert.Equal(2, rig.Row(0).Finger);
        Assert.False(rig.Row(0).HasMixedFinger);

        rig.History.Undo();
        Assert.Equal(grip.Id, shape.TemplateId);
        Assert.All(same, c => Assert.Equal(grip.Id, c.TemplateId));
        Assert.Equal(otherId, other.TemplateId);
    }

    [Fact]
    public void AFingerSave_InALinkedMeasure_LeavesEveryPlacementsShapeAndChordsOnOneGrip()
    {
        var (rig, grip, shape, same, other) = ShapeOverThree();
        rig.Shape(grip, 8, 12);
        foreach (var beat in new[] { 8, 9, 10 }) rig.Strum(new(beat), grip);
        rig.Strum(new(11), rig.Arrangement.FindChordTemplate(other.TemplateId)!);
        Assert.Equal(LinkCreateRefusal.None, LinkGroupCreator.TryBuild(rig.Project,
            [new(rig.Arrangement, new(0), new(4)), new(rig.Arrangement, new(8), new(12))], "Riff",
            LinkSuggestionFinder.RuleFor(new AppConfig()), out var link));
        Assert.True(rig.History.Do(link!));
        var templateCount = rig.Arrangement.ChordTemplates.Count;
        rig.Select(SoundSelectionKind.ShapeAndSounds, [shape], [same[0]]);
        rig.Vm.ScopeIndex = 0;

        rig.Vm.SetFinger(0, 2);
        rig.Vm.SetFinger(1, 2);
        rig.Vm.SaveFingeringCommand.Execute(null);

        var shapes = rig.Level.HandShapes;
        Assert.Equal(2, shapes.Count);
        var id = shapes[0].TemplateId;
        Assert.NotEqual(grip.Id, id);
        Assert.All(shapes, s => Assert.Equal(id, s.TemplateId));
        var chords = rig.Level.Chords.Where(c => c.ChordNotes.All(n => n.Fret == 6)).ToArray();
        Assert.Equal(6, chords.Length);
        Assert.All(chords, c => Assert.Equal(id, c.TemplateId));
        Assert.All(rig.Level.Chords.Where(c => c.ChordNotes.All(n => n.Fret == 8)),
            c => Assert.Equal(other.TemplateId, c.TemplateId));
        Assert.Single(rig.Project.LinkGroups);

        // One undo takes the save back everywhere: the source bar AND every linked placement.
        rig.History.Undo();
        Assert.Equal(templateCount, rig.Arrangement.ChordTemplates.Count);
        Assert.Equal((1, 1), (grip.GetFinger(0), grip.GetFinger(1)));
        Assert.Equal(2, rig.Level.HandShapes.Count);
        Assert.All(rig.Level.HandShapes, s => Assert.Equal(grip.Id, s.TemplateId));
        Assert.Equal(8, rig.Level.Chords.Count);
        Assert.All(rig.Level.Chords.Where(c => c.ChordNotes.All(n => n.Fret == 6)), c => Assert.Equal(grip.Id, c.TemplateId));
        Assert.All(rig.Level.Chords.Where(c => c.ChordNotes.All(n => n.Fret == 8)),
            c => Assert.Equal(other.TemplateId, c.TemplateId));
        Assert.Single(rig.Project.LinkGroups);
    }
}
