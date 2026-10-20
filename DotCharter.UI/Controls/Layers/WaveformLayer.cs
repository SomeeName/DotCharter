using DotCharter.Rendering.Timeline2D;
using DotCharter.UI.Controls.Layers.Helpers;
using DotCharter.UI.Controls.Themes;

namespace DotCharter.UI.Controls.Layers;

/// <summary>
/// LOD-aware DAW-style waveform: a translucent PEAK envelope carrying the
/// transients with a denser LOUDNESS BODY inside it, mirrored around a zero
/// line. The peak/body split is what makes the strip an audio cue rather than
/// decoration — peak alone saturates the moment one column spans more than a
/// few tens of milliseconds, so a zoomed-out peak-only waveform is a
/// featureless slab, and the distance between the two IS the crest factor
/// (percussive attack vs. sustained chord).
/// <para>The level for both comes from the pure <see cref="WaveformLod"/>;
/// the two pyramids are the same shape so ONE level indexes both.</para>
/// </summary>
public class WaveformLayer : ITimelineRenderLayer
{
    // The silhouette is ONE miter-joined ribbon per edge (top and
    // bottom), so a fat stroke does not break into seamed per-segment slabs.
    // The exp(0.2·step) curve must not reach a 15 px cap, which — even
    // seam-free — reads as a heavy band that swallows the shape at max zoom. Cap
    // it at 7 px: the exp curve is untouched below step ≈ 9.7 px (where it first
    // reaches 7), so every zoom short of "very deep" keeps its natural thickness;
    // only the deepest zoom, where the chunkiness shows, is thinned.
    private const float MaxStrokeThickness = 7f;
    // AA fade width (px) on each ribbon edge — matches AddThickLineRaw's pad so a
    // straight run is byte-identical to the old per-segment stroke.
    private const float OutlineAaPad = 1.0f;
    // Miter limit: at bends sharper than this the join bevels instead of
    // spiking, so a waveform peak never grows a long needle out of the ribbon.
    private const float OutlineMiterLimit = 3.0f;
    // Modest height cap (0.7). The mirrored body does not
    // climb into the outer string rows, so gems/labels on the top/bottom
    // strings sit clear of the waveform mass even in the loudest chorus.
    private const float HeightScale = 0.7f;
    // Thickness of the zero line, in px. One quad for the whole frame.
    private const float ZeroLineThickness = 1f;

    // Reusable silhouette-point buffers (interleaved x,y) for the two joined
    // outline ribbons. Grown on demand and reused across frames — the layer is
    // constructed once by ChartRenderer, so after warm-up the per-frame
    // allocation stays zero. Only touched on the outline path (high zoom); the
    // zoomed-out path never allocates or fills them.
    private float[] _topPts = [];
    private float[] _botPts = [];

    public void Render(TimelineBatcher batcher, ChartRenderContext rc)
    {
        if (rc.WaveformData is not { HasData: true } samples) return;
        if (WaveformLod.Compute(samples, rc.CurrentZoom) is not { } lod) return;

        var level = samples.Mipmaps[lod.Level];
        // Same index space as the peak level — FromBasePeaks derives both
        // pyramids in lockstep, so this never needs its own bounds logic.
        var body = samples.Body[lod.Level];
        var blockMs = lod.BlockMs;

        var wfHeightScale = rc.LaneAreaHeight / 2f * HeightScale;
        var wfCenter = (float)Math.Round(rc.TopMargin + rc.LaneAreaHeight / 2f);

        // Hoist Color → float conversions out of the per-sample loop. The
        // profiler showed get_R/get_G/get_B accounting for >20 ms total
        // here when the loop iterates ~1–2k times per frame.
        var fillColor = ChartTheme.Purple900Color;
        var strokeColor = ChartTheme.Purple800Color;
        // The theme fill is fully opaque, which turned the waveform into a
        // solid mass that swallowed the beat grid and lane lines behind it.
        // Draw the body translucent (silhouette stroke stays near-opaque) so
        // notes and grid stay legible on top of loud sections. 0.15.5 D3
        // quieted both a step further (0.55/0.85 → 0.45/0.72); 0.26 takes one
        // more (→ 0.35/0.60) off an owner screenshot where the purple mass
        // still competed with the fret chips at a glance: the waveform is
        // context, not content — notes and grid stay the loudest layer.
        const float fillOpacity = 0.35f;
        const float strokeOpacity = 0.60f;
        float fr = fillColor.R / 255f, fg = fillColor.G / 255f, fb = fillColor.B / 255f,
            fa = fillColor.A / 255f * fillOpacity;
        float sr = strokeColor.R / 255f, sg = strokeColor.G / 255f, sb = strokeColor.B / 255f,
            sa = strokeColor.A / 255f * strokeOpacity;

        // 0.14.3 B restyle: gradient-filled mirrored body. The centreline
        // stays the deep secondary token; the peak tips lerp toward the
        // primary token scaled by the sample's own amplitude, so quiet
        // passages stay deep and loud passages light up at the silhouette.
        // Both endpoints come from the ChartWaveformPrimary/Secondary theme
        // tokens — community themes restyle the whole body. Two gradient
        // quads per sample replace the one flat quad; the mipmap picker and
        // silhouette stroke above are untouched.
        var gradientBody = Timeline2DVisuals.PolishEnabled;
        float tipR = strokeColor.R / 255f, tipG = strokeColor.G / 255f, tipB = strokeColor.B / 255f;
        // 0.19 D: deepen the centre falloff (0.62 → 0.34). The inner half of
        // the body — where the mid strings and most note gems live — now reads
        // near-transparent, so the fill is a silhouette that gems/grid show
        // through, not a filled mass. Notes stay the loudest layer.
        var centerA = fa * 0.34f;
        var invHeightScale = wfHeightScale > 0f ? 1f / wfHeightScale : 0f;

        // v0.35 loudness body. Drawn as a SECOND mirrored pair inside the peak
        // envelope, using the same transparent-at-the-centreline gradient — so
        // it reads as a brighter shoulder at the RMS-ish level, not as the
        // filled mass 0.19 D deliberately removed. Gems and grid still show
        // through the middle; what the charter gains is a visible line between
        // "loud passage" and "one transient in a quiet one".
        var drawBody = gradientBody && !ReferenceEquals(body, level);
        var bodyA = fa * 0.55f;

        var stepWidthPx = lod.StepWidthPx;
        var drawOutline = lod.DrawOutline;
        var strokeThick = WaveformLod.StrokeThickness(stepWidthPx, MaxStrokeThickness);

        // Hoist scalar reads from the render context — these are
        // properties/fields read every iteration.
        var viewStartMs = rc.ViewStartMs;
        var zoom = rc.CurrentZoom;
        var width = rc.Width;
        // Column edges round to whole DEVICE pixels of CHART time, then subtract
        // the frame's whole-pixel origin (rc.ViewStartMs is snapped to one).
        // Rounding `(time − viewStart) × zoom` per block instead gave each column
        // its own rounding on its own frame; this way a column's pixel is a
        // constant of its time and the whole waveform moves by the same integer
        // step as the notes and the grid.
        var pixelScale = rc.PixelScale > 0 && double.IsFinite(rc.PixelScale) ? rc.PixelScale : 1.0;
        var invPixelScale = 1.0 / pixelScale;
        var devicePxPerMs = zoom * pixelScale;
        var originDevicePx = Math.Round(viewStartMs * devicePxPerMs);

        // Zero line first, so the body and peak fills lay over it. It is the
        // reference the mirrored envelope is measured against, and it is drawn
        // BEFORE the empty-window bail so the strip keeps a baseline past the
        // end of the audio instead of just stopping. ONE quad per frame, not
        // per column.
        batcher.AddQuadRaw(
            0f, wfCenter - ZeroLineThickness * 0.5f, (float)width, wfCenter - ZeroLineThickness * 0.5f,
            (float)width, wfCenter + ZeroLineThickness * 0.5f, 0f, wfCenter + ZeroLineThickness * 0.5f,
            sr, sg, sb, sa * 0.5f, 0f, 0f, 0f, 0f, 0f);

        var startSample = Math.Max(0, (int)(rc.ViewStartMs / blockMs));
        var endSample = Math.Min(level.Length, (int)(rc.ViewEndMs / blockMs) + 2);
        if (endSample <= startSample) return;

        // Pre-compute first edge so we only do one Round per iteration
        // instead of two redundant ones (the next iter's left == this iter's right).
        var x1 = (float)((Math.Round(startSample * blockMs * devicePxPerMs) - originDevicePx) * invPixelScale);
        var p1 = Math.Max(0.5f, level[startSample] * wfHeightScale);
        var b1 = body[startSample] * wfHeightScale;

        // Pre-grow the solid buffer: 1 flat fill quad (2 gradient quads with
        // the 0.14.3 body) + 2 loudness-body quads + (outline ? 2 stroke lines
        // × 3 quads each) per iteration, plus the single zero-line quad. Turns
        // the per-call EnsureCapacity into a branch-predictable no-op across
        // the full loop.
        var iterations = endSample - 1 - startSample;
        if (iterations > 0)
        {
            // Outline quads: the joined ribbon emits 3 quads
            // (core + 2 AA fades) per segment × 2 edges = 6 per iteration, the
            // exact count two per-segment thick lines would reserve.
            var solidPerIter = (gradientBody ? 2 : 1) + (drawBody ? 2 : 0) + (drawOutline ? 6 : 0);
            batcher.EnsureQuadsCapacity(iterations * solidPerIter, 0);
        }

        // Seed the outline ribbons. The silhouette is now drawn as ONE joined
        // ribbon per edge (built AFTER the loop) instead of an independent thick
        // line per segment, so we collect every window point into two reusable
        // buffers here. Point 0 is startSample; the loop appends i+1 each pass.
        // Collection is UNCONDITIONAL over the window (not gated by the per-
        // segment visibility cull) so the polyline stays continuous.
        var ptCount = 0;
        if (drawOutline)
        {
            var need = (iterations + 1) * 2;
            if (_topPts.Length < need)
            {
                var cap = _topPts.Length == 0 ? 512 : _topPts.Length;
                while (cap < need) cap *= 2;
                _topPts = new float[cap];
                _botPts = new float[cap];
            }

            _topPts[0] = x1;
            _topPts[1] = wfCenter - p1;
            _botPts[0] = x1;
            _botPts[1] = wfCenter + p1;
            ptCount = 1;
        }

        // Left-edge tip colour/alpha for the gradient body (re-used as the
        // next iteration's left edge, mirroring the x1/p1 rolling pattern).
        // 0.19 D: the tip alpha is CEILINGED at the fill constant (fa) — the
        // old (0.9 + 0.6·t) boost pushed the peak edge up to 1.5·fa, so loud
        // passages lit up into a saturated wall that swallowed gems and grid.
        // The ramp now runs 0.45·fa (quiet) → 1.0·fa (loud) so amplitude still
        // reads in the silhouette without ever exceeding the base fill.
        var t1 = Math.Min(1f, p1 * invHeightScale);
        float tr1 = fr + (tipR - fr) * t1, tg1 = fg + (tipG - fg) * t1, tb1 = fb + (tipB - fb) * t1;
        var ta1 = fa * (0.45f + 0.55f * t1);

        for (var i = startSample; i < endSample - 1; i++)
        {
            var x2 = (float)((Math.Round((i + 1) * blockMs * devicePxPerMs) - originDevicePx) * invPixelScale);
            var p2 = Math.Max(0.5f, level[i + 1] * wfHeightScale);
            var b2 = body[i + 1] * wfHeightScale;
            var t2 = Math.Min(1f, p2 * invHeightScale);
            float tr2 = fr + (tipR - fr) * t2, tg2 = fg + (tipG - fg) * t2, tb2 = fb + (tipB - fb) * t2;
            var ta2 = fa * (0.45f + 0.55f * t2);

            if (x2 >= 0 && x1 <= width)
            {
                if (gradientBody)
                {
                    // Top half: bright amplitude-scaled tip → deep centre.
                    batcher.AddQuadGradientRaw(
                        x1, wfCenter - p1, x1, wfCenter, x2, wfCenter, x2, wfCenter - p2,
                        tr1, tg1, tb1, ta1,
                        fr, fg, fb, centerA,
                        fr, fg, fb, centerA,
                        tr2, tg2, tb2, ta2);
                    // Bottom half, mirrored around the centreline.
                    batcher.AddQuadGradientRaw(
                        x1, wfCenter, x1, wfCenter + p1, x2, wfCenter + p2, x2, wfCenter,
                        fr, fg, fb, centerA,
                        tr1, tg1, tb1, ta1,
                        tr2, tg2, tb2, ta2,
                        fr, fg, fb, centerA);
                }
                else
                {
                    // Winding matches the original: top-left, top-right, bottom-right, bottom-left.
                    batcher.AddQuadRaw(x1, wfCenter - p1, x2, wfCenter - p2, x2, wfCenter + p2, x1, wfCenter + p1,
                        fr, fg, fb, fa, 0f, 0f, 0f, 0f, 0f);
                }

                if (drawBody)
                {
                    // The loudness shoulder, inside the peak envelope by
                    // construction (Body[l][i] <= Mipmaps[l][i]). Same
                    // fade-to-nothing at the centreline as the peak fill, so
                    // the two compose into ONE silhouette with a brighter
                    // inner edge rather than into a slab.
                    batcher.AddQuadGradientRaw(
                        x1, wfCenter - b1, x1, wfCenter, x2, wfCenter, x2, wfCenter - b2,
                        tipR, tipG, tipB, bodyA,
                        fr, fg, fb, 0f,
                        fr, fg, fb, 0f,
                        tipR, tipG, tipB, bodyA);
                    batcher.AddQuadGradientRaw(
                        x1, wfCenter, x1, wfCenter + b1, x2, wfCenter + b2, x2, wfCenter,
                        fr, fg, fb, 0f,
                        tipR, tipG, tipB, bodyA,
                        tipR, tipG, tipB, bodyA,
                        fr, fg, fb, 0f);
                }
            }

            // Append this segment's far vertex to both silhouette polylines
            // (outside the visibility cull so the ribbon never breaks). The
            // ribbons themselves are emitted once, after the loop.
            if (drawOutline)
            {
                var pi = ptCount * 2;
                _topPts[pi] = x2;
                _topPts[pi + 1] = wfCenter - p2;
                _botPts[pi] = x2;
                _botPts[pi + 1] = wfCenter + p2;
                ptCount++;
            }

            x1 = x2;
            p1 = p2;
            b1 = b2;
            tr1 = tr2;
            tg1 = tg2;
            tb1 = tb2;
            ta1 = ta2;
        }

        // The two silhouette outlines, each a single miter-joined ribbon so the
        // stroke reads as one smooth continuous line at high zoom instead of a
        // row of seamed per-segment slabs. Drawn after the fill so the outline
        // still layers over the body (unchanged from the interleaved order — the
        // fill of column i and the outline of column i keep fill-then-outline).
        if (drawOutline && ptCount >= 2)
        {
            batcher.AddPolylineRibbon(_topPts, ptCount, strokeThick, OutlineAaPad,
                sr, sg, sb, sa, OutlineMiterLimit);
            batcher.AddPolylineRibbon(_botPts, ptCount, strokeThick, OutlineAaPad,
                sr, sg, sb, sa, OutlineMiterLimit);
        }
    }
}
