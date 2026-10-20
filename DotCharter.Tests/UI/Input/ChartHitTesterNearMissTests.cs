using Avalonia;
using DotCharter.UI.Constants;
using DotCharter.UI.Input;

namespace DotCharter.Tests.UI.Input;

/// <summary>
///     A near miss must NOT read as empty highway. Falling through to "empty"
///     arms the marquee, and a marquee click with no drag CLEARS every
///     selection — so grazing a 20x16 note head cost the owner the whole
///     selection they had just built. The 2026-07-26 session log carries the
///     signature repeatedly: "Left-click empty highway at X; cleared
///     selections" followed within half a second by a successful
///     <c>HitChord=True</c> at the SAME snapped position (20:48:54, 20:53:58,
///     21:00:15). The sound was there both times.
///     <para>
///         Same viewport convention as <see cref="MarqueeHitTesterTests" />:
///         pixelsPerMs = 1 and viewAnchorMs = 450 put viewStartMs at 0, so a
///         sound at N ms sits at screen x = N; 6 lanes over 400 px, 40 px margins.
///     </para>
/// </summary>
public sealed class ChartHitTesterNearMissTests
{
    private const double ViewAnchorMs = 450.0; // pixelsPerMs=1 + PlayheadX 450 => viewStartMs 0
    private const double PixelsPerMs = 1.0;
    private const double Width = 1000.0;
    private const double Height = 400.0;
    private const double TopMargin = 40.0;
    private const double BottomMargin = 40.0;
    private const int StringCount = 6;

    private static List<BeatMarker> Beats(int count = 32, double bpm = 120)
        => [.. Enumerable.Range(0, count).Select(i => new BeatMarker { Time = i * (60000.0 / bpm), Bpm = bpm })];

    /// <summary>Lane centre y for a string, mirroring ChartHitTester.GetLaneY.</summary>
    private static double LaneY(int stringIndex)
    {
        var laneHeight = (Height - TopMargin - BottomMargin) / StringCount;
        return TopMargin + laneHeight * stringIndex + laneHeight / 2.0;
    }

    private static GuitarNote NoteAt(double ms, int stringIndex, int fret = 5) => new()
    {
        Position = TimelineMath.PositionFromMs(ms, Beats()),
        StringIndex = stringIndex,
        Fret = fret
    };

    private static GuitarNote? HitNote(Point p, IList<GuitarNote> notes)
        => ChartHitTester.HitTestNote(p, notes, ViewAnchorMs, PixelsPerMs, Width, Height,
            TopMargin, BottomMargin, StringCount, Beats(), false);

    private static Chord? HitChord(Point p, IList<Chord> chords)
        => ChartHitTester.HitTestChord(p, chords, ViewAnchorMs, PixelsPerMs, Width, Height,
            TopMargin, BottomMargin, StringCount, Beats(), false);

    [Fact]
    public void AClickInsideTheHead_StillHitsExactly()
    {
        var note = NoteAt(500, 2);
        var hit = HitNote(new Point(500, LaneY(2)), [note]);
        Assert.Same(note, hit);
    }

    [Fact]
    public void AClickJustOutsideTheHead_StillHitsTheNote()
    {
        var note = NoteAt(500, 2);
        // The head spans y in [laneY-8, laneY+8]; 12 px below its centre is a
        // 4 px miss — inside the 6 px tolerance.
        var hit = HitNote(new Point(500, LaneY(2) + 12), [note]);
        Assert.Same(note, hit);
    }

    [Fact]
    public void AClickWellClearOfEverything_StillReportsEmpty()
    {
        var note = NoteAt(500, 2);
        // Far enough that the marquee/clear behaviour must still be reachable —
        // otherwise clicking empty space could never deselect.
        var hit = HitNote(new Point(500, LaneY(2) + 40), [note]);
        Assert.Null(hit);
    }

    [Fact]
    public void TheToleranceIsNotSoWideThatTwoLanesOverlap()
    {
        var laneHeight = (Height - TopMargin - BottomMargin) / StringCount;
        var reach = ChartEditorConstants.NoteHeadHeight / 2 + ChartEditorConstants.NearMissHitTolerance;
        Assert.True(reach * 2 < laneHeight,
            $"A near-miss reach of {reach} px each way would let neighbouring lanes " +
            $"(height {laneHeight}) claim the same pixel, making the hit ambiguous.");
    }

    [Fact]
    public void AmongNearCandidates_TheClosestHeadWins()
    {
        // Two notes on the SAME lane, 40 ms apart. A click between them but
        // nearer the second must resolve to the second — never to whichever
        // came first in chart order.
        var first = NoteAt(500, 2);
        var second = NoteAt(540, 2);
        var hit = HitNote(new Point(535, LaneY(2) + 12), [first, second]);
        Assert.Same(second, hit);
    }

    [Fact]
    public void AClickJustOutsideAChordHead_StillHitsTheChord()
    {
        var chord = new Chord { Position = TimelineMath.PositionFromMs(500, Beats()) };
        chord.ChordNotes.Add(new ChordNote { StringIndex = 2, Fret = 5 });
        chord.ChordNotes.Add(new ChordNote { StringIndex = 3, Fret = 7 });

        var hit = HitChord(new Point(500, LaneY(2) + 12), [chord]);

        Assert.Same(chord, hit);
    }

    [Fact]
    public void AChordClickWellClearOfEverything_StillReportsEmpty()
    {
        var chord = new Chord { Position = TimelineMath.PositionFromMs(500, Beats()) };
        chord.ChordNotes.Add(new ChordNote { StringIndex = 2, Fret = 5 });

        Assert.Null(HitChord(new Point(500, LaneY(2) + 40), [chord]));
    }
}
