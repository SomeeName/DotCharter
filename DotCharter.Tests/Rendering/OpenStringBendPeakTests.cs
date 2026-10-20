using DotCharter.Rendering.Batching;
using DotCharter.Rendering.Components.NoteRendering;
using DotCharter.Rendering.Data;
using DotCharter.Rendering.Data.Render;
using DotCharter.Rendering.Scene;
using DotCharter.Core.Graphics;

namespace DotCharter.Tests.Rendering;

/// <summary>
///     A bend's PEAK — the white shine marker plus the "v Full" / "v 1/2" step
///     label — is drawn from <c>SustainRenderer</c>, and only the FRETTED tail
///     path ever called it. `RenderOpenTailSegment` returns without it.
///     <para>
///         So an open-string bend displaced its tail (the open path applies
///         <c>GetBendOffset</c> per segment) while showing no marker and no step
///         count at all — the player could see the string move but was never told
///         how far. And because the highway's open-note rule is
///         <c>fret &lt;= Capo</c>, that is not an exotic case: on a capo-3 chart
///         EVERY bend at fret 3 took the open path and lost its label.
///     </para>
///     <para>
///         Asserted on <see cref="RenderContext.Deferred3DLabelCount" /> — bend
///         labels are 3D text, which a geometry pass may not stage directly, so
///         they go through the deferral queue that `BendLabelComponent` drains in
///         the Ui pass. That queue is the observable seam; the font atlas is not
///         (it needs a GPU).
///     </para>
/// </summary>
public sealed class OpenStringBendPeakTests
{
    private const double NoteTimeMs = 1_000;
    private const double SustainMs = 800;
    private const float FullBendStep = 2f;

    [Theory]
    [InlineData(0, 0)]  // an open string, no capo
    [InlineData(3, 3)]  // fret 3 under a capo on 3 — open by the highway's rule
    [InlineData(0, 5)]  // an open string under a capo
    public void An_open_string_bend_still_shows_its_step_count(byte fret, int capo)
    {
        var context = Context(fret, capo);

        SustainRenderer.Render(context);

        Assert.Equal(1, context.Deferred3DLabelCount);
        Assert.Equal("v Full", context.Deferred3DLabels[0].Text);
    }

    [Fact]
    public void A_fretted_bend_is_unchanged()
    {
        var context = Context(fret: 7, capo: 0);

        SustainRenderer.Render(context);

        Assert.Equal(1, context.Deferred3DLabelCount);
        Assert.Equal("v Full", context.Deferred3DLabels[0].Text);
    }

    [Fact]
    public void The_open_peak_sits_on_the_bent_strings_lane_over_the_anchor()
    {
        var context = Context(fret: 0, capo: 0);
        var (spanX0, spanX1) = AnchorFretSpan.WorldSpanAt(
            context.Snapshot.Anchors, context.Snapshot.AnchorCount, NoteTimeMs);

        SustainRenderer.Render(context);

        var label = context.Deferred3DLabels[0];
        // x: the open bar's centre, the same origin the open tail is drawn from.
        Assert.Equal((spanX0 + spanX1) / 2f, label.Position.X, 3);
        // y: above the string's own lane, lifted by the bend's height — never
        // the bare lane, which would put the label inside the tail.
        var lane = FretboardConstants.BottomStringPosition +
                   FretboardConstants.StringLaneIndex(0, 6, false) * FretboardConstants.StringDistance;
        Assert.True(label.Position.Y > lane,
            $"the peak label ({label.Position.Y}) must sit above string 0's lane ({lane})");
    }

    private static RenderContext Context(byte fret, int capo)
    {
        var snapshot = new FrameSnapshot
        {
            StringCount = 6,
            Capo = capo,
            Anchors = [new RenderAnchor { Time = 0, EndTime = 100_000, Fret = 1, Width = 4 }],
            AnchorCount = 1,
            // One bend point, mid-sustain, a full tone up.
            BendValues = [new RenderBendValue { Time = NoteTimeMs + SustainMs / 2, Step = FullBendStep }],
            BendValueCount = 1,
            Notes =
            [
                new RenderNote
                {
                    Time = NoteTimeMs, Sustain = SustainMs, StringIndex = 0, Fret = fret,
                    Flags = NoteFlags.HasBends, BendOffset = 0, BendCount = 1
                }
            ],
            NoteCount = 1
        };

        return new RenderContext
        {
            Snapshot = snapshot,
            TimeMs = 0,
            BoardWidth = 10f,
            QuadBatcher = new QuadBatcher(null!), // never flushed; nothing reaches a device
            TimeToZScale = HighwaySpeedPolicy.TimeToZScale(HighwaySpeedPolicy.MaxScrollSpeed),
            FadeEndZ = HighwaySpeedPolicy.MaxDrawDistanceZ,
            FadeStartZ = HighwaySpeedPolicy.FadeStartZ(HighwaySpeedPolicy.MaxDrawDistanceZ),
            HorizonMs = HighwaySpeedPolicy.HorizonMs(
                HighwaySpeedPolicy.MaxScrollSpeed, HighwaySpeedPolicy.MaxDrawDistanceZ)
        };
    }
}
