using System.Numerics;
using DotCharter.Rendering.Data.Render;

namespace DotCharter.Rendering.Data;

/// <summary>
///     The highway's left and right edges on screen, in FRAME FRACTIONS (u, v; y
///     down), each given by two projected points: one at the draw-distance end and
///     one nearer. A straight world edge projects to a straight screen line, so
///     <see cref="At" /> gives the highway's width at any row. The stage lights'
///     haze notches out over it, because the lane fill is translucent.
/// </summary>
public readonly record struct HighwaySpan(Vector2 LeftFar, Vector2 LeftNear, Vector2 RightFar, Vector2 RightNear)
{
    /// <summary>A narrow span around <paramref name="centreU" />, for when the
    /// highway's edges cannot be projected.</summary>
    public static HighwaySpan Centred(float centreU, float halfWidth = 0.06f) => new(
        new Vector2(centreU - halfWidth, 0.4f), new Vector2(centreU - halfWidth, 0.6f),
        new Vector2(centreU + halfWidth, 0.4f), new Vector2(centreU + halfWidth, 0.6f));

    /// <summary>The notch is never narrower than this either side of the
    /// highway's middle: the notes stand up above the far end of a narrow highway,
    /// and the haze must stay clear of them there too.</summary>
    public const float MinHalfWidth = 0.075f;

    /// <summary>The highway's left and right edge (u) at row <paramref name="v" />,
    /// widened to at least <see cref="MinHalfWidth" /> either side of its middle.
    /// Above the far end the far width holds.</summary>
    public (float Left, float Right) At(float v)
    {
        var l = EdgeU(LeftFar, LeftNear, v);
        var r = EdgeU(RightFar, RightNear, v);
        var mid = (l + r) / 2f;
        var half = Math.Max((r - l) / 2f, MinHalfWidth);
        return (mid - half, mid + half);
    }

    private static float EdgeU(Vector2 far, Vector2 near, float v)
    {
        var dv = near.Y - far.Y;
        if (MathF.Abs(dv) < 1e-5f || v <= far.Y) return far.X;
        return far.X + (near.X - far.X) * (v - far.Y) / dv;
    }

    /// <summary>
    ///     Projects the world span every anchor in view opens — the one at
    ///     <paramref name="timeMs" /> and each one starting before the highway's
    ///     far end, <paramref name="lookAheadMs" /> ahead — at the draw-distance end
    ///     (<paramref name="farZ" />) and halfway there. Null when a point does not
    ///     project (a degenerate camera).
    /// </summary>
    public static HighwaySpan? Project(Matrix4x4 mvp, RenderAnchor[] anchors, int anchorCount, double timeMs,
        double lookAheadMs, float y, float farZ, int width, int height)
    {
        if (width <= 0 || height <= 0) return null;
        var (x0, x1) = AnchorFretSpan.WorldSpanAt(anchors, anchorCount, timeMs);
        var n = Math.Min(anchorCount, anchors.Length);
        for (var i = 0; i < n; i++)
        {
            var a = anchors[i];
            if (a.Time <= timeMs || a.Time > timeMs + lookAheadMs) continue;
            var (ax0, ax1) = AnchorFretSpan.WorldSpan(a.Fret, a.Width);
            x0 = Math.Min(x0, ax0);
            x1 = Math.Max(x1, ax1);
        }

        var lf = Frac(CrowdBackgroundPalette.ProjectToScreen(mvp, new Vector3(x0, y, farZ), width, height));
        var ln = Frac(CrowdBackgroundPalette.ProjectToScreen(mvp, new Vector3(x0, y, farZ * 0.5f), width, height));
        var rf = Frac(CrowdBackgroundPalette.ProjectToScreen(mvp, new Vector3(x1, y, farZ), width, height));
        var rn = Frac(CrowdBackgroundPalette.ProjectToScreen(mvp, new Vector3(x1, y, farZ * 0.5f), width, height));
        if (lf is not { } a0 || ln is not { } a1 || rf is not { } b0 || rn is not { } b1) return null;
        return new HighwaySpan(a0, a1, b0, b1);

        Vector2? Frac(Vector2? p) => p is { } q ? new Vector2(q.X / width, q.Y / height) : null;
    }
}
