using DotCharter.Rendering.Data.Render;
using DotCharter.Rendering.Timeline2D;
using DotCharter.UI.Controls.Layers;
using DotCharter.UI.Controls.Layers.Helpers;

namespace DotCharter.Tests.Rendering;

/// <summary>
///     Vibrato and tremolo draw a squiggle by stepping the sustain in ~3 px /
///     ~6 px increments. That loop used to run over the WHOLE sustain regardless
///     of the viewport: a 5000 ms drone at 1 px/ms is ~1660 iterations x 3 quads
///     for ONE note, and the frame snapshot reaches ±5000 ms past the view, so
///     most of it was for geometry off screen entirely.
///     <para>
///         The claim these tests pin is behavioural — <b>squiggle cost scales with
///         the VISIBLE span, not the sustain length</b> — plus the two things a
///         clipping bug would break: the squiggle must still be drawn where it is
///         visible, and a sustain scrolled partly off the left edge must keep its
///         visible tail.
///     </para>
///     GPU-free: <see cref="TimelineBatcher" /> only stages floats.
/// </summary>
public sealed class SustainSquiggleCostTests
{
    private const double ViewWidth = 900.0;
    private const double Zoom = 1.0; // 1 px per ms

    [Fact]
    public void A_vibrato_sustain_longer_than_the_viewport_costs_no_more_than_one_that_just_fills_it()
    {
        // Both run off the right edge, so the visible squiggle span is identical.
        // Unclipped, the 20 s note costs 4x the 5 s one.
        var fills = Emit(sustainMs: 5_000, NoteFlags.Vibrato);
        var overflows = Emit(sustainMs: 20_000, NoteFlags.Vibrato);

        Assert.Equal(fills, overflows);
    }

    [Fact]
    public void A_tremolo_sustain_longer_than_the_viewport_costs_no_more_than_one_that_just_fills_it() => Assert.Equal(Emit(5_000, NoteFlags.Tremolo), Emit(20_000, NoteFlags.Tremolo));

    [Fact]
    public void Squiggle_cost_stays_bounded_by_the_viewport_width()
    {
        // Vibrato steps 3 px and emits 3 quads (18 vertices) per step, so the
        // ceiling is 6 vertices per visible pixel plus the sustain body. 12 per
        // pixel leaves generous headroom while still failing hard if the clamp
        // is removed (a 20 s sustain unclipped emits ~120 000 vertices here).
        var vertices = Emit(20_000, NoteFlags.Vibrato);

        Assert.True(vertices < 12 * ViewWidth,
            $"a 20 s vibrato sustain emitted {vertices} vertices for a {ViewWidth} px viewport — the squiggle loop is unclipped again");
    }

    [Fact]
    public void A_visible_vibrato_sustain_still_draws_its_squiggle()
    {
        // The clamp must not over-clip: on screen, vibrato costs strictly more
        // than the plain sustain body it decorates.
        var plain = Emit(2_000, NoteFlags.None);
        var vibrato = Emit(2_000, NoteFlags.Vibrato);

        Assert.True(vibrato > plain,
            "a visible vibrato sustain must emit more geometry than a plain one");
    }

    [Fact]
    public void A_visible_tremolo_sustain_still_draws_its_zigzag() => Assert.True(Emit(2_000, NoteFlags.Tremolo) > Emit(2_000, NoteFlags.None));

    [Fact]
    public void A_sustain_scrolled_off_the_left_edge_keeps_the_squiggle_over_its_visible_tail()
    {
        // Head at -4000 px, tail crossing the whole viewport. Clipping on the
        // note's own origin instead of the viewport would blank it.
        var vibrato = Emit(8_000, NoteFlags.Vibrato, timeMs: -4_000);
        var plain = Emit(8_000, NoteFlags.None, timeMs: -4_000);

        Assert.True(vibrato > plain,
            "the visible tail of a sustain that starts off screen must still be decorated");
        Assert.True(vibrato < 12 * ViewWidth,
            "the off-screen head must not be stepped over");
    }

    [Fact]
    public void A_vibrato_sustain_entirely_off_screen_emits_nothing() => Assert.Equal(0, Emit(1_000, NoteFlags.Vibrato, timeMs: 4_000));

    [Fact]
    public void Marks_LOD_collapses_a_sustain_to_a_single_density_strip()
    {
        // The Marks short-circuit predates the clamp and must survive it.
        var batcher = new TimelineBatcher();
        var rc = Context();
        rc.NoteLod = NoteLevelOfDetail.Marks;

        HighwaySustainRenderer.Draw(batcher, rc,
            new NoteDrawData(100, 5_000, 2, 5, 0, 0, 0, 0, NoteFlags.Vibrato, false, 0, 0), []);

        Assert.Equal(6, batcher.SolidVertexCount);
        Assert.Equal(0, batcher.GeneralVertexCount);
    }

    // ---- fixtures -----------------------------------------------------------

    private static int Emit(double sustainMs, NoteFlags flags, double timeMs = 100)
    {
        var batcher = new TimelineBatcher();
        HighwaySustainRenderer.Draw(batcher, Context(),
            new NoteDrawData(timeMs, sustainMs, 2, 5, 0, 0, 0, 0, flags, false, 0, 0), []);
        return batcher.SolidVertexCount + batcher.GeneralVertexCount;
    }

    private static ChartRenderContext Context() => new()
    {
        Width = ViewWidth,
        Height = 400,
        CurrentZoom = Zoom,
        ViewStartMs = 0,
        ViewEndMs = ViewWidth / Zoom,
        TopMargin = 20f,
        BottomMargin = 20f,
        LaneAreaHeight = 360f,
        LaneHeight = 60f,
        StringCount = 6,
        DrawLabels = true
    };
}
