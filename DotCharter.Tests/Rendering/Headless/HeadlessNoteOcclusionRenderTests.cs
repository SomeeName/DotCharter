using DotCharter.Rendering.Data;
using DotCharter.Rendering.Data.Render;
using DotCharter.Rendering.Headless;

namespace DotCharter.Tests.Rendering.Headless;

/// <summary>
///     A NEARER note head must hide a FARTHER one where the two overlap on
///     screen, through the REAL 3D highway. Notes and chords are drawn in the
///     transparent pass, which writes no depth, so draw order IS occlusion: a far
///     gem staged after a near one shows through it. Single heads used to be
///     staged near-to-far and every single head before every chord, so a far
///     single gem or a far chord painted over the nearer note in front of it.
///     The pixels both heads cover are found by rendering each alone; in the
///     frame with both, those pixels must be the near head's. The CPU-side
///     order is pinned by <see cref="NoteHeadDrawOrderTests" />.
///     GPU-gated: <c>DOTCHARTER_GPU_TESTS=1</c>; <c>DOTCHARTER_GPU_DUMP=1</c>
///     writes <c>vk_note_occlusion_*</c>.
/// </summary>
[Collection("HeadlessGpu")]
public class HeadlessNoteOcclusionRenderTests
{
    private const int Width = 1600;
    private const int Height = 900;
    private const double FrameMs = 1_000;
    private const double NearMs = 1_350;
    private const double FarMs = 1_400;
    private const int Fret = 7;

    /// <summary>Only the highway ABOVE this row is compared: at the hit line the
    /// string a note is on lights up across the whole frame, a difference between
    /// the renders that has nothing to do with which head is in front.</summary>
    private const int HitLineTop = Height * 2 / 3;

    [Fact]
    public void A_near_single_note_hides_a_far_single_note_on_the_same_string()
    {
        Assert.SkipWhen(!GoldenFrame.GpuTestsEnabled, "GPU headless test — set DOTCHARTER_GPU_TESTS=1 to run.");

        using var r = new HeadlessHighwayRenderer(useRealChartTextures: true);
        var both = r.Render(Snapshot(Single(NearMs, 2), Single(FarMs, 2)), Width, Height, FrameMs);
        var nearOnly = r.Render(Snapshot(Single(NearMs, 2)), Width, Height, FrameMs);
        var farOnly = r.Render(Snapshot(Single(FarMs, 2)), Width, Height, FrameMs);
        var bare = r.Render(Snapshot(), Width, Height, FrameMs);
        GoldenFrame.Dump(both, Width, Height, "vk_note_occlusion_single_single");

        AssertNearWins(both, nearOnly, farOnly, bare, "single note", "single note");
    }

    [Fact]
    public void A_near_single_note_hides_a_far_chord()
    {
        Assert.SkipWhen(!GoldenFrame.GpuTestsEnabled, "GPU headless test — set DOTCHARTER_GPU_TESTS=1 to run.");

        using var r = new HeadlessHighwayRenderer(useRealChartTextures: true);
        var both = r.Render(WithChord(Snapshot(Single(NearMs, 2)), FarMs, 1, 2, 3), Width, Height, FrameMs);
        var nearOnly = r.Render(Snapshot(Single(NearMs, 2)), Width, Height, FrameMs);
        var farOnly = r.Render(WithChord(Snapshot(), FarMs, 1, 2, 3), Width, Height, FrameMs);
        var bare = r.Render(Snapshot(), Width, Height, FrameMs);
        GoldenFrame.Dump(both, Width, Height, "vk_note_occlusion_single_chord");

        AssertNearWins(both, nearOnly, farOnly, bare, "single note", "chord");
    }

    [Fact]
    public void A_near_chord_hides_a_far_single_note()
    {
        Assert.SkipWhen(!GoldenFrame.GpuTestsEnabled, "GPU headless test — set DOTCHARTER_GPU_TESTS=1 to run.");

        using var r = new HeadlessHighwayRenderer(useRealChartTextures: true);
        var both = r.Render(WithChord(Snapshot(Single(FarMs, 2)), NearMs, 1, 2, 3), Width, Height, FrameMs);
        var nearOnly = r.Render(WithChord(Snapshot(), NearMs, 1, 2, 3), Width, Height, FrameMs);
        var farOnly = r.Render(Snapshot(Single(FarMs, 2)), Width, Height, FrameMs);
        var bare = r.Render(Snapshot(), Width, Height, FrameMs);
        GoldenFrame.Dump(both, Width, Height, "vk_note_occlusion_chord_single");

        AssertNearWins(both, nearOnly, farOnly, bare, "chord", "single note");
    }

    private static void AssertNearWins(byte[] both, byte[] nearOnly, byte[] farOnly, byte[] bare, string nearName,
        string farName)
    {
        var overlap = 0;
        var nearWins = 0;
        for (var i = 0; i < HitLineTop * Width * 4; i += 4)
        {
            if (Distance(nearOnly, bare, i) < 60 || Distance(farOnly, bare, i) < 60 ||
                Distance(nearOnly, farOnly, i) < 60)
            {
                continue;
            }

            overlap++;
            if (Distance(both, nearOnly, i) < Distance(both, farOnly, i)) nearWins++;
        }

        Assert.True(overlap > 50,
            $"the near {nearName} and the far {farName} overlap on only {overlap} pixels — the scene tests nothing");
        // Not every overlap pixel can be the near head's: its soft halo and the
        // gem's rim are translucent on purpose, so the far gem tints them. Drawn
        // in the wrong order the near head won on 2 of 99 and 0 of 298.
        Assert.True(nearWins > overlap * 0.75,
            $"the near {nearName} is in front on only {nearWins} of the {overlap} pixels where it overlaps " +
            $"the far {farName}");
    }

    private static RenderNote Single(double timeMs, int stringIndex) =>
        new() { Time = timeMs, StringIndex = (byte)stringIndex, Fret = Fret };

    private static FrameSnapshot WithChord(FrameSnapshot snapshot, double timeMs, params int[] strings)
    {
        snapshot.Chords =
            [new RenderChord { Time = timeMs, TemplateId = -1, NoteOffset = 0, NoteCount = (byte)strings.Length }];
        snapshot.ChordCount = 1;
        snapshot.ChordNotes = strings
            .Select(s => new RenderChordNote { StringIndex = (byte)s, Fret = Fret })
            .ToArray();
        snapshot.ChordNoteCount = strings.Length;
        return snapshot;
    }

    private static int Distance(byte[] a, byte[] b, int i) =>
        Math.Abs(a[i] - b[i]) + Math.Abs(a[i + 1] - b[i + 1]) + Math.Abs(a[i + 2] - b[i + 2]);

    private static FrameSnapshot Snapshot(params RenderNote[] notes) => new()
    {
        StringCount = 6,
        HighwayScrollSpeed = 1.3f,
        EnableNoteHitExplosions = false,
        EnableHitSparks = false,
        EnableEnhancedHitFeedback = false,
        Anchors = [new RenderAnchor { Time = 0, EndTime = 20_000, Fret = 5, Width = 4 }],
        AnchorCount = 1,
        Notes = notes,
        NoteCount = notes.Length
    };
}
