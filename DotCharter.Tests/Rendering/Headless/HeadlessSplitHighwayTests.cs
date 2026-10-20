using DotCharter.Rendering.Data;
using DotCharter.Rendering.Data.Render;
using DotCharter.Rendering.Headless;
using DotCharter.Rendering.Vulkan;

namespace DotCharter.Tests.Rendering.Headless;

/// <summary>
///     Golden frames for the couch co-op SPLIT HIGHWAY: two players' highways
///     rendered into ONE frame, each following its own chart and its own
///     anchor-follow camera.
///     <para>
///         This drives the REAL <c>Fretboard3DEngine</c> multi-viewport frame
///         (one <c>BeginFrame</c>/<c>EndFrame</c>, one depth buffer, N scene
///         recordings) through <see cref="HeadlessHighwayRenderer.RenderSplit" />
///         — the same code the app's render driver calls — so what these assert
///         is what ships.
///     </para>
///     <para>
///         The shape of the assertion is deliberate. "It rendered" is not
///         enough: a split that silently drew ONE highway across the whole frame
///         would pass a plain non-black check, and a split whose two views
///         shared a camera would pass a "both halves have content" check. So
///         each half is checked for content SEPARATELY, the divider band is
///         checked to be empty, and the two halves are checked to differ from
///         each other AND from what the same chart produces on its own.
///     </para>
///     GPU-gated: <c>DOTCHARTER_GPU_TESTS=1</c>.
/// </summary>
[Collection("HeadlessGpu")]
public class HeadlessSplitHighwayTests
{
    private const int Width = 640;
    private const int Height = 480;

    public HeadlessSplitHighwayTests() => Environment.SetEnvironmentVariable("DOTCHARTER_VK_VALIDATION", "1");

    /// <summary>Player 1's chart: a low-fret run across all six strings.</summary>
    private static FrameSnapshot BuildPlayerOneChart()
    {
        var s = new FrameSnapshot
        {
            StringCount = 6, HighwayScrollSpeed = 1.3f, Anchors = [new RenderAnchor { Time = 0, EndTime = 20000, Fret = 1, Width = 5 }]
        };
        s.AnchorCount = s.Anchors.Length;

        var beats = new List<RenderBeat>();
        for (var i = 0; i < 48; i++)
        {
            beats.Add(new RenderBeat
            {
                Time = i * 500, IsMeasure = i % 4 == 0, Bpm = 120,
                BeatsInMeasure = 4, NoteDenominator = 4, MeasureNumber = i / 4
            });
        }

        s.Beats = beats.ToArray();
        s.BeatCount = beats.Count;

        var notes = new List<RenderNote>();
        for (var i = 0; i < 18; i++)
        {
            notes.Add(new RenderNote
            {
                Time = 300 + i * 220,
                StringIndex = (byte)(i % 6),
                Fret = (byte)(1 + i % 5),
                Sustain = i % 3 == 0 ? 180 : 0
            });
        }

        s.Notes = notes.ToArray();
        s.NoteCount = notes.Count;

        return s;
    }

    /// <summary>Player 2's chart: the SAME song, a different part — high up the
    /// neck (anchor at fret 14, not 1) and on the bass strings only. Two things
    /// have to differ for the split to be provable: the CONTENT and the CAMERA.
    /// A shared camera would frame both halves at the same fret, so an anchor
    /// 13 frets away is what makes a camera leak visible as pixels.</summary>
    private static FrameSnapshot BuildPlayerTwoChart()
    {
        var s = BuildPlayerOneChart();

        s.Anchors = [new RenderAnchor { Time = 0, EndTime = 20000, Fret = 14, Width = 4 }];
        s.AnchorCount = s.Anchors.Length;

        var notes = new List<RenderNote>();
        for (var i = 0; i < 12; i++)
        {
            notes.Add(new RenderNote
            {
                Time = 250 + i * 330,
                StringIndex = (byte)(i % 3),
                Fret = (byte)(14 + i % 4),
                Sustain = 260
            });
        }

        s.Notes = notes.ToArray();
        s.NoteCount = notes.Count;

        return s;
    }

    private static void AssertNoValidationErrors(string when)
    {
        if (!VulkanValidationDiagnostics.Active) return;
        Assert.True(VulkanValidationDiagnostics.ErrorCount == 0,
            $"Vulkan validation errors after {when}: {VulkanValidationDiagnostics.ErrorCount}. " +
            $"Last: {VulkanValidationDiagnostics.LastError}");
    }

    [Fact]
    public void TwoPlayers_RenderIntoTwoDistinctNonBlackHalvesOfOneFrame()
    {
        Assert.SkipWhen(!GoldenFrame.GpuTestsEnabled,
            "GPU headless test — set DOTCHARTER_GPU_TESTS=1 to run.");

        using var r = new HeadlessHighwayRenderer();

        var p1 = BuildPlayerOneChart();
        var p2 = BuildPlayerTwoChart();
        var px = r.RenderSplit(new[] { p1, p2 }, Width, Height, timeMs: 0,
            SplitHighwayOrientation.Stacked);
        GoldenFrame.Dump(px, Width, Height, "vk_coop_split");

        Assert.Equal(Width * Height * 4, px.Length);

        var rects = new HighwayViewportRect[2];
        var count = SplitHighwayLayout.Compute(Width, Height, 2, SplitHighwayOrientation.Stacked, rects);
        Assert.Equal(2, count);

        double Coverage(HighwayViewportRect rc) => GoldenFrame.NonBlackRatioInRegion(px, Width, Height,
            rc.X / (double)Width, rc.Y / (double)Height, rc.Right / (double)Width, rc.Bottom / (double)Height);

        double Colour(HighwayViewportRect rc) => RegionColorfulRatio(px, Width, rc);

        var topCoverage = Coverage(rects[0]);
        var bottomCoverage = Coverage(rects[1]);
        var topColour = Colour(rects[0]);
        var bottomColour = Colour(rects[1]);

        // The divider band between the two views: nothing draws there, so it
        // must still hold the render pass's black clear. This is what proves
        // the scissor is doing its job — with only a viewport set (no scissor)
        // the ortho crowd-background quad spans the whole NDC square and would
        // paint straight across the band into the neighbour's half.
        var dividerCoverage = GoldenFrame.NonBlackRatioInRegion(px, Width, Height,
            0.0, rects[0].Bottom / (double)Height, 1.0, rects[1].Y / (double)Height);

        try
        {
            Assert.True(topCoverage > 0.3, $"Player 1's half is sparse/black (non-black {topCoverage:P1}).");
            Assert.True(bottomCoverage > 0.3, $"Player 2's half is sparse/black (non-black {bottomCoverage:P1}).");
            Assert.True(topColour > 0.004, $"Player 1's half has no saturated content ({topColour:P2}).");
            Assert.True(bottomColour > 0.004, $"Player 2's half has no saturated content ({bottomColour:P2}).");
            Assert.True(dividerCoverage < 0.10,
                $"The divider band between the two views is painted ({dividerCoverage:P1}) — a view is bleeding " +
                "past its scissor into the other player's half.");
        }
        catch
        {
            GoldenFrame.Attach(px, Width, Height, "vk_coop_split_fail");
            throw;
        }

        AssertNoValidationErrors("split highway render");
    }

    [Fact]
    public void EachPlayersHalf_ShowsThatPlayersOwnChartAndOwnCamera()
    {
        Assert.SkipWhen(!GoldenFrame.GpuTestsEnabled,
            "GPU headless test — set DOTCHARTER_GPU_TESTS=1 to run.");

        using var r = new HeadlessHighwayRenderer();

        var rects = new HighwayViewportRect[2];
        SplitHighwayLayout.Compute(Width, Height, 2, SplitHighwayOrientation.Stacked, rects);
        var half = rects[0];

        // The split frame.
        var split = r.RenderSplit(new[] { BuildPlayerOneChart(), BuildPlayerTwoChart() },
            Width, Height, timeMs: 0, SplitHighwayOrientation.Stacked);
        GoldenFrame.Dump(split, Width, Height, "vk_coop_split_content");

        // The SAME frame with player 2 given player 1's chart. If the two views
        // shared a camera or a snapshot, this would be pixel-identical to the
        // frame above in the BOTTOM half — that is the leak this isolates.
        var mirrored = r.RenderSplit(new[] { BuildPlayerOneChart(), BuildPlayerOneChart() },
            Width, Height, timeMs: 0, SplitHighwayOrientation.Stacked);
        GoldenFrame.Dump(mirrored, Width, Height, "vk_coop_split_mirrored");

        var bottomDiff = RegionDifferenceRatio(split, mirrored, Width, rects[1]);
        var topDiff = RegionDifferenceRatio(split, mirrored, Width, rects[0]);

        // And the two halves of the mirrored frame must now agree with each
        // other (same chart, same camera target, different screen position) —
        // which proves the difference above really came from the chart and not
        // from some position-dependent artefact.
        var mirroredHalvesDiff = HalfToHalfDifferenceRatio(mirrored, Width, rects[0], rects[1]);
        var splitHalvesDiff = HalfToHalfDifferenceRatio(split, Width, rects[0], rects[1]);

        try
        {
            Assert.True(bottomDiff > 0.05,
                $"Player 2's half did not change when its chart changed (diff {bottomDiff:P1}) — the second view " +
                "is not reading its own snapshot.");
            Assert.True(topDiff < 0.02,
                $"Player 1's half changed when only player 2's chart changed (diff {topDiff:P1}) — the views are " +
                "sharing state.");
            Assert.True(splitHalvesDiff > 0.05,
                $"The two halves of the co-op frame are near-identical (diff {splitHalvesDiff:P1}) — both players " +
                "appear to be showing the same part.");
            Assert.True(mirroredHalvesDiff < splitHalvesDiff,
                $"Two views of the SAME chart ({mirroredHalvesDiff:P1}) should agree more closely than two views " +
                $"of different charts ({splitHalvesDiff:P1}).");
        }
        catch
        {
            GoldenFrame.Attach(split, Width, Height, "vk_coop_split_content_fail");
            GoldenFrame.Attach(mirrored, Width, Height, "vk_coop_split_mirrored_fail");
            throw;
        }

        AssertNoValidationErrors("split highway content isolation");
        _ = half;
    }

    [Fact]
    public void ASingleViewportSplit_MatchesTheOrdinarySoloFrame()
    {
        Assert.SkipWhen(!GoldenFrame.GpuTestsEnabled,
            "GPU headless test — set DOTCHARTER_GPU_TESTS=1 to run.");

        using var r = new HeadlessHighwayRenderer();

        var solo = r.Render(BuildPlayerOneChart(), Width, Height, timeMs: 0);
        var oneUp = r.RenderSplit(new[] { BuildPlayerOneChart() }, Width, Height, timeMs: 0);
        GoldenFrame.Dump(solo, Width, Height, "vk_coop_solo_baseline");
        GoldenFrame.Dump(oneUp, Width, Height, "vk_coop_solo_via_split");

        // One player must go through the split path unchanged — that is what
        // makes the multi-viewport frame safe to be the ONLY frame path (the
        // solo overload delegates to it).
        var diff = GoldenFrame.DifferenceRatio(solo, oneUp);
        try
        {
            Assert.True(diff < 0.005,
                $"A one-player split frame differs from the ordinary solo frame ({diff:P2}).");
        }
        catch
        {
            GoldenFrame.Attach(solo, Width, Height, "vk_coop_solo_baseline_fail");
            GoldenFrame.Attach(oneUp, Width, Height, "vk_coop_solo_via_split_fail");
            throw;
        }

        AssertNoValidationErrors("single-viewport split render");
    }

    // ---- region helpers -------------------------------------------------
    // GoldenFrame's helpers take fractional rects or whole frames; the split
    // needs pixel-rect versions so a viewport's own bounds can be used
    // directly.

    private static double RegionColorfulRatio(byte[] bgra, int width, HighwayViewportRect rc, int spread = 40)
    {
        long hits = 0, count = 0;
        for (var y = rc.Y; y < rc.Bottom; y++)
        {
            for (var x = rc.X; x < rc.Right; x++)
            {
                var i = (y * width + x) * 4;
                var max = Math.Max(bgra[i], Math.Max(bgra[i + 1], bgra[i + 2]));
                var min = Math.Min(bgra[i], Math.Min(bgra[i + 1], bgra[i + 2]));
                if (max - min > spread) hits++;
                count++;
            }
        }

        return count == 0 ? 0 : (double)hits / count;
    }

    private static double RegionDifferenceRatio(byte[] a, byte[] b, int width, HighwayViewportRect rc,
        int perChannelTol = 8)
    {
        long diff = 0, count = 0;
        for (var y = rc.Y; y < rc.Bottom; y++)
        {
            for (var x = rc.X; x < rc.Right; x++)
            {
                var i = (y * width + x) * 4;
                if (Math.Abs(a[i] - b[i]) > perChannelTol ||
                    Math.Abs(a[i + 1] - b[i + 1]) > perChannelTol ||
                    Math.Abs(a[i + 2] - b[i + 2]) > perChannelTol)
                {
                    diff++;
                }

                count++;
            }
        }

        return count == 0 ? 0 : (double)diff / count;
    }

    /// <summary>Compares two same-sized viewports of the SAME frame pixel for
    /// pixel (the last view absorbs the tiling remainder, so only the
    /// overlapping extent is compared).</summary>
    private static double HalfToHalfDifferenceRatio(byte[] bgra, int width,
        HighwayViewportRect a, HighwayViewportRect b, int perChannelTol = 8)
    {
        var w = Math.Min(a.Width, b.Width);
        var h = Math.Min(a.Height, b.Height);
        long diff = 0, count = 0;
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var ia = ((a.Y + y) * width + a.X + x) * 4;
                var ib = ((b.Y + y) * width + b.X + x) * 4;
                if (Math.Abs(bgra[ia] - bgra[ib]) > perChannelTol ||
                    Math.Abs(bgra[ia + 1] - bgra[ib + 1]) > perChannelTol ||
                    Math.Abs(bgra[ia + 2] - bgra[ib + 2]) > perChannelTol)
                {
                    diff++;
                }

                count++;
            }
        }

        return count == 0 ? 0 : (double)diff / count;
    }
}
