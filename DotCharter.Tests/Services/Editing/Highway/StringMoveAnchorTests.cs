using DotCharter.Services.Editing.Validation.Rules;

namespace DotCharter.Tests.Services.Editing.Highway;

public sealed class StringMoveAnchorTests
{
    [Fact]
    public void ChordLineMovesItsAnchorsAndLeavesTheUnreachableChordInPlace()
    {
        var h = Line();

        h.Service.MoveSelectedStringPreservingPitch(1, 6);

        Assert.Equal(new[] { 1, 2, 4, 1 }, h.State.ActiveLevel!.Anchors.Select(a => a.Fret));
        Assert.Equal(new[] { 1, 1, 0, 1 }, h.State.PlacedChords.Select(c => c.ChordNotes[0].StringIndex));
        Assert.Equal(new[] { 2, 2, 1, 2 }, h.State.PlacedChords.Select(c => c.ChordNotes[1].StringIndex));
        Assert.Equal(new[] { 1, 2, 4, 1 }, h.State.PlacedChords.Select(c => c.ChordNotes[0].Fret));
        Assert.Equal(new[] { 3, 4, 6, 3 }, h.State.PlacedChords.Select(c => c.ChordNotes[1].Fret));
        Assert.Equal(4, h.Selection.SelectedChords.Count);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void MixedSpanRegeneratesCoverageForMovedAndStationarySounds(bool selectStationary)
    {
        var h = new ChordHandShapeSyncTests.Harness();
        AddNote(h, 2, 0, 8, true);
        AddNote(h, 4, 0, selectStationary ? 4 : 8, selectStationary);
        h.State.ActiveLevel!.Anchors.Add(new FretAnchor(new FractionalPosition(1), 4, 5));
        h.State.ActiveLevel.Anchors.Add(new FretAnchor(new FractionalPosition(6), 12));

        h.Service.MoveSelectedStringPreservingPitch(1, 6);

        Assert.Equal(3, h.State.ActiveLevel.Anchors.OrderBy(a => a.Position).First().Fret);
        Assert.Empty(FhpIssues(h));
        Assert.Contains(h.State.ActiveLevel.Anchors, a => a.Position == new FractionalPosition(6) && a.Fret == 12);
        Assert.Equal(selectStationary ? 2 : 1, h.Selection.SelectedNotes.Count);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(3, 4)]
    public void UniformSpanRespectsTheCapoFloorAndKeepsWidth(int capo, int expected)
    {
        var h = new ChordHandShapeSyncTests.Harness();
        h.Arrangement.Capo = capo;
        AddNote(h, 2, 0, capo + 5, true);
        h.State.ActiveLevel!.Anchors.Add(new FretAnchor(new FractionalPosition(1), capo + 2, 6));

        h.Service.MoveSelectedStringPreservingPitch(1, 6);

        var anchor = Assert.Single(h.State.ActiveLevel.Anchors);
        Assert.Equal(expected, anchor.Fret);
        Assert.Equal(6, anchor.Width);
        Assert.Equal(new FractionalPosition(1), anchor.Position);
    }

    [Theory]
    [InlineData(4, 21)]
    [InlineData(7, 18)]
    public void UniformSpanCapsTheAnchorAtTheNeckLimit(int width, int expected)
    {
        var h = new ChordHandShapeSyncTests.Harness();
        AddNote(h, 2, 1, 19, true);
        h.State.ActiveLevel!.Anchors.Add(new FretAnchor(new FractionalPosition(1), expected - 2, width));
        Assert.Empty(FhpIssues(h));

        h.Service.MoveSelectedStringPreservingPitch(-1, 6);

        var anchor = Assert.Single(h.State.ActiveLevel.Anchors);
        Assert.Equal(expected, anchor.Fret);
        Assert.Equal(width, anchor.Width);
        Assert.Empty(FhpIssues(h));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ShiftedPhraseStartAnchorsAreKeptWhenTheirFretsMatch(bool imported)
    {
        var h = new ChordHandShapeSyncTests.Harness();
        AddPhrase(h, 1, "verse", imported);
        AddPhrase(h, 5, "chorus", imported);
        foreach (var beat in new[] { 1, 5 })
        {
            h.State.ActiveLevel!.Anchors.Add(new FretAnchor(new FractionalPosition(beat), 8));
            AddNote(h, beat + 1, 0, 8, true);
        }

        var before = FhpIssues(h);
        Assert.Empty(before);

        h.Service.MoveSelectedStringPreservingPitch(1, 6);

        Assert.Equal(new[] { 1, 5 }, h.State.ActiveLevel!.Anchors.Select(a => a.Position.BeatId));
        Assert.Equal(new[] { 3, 3 }, h.State.ActiveLevel.Anchors.Select(a => a.Fret));
        Assert.Empty(FhpIssues(h).Except(before));
    }

    [Fact]
    public void UnchangedPhraseStartAnchorIsKeptAfterAShiftedSpan()
    {
        var h = new ChordHandShapeSyncTests.Harness();
        AddPhrase(h, 1, "verse", false);
        AddPhrase(h, 5, "chorus", true);
        var level = h.State.ActiveLevel!;
        level.Anchors.Add(new FretAnchor(new FractionalPosition(1), 8));
        var opening = new FretAnchor(new FractionalPosition(5), 3);
        level.Anchors.Add(opening);
        AddNote(h, 2, 0, 8, true);
        AddNote(h, 6, 0, 3, false);
        Assert.Empty(FhpIssues(h));

        h.Service.MoveSelectedStringPreservingPitch(1, 6);

        Assert.Contains(opening, level.Anchors);
        Assert.Equal(new[] { 3, 3 }, level.Anchors.Select(a => a.Fret));
        Assert.Empty(FhpIssues(h));
    }

    [Theory]
    [InlineData(5)]
    [InlineData(6)]
    public void RegeneratedPhraseOpeningAnchorIsKeptWhenItMatchesItsPredecessor(int firstSound)
    {
        var h = new ChordHandShapeSyncTests.Harness();
        AddPhrase(h, 1, "verse", false);
        AddPhrase(h, 5, "chorus", true);
        var level = h.State.ActiveLevel!;
        level.Anchors.Add(new FretAnchor(new FractionalPosition(1), 3));
        level.Anchors.Add(new FretAnchor(new FractionalPosition(5), 4, 5));
        AddNote(h, 2, 0, 3, false);
        AddNote(h, firstSound, 0, 8, true);
        AddNote(h, firstSound + 1, 0, 4, false);
        Assert.Empty(FhpIssues(h));

        h.Service.MoveSelectedStringPreservingPitch(1, 6);

        var opening = Assert.Single(level.Anchors, a => a.Position == new FractionalPosition(firstSound));
        Assert.Equal(3, opening.Fret);
        Assert.Equal(4, opening.Width);
        Assert.Empty(FhpIssues(h));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RemovedAnchorSelectionFollowsUndoAndRedo(bool regenerate)
    {
        var h = new ChordHandShapeSyncTests.Harness();
        var level = h.State.ActiveLevel!;
        var first = new FretAnchor(new FractionalPosition(1), regenerate ? 4 : 8, regenerate ? 5 : 4);
        var following = new FretAnchor(new FractionalPosition(5), regenerate ? 12 : 3);
        var retained = new FretAnchor(new FractionalPosition(9), 14);
        level.Anchors.Add(first);
        level.Anchors.Add(following);
        level.Anchors.Add(retained);
        var removed = regenerate ? first : following;
        h.Selection.SelectedAnchors.Add(removed);
        h.Selection.SelectedAnchors.Add(retained);
        var note = AddNote(h, 2, 0, 8, true);
        if (regenerate) AddNote(h, 3, 0, 4, false);
        var before = Anchors(level);

        h.Service.MoveSelectedStringPreservingPitch(1, 6);

        var after = Anchors(level);
        Assert.DoesNotContain(removed, level.Anchors);
        Assert.Same(retained, Assert.Single(h.Selection.SelectedAnchors));
        for (var i = 0; i < 2; i++)
        {
            h.History.Undo();
            Assert.Equal(before, Anchors(level));
            Assert.Contains(removed, level.Anchors);
            Assert.Contains(removed, h.Selection.SelectedAnchors);
            Assert.Contains(retained, h.Selection.SelectedAnchors);
            Assert.Equal(2, h.Selection.SelectedAnchors.Count);
            Assert.Equal(8, note.Fret);
            h.History.Redo();
            Assert.Equal(after, Anchors(level));
            Assert.DoesNotContain(removed, level.Anchors);
            Assert.Same(retained, Assert.Single(h.Selection.SelectedAnchors));
            Assert.All(h.Selection.SelectedAnchors, a => Assert.Contains(a, level.Anchors));
            Assert.Same(note, Assert.Single(h.Selection.SelectedNotes));
            Assert.Equal(3, note.Fret);
        }
    }

    [Fact]
    public void EqualFollowingAnchorIsRemovedButDifferentWidthAndFretStay()
    {
        var h = new ChordHandShapeSyncTests.Harness();
        AddNote(h, 2, 0, 8, true);
        var level = h.State.ActiveLevel!;
        level.Anchors.Add(new FretAnchor(new FractionalPosition(7), 9));
        level.Anchors.Add(new FretAnchor(new FractionalPosition(1), 8));
        level.Anchors.Add(new FretAnchor(new FractionalPosition(3), 3));
        level.Anchors.Add(new FretAnchor(new FractionalPosition(5), 3, 5));

        h.Service.MoveSelectedStringPreservingPitch(1, 6);

        Assert.Equal(new[] { 1, 5, 7 }, level.Anchors.OrderBy(a => a.Position).Select(a => a.Position.BeatId));
        Assert.Equal(new[] { 3, 3, 9 }, level.Anchors.OrderBy(a => a.Position).Select(a => a.Fret));
        Assert.Equal(new[] { 4, 5, 4 }, level.Anchors.OrderBy(a => a.Position).Select(a => a.Width));
    }

    [Fact]
    public void OneUndoAndRedoRestoreSoundsHandShapesAndEveryAnchorField()
    {
        var h = Line();
        var level = h.State.ActiveLevel!;
        level.Anchors.Add(new FretAnchor(new FractionalPosition(9), 1, 4));
        var before = Anchors(level);
        var templates = level.HandShapes.Select(s => s.TemplateId).ToArray();

        h.Service.MoveSelectedStringPreservingPitch(1, 6);

        var after = Anchors(level);
        Assert.Equal(new[] { 1, 2, 4, 1 }, level.Anchors.Select(a => a.Fret));
        var movedTemplates = level.HandShapes.Select(s => s.TemplateId).ToArray();
        Assert.NotEqual(templates[0], movedTemplates[0]);
        h.History.Undo();
        Assert.Equal(before, Anchors(level));
        Assert.Equal(templates, level.HandShapes.Select(s => s.TemplateId));
        Assert.Equal(new[] { 6, 7, 4, 6 }, level.Chords.Select(c => c.ChordNotes[0].Fret));
        Assert.False(h.History.CanUndo);
        h.History.Redo();
        Assert.Equal(after, Anchors(level));
        Assert.Equal(movedTemplates, level.HandShapes.Select(s => s.TemplateId));
        Assert.Equal(new[] { 1, 2, 4, 1 }, level.Chords.Select(c => c.ChordNotes[0].Fret));
        Assert.Equal(4, h.Selection.SelectedChords.Count);
    }

    [Fact]
    public void LevelWithoutAnchorsStillMovesAndCreatesNone()
    {
        var h = new ChordHandShapeSyncTests.Harness();
        var note = AddNote(h, 2, 0, 8, true);
        var other = new ChartLevel();
        other.Anchors.Add(new FretAnchor(new FractionalPosition(1), 8));
        h.Arrangement.Levels.Add(other);

        h.Service.MoveSelectedStringPreservingPitch(1, 6);

        Assert.Equal(3, note.Fret);
        Assert.Equal(1, note.StringIndex);
        Assert.Empty(h.State.ActiveLevel!.Anchors);
        Assert.Equal(8, Assert.Single(other.Anchors).Fret);
    }

    [Fact]
    public void InspectorFhpRulesReportNoNewFindingsOnTheMovedLine()
    {
        var h = Line();
        AddPhrase(h, 1, "verse", false);
        AddPhrase(h, 5, "chorus", true);
        var before = FhpIssues(h);
        Assert.Empty(before);

        h.Service.MoveSelectedStringPreservingPitch(1, 6);

        Assert.Empty(FhpIssues(h).Except(before));
    }

    [Fact]
    public void ChordWithDifferentStringDeltasRegeneratesItsSpan()
    {
        var h = new ChordHandShapeSyncTests.Harness();
        h.AddTemplate(0, (2, 8), (3, 8));
        var chord = h.AddChord(2, 0, (2, 8), (3, 8));
        h.Selection.SelectedChords.Add(chord);
        h.State.ActiveLevel!.Anchors.Add(new FretAnchor(new FractionalPosition(1), 8));

        h.Service.MoveSelectedStringPreservingPitch(1, 6);

        Assert.Equal(new[] { 3, 4 }, chord.ChordNotes.Select(n => n.Fret));
        Assert.Equal(3, Assert.Single(h.State.ActiveLevel.Anchors).Fret);
        Assert.Empty(FhpIssues(h));
    }

    private static ChordHandShapeSyncTests.Harness Line()
    {
        var h = new ChordHandShapeSyncTests.Harness();
        var frets = new[] { 6, 7, 4, 6 };
        for (var i = 0; i < frets.Length; i++)
        {
            h.AddTemplate(i, (0, frets[i]), (1, frets[i] + 2));
            h.Selection.SelectedChords.Add(h.AddChord(2 * i + 1, i, (0, frets[i]), (1, frets[i] + 2)));
            h.AddHandShape(2 * i + 1, 2 * i + 2, i);
            h.State.ActiveLevel!.Anchors.Add(new FretAnchor(new FractionalPosition(2 * i + 1), frets[i]));
        }

        return h;
    }

    private static void AddPhrase(ChordHandShapeSyncTests.Harness h, int beat, string name, bool imported)
    {
        if (imported)
        {
            var id = h.Arrangement.Phrases.Count;
            h.Arrangement.Phrases.Add(name);
            h.Arrangement.PhraseIterations.Add(new PhraseIteration { Position = new FractionalPosition(beat), PhraseId = id });
        }
        else
        {
            h.Arrangement.EventPoints.Add(new EventPoint { Position = new FractionalPosition(beat), Phrase = name });
        }
    }

    private static GuitarNote AddNote(ChordHandShapeSyncTests.Harness h, int beat, int lane, int fret, bool selected)
    {
        var note = new GuitarNote { Position = new FractionalPosition(beat), StringIndex = lane, Fret = fret };
        h.State.PlacedNotes.Add(note);
        if (selected) h.Selection.SelectedNotes.Add(note);
        return note;
    }

    private static (FractionalPosition Position, int Fret, int Width)[] Anchors(ChartLevel level) =>
        level.Anchors.OrderBy(a => a.Position).Select(a => (a.Position, a.Fret, a.Width)).ToArray();

    private static ArrangementIssueType[] FhpIssues(ChordHandShapeSyncTests.Harness h)
    {
        var context = ArrangementValidationContext.Build(h.Arrangement, h.State.ActiveProject!.Beats, null);
        var issues = new List<ArrangementIssue>();
        IArrangementRule[] rules = [new AnchorRule(), new CapoAnchorRangeRule(), new IndexFingerAnchorRule(),
            new HandShapeRule(), new PhraseAnchorRule()];
        foreach (var rule in rules) rule.Validate(context, issues);
        return issues.Select(i => i.Type).ToArray();
    }
}
