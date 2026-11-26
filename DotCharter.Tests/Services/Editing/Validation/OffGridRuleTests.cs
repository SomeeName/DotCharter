using DotCharter.Services.Editing.Validation.Rules;
using DotCharter.Tests.Services.Editing.HandShapes;

namespace DotCharter.Tests.Services.Editing.Validation;

/// <summary>
///     D40/D41 (<see cref="OffGridRule" />): a note or chord off the beat grid, and the snap
///     (<see cref="BeatGridSnap.Snap" />). The grid is 120 BPM, one beat 500 ms, so 1 ms is 1/500 of a beat.
///     Straight subdivisions to 1/16 and triplet ones to 1/24 are on the grid; swing is triplets.
/// </summary>
public sealed class OffGridRuleTests
{
    private static List<BeatMarker> Beats() =>
        [.. Enumerable.Range(0, 32).Select(i => new BeatMarker { Time = i * 500.0, Bpm = 120, IsMeasure = i % 4 == 0 })];

    /// <summary>Beat 4 plus <paramref name="num" />/<paramref name="den" /> of a beat.</summary>
    private static FractionalPosition At(long num, long den) => new(4, new Fraction(num, den));

    private static (Arrangement Arr, GuitarNote Note) WithNote(FractionalPosition position, double sustainBeats = 0)
    {
        var arr = ChordHandShapePlannerTests.Arr();
        var note = new GuitarNote
        {
            Position = position, StringIndex = 1, Fret = 5, Sustain = ChordHandShapePlannerTests.Pos(sustainBeats)
        };
        arr.Levels[0].Notes.Add(note);
        return (arr, note);
    }

    private static List<ArrangementIssue> Run(Arrangement arr)
    {
        var issues = new List<ArrangementIssue>();
        new OffGridRule().Validate(ArrangementValidationContext.Build(arr, Beats(), null), issues);
        return issues;
    }

    [Fact]
    public void TheCodesAreD40AndD41_Warnings()
    {
        Assert.Equal("D40", ArrangementIssueType.NoteNearlyOnGrid.GetCode());
        Assert.Equal("D41", ArrangementIssueType.NoteOffGrid.GetCode());
        Assert.Equal(ArrangementIssueSeverity.Warning, ArrangementIssueType.NoteNearlyOnGrid.GetSeverity());
        Assert.Equal(ArrangementIssueSeverity.Warning, ArrangementIssueType.NoteOffGrid.GetSeverity());
    }

    [Fact]
    public void TheRuleRunsByDefault()
    {
        var (arr, _) = WithNote(At(1, 500));
        var issues = new ArrangementValidationService(new FakeAppLogger()).Validate(arr, Beats(), null);
        Assert.Contains(issues, i => i.Type == ArrangementIssueType.NoteNearlyOnGrid);
    }

    [Theory]
    [InlineData(1, 1000, null)] // 0.5 ms: timestamp rounding
    [InlineData(1, 500, ArrangementIssueType.NoteNearlyOnGrid)] // exactly 1 ms
    [InlineData(3, 500, ArrangementIssueType.NoteNearlyOnGrid)] // exactly 3 ms
    [InlineData(7, 1000, ArrangementIssueType.NoteOffGrid)] // 3.5 ms
    [InlineData(499, 500, ArrangementIssueType.NoteNearlyOnGrid)] // 1 ms before the next beat
    [InlineData(1, 5, ArrangementIssueType.NoteOffGrid)] // a quintuplet, 4.2 ms from 5/24
    public void TheMillisecondBands(long num, long den, ArrangementIssueType? expected)
    {
        var issues = Run(WithNote(At(num, den)).Arr);
        Assert.Equal(expected, issues.SingleOrDefault()?.Type);
    }

    [Theory]
    [InlineData(1, 2)]
    [InlineData(3, 16)]
    [InlineData(1, 3)] // triplet
    [InlineData(2, 3)] // triplet-feel swung eighth
    [InlineData(1, 6)] // swung sixteenth
    [InlineData(5, 6)]
    [InlineData(7, 24)]
    [InlineData(15, 16)]
    public void StraightTripletAndSwungPositions_AreOnTheGrid(long num, long den) =>
        Assert.Empty(Run(WithNote(At(num, den)).Arr));

    [Fact]
    public void TwoNotesAtOnePosition_AreOneFinding()
    {
        var (arr, _) = WithNote(At(2, 500));
        arr.Levels[0].Notes.Add(new GuitarNote { Position = At(2, 500), StringIndex = 2, Fret = 7 });
        var issue = Assert.Single(Run(arr));
        Assert.Equal(2.0, (double)issue.MessageArgs![0], 3);
    }

    [Fact]
    public void TheSnap_MovesTheNoteAndWhatStartsWithIt_KeepsTheSustainEnd_InOneUndoStep()
    {
        var (arr, note) = WithNote(At(3, 500), sustainBeats: 1);
        var shape = new HandShape { Position = At(3, 500), EndPosition = new FractionalPosition(6) };
        var anchor = new FretAnchor(At(3, 500), 5);
        arr.Levels[0].HandShapes.Add(shape);
        arr.Levels[0].Anchors.Add(anchor);
        var end = note.EndPosition;
        var history = new HistoryService(new FakeAppLogger());

        var fix = BeatGridSnap.Snap(arr.Levels[0], Assert.Single(Run(arr)), Beats());
        Assert.NotNull(fix);
        history.Do(fix);

        Assert.Equal(new FractionalPosition(4), note.Position);
        Assert.Equal(end, note.EndPosition);
        Assert.Equal(new FractionalPosition(4), shape.Position);
        Assert.Equal(new FractionalPosition(4), anchor.Position);
        Assert.Empty(Run(arr));

        history.Undo();
        Assert.False(history.CanUndo);
        Assert.Equal(At(3, 500), note.Position);
        Assert.Equal(At(3, 500), shape.Position);
        Assert.Equal(At(3, 500), anchor.Position);
    }

    [Fact]
    public void TheSnap_MovesAChord()
    {
        var arr = ChordHandShapePlannerTests.Arr();
        var chord = new Chord { Position = At(499, 500) };
        chord.ChordNotes.Add(new ChordNote { StringIndex = 1, Fret = 5 });
        arr.Levels[0].Chords.Add(chord);

        BeatGridSnap.Snap(arr.Levels[0], Assert.Single(Run(arr)), Beats())!.Execute();

        Assert.Equal(new FractionalPosition(5), arr.Levels[0].Chords[0].Position);
    }

    [Fact]
    public void NoSnap_BeyondThreeMs_OrOntoAnOccupiedLine_OrAfterALinkNext()
    {
        var (far, _) = WithNote(At(7, 1000));
        Assert.Null(BeatGridSnap.Snap(far.Levels[0], Assert.Single(Run(far)), Beats()));

        var (taken, _) = WithNote(At(2, 500));
        taken.Levels[0].Notes.Add(new GuitarNote { Position = new FractionalPosition(4), StringIndex = 3, Fret = 2 });
        Assert.Null(BeatGridSnap.Snap(taken.Levels[0], Assert.Single(Run(taken)), Beats()));

        var (linked, _) = WithNote(At(2, 500));
        linked.Levels[0].Notes.Insert(0, new GuitarNote
        {
            Position = new FractionalPosition(3), StringIndex = 1, Fret = 5,
            Sustain = At(2, 500) - new FractionalPosition(3), LinkNext = true
        });
        Assert.Null(BeatGridSnap.Snap(linked.Levels[0], Assert.Single(Run(linked)), Beats()));
    }
}
