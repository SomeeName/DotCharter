using System.Reflection;
using System.Runtime.CompilerServices;
using DotCharter.Rendering.Data;
using DotCharter.Rendering.Data.Render;
using DotCharter.Rendering.Timeline2D;
using DotCharter.UI.Controls.Layers;
using DotCharter.UI.Editing.Tracks;
using DotCharter.UI.ViewModels.Editor;

namespace DotCharter.Tests.Rendering;

/// <summary>
///     The beat grid's dashed lines (plain beats and the sub-grid) are the most
///     numerous lines in the 2D frame. Each is ONE quad whose dash pattern the
///     general fragment shader evaluates; drawn as one quad per dash they were
///     over half the frame's vertices at the default editing zooms, scaling with
///     the pane height. No GPU: the layer only writes floats into the batcher.
/// </summary>
public sealed class Timeline2DDashedGridVertexTests
{
    private const int GeneralFloatsPerVertex = 9;
    private const int TypeOffset = 8;

    [Theory]
    [InlineData(0.1)]
    [InlineData(0.2)]
    [InlineData(0.5)]
    public void The_grid_costs_the_same_vertices_at_every_pane_height(double zoom)
    {
        var shortPane = Render(zoom, height: 412);
        var tallPane = Render(zoom, height: 1400);

        Assert.True(shortPane.GeneralVertexCount > 0, "the beat grid emitted no general geometry");
        Assert.Equal(shortPane.GeneralVertexCount, tallPane.GeneralVertexCount);
    }

    [Theory]
    [InlineData(0.1)]
    [InlineData(0.2)]
    [InlineData(0.5)]
    public void Every_dashed_grid_line_is_exactly_one_quad(double zoom)
    {
        var batcher = Render(zoom, height: 560);
        var dashed = DashedVertexXs(batcher);

        // Plain beats at 0.1 px/ms are 50 px apart and the 4-way sub-grid 12.5 px,
        // both dashed: dozens of lines at every zoom here.
        Assert.True(dashed.Count >= 6 * 20, $"only {dashed.Count / 6} dashed grid lines were drawn");
        Assert.Equal(0, dashed.Count % 6);

        // One quad per line means one quad per distinct line position.
        var lines = dashed.Select(x => MathF.Round(x * 4f)).Distinct().Count();
        var quads = dashed.Count / 6;
        Assert.True(quads == lines,
            $"{quads} dashed quads for {lines} grid lines — a dashed line is being drawn one quad per dash");
    }

    [Fact]
    public void A_dashed_line_is_one_general_quad_however_long_it_is()
    {
        var batcher = new TimelineBatcher();
        batcher.AddDashedVerticalLineAaRaw(10f, 0f, 1f, 10_000f, 4f, 4f, 1f, 1f, 1f, 1f);
        Assert.Equal(6, batcher.GeneralVertexCount);
        Assert.Equal(0, batcher.SolidVertexCount);
    }

    [Fact]
    public void The_packed_dash_pattern_round_trips_exactly()
    {
        // The shader decodes floor(code / 1024) and code mod 1024 from the type;
        // every field must survive the float exactly.
        foreach (var (dash, gap) in new[] { (4f, 4f), (2f, 4f), (6f, 4f), (0.5f, 63.9375f) })
        {
            var code = TimelineBatcher.DashedLineType(dash, gap) - TimelineBatcher.DashedLineTypeBase;
            var dashUnits = MathF.Floor(code / TimelineBatcher.DashedLineDashStride);
            var gapUnits = code - dashUnits * TimelineBatcher.DashedLineDashStride;
            Assert.Equal(dash, dashUnits / TimelineBatcher.DashedLineUnitsPerPx);
            Assert.Equal(gap, gapUnits / TimelineBatcher.DashedLineUnitsPerPx);
        }
    }

    private static List<float> DashedVertexXs(TimelineBatcher batcher)
    {
        var xs = new List<float>();
        for (var v = 0; v < batcher.GeneralVertexCount; v += 6)
        {
            var i = v * GeneralFloatsPerVertex;
            if (batcher.GeneralBuffer[i + TypeOffset] < TimelineBatcher.DashedLineTypeBase - 0.5f) continue;
            for (var k = 0; k < 6; k++) xs.Add(batcher.GeneralBuffer[i]);
        }

        return xs;
    }

    private static TimelineBatcher Render(double zoom, int height)
    {
        var batcher = new TimelineBatcher();
        new BeatGridLayer().Render(batcher, Context(zoom, height));
        return batcher;
    }

    private static ChartRenderContext Context(double zoom, int height)
    {
        // An uninitialized transport carries no audio engine; only the grid
        // divisor is read, so the backing field is set directly.
        var transport = (TransportViewModel)RuntimeHelpers.GetUninitializedObject(typeof(TransportViewModel));
        typeof(TransportViewModel).GetField("_gridDivisor", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(transport, 4);

        var beats = new RenderBeat[120];
        for (var i = 0; i < beats.Length; i++)
        {
            beats[i] = new RenderBeat
            {
                Time = i * 500, IsMeasure = i % 4 == 0, Bpm = 120, BeatsInMeasure = 4, NoteDenominator = 4,
                MeasureNumber = i / 4 + 1
            };
        }

        const int width = 1920;
        return new ChartRenderContext
        {
            Width = width,
            Height = height,
            CurrentZoom = zoom,
            ViewStartMs = 1000,
            ViewEndMs = 1000 + width / zoom,
            TopMargin = 40f,
            BottomMargin = 20f,
            LaneAreaHeight = height - 60f,
            StringCount = 6,
            DrawLabels = true,
            Snapshot = new FrameSnapshot { StringCount = 6, Beats = beats, BeatCount = beats.Length },
            Transport = transport,
            Ribbon = new RibbonLayout(RibbonLanes.All)
        };
    }
}
