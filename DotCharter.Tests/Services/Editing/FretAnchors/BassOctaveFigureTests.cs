using System.Collections.ObjectModel;
using DotCharter.Services.Editing.FretAnchors;

namespace DotCharter.Tests.Services.Editing.FretAnchors;

/// <summary>
///     #501: on bass, a note that starts an octave figure (the same note two strings up and two frets higher follows
///     within the next two notes) gets its FHP ON the root, AT the root — even when the root alone was reachable from
///     the anchor in force. The three patterns are the issue's table; the rest must not trigger.
/// </summary>
public sealed class BassOctaveFigureTests
{
    private static GuitarNote Note(int beat, int stringIndex, int fret) =>
        new() { Position = new FractionalPosition(beat), StringIndex = stringIndex, Fret = fret };

    private static ChartLevel Level(params (int String, int Fret)[] notes)
    {
        var level = new ChartLevel();
        for (var i = 0; i < notes.Length; i++) level.Notes.Add(Note(i, notes[i].String, notes[i].Fret));
        return level;
    }

    private static string Bass(ChartLevel level) =>
        Describe(new FretAnchorGenerationService().GenerateAnchors(level, bassArrangement: true));

    private static string Describe(IEnumerable<FretAnchor> anchors) =>
        string.Join(" ", anchors.Select(a => $"{a.Position.BeatId}:{a.Fret}"));

    [Fact]
    public void OneThenTheOctaveFigureOnThree_AnchorsOnTheThree()
    {
        // E1 | E3 D5^ E3 D5^: the hand at 1 reaches the 3, but the figure is played index-on-root.
        var level = Level((0, 1), (0, 3), (2, 5), (0, 3), (2, 5));

        Assert.Equal("0:1 1:3", Bass(level));
    }

    [Fact]
    public void TwoThenTheOctaveFigureOnFiveOnTheAString_AnchorsOnTheFive()
    {
        // A2 | A5 G7^ A5 G7^ — the anchor goes on the root, never on the octave note.
        var level = Level((1, 2), (1, 5), (3, 7), (1, 5), (3, 7));

        Assert.Equal("0:2 1:5", Bass(level));
    }

    [Fact]
    public void AWalkUpIntoTheFigure_MovesTheHandAtTheRoot()
    {
        // E1 E2 | E3 D5^ E3 D5^: neither held at 1 until the octave forces a move, nor pulled back onto the 2.
        var level = Level((0, 1), (0, 2), (0, 3), (2, 5), (0, 3), (2, 5));

        Assert.Equal("0:1 2:3", Bass(level));
    }

    [Fact]
    public void TheHandOneFretBelowTheRoot_StillMovesOntoIt()
    {
        // E2 | E3 D5^ E3 D5^: the hand at 2 reaches the whole figure (3..5), and the plain walk held it there — the
        // commonest miss before #501 (145 of the 150 octave starts it got wrong were exactly one fret low).
        var level = Level((0, 2), (0, 3), (2, 5), (0, 3), (2, 5));

        Assert.Equal("0:2 1:3", Bass(level));
    }

    [Fact]
    public void TheOctaveTwoNotesLater_StillStartsTheFigure()
    {
        // E2 | E3 E4 D5^: the octave may come one note after the next (gap 2; the charters anchor 96.6% of those
        // on the root, and limiting the rule to gap 1 measured 0.1-0.2 pp worse).
        var level = Level((0, 2), (0, 3), (0, 4), (2, 5));

        Assert.Equal("0:2 1:3", Bass(level));
    }

    [Fact]
    public void AFigureBelowTheHand_IsPlantedAtTheRoot_NotDescendedOntoTheEarlierNotes()
    {
        // E5 E6 | E3 D5^: the plain walk pulls the opening anchor down to 3 (group descend); the figure is a new
        // hand position at the root instead.
        var level = Level((0, 5), (0, 6), (0, 3), (2, 5));

        Assert.Equal("0:5 2:3", Bass(level));
    }

    [Fact]
    public void AFigureTheHandIsAlreadyOn_AddsNoAnchor()
    {
        var level = Level((0, 3), (1, 4), (0, 3), (2, 5));

        Assert.Equal("0:3", Bass(level));
    }

    [Theory]
    // Two strings up, one fret higher: a major seventh, not an octave.
    [InlineData(2, 1)]
    // Three strings up, two frets higher: an eleventh.
    [InlineData(3, 2)]
    // The next string, two frets higher: a sixth.
    [InlineData(1, 2)]
    public void AStringSkipThatIsNotAnOctave_DoesNotTrigger(int stringsUp, int fretsUp)
    {
        // E2 | E3, then a note that is NOT the 3's octave: the hand at 2 already reaches both, so it stays.
        var level = Level((0, 2), (0, 3), (stringsUp, 3 + fretsUp), (0, 2));

        Assert.Equal("0:2", Bass(level));
    }

    [Fact]
    public void OnGuitar_TheSameFigure_DoesNotTrigger()
    {
        var level = Level((0, 1), (0, 3), (2, 5), (0, 3), (2, 5));

        var guitar = new FretAnchorGenerationService().GenerateAnchors(level);

        // Guitar keeps the plain walk: the hand moves when the octave note forces it (pulled down onto 3).
        Assert.Equal("0:1 2:3", Describe(guitar));
    }

    [Fact]
    public void ASlideLandingOnTheRoot_KeepsTheSlidesAnchor()
    {
        // E2 | E3 slides up to E5, landing where the figure E5 G7^ starts. The pitched slide MOVES the hand by its
        // own shift (anchor 2 + 2 = 4), and that landing is a fact the octave rule does not overrule.
        var level = new ChartLevel();
        level.Notes.Add(Note(0, 0, 2));
        level.Notes.Add(new GuitarNote
        {
            Position = new FractionalPosition(1), StringIndex = 0, Fret = 3, SlideTo = 5,
            Sustain = new FractionalPosition(1)
        });
        level.Notes.Add(Note(2, 0, 5));
        level.Notes.Add(Note(3, 2, 7));

        Assert.Equal("0:2 2:4", Bass(level));
    }

    // ─── Review fixes (#507) ────────────────────────────────────────────────

    [Theory]
    // A5 G7^ then G9: the 9 forces the hand up, and the bass pull-back would set
    // it on the octave note (7..9 fits one hand) — splitting the figure.
    [InlineData(new[] { 1, 5, 3, 7, 3, 9 }, "0:5 2:9")]
    // The same after a repeat of the figure: the repeat's octave stops it too.
    [InlineData(new[] { 1, 5, 3, 7, 1, 5, 3, 7, 3, 9 }, "0:5 4:9")]
    public void ThePullBack_NeverSplitsARootFromItsOctave(int[] stringFret, string expected)
    {
        var notes = new (int, int)[stringFret.Length / 2];
        for (var i = 0; i < notes.Length; i++) notes[i] = (stringFret[2 * i], stringFret[2 * i + 1]);

        Assert.Equal(expected, Bass(Level(notes)));
    }

    [Fact]
    // The vacuity keeper: the same climb through D7 (a fifth, not the octave)
    // is pulled back onto the 7 — the stop, not the shape, keeps it.
    public void ThePullBack_StillRuns_WhereNoFigureIsInTheWay() =>
        Assert.Equal("0:5 1:7", Bass(Level((1, 5), (2, 7), (3, 9))));

    private static Arrangement Bass4(params int[] offsets) => new()
    {
        ArrangementType = ArrangementType.Bass,
        StringCount = 4,
        Tuning = ["E", "A", "D", "G"],
        TuningPitches = new ObservableCollection<TuningOffset>(offsets.Select(o => new TuningOffset(o)))
    };

    [Theory]
    [InlineData(0, "0:2 1:3")]
    // Drop D: the low string is a tone down, so E3 + D5 is a ninth, not an octave.
    [InlineData(-2, "0:2")]
    public void TheOctave_IsReadThroughTheTuning(int lowStringOffset, string expected)
    {
        var level = Level((0, 2), (0, 3), (2, 5), (0, 3), (2, 5));
        var tuning = Bass4(lowStringOffset, 0, 0, 0);

        var anchors = new FretAnchorGenerationService().GenerateAnchors(level, bassArrangement: true, tuning: tuning);

        Assert.Equal(expected, Describe(anchors));
    }

    [Fact]
    public void InDropD_AFigureOnTheAString_IsStillAFigure()
    {
        var level = Level((1, 2), (1, 5), (3, 7), (1, 5), (3, 7));

        var anchors = new FretAnchorGenerationService().GenerateAnchors(level, bassArrangement: true,
            tuning: Bass4(-2, 0, 0, 0));

        Assert.Equal("0:2 1:5", Describe(anchors));
        Assert.Empty(BassOctaveFigures.FindStarts(Level((0, 2), (0, 3), (2, 5)).Notes, 0, Bass4(-2, 0, 0, 0)));
    }

    [Fact]
    public void FindStarts_CountsARepeatedFigureOnce()
    {
        var level = Level((0, 1), (0, 3), (2, 5), (0, 3), (2, 5), (1, 2), (3, 4));

        var starts = BassOctaveFigures.FindStarts(level.Notes, capo: 0);

        Assert.Equal([new FractionalPosition(1), new FractionalPosition(5)], starts.Order());
    }

    [Fact]
    public void FindStarts_IgnoresOpenRootsAndDoubleStops()
    {
        var level = new ChartLevel();
        level.Notes.Add(Note(0, 0, 0));
        level.Notes.Add(Note(1, 2, 2));
        level.Notes.Add(Note(2, 0, 3));
        level.Notes.Add(Note(2, 1, 3));
        level.Notes.Add(Note(3, 2, 5));

        Assert.Empty(BassOctaveFigures.FindStarts(level.Notes, capo: 0));
    }
}
