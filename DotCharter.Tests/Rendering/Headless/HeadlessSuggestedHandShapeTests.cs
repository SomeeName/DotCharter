using DotCharter.Rendering.Data;
using DotCharter.Rendering.Data.Render;
using DotCharter.UI.Constants;
using DotCharter.UI.Controls.Layers;
using DotCharter.UI.Graphics.Vulkan2D;

namespace DotCharter.Tests.Rendering.Headless;

/// <summary>
///     A suggested hand shape (#491) through the REAL Vulkan 2D path: its edge is dashed on screen
///     where a committed bar's is solid. <see cref="HandShapeLayerBarStyleTests" /> pins the geometry
///     on the CPU; this proves the dashes survive rasterization at 1 px (a dash pattern finer than the
///     pixel grid would blur into a solid line). GPU-gated: <c>DOTCHARTER_GPU_TESTS=1</c>;
///     <c>DOTCHARTER_GPU_DUMP=1</c> writes <c>vk_hand_shape_suggested.png</c>.
/// </summary>
[Collection("HeadlessGpu")]
public sealed class HeadlessSuggestedHandShapeTests
{
    private const int Width = 900;
    private const int Height = 420;
    private const float TopMargin = 150f;
    private const float LaneAreaHeight = 230f;

    [Fact]
    public void ASuggestedBarsEdge_IsDashedOnScreen_WhereACommittedBarsIsSolid()
    {
        Assert.SkipWhen(!GoldenFrame.GpuTestsEnabled, "GPU headless test — set DOTCHARTER_GPU_TESTS=1 to run.");

        var snapshot = new FrameSnapshot
        {
            HandShapes = [new RenderHandShape { Time = 60, EndTime = 380, TemplateId = 0, Label = "Am" }],
            HandShapeCount = 1,
            SuggestedHandShapes = [new RenderHandShape { Time = 480, EndTime = 840, TemplateId = -1, Label = "C", IsSuggested = true }],
            SuggestedHandShapeCount = 1
        };
        var rc = new ChartRenderContext
        {
            Width = Width,
            Height = Height,
            CurrentZoom = 1.0,
            ViewStartMs = 0,
            ViewEndMs = Width,
            TopMargin = TopMargin,
            BottomMargin = 20f,
            LaneAreaHeight = LaneAreaHeight,
            LaneHeight = LaneAreaHeight / 6f,
            StringCount = 6,
            DrawLabels = true,
            Snapshot = snapshot
        };

        using var r = new HeadlessVulkanTimelineRenderer(Width, Height);
        var px = r.Render(rc, [new HandShapeLayer()]);
        GoldenFrame.Dump(px, Width, Height, "vk_hand_shape_suggested");

        var edgeY = (int)(TopMargin + LaneAreaHeight + ChartEditorConstants.RenderMetrics.HandShapeBarYOffset);
        var committedGaps = DimRuns(px, edgeY, 70, 370);
        var suggestedGaps = DimRuns(px, edgeY, 490, 830);

        Assert.Equal(0, committedGaps);
        Assert.True(suggestedGaps >= 20, $"the suggested bar's top edge shows {suggestedGaps} gaps — it is not dashed");
    }

    /// <summary>How many separate runs of DIM pixels the row holds between two columns: an
    /// unbroken edge has none, a dashed one one per gap.</summary>
    private static int DimRuns(byte[] bgra, int y, int x0, int x1)
    {
        var brightest = 0;
        for (var x = x0; x < x1; x++) brightest = Math.Max(brightest, Luma(bgra, x, y));
        var runs = 0;
        var inDim = false;
        for (var x = x0; x < x1; x++)
        {
            var dim = Luma(bgra, x, y) < brightest * 0.6;
            if (dim && !inDim) runs++;
            inDim = dim;
        }

        return runs;
    }

    private static int Luma(byte[] bgra, int x, int y)
    {
        var i = (y * Width + x) * 4;
        return Math.Max(bgra[i], Math.Max(bgra[i + 1], bgra[i + 2]));
    }
}
