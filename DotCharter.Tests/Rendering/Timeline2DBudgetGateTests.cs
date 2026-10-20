using DotCharter.Rendering.Data;
using DotCharter.Rendering.Timeline2D;
using DotCharter.UI.Controls.Layers;

namespace DotCharter.Tests.Rendering;

/// <summary>
///     The 2D twin of <see cref="FrameSnapshotPerfGateTests" />: the snapshot
///     gate proves the PROJECTION stays O(visible), this proves the GEOMETRY
///     the layer stack emits from it does too.
///     <para>
///         Everything the 12 golden-frame tests and 3 perf gates would have
///         covered here is GPU-gated and skips by default, so the default gate
///         covered zero layer geometry. These run in it: the layers below take
///         no view models, and <see cref="TimelineBatcher" /> is a plain
///         float-buffer builder — no GPU, no display, no Avalonia app.
///     </para>
///     <para>
///         Asserted channels are counts and bytes, never wall-clock — hosted CI
///         runners have jittery shared CPUs.
///     </para>
/// </summary>
public sealed class Timeline2DBudgetGateTests
{
    private const double ViewWidth = 1920;
    private const double ViewHeight = 500;
    private const double Zoom = 0.2; // the default PixelsPerMs => a 9600 ms window

    [Fact]
    public void A_dense_frame_stays_within_the_vertex_budget()
    {
        var vertices = RenderFrame(soundCount: 20_000, lengthMinutes: 5);

        // Measured 60,474 with culling on and 94,914 with NoteCulling.IsOffScreen
        // forced to false — the snapshot window is 2.04x the 9600 ms viewport at
        // this zoom. The number is fully deterministic (synthetic chart, no
        // wall-clock, no RNG), so the budget can sit tight: 75k leaves ~24 % for
        // legitimate visual additions and still fails hard the moment the note
        // path stops culling or a squiggle loop goes unclipped again.
        Assert.True(vertices < 75_000,
            $"the 2D layer stack emitted {vertices} vertices for one dense frame — the geometry budget is blown");
    }

    [Fact]
    public void Frame_cost_is_O_visible_not_O_chart()
    {
        // Same DENSITY, 10x the chart. Every windowed projection and every
        // layer cull has to hold for the cost to stay flat; a single O(total)
        // loop anywhere in the stack shows up here as a 10x.
        var shortChart = RenderFrame(soundCount: 2_000, lengthMinutes: 1);
        var longChart = RenderFrame(soundCount: 20_000, lengthMinutes: 10);

        Assert.True(longChart <= shortChart * 1.35,
            $"a 10x longer chart at the same density cost {longChart} vertices vs {shortChart} — something in the layer stack is O(total)");
    }

    [Fact]
    public void The_batcher_buffers_stay_far_below_the_old_eager_LOH_allocation()
    {
        var batcher = new TimelineBatcher();
        RenderDenseFrame(batcher);

        var bytes = (batcher.SolidBuffer.Length + batcher.GeneralBuffer.Length) * sizeof(float);

        // The eager field initializers were 11.44 MiB PER BATCHER, and there is
        // one per ChartRenderer plus one per stacked-track row (5 renderers =
        // 57 MiB before drawing anything). A dense frame must settle well under
        // that after the on-demand doubling.
        Assert.True(bytes < 4 * 1024 * 1024,
            $"a dense frame grew the batcher to {bytes / 1024} KiB — the buffers are back to eager LOH sizing");
    }

    [Fact]
    public void Every_layer_finishes_the_frame_with_whole_quads()
    {
        // The vertex streams are replayed as triangle lists; a layer that leaves
        // a partial primitive shifts every later draw command in the frame.
        var batcher = new TimelineBatcher();
        RenderDenseFrame(batcher);
        batcher.FinalizeFrame();

        Assert.Equal(0, batcher.SolidVertexCount % 3);
        Assert.Equal(0, batcher.GeneralVertexCount % 3);
        Assert.All(batcher.DrawCommands, c => Assert.Equal(0, c.VertexCount % 3));
    }

    [Fact]
    public void Draw_commands_tile_each_stream_contiguously_in_insertion_order()
    {
        // RecordDraws replays these against ONE vertex buffer per stream, so a
        // gap or an overlap silently draws the wrong geometry.
        var batcher = new TimelineBatcher();
        RenderDenseFrame(batcher);
        batcher.FinalizeFrame();

        var nextSolid = 0;
        var nextGeneral = 0;
        foreach (var cmd in batcher.DrawCommands)
        {
            if (cmd.Kind == BatchKind.Solid)
            {
                Assert.Equal(nextSolid, cmd.FirstVertex);
                nextSolid += cmd.VertexCount;
            }
            else
            {
                Assert.Equal(nextGeneral, cmd.FirstVertex);
                nextGeneral += cmd.VertexCount;
            }
        }

        Assert.Equal(batcher.SolidVertexCount, nextSolid);
        Assert.Equal(batcher.GeneralVertexCount, nextGeneral);
    }

    // ---- harness ------------------------------------------------------------

    private static int RenderFrame(int soundCount, double lengthMinutes)
    {
        var batcher = new TimelineBatcher();
        RenderInto(batcher, soundCount, lengthMinutes);
        return batcher.SolidVertexCount + batcher.GeneralVertexCount;
    }

    private static void RenderDenseFrame(TimelineBatcher batcher)
        => RenderInto(batcher, soundCount: 20_000, lengthMinutes: 5);

    /// <summary>
    ///     The view-model-free half of the layer stack — which is every
    ///     geometry-heavy layer. <c>BeatGridLayer</c> (via
    ///     <c>BackgroundLayer</c>) and <c>LaneLayer</c> read
    ///     <c>rc.Editor</c>/<c>rc.Transport</c>, and constructing a
    ///     <c>TransportViewModel</c> pulls in the audio service, the config
    ///     service and a dispatcher, i.e. most of the DI graph. Their own
    ///     culling is already pre-computed (bookmark/annotation screen tests
    ///     and a j-range pre-culled sub-grid), which is exactly what the note
    ///     path was missing.
    /// </summary>
    private static void RenderInto(TimelineBatcher batcher, int soundCount, double lengthMinutes)
    {
        var (snapshot, viewStartMs) = Snapshot(soundCount, lengthMinutes);
        var rc = Context(viewStartMs);
        rc.Snapshot = snapshot;
        ITimelineRenderLayer[] layers =
        [
            new HandShapeLayer(),
            new FretAnchorLayer(),
            new NoteLayer(),
            new EventLayer(new PinnedLabelHitRegistry(), new object()),
            new CursorLayer(),
            new PlayheadLayer()
        ];

        foreach (var layer in layers) layer.Render(batcher, rc);
    }

    private static (FrameSnapshot Snapshot, double ViewStartMs) Snapshot(int soundCount, double lengthMinutes)
    {
        var chart = StressChartFactory.Chart(soundCount, lengthMinutes);
        var ctx = new SnapshotUpdateContext
        {
            ActiveBeats = chart.Beats,
            ActiveNotes = chart.Notes,
            ActiveChords = chart.Chords,
            ActiveAnchors = chart.Anchors,
            ActiveHandShapes = chart.HandShapes,
            EventPoints = chart.EventPoints,
            Tones = chart.Tones,
            LyricPhrases = chart.LyricPhrases,
            Showlights = chart.Showlights
        };

        var snapshot = new FrameSnapshot { StringCount = 6 };
        var mid = chart.LengthMs / 2;
        FrameSnapshot.BuildSnapshot(mid, mid + ViewWidth / Zoom, mid, snapshot, ctx, []);
        return (snapshot, mid);
    }

    private static ChartRenderContext Context(double viewStartMs = 0) => new()
    {
        Width = ViewWidth,
        Height = ViewHeight,
        CurrentZoom = Zoom,
        ViewStartMs = viewStartMs,
        ViewEndMs = viewStartMs + ViewWidth / Zoom,
        CurrentTimeMs = viewStartMs,
        TopMargin = 40f,
        BottomMargin = 20f,
        LaneAreaHeight = 420f,
        LaneHeight = 70f,
        FhpY = 470f,
        StringCount = 6,
        DrawLabels = true,
        RendererPixelsPerMs = Zoom
    };
}
