using static DotCharter.Tests.Services.Editing.PasteReplacementHarness;

namespace DotCharter.Tests.Services.Editing;

public sealed class PasteHandShapeReplacementTests
{
    [Theory]
    [InlineData("Paste")]
    [InlineData("Mouse")]
    [InlineData("SameType")]
    [InlineData("Send")]
    public void ReplacingAShortSpan_KeepsBothChordBearingEdgesAndTheirTemplates(string mode)
    {
        var h = new PasteReplacementHarness();
        h.Arrangement.ChordTemplates.Add(new ChordTemplate { Id = 0, Name = "Source", Frets = new Dictionary<int, int> { [0] = 3, [2] = 5 } });
        var incoming = new HandShape { Position = new FractionalPosition(12), EndPosition = new FractionalPosition(13), TemplateId = 0, Name = "Incoming" };
        h.Level.HandShapes.Add(incoming);
        h.Selection.SelectedHandShapes.Add(incoming);
        var target = h.AddTarget();
        target.ChordTemplates.Add(new ChordTemplate { Id = 0, Name = "Destination", Frets = new Dictionary<int, int> { [0] = 8, [2] = 10 } });
        var original = new HandShape { Position = new FractionalPosition(0), EndPosition = new FractionalPosition(16), TemplateId = 0, Name = "Retained" };
        target.Levels[0].HandShapes.Add(original);
        for (var beat = 0; beat < 16; beat++)
        {
            var chord = Chord(beat);
            chord.TemplateId = 0;
            chord.ChordNotes[0].Fret = 8;
            chord.ChordNotes[1].Fret = 10;
            target.Levels[0].Chords.Add(chord);
        }
        var before = ChartProjectCloner.CloneArrangement(target);
        var originalChords = target.Levels[0].Chords.ToArray();

        h.PasteTo(target, mode, 12);

        Assert.Equal(originalChords, target.Levels[0].Chords);
        var shapes = target.Levels[0].HandShapes.OrderBy(s => s.Position).ToArray();
        Assert.Equal(3, shapes.Length);
        Assert.Equal(new FractionalPosition(0), shapes[0].Position);
        Assert.Equal(new FractionalPosition(12), shapes[0].EndPosition);
        Assert.Equal(new FractionalPosition(12), shapes[1].Position);
        Assert.Equal(new FractionalPosition(13), shapes[1].EndPosition);
        Assert.Equal(new FractionalPosition(13), shapes[2].Position);
        Assert.Equal(new FractionalPosition(16), shapes[2].EndPosition);
        Assert.Equal(0, shapes[0].TemplateId);
        Assert.Equal(0, shapes[2].TemplateId);
        Assert.Equal("Retained", shapes[0].Name);
        Assert.Equal("Retained", shapes[2].Name);
        Assert.NotEqual(0, shapes[1].TemplateId);
        Assert.All(originalChords, chord => Assert.Contains(shapes, s => s.Covers(chord.Position)));
        var after = ChartProjectCloner.CloneArrangement(target);
        h.History.Undo();
        Assert.Same(original, Assert.Single(target.Levels[0].HandShapes));
        Assert.Empty(DeepCompare.Diff("Target", before, target, new HashSet<string>()));
        Assert.False(h.History.CanUndo);
        h.History.Redo();
        Assert.Empty(DeepCompare.Diff("Target", after, target, new HashSet<string>()));
    }

    [Theory]
    [InlineData("Paste", true, false)]
    [InlineData("SameType", false, true)]
    [InlineData("Send", false, false)]
    public void AnOutsidePieceWithoutAChord_IsDropped(string mode, bool headChord, bool tailChord)
    {
        var h = new PasteReplacementHarness();
        h.Selection.SelectedHandShapes.Add(new HandShape { Position = new FractionalPosition(12), EndPosition = new FractionalPosition(13) });
        foreach (var selectedShape in h.Selection.SelectedHandShapes) h.Level.HandShapes.Add(selectedShape);
        var target = h.AddTarget();
        target.Levels[0].HandShapes.Add(new HandShape { Position = new FractionalPosition(0), EndPosition = new FractionalPosition(16) });
        if (headChord) target.Levels[0].Chords.Add(Chord(2));
        if (tailChord) target.Levels[0].Chords.Add(Chord(14));

        h.PasteTo(target, mode, 12);

        Assert.Equal(1 + (headChord ? 1 : 0) + (tailChord ? 1 : 0), target.Levels[0].HandShapes.Count);
        Assert.Equal(headChord, target.Levels[0].HandShapes.Any(s => s.Position == FractionalPosition.Zero));
        Assert.Equal(tailChord, target.Levels[0].HandShapes.Any(s => s.EndPosition == new FractionalPosition(16)));
    }

    [Fact]
    public void SeparatePastedSpans_PreserveTheChordBearingGap()
    {
        var h = new PasteReplacementHarness();
        h.Selection.SelectedHandShapes.Add(new HandShape { Position = new FractionalPosition(2), EndPosition = new FractionalPosition(3) });
        h.Selection.SelectedHandShapes.Add(new HandShape { Position = new FractionalPosition(12), EndPosition = new FractionalPosition(13) });
        foreach (var selectedShape in h.Selection.SelectedHandShapes) h.Level.HandShapes.Add(selectedShape);
        var target = h.AddTarget();
        target.Levels[0].HandShapes.Add(new HandShape { Position = new FractionalPosition(0), EndPosition = new FractionalPosition(16) });
        var chord = Chord(8);
        target.Levels[0].Chords.Add(chord);

        h.PasteTo(target, "SameType", 2);

        Assert.Contains(target.Levels[0].HandShapes, s => s.Position == new FractionalPosition(3) && s.EndPosition == new FractionalPosition(12));
        Assert.Contains(target.Levels[0].HandShapes, s => s.Covers(chord.Position));
        Assert.Same(chord, Assert.Single(target.Levels[0].Chords));
    }

    [Theory]
    [InlineData("Paste")]
    [InlineData("Mouse")]
    [InlineData("SameType")]
    [InlineData("Send")]
    public void AnExtendedHandShape_DoesNotDeleteSoundsOutsideItsReplacedShape(string mode)
    {
        var h = new PasteReplacementHarness();
        h.Selection.SelectedHandShapes.Add(new HandShape { Position = new FractionalPosition(12), EndPosition = new FractionalPosition(15) });
        foreach (var selectedShape in h.Selection.SelectedHandShapes) h.Level.HandShapes.Add(selectedShape);
        var target = h.AddTarget();
        target.Levels[0].HandShapes.Add(new HandShape { Position = new FractionalPosition(0), EndPosition = new FractionalPosition(13) });
        var head = Chord(2);
        var tail = Chord(14);
        target.Levels[0].Chords.Add(head);
        target.Levels[0].Chords.Add(tail);

        h.PasteTo(target, mode, 12);

        Assert.Equal(new[] { head, tail }, target.Levels[0].Chords);
        Assert.Contains(target.Levels[0].HandShapes, s => s.Covers(head.Position));
        Assert.Contains(target.Levels[0].HandShapes, s => s.Covers(tail.Position));
    }

    [Theory]
    [InlineData("Paste")]
    [InlineData("Mouse")]
    [InlineData("SameType")]
    [InlineData("Send")]
    public void ArpeggioEdge_KeepsThePieceCoveringThreeLooseNotes_AndUndoRestoresTheShape(string mode)
    {
        var h = new PasteReplacementHarness();
        var incoming = new HandShape { Position = new FractionalPosition(12), EndPosition = new FractionalPosition(13) };
        var pasted = Note(12);
        h.Level.HandShapes.Add(incoming);
        h.Level.Notes.Add(pasted);
        h.Selection.SelectedHandShapes.Add(incoming);
        h.Selection.SelectedNotes.Add(pasted);
        var target = h.AddTarget();
        target.ChordTemplates.Add(new ChordTemplate { Id = 0, Name = "Arpeggio", Arpeggio = true, Frets = new Dictionary<int, int> { [0] = 3 } });
        var original = new HandShape { Position = FractionalPosition.Zero, EndPosition = new FractionalPosition(16), TemplateId = 0 };
        target.Levels[0].HandShapes.Add(original);
        var loose = new[] { 0, 4, 8, 12 }.Select(beat => Note(beat, 0, 3)).ToArray();
        foreach (var note in loose) target.Levels[0].Notes.Add(note);
        var before = ChartProjectCloner.CloneArrangement(target);

        h.PasteTo(target, mode, 12);

        Assert.Empty(target.Levels[0].Chords);
        Assert.Equal(2, target.Levels[0].HandShapes.Count);
        var retained = Assert.Single(target.Levels[0].HandShapes, s => s.Position == FractionalPosition.Zero);
        Assert.Equal(new FractionalPosition(12), retained.EndPosition);
        Assert.Equal(0, retained.TemplateId);
        Assert.All(loose.Take(3), note =>
        {
            Assert.Contains(note, target.Levels[0].Notes);
            Assert.True(retained.Covers(note.Position));
        });
        Assert.DoesNotContain(loose[3], target.Levels[0].Notes);
        h.History.Undo();
        Assert.Same(original, Assert.Single(target.Levels[0].HandShapes));
        Assert.Empty(DeepCompare.Diff("Target", before, target, new HashSet<string>()));
        Assert.False(h.History.CanUndo);
    }

    [Theory]
    [InlineData("Paste")]
    [InlineData("Mouse")]
    [InlineData("SameType")]
    [InlineData("Send")]
    public void EverySoundInsideThePastedSpan_DropsTheEmptyOutsidePieces_AndUndoRestoresTheShape(string mode)
    {
        var h = new PasteReplacementHarness();
        var incoming = new HandShape { Position = new FractionalPosition(12), EndPosition = new FractionalPosition(13) };
        h.Level.HandShapes.Add(incoming);
        h.Selection.SelectedHandShapes.Add(incoming);
        var positions = Enumerable.Range(0, 4).Select(i => new FractionalPosition(12, new Fraction(i, 4))).ToArray();
        foreach (var position in new[] { positions[0], positions[3] })
        {
            var note = Note(12);
            note.Position = position;
            h.Level.Notes.Add(note);
            h.Selection.SelectedNotes.Add(note);
        }
        var target = h.AddTarget();
        target.ChordTemplates.Add(new ChordTemplate { Id = 0, Name = "Arpeggio", Arpeggio = true, Frets = new Dictionary<int, int> { [0] = 3 } });
        var original = new HandShape { Position = FractionalPosition.Zero, EndPosition = new FractionalPosition(16), TemplateId = 0 };
        target.Levels[0].HandShapes.Add(original);
        var loose = positions.Select(position => new GuitarNote { Position = position, StringIndex = 0, Fret = 3 }).ToArray();
        foreach (var note in loose) target.Levels[0].Notes.Add(note);
        var before = ChartProjectCloner.CloneArrangement(target);

        h.PasteTo(target, mode, 12);

        var shape = Assert.Single(target.Levels[0].HandShapes);
        Assert.Equal(new FractionalPosition(12), shape.Position);
        Assert.Equal(new FractionalPosition(13), shape.EndPosition);
        Assert.All(loose, note => Assert.DoesNotContain(note, target.Levels[0].Notes));
        Assert.Equal(2, target.Levels[0].Notes.Count);
        h.History.Undo();
        Assert.Same(original, Assert.Single(target.Levels[0].HandShapes));
        Assert.Empty(DeepCompare.Diff("Target", before, target, new HashSet<string>()));
        Assert.False(h.History.CanUndo);
    }

    [Fact]
    public void OverwriteAll_RemovesTheWholeOverlappingShape()
    {
        var h = new PasteReplacementHarness();
        h.Selection.SelectedHandShapes.Add(new HandShape { Position = new FractionalPosition(12), EndPosition = new FractionalPosition(13) });
        foreach (var selectedShape in h.Selection.SelectedHandShapes) h.Level.HandShapes.Add(selectedShape);
        var target = h.AddTarget();
        var original = new HandShape { Position = new FractionalPosition(0), EndPosition = new FractionalPosition(16) };
        target.Levels[0].HandShapes.Add(original);
        target.Levels[0].Chords.Add(Chord(2));
        target.Levels[0].Chords.Add(Chord(14));

        h.PasteTo(target, "All", 12);

        var shape = Assert.Single(target.Levels[0].HandShapes);
        Assert.Equal(new FractionalPosition(12), shape.Position);
        Assert.Equal(new FractionalPosition(13), shape.EndPosition);
        h.History.Undo();
        Assert.Same(original, Assert.Single(target.Levels[0].HandShapes));
        Assert.False(h.History.CanUndo);
    }
}
