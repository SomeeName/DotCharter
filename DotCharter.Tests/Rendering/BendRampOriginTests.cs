using DotCharter.Rendering.Batching;
using DotCharter.Rendering.Components.NoteRendering;
using DotCharter.Rendering.Data;
using DotCharter.Rendering.Data.Render;
using DotCharter.Rendering.Scene;
using DotCharter.Core.Graphics;

namespace DotCharter.Tests.Rendering;

/// <summary>
///     A bend curve belongs to its NOTE: at the note's own onset the string has
///     not been pushed yet, so the head sits on its lane and the tail ramps up
///     from there to the first bend point.
///     <para>
///         <see cref="NoteRenderMath.GetBendOffset" /> used to seed the ramp's
///         origin with <c>context.TimeMs</c> — the PLAYHEAD — instead of the
///         sound's start. The interpolation origin therefore moved every frame:
///         a note at 5 000 ms whose first bend point is at 5 200 ms was drawn
///         at 95% of full bend height while the playhead was still at 1 000 ms,
///         and visibly sank onto its string as it flew in. Bend height must be a
///         function of the CHART, not of where the transport happens to be.
///     </para>
/// </summary>
public sealed class BendRampOriginTests
{
    private const double NoteTime = 5_000;
    private const double BendTime = 5_200;
    private const float BendStep = 2f; // a full-tone bend

    [Theory]
    [InlineData(0)]      // note far ahead
    [InlineData(1_000)]  // note approaching
    [InlineData(4_900)]  // note nearly at the strike line
    [InlineData(5_000)]  // note ON the strike line
    public void The_offset_at_a_notes_own_onset_is_zero_at_every_playhead(double playheadMs)
    {
        var context = Context(playheadMs);

        var atOnset = NoteRenderMath.GetBendOffset(context, 0, 1, NoteTime, NoteTime);

        Assert.Equal(0f, atOnset, 4);
    }

    [Fact]
    public void The_whole_ramp_is_playhead_independent()
    {
        // Sample the ramp across the sustain from two very different transport
        // positions. Same chart, same note ⇒ byte-identical curve.
        var early = Context(0);
        var late = Context(4_900);

        for (var t = NoteTime; t <= BendTime; t += 25)
        {
            var a = NoteRenderMath.GetBendOffset(early, 0, 1, NoteTime, t);
            var b = NoteRenderMath.GetBendOffset(late, 0, 1, NoteTime, t);
            Assert.Equal(a, b, 4);
        }
    }

    [Fact]
    public void The_ramp_rises_linearly_from_the_note_to_the_first_bend_point()
    {
        var context = Context(0);

        // Halfway between onset and the bend point ⇒ half the step.
        var mid = NoteRenderMath.GetBendOffset(context, 0, 1, NoteTime, (NoteTime + BendTime) / 2);
        Assert.Equal(BendStep / 2f, mid, 3);

        // At the bend point itself ⇒ the full step, and it holds after.
        Assert.Equal(BendStep, NoteRenderMath.GetBendOffset(context, 0, 1, NoteTime, BendTime), 3);
        Assert.Equal(BendStep, NoteRenderMath.GetBendOffset(context, 0, 1, NoteTime, BendTime + 500), 3);
    }

    [Fact]
    public void A_bend_point_sitting_exactly_on_the_note_reads_as_its_step_immediately()
    {
        // The degenerate range the old code special-cased (range <= 0) must
        // survive the re-anchoring: an onset bend is already at its step.
        var snapshot = new FrameSnapshot
        {
            StringCount = 6,
            BendValues = [new RenderBendValue { Time = NoteTime, Step = BendStep }],
            BendValueCount = 1
        };
        var context = Context(0, snapshot);

        Assert.Equal(BendStep, NoteRenderMath.GetBendOffset(context, 0, 1, NoteTime, NoteTime), 3);
    }

    private static RenderContext Context(double playheadMs, FrameSnapshot? snapshot = null)
    {
        snapshot ??= new FrameSnapshot
        {
            StringCount = 6,
            BendValues = [new RenderBendValue { Time = BendTime, Step = BendStep }],
            BendValueCount = 1
        };

        return new RenderContext
        {
            Snapshot = snapshot,
            TimeMs = playheadMs,
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
