using Avalonia;
using DotCharter.UI.Input;

namespace DotCharter.Tests.UI.Input;

/// <summary>
///     Pure-geometry tests for the marquee (rubber-band) selection helper
///     (plan 6.5). Viewport setup: pixelsPerMs=1 and currentTimeMs=450 puts
///     viewStartMs at 0 (PlayheadX is 450 px), so a note at N ms sits at
///     screen x = N. 6 lanes over a 400 px viewport with 40 px margins gives
///     a lane height of 320/6 ≈ 53.33 px.
/// </summary>
public class MarqueeHitTesterTests
{
    private const double CurrentTimeMs = 450.0; // viewStartMs == 0
    private const double PixelsPerMs = 1.0;
    private const double Width = 1000.0;
    private const double Height = 400.0;
    private const double TopMargin = 40.0;
    private const double BottomMargin = 40.0;
    private const int StringCount = 6;

    private static List<BeatMarker> Beats(int count = 32, double bpm = 120)
    {
        var msPerBeat = 60000.0 / bpm; // 500 ms per beat
        var beats = new List<BeatMarker>();
        for (var i = 0; i < count; i++) beats.Add(new BeatMarker { Time = i * msPerBeat, Bpm = bpm });
        return beats;
    }

    private static double LaneY(int stringIndex) => ChartHitTester.GetLaneY(Height, TopMargin, BottomMargin, StringCount, stringIndex, leftyFlip: false);

    /// <summary>v0.34 lefty flip: forward and inverse lane mapping must agree
    /// in BOTH orientations, and the flip must actually mirror the order —
    /// this is the hit-test parity the renderer's LaneTopFor relies on.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LaneMapping_RoundTrips_ForBothOrientations(bool leftyFlip)
    {
        for (var s = 0; s < StringCount; s++)
        {
            var y = ChartHitTester.GetLaneY(Height, TopMargin, BottomMargin, StringCount, s, leftyFlip);
            Assert.Equal(s, ChartHitTester.GetLaneIndexAtY(y, Height, TopMargin, BottomMargin, StringCount, leftyFlip));
        }
    }

    [Fact]
    public void LeftyFlip_MirrorsTheVerticalStringOrder()
    {
        var normalTop = ChartHitTester.GetLaneY(Height, TopMargin, BottomMargin, StringCount, StringCount - 1, false);
        var leftyTop = ChartHitTester.GetLaneY(Height, TopMargin, BottomMargin, StringCount, 0, true);
        Assert.Equal(normalTop, leftyTop, 6);

        // A click on the lane that holds string 0 in normal orientation must
        // resolve to the HIGHEST string when flipped.
        var lowStringY = ChartHitTester.GetLaneY(Height, TopMargin, BottomMargin, StringCount, 0, false);
        Assert.Equal(StringCount - 1,
            ChartHitTester.GetLaneIndexAtY(lowStringY, Height, TopMargin, BottomMargin, StringCount, true));
    }

    private static GuitarNote Note(int beat, int stringIndex, int sustainBeats = 0)
    {
        return new GuitarNote
        {
            Position = new FractionalPosition(beat),
            StringIndex = stringIndex,
            Sustain = new FractionalPosition(sustainBeats)
        };
    }

    private static Chord ChordAt(int beat, params int[] strings)
    {
        var chord = new Chord { Position = new FractionalPosition(beat) };
        foreach (var s in strings)
            chord.ChordNotes.Add(new ChordNote { StringIndex = s });
        return chord;
    }

    private static List<GuitarNote> CollectNotes(Rect marquee, IList<GuitarNote> notes)
    {
        var results = new List<GuitarNote>();
        MarqueeHitTester.CollectNotes(marquee, notes, CurrentTimeMs, PixelsPerMs, Width, Height,
            TopMargin, BottomMargin, StringCount, Beats(), results, leftyFlip: false);
        return results;
    }

    private static List<Chord> CollectChords(Rect marquee, IList<Chord> chords)
    {
        var results = new List<Chord>();
        MarqueeHitTester.CollectChords(marquee, chords, CurrentTimeMs, PixelsPerMs, Width, Height,
            TopMargin, BottomMargin, StringCount, Beats(), results, leftyFlip: false);
        return results;
    }

    // ---- Rect primitives ---------------------------------------------------

    [Fact]
    public void Normalize_IsCornerOrderIndependent()
    {
        var a = new Point(10, 20);
        var b = new Point(110, 220);
        var expected = new Rect(10, 20, 100, 200);
        Assert.Equal(expected, MarqueeHitTester.Normalize(a, b));
        Assert.Equal(expected, MarqueeHitTester.Normalize(b, a));
        Assert.Equal(expected, MarqueeHitTester.Normalize(new Point(110, 20), new Point(10, 220)));
    }

    [Fact]
    public void Clamp_ConstrainsToViewport()
    {
        var r = MarqueeHitTester.Clamp(new Rect(-50, -10, 2000, 3000), 800, 400);
        Assert.Equal(new Rect(0, 0, 800, 400), r);
    }

    [Fact]
    public void IsDrag_RequiresThresholdOnEitherAxis()
    {
        var origin = new Point(100, 100);
        Assert.False(MarqueeHitTester.IsDrag(origin, new Point(102, 101)));
        Assert.True(MarqueeHitTester.IsDrag(origin, new Point(100 + MarqueeHitTester.DragThresholdPx, 100)));
        Assert.True(MarqueeHitTester.IsDrag(origin, new Point(100, 100 - MarqueeHitTester.DragThresholdPx)));
    }

    // ---- Notes -------------------------------------------------------------

    [Fact]
    public void CollectNotes_SelectsNoteWhoseHeadIntersects()
    {
        var note = Note(beat: 1, stringIndex: 0); // 500 ms → x=500
        var y = LaneY(0);
        var marquee = new Rect(480, y - 20, 60, 40);
        var hit = CollectNotes(marquee, [note]);
        Assert.Single(hit);
        Assert.Same(note, hit[0]);
    }

    [Fact]
    public void CollectNotes_PartialOverlapCounts()
    {
        // Head rect spans x 490..510 — a marquee ending at 495 still overlaps.
        var note = Note(beat: 1, stringIndex: 0);
        var y = LaneY(0);
        var marquee = new Rect(400, y - 4, 95, 8);
        Assert.Single(CollectNotes(marquee, [note]));
    }

    [Fact]
    public void CollectNotes_IgnoresNoteOutsideRect()
    {
        var note = Note(beat: 1, stringIndex: 0);
        var y = LaneY(0);
        var marquee = new Rect(600, y - 20, 50, 40); // right of the head (no sustain)
        Assert.Empty(CollectNotes(marquee, [note]));
    }

    [Fact]
    public void CollectNotes_RespectsLanes()
    {
        // Same x, different strings: only the lane the marquee covers is hit.
        var low = Note(beat: 1, stringIndex: 0);
        var high = Note(beat: 1, stringIndex: 5);
        var marquee = new Rect(480, LaneY(5) - 10, 60, 20);
        var hit = CollectNotes(marquee, [low, high]);
        Assert.Single(hit);
        Assert.Same(high, hit[0]);
    }

    [Fact]
    public void CollectNotes_SustainTailIsSelectable()
    {
        // 500 ms note sustained one beat → tail spans x 500..1000.
        var note = Note(beat: 1, stringIndex: 2, sustainBeats: 1);
        var y = LaneY(2);
        var marquee = new Rect(700, y - 2, 40, 4); // over the tail only
        Assert.Single(CollectNotes(marquee, [note]));
    }

    [Fact]
    public void CollectNotes_DegeneratePointRect_SelectsNothing()
    {
        var note = Note(beat: 1, stringIndex: 0);
        var y = LaneY(0);
        Assert.Empty(CollectNotes(new Rect(500, y, 0, 0), [note]));
    }

    [Fact]
    public void CollectNotes_EmptyOrNullList_IsSafe()
    {
        var results = new List<GuitarNote>();
        MarqueeHitTester.CollectNotes(new Rect(0, 0, 100, 100), null, CurrentTimeMs, PixelsPerMs,
            Width, Height, TopMargin, BottomMargin, StringCount, Beats(), results, leftyFlip: false);
        Assert.Empty(results);
        Assert.Empty(CollectNotes(new Rect(0, 0, 100, 100), []));
    }

    // ---- Chords ------------------------------------------------------------

    [Fact]
    public void CollectChords_SelectsChordWhenAnyChordNoteIntersects()
    {
        var chord = ChordAt(beat: 1, 1, 2); // strings 1+2 at x=500
        var marquee = new Rect(480, LaneY(2) - 10, 60, 20); // covers string 2's lane only
        var hit = CollectChords(marquee, [chord]);
        Assert.Single(hit);
        Assert.Same(chord, hit[0]);
    }

    [Fact]
    public void CollectChords_IgnoresChordOnUncoveredLanes()
    {
        var chord = ChordAt(beat: 1, 1, 2);
        var marquee = new Rect(480, LaneY(5) - 10, 60, 20); // string 5's lane — chord has none there
        Assert.Empty(CollectChords(marquee, [chord]));
    }

    [Fact]
    public void CollectChords_ChordAddedOnlyOnce()
    {
        var chord = ChordAt(beat: 1, 0, 1, 2, 3, 4, 5);
        var marquee = new Rect(0, 0, Width, Height); // covers everything
        Assert.Single(CollectChords(marquee, [chord]));
    }

    // ---- Mixed spans -------------------------------------------------------

    [Fact]
    public void CollectNotes_WideMarquee_SelectsAllInsideOnly()
    {
        var n1 = Note(beat: 1, stringIndex: 0); // x=500
        var n2 = Note(beat: 2, stringIndex: 0); // x=1000 — head 990..1010, clips viewport edge
        var n3 = Note(beat: 1, stringIndex: 3); // other lane
        var y = LaneY(0);
        var marquee = new Rect(450, y - 20, 500, 40); // x 450..950 in lane 0
        var hit = CollectNotes(marquee, [n1, n3, n2]);
        Assert.Single(hit);
        Assert.Same(n1, hit[0]);
    }
}
