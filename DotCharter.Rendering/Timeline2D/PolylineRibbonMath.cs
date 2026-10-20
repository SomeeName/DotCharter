using System.Runtime.CompilerServices;

namespace DotCharter.Rendering.Timeline2D;

/// <summary>
/// Pure vertex math for a miter-joined polyline "ribbon" — a thick stroke whose
/// consecutive segments SHARE their edge vertices (computed from the averaged,
/// miter-limited normals of the adjacent segments) so the stroke reads as ONE
/// continuous line instead of a row of independent per-segment quads with
/// seams/notches at every bend. Consumed by
/// <see cref="TimelineBatcher.AddPolylineRibbon"/> (the 2D timeline waveform
/// silhouette outline). No GL state — unit-tested directly.
/// </summary>
public static class PolylineRibbonMath
{
    /// <summary>
    /// Left-hand unit normal of the segment (x1,y1)→(x2,y2). Returns false for a
    /// degenerate (zero-length) segment, leaving the outputs at 0 so the caller
    /// can carry the previous normal instead.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool SegmentNormal(float x1, float y1, float x2, float y2, out float nx, out float ny)
    {
        var dx = x2 - x1;
        var dy = y2 - y1;
        var len2 = dx * dx + dy * dy;
        if (len2 < 1e-12f)
        {
            nx = 0f;
            ny = 0f;
            return false;
        }

        var inv = 1f / (float)Math.Sqrt(len2);
        nx = -dy * inv;
        ny = dx * inv;
        return true;
    }

    /// <summary>
    /// Miter offset at the vertex shared by two segments whose left unit normals
    /// are (prevNx,prevNy) and (curNx,curNy). Returns the offset VECTOR
    /// (<paramref name="ox"/>,<paramref name="oy"/>) = unit-miter ×
    /// half-thickness widened by 1/cos(θ/2), plus the unit miter direction
    /// (<paramref name="ux"/>,<paramref name="uy"/>), which extends the AA fade.
    /// The miter length is clamped to <paramref name="halfThick"/> ×
    /// <paramref name="miterLimit"/> so acute angles bevel instead of shooting a
    /// long spike, and a ~180° reversal falls back to the current segment
    /// normal. Passing the SAME normal twice yields a plain butt offset
    /// (halfThick × normal) — the polyline-endpoint case, so callers need no
    /// separate branch for the first/last vertex.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Miter(float prevNx, float prevNy, float curNx, float curNy,
        float halfThick, float miterLimit,
        out float ox, out float oy, out float ux, out float uy)
    {
        var mx = prevNx + curNx;
        var my = prevNy + curNy;
        var mlen2 = mx * mx + my * my;
        if (mlen2 < 1e-8f)
        {
            // Segments double straight back on themselves — no bisector exists.
            ux = curNx;
            uy = curNy;
            ox = curNx * halfThick;
            oy = curNy * halfThick;
            return;
        }

        var inv = 1f / (float)Math.Sqrt(mlen2);
        ux = mx * inv;
        uy = my * inv;

        // cos(half-angle) between the two unit normals; > 0 for any turn < 180°.
        var cos = ux * curNx + uy * curNy;
        var max = halfThick * miterLimit;
        var scale = cos > 1e-4f ? halfThick / cos : max;
        if (scale > max) scale = max;

        ox = ux * scale;
        oy = uy * scale;
    }
}
