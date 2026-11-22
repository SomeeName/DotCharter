using DotCharter.Rendering.Batching;
using DotCharter.Rendering.Components.NoteRendering;
using DotCharter.Rendering.Data;
using DotCharter.Rendering.Data.Render;
using DotCharter.Rendering.Scene;
using DotCharter.Core.Graphics;

namespace DotCharter.Tests.Rendering;

/// <summary>
///     On the 3D highway: the peak labels follow the same one-label-per-gesture rule as the 2D timeline
///     (<c>BendGestures</c>). A plain bend through its middle point is one "↗Full", not "1/2" and "Full"; a release
///     tied in from a bend is one "↘Full" where it lands, never an up-arrowed "Full" at its start.
/// </summary>
public sealed class BendGesturePeakLabelTests
{
    private const double NoteTimeMs = 1_000;
    private const double SustainMs = 800;

    [Fact]
    public void APlainBendThroughItsMiddlePoint_IsOneFull() =>
        Assert.Equal(["↗Full"], Labels(NoteFlags.HasBends, 0f, (0, 0f), (200, 1f), (400, 2f)));

    [Fact]
    public void AReleaseTiedInFromABend_IsOneDownLabel() =>
        Assert.Equal(["↘Full"],
            Labels(NoteFlags.HasBends | NoteFlags.LinkPrevious, 2f, (0, 2f), (200, 1f), (400, 0f)));

    [Fact]
    public void APrebendReleased_SaysPrebend_ThenRelease() =>
        Assert.Equal(["PB Full", "↘Full"], Labels(NoteFlags.HasBends, 0f, (0, 2f), (400, 2f), (600, 0f)));

    private static string[] Labels(NoteFlags flags, float carry, params (double OffsetMs, float Step)[] points)
    {
        var context = new RenderContext
        {
            Snapshot = new FrameSnapshot
            {
                StringCount = 6,
                Anchors = [new RenderAnchor { Time = 0, EndTime = 100_000, Fret = 5, Width = 4 }],
                AnchorCount = 1,
                BendValues = [.. points.Select(p => new RenderBendValue { Time = NoteTimeMs + p.OffsetMs, Step = p.Step })],
                BendValueCount = points.Length,
                Notes =
                [
                    new RenderNote
                    {
                        Time = NoteTimeMs, Sustain = SustainMs, StringIndex = 0, Fret = 7, Flags = flags,
                        BendOffset = 0, BendCount = points.Length, BendCarry = carry
                    }
                ],
                NoteCount = 1
            },
            TimeMs = 0,
            BoardWidth = 10f,
            QuadBatcher = new QuadBatcher(null!), // never flushed; nothing reaches a device
            TimeToZScale = HighwaySpeedPolicy.TimeToZScale(HighwaySpeedPolicy.MaxScrollSpeed),
            FadeEndZ = HighwaySpeedPolicy.MaxDrawDistanceZ,
            FadeStartZ = HighwaySpeedPolicy.FadeStartZ(HighwaySpeedPolicy.MaxDrawDistanceZ),
            HorizonMs = HighwaySpeedPolicy.HorizonMs(HighwaySpeedPolicy.MaxScrollSpeed, HighwaySpeedPolicy.MaxDrawDistanceZ)
        };

        SustainRenderer.Render(context);
        return [.. Enumerable.Range(0, context.Deferred3DLabelCount).Select(i => context.Deferred3DLabels[i].Text)];
    }
}
