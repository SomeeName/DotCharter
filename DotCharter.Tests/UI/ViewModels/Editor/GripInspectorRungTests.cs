using DotCharter.UI.ViewModels.Editor;
using DotCharter.UI.ViewModels.Panels;

namespace DotCharter.Tests.UI.ViewModels.Editor;

/// <summary>
///     #751 (inspector design D, phase 6): what a Save on the Shape page reaches on each Apply-to rung. Four bars,
///     each with its OWN ghost mask: two selected bars and an unselected one on one grip, and a bar on the same grip
///     moved two frets up the neck, a TWIN grip (the same frets under another name) under a fifth bar, and chords
///     on the grip and on the twin. A fret, finger, ghost or edit-mask-limited edit on string 3 reaches This chord =
///     the first bar, Selected = both selected bars, Every use = every bar on the grip, Same grip = that plus the
///     twin's bar and the moved grip's fingering (so a finger edit tells Same grip from Every use by the bars it
///     reaches). Every reached bar keeps its own mask, every other bar is untouched, a grip edit moves no chord and
///     adds no note, and ONE undo puts everything back. The Note column's fret moves the chords the rung reaches.
/// </summary>
public sealed class GripInspectorRungTests
{
    private static readonly int Two = ChordTemplate.GhostBit(2);
    private static readonly int Four = ChordTemplate.GhostBit(4);
    private static readonly int Three = ChordTemplate.GhostBit(3);

    public enum Edit
    {
        Fret,
        Finger,
        Ghost,
        MaskedFret
    }

    public static TheoryData<int, Edit> Cases()
    {
        var data = new TheoryData<int, Edit>();
        for (var rung = 0; rung < 4; rung++)
        {
            foreach (var edit in Enum.GetValues<Edit>())
                data.Add(rung, edit);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void ASave_ReachesTheRung_EachBarKeepsItsOwnMask_AndOneUndoTakesItBack(int scope, Edit edit)
    {
        var rig = new GripInspectorRig();
        var grip = rig.Grip(new() { [2] = 5, [3] = 5, [4] = 7 });
        var moved = rig.Grip(new() { [2] = 7, [3] = 7, [4] = 9 });
        var first = rig.Shape(grip, 0, 2, Two);
        var second = rig.Shape(grip, 4, 6, Four);
        var far = rig.Shape(grip, 8, 10, 0);
        var other = rig.Shape(moved, 12, 14, Two | Four);
        var twin = rig.Grip(new() { [2] = 5, [3] = 5, [4] = 7 });
        twin.Name = "h";
        var twinBar = rig.Shape(twin, 16, 18, Two);
        var chords = new[] { rig.Strum(new(1), grip), rig.Strum(new(9), grip), rig.Strum(new(17), twin) };
        var notes = Notes(chords);
        rig.Select(SoundSelectionKind.HandShape, [first, second], []);
        rig.Vm.ScopeIndex = scope;
        var before = Snapshot(rig);

        Apply(rig, edit);
        Assert.True(rig.Vm.HasDraft, $"{edit} drafted nothing");
        var node = rig.History.CurrentNodeId;
        rig.Vm.SaveFingeringCommand.Execute(null);
        Assert.NotEqual(node, rig.History.CurrentNodeId);

        var rung = (ChordApplyRung[])[ChordApplyRung.ThisChord, ChordApplyRung.Selected, ChordApplyRung.SameGrip, ChordApplyRung.EveryUse];
        var reached = rung[scope] switch
        {
            ChordApplyRung.ThisChord => new[] { first },
            ChordApplyRung.Selected => [first, second],
            _ => [first, second, far]
        };
        foreach (var (bar, mask) in new[] { (first, Two), (second, Four), (far, 0) })
        {
            var now = rig.Arrangement.FindChordTemplate(bar.TemplateId)!;
            if (!reached.Contains(bar))
            {
                Assert.Equal(grip.Id, bar.TemplateId);
                Assert.Equal(mask, bar.GhostMask);
                continue;
            }

            var expectedMask = edit == Edit.Ghost ? mask | Three : mask;
            Assert.Equal(expectedMask, bar.GetGhostMask(now));
            AssertEdited(now, edit);
        }

        var otherGrip = rig.Arrangement.FindChordTemplate(other.TemplateId)!;
        Assert.Equal(Two | Four, other.GetGhostMask(otherGrip));
        Assert.Equal(rung[scope] == ChordApplyRung.SameGrip && edit == Edit.Finger ? 2 : -1, otherGrip.GetFinger(3));
        Assert.Equal(7, otherGrip.GetFret(3));
        var twinNow = rig.Arrangement.FindChordTemplate(twinBar.TemplateId)!;
        Assert.Equal(rung[scope] == ChordApplyRung.SameGrip && edit == Edit.Finger ? 2 : -1, twinNow.GetFinger(3));
        Assert.Equal(5, twinNow.GetFret(3));
        Assert.Equal(Two, twinBar.GhostMask);
        // A grip edit writes hand shapes only: no chord moves, none gains a note.
        Assert.Equal(notes, Notes(chords));

        rig.History.Undo();
        Assert.Equal(node, rig.History.CurrentNodeId);
        Assert.Equal(before, Snapshot(rig));
    }

    /// <summary>
    ///     The Shape page's Note column moves chords: on Every use (and Same grip) every chord on the grip, on every
    ///     level, through the page's own Save; on Selected only the selected chord. No chord gains a note, and the
    ///     twin grip's chord (another template) is never reached.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void TheNoteColumn_MovesTheChordsTheRungReaches_WithoutAddingANote(int scope)
    {
        var rig = new GripInspectorRig();
        var grip = rig.Grip(new() { [2] = 5, [3] = 5, [4] = 7 });
        var twin = rig.Grip(new() { [2] = 5, [3] = 5, [4] = 7 });
        twin.Name = "h";
        var bar = rig.Shape(grip, 0, 2, Two);
        var near = rig.Strum(new(1), grip);
        var far = rig.Strum(new(9), grip);
        var other = rig.Strum(new(17), twin);
        rig.Select(SoundSelectionKind.ShapeAndSounds, [bar], [near]);
        rig.Vm.ScopeIndex = scope;
        var strings = Strings([near, far, other]);

        rig.Row(3).ChordFretText = "6";
        rig.Vm.SaveFingeringCommand.Execute(null);

        var everyUse = scope >= 2;
        Assert.Equal(6, Fret(near, 3));
        Assert.Equal(everyUse ? 6 : 5, Fret(far, 3));
        Assert.Equal(5, Fret(other, 3));
        Assert.Equal(twin.Id, other.TemplateId);
        Assert.Equal(strings, Strings([near, far, other]));
        Assert.True(rig.Arrangement.FindChordTemplate(far.TemplateId)!.DescribesChordNotes(far.ChordNotes));
    }

    private static int Fret(Chord chord, int stringIndex) => chord.ChordNotes.Single(n => n.StringIndex == stringIndex).Fret;

    private static string Strings(IEnumerable<Chord> chords) =>
        string.Join(";", chords.Select(c => string.Join(',', c.ChordNotes.Select(n => n.StringIndex))));

    private static string Notes(IEnumerable<Chord> chords) =>
        string.Join(";", chords.Select(c => string.Join(',', c.ChordNotes.Select(n => $"{n.StringIndex}={n.Fret}"))));

    private static void Apply(GripInspectorRig rig, Edit edit)
    {
        switch (edit)
        {
            case Edit.Fret:
                rig.Row(3).GripFretText = "6";
                break;
            case Edit.Finger:
                rig.Vm.SetFinger(3, 2);
                break;
            case Edit.Ghost:
                rig.Row(3).IsGhost = true;
                break;
            case Edit.MaskedFret:
                // The edit mask holds string 3 only: string 4's typed fret is refused, string 3's lands.
                rig.State.ToggleStringEditMask(3);
                rig.Row(4).GripFretText = "9";
                rig.Row(3).GripFretText = "6";
                break;
        }
    }

    private static void AssertEdited(ChordTemplate now, Edit edit)
    {
        Assert.Equal(edit is Edit.Fret or Edit.MaskedFret ? 6 : 5, now.GetFret(3));
        Assert.Equal(7, now.GetFret(4));
        Assert.Equal(5, now.GetFret(2));
        if (edit == Edit.Finger) Assert.Equal(2, now.GetFinger(3));
    }

    // Every bar's template and mask, and every template's frets, fingers and ghosts.
    private static string Snapshot(GripInspectorRig rig) => string.Join(";",
        rig.Level.HandShapes.Select(h => $"{h.TemplateId}/{h.GhostMask}").Concat(rig.Arrangement.ChordTemplates.Select(t =>
            $"{t.Id}:{string.Join(',', t.Frets.OrderBy(f => f.Key))}|{string.Join(',', t.Fingers.OrderBy(f => f.Key))}|{t.GhostMask}")));
}
