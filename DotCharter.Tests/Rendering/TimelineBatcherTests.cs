using Avalonia.Media;
using DotCharter.Rendering.Timeline2D;

namespace DotCharter.Tests.Rendering;

/// <summary>
///     <see cref="TimelineBatcher" /> is the CPU-side geometry foundation the whole
///     2D timeline sits on — every layer emits through it and the composed renderer
///     replays nothing but its output. It had no test at all: the golden-frame
///     suites that would have covered it are GPU-gated and skip by default, so the
///     default gate covered zero batcher geometry.
///     <para>
///         No GPU, no display, no Avalonia app — the batcher only writes floats into
///         managed arrays.
///     </para>
/// </summary>
public sealed class TimelineBatcherTests
{
    private static readonly Color Opaque = Color.FromArgb(255, 10, 20, 30);

    // ---- per-primitive vertex counts ---------------------------------------
    // These are a CONTRACT, not an implementation detail: Timeline2DFrameResources
    // uploads exactly VertexCount x floatsPerVertex and the draw commands index by
    // vertex, so a primitive that emits a different count than it reports corrupts
    // every later run in the frame.

    [Fact]
    public void AddRect_emits_one_solid_quad()
    {
        var b = new TimelineBatcher();

        b.AddRect(0, 0, 10, 10, Opaque);

        Assert.Equal(6, b.SolidVertexCount);
        Assert.Equal(0, b.GeneralVertexCount);
    }

    [Fact]
    public void AddThickLine_emits_a_core_quad_plus_two_AA_fade_quads()
    {
        var b = new TimelineBatcher();

        b.AddThickLine(0, 0, 100, 0, 2f, Opaque);

        Assert.Equal(18, b.SolidVertexCount);
        Assert.Equal(0, b.GeneralVertexCount);
    }

    [Fact]
    public void AddThickLine_of_zero_length_emits_nothing()
    {
        var b = new TimelineBatcher();

        b.AddThickLine(5, 5, 5, 5, 2f, Opaque);

        Assert.Equal(0, b.SolidVertexCount);
    }

    [Fact]
    public void AddTriangle_emits_three_solid_vertices()
    {
        var b = new TimelineBatcher();

        b.AddTriangle(0, 0, 10, 0, 0, 10, Opaque);

        Assert.Equal(3, b.SolidVertexCount);
    }

    [Fact]
    public void AddSprite_goes_to_the_general_buffer()
    {
        var b = new TimelineBatcher();

        b.AddSprite(0, 0, 8, 8, Opaque, type: 1f);

        Assert.Equal(0, b.SolidVertexCount);
        Assert.Equal(6, b.GeneralVertexCount);
    }

    [Fact]
    public void AddQuadRaw_routes_by_type_solid_for_zero_general_otherwise()
    {
        var solid = new TimelineBatcher();
        solid.AddQuadRaw(0, 0, 0, 1, 1, 1, 1, 0, 1f, 1f, 1f, 1f, 0f, 0f, 0f, 1f, 1f);
        Assert.Equal(6, solid.SolidVertexCount);
        Assert.Equal(0, solid.GeneralVertexCount);

        var general = new TimelineBatcher();
        general.AddQuadRaw(0, 0, 0, 1, 1, 1, 1, 0, 1f, 1f, 1f, 1f, 2f, 0f, 0f, 1f, 1f);
        Assert.Equal(0, general.SolidVertexCount);
        Assert.Equal(6, general.GeneralVertexCount);
    }

    [Fact]
    public void AddRoundRect_with_no_radius_degrades_to_a_plain_rect()
    {
        var b = new TimelineBatcher();

        b.AddRoundRect(0, 0, 20, 10, 0f, Opaque);

        Assert.Equal(6, b.SolidVertexCount);
    }

    [Fact]
    public void AddRoundRect_emits_three_rects_plus_four_corner_fans()
    {
        var b = new TimelineBatcher();

        b.AddRoundRect(0, 0, 40, 20, 5f, Opaque);

        // 3 rects (18 verts) + 4 corners x 14 triangles x 3 verts.
        Assert.Equal(18 + 4 * 14 * 3, b.SolidVertexCount);
    }

    [Fact]
    public void AddPolylineRibbon_emits_three_quads_per_segment()
    {
        var b = new TimelineBatcher();
        float[] pts = [0, 0, 10, 5, 20, 0, 30, 5];

        b.AddPolylineRibbon(pts, pointCount: 4, thickness: 2f, pad: 1f, 1f, 1f, 1f, 1f, miterLimit: 4f);

        // 3 quads per segment, 3 segments, 6 vertices per quad.
        Assert.Equal(3 * 3 * 6, b.SolidVertexCount);
    }

    [Fact]
    public void AddPolylineRibbon_with_fewer_than_two_points_emits_nothing()
    {
        var b = new TimelineBatcher();
        float[] pts = [0, 0];

        b.AddPolylineRibbon(pts, pointCount: 1, thickness: 2f, pad: 1f, 1f, 1f, 1f, 1f, miterLimit: 4f);

        Assert.Equal(0, b.SolidVertexCount);
    }

    // ---- draw-command runs --------------------------------------------------

    [Fact]
    public void Consecutive_same_kind_emissions_coalesce_into_one_run()
    {
        var b = new TimelineBatcher();

        b.AddRect(0, 0, 1, 1, Opaque);
        b.AddRect(2, 0, 1, 1, Opaque);
        b.AddRect(4, 0, 1, 1, Opaque);
        b.FinalizeFrame();

        var cmd = Assert.Single(b.DrawCommands);
        Assert.Equal(BatchKind.Solid, cmd.Kind);
        Assert.Equal(0, cmd.FirstVertex);
        Assert.Equal(18, cmd.VertexCount);
    }

    [Fact]
    public void Alternating_kinds_produce_runs_in_insertion_order_with_correct_offsets()
    {
        var b = new TimelineBatcher();

        b.AddRect(0, 0, 1, 1, Opaque);        // solid 0..6
        b.AddSprite(0, 0, 1, 1, Opaque, 1f);  // general 0..6
        b.AddRect(2, 0, 1, 1, Opaque);        // solid 6..12

        b.FinalizeFrame();

        Assert.Equal(3, b.DrawCommands.Count);

        Assert.Equal(BatchKind.Solid, b.DrawCommands[0].Kind);
        Assert.Equal(0, b.DrawCommands[0].FirstVertex);
        Assert.Equal(6, b.DrawCommands[0].VertexCount);

        Assert.Equal(BatchKind.General, b.DrawCommands[1].Kind);
        Assert.Equal(0, b.DrawCommands[1].FirstVertex);
        Assert.Equal(6, b.DrawCommands[1].VertexCount);

        // The second solid run must start where the first ended, not at 0 —
        // the two streams have independent vertex cursors.
        Assert.Equal(BatchKind.Solid, b.DrawCommands[2].Kind);
        Assert.Equal(6, b.DrawCommands[2].FirstVertex);
        Assert.Equal(6, b.DrawCommands[2].VertexCount);
    }

    [Fact]
    public void FinalizeFrame_emits_the_trailing_run()
    {
        // Dropping FinalizeFrame silently loses every frame's FINAL run, so a
        // single-stream frame renders nothing at all. RecordDraws calls it; this
        // pins that it is the call that matters.
        var b = new TimelineBatcher();
        b.AddRect(0, 0, 1, 1, Opaque);

        Assert.Empty(b.DrawCommands);

        b.FinalizeFrame();

        Assert.Single(b.DrawCommands);
    }

    [Fact]
    public void FinalizeFrame_on_an_empty_frame_emits_no_run()
    {
        var b = new TimelineBatcher();

        b.FinalizeFrame();

        Assert.Empty(b.DrawCommands);
    }

    [Fact]
    public void Reset_clears_counts_commands_and_the_run_cursor()
    {
        var b = new TimelineBatcher();
        b.AddSprite(0, 0, 1, 1, Opaque, 1f);
        b.AddRect(0, 0, 1, 1, Opaque);
        b.FinalizeFrame();

        b.Reset();

        Assert.Equal(0, b.SolidVertexCount);
        Assert.Equal(0, b.GeneralVertexCount);
        Assert.Empty(b.DrawCommands);

        // The run cursor must reset too, or the first run of the NEXT frame
        // starts at a stale offset.
        b.AddRect(0, 0, 1, 1, Opaque);
        b.FinalizeFrame();
        Assert.Equal(0, Assert.Single(b.DrawCommands).FirstVertex);
    }

    // ---- buffers ------------------------------------------------------------

    [Fact]
    public void Buffers_start_small_and_grow_on_demand()
    {
        // They were 2M + 1M floats allocated EAGERLY = 11.44 MiB of Large Object
        // Heap per batcher, and there is one per ChartRenderer plus one per
        // stacked-track row. Five renderers cost 57 MiB before drawing anything.
        var b = new TimelineBatcher();
        var initialSolid = b.SolidBuffer.Length;
        var initialGeneral = b.GeneralBuffer.Length;

        Assert.True(initialSolid <= 128 * 1024,
            $"solid buffer starts at {initialSolid} floats ({initialSolid * 4 / 1024} KiB) — the eager LOH allocation is back");
        Assert.True(initialGeneral <= 128 * 1024,
            $"general buffer starts at {initialGeneral} floats — the eager LOH allocation is back");

        // ...and still holds whatever a heavy frame asks for.
        for (var i = 0; i < 40_000; i++) b.AddRect(i, 0, 1, 1, Opaque);

        Assert.True(b.SolidBuffer.Length >= b.SolidVertexCount * 6);
        Assert.Equal(40_000 * 6, b.SolidVertexCount);
    }

    [Fact]
    public void EnsureQuadsCapacity_pre_grows_both_streams()
    {
        var b = new TimelineBatcher();

        b.EnsureQuadsCapacity(solidQuads: 50_000, generalQuads: 50_000);

        Assert.True(b.SolidBuffer.Length >= 50_000 * 6 * 6);
        Assert.True(b.GeneralBuffer.Length >= 50_000 * 6 * 9);
    }

    [Fact]
    public void AddText_without_an_atlas_is_a_no_op()
    {
        // The headless test platform and the pre-atlas first frame both hand the
        // batcher a null FontAtlas; text must degrade to nothing, never throw.
        var b = new TimelineBatcher();

        b.AddText("120 BPM", 10, 10, Opaque);

        Assert.Equal(0, b.GeneralVertexCount);
        Assert.Equal(0f, b.MeasureTextWidth("120 BPM"));
    }
}
