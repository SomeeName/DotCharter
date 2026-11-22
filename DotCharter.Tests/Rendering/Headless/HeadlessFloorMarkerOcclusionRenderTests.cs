using DotCharter.Rendering.Data;
using DotCharter.Rendering.Data.Render;
using DotCharter.Rendering.Headless;

namespace DotCharter.Tests.Rendering.Headless;

/// <summary>
///     A FAR note's flat floor marker must not paint over a NEARER sustain tail (#465).
///     used to be staged with the heads, after every tail. The pixels both the tail
///     and the far note cover are found by rendering each alone; where they overlap
///     the frame with both must show the tail (the check is limited to the far note's
///     stem and floor marker; its head is drawn over tails by design).
///     GPU-gated: <c>DOTCHARTER_GPU_TESTS=1</c>; <c>DOTCHARTER_GPU_DUMP=1</c>
///     writes <c>vk_floor_marker_*</c>.
///     <c>DOTCHARTER_GPU_DUMP=1</c> writes <c>vk_floor_marker_*</c>.
/// </summary>
[Collection("HeadlessGpu")]
public class HeadlessFloorMarkerOcclusionRenderTests
{
    private const int Width = 1600;
    private const int Height = 900;
    private const double TailNoteMs = 2_000;
    private const double TailMs = 4_000;
    private const double FarNoteMs = 3_000;
    private const double FrameMs = TailNoteMs + 300;
    private const int MinX = 780, MaxX = 840, MinY = 360, MaxY = 395;
    /// <summary>A pixel the tail owns outright: the far marker's tint would move it by more than this.</summary>
    private const int TailTolerance = 40;

    private const int RedString = 0;
    private const int YellowString = 1;

    [Fact]
    public void A_far_floor_marker_does_not_draw_over_a_nearer_sustain_tail()
    {
        Assert.SkipWhen(!GoldenFrame.GpuTestsEnabled, "GPU headless test — set DOTCHARTER_GPU_TESTS=1 to run.");

        using var r = new HeadlessHighwayRenderer(useRealChartTextures: true);
        var both = r.Render(Scene(true, true), Width, Height, FrameMs);
        var tailOnly = r.Render(Scene(true, false), Width, Height, FrameMs);
        var farOnly = r.Render(Scene(false, true), Width, Height, FrameMs);
        var bare = r.Render(Scene(false, false), Width, Height, FrameMs);
        GoldenFrame.Dump(both, Width, Height, "vk_floor_marker_both");
        GoldenFrame.Dump(tailOnly, Width, Height, "vk_floor_marker_tail");
        GoldenFrame.Dump(farOnly, Width, Height, "vk_floor_marker_far");

        var overlap = 0;
        var tailWins = 0;
        for (var i = 0; i < both.Length; i += 4)
        {
            // Only the stem and the floor marker, below the far head (which is drawn over tails by design).
            var x = i / 4 % Width;
            var y = i / 4 / Width;
            if (x < MinX || x > MaxX || y < MinY || y > MaxY) continue;
            if (Distance(tailOnly, bare, i) < 60 || Distance(farOnly, bare, i) < 60 ||
                Distance(tailOnly, farOnly, i) < 20)
            {
                continue;
            }

            overlap++;
            if (Distance(both, tailOnly, i) <= TailTolerance) tailWins++;
        }

        Assert.True(overlap > 30, $"the marker and tail overlap on only {overlap} pixels — the scene no longer tests #465");
        Assert.True(tailWins > overlap * 0.9,
            $"the tail shows on only {tailWins} of the {overlap} pixels where the far marker overlaps it");
    }

    private static int Distance(byte[] a, byte[] b, int i) =>
        Math.Abs(a[i] - b[i]) + Math.Abs(a[i + 1] - b[i + 1]) + Math.Abs(a[i + 2] - b[i + 2]);

    private static FrameSnapshot Scene(bool tail, bool far)
    {
        var notes = new List<RenderNote>();
        if (tail)
            notes.Add(new RenderNote { Time = TailNoteMs, Sustain = TailMs, StringIndex = YellowString, Fret = 7 });
        if (far) notes.Add(new RenderNote { Time = FarNoteMs, StringIndex = RedString, Fret = 7 });
        return new FrameSnapshot
        {
            StringCount = 6,
            HighwayScrollSpeed = 1.3f,
            EnableNoteHitExplosions = false,
            EnableHitSparks = false,
            EnableEnhancedHitFeedback = false,
            Anchors = [new RenderAnchor { Time = 0, EndTime = 20_000, Fret = 5, Width = 4 }],
            AnchorCount = 1,
            Notes = notes.ToArray(),
            NoteCount = notes.Count
        };
    }
}
