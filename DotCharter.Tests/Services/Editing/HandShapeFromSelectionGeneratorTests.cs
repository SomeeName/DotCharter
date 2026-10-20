using DotCharter.Services.Editing.Selection;
using DotCharter.Services.Editing.Tracks;
using DotCharter.Services.Editing;

namespace DotCharter.Tests.Services.Editing;

public class HandShapeFromSelectionGeneratorTests
{
    private static (TrackEditorContext track, List<GuitarNote> notes, List<Chord> chords) MakeTrack(
        IEnumerable<int> notePositions, IEnumerable<int> chordPositions)
    {
        var arr = new Arrangement { Name = "Lead" };
        var lvl = new ChartLevel();
        arr.Levels.Add(lvl);

        var notes = new List<GuitarNote>();
        foreach (var p in notePositions)
        {
            var n = new GuitarNote { Position = new FractionalPosition(p), Sustain = new FractionalPosition(2) };
            lvl.Notes.Add(n);
            notes.Add(n);
        }

        var chords = new List<Chord>();
        foreach (var p in chordPositions)
        {
            var c = new Chord { Position = new FractionalPosition(p) };
            lvl.Chords.Add(c);
            chords.Add(c);
        }

        var track = new TrackEditorContext(arr, new SelectionService());
        return (track, notes, chords);
    }

    [Fact]
    public void ComputeContiguousSelectedRuns_NoSelection_ReturnsEmpty()
    {
        var (track, _, _) = MakeTrack(new[] { 0, 5, 10 }, Array.Empty<int>());

        var runs = HandShapeFromSelectionGenerator.ComputeContiguousSelectedRuns(
            track, [], []);

        Assert.Empty(runs);
    }

    [Fact]
    public void ComputeContiguousSelectedRuns_AllSelected_OneRunSpansEverything()
    {
        var (track, notes, _) = MakeTrack(new[] { 0, 5, 10 }, Array.Empty<int>());

        var runs = HandShapeFromSelectionGenerator.ComputeContiguousSelectedRuns(
            track, notes.ToHashSet(), []);

        var run = Assert.Single(runs);
        Assert.Equal(new FractionalPosition(0), run.Start);
        Assert.Equal(new FractionalPosition(12), run.End);
    }

    [Fact]
    public void ComputeContiguousSelectedRuns_UnselectedGap_BreaksIntoMultipleRuns()
    {
        // Notes at 0, 5, 10, 15, 20. Select 0,5 then skip 10 then select 15,20.
        var (track, notes, _) = MakeTrack(new[] { 0, 5, 10, 15, 20 }, Array.Empty<int>());
        var selected = new HashSet<GuitarNote> { notes[0], notes[1], notes[3], notes[4] };

        var runs = HandShapeFromSelectionGenerator.ComputeContiguousSelectedRuns(
            track, selected, []);

        Assert.Equal(2, runs.Count);
        Assert.Equal(new FractionalPosition(0), runs[0].Start);
        Assert.Equal(new FractionalPosition(7), runs[0].End);
        Assert.Equal(new FractionalPosition(15), runs[1].Start);
        Assert.Equal(new FractionalPosition(22), runs[1].End);
    }

    [Fact]
    public void ComputeContiguousSelectedRuns_MixedNotesAndChords_OrderedByPosition()
    {
        var (track, notes, chords) = MakeTrack(new[] { 0, 10 }, new[] { 5, 15 });
        // Note(0), Chord(5), Note(10), Chord(15) — select all.
        var runs = HandShapeFromSelectionGenerator.ComputeContiguousSelectedRuns(
            track, notes.ToHashSet(), chords.ToHashSet());

        var run = Assert.Single(runs);
        Assert.Equal(new FractionalPosition(0), run.Start);
        // Note(10) end = 12; Chord(15) end = 15 (no chord notes => GetMaxSustain = 0
        // => EndPosition = 15) — and a run ending ON its last sound would not
        // CONTAIN it, so it reaches a beat past instead. See the tail-less cases
        // below: this row used to expect 15 and was the reported bug in miniature.
        Assert.Equal(new FractionalPosition(16), run.End);
    }

    /// <summary>
    ///     THE REPORTED BUG. A hand shape's span is HALF-OPEN, so a run ending on
    ///     its last sound's onset excludes it: select four tail-less notes,
    ///     generate, and the bar covers three. The grip is built from the same
    ///     half-open range, so the fourth note was missing from that too — and the
    ///     workaround people found was to give the notes tails first.
    /// </summary>
    [Fact]
    public void ARunOfTaillessNotes_ReachesPastItsLastNote()
    {
        var (track, notes, _) = MakeTrack(new[] { 0, 2, 4, 6 }, Array.Empty<int>());
        foreach (var note in notes) note.Sustain = FractionalPosition.Zero;

        var run = Assert.Single(HandShapeFromSelectionGenerator.ComputeContiguousSelectedRuns(
            track, notes.ToHashSet(), []));

        Assert.Equal(new FractionalPosition(0), run.Start);
        Assert.True(run.End > new FractionalPosition(6),
            $"the run ends at {run.End}, which does not contain the note at 6");
    }

    /// <summary>
    ///     …and it reaches the NEXT SOUND rather than an invented step, so
    ///     <c>ChartSpacingEnforcer</c> gets to apply the same minimum gap every
    ///     other shape gets.
    /// </summary>
    [Fact]
    public void ItReachesTheNextSound_WhenThereIsOne()
    {
        var (track, notes, _) = MakeTrack(new[] { 0, 2, 4, 6, 9 }, Array.Empty<int>());
        foreach (var note in notes) note.Sustain = FractionalPosition.Zero;
        var selected = new HashSet<GuitarNote> { notes[0], notes[1], notes[2], notes[3] };

        var run = Assert.Single(HandShapeFromSelectionGenerator.ComputeContiguousSelectedRuns(
            track, selected, []));

        Assert.Equal(new FractionalPosition(9), run.End);
    }

    [Fact]
    public void ARunWhoseLastNoteHasATail_IsLeftAlone()
    {
        // Already contains its last sound, so nothing is extended.
        var (track, notes, _) = MakeTrack(new[] { 0, 5 }, Array.Empty<int>());
        var run = Assert.Single(HandShapeFromSelectionGenerator.ComputeContiguousSelectedRuns(
            track, notes.ToHashSet(), []));

        Assert.Equal(new FractionalPosition(7), run.End); // note(5) + its 2-beat sustain
    }

    [Fact]
    public void ComputeContiguousSelectedRuns_SelectionWithUnselectedAtTail_FlushesLastRun()
    {
        var (track, notes, _) = MakeTrack(new[] { 0, 5, 10 }, Array.Empty<int>());
        var selected = new HashSet<GuitarNote> { notes[0], notes[1] };

        var runs = HandShapeFromSelectionGenerator.ComputeContiguousSelectedRuns(
            track, selected, []);

        var run = Assert.Single(runs);
        Assert.Equal(new FractionalPosition(0), run.Start);
        Assert.Equal(new FractionalPosition(7), run.End);
    }
}
