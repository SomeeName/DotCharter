using static DotCharter.Tests.Services.Editing.PasteReplacementHarness;

namespace DotCharter.Tests.Services.Editing;

public sealed class PasteReplacementTests
{
    [Theory]
    [InlineData(false, true)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(true, false)]
    public void PasteAtTheChosenTime_ReplacesOtherStrings_UnlessDisabled(bool throughGlobal, bool replace)
    {
        var h = new PasteReplacementHarness();
        h.Config.AppConfig.PasteReplacesExisting = replace;
        h.Selection.SelectedNotes.Add(Note(0));
        h.Copy();
        var old = Note(8, 0, 3);
        h.Level.Notes.Add(old);

        if (throughGlobal) h.Coordinator.Paste(4000, 4);
        else h.Clipboard.Paste(h.Track, 4000, 4);

        if (replace)
        {
            var pasted = Assert.Single(h.Level.Notes);
            Assert.Equal(1, pasted.StringIndex);
            Assert.Equal(7, pasted.Fret);
            Assert.Empty(h.Level.Chords);
        }
        else
        {
            Assert.Empty(h.Level.Notes);
            var chord = Assert.Single(h.Level.Chords);
            Assert.Equal(new[] { 0, 1 }, chord.ChordNotes.Select(n => n.StringIndex).Order().ToArray());
        }
    }

    [Fact]
    public void EightRefrettedNotes_ReplaceTheRunAndItsAnchor()
    {
        var h = new PasteReplacementHarness();
        for (var i = 0; i < 8; i++) h.Selection.SelectedNotes.Add(Note(i));
        h.Selection.SelectedAnchors.Add(new FretAnchor(new FractionalPosition(0), 7));
        h.Copy();
        var old = Enumerable.Range(8, 8).Select(i => Note(i, 0, 3)).ToList();
        foreach (var note in old) h.Level.Notes.Add(note);
        var anchor = new FretAnchor(new FractionalPosition(8), 3);
        h.Level.Anchors.Add(anchor);

        h.Clipboard.Paste(h.Track, 4000, 4);

        Assert.Equal(8, h.Level.Notes.Count);
        Assert.All(h.Level.Notes, n =>
        {
            Assert.Equal(1, n.StringIndex);
            Assert.Equal(7, n.Fret);
        });
        Assert.Equal(Enumerable.Range(8, 8).Select(i => new FractionalPosition(i)), h.Level.Notes.Select(n => n.Position));
        Assert.Empty(h.Level.Chords);
        Assert.Equal(7, Assert.Single(h.Level.Anchors).Fret);
        Assert.DoesNotContain(h.Level.Anchors, a => ReferenceEquals(a, anchor));
        h.History.Undo();
        Assert.Equal(old, h.Level.Notes);
        Assert.Same(anchor, Assert.Single(h.Level.Anchors));
        Assert.False(h.History.CanUndo);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NotesOnly_ReplaceAChord(bool special)
    {
        var h = new PasteReplacementHarness();
        h.Selection.SelectedNotes.Add(Note(0));
        h.Copy();
        h.Level.Chords.Add(Chord(8));

        if (special) h.Clipboard.PasteSpecial(h.Track, 4000, 4, PasteSpecialMode.OverwriteSameType);
        else h.Clipboard.Paste(h.Track, 4000, 4);

        Assert.Empty(h.Level.Chords);
        Assert.Equal(1, Assert.Single(h.Level.Notes).StringIndex);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ChordsOnly_ReplaceNotes(bool special)
    {
        var h = new PasteReplacementHarness();
        h.Selection.SelectedChords.Add(Chord(0));
        h.Copy();
        h.Level.Notes.Add(Note(8, 1, 12));

        if (special) h.Clipboard.PasteSpecial(h.Track, 4000, 4, PasteSpecialMode.OverwriteSameType);
        else h.Clipboard.Paste(h.Track, 4000, 4);

        Assert.Empty(h.Level.Notes);
        Assert.Equal(2, Assert.Single(h.Level.Chords).ChordNotes.Count);
    }

    [Theory]
    [InlineData("Notes")]
    [InlineData("Chords")]
    [InlineData("HandShapes")]
    [InlineData("FretAnchors")]
    [InlineData("Events")]
    [InlineData("Tones")]
    public void ExcludedKinds_AreNeitherPastedNorRemoved(string kind)
    {
        var h = new PasteReplacementHarness();
        h.Selection.SelectedNotes.Add(Note(0));
        h.Selection.SelectedChords.Add(Chord(1));
        h.Selection.SelectedHandShapes.Add(new HandShape { Position = new FractionalPosition(0), EndPosition = new FractionalPosition(2), TemplateId = -1 });
        h.Selection.SelectedAnchors.Add(new FretAnchor(new FractionalPosition(0), 7));
        h.Selection.SelectedEventPoints.Add(new EventPoint { Position = new FractionalPosition(0) });
        h.Selection.SelectedTones.Add(new ToneChange { Position = new FractionalPosition(0) });
        h.Copy();
        h.Config.AppConfig.PasteIncludeNotes = kind != "Notes";
        h.Config.AppConfig.PasteIncludeChords = kind != "Chords";
        h.Config.AppConfig.PasteIncludeHandShapes = kind != "HandShapes";
        h.Config.AppConfig.PasteIncludeFretAnchors = kind != "FretAnchors";
        h.Config.AppConfig.PasteIncludeEvents = kind != "Events";
        h.Config.AppConfig.PasteIncludeTones = kind != "Tones";
        var note = Note(8, 4, 12);
        var chord = Chord(9);
        var shape = new HandShape { Position = new FractionalPosition(8), EndPosition = new FractionalPosition(10), TemplateId = -1 };
        var anchor = new FretAnchor(new FractionalPosition(8), 3);
        var marker = new EventPoint { Position = new FractionalPosition(8) };
        var tone = new ToneChange { Position = new FractionalPosition(8) };
        h.Level.Notes.Add(note);
        h.Level.Chords.Add(chord);
        h.Level.HandShapes.Add(shape);
        h.Level.Anchors.Add(anchor);
        h.Arrangement.EventPoints.Add(marker);
        h.Arrangement.ToneChanges.Add(tone);

        h.Clipboard.Paste(h.Track, 4000, 4);

        switch (kind)
        {
            case "Notes": Assert.Same(note, Assert.Single(h.Level.Notes)); break;
            case "Chords": Assert.Same(chord, Assert.Single(h.Level.Chords)); break;
            case "HandShapes": Assert.Same(shape, Assert.Single(h.Level.HandShapes)); break;
            case "FretAnchors": Assert.Same(anchor, Assert.Single(h.Level.Anchors)); break;
            case "Events": Assert.Same(marker, Assert.Single(h.Arrangement.EventPoints)); break;
            case "Tones": Assert.Same(tone, Assert.Single(h.Arrangement.ToneChanges)); break;
        }
    }

    [Fact]
    public void UndoRedo_RestoresEveryReplacedKind_InOneStep()
    {
        var h = new PasteReplacementHarness();
        h.Selection.SelectedNotes.Add(Note(0));
        h.Selection.SelectedNotes.Add(Note(2));
        h.Selection.SelectedHandShapes.Add(new HandShape { Position = new FractionalPosition(0), EndPosition = new FractionalPosition(2), TemplateId = -1 });
        h.Selection.SelectedAnchors.Add(new FretAnchor(new FractionalPosition(0), 7));
        h.Selection.SelectedEventPoints.Add(new EventPoint { Position = new FractionalPosition(0) });
        h.Selection.SelectedTones.Add(new ToneChange { Position = new FractionalPosition(0) });
        h.Copy();
        var note = Note(8, 0, 3);
        var chord = Chord(10);
        var shape = new HandShape { Position = new FractionalPosition(7), EndPosition = new FractionalPosition(9), TemplateId = -1 };
        var anchor = new FretAnchor(new FractionalPosition(8), 3);
        var marker = new EventPoint { Position = new FractionalPosition(8) };
        var tone = new ToneChange { Position = new FractionalPosition(8) };
        h.Level.Notes.Add(note);
        h.Level.Chords.Add(chord);
        h.Level.HandShapes.Add(shape);
        h.Level.Anchors.Add(anchor);
        h.Arrangement.EventPoints.Add(marker);
        h.Arrangement.ToneChanges.Add(tone);
        var before = ChartProjectCloner.CloneArrangement(h.Arrangement);

        h.Clipboard.Paste(h.Track, 4000, 4);

        Assert.DoesNotContain(note, h.Level.Notes);
        Assert.Empty(h.Level.Chords);
        Assert.DoesNotContain(shape, h.Level.HandShapes);
        Assert.DoesNotContain(h.Level.Anchors, a => ReferenceEquals(a, anchor));
        Assert.DoesNotContain(marker, h.Arrangement.EventPoints);
        Assert.DoesNotContain(tone, h.Arrangement.ToneChanges);
        var after = ChartProjectCloner.CloneArrangement(h.Arrangement);
        h.History.Undo();
        Assert.Empty(DeepCompare.Diff("Arrangement", before, h.Arrangement, new HashSet<string>()));
        Assert.False(h.History.CanUndo);
        h.History.Redo();
        Assert.Empty(DeepCompare.Diff("Arrangement", after, h.Arrangement, new HashSet<string>()));
    }

    [Theory]
    [InlineData(PasteSpecialMode.Preserve, false)]
    [InlineData(PasteSpecialMode.Preserve, true)]
    [InlineData(PasteSpecialMode.OverwriteAll, false)]
    [InlineData(PasteSpecialMode.OverwriteAll, true)]
    public void ExplicitPasteModes_IgnoreTheDefaultSetting(PasteSpecialMode mode, bool replace)
    {
        var h = new PasteReplacementHarness();
        h.Config.AppConfig.PasteReplacesExisting = replace;
        h.Selection.SelectedNotes.Add(Note(0));
        h.Copy();
        h.Level.Notes.Add(Note(8, 0, 3));
        var anchor = new FretAnchor(new FractionalPosition(8), 3);
        h.Level.Anchors.Add(anchor);
        h.Clipboard.PasteSpecial(h.Track, 4000, 4, mode);

        if (mode == PasteSpecialMode.Preserve)
        {
            Assert.Equal(2, Assert.Single(h.Level.Chords).ChordNotes.Count);
            Assert.Same(anchor, Assert.Single(h.Level.Anchors));
        }
        else
        {
            Assert.Empty(h.Level.Chords);
            Assert.Single(h.Level.Notes);
            Assert.Empty(h.Level.Anchors);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SendSelection_ReplacesByDefault_AndUndoRestoresBothArrangements(bool replace)
    {
        var h = new PasteReplacementHarness();
        Assert.True(h.Config.AppConfig.PasteReplacesExisting);
        h.Config.AppConfig.PasteReplacesExisting = replace;
        var note = Note(8);
        h.Level.Notes.Add(note);
        h.Selection.SelectedNotes.Add(note);
        var target = h.AddTarget();
        var old = Chord(8);
        target.Levels[0].Chords.Add(old);

        h.Coordinator.SendSelectionToArrangement(target);

        Assert.Empty(h.Level.Notes);
        Assert.Same(note, Assert.Single(target.Levels[0].Notes));
        Assert.Equal(replace ? 0 : 1, target.Levels[0].Chords.Count);
        h.History.Undo();
        Assert.Same(note, Assert.Single(h.Level.Notes));
        Assert.Same(old, Assert.Single(target.Levels[0].Chords));
        Assert.Empty(target.Levels[0].Notes);
        Assert.False(h.History.CanUndo);
        h.History.Redo();
        Assert.Empty(h.Level.Notes);
        Assert.Same(note, Assert.Single(target.Levels[0].Notes));
        Assert.Equal(replace ? 0 : 1, target.Levels[0].Chords.Count);
    }

    [Fact]
    public void PlainPaste_UsesSoundOnsets_AndLeavesTheLastSustainsNeighbour()
    {
        var h = new PasteReplacementHarness();
        var incoming = Note(0);
        incoming.Sustain = new FractionalPosition(2);
        h.Selection.SelectedNotes.Add(incoming);
        h.Selection.SelectedEventPoints.Add(new EventPoint { Position = new FractionalPosition(4) });
        h.Copy();
        var replaced = Note(8, 0, 3);
        var neighbour = Note(10, 0, 3);
        h.Level.Notes.Add(replaced);
        h.Level.Notes.Add(neighbour);

        h.Clipboard.Paste(h.Track, 4000, 4);

        Assert.Equal(2, h.Level.Notes.Count);
        Assert.DoesNotContain(replaced, h.Level.Notes);
        Assert.Contains(neighbour, h.Level.Notes);
        Assert.Empty(h.Level.Chords);
        Assert.Equal(new FractionalPosition(12), Assert.Single(h.Arrangement.EventPoints).Position);
    }
}
