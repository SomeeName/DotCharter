using DotCharter.Rendering.Data;
using DotCharter.Rendering.Headless;
using DotCharter.Tests.Services.Parsing.GuitarPro;
using DotCharter.UI.Controls.Layers;
using DotCharter.UI.Graphics.Vulkan2D;
using static DotCharter.Tests.Rendering.PartialChordSlideFixture;

namespace DotCharter.Tests.Rendering.Headless;

/// <summary>
///     The owner's partial chord slide (#476) through the REAL 3D highway: 5+8
///     where the 5 slides to 6 and the 8 is held (tied) or re-picked. The two
///     charts differ ONLY in whether the 8 is struck again on the landing, so the
///     pixels that differ between their frames ARE the re-picked 8's head — which
///     the highway dropped while it took the chord's link for every string.
///     The CPU-side structure is pinned by <see cref="PartialChordSlideRenderTests" />;
///     <c>DOTCHARTER_GPU_DUMP=1</c> also writes the review frames (highway with
///     the real chart textures, and the 2D timeline).
/// </summary>
[Collection("HeadlessGpu")]
public class HeadlessPartialChordSlideTests
{
    private const int Width = 1600;
    private const int Height = 900;

    /// <summary>The landing is 300 ms out: well inside the highway, in front of the strike line.</summary>
    private const double FrameMs = LandingMs - 300;

    /// <summary>Generous for one note's column at this depth (a gem plus its
    /// anticipation frame measured 104 px).</summary>
    private const int MaxHeadSpanPx = 220;

    [Fact]
    public void TheRepickedHeldNote_DrawsAHeadOnTheLanding()
    {
        Assert.SkipWhen(!GoldenFrame.GpuTestsEnabled, "GPU headless test — set DOTCHARTER_GPU_TESTS=1 to run.");

        var tied = HighwaySnapshot(Import());
        var repicked = HighwaySnapshot(GpPartialChordSlideTests.ImportRepickedHold());

        using var r = new HeadlessHighwayRenderer(useRealChartTextures: true);
        var tiedFrame = r.Render(tied, Width, Height, timeMs: FrameMs);
        var repickedFrame = r.Render(repicked, Width, Height, timeMs: FrameMs);
        GoldenFrame.Dump(tiedFrame, Width, Height, "vk_partial_slide_tied");
        GoldenFrame.Dump(repickedFrame, Width, Height, "vk_partial_slide_repicked");
        // The slide itself, mid-way: only the A string's tail bends across the frets.
        GoldenFrame.Dump(r.Render(tied, Width, Height, timeMs: OriginMs + 150), Width, Height,
            "vk_partial_slide_tied_midslide");

        var box = GoldenFrame.DifferenceBounds(tiedFrame, repickedFrame, Width, Height);
        Assert.True(box is not null, "a re-picked 8 drew nothing on the landing — the highway took it for a link");
        Assert.True(box!.Value.Pixels > 100, $"only {box.Value.Pixels} pixels differ — too few for a note head");
        // ONE fret column in the playfield: the gem, and the anticipation frame it
        // casts on the board at the strike line (measured 104 px wide). Not at an
        // edge, and not spread across the board.
        var (x0, y0, x1, y1, _) = box.Value;
        Assert.True(x0 > 0 && x1 < Width - 1 && y1 < Height - 1,
            $"the difference touches the frame edge ({x0},{y0})..({x1},{y1})");
        Assert.True(x1 - x0 < MaxHeadSpanPx, $"the difference spans x {x0}..{x1} — wider than one note's column");

        DumpTimeline(tied, "tl_partial_slide_tied");
        DumpTimeline(repicked, "tl_partial_slide_repicked");
    }

    private static FrameSnapshot HighwaySnapshot(Arrangement arrangement)
    {
        var s = Snapshot(arrangement);
        s.HighwayScrollSpeed = 1.3f;
        return s;
    }

    private static void DumpTimeline(FrameSnapshot snapshot, string name)
    {
        if (!GoldenFrame.DumpFrames) return;
        const int w = 1200, h = 420;
        using var r = new HeadlessVulkanTimelineRenderer(w, h);
        var rc = new ChartRenderContext
        {
            Width = w,
            Height = h,
            CurrentZoom = 0.4,
            ViewStartMs = 500,
            ViewEndMs = 500 + w / 0.4,
            TopMargin = 30f,
            BottomMargin = 30f,
            LaneAreaHeight = 360f,
            LaneHeight = 60f,
            StringCount = 6,
            DrawLabels = true,
            Snapshot = snapshot
        };
        var px = r.Render(rc, new ITimelineRenderLayer[] { new LaneLayer(), new HandShapeLayer(), new NoteLayer() });
        GoldenFrame.Dump(px, w, h, name);
    }
}
