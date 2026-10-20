using DotCharter.Core.Domain.Vocals;
using DotCharter.UI.Input.Tools.EventInteractionGestures;

namespace DotCharter.Tests.UI.Input.Tools.EventInteractionGestures;

/// <summary>
///     The vocal twin of <see cref="BeatDragBoundsTests" />: a lyric may never
///     be dragged (or wheel-resized) past its neighbouring syllable, and since
///     no spacing pass covers vocals on save, the gesture-side math here is the
///     only enforcement. Pure — no view models, no app.
/// </summary>
public sealed class LyricDragBoundsTests
{
    /// <summary>120 BPM grid: one beat every 500 ms.</summary>
    private static List<BeatMarker> Beats(int count = 32) =>
        [.. Enumerable.Range(0, count).Select(i => new BeatMarker { Time = i * 500.0, Bpm = 120 })];

    private static LyricEvent LyricAt(int beat, string text = "la") => new()
    {
        Position = new FractionalPosition(beat),
        EndPosition = new FractionalPosition(beat),
        Text = text
    };

    private static LyricPhrase Phrase(params LyricEvent[] lyrics)
    {
        var phrase = new LyricPhrase();
        foreach (var l in lyrics) phrase.Lyrics.Add(l);
        if (lyrics.Length > 0)
        {
            phrase.Position = lyrics[0].Position;
            phrase.EndPosition = lyrics[^1].EndPosition;
        }

        return phrase;
    }

    // ---- FindNeighbors ------------------------------------------------------

    [Fact]
    public void FindNeighbors_InsideAPhrase_ReturnsBothSiblings()
    {
        var a = LyricAt(1);
        var b = LyricAt(2);
        var c = LyricAt(3);
        var phrases = new List<LyricPhrase> { Phrase(a, b, c) };

        var (prev, next) = LyricDragBounds.FindNeighbors(phrases, b);

        Assert.Same(a, prev);
        Assert.Same(c, next);
    }

    /// <summary>The neighbour may live in the ADJACENT PHRASE — the last
    /// syllable of one phrase bounds against the next phrase's first.</summary>
    [Fact]
    public void FindNeighbors_AtAPhraseBoundary_CrossesIntoTheAdjacentPhrases()
    {
        var a = LyricAt(1);
        var b = LyricAt(2);
        var c = LyricAt(4);
        var d = LyricAt(5);
        var phrases = new List<LyricPhrase> { Phrase(a, b), Phrase(c, d) };

        var (prevOfC, nextOfC) = LyricDragBounds.FindNeighbors(phrases, c);
        Assert.Same(b, prevOfC);
        Assert.Same(d, nextOfC);

        var (prevOfB, nextOfB) = LyricDragBounds.FindNeighbors(phrases, b);
        Assert.Same(a, prevOfB);
        Assert.Same(c, nextOfB);
    }

    [Fact]
    public void FindNeighbors_SkipsEmptyPhrasesBetween()
    {
        var a = LyricAt(1);
        var b = LyricAt(4);
        var phrases = new List<LyricPhrase> { Phrase(a), Phrase(), Phrase(b) };

        var (prev, next) = LyricDragBounds.FindNeighbors(phrases, b);

        Assert.Same(a, prev);
        Assert.Null(next);
    }

    [Fact]
    public void FindNeighbors_FirstAndLastSyllable_HaveOpenSides()
    {
        var a = LyricAt(1);
        var b = LyricAt(2);
        var phrases = new List<LyricPhrase> { Phrase(a, b) };

        Assert.Equal((null, b), LyricDragBounds.FindNeighbors(phrases, a));
        Assert.Equal((a, null), LyricDragBounds.FindNeighbors(phrases, b));
    }

    [Fact]
    public void FindNeighbors_UnknownTarget_ReturnsNothing()
    {
        var phrases = new List<LyricPhrase> { Phrase(LyricAt(1)) };

        var (prev, next) = LyricDragBounds.FindNeighbors(phrases, LyricAt(9));
        Assert.Null(prev);
        Assert.Null(next);
    }

    // ---- ComputeStartBounds / Clamp -----------------------------------------

    [Fact]
    public void ComputeStartBounds_BoundsBetweenTheNeighbourStartsWithTheGaps()
    {
        var (lower, upper) = LyricDragBounds.ComputeStartBounds(
            prevStartMs: 1000.0, gapAfterPrevMs: 30.0, nextStartMs: 2000.0, gapBeforeNextMs: 40.0);

        Assert.Equal(1030.0, lower, 6);
        Assert.Equal(1960.0, upper, 6);
    }

    [Fact]
    public void ComputeStartBounds_AnOpenSideIsInfinite()
    {
        var (lower, upper) = LyricDragBounds.ComputeStartBounds(null, 0.0, 2000.0, 30.0);
        Assert.Equal(double.NegativeInfinity, lower);
        Assert.Equal(1970.0, upper, 6);

        (lower, upper) = LyricDragBounds.ComputeStartBounds(1000.0, 30.0, null, 0.0);
        Assert.Equal(1030.0, lower, 6);
        Assert.Equal(double.PositiveInfinity, upper);
    }

    /// <summary>Neighbours already closer than two gaps: the window comes back
    /// INVERTED, which the gesture answers by not moving the pill at all.</summary>
    [Fact]
    public void ComputeStartBounds_TightNeighbours_ReturnAnInvertedWindow()
    {
        var (lower, upper) = LyricDragBounds.ComputeStartBounds(1000.0, 50.0, 1040.0, 50.0);
        Assert.True(upper < lower);
    }

    [Fact]
    public void Clamp_PinsToEitherEdgeAndPassesInteriorValuesThrough()
    {
        Assert.Equal(10.0, LyricDragBounds.Clamp(5.0, 10.0, 20.0));
        Assert.Equal(20.0, LyricDragBounds.Clamp(25.0, 10.0, 20.0));
        Assert.Equal(15.0, LyricDragBounds.Clamp(15.0, 10.0, 20.0));
    }

    // ---- MinGapMsAt ---------------------------------------------------------

    /// <summary>Product default (measure fraction, 4 beats over 64) is 1/16
    /// beat = 31.25 ms on the 120 BPM grid — the same figure note spacing uses.</summary>
    [Fact]
    public void MinGapMs_FollowsTheMeasureFractionDefault()
    {
        var gap = LyricDragBounds.MinGapMsAt(new FractionalPosition(2), Beats(), new AppConfig());
        Assert.Equal(31.25, gap, 2);
    }

    [Fact]
    public void MinGapMs_FollowsTheMillisecondsMode()
    {
        var config = new AppConfig
        {
            MinNoteDistanceMode = MinNoteDistanceMode.Milliseconds,
            MinNoteDistanceMs = 50.0
        };

        // The ms distance is quantized onto the fractional grid by
        // GetMillisecondsDistance (PositionFromMs), so the round-trip lands
        // within a grid tick of the configured figure, not exactly on it.
        var gap = LyricDragBounds.MinGapMsAt(new FractionalPosition(2), Beats(), config);
        Assert.Equal(50.0, gap, 1.0);
    }

    [Fact]
    public void MinGapMs_IsZeroWithoutABeatGrid() => Assert.Equal(0.0, LyricDragBounds.MinGapMsAt(new FractionalPosition(2), [], new AppConfig()));

    // ---- the call sites (source lints) --------------------------------------

    /// <summary>
    ///     The gestures take a <c>MainWindowViewModel</c> / <c>ToolContext</c>,
    ///     which needs the whole app booted — so the CALL SITES are pinned by
    ///     source lint, the same way <c>ResizeLyricGestureMergeTests</c> pins
    ///     the wheel's merge verb. Without these, deleting the clamp compiles,
    ///     every pure test above stays green, and the unclamped drag ships back.
    /// </summary>
    [Fact]
    public void TheDragGesture_ClampsThroughLyricDragBounds_AfterTheSnap()
    {
        var source = UiSourceTree.ReadAllText(UiSourceTree.Resolve(Path.Combine(TestPaths.RepoRoot(),
            "DotCharter.UI", "Input", "Tools", "EventInteractionGestures", "LyricDragGesture.cs")));

        var snap = source.IndexOf("SnapLyricStartMs(", StringComparison.Ordinal);
        Assert.True(snap >= 0, "The lyric drag must still snap through SnapLyricStartMs.");

        var neighbors = source.IndexOf("LyricDragBounds.FindNeighbors(", snap, StringComparison.Ordinal);
        Assert.True(neighbors > snap, "The drag must resolve its neighbours AFTER the snap.");
        Assert.Contains("LyricDragBounds.ComputeStartBounds(", source, StringComparison.Ordinal);
        Assert.Contains("LyricDragBounds.Clamp(", source, StringComparison.Ordinal);
    }

    [Fact]
    public void TheWheelResize_CapsTheEndBeforeTheNextSyllable()
    {
        var source = UiSourceTree.ReadAllText(UiSourceTree.Resolve(Path.Combine(TestPaths.RepoRoot(),
            "DotCharter.UI", "Input", "TimelineWheelAdjuster.cs")));

        var start = source.IndexOf("TryAdjustLyricSustain(", StringComparison.Ordinal);
        Assert.True(start >= 0, "TryAdjustLyricSustain is the lyric wheel rule; it must still exist.");

        var end = source.IndexOf("public WheelAdjustmentResult TryAdjustHandShapeLength", start,
            StringComparison.Ordinal);
        var body = end > start ? source[start..end] : source[start..];
        Assert.Contains("LyricDragBounds.FindNeighbors(", body, StringComparison.Ordinal);
        Assert.Contains("ChartSpacingEnforcer.GetMinDistance(", body, StringComparison.Ordinal);
    }
}
