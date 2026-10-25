using DotCharter.Services.Editing.Fingering;
using DotCharter.UI.Input;
using DotCharter.UI.Input.Actions;

namespace DotCharter.Tests.UI.Input;

/// <summary>
///     A wheel resize rebuilds the grip of a bar that owns its template alone from the sounds the bar
///     now covers. When those sounds are exactly one chord's notes, the bar belongs on THAT chord's
///     template (sync on): rewriting a private copy of the same grip leaves the bar and its chord on two
///     templates, and the fix for that was a manual repoint. With the sync off the rewrite stands.
/// </summary>
public sealed class HandShapeResizeSyncTests
{
    [Fact]
    public void ABarGrownOverAChord_TakesTheChordsTemplate_InsteadOfRewritingItsOwn()
    {
        var h = new Harness();
        var chordGrip = h.AddTemplate(0, (0, 3), (1, 5));
        var own = h.AddTemplate(1, (0, 3));
        h.AddNote(0, 0, 3);
        h.AddChord(6, 0, (0, 3), (1, 5));
        var bar = h.AddHandShape(0, 4, 1);

        var next = h.Resize(bar, 8, sync: true, out var grips, out var minted);

        Assert.Equal(0, next.TemplateId);
        Assert.Empty(grips);
        Assert.Empty(minted);
        Assert.Single(own.Frets);
        Assert.Equal(2, chordGrip.Frets.Count);
    }

    [Fact]
    public void ABarGrownOverAChord_RewritesItsOwnGripWhenTheSyncIsOff()
    {
        var h = new Harness();
        h.AddTemplate(0, (0, 3), (1, 5));
        h.AddTemplate(1, (0, 3));
        h.AddNote(0, 0, 3);
        h.AddChord(6, 0, (0, 3), (1, 5));
        var bar = h.AddHandShape(0, 4, 1);

        var next = h.Resize(bar, 8, sync: false, out var grips, out _);

        Assert.Equal(1, next.TemplateId);
        var rewrite = Assert.Single(grips);
        Assert.Equal(2, rewrite.After.Frets.Count);
    }

    [Fact]
    public void ABarWithoutATemplate_TakesTheChordsTemplate_InsteadOfMintingOne()
    {
        var h = new Harness();
        h.AddTemplate(0, (0, 3), (1, 5));
        h.AddNote(0, 0, 3);
        h.AddChord(6, 0, (0, 3), (1, 5));
        var bar = h.AddHandShape(0, 4, -1);

        var next = h.Resize(bar, 8, sync: true, out var grips, out var minted);

        Assert.Equal(0, next.TemplateId);
        Assert.Empty(grips);
        Assert.Empty(minted);
    }

    [Fact]
    public void ABarGrownOverMoreThanTheChord_KeepsRewritingItsOwnGrip()
    {
        var h = new Harness();
        h.AddTemplate(0, (0, 3), (1, 5));
        h.AddTemplate(1, (0, 3));
        h.AddNote(0, 0, 3);
        h.AddNote(2, 2, 7);
        h.AddChord(6, 0, (0, 3), (1, 5));
        var bar = h.AddHandShape(0, 4, 1);

        var next = h.Resize(bar, 8, sync: true, out var grips, out _);

        Assert.Equal(1, next.TemplateId);
        Assert.Equal(3, Assert.Single(grips).After.Frets.Count);
    }

    [Fact]
    public void TheRepoint_IsOneUndoStepThatPutsTheBarsTemplateBack()
    {
        var h = new Harness();
        h.AddTemplate(0, (0, 3), (1, 5));
        h.AddTemplate(1, (0, 3));
        h.AddNote(0, 0, 3);
        h.AddChord(6, 0, (0, 3), (1, 5));
        var bar = h.AddHandShape(0, 4, 1);
        var old = bar.Clone();

        var next = h.Resize(bar, 8, sync: true, out var grips, out var minted);
        var action = new ResizeHandShapesAction(h.Level.HandShapes, [bar], [old], [next],
            h.Arrangement.ChordTemplates, grips, minted, null);

        action.Execute();
        Assert.Equal(0, bar.TemplateId);
        Assert.Equal(new FractionalPosition(8), bar.EndPosition);

        action.Undo();
        Assert.Equal(1, bar.TemplateId);
        Assert.Equal(new FractionalPosition(4), bar.EndPosition);
    }

    private sealed class Harness
    {
        public Harness()
        {
            Arrangement = new Arrangement { Name = "Lead", ArrangementType = ArrangementType.Lead };
            Arrangement.Levels.Add(new ChartLevel());
        }

        public Arrangement Arrangement { get; }
        public ChartLevel Level => Arrangement.Levels[0];

        public ChordTemplate AddTemplate(int id, params (int stringIdx, int fret)[] frets)
        {
            var t = new ChordTemplate { Id = id };
            foreach (var (stringIdx, fret) in frets) t.Frets[stringIdx] = fret;
            Arrangement.ChordTemplates.Add(t);
            return t;
        }

        public HandShape AddHandShape(int start, int end, int templateId)
        {
            var shape = new HandShape
            {
                Position = new FractionalPosition(start),
                EndPosition = new FractionalPosition(end),
                TemplateId = templateId
            };
            Level.HandShapes.Add(shape);
            return shape;
        }

        public void AddNote(int beat, int stringIndex, int fret) =>
            Level.Notes.Add(new GuitarNote { Position = new FractionalPosition(beat), StringIndex = stringIndex, Fret = fret });

        public void AddChord(int beat, int templateId, params (int stringIndex, int fret)[] frets)
        {
            var chord = new Chord { Position = new FractionalPosition(beat), TemplateId = templateId };
            foreach (var (stringIndex, fret) in frets)
                chord.ChordNotes.Add(new ChordNote { StringIndex = stringIndex, Fret = fret });
            Level.Chords.Add(chord);
        }

        /// <summary>One wheel run's plan for <paramref name="bar" /> grown to end at beat <paramref name="end" />.</summary>
        public HandShape Resize(HandShape bar, int end, bool sync,
            out List<ResizeHandShapesAction.GripRewrite> grips, out List<ChordTemplate> minted)
        {
            var next = bar.Clone();
            next.EndPosition = new FractionalPosition(end);
            grips = [];
            minted = [];
            TimelineWheelAdjuster.RebuildGrips(new FingeringGenerationService(), Arrangement, sync,
                Level.Notes, Level.Chords, Level.HandShapes, [bar], [next], grips, minted);
            return next;
        }
    }
}
