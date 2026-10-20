using System.Numerics;
using Avalonia.Media;
using DotCharter.Rendering.Scene;

namespace DotCharter.Rendering.Components.NoteRendering;

internal static class BendRenderer
{
    private static readonly Color ShineColor = Color.FromArgb(220, 255, 255, 255);

    // Precomputed "v <label>" text for the common integer half-step bends
    // (1 = ½, 2 = full, 3 = 1½, 4 = 2 steps). Index 0 is unused — bend.Step > 0
    // is guaranteed by the caller — so the per-visible-bend, per-frame text path
    // allocates nothing for the normal cases (only exotic fractional bends fall
    // through to the formatted string).
    private static readonly string[] BendLabels = ["v 0", "v 1/2", "v Full", "v 1 1/2", "v 2"];

    /// <summary>World scale of the peak label. Named so the Ui-pass drain and this
    /// producer cannot drift apart.</summary>
    internal const float LabelScale = 0.004f;

    public static void RenderPeaks(RenderContext context, int bendOffset, int bendCount, float baseNy, float nX)
    {
        for (var b = 0; b < bendCount; b++)
        {
            var bend = context.Snapshot.BendValues[bendOffset + b];
            if (bend.Step > 0 && bend.Time >= context.TimeMs)
            {
                var bZ = context.GetZDepthForTime(bend.Time, context.TimeMs);
                if (bZ <= context.FadeEndZ)
                {
                    var bY = baseNy + bend.Step * NoteRenderMath.BendMultiplier;
                    context.QuadBatcher.AddQuad(
                        new Vector3(nX - 0.05f, bY + 0.1f, bZ), new Vector3(nX + 0.05f, bY + 0.1f, bZ),
                        new Vector3(nX + 0.05f, bY - 0.1f, bZ), new Vector3(nX - 0.05f, bY - 0.1f, bZ),
                        Vector3.UnitZ, ShineColor);

                    // DEFERRED, not DrawText3D: RenderPeaks is reached from
                    // NotesRenderComponent (through SustainRenderer), which lives in
                    // TransparentElements — and 3D text staged from a geometry
                    // pass is drawn against a null atlas (frame 1) and then the
                    // previous frame's ORTHO HUD matrix, i.e. nowhere near the
                    // board. BendLabelComponent draws these in the Ui pass.
                    context.Defer3DLabel(new Deferred3DLabel(
                        LabelFor(bend.Step), new Vector3(nX, bY + 0.25f, bZ), Colors.White, LabelScale));
                }
            }
        }
    }

    private static string LabelFor(float step)
    {
        var idx = (int)MathF.Round(step);
        if (idx is >= 1 and <= 4 && MathF.Abs(step - idx) < 0.01f)
            return BendLabels[idx];
        return "v " + (step / 2f).ToString("0.#");
    }
}
