using DotCharter.UI.ViewModels.Editor.Inspector;

namespace DotCharter.Tests.UI.ViewModels.Editor;

public sealed class GripEditTests
{
    [Fact]
    public void OnlyAnExistingChordNoteCanBeRefretted()
    {
        var chord = new Chord
        {
            ChordNotes = [new ChordNote { StringIndex = 1, Fret = 7, Mute = MuteType.Palm, SlideTo = 9 }]
        };

        Assert.Null(GripEdit.Refret(chord, new Dictionary<int, int> { [2] = 5 }));
        var result = Assert.IsType<Chord>(GripEdit.Refret(chord, new Dictionary<int, int> { [1] = 8, [2] = 5 }));
        var note = Assert.Single(result.ChordNotes);
        Assert.Equal(1, note.StringIndex);
        Assert.Equal(8, note.Fret);
        Assert.Equal(MuteType.Palm, note.Mute);
        Assert.Equal(9, note.SlideTo);
        Assert.Equal(7, Assert.Single(chord.ChordNotes).Fret);
    }

    [Fact]
    public void OpeningGhostsIncludeLaterAndNeverStruckStrings()
    {
        var shape = new HandShape { EndPosition = new FractionalPosition(3) };
        var level = new ChartLevel
        {
            Notes =
            [
                new GuitarNote { StringIndex = 1, Fret = 0 },
                new GuitarNote { Position = new FractionalPosition(1), StringIndex = 2, Fret = 5 }
            ]
        };
        var grip = new ChordTemplate { Frets = new() { [1] = 0, [2] = 5, [3] = 5 } };

        var infer = DotCharter.Services.Editing.ChordShapes.GhostGems.IndexOpeningGhostMasks(level);
        Assert.Equal(12, GripEdit.Ghosts(shape, grip, infer));
        shape.GhostMask = 0;
        Assert.Equal(0, GripEdit.Ghosts(shape, grip, infer));
        Assert.Equal(5, grip.GetFret(2));
    }

    [Fact]
    public void SelectedAndEveryUseResolveIndependentlyOfTheDraft()
    {
        var first = new HandShape { TemplateId = 0, EndPosition = new(1) };
        var selected = new HandShape { TemplateId = 1, Position = new(2), EndPosition = new(3) };
        var repeat = new HandShape { TemplateId = 0 };
        var sameGrip = new HandShape { TemplateId = 2 };
        var level = new ChartLevel { HandShapes = [first, selected] };
        var otherLevel = new ChartLevel { HandShapes = [repeat, sameGrip] };
        var arrangement = new Arrangement
        {
            Levels = [level, otherLevel],
            ChordTemplates =
            [
                new() { Id = 0, Frets = { [1] = 5 } },
                new() { Id = 1, Frets = { [1] = 7 } },
                new() { Id = 2, Frets = { [1] = 5 } }
            ]
        };
        HandShape[] Resolve(DotCharter.UI.ViewModels.Panels.ChordApplyRung scope) =>
            GripInspectorTargets.Resolve(arrangement, level, first, null, [first, selected], [], scope)
                .Shapes.Select(t => t.Shape).ToArray();

        Assert.Equal([first], Resolve(DotCharter.UI.ViewModels.Panels.ChordApplyRung.ThisChord));
        Assert.Equal([first, selected], Resolve(DotCharter.UI.ViewModels.Panels.ChordApplyRung.Selected));
        Assert.Equal([first, repeat], Resolve(DotCharter.UI.ViewModels.Panels.ChordApplyRung.EveryUse));
        Assert.Equal([first, selected, repeat, sameGrip], Resolve(DotCharter.UI.ViewModels.Panels.ChordApplyRung.SameGrip));
    }

    [Fact]
    public void AFieldEditKeepsOtherMixedValuesPerShape()
    {
        var shape = new HandShape();
        var draft = new GripInspectorDraft { Arpeggio = true };
        var first = new ChordTemplate { Name = "A", Frets = { [1] = 7 }, Fingers = { [1] = 3 } };
        var second = new ChordTemplate { Name = "B", Frets = { [1] = 9 }, Fingers = { [1] = 4 }, NopInRs = true };
        var a = draft.Project(shape, first, (_, _) => 0);
        var b = draft.Project(shape, second, (_, _) => 0);
        Assert.True(a.IsArpeggio && b.IsArpeggio);
        Assert.Equal(("A", 7, 3, false), (a.Name, a.GetFret(1), a.GetFinger(1), a.NopInRs));
        Assert.Equal(("B", 9, 4, true), (b.Name, b.GetFret(1), b.GetFinger(1), b.NopInRs));
    }

    [Fact]
    public void MissingIsPerFrettedStringAndAutoFillHonorsPin()
    {
        var grip = new ChordTemplate { Name = "E5", Frets = { [0] = 0, [1] = 2, [2] = 2 }, Fingers = { [1] = 2 } };
        Assert.True(GripEdit.MissingFingering(grip, 0));
        grip.Pinned = true;
        Assert.Null(GripEdit.AutoFill(new Arrangement(), grip, 0));
        grip.Pinned = false;
        Assert.Null(GripEdit.AutoFill(new Arrangement(), grip, ChordTemplate.GhostBit(2)));
        grip.Fingers[2] = 3;
        Assert.False(GripEdit.MissingFingering(grip, 0));
        grip.Fingers.Clear();
        grip.Fingerless = true;
        Assert.False(GripEdit.MissingFingering(grip, 0));
    }
}
