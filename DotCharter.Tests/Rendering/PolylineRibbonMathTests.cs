using DotCharter.Rendering.Timeline2D;

namespace DotCharter.Tests.Rendering;

/// <summary>
/// Vertex math for the miter-joined waveform outline ribbon
/// (<see cref="PolylineRibbonMath"/> + <see cref="TimelineBatcher.AddPolylineRibbon"/>).
/// The defining property of a miter join is that the shared offset vertex sits
/// at exactly half-thickness perpendicular distance from BOTH adjacent
/// segments, so the two segments' inner/outer edges meet with no gap or overlap.
/// </summary>
public class PolylineRibbonMathTests
{
    private const float Tol = 1e-4f;

    [Fact]
    public void SegmentNormal_Horizontal_IsUnitUp()
    {
        Assert.True(PolylineRibbonMath.SegmentNormal(0f, 0f, 5f, 0f, out var nx, out var ny));
        Assert.Equal(0f, nx, Tol);
        Assert.Equal(1f, ny, Tol);
    }

    [Fact]
    public void SegmentNormal_Vertical_IsUnitLeft()
    {
        Assert.True(PolylineRibbonMath.SegmentNormal(0f, 0f, 0f, 3f, out var nx, out var ny));
        Assert.Equal(-1f, nx, Tol);
        Assert.Equal(0f, ny, Tol);
    }

    [Fact]
    public void SegmentNormal_ZeroLength_ReturnsFalse()
    {
        Assert.False(PolylineRibbonMath.SegmentNormal(2f, 2f, 2f, 2f, out var nx, out var ny));
        Assert.Equal(0f, nx, Tol);
        Assert.Equal(0f, ny, Tol);
    }

    [Fact]
    public void SegmentNormal_IsAlwaysUnitLength()
    {
        Assert.True(PolylineRibbonMath.SegmentNormal(1f, 2f, 4f, 6f, out var nx, out var ny));
        Assert.Equal(1f, MathF.Sqrt(nx * nx + ny * ny), Tol);
    }

    [Fact]
    public void Miter_StraightRun_IsPlainButtOffset()
    {
        // Same normal twice ⇒ the offset is normal × halfThick, unit = normal.
        PolylineRibbonMath.Miter(0f, 1f, 0f, 1f, 3f, 4f,
            out var ox, out var oy, out var ux, out var uy);
        Assert.Equal(0f, ox, Tol);
        Assert.Equal(3f, oy, Tol);
        Assert.Equal(0f, ux, Tol);
        Assert.Equal(1f, uy, Tol);
    }

    [Fact]
    public void Miter_RightAngleBend_KeepsHalfThicknessFromBothSegments()
    {
        // Segment A →(+x) has normal (0,1); segment B ↑(+y) has normal (-1,0).
        const float half = 2f;
        PolylineRibbonMath.Miter(0f, 1f, -1f, 0f, half, 4f,
            out var ox, out var oy, out _, out _);

        // Perpendicular distance to each segment == halfThick (the miter join
        // invariant): offset · segmentNormal == halfThick.
        var distToA = ox * 0f + oy * 1f;
        var distToB = ox * -1f + oy * 0f;
        Assert.Equal(half, distToA, Tol);
        Assert.Equal(half, distToB, Tol);
        // 90° join widens by √2.
        Assert.Equal(half * MathF.Sqrt(2f), MathF.Sqrt(ox * ox + oy * oy), Tol);
    }

    [Fact]
    public void Miter_UnitDirection_IsAlwaysNormalized()
    {
        PolylineRibbonMath.Miter(0f, 1f, -1f, 0f, 2f, 4f,
            out _, out _, out var ux, out var uy);
        Assert.Equal(1f, MathF.Sqrt(ux * ux + uy * uy), Tol);
    }

    [Fact]
    public void Miter_AcuteBend_ClampsSpikeToMiterLimit()
    {
        // Two normals symmetric about +y at 80° off-axis ⇒ half-angle 80°,
        // cos ≈ 0.174 ⇒ raw miter ≈ 5.76×halfThick, well past the limit.
        var a = 80f * MathF.PI / 180f;
        float px = MathF.Sin(a), py = MathF.Cos(a);
        const float half = 1f;
        const float limit = 3f;
        PolylineRibbonMath.Miter(px, py, -px, py, half, limit,
            out var ox, out var oy, out var ux, out var uy);

        var len = MathF.Sqrt(ox * ox + oy * oy);
        Assert.Equal(half * limit, len, Tol);   // clamped, not 5.76
        // Bisector points straight up for the symmetric case.
        Assert.Equal(0f, ux, Tol);
        Assert.Equal(1f, uy, Tol);
    }

    [Fact]
    public void Miter_ReversalFallsBackToCurrentNormal()
    {
        // 180° reversal: no bisector ⇒ use the current segment normal.
        PolylineRibbonMath.Miter(0f, 1f, 0f, -1f, 2f, 4f,
            out var ox, out var oy, out var ux, out var uy);
        Assert.Equal(0f, ox, Tol);
        Assert.Equal(-2f, oy, Tol);
        Assert.Equal(0f, ux, Tol);
        Assert.Equal(-1f, uy, Tol);
    }

    [Fact]
    public void AddPolylineRibbon_EmitsThreeQuadsPerSegment()
    {
        var batcher = new TimelineBatcher();
        // 4 points ⇒ 3 segments ⇒ 3 quads/segment ⇒ 9 quads ⇒ 54 vertices.
        float[] pts = [0, 0, 10, 0, 20, 0, 30, 0];
        batcher.AddPolylineRibbon(pts, 4, 4f, 1f, 1f, 1f, 1f, 1f, 3f);
        Assert.Equal(9 * 6, batcher.SolidVertexCount);
    }

    [Fact]
    public void AddPolylineRibbon_StraightRun_CoreSpansHalfThicknessBothSides()
    {
        var batcher = new TimelineBatcher();
        const float thickness = 6f;
        const float cy = 100f;
        // A flat horizontal polyline at y = cy: the opaque core (first quad) must
        // span cy ± thickness/2, exactly like a single AddThickLineRaw would.
        float[] pts = [0, cy, 10, cy, 20, cy];
        batcher.AddPolylineRibbon(pts, 3, thickness, 1f, 1f, 1f, 1f, 1f, 3f);

        // First quad = the opaque core of segment 0; vertex stride is 6 floats
        // (x,y,r,g,b,a). Winding is (aOut, aIn, bIn, bOut) so y0 and y1 are the
        // two edges at x=0.
        var y0 = batcher.SolidBuffer[1];
        var y1 = batcher.SolidBuffer[6 + 1];
        var lo = MathF.Min(y0, y1);
        var hi = MathF.Max(y0, y1);
        Assert.Equal(cy - thickness / 2f, lo, Tol);
        Assert.Equal(cy + thickness / 2f, hi, Tol);
    }

    [Fact]
    public void AddPolylineRibbon_TooFewPoints_EmitsNothing()
    {
        var batcher = new TimelineBatcher();
        float[] pts = [0, 0];
        batcher.AddPolylineRibbon(pts, 1, 4f, 1f, 1f, 1f, 1f, 1f, 3f);
        Assert.Equal(0, batcher.SolidVertexCount);
    }
}
