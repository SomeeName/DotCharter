using DotCharter.Services.Editing.Anchors;

namespace DotCharter.Tests.Services.Editing.Anchors;

/// <summary>
///     A new hand position opens on the fret its own notes need.
///     <para>
///         Pins the replacement for "copy the previous anchor's fret", which in
///         the owner's 2026-07-28 session was corrected within 3.4 seconds all six
///         times it fired — five of them on the fret.
///     </para>
/// </summary>
public sealed class AnchorFretSuggesterTests
{
    private static GuitarNote Note(int beat, int fret, int stringIndex = 0) => new()
    {
        Position = new FractionalPosition(beat), Fret = fret, StringIndex = stringIndex
    };

    private static Chord ChordAt(int beat, params int[] frets)
    {
        var chord = new Chord { Position = new FractionalPosition(beat) };
        for (var i = 0; i < frets.Length; i++)
            chord.ChordNotes.Add(new ChordNote { StringIndex = i, Fret = frets[i] });
        return chord;
    }

    private static FretAnchor Anchor(int beat, int fret = 1) => new()
    {
        Position = new FractionalPosition(beat), Fret = fret, Width = 4
    };

    [Fact]
    public void TheLowestFrettedNoteInTheSpanSetsTheFret()
    {
        var fret = AnchorFretSuggester.Suggest(
            new FractionalPosition(8),
            [Note(8, 9), Note(9, 7), Note(10, 12)],
            [], []);

        Assert.Equal(7, fret);
    }

    [Fact]
    public void AChordsLowestFrettedNoteCounts()
    {
        var fret = AnchorFretSuggester.Suggest(
            new FractionalPosition(8), [], [ChordAt(8, 14, 15, 14)], []);

        Assert.Equal(14, fret);
    }

    /// <summary>The span ENDS at the next anchor — notes past it belong to a
    /// different hand position and must not pull this one down.</summary>
    [Fact]
    public void NotesPastTheNextAnchorAreIgnored()
    {
        var fret = AnchorFretSuggester.Suggest(
            new FractionalPosition(8),
            [Note(9, 12), Note(20, 2)],
            [],
            [Anchor(8), Anchor(16)]);

        Assert.Equal(12, fret);
    }

    [Fact]
    public void NotesBeforeThePositionAreIgnored()
    {
        var fret = AnchorFretSuggester.Suggest(
            new FractionalPosition(8), [Note(2, 3), Note(9, 12)], [], []);

        Assert.Equal(12, fret);
    }

    /// <summary>An open string never constrains the fret hand.</summary>
    [Fact]
    public void OpenStringsDoNotPullTheAnchorDown()
    {
        var fret = AnchorFretSuggester.Suggest(
            new FractionalPosition(8), [Note(8, 0), Note(9, 10)], [], []);

        Assert.Equal(10, fret);
    }

    /// <summary>
    ///     A fret at or below the capo SOUNDS as the open string, so it says
    ///     nothing about the hand — and the anchor itself may not sit on or below
    ///     the capo (inspector rule L02).
    /// </summary>
    [Fact]
    public void FretsAtOrBelowTheCapoAreOpenAndTheFloorIsCapoPlusOne()
    {
        var fret = AnchorFretSuggester.Suggest(
            new FractionalPosition(8), [Note(8, 2), Note(9, 5)], [], [], capo: 2);

        Assert.Equal(5, fret);
    }

    [Fact]
    public void WithNothingFrettedTheCapoFloorStillHolds()
    {
        var fret = AnchorFretSuggester.Suggest(
            new FractionalPosition(8), [Note(8, 0)], [], [], capo: 3);

        Assert.Equal(4, fret);
    }

    /// <summary>
    ///     An empty span has nothing to derive from, so continuing the previous
    ///     hand position is the least surprising answer — and is what the user
    ///     got before this existed.
    /// </summary>
    [Fact]
    public void AnEmptySpanFallsBackToThePreviousAnchorsFret()
    {
        var fret = AnchorFretSuggester.Suggest(
            new FractionalPosition(8), [], [], [], previousFret: 9);

        Assert.Equal(9, fret);
    }

    [Fact]
    public void AnEmptySpanWithNoPreviousAnchorFallsBackToFretOne()
    {
        Assert.Equal(AnchorFretSuggester.DefaultFret,
            AnchorFretSuggester.Suggest(new FractionalPosition(8), [], [], []));
    }

    /// <summary>A note exactly ON the anchor is governed by it.</summary>
    [Fact]
    public void ANoteExactlyOnThePositionCounts()
    {
        var fret = AnchorFretSuggester.Suggest(
            new FractionalPosition(8), [Note(8, 6)], [], []);

        Assert.Equal(6, fret);
    }

    /// <summary>
    ///     The real regression: the previous anchor says 12, the notes here say 5.
    ///     Before, the dialog opened on 12 and the charter changed it by hand.
    /// </summary>
    [Fact]
    public void TheNotesWinOverThePreviousAnchor()
    {
        var fret = AnchorFretSuggester.Suggest(
            new FractionalPosition(70),
            [Note(70, 5), Note(71, 7)],
            [], [Anchor(60, 12)],
            previousFret: 12);

        Assert.Equal(5, fret);
    }

    /// <summary>
    ///     The <c>capo + 1</c> floor is a floor on the NECK. The arrangement
    ///     dialog's capo spinner goes to 24, and an unclamped floor put a brand
    ///     new anchor on fret 25 — past the end of a 24-fret neck, which is
    ///     exactly the out-of-range value the two anchor-GENERATING services
    ///     clamp against (<c>Math.Clamp(capo + 1, 1, MaxFret)</c>).
    /// </summary>
    [Theory]
    [InlineData(23)]
    [InlineData(24)]
    public void TheCapoFloorNeverLeavesTheNeck(int capo)
    {
        var fret = AnchorFretSuggester.Suggest(
            new FractionalPosition(8), [], [], [], capo);

        Assert.InRange(fret, 1, 24);
    }
}
