using System.Numerics;
using Avalonia.Media;
using DotCharter.Rendering.Data;
using DotCharter.Rendering.Data.Render;
using DotCharter.Rendering.Scene;
using DotCharter.Rendering.Vulkan;

namespace DotCharter.Rendering.Components;

public class HighwayRenderComponent : IRenderable
{
    // Constant lane/tick hex hoisted out of the per-frame (and per-visible-beat)
    // draw paths — Color.Parse re-parses the string on every call.
    private static readonly Color FretLaneBaseColor = Color.Parse("#0088FF");
    private static readonly Color MeasureTickColor = Color.Parse("#00FFFF");
    private static readonly Color BeatTickColor = Color.Parse("#008888");

    private readonly int[] _fretsOpacity = new int[HighwayLaneOpacity.LaneCount];
    private int[] _spanFrets = new int[64];
    private int[] _spanWidths = new int[64];
    private double[] _spanStarts = new double[64];
    private double[] _spanEnds = new double[64];

    public void Initialize(IVulkanRenderService vulkanRenderer, RenderResources resources)
    {
    }

    private void EnsureSpanCapacity(int needed)
    {
        if (_spanFrets.Length >= needed) return;
        var n = _spanFrets.Length;
        while (n < needed) n *= 2;
        _spanFrets = new int[n];
        _spanWidths = new int[n];
        _spanStarts = new double[n];
        _spanEnds = new double[n];
    }

    // Beat-pulse intensity for this frame (0.14.2 C), computed once in
    // Render and consumed by the lane fills + the edge glow.
    private float _beatPulse;

    public void Render(RenderContext context)
    {
        _beatPulse = context.Snapshot.EnableBeatPulse
            ? BeatPulseMath.Compute(context.Snapshot.Beats, context.Snapshot.BeatCount, context.TimeMs)
            : 0f;

        RenderFretboardLanes(context);
        RenderFhpSpans(context);
        RenderBeats(context);
        RenderBeatPulseEdges(context);
        RenderAnchorSweep(context);
        RenderHandShapes(context);
    }

    /// <summary>
    ///     0.14.2 C: edge glow pulsing on the beat grid — two thin gradient
    ///     bands hugging the active anchor span's outer edges, flashing with
    ///     each beat (measures stronger) and fading toward the horizon.
    ///     Vertex-color work only; no shader involvement.
    /// </summary>
    private void RenderBeatPulseEdges(RenderContext context)
    {
        if (_beatPulse <= 0.01f) return;

        var (x0, x1) = GetAnchorSpan(context, context.TimeMs);
        var y = FretboardConstants.ChartboardYPosition - 0.0008f;
        const float bandWidth = 0.14f;
        var farZ = context.FadeEndZ * 0.6f;

        var glow = Color.FromArgb((byte)(90 * _beatPulse), 0, 180, 255);
        var clear = Color.FromArgb(0, 0, 180, 255);
        var normal = new Vector3(0, 1, 0);

        // Left edge: bright at the span border, fading outward and distally.
        context.QuadBatcher.AddQuad(
            new Vector3(x0 - bandWidth, y, 0f), new Vector3(x0, y, 0f),
            new Vector3(x0, y, farZ), new Vector3(x0 - bandWidth, y, farZ),
            normal, clear, glow, clear, clear);

        // Right edge, mirrored.
        context.QuadBatcher.AddQuad(
            new Vector3(x1, y, 0f), new Vector3(x1 + bandWidth, y, 0f),
            new Vector3(x1 + bandWidth, y, farZ), new Vector3(x1, y, farZ),
            normal, glow, clear, clear, clear);
    }

    /// <summary>
    ///     0.14.2 B: subtle floor highlight sliding toward the upcoming anchor
    ///     zone while the next hand-position change is inside the lead window
    ///     (companion of the dimmed fret-number preview): a faint zone tint at
    ///     the strike line that brightens as the change approaches, plus a
    ///     leading-edge band that sweeps in from the distance.
    /// </summary>
    private void RenderAnchorSweep(RenderContext context)
    {
        if (!context.Snapshot.EnableAnchorSweep) return;
        var preview = context.AnchorPreview;
        if (!preview.HasUpcoming || preview.FadeFraction <= 0f) return;

        // AnchorFretSpan, not a hand-rolled pair: clamping only the LOWER
        // bound made a fret-0 anchor's sweep one fret narrower than the span
        // every other pass draws.
        var (x0, x1) = AnchorFretSpan.WorldSpan(preview.Fret, preview.Width);

        // Amber to match the active fret-number/highlight accent. Kept faint —
        // this is a peripheral-vision cue, not a spotlight.
        var fade = preview.FadeFraction;
        var y = FretboardConstants.ChartboardYPosition - 0.0005f;

        // Zone tint at the strike line, brightening as the change approaches.
        var tintNear = Color.FromArgb((byte)(36 * fade), 255, 170, 0);
        var tintFar = Color.FromArgb(0, 255, 170, 0);
        context.QuadBatcher.AddSafeFlatQuad(x0, x1, 0f, 2.5f, y, tintNear, tintFar);

        // Leading edge sweeping in: starts a highway-length out and lands at
        // the strike line exactly when the anchor changes.
        var edgeZ = (1f - fade) * 8f;
        var edgeC = Color.FromArgb((byte)(70 * fade), 255, 170, 0);
        var edgeClear = Color.FromArgb(0, 255, 170, 0);
        context.QuadBatcher.AddSafeFlatQuad(x0, x1, edgeZ, edgeZ + 0.35f, y, edgeC, edgeClear);
    }

    private void RenderFretboardLanes(RenderContext context)
    {
        var fretsOpacity = _fretsOpacity;

        // Pure, allocation-free and unit-tested (HighwayLaneOpacityTests) —
        // the indexing here is what a malformed anchor could kill the whole
        // render loop through, so it must be provable without a GPU.
        HighwayLaneOpacity.Fill(context.Snapshot.Anchors, context.Snapshot.AnchorCount,
            context.TimeMs, context.HorizonMs, fretsOpacity);

        var baseColor = FretLaneBaseColor;
        // Fade each lane line to transparent toward the horizon:
        // opaque lines running all the way to FadeEndZ would converge into one
        // hard bright smear at the vanishing point (the "coloured line above
        // the highway"). Fading distally lets them dissolve like RsCharter's
        // lanes and removes the smear.
        var alphaFar = Color.FromArgb(0, baseColor.R, baseColor.G, baseColor.B);
        for (var i = 0; i <= 24; i++)
        {
            var x = FretboardConstants.GetLinearFretPosition(i);
            var c = Color.FromArgb((byte)fretsOpacity[i], baseColor.R, baseColor.G, baseColor.B);
            context.QuadBatcher.AddSafeFlatQuad(x - FretboardConstants.FretThickness,
                x + FretboardConstants.FretThickness, 0, context.FadeEndZ,
                FretboardConstants.ChartboardYPosition, c, alphaFar);
        }
    }

    private void DrawThickHandshapeLine(RenderContext context, float x, float y, float z0, float z1, Color color)
    {
        var alpha = Color.FromArgb(0, color.R, color.G, color.B);
        var lineThickness0 = FretboardConstants.FretThickness * 3f;
        var lineThickness1 = FretboardConstants.FretThickness * 9f;

        var x0 = x - lineThickness1;
        var x1 = x - lineThickness0;
        var x2 = x + lineThickness0;
        var x3 = x + lineThickness1;

        context.QuadBatcher.AddSafeFlatQuad(x0, x1, z0, z1, y, alpha, color);
        context.QuadBatcher.AddSafeFlatQuad(x1, x2, z0, z1, y, color, color);
        context.QuadBatcher.AddSafeFlatQuad(x2, x3, z0, z1, y, color, alpha);
    }

    private void RenderHandShapes(RenderContext context)
    {
        var handShapes = context.Snapshot.HandShapes;
        var handShapeCount = context.Snapshot.HandShapeCount;
        var anchors = context.Snapshot.Anchors;
        var anchorCount = context.Snapshot.AnchorCount;
        var timeMs = context.TimeMs;
        var yBottom = FretboardConstants.ChartboardYPosition + 0.0002f;

        for (var i = 0; i < handShapeCount; i++)
        {
            var hs = handShapes[i];
            // There is ONE look-ahead notion: RenderContext.HorizonMs. The
            // hardcoded 10 000 here would be BELOW
            // HighwaySpeedPolicy.MaxHorizonMs (12 000), so at max draw distance
            // with a slow scroll speed a hand-shape bracket between 10 s and
            // 12 s of look-ahead would be culled while the board was still showing
            // that stretch — the bracket would pop in mid-board instead of flying
            // in from the horizon. Never reintroduce a literal here.
            if (hs.EndTime < timeMs || hs.Time >= timeMs + context.HorizonMs) continue;

            var z0 = Math.Max(0f, context.GetZDepthForTime(hs.Time, timeMs));
            var z1 = context.GetZDepthForTime(hs.EndTime, timeMs);
            if (z0 > context.FadeEndZ || z1 <= z0) continue;

            RenderAnchor? anchor = null;
            for (var a = anchorCount - 1; a >= 0; a--)
            {
                if (anchors[a].Time <= hs.Time)
                {
                    anchor = anchors[a];
                    break;
                }
            }

            // Unclamped `anchor.Fret` would put xMin at
            // GetLinearFretPosition(-1) on a fret-0 anchor — a fret left of the
            // nut, and a fret away from the chord box inside the shape.
            var (xMin, xMax) = anchor.HasValue
                ? AnchorFretSpan.WorldSpan(anchor.Value.Fret, anchor.Value.Width)
                : AnchorFretSpan.WorldSpan(1, AnchorFretSpan.DefaultWidth);

            var template = context.Snapshot.TemplateCount > hs.TemplateId && hs.TemplateId >= 0
                ? context.Snapshot.Templates[hs.TemplateId]
                : default;
            // RsCharter PREVIEW_3D_ARPEGGIO (192,64,255) and PREVIEW_3D_LANE_BORDER (7,146,143)
            var color = template.Arpeggio
                ? Color.FromArgb(255, 192, 64, 255)
                : Color.FromArgb(255, 7, 146, 143);

            DrawThickHandshapeLine(context, xMin, yBottom, z0, z1, color);
            DrawThickHandshapeLine(context, xMax, yBottom, z0, z1, color);
        }
    }

    private void RenderFhpSpans(RenderContext context)
    {
        var anchors = context.Snapshot.Anchors;
        var anchorCount = context.Snapshot.AnchorCount;
        var timeMs = context.TimeMs;

        var maxSpans = anchorCount + 1;
        EnsureSpanCapacity(maxSpans);
        var spanFrets = _spanFrets;
        var spanWidths = _spanWidths;
        var spanStarts = _spanStarts;
        var spanEnds = _spanEnds;
        var spanCount = 0;
        var tCursor = timeMs;

        RenderAnchor? currentAnchor = null;
        for (var i = anchorCount - 1; i >= 0; i--)
        {
            if (anchors[i].Time <= timeMs)
            {
                currentAnchor = anchors[i];
                break;
            }
        }

        var activeFret = currentAnchor?.Fret ?? 1;
        var activeWidth = currentAnchor?.Width ?? 4;

        for (var i = 0; i < anchorCount; i++)
        {
            var a = anchors[i];
            if (a.Time <= timeMs || a.Time >= timeMs + context.HorizonMs) continue;
            if (a.Time > tCursor)
            {
                spanFrets[spanCount] = activeFret;
                spanWidths[spanCount] = activeWidth;
                spanStarts[spanCount] = tCursor;
                spanEnds[spanCount] = a.Time;
                spanCount++;
                tCursor = a.Time;
            }

            activeFret = a.Fret;
            activeWidth = a.Width;
        }

        spanFrets[spanCount] = activeFret;
        spanWidths[spanCount] = activeWidth;
        spanStarts[spanCount] = tCursor;
        spanEnds[spanCount] = timeMs + context.HorizonMs;
        spanCount++;

        var y = FretboardConstants.ChartboardYPosition - 0.001f;

        for (var i = 0; i < spanCount; i++)
        {
            var z0 = context.GetZDepthForTime(spanStarts[i], timeMs);
            var z1 = context.GetZDepthForTime(spanEnds[i], timeMs);

            if (z0 > context.FadeEndZ) continue;
            if (z1 > context.FadeEndZ) z1 = context.FadeEndZ;

            // AnchorFretSpan: `Math.Max(1, fret)` with an UNCLAMPED upper
            // bound drew three lanes for a fret-0 anchor while the beat lines,
            // the chord box and the camera all framed four.
            var (fMin, fMax) = AnchorFretSpan.FretRange(spanFrets[i], spanWidths[i]);

            for (var fret = fMin; fret <= fMax; fret++)
            {
                var x0 = FretboardConstants.GetLinearFretPosition(fret - 1);
                var x1 = FretboardConstants.GetLinearFretPosition(fret);
                var isDotted = fret is 3 or 5 or 7 or 9 or 12 or 15 or
                               17 or 19 or 21 or 24;
                // The lane fill does NOT breathe with the beat —
                // a per-beat alpha lift makes the whole highway visibly
                // pulsate. Beat pulse lives ONLY in the
                // subtle peripheral edge glow (RenderBeatPulseEdges); the lane
                // fill stays at a steady base tint.
                var laneAlpha = (byte)(isDotted ? 80 : 40);
                var laneColor = isDotted
                    ? Color.FromArgb(laneAlpha, 0, 180, 255)
                    : Color.FromArgb(laneAlpha, 0, 120, 220);
                // Use mode 11 (solid + near-fade) when enabled so the highway
                // dissolves into the chartboard like RsCharter; otherwise
                // fall back to the plain solid mode.
                var laneType = context.Snapshot.EnableHighwayNearFade ? 11.0f : 1.0f;
                context.QuadBatcher.AddSafeFlatQuad(x0, x1, z0, z1, y, laneColor, laneType);
            }
        }
    }

    private void RenderBeats(RenderContext context)
    {
        var measureC = Color.FromArgb(200, 255, 255, 255);
        var beatC = Color.FromArgb(80, 255, 255, 255);
        var alphaMeasure = Color.FromArgb(0, 255, 255, 255);
        var alphaBeat = Color.FromArgb(0, 255, 255, 255);
        var y = FretboardConstants.ChartboardYPosition + 0.0001f;

        for (var i = 0; i < context.Snapshot.BeatCount; i++)
        {
            var b = context.Snapshot.Beats[i];
            if (b.Time < context.TimeMs || b.Time >= context.TimeMs + context.HorizonMs) continue;

            var z = context.GetZDepthForTime(b.Time, context.TimeMs);
            if (z > context.FadeEndZ) continue;

            var (spanX0, spanX1) = GetAnchorSpan(context, b.Time);

            if (b.IsMeasure)
            {
                context.QuadBatcher.AddSafeFlatQuad(spanX0, spanX1, z - 0.2f, z - 0.1f, y, alphaMeasure, measureC);
                context.QuadBatcher.AddSafeFlatQuad(spanX0, spanX1, z - 0.1f, z + 0.1f, y, measureC, measureC);
                context.QuadBatcher.AddSafeFlatQuad(spanX0, spanX1, z + 0.1f, z + 0.2f, y, measureC, alphaMeasure);
                context.QuadBatcher.AddSafeFlatQuad(spanX0, spanX1, z - 0.015f, z + 0.015f, y + 0.001f,
                    MeasureTickColor);
            }
            else
            {
                context.QuadBatcher.AddSafeFlatQuad(spanX0, spanX1, z - 0.1f, z, y, alphaBeat, beatC);
                context.QuadBatcher.AddSafeFlatQuad(spanX0, spanX1, z, z + 0.1f, y, beatC, alphaBeat);
                context.QuadBatcher.AddSafeFlatQuad(spanX0, spanX1, z - 0.005f, z + 0.005f, y + 0.001f,
                    BeatTickColor);
            }
        }
    }

    private static (float x0, float x1) GetAnchorSpan(RenderContext context, double atTime)
        => AnchorFretSpan.WorldSpanAt(context.Snapshot.Anchors, context.Snapshot.AnchorCount, atTime);
}
