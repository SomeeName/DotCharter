using DotCharter.Services.Editing;
using DotCharter.Services.Parsing.GuitarPro;

namespace DotCharter.Tests.Services.Editing.Highway;

public sealed class PickSlideHandShapeTests
{
    [Theory]
    [InlineData("convert")]
    [InlineData("place")]
    public void ScrapeCutsTheShapeWhileKeepingChordsOnBothSidesAndUndoRestoresIt(string operation)
    {
        using var h = new PickSlideCommandTests.Harness();
        var chords = Enumerable.Range(0, 4).Select(i => h.Chord(i, 0)).ToList();
        if (operation == "place")
        {
            h.Level.Chords.Remove(chords[1]);
            h.PenPosition = new FractionalPosition(1);
        }
        else
        {
            h.Selection.SelectedChords.Add(chords[1]);
        }

        var shape = new HandShape { Position = FractionalPosition.Zero, EndPosition = new FractionalPosition(4), TemplateId = chords[0].TemplateId };
        h.Level.HandShapes.Add(shape);
        h.History.Clear();
        var before = FocusedHighwayEditingTests.ChartBytes(h.State.ActiveProject!);

        h.Run();

        Assert.Equal(2, h.Level.HandShapes.Count);
        Assert.All(h.Level.HandShapes, s => Assert.False(s.Covers(new FractionalPosition(1))));
        Assert.Single(h.Level.HandShapes, s => s.Covers(FractionalPosition.Zero));
        var tail = Assert.Single(h.Level.HandShapes, s => s.Covers(new FractionalPosition(2)));
        Assert.True(tail.Covers(new FractionalPosition(3)));
        var after = FocusedHighwayEditingTests.ChartBytes(h.State.ActiveProject!);
        h.History.Undo();
        Assert.Same(shape, Assert.Single(h.Level.HandShapes));
        Assert.Equal(before, FocusedHighwayEditingTests.ChartBytes(h.State.ActiveProject!));
        Assert.False(h.History.CanUndo);
        h.History.Redo();
        Assert.Equal(after, FocusedHighwayEditingTests.ChartBytes(h.State.ActiveProject!));
    }

    [Fact]
    public void ConvertingAnArpeggioNoteKeepsTheSoundBearingPiecesAndUndoRestoresTheShape()
    {
        using var h = new PickSlideCommandTests.Harness();
        var notes = Enumerable.Range(0, 4).Select(i => h.Note(i, i, 7 + i,
            new FractionalPosition(new Fraction(1, 4)))).ToList();
        var template = new ChordTemplate { Id = 0, Arpeggio = true };
        foreach (var note in notes) template.Frets[note.StringIndex] = note.Fret;
        h.Arrangement.ChordTemplates.Add(template);
        var shape = new HandShape { Position = FractionalPosition.Zero, EndPosition = new FractionalPosition(4), TemplateId = template.Id };
        h.Level.HandShapes.Add(shape);
        h.Selection.SelectedNotes.Add(notes[1]);
        var before = FocusedHighwayEditingTests.ChartBytes(h.State.ActiveProject!);

        h.Run();

        Assert.Equal(2, h.Level.HandShapes.Count);
        Assert.Single(h.Level.HandShapes, s => s.Covers(notes[0].Position));
        var tail = Assert.Single(h.Level.HandShapes, s => s.Covers(notes[2].Position));
        Assert.True(tail.Covers(notes[3].Position));
        Assert.All(h.Level.HandShapes, s => Assert.False(s.Covers(notes[1].Position)));
        var after = FocusedHighwayEditingTests.ChartBytes(h.State.ActiveProject!);
        h.History.Undo();
        Assert.Same(shape, Assert.Single(h.Level.HandShapes));
        Assert.Equal(before, FocusedHighwayEditingTests.ChartBytes(h.State.ActiveProject!));
        Assert.False(h.History.CanUndo);
        h.History.Redo();
        Assert.Equal(after, FocusedHighwayEditingTests.ChartBytes(h.State.ActiveProject!));
    }

    [Fact]
    public void ConvertingTheOnlyLooseNoteRemovesItsShapeAndUndoRestoresIt()
    {
        using var h = new PickSlideCommandTests.Harness();
        var note = h.Note(1, 0, 7, new FractionalPosition(new Fraction(1, 4)));
        var shape = new HandShape { Position = FractionalPosition.Zero, EndPosition = new FractionalPosition(3) };
        h.Level.HandShapes.Add(shape);
        h.Selection.SelectedNotes.Add(note);
        var before = FocusedHighwayEditingTests.ChartBytes(h.State.ActiveProject!);
        h.Run();
        Assert.Empty(h.Level.HandShapes);
        h.History.Undo();
        Assert.Same(shape, Assert.Single(h.Level.HandShapes));
        Assert.Equal(before, FocusedHighwayEditingTests.ChartBytes(h.State.ActiveProject!));
        Assert.False(h.History.CanUndo);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ConvertingElsewhereDoesNotTouchAShapeOverAnOlderPickSlide(bool chordScrape)
    {
        using var h = new PickSlideCommandTests.Harness();
        if (chordScrape)
        {
            var old = h.Chord(1, 0);
            foreach (var sound in old.ChordNotes) GpPickScrape.Apply(sound, GpPickScrape.DownFlag, 0);
            old.SplitIntoNotes = true;
            old.Ignore = true;
        }
        else
        {
            var old = h.Note(1, 0, 7, new FractionalPosition(new Fraction(1, 4)));
            GpPickScrape.Apply(old, GpPickScrape.DownFlag, 0);
        }

        var untouched = new HandShape { Position = FractionalPosition.Zero, EndPosition = new FractionalPosition(2), TemplateId = -1 };
        var target = h.Note(5, 2, 9, new FractionalPosition(new Fraction(1, 4)));
        var edited = new HandShape { Position = new FractionalPosition(4), EndPosition = new FractionalPosition(6) };
        h.Level.HandShapes.Add(untouched);
        h.Level.HandShapes.Add(edited);
        h.Service.GetLinkNextUpdater()();
        var snapshot = untouched.Clone();
        h.Selection.SelectedNotes.Add(target);

        h.Run();

        Assert.Same(untouched, Assert.Single(h.Level.HandShapes));
        Assert.Equal((snapshot.Position, snapshot.EndPosition, snapshot.TemplateId, snapshot.Name),
            (untouched.Position, untouched.EndPosition, untouched.TemplateId, untouched.Name));
        Assert.DoesNotContain(edited, h.Level.HandShapes);
        h.History.Undo();
        Assert.Contains(edited, h.Level.HandShapes);
        Assert.Contains(untouched, h.Level.HandShapes);
        Assert.False(h.History.CanUndo);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FlippingInsideAnExistingSplitPieceLeavesEveryShapeUnchanged(bool chordScrape)
    {
        using var h = new PickSlideCommandTests.Harness();
        if (chordScrape)
        {
            var chord = h.Chord(1, 0);
            foreach (var sound in chord.ChordNotes) GpPickScrape.Apply(sound, GpPickScrape.DownFlag, 0);
            chord.SplitIntoNotes = true;
            chord.Ignore = true;
            h.Selection.SelectedChords.Add(chord);
        }
        else
        {
            var note = h.Note(1, 0, 7, new FractionalPosition(new Fraction(1, 4)));
            GpPickScrape.Apply(note, GpPickScrape.DownFlag, 0);
            h.Selection.SelectedNotes.Add(note);
        }

        h.Note(3, 2, 9, new FractionalPosition(new Fraction(1, 4)));
        var original = new HandShape { Position = FractionalPosition.Zero, EndPosition = new FractionalPosition(4) };
        var split = Assert.Single(HandShapeSplitter.ComputeSplits([original], [new FractionalPosition(2)]));
        var head = original.Clone();
        head.EndPosition = split.TrimmedEnd;
        var tail = original.Clone();
        tail.Position = split.NewSpans[0].Start;
        h.Level.HandShapes.Add(head);
        h.Level.HandShapes.Add(tail);
        h.Service.GetLinkNextUpdater()();
        var shapes = h.Level.HandShapes.ToArray();
        var states = shapes.Select(s => (s.Position, s.EndPosition, s.TemplateId, s.Name)).ToArray();
        var sounds = h.Level.Notes.Cast<IGuitarSound>().Concat(h.Level.Chords.SelectMany(c => c.ChordNotes))
            .Where(GpPickScrape.IsScrape).ToArray();
        Assert.NotEmpty(sounds);
        Assert.All(sounds, sound => Assert.True(GpPickScrape.IsTowardNut(sound)));
        h.Run();
        Assert.All(sounds, sound => Assert.False(GpPickScrape.IsTowardNut(sound)));
        Assert.Equal(shapes, h.Level.HandShapes);
        Assert.Equal(states, h.Level.HandShapes.Select(s => (s.Position, s.EndPosition, s.TemplateId, s.Name)));
        h.History.Undo();
        Assert.All(sounds, sound => Assert.True(GpPickScrape.IsTowardNut(sound)));
        Assert.Equal(shapes, h.Level.HandShapes);
        Assert.False(h.History.CanUndo);
    }

    [Fact]
    public void AShapeWithOnlyTheConvertedChordIsRemoved()
    {
        using var h = new PickSlideCommandTests.Harness();
        var chord = h.Chord(1, 0);
        var shape = new HandShape { Position = FractionalPosition.Zero, EndPosition = new FractionalPosition(3), TemplateId = chord.TemplateId };
        h.Level.HandShapes.Add(shape);
        h.Selection.SelectedChords.Add(chord);
        h.Run();
        Assert.Empty(h.Level.HandShapes);
        Assert.False(chord.PassOtherNotes);
        h.History.Undo();
        Assert.Same(shape, Assert.Single(h.Level.HandShapes));
    }
}
