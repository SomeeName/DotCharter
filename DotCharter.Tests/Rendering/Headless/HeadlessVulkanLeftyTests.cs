using DotCharter.Rendering.Data;
using DotCharter.Rendering.Data.Render;
using DotCharter.Rendering.Headless;

namespace DotCharter.Tests.Rendering.Headless;

/// <summary>
///     v0.34 lefty flip: the profile-level mirror must actually mirror the 3D
///     highway. Structural (never byte-exact): the two frames differ
///     substantially AND the horizontal content bias swaps sides — the lefty
///     frame's left-half density matches the righty frame's right-half and
///     vice versa, with a vacuity guard proving the fixture is asymmetric
///     enough for the swap to mean something.
/// </summary>
[Collection("HeadlessGpu")]
public class HeadlessVulkanLeftyTests
{
    private const int Width = 640;
    private const int Height = 360;

    /// <summary>Deliberately X-asymmetric: anchor + notes at frets 1-5 on a
    /// 24-fret board put the content off-centre so the mirror has a side to
    /// swap.</summary>
    private static FrameSnapshot BuildAsymmetricChart()
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

    [Fact]
    public void LeftyFlip_MirrorsTheHighwayHorizontally()
    {
        Assert.SkipWhen(!GoldenFrame.GpuTestsEnabled,
            "GPU headless test — set DOTCHARTER_GPU_TESTS=1 to run.");

        using var r = new HeadlessHighwayRenderer();
        var snap = BuildAsymmetricChart();

        var righty = r.Render(snap, Width, Height, timeMs: 0);
        snap.LeftyFlip = true;
        var lefty = r.Render(snap, Width, Height, timeMs: 0);
        GoldenFrame.Dump(righty, Width, Height, "vk_lefty_off");
        GoldenFrame.Dump(lefty, Width, Height, "vk_lefty_on");

        // Both rendered something, and the flip visibly changed the frame.
        Assert.True(GoldenFrame.NonBlackRatio(lefty) > 0.05, "Lefty frame is ~all black.");
        var difference = GoldenFrame.DifferenceRatio(righty, lefty, 8);
        Assert.True(difference > 0.02,
            $"Lefty flip barely changed the frame (difference ratio {difference:P1}).");

        // The horizontal content bias must SWAP sides.
        var rightyLeft = GoldenFrame.NonBlackRatioInRegion(righty, Width, Height, 0.0, 0.0, 0.5, 1.0);
        var rightyRight = GoldenFrame.NonBlackRatioInRegion(righty, Width, Height, 0.5, 0.0, 1.0, 1.0);
        var leftyLeft = GoldenFrame.NonBlackRatioInRegion(lefty, Width, Height, 0.0, 0.0, 0.5, 1.0);
        var leftyRight = GoldenFrame.NonBlackRatioInRegion(lefty, Width, Height, 0.5, 0.0, 1.0, 1.0);

        // Vacuity guard: the fixture must actually be asymmetric, or the swap
        // assertions below would pass on ANY change.
        Assert.True(Math.Abs(rightyLeft - rightyRight) > 0.005,
            $"Fixture renders symmetric halves ({rightyLeft:P1} vs {rightyRight:P1}) — the swap check is vacuous.");

        Assert.True(Math.Abs(leftyLeft - rightyRight) < 0.05 &&
                    Math.Abs(leftyRight - rightyLeft) < 0.05,
            $"Halves did not swap: righty L/R {rightyLeft:P1}/{rightyRight:P1}, " +
            $"lefty L/R {leftyLeft:P1}/{leftyRight:P1}.");
    }
}
