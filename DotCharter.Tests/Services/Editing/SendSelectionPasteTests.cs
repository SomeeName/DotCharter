using static DotCharter.Tests.Services.Editing.PasteReplacementHarness;

namespace DotCharter.Tests.Services.Editing;

public sealed class SendSelectionPasteTests
{
    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public void Send_MovesUnfilteredHighwayKinds_AndLeavesEventsAndTonesInBothArrangements(bool replace, bool include)
    {
        var h = new PasteReplacementHarness();
        h.Config.AppConfig.PasteReplacesExisting = replace;
        h.Config.AppConfig.PasteIncludeNotes = include;
        h.Config.AppConfig.PasteIncludeChords = include;
        h.Config.AppConfig.PasteIncludeHandShapes = include;
        h.Config.AppConfig.PasteIncludeFretAnchors = include;
        h.Config.AppConfig.PasteIncludeEvents = include;
        h.Config.AppConfig.PasteIncludeTones = include;
        var note = Note(8);
        var chord = Chord(9);
        var shape = new HandShape { Position = new FractionalPosition(8), EndPosition = new FractionalPosition(10) };
        var anchor = new FretAnchor(new FractionalPosition(8), 7);
        var marker = new EventPoint { Position = new FractionalPosition(8), Section = "verse" };
        var tone = new ToneChange { Position = new FractionalPosition(8), ToneName = "clean" };
        h.Level.Notes.Add(note);
        h.Level.Chords.Add(chord);
        h.Level.HandShapes.Add(shape);
        h.Level.Anchors.Add(anchor);
        h.Arrangement.EventPoints.Add(marker);
        h.Arrangement.ToneChanges.Add(tone);
        h.Selection.SelectedNotes.Add(note);
        h.Selection.SelectedChords.Add(chord);
        h.Selection.SelectedHandShapes.Add(shape);
        h.Selection.SelectedAnchors.Add(anchor);
        h.Selection.SelectedEventPoints.Add(marker);
        h.Selection.SelectedTones.Add(tone);
        var target = h.AddTarget();
        var oldNote = Note(8, 0, 3);
        var oldShape = new HandShape { Position = new FractionalPosition(7), EndPosition = new FractionalPosition(9) };
        var oldAnchor = new FretAnchor(new FractionalPosition(8), 3);
        var oldMarker = new EventPoint { Position = new FractionalPosition(8), Section = "chorus" };
        var oldTone = new ToneChange { Position = new FractionalPosition(8), ToneName = "drive" };
        target.Levels[0].Notes.Add(oldNote);
        target.Levels[0].HandShapes.Add(oldShape);
        target.Levels[0].Anchors.Add(oldAnchor);
        target.EventPoints.Add(oldMarker);
        target.ToneChanges.Add(oldTone);
        var sourceBefore = ChartProjectCloner.CloneArrangement(h.Arrangement);
        var targetBefore = ChartProjectCloner.CloneArrangement(target);

        h.Coordinator.SendSelectionToArrangement(target);

        Assert.Empty(h.Level.Notes);
        Assert.Empty(h.Level.Chords);
        Assert.Empty(h.Level.HandShapes);
        Assert.Empty(h.Level.Anchors);
        Assert.Contains(note, target.Levels[0].Notes);
        Assert.Same(chord, Assert.Single(target.Levels[0].Chords));
        Assert.Contains(shape, target.Levels[0].HandShapes);
        Assert.Contains(target.Levels[0].Anchors, a => ReferenceEquals(a, anchor));
        Assert.Equal(replace ? 1 : 2, target.Levels[0].Notes.Count);
        Assert.Equal(replace ? 1 : 2, target.Levels[0].HandShapes.Count);
        Assert.Equal(replace ? 1 : 2, target.Levels[0].Anchors.Count);
        Assert.Same(marker, Assert.Single(h.Arrangement.EventPoints));
        Assert.Same(tone, Assert.Single(h.Arrangement.ToneChanges));
        Assert.Same(oldMarker, Assert.Single(target.EventPoints));
        Assert.Same(oldTone, Assert.Single(target.ToneChanges));
        Assert.Contains(h.Logger.Entries, e => e.Message.Contains("Moved highway selection", StringComparison.Ordinal));
        var sourceAfter = ChartProjectCloner.CloneArrangement(h.Arrangement);
        var targetAfter = ChartProjectCloner.CloneArrangement(target);
        h.History.Undo();
        Assert.Empty(DeepCompare.Diff("Source", sourceBefore, h.Arrangement, new HashSet<string>()));
        Assert.Empty(DeepCompare.Diff("Target", targetBefore, target, new HashSet<string>()));
        Assert.False(h.History.CanUndo);
        h.History.Redo();
        Assert.Empty(DeepCompare.Diff("Source", sourceAfter, h.Arrangement, new HashSet<string>()));
        Assert.Empty(DeepCompare.Diff("Target", targetAfter, target, new HashSet<string>()));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SendingOnlyEventsAndTones_IsANoOp(bool replace)
    {
        var h = new PasteReplacementHarness();
        h.Config.AppConfig.PasteReplacesExisting = replace;
        var marker = new EventPoint { Position = new FractionalPosition(0), Section = "verse" };
        var tone = new ToneChange { Position = new FractionalPosition(32), ToneName = "clean" };
        h.Arrangement.EventPoints.Add(marker);
        h.Arrangement.ToneChanges.Add(tone);
        h.Selection.SelectedEventPoints.Add(marker);
        h.Selection.SelectedTones.Add(tone);
        var target = h.AddTarget();

        h.Coordinator.SendSelectionToArrangement(target);

        Assert.Same(marker, Assert.Single(h.Arrangement.EventPoints));
        Assert.Same(tone, Assert.Single(h.Arrangement.ToneChanges));
        Assert.Empty(target.EventPoints);
        Assert.Empty(target.ToneChanges);
        Assert.False(h.History.CanUndo);
    }
}
