using DotCharter.Rendering.Data;
using DotCharter.Rendering.Data.Render;
using DotCharter.Rendering.Headless;

namespace DotCharter.Tests.Rendering.Headless;

/// <summary>
///     The frame is 8-bit (B8G8R8A8), and the sky behind the highway is a slow,
///     dark gradient — about forty code values over half the screen — so it
///     quantises into flat bands unless the shader dithers, and the vignette's
///     translucent black ramps band the same way along the edges.
///     <c>highway.frag</c> ends in ±1 LSB triangular noise, scaled by 1/alpha
///     for translucent fragments so the noise survives the blend; the ±0.5 LSB
///     uniform noise it replaced left long flat runs and was multiplied away
///     entirely on the vignette.
///     <para>
///         Measured as the fraction of neighbouring pixels (along the gradient)
///         that are exactly EQUAL: a flat band is all equal pixels, a dithered
///         ramp changes value most of the time. Measured on this box's GPU:
///         0.28 / 0.26 / 0.25 (sky / left vignette / top vignette) with the
///         current shader, 0.40 / 0.42 / 0.39 with the old noise. The hash is a
///         pure function of the pixel position, so a frame is deterministic.
///     </para>
/// </summary>
[Collection("HeadlessGpu")]
public class HeadlessBackgroundBandingTests
{
    private const int Width = 1600;
    private const int Height = 900;
    private const double MaxEqualNeighbourFraction = 0.34;

    [Fact]
    public void The_sky_gradient_and_the_vignette_are_dithered_not_banded()
    {
        Assert.SkipWhen(!GoldenFrame.GpuTestsEnabled,
            "GPU headless test — set DOTCHARTER_GPU_TESTS=1 to run.");

        using var r = new HeadlessHighwayRenderer();
        var s = new FrameSnapshot
        {
            StringCount = 6,
            HighwayScrollSpeed = 1.3f,
            Anchors = [new RenderAnchor { Time = 0, EndTime = 20000, Fret = 3, Width = 4 }],
            AnchorCount = 1
        };
        var px = r.Render(s, Width, Height, timeMs: 0);
        GoldenFrame.Dump(px, Width, Height, "vk_background_banding");

        // Regions clear of the highway and the board (both sit low and central).
        var sky = EqualFraction(px, 100, 500, 40, 300, vertical: true);
        var leftVignette = EqualFraction(px, 0, 250, 100, 400, vertical: false);
        var topVignette = EqualFraction(px, 500, 1100, 0, 140, vertical: true);

        Assert.True(sky <= MaxEqualNeighbourFraction,
            $"sky: {sky:P0} of neighbouring pixels are identical — the gradient is banding");
        Assert.True(leftVignette <= MaxEqualNeighbourFraction,
            $"left vignette: {leftVignette:P0} of neighbouring pixels are identical — it is banding");
        Assert.True(topVignette <= MaxEqualNeighbourFraction,
            $"top vignette: {topVignette:P0} of neighbouring pixels are identical — it is banding");
    }

    /// <summary>
    ///     No stage light behind the far end of the highway: that is where the
    ///     notes are read, and a radial glow there was removed in favour of the
    ///     show lights' aurora in the top of the sky. With no showlights the sky
    ///     right above the far end reads no brighter than the same row a quarter
    ///     of the way in from the side (the vignette only darkens the outer 16 %).
    /// </summary>
    [Fact]
    public void The_sky_behind_the_far_end_of_the_highway_is_not_lit()
    {
        Assert.SkipWhen(!GoldenFrame.GpuTestsEnabled,
            "GPU headless test — set DOTCHARTER_GPU_TESTS=1 to run.");

        using var r = new HeadlessHighwayRenderer();
        var s = new FrameSnapshot
        {
            StringCount = 6,
            HighwayScrollSpeed = 1.3f,
            Anchors = [new RenderAnchor { Time = 0, EndTime = 20000, Fret = 3, Width = 4 }],
            AnchorCount = 1
        };
        var px = r.Render(s, Width, Height, timeMs: 0);

        var y = Height / 3;
        var centre = BandLuma(px, Width / 2 - 60, Width / 2 + 60, y - 30, y);
        var side = BandLuma(px, Width / 4 - 60, Width / 4 + 60, y - 30, y);

        Assert.True(centre < side * 1.05 + 1,
            $"the sky over the highway ({centre:F1}) is lit above the sky at the side ({side:F1})");
    }

    private static double BandLuma(byte[] bgra, int x0, int x1, int y0, int y1)
    {
        double sum = 0;
        var n = 0;
        for (var y = y0; y < y1; y++)
        {
            for (var x = x0; x < x1; x++)
            {
                var o = (y * Width + x) * 4;
                sum += 0.0722 * bgra[o] + 0.7152 * bgra[o + 1] + 0.2126 * bgra[o + 2];
                n++;
            }
        }

        return sum / n;
    }

    private static double EqualFraction(byte[] bgra, int x0, int x1, int y0, int y1, bool vertical)
    {
        long equal = 0, total = 0;
        for (var y = y0; y < y1; y++)
        {
            for (var x = x0; x < x1; x++)
            {
                var (nx, ny) = vertical ? (x, y + 1) : (x + 1, y);
                if (nx >= x1 || ny >= y1) continue;
                var a = (y * Width + x) * 4;
                var b = (ny * Width + nx) * 4;
                total++;
                if (bgra[a] == bgra[b] && bgra[a + 1] == bgra[b + 1] && bgra[a + 2] == bgra[b + 2]) equal++;
            }
        }

        return (double)equal / total;
    }
}
