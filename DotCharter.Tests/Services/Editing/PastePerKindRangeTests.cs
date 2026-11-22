using static DotCharter.Tests.Services.Editing.PasteReplacementHarness;

namespace DotCharter.Tests.Services.Editing;

public sealed class PastePerKindRangeTests
{
    [Theory]
    [InlineData("Paste")]
    [InlineData("Mouse")]
    [InlineData("SameType")]
    [InlineData("Send")]
    public void DistantMetadata_DoesNotExtendTheSoundOrOtherKindsRange(string mode)
    {
        var h = new PasteReplacementHarness();
        foreach (var beat in new[] { 0, 1 })
        {
            var note = Note(beat);
            h.Level.Notes.Add(note);
            h.Selection.SelectedNotes.Add(note);
        }
        var anchor = new FretAnchor(new FractionalPosition(32), 7);
        var marker = new EventPoint { Position = new FractionalPosition(32), Section = "verse" };
        var tone = new ToneChange { Position = new FractionalPosition(32), ToneName = "clean" };
        h.Level.Anchors.Add(anchor);
        h.Arrangement.EventPoints.Add(marker);
        h.Arrangement.ToneChanges.Add(tone);
        h.Selection.SelectedAnchors.Add(anchor);
        h.Selection.SelectedEventPoints.Add(marker);
        h.Selection.SelectedTones.Add(tone);
        var target = h.AddTarget();
        var notes = Enumerable.Range(0, 33).Where(i => i != 10).Select(i => Note(i, 0, 3)).ToList();
        foreach (var note in notes) target.Levels[0].Notes.Add(note);
        var lateChord = Chord(10);
        target.Levels[0].Chords.Add(lateChord);
        var earlyAnchor = new FretAnchor(new FractionalPosition(2), 4);
        var earlyMarker = new EventPoint { Position = new FractionalPosition(2), Section = "chorus" };
        var earlyTone = new ToneChange { Position = new FractionalPosition(2), ToneName = "drive" };
        target.Levels[0].Anchors.Add(earlyAnchor);
        target.EventPoints.Add(earlyMarker);
        target.ToneChanges.Add(earlyTone);
        var before = ChartProjectCloner.CloneArrangement(target);

        h.PasteTo(target, mode);

        Assert.Equal(32, target.Levels[0].Notes.Count);
        Assert.All(notes.Skip(2), note => Assert.Contains(note, target.Levels[0].Notes));
        Assert.All(target.Levels[0].Notes.Where(n => n.Position <= new FractionalPosition(1)), n => Assert.Equal(1, n.StringIndex));
        Assert.Same(lateChord, Assert.Single(target.Levels[0].Chords));
        Assert.Contains(target.Levels[0].Anchors, a => ReferenceEquals(a, earlyAnchor));
        Assert.Contains(earlyMarker, target.EventPoints);
        Assert.Contains(earlyTone, target.ToneChanges);
        if (mode != "Send")
        {
            Assert.Contains(target.EventPoints, e => e.Position == new FractionalPosition(32));
            Assert.Contains(target.ToneChanges, t => t.Position == new FractionalPosition(32));
        }
        h.History.Undo();
        Assert.Empty(DeepCompare.Diff("Target", before, target, new HashSet<string>()));
        Assert.False(h.History.CanUndo);
    }

    [Theory]
    [InlineData("Paste", true)]
    [InlineData("Mouse", true)]
    [InlineData("SameType", true)]
    [InlineData("Send", true)]
    [InlineData("Paste", false)]
    [InlineData("Mouse", false)]
    [InlineData("SameType", false)]
    [InlineData("Send", false)]
    public void MetadataOnly_DoesNotRemoveSounds(string mode, bool anchorsOnly)
    {
        var h = new PasteReplacementHarness();
        if (anchorsOnly)
        {
            foreach (var beat in new[] { 0, 32 })
            {
                var anchor = new FretAnchor(new FractionalPosition(beat), beat == 0 ? 3 : 7);
                h.Level.Anchors.Add(anchor);
                h.Selection.SelectedAnchors.Add(anchor);
            }
        }
        else
        {
            var marker = new EventPoint { Position = new FractionalPosition(0), Section = "verse" };
            var tone = new ToneChange { Position = new FractionalPosition(32), ToneName = "clean" };
            h.Arrangement.EventPoints.Add(marker);
            h.Arrangement.ToneChanges.Add(tone);
            h.Selection.SelectedEventPoints.Add(marker);
            h.Selection.SelectedTones.Add(tone);
        }
        var target = h.AddTarget();
        var notes = Enumerable.Range(0, 33).Select(i => Note(i, 0, 3)).ToList();
        foreach (var note in notes) target.Levels[0].Notes.Add(note);
        var chord = Chord(34);
        target.Levels[0].Chords.Add(chord);

        h.PasteTo(target, mode);

        Assert.Equal(notes, target.Levels[0].Notes);
        Assert.Same(chord, Assert.Single(target.Levels[0].Chords));
        Assert.Equal(mode == "Send" && !anchorsOnly ? 0 : 1, h.History.GetUndoSteps().Count);
    }

    [Theory]
    [InlineData("Paste")]
    [InlineData("Mouse")]
    [InlineData("SameType")]
    public void BlockedPaste_LeavesEveryKindAndHistoryUnchanged(string mode)
    {
        var h = new PasteReplacementHarness();
        var invalid = Note(0, 8, 30);
        h.Level.Notes.Add(invalid);
        h.Selection.SelectedNotes.Add(invalid);
        h.Selection.SelectedEventPoints.Add(new EventPoint { Position = new FractionalPosition(32), Section = "verse" });
        var target = h.AddTarget();
        target.Levels[0].Notes.Add(Note(0, 0, 3));
        target.Levels[0].Chords.Add(Chord(8));
        target.Levels[0].HandShapes.Add(new HandShape { Position = new FractionalPosition(0), EndPosition = new FractionalPosition(16) });
        target.Levels[0].Anchors.Add(new FretAnchor(new FractionalPosition(0), 3));
        target.EventPoints.Add(new EventPoint { Position = new FractionalPosition(0), Section = "chorus" });
        target.ToneChanges.Add(new ToneChange { Position = new FractionalPosition(0), ToneName = "drive" });
        var before = ChartProjectCloner.CloneArrangement(target);
        var node = h.History.CurrentNodeId;

        h.PasteTo(target, mode);

        Assert.Empty(DeepCompare.Diff("Target", before, target, new HashSet<string>()));
        Assert.Equal(node, h.History.CurrentNodeId);
        Assert.False(h.History.CanUndo);
    }

    [Fact]
    public void OverwriteAll_KeepsTheWholePayloadRange()
    {
        var h = new PasteReplacementHarness();
        h.Selection.SelectedNotes.Add(Note(0));
        h.Selection.SelectedNotes.Add(Note(1));
        h.Selection.SelectedEventPoints.Add(new EventPoint { Position = new FractionalPosition(32), Section = "verse" });
        var target = h.AddTarget();
        foreach (var beat in Enumerable.Range(0, 34)) target.Levels[0].Notes.Add(Note(beat, 0, 3));
        var after = target.Levels[0].Notes[^1];

        h.PasteTo(target, "All");

        Assert.Equal(3, target.Levels[0].Notes.Count);
        Assert.Contains(after, target.Levels[0].Notes);
        Assert.Equal(2, target.Levels[0].Notes.Count(n => n.StringIndex == 1));
    }
}
