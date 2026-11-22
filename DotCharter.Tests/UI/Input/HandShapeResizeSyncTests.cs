using DotCharter.Services.Editing.ChordShapes;
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
    public void ABarWithItsOwnGhostMaskKeepsItsGripOnResize()
    {
        var h = new Harness();
        var grip = h.AddTemplate(0, (0, 3), (1, 5));
        grip.Fingers[1] = 3;
        h.AddNote(0, 0, 3);
        var bar = h.AddHandShape(0, 2, grip.Id);
        bar.GhostMask = 1 << 1;

        var next = h.Resize(bar, 4, sync: false, out var grips, out var minted);

        Assert.False(grip.IsPinned);
        Assert.Equal(grip.Id, next.TemplateId);
        Assert.Equal(1 << 1, next.GetGhostMask(grip));
        Assert.Equal(3, grip.GetFinger(1));
        Assert.Empty(grips);
        Assert.Empty(minted);
    }

    [Theory]
    [InlineData("chord")]
    [InlineData("hand shape")]
    [InlineData("notation voice")]
    public void ATemplateUsedOutsideTheActiveLevel_IsNeverRewrittenInPlace(string otherUser)
    {
        var h = new Harness();
        var shared = h.AddTemplate(7, (0, 3));
        shared.Fingers[0] = 2;
        h.AddNote(0, 0, 3);
        h.AddNote(6, 1, 5);
        var bar = h.AddHandShape(0, 4, shared.Id);
        var otherLevel = new ChartLevel();
        h.Arrangement.Levels.Add(otherLevel);
        if (otherUser == "hand shape")
            otherLevel.HandShapes.Add(new HandShape { TemplateId = shared.Id });
        else if (otherUser == "chord")
            otherLevel.Chords.Add(new Chord { TemplateId = shared.Id });
        else
            h.Arrangement.NotationVoices.Add(new NotationVoice { Chords = { new Chord { TemplateId = shared.Id } } });

        var next = h.Resize(bar, 8, sync: false, out var grips, out var minted);
        var action = new ResizeHandShapesAction(h.Level.HandShapes, [bar], [bar.Clone()], [next],
            h.Arrangement.ChordTemplates, grips, minted, null);
        action.Execute();

        // The grown grip is a template of its own; the shared one is untouched.
        Assert.Empty(grips);
        var grown = Assert.Single(minted);
        Assert.Equal(grown.Id, bar.TemplateId);
        Assert.Equal(5, grown.Frets[1]);
        Assert.Equal(new FractionalPosition(8), bar.EndPosition);
        Assert.Single(shared.Frets);
        Assert.Equal(2, shared.Fingers[0]);
        action.Undo();
        Assert.Equal(new FractionalPosition(4), bar.EndPosition);
        Assert.Equal(shared.Id, bar.TemplateId);
        Assert.DoesNotContain(grown, h.Arrangement.ChordTemplates);
    }

    /// <summary>
    ///     An imported arpeggio bar shares its grip with every other bar of that shape, so a
    ///     resize over the arpeggio's fourth note changed the length and left the grip without the note.
    ///     The grown grip resolves to (or mints) a template of its own, in the same undo step.
    /// </summary>
    [Fact]
    public void ASharedGrip_GrownOverAFittingNote_GetsAGripWithThatNote_InOneUndoStep()
    {
        var h = new Harness();
        var shared = h.AddTemplate(0, (5, 1), (4, 3), (2, 3));
        h.AddNote(0, 5, 1);
        h.AddNote(1, 4, 3);
        h.AddNote(2, 2, 3);
        h.AddNote(3, 3, 3);
        var bar = h.AddHandShape(0, 3, shared.Id);
        h.AddHandShape(8, 11, shared.Id);
        var old = bar.Clone();

        var next = h.Resize(bar, 4, sync: true, out var grips, out var minted);
        var action = new ResizeHandShapesAction(h.Level.HandShapes, [bar], [old], [next],
            h.Arrangement.ChordTemplates, grips, minted, null);
        action.Execute();

        Assert.Empty(grips);
        var grown = h.Arrangement.FindChordTemplate(bar.TemplateId)!;
        Assert.NotSame(shared, grown);
        Assert.Equal(3, grown.Frets[3]);
        Assert.Equal(4, grown.Frets.Count);
        Assert.Equal(3, shared.Frets.Count);
        Assert.All(h.Level.Notes, n => Assert.Equal(FractionalPosition.Zero, n.Sustain));

        action.Undo();
        Assert.Equal(shared.Id, bar.TemplateId);
        Assert.Equal(new FractionalPosition(3), bar.EndPosition);
        Assert.Single(h.Arrangement.ChordTemplates);
    }

    [Fact]
    public void ASharedGrip_GrownOverAFittingNote_ReusesAnIdenticalTemplate()
    {
        var h = new Harness();
        var shared = h.AddTemplate(0, (5, 1), (4, 3));
        var whole = h.AddTemplate(1, (5, 1), (4, 3), (3, 3));
        h.AddNote(0, 5, 1);
        h.AddNote(1, 4, 3);
        h.AddNote(2, 3, 3);
        var bar = h.AddHandShape(0, 2, shared.Id);
        h.AddHandShape(8, 10, shared.Id);

        var next = h.Resize(bar, 3, sync: true, out var grips, out var minted);

        Assert.Equal(whole.Id, next.TemplateId);
        Assert.Empty(grips);
        Assert.Empty(minted);
    }

    [Fact]
    public void AnArpeggioGrip_GrownNextToAnArpeggioTemplateOfThoseFrets_ReusesIt()
    {
        var h = new Harness();
        var shared = h.AddTemplate(0, (5, 1), (4, 3), (2, 3));
        shared.Arpeggio = true;
        var whole = h.AddTemplate(1, (5, 1), (4, 3), (3, 3), (2, 3));
        whole.Arpeggio = true;
        h.AddNote(0, 5, 1);
        h.AddNote(1, 4, 3);
        h.AddNote(2, 2, 3);
        h.AddNote(3, 3, 3);
        var bar = h.AddHandShape(0, 3, shared.Id);
        h.AddHandShape(8, 11, shared.Id);

        var next = h.Resize(bar, 4, sync: true, out var grips, out var minted);

        Assert.Equal(whole.Id, next.TemplateId);
        Assert.Empty(grips);
        Assert.Empty(minted);
    }

    [Fact]
    public void GrowingEightArpeggioBars_MintsOneTemplate_AndNamesItForItsFrets()
    {
        var h = new Harness();
        var shared = h.AddTemplate(0, (5, 3), (4, 2), (3, 0));
        shared.Arpeggio = true;
        shared.Name = "Stale";
        var bars = new List<HandShape>();
        for (var i = 0; i < 8; i++)
        {
            var at = i * 8;
            h.AddNote(at, 5, 3);
            h.AddNote(at + 1, 4, 2);
            h.AddNote(at + 2, 3, 0);
            h.AddNote(at + 3, 2, 0);
            bars.Add(h.AddHandShape(at, at + 3, shared.Id));
        }

        var nexts = bars.Select((b, i) =>
        {
            var n = b.Clone();
            n.EndPosition = new FractionalPosition((i * 8) + 4);
            return n;
        }).ToList();
        var grips = new List<ResizeHandShapesAction.GripRewrite>();
        var minted = new List<ChordTemplate>();
        TimelineWheelAdjuster.RebuildGrips(new FingeringGenerationService(), h.Arrangement, true,
            h.Level.Notes, h.Level.Chords, h.Level.HandShapes, bars, nexts, grips, minted);

        var grown = Assert.Single(minted);
        Assert.True(grown.Arpeggio);
        Assert.All(nexts, n => Assert.Equal(grown.Id, n.TemplateId));
        Assert.NotEqual("Stale", grown.Name);
        Assert.Equal(NewChordNames.For(h.Arrangement, grown.Frets), grown.Name);
    }

    [Theory]
    [InlineData(2, 5, true)] // the same string at another fret
    [InlineData(3, 9, true)] // a stretch past one hand
    [InlineData(2, 5, false)]
    [InlineData(3, 9, false)]
    public void AGripGrownOverANoteThatDoesNotFit_IsLeftAlone(int stringIndex, int fret, bool shared)
    {
        var h = new Harness();
        var grip = h.AddTemplate(0, (5, 1), (4, 3), (2, 3));
        h.AddNote(0, 5, 1);
        h.AddNote(1, 4, 3);
        h.AddNote(2, 2, 3);
        h.AddNote(3, stringIndex, fret);
        var bar = h.AddHandShape(0, 3, grip.Id);
        if (shared) h.AddHandShape(8, 11, grip.Id);

        var next = h.Resize(bar, 4, sync: true, out var grips, out var minted);

        Assert.Equal(grip.Id, next.TemplateId);
        Assert.Empty(grips);
        Assert.Empty(minted);
        Assert.Equal(3, grip.Frets.Count);
    }

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
