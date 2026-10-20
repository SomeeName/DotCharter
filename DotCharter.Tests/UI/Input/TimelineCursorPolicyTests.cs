using Avalonia.Input;
using DotCharter.UI.Input;

namespace DotCharter.Tests.UI.Input;

/// <summary>
///     The zone-following timeline cursor (P1-3/P1-4; the grid wore a
///     permanent crosshair before, including over the event rows, the editing
///     bands and the ruler). The policy reads the SAME <see cref="TimelineZones" />
///     the ToolCoordinator routes by, so the cursor cannot promise a different
///     gesture than the click delivers — and it deliberately shows NO resize
///     cursor on a note's sustain edge, because no sustain edge-drag gesture
///     exists (sustain is Alt+wheel / the nudge keys).
/// </summary>
public sealed class TimelineCursorPolicyTests
{
    private const double H = 600;
    private const double Top = 80;
    private const double Bottom = 58;

    [Fact]
    public void TheLanesGetTheCrosshair() => Assert.Equal(StandardCursorType.Cross, TimelineCursorPolicy.For(H / 2, H, Top, Bottom, panning: false));

    [Fact]
    public void TheEventRowsAndTheRulerGetTheArrow()
    {
        Assert.Equal(StandardCursorType.Arrow, TimelineCursorPolicy.For(Top - 1, H, Top, Bottom, panning: false));
        Assert.Equal(StandardCursorType.Arrow, TimelineCursorPolicy.For(H - 5, H, Top, Bottom, panning: false));
    }

    [Fact]
    public void TheLengthEditingBandsGetSizeWestEast()
    {
        var laneBottom = H - Bottom;
        Assert.Equal(StandardCursorType.SizeWestEast,
            TimelineCursorPolicy.For(laneBottom + 8, H, Top, Bottom, panning: false)); // hand-shape band
        Assert.Equal(StandardCursorType.SizeWestEast,
            TimelineCursorPolicy.For(laneBottom + 25, H, Top, Bottom, panning: false)); // FHP band
    }

    [Fact]
    public void PanningOverridesEverything()
    {
        Assert.Equal(StandardCursorType.SizeAll, TimelineCursorPolicy.For(H / 2, H, Top, Bottom, panning: true));
        Assert.Equal(StandardCursorType.SizeAll, TimelineCursorPolicy.For(H - 5, H, Top, Bottom, panning: true));
    }

    /// <summary>The cursor and the routing must agree band-for-band: everywhere
    /// the policy says size-we, the coordinator routes to a length-editing
    /// tool zone, and vice versa.</summary>
    [Fact]
    public void TheCursorAndTheRouting_AgreeOnEveryPixel()
    {
        for (var y = 0.0; y <= H; y += 0.5)
        {
            var cursor = TimelineCursorPolicy.For(y, H, Top, Bottom, panning: false);
            var inEditingBand = TimelineZones.IsHandShapeBand(y, H, Bottom) || TimelineZones.IsFhpBand(y, H, Bottom);
            var inRuler = TimelineZones.IsTimeRuler(y, H);

            if (inRuler)
                Assert.Equal(StandardCursorType.Arrow, cursor);
            else if (inEditingBand)
                Assert.Equal(StandardCursorType.SizeWestEast, cursor);
            else if (y >= Top)
                Assert.Equal(StandardCursorType.Cross, cursor);
        }
    }

    // (No cache test: constructing an Avalonia Cursor needs ICursorFactory,
    // which the default gate has no platform for. The cache is a ??= array.)

    [Fact]
    public void PanStarts_OnMiddle_OrAltLeft_AndNothingElse()
    {
        Assert.True(TimelinePanGesture.IsPanStart(middlePressed: true, leftPressed: false, alt: false));
        Assert.True(TimelinePanGesture.IsPanStart(middlePressed: false, leftPressed: true, alt: true));
        Assert.False(TimelinePanGesture.IsPanStart(middlePressed: false, leftPressed: true, alt: false));
        Assert.False(TimelinePanGesture.IsPanStart(middlePressed: false, leftPressed: false, alt: true));
    }
}
