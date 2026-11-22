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
}
