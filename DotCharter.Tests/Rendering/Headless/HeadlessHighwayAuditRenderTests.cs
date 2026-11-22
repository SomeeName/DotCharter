using DotCharter.Rendering.Components.NoteRendering;
using DotCharter.Rendering.Data;
using DotCharter.Rendering.Data.Render;
using DotCharter.Rendering.Headless;

namespace DotCharter.Tests.Rendering.Headless;

/// <summary>
///     Issue #470 through the REAL 3D highway: the hand-shape bracket's soft bands
///     are drawn on both sides (smoke; the gradient axis is a unit test), and
///     with inverted strings the chord head in the higher lane must be the one in
///     front where a bend stacks two heads. The CPU-side order and gradient axis
///     are pinned by <see cref="HighwayAuditFindingsTests" />.
///     GPU-gated: <c>DOTCHARTER_GPU_TESTS=1</c>; <c>DOTCHARTER_GPU_DUMP=1</c>
///     writes <c>vk_audit470_*</c>.
/// </summary>
[Collection("HeadlessGpu")]
public class HeadlessHighwayAuditRenderTests
{
    private const int Width = 1600;
    private const int Height = 900;
    private const double FrameMs = 1_000;

    [Fact]
    public void Both_hand_shape_bands_are_drawn_at_the_strike_line()
    {
        Assert.SkipWhen(!GoldenFrame.GpuTestsEnabled, "GPU headless test — set DOTCHARTER_GPU_TESTS=1 to run.");

        using var r = new HeadlessHighwayRenderer(useRealChartTextures: true);
        var bare = r.Render(Snapshot(), Width, Height, FrameMs);
        var shape = Snapshot();
        shape.HandShapes = [new RenderHandShape { Time = 0, EndTime = 20_000, TemplateId = -1 }];
        shape.HandShapeCount = 1;
        var with = r.Render(shape, Width, Height, FrameMs);
        GoldenFrame.Dump(with, Width, Height, "vk_audit470_handshape_band");
        GoldenFrame.Dump(bare, Width, Height, "vk_audit470_handshape_bare");

        // Both bands add light near the strike line. The soft-edge direction itself is
        // pinned on the staged vertices (HighwayAuditFindingsTests): a pixel metric
        // cannot tell the two gradients apart because the solid centre strip dominates.
        long left = 0, right = 0;
        for (var y = Height * 2 / 3; y < Height; y++)
        {
            for (var x = 0; x < Width; x++)
            {
                var i = (y * Width + x) * 4;
                var d = Math.Abs(with[i] - bare[i]) + Math.Abs(with[i + 1] - bare[i + 1]) +
                        Math.Abs(with[i + 2] - bare[i + 2]);
                if (x < Width / 2) left += d;
                else right += d;
            }
        }

        Assert.True(left > 0 && right > 0, $"a band adds no light near the strike line (left {left}, right {right})");
    }

    [Fact]
    public void With_inverted_strings_the_higher_lane_head_is_in_front_where_a_bend_stacks_two()
    {
        Assert.SkipWhen(!GoldenFrame.GpuTestsEnabled, "GPU headless test — set DOTCHARTER_GPU_TESTS=1 to run.");

        using var r = new HeadlessHighwayRenderer(useRealChartTextures: true);
        var bare = r.Render(Snapshot(), Width, Height, FrameMs);
        // Inverted: lane = string index. String 1 bent up one lane lands on string 2's head.
        var both = r.Render(Chord(true, true), Width, Height, FrameMs);
        var topOnly = r.Render(Chord(false, true), Width, Height, FrameMs);
        var bentOnly = r.Render(Chord(true, false), Width, Height, FrameMs);
        GoldenFrame.Dump(both, Width, Height, "vk_audit470_chord_heads");

        var overlap = 0;
        var topWins = 0;
        for (var i = 0; i < Height * 2 / 3 * Width * 4; i += 4)
        {
            if (Distance(topOnly, bare, i) < 60 || Distance(bentOnly, bare, i) < 60 ||
                Distance(topOnly, bentOnly, i) < 60)
            {
                continue;
            }

            overlap++;
            if (Distance(both, topOnly, i) < Distance(both, bentOnly, i)) topWins++;
        }

        Assert.True(overlap > 50, $"the two heads overlap on only {overlap} pixels: the scene tests nothing");
        Assert.True(topWins > overlap * 0.6, $"the higher lane is in front on only {topWins} of {overlap} pixels");
    }

    private static FrameSnapshot Chord(bool bentString1, bool string2)
    {
        var s = Snapshot();
        s.InvertStrings = true;
        var notes = new List<RenderChordNote>();
        if (bentString1)
            notes.Add(new RenderChordNote { StringIndex = 1, Fret = 7, BendOffset = 0, BendCount = 1 });
        if (string2) notes.Add(new RenderChordNote { StringIndex = 2, Fret = 7 });
        s.Chords = [new RenderChord { Time = 1_400, TemplateId = -1, NoteOffset = 0, NoteCount = (byte)notes.Count }];
        s.ChordCount = 1;
        s.ChordNotes = [.. notes];
        s.ChordNoteCount = notes.Count;
        s.BendValues = [new RenderBendValue { Time = 1_400, Step = FretboardConstants.StringDistance / NoteRenderMath.BendMultiplier }];
        return s;
    }

    private static int Distance(byte[] a, byte[] b, int i) =>
        Math.Abs(a[i] - b[i]) + Math.Abs(a[i + 1] - b[i + 1]) + Math.Abs(a[i + 2] - b[i + 2]);

    private static FrameSnapshot Snapshot() => new()
    {
        StringCount = 6,
        HighwayScrollSpeed = 1.3f,
        EnableNoteHitExplosions = false,
        EnableHitSparks = false,
        EnableEnhancedHitFeedback = false,
        Anchors = [new RenderAnchor { Time = 0, EndTime = 20_000, Fret = 5, Width = 4 }],
        AnchorCount = 1
    };
}
