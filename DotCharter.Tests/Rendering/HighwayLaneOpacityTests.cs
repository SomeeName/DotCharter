using DotCharter.Rendering.Data;
using DotCharter.Rendering.Data.Render;

namespace DotCharter.Tests.Rendering;

/// <summary>
///     The highway's per-fret lane opacity. This was inline array indexing
///     inside <c>HighwayRenderComponent.RenderFretboardLanes</c> with no lower
///     clamp on <c>anchor.Fret - 1</c>, so a fret-0 anchor threw out of the
///     frame; the 3D driver retried, hit its consecutive-failure ceiling and
///     killed the render loop, leaving the preview permanently dead for the
///     session with one log line.
///     <para>
///         A malformed anchor is a DATA problem the inspector flags — but the
///         renderer must degrade, never die, and three importers still admit
///         fret 0. These tests pin exactly that.
///     </para>
/// </summary>
public sealed class HighwayLaneOpacityTests
{
    private const double Horizon = 12_000;

    [Fact]
    public void With_no_anchors_every_lane_gets_the_base_wash()
    {
        var op = Fill([], 0, timeMs: 0);

        Assert.All(op.ToArray(), v => Assert.Equal(HighwayLaneOpacity.BaseOpacity, v));
    }

    [Fact]
    public void A_fret_zero_anchor_does_not_throw_and_lights_lane_zero()
    {
        // `Fret - 1` == -1. Unguarded this was an IndexOutOfRangeException
        // straight out of RenderFrame.
        var op = Fill([Anchor(0, fret: 0, width: 4)], 1, timeMs: 500);

        Assert.Equal(HighwayLaneOpacity.ActiveOpacity, op[0]);
        Assert.Equal(HighwayLaneOpacity.ActiveOpacity, op[3]);
        Assert.Equal(HighwayLaneOpacity.BaseOpacity, op[4]);
    }

    [Fact]
    public void An_anchor_past_the_last_lane_does_not_throw()
    {
        // Fret is a byte, so nothing stops an importer handing us 30. The old
        // code computed Math.Clamp(value, min: 29, max: 24) — and Clamp THROWS
        // when min > max, killing the loop the same way fret 0 did.
        var op = Fill([Anchor(0, fret: 30, width: 4)], 1, timeMs: 500);

        Assert.Equal(HighwayLaneOpacity.LaneCount, op.Length);
        Assert.Equal(HighwayLaneOpacity.ActiveOpacity, op[HighwayLaneOpacity.LaneCount - 1]);
    }

    [Fact]
    public void A_zero_width_anchor_still_lights_at_least_its_own_fret()
    {
        var op = Fill([Anchor(0, fret: 7, width: 0)], 1, timeMs: 500);

        Assert.Equal(HighwayLaneOpacity.ActiveOpacity, op[7]);
    }

    [Fact]
    public void The_active_anchor_lights_one_fret_below_its_span_as_a_lead_in()
    {
        var op = Fill([Anchor(0, fret: 5, width: 4)], 1, timeMs: 500);

        Assert.Equal(HighwayLaneOpacity.BaseOpacity, op[3]);
        Assert.Equal(HighwayLaneOpacity.ActiveOpacity, op[4]); // the lead-in
        Assert.Equal(HighwayLaneOpacity.ActiveOpacity, op[5]);
        Assert.Equal(HighwayLaneOpacity.ActiveOpacity, op[8]);
        Assert.Equal(HighwayLaneOpacity.BaseOpacity, op[9]);
    }

    [Fact]
    public void An_upcoming_anchor_inside_the_horizon_gets_the_dim_preview()
    {
        var op = Fill([Anchor(5_000, fret: 12, width: 4)], 1, timeMs: 0);

        Assert.Equal(HighwayLaneOpacity.UpcomingOpacity, op[12]);
        Assert.Equal(HighwayLaneOpacity.UpcomingOpacity, op[15]);
        // No lead-in fret for a preview — only the active anchor gets one.
        Assert.Equal(HighwayLaneOpacity.BaseOpacity, op[11]);
        Assert.Equal(HighwayLaneOpacity.BaseOpacity, op[16]);
    }

    [Fact]
    public void An_anchor_beyond_the_horizon_is_not_previewed()
    {
        // The horizon is the app's ONE look-ahead notion and reaches 12 s.
        var op = Fill([Anchor(11_000, fret: 12, width: 4)], 1, timeMs: 0, horizonMs: 6_000);

        Assert.Equal(HighwayLaneOpacity.BaseOpacity, op[12]);
    }

    [Fact]
    public void The_active_anchor_wins_over_a_preview_on_the_same_fret()
    {
        var op = Fill([Anchor(0, fret: 5, width: 4), Anchor(3_000, fret: 5, width: 4)], 2, timeMs: 500);

        Assert.Equal(HighwayLaneOpacity.ActiveOpacity, op[5]);
    }

    [Fact]
    public void The_latest_anchor_at_or_before_the_playhead_is_the_active_one()
    {
        var op = Fill([Anchor(0, fret: 1, width: 4), Anchor(1_000, fret: 12, width: 3)], 2, timeMs: 2_000);

        Assert.Equal(HighwayLaneOpacity.ActiveOpacity, op[12]);
        Assert.Equal(HighwayLaneOpacity.BaseOpacity, op[1]);
    }

    [Fact]
    public void Only_the_first_anchorCount_entries_are_read()
    {
        // Snapshot arrays are pooled and oversized; reading past the count
        // would light lanes from a previous frame's leftovers.
        var anchors = new[] { Anchor(0, 5, 4), Anchor(0, 20, 4) };

        var op = Fill(anchors, 1, timeMs: 500);

        Assert.Equal(HighwayLaneOpacity.BaseOpacity, op[20]);
    }

    private static int[] Fill(RenderAnchor[] anchors, int count, double timeMs, double horizonMs = Horizon)
    {
        var op = new int[HighwayLaneOpacity.LaneCount];
        HighwayLaneOpacity.Fill(anchors, count, timeMs, horizonMs, op);
        return op;
    }

    private static RenderAnchor Anchor(double timeMs, byte fret, byte width) =>
        new() { Time = timeMs, Fret = fret, Width = width };
}
