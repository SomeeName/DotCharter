using System.Numerics;
using Avalonia.Media;
using DotCharter.Rendering.Data.Render;
using DotCharter.Rendering.Scene;

namespace DotCharter.Rendering.Components.NoteRendering;

internal static class BendRenderer
{
    private static readonly Color ShineColor = Color.FromArgb(220, 255, 255, 255);

    /// <summary>Bend points past this many carry no label (the peaks still draw).</summary>
    private const int MaxLabelledBendPoints = 64;

    /// <summary>World scale of the peak label. Named so the Ui-pass drain and this
    /// producer cannot drift apart.</summary>
    internal const float LabelScale = 0.004f;

    /// <summary>
    ///     The shine on every raised bend point still ahead of the now line, and the label of each bend GESTURE
    ///     (<see cref="BendGestures" />, the rule the 2D timeline shares): one "↗Full" at a bend's peak, one
    ///     "↘Full" where a release lands, "PB Full" on a prebend.
    /// </summary>
    public static void RenderPeaks(RenderContext context, in BendTail tail, float baseNy, float nX)
    {
        var count = Math.Min(tail.BendCount, MaxLabelledBendPoints);
        Span<float> steps = stackalloc float[count];
        for (var b = 0; b < count; b++) steps[b] = context.Snapshot.BendValues[tail.BendOffset + b].Step;
        Span<BendGesture> gestures = stackalloc BendGesture[count];
        var firstAtOnset = count > 0 && Math.Abs(context.Snapshot.BendValues[tail.BendOffset].Time - tail.Time) < 1.0;
        float? carriedIn = (tail.Flags & NoteFlags.LinkPrevious) != 0 ? tail.BendCarry : null;
        BendGestures.Classify(steps, firstAtOnset, carriedIn, gestures);

        for (var b = 0; b < tail.BendCount; b++)
        {
            var bend = context.Snapshot.BendValues[tail.BendOffset + b];
            var labelled = b < count && gestures[b].Kind != BendGestureKind.None;
            if ((bend.Step <= 0 && !labelled) || bend.Time < context.TimeMs) continue;
            var bZ = context.GetZDepthForTime(bend.Time, context.TimeMs);
            if (bZ > context.FadeEndZ) continue;

            var bY = baseNy + bend.Step * NoteRenderMath.BendMultiplier;
            if (bend.Step > 0)
            {
                context.QuadBatcher.AddQuad(
                    new Vector3(nX - 0.05f, bY + 0.1f, bZ), new Vector3(nX + 0.05f, bY + 0.1f, bZ),
                    new Vector3(nX + 0.05f, bY - 0.1f, bZ), new Vector3(nX - 0.05f, bY - 0.1f, bZ),
                    Vector3.UnitZ, ShineColor);
            }

            // DEFERRED, not DrawText3D: RenderPeaks is reached from
            // NotesRenderComponent (through SustainRenderer), which lives in
            // TransparentElements — and 3D text staged from a geometry
            // pass is drawn against a null atlas (frame 1) and then the
            // previous frame's ORTHO HUD matrix, i.e. nowhere near the
            // board. BendLabelComponent draws these in the Ui pass.
            if (labelled)
            {
                context.Defer3DLabel(new Deferred3DLabel(
                    BendGestures.Text(gestures[b]), new Vector3(nX, bY + 0.25f, bZ), Colors.White, LabelScale));
            }
        }
    }
}

/// <summary>What <see cref="BendRenderer.RenderPeaks" /> reads of a tail: its onset, flags, bend points and the
/// pitch a tie carried in.</summary>
internal readonly record struct BendTail(double Time, NoteFlags Flags, int BendOffset, int BendCount, float BendCarry);
