using System.Numerics;
using Avalonia.Media;
using DotCharter.Rendering.Data;
using DotCharter.Rendering.Scene;

namespace DotCharter.Rendering.Components.NoteRendering;

internal static class HandShapeRenderer
{
    /// <summary>Every hand shape's bracket, farthest first (see
    /// <see cref="NoteHeadRenderer.Render" />).</summary>
    public static void Render(RenderContext context)
    {
        for (var i = context.Snapshot.HandShapeCount - 1; i >= 0; i--) RenderHandShape(context, i);
    }

    /// <summary>Hand shape <paramref name="index" />'s arpeggio bracket (or
    /// nothing). Expects the pass untextured.</summary>
    public static void RenderHandShape(RenderContext context, int index)
    {
        var hs = context.Snapshot.HandShapes[index];
        if (hs.Time < context.TimeMs || hs.Time >= context.TimeMs + context.HorizonMs) return;
        if (hs.TemplateId < 0 || hs.TemplateId >= context.Snapshot.TemplateCount) return;

        var template = context.Snapshot.Templates[hs.TemplateId];
        var zStart = context.GetZDepthForTime(hs.Time, context.TimeMs);
        if (zStart > context.FadeEndZ) return;

        // ONE clamp for every "where is the hand" answer (AnchorFretSpan):
        // reading `anchor.Fret` raw would let a fret-0 anchor put the
        // nut-side bracket post at GetLinearFretPosition(-1) — a whole fret
        // to the LEFT of the nut, and one fret away from the chord box
        // inside it.

        // The NAME is drawn by ChordNameRenderer, Rocksmith-style, at the
        // nut side of the neck — centred above the span put it straight over
        // the incoming note heads of a wide shape.
        if (template.Arpeggio)
        {
            // Rocksmith-style arpeggio brackets: a closed '[' ']' pair in
            // the arpeggio purple (cf. HighwayRenderComponent), spanning
            // the full string range across the hand shape's fret span.
            // Flat on the playhead-facing plane — no note-style rotation.
            var yBottom = FretboardConstants.ChartboardYPosition;
            var yTop = FretboardConstants.BottomStringPosition +
                       context.Snapshot.StringCount * FretboardConstants.StringDistance;
            var (x0, x1) = NoteRenderMath.GetAnchorSpan(context, hs.Time);

            var bracketColor = Color.FromArgb(200, 192, 64, 255);
            const float thickness = 0.06f;
            var armLength = Math.Min(0.35f, (x1 - x0) * 0.25f);

            DrawBracket(context, x0, +1f, yBottom, yTop, zStart, thickness, armLength, bracketColor);
            DrawBracket(context, x1, -1f, yBottom, yTop, zStart, thickness, armLength, bracketColor);
        }
    }

    /// <summary>One bracket half: a full-height vertical post at
    /// <paramref name="xEdge"/> with short top and bottom arms pointing
    /// inward (<paramref name="inward"/> = +1 for '[', -1 for ']').</summary>
    private static void DrawBracket(RenderContext context, float xEdge, float inward, float yBottom, float yTop,
        float z, float thickness, float armLength, Color color)
    {
        var n = new Vector3(0, 0, -1);

        var postX0 = Math.Min(xEdge, xEdge + inward * thickness);
        var postX1 = Math.Max(xEdge, xEdge + inward * thickness);
        context.QuadBatcher.AddQuad(new Vector3(postX0, yTop, z), new Vector3(postX1, yTop, z),
            new Vector3(postX1, yBottom, z), new Vector3(postX0, yBottom, z), n, color);

        var armX0 = Math.Min(xEdge, xEdge + inward * armLength);
        var armX1 = Math.Max(xEdge, xEdge + inward * armLength);
        context.QuadBatcher.AddQuad(new Vector3(armX0, yTop, z), new Vector3(armX1, yTop, z),
            new Vector3(armX1, yTop - thickness, z), new Vector3(armX0, yTop - thickness, z), n, color);
        context.QuadBatcher.AddQuad(new Vector3(armX0, yBottom + thickness, z),
            new Vector3(armX1, yBottom + thickness, z),
            new Vector3(armX1, yBottom, z), new Vector3(armX0, yBottom, z), n, color);
    }
}
