using System.Runtime.CompilerServices;
using Avalonia.Media;
using DotCharter.Rendering.Text;

namespace DotCharter.Rendering.Timeline2D;

public enum BatchKind : byte
{
    Solid = 0,
    General = 1
}

public struct DrawCommand
{
    public BatchKind Kind;
    public int FirstVertex;
    public int VertexCount;
}

/// <summary>
/// Two-buffer batcher. Most 2D geometry (rects, triangles, thick-line fills
/// and gradient edges) is flat-shaded with no UV or type info — it goes to
/// <see cref="SolidBuffer"/> with a compact 6-float/vertex layout
/// (x, y, r, g, b, a) and is drawn by the cheap solid shader.
/// Circles, ring outlines, AA lines and font glyphs need UV + a type flag
/// and go to <see cref="GeneralBuffer"/> with the full 9-float/vertex
/// layout (x, y, r, g, b, a, u, v, type). <see cref="DrawCommands"/>
/// records runs of each kind in insertion order so the engine can issue
/// draw calls in the exact order the layers emitted their geometry —
/// alpha-blending stays identical to the pre-split behaviour.
/// </summary>
public class TimelineBatcher
{
    private static readonly float[] CornerSin;
    private static readonly float[] CornerCos;

    // Sized for an ordinary frame, not a worst case. These were 2M + 1M floats
    // = 11.44 MiB allocated EAGERLY on the Large Object Heap per batcher — and
    // there is one per ChartRenderer plus one per stacked track row, so five
    // renderers cost 57 MiB before drawing anything. EnsureSolidCapacity /
    // EnsureGeneralCapacity already grow on demand (and never shrink), so a
    // genuinely heavy frame reaches the same place after a couple of doublings
    // and stays there; a typical session simply never pays for the headroom.
    // 64K floats = 256 KiB, still under the 85 KiB LOH threshold only after
    // reduction — deliberately kept a single allocation either way.
    private const int InitialSolidFloats = 64 * 1024;
    private const int InitialGeneralFloats = 32 * 1024;

    public float[] SolidBuffer = new float[InitialSolidFloats];
    public int SolidVertexCount;

    public float[] GeneralBuffer = new float[InitialGeneralFloats];
    public int GeneralVertexCount;

    public readonly List<DrawCommand> DrawCommands = new(256);
    private BatchKind _currentKind;
    private int _runStartVertex;

    static TimelineBatcher()
    {
        // 14 segments per quarter circle (≈6.4° per step) — visibly smoother
        // than the previous 6 (15° steps) at the chip sizes we draw, with no
        // measurable cost (a single AddRoundRect is now 14 triangles per
        // corner = 56 total, still trivial for the GPU).
        var segments = 14;
        CornerSin = new float[segments + 1];
        CornerCos = new float[segments + 1];
        for (var i = 0; i <= segments; i++)
        {
            var angle = (float)(i * Math.PI / 2.0 / segments);
            CornerSin[i] = (float)Math.Sin(angle);
            CornerCos[i] = (float)Math.Cos(angle);
        }
    }

    /// <summary>The glyph atlas text quads sample from (the renderer's
    /// upload subclass of <see cref="ChartFontAtlas" />). Metrics live on the
    /// base class, so batched geometry is a pure function of the base.</summary>
    public ChartFontAtlas? FontAtlas { get; set; }

    public void Reset()
    {
        SolidVertexCount = 0;
        GeneralVertexCount = 0;
        DrawCommands.Clear();
        _currentKind = BatchKind.Solid;
        _runStartVertex = 0;
    }

    /// <summary>
    /// Flushes the pending run into <see cref="DrawCommands"/>. The engine
    /// must call this once after the per-frame layer loop finishes, before
    /// iterating DrawCommands for issuing GL draws.
    /// </summary>
    public void FinalizeFrame() => FlushCurrentRun();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void FlushCurrentRun()
    {
        var current = _currentKind == BatchKind.Solid ? SolidVertexCount : GeneralVertexCount;
        var count = current - _runStartVertex;
        if (count > 0)
        {
            DrawCommands.Add(new DrawCommand
            {
                Kind = _currentKind,
                FirstVertex = _runStartVertex,
                VertexCount = count
            });
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void SwitchKind(BatchKind newKind)
    {
        if (newKind == _currentKind) return;
        FlushCurrentRun();
        _currentKind = newKind;
        _runStartVertex = newKind == BatchKind.Solid ? SolidVertexCount : GeneralVertexCount;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void EnsureSolidCapacity(int newVertices)
    {
        var requiredFloats = (SolidVertexCount + newVertices) * 6;
        if (requiredFloats <= SolidBuffer.Length) return;

        var newSize = SolidBuffer.Length;
        while (newSize < requiredFloats)
            newSize *= 2;
        Array.Resize(ref SolidBuffer, newSize);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void EnsureGeneralCapacity(int newVertices)
    {
        var requiredFloats = (GeneralVertexCount + newVertices) * 9;
        if (requiredFloats <= GeneralBuffer.Length) return;

        var newSize = GeneralBuffer.Length;
        while (newSize < requiredFloats)
            newSize *= 2;
        Array.Resize(ref GeneralBuffer, newSize);
    }

    /// <summary>
    /// Pre-grows both buffers to hold at least the given count of extra
    /// solid/general quads. Call once per hot layer loop to turn the
    /// per-call EnsureCapacity branch into a predictable no-op.
    /// </summary>
    public void EnsureQuadsCapacity(int solidQuads, int generalQuads)
    {
        if (solidQuads > 0) EnsureSolidCapacity(solidQuads * 6);
        if (generalQuads > 0) EnsureGeneralCapacity(generalQuads * 6);
    }

    public float MeasureTextWidth(string text) => FontAtlas?.MeasureTextWidth(text) ?? 0f;

    public void AddText(string text, float x, float y, Color c, bool centerX = false, bool centerY = false)
    {
        var atlas = FontAtlas;
        if (atlas == null || string.IsNullOrEmpty(text)) return;
        if (centerX) x -= MeasureTextWidth(text) / 2f;
        if (centerY) y -= atlas.TextCenterYOffset;

        // PHYSICAL-PIXEL SNAP. Quads are emitted in logical units
        // and the ortho multiplies by the surface scale, so a glyph's real
        // position is `logical x renderScale` — fractional for every label the
        // moment that scale is fractional, which the global UI scale made the
        // default (1.05, 1.15, 1.30 …). A glyph straddling two pixel columns is
        // resampled by the sampler no matter how well the SURFACE is aligned:
        // this is the half `ComposedSurfaceGeometry` cannot reach. Snapping the
        // pen to the physical grid — with the atlas packed on whole texels and
        // `Width` being exactly its texel count — makes each glyph a 1:1 copy,
        // which is what every native text renderer does with integer pen
        // positions. The shift is under half a physical pixel, i.e. invisible
        // as layout and decisive as rasterization.
        var pixelScale = atlas.RenderScale;
        var snap = float.IsFinite(pixelScale) && pixelScale > 0;
        if (snap) y = MathF.Round(y * pixelScale) / pixelScale;

        // Hoist color → float once per AddText call; AddGeneralQuadRaw
        // takes pre-divided components and is inlined into the glyph loop.
        float r = c.R / 255f, g = c.G / 255f, b = c.B / 255f, a = c.A / 255f;

        var ascii = atlas.AsciiGlyphs;
        var valid = atlas.AsciiValid;
        var currentX = x;
        foreach (var ch in text)
        {
            GlyphInfo glyph;
            if (ch < 128)
            {
                if (!valid[ch]) continue;
                glyph = ascii[ch];
            }
            else if (!atlas.GlyphMap.TryGetValue(ch, out glyph))
            {
                continue;
            }

            var renderX = currentX + glyph.OffsetX;
            if (snap) renderX = MathF.Round(renderX * pixelScale) / pixelScale;
            AddGeneralQuadRaw(renderX, y, renderX, y + glyph.Height, renderX + glyph.Width, y + glyph.Height,
                renderX + glyph.Width, y, r, g, b, a, 3f, glyph.U1, glyph.V1, glyph.U2, glyph.V2);
            currentX += glyph.Advance;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void AddVerticalLineAa(float x, float y, float w, float h, Color c) => AddVerticalLineAaRaw(x, y, w, h, c.R / 255f, c.G / 255f, c.B / 255f, c.A / 255f);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void AddVerticalLineAaRaw(float x, float y, float w, float h, float r, float g, float b, float a)
    {
        var pad = 1.0f;
        var totalW = w + pad * 2;
        AddGeneralQuadRaw(x - pad, y, x - pad, y + h, x + w + pad, y + h, x + w + pad, y,
            r, g, b, a, 4f, 0f, totalW, totalW, totalW);
    }

    /// <summary>
    ///     A dashed vertical AA line as ONE quad: the general fragment shader
    ///     discards the gaps, so the vertex count is independent of the line's
    ///     length. Same across-line ramp as <see cref="AddVerticalLineAaRaw" />; the
    ///     dash phase starts at <paramref name="y" /> and the last dash is clipped at
    ///     <c>y + h</c>, exactly like a loop of one quad per dash. Dash and gap are
    ///     carried in the vertex type in 1/16 px units (at most
    ///     <see cref="DashedLineMaxPx" /> each).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void AddDashedVerticalLineAaRaw(float x, float y, float w, float h, float dash, float gap,
        float r, float g, float b, float a)
    {
        // A zero dash draws nothing, as the per-dash loop drew nothing.
        if (h <= 0f || dash <= 0f) return;
        var pad = 1.0f;
        var totalW = w + pad * 2;
        AddGeneralQuadRaw(x - pad, y, x - pad, y + h, x + w + pad, y + h, x + w + pad, y,
            r, g, b, a, DashedLineType(dash, gap), 0f, totalW, totalW, totalW);
    }

    /// <summary>First vertex-type code of the dashed vertical AA line (timeline2d_general.vert decodes it).</summary>
    public const float DashedLineTypeBase = 8f;

    /// <summary>Dash and gap resolution in the packed type: 1/16 px.</summary>
    public const float DashedLineUnitsPerPx = 16f;

    /// <summary>Multiplier of the dash field in the packed type; the gap field sits below it.</summary>
    public const float DashedLineDashStride = 1024f;

    /// <summary>The longest dash or gap the packed type can carry.</summary>
    public const float DashedLineMaxPx = (DashedLineDashStride - 1f) / DashedLineUnitsPerPx;

    /// <summary>
    ///     The packed vertex type of a dashed line. Every field is a small integer, so
    ///     the float carries it exactly and the shader decodes it without rounding.
    /// </summary>
    public static float DashedLineType(float dash, float gap)
    {
        var dashUnits = Math.Clamp(MathF.Round(dash * DashedLineUnitsPerPx), 0f, DashedLineDashStride - 1f);
        var gapUnits = Math.Clamp(MathF.Round(gap * DashedLineUnitsPerPx), 0f, DashedLineDashStride - 1f);
        return DashedLineTypeBase + dashUnits * DashedLineDashStride + gapUnits;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void AddHorizontalLineAa(float x, float y, float w, float h, Color c)
    {
        var pad = 1.0f;
        var totalH = h + pad * 2;
        float r = c.R / 255f, g = c.G / 255f, b = c.B / 255f, a = c.A / 255f;
        AddGeneralQuadRaw(x, y - pad, x, y + h + pad, x + w, y + h + pad, x + w, y - pad,
            r, g, b, a, 5f, totalH, 0f, totalH, totalH);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void AddSprite(float x, float y, float w, float h, Color c, float type)
    {
        float r = c.R / 255f, g = c.G / 255f, b = c.B / 255f, a = c.A / 255f;
        AddGeneralQuadRaw(x, y, x, y + h, x + w, y + h, x + w, y, r, g, b, a, type, -1f, -1f, 1f, 1f);
    }

    /// <summary>
    /// Sprite (AA circle / ring, same type codes as <see cref="AddSprite"/>)
    /// with a vertical top → bottom colour gradient. Pure vertex-colour work
    /// on the existing general buffer — no new GL state; the fragment path is
    /// identical to a flat sprite (0.14.3 note-gem shading).
    /// </summary>
    public void AddSpriteGradientV(float x, float y, float w, float h, Color top, Color bottom, float type)
    {
        SwitchKind(BatchKind.General);
        EnsureGeneralCapacity(6);
        float tr = top.R / 255f, tg = top.G / 255f, tb = top.B / 255f, ta = top.A / 255f;
        float br = bottom.R / 255f, bg = bottom.G / 255f, bb = bottom.B / 255f, ba = bottom.A / 255f;
        var idx = GeneralVertexCount * 9;
        var buf = GeneralBuffer;

        // Triangle 1: TL, BL, BR — matches AddGeneralQuadRaw's layout with
        // per-vertex colour (top colour on the y row, bottom on y+h).
        WriteGeneralVertex(buf, ref idx, x, y, tr, tg, tb, ta, -1f, -1f, type);
        WriteGeneralVertex(buf, ref idx, x, y + h, br, bg, bb, ba, -1f, 1f, type);
        WriteGeneralVertex(buf, ref idx, x + w, y + h, br, bg, bb, ba, 1f, 1f, type);
        // Triangle 2: TL, BR, TR.
        WriteGeneralVertex(buf, ref idx, x, y, tr, tg, tb, ta, -1f, -1f, type);
        WriteGeneralVertex(buf, ref idx, x + w, y + h, br, bg, bb, ba, 1f, 1f, type);
        WriteGeneralVertex(buf, ref idx, x + w, y, tr, tg, tb, ta, 1f, -1f, type);

        GeneralVertexCount += 6;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void WriteGeneralVertex(float[] buf, ref int idx, float x, float y,
        float r, float g, float b, float a, float u, float v, float type)
    {
        buf[idx++] = x;
        buf[idx++] = y;
        buf[idx++] = r;
        buf[idx++] = g;
        buf[idx++] = b;
        buf[idx++] = a;
        buf[idx++] = u;
        buf[idx++] = v;
        buf[idx++] = type;
    }

    public void AddRoundRect(float x, float y, float w, float h, float radius, Color c)
    {
        if (radius <= 0)
        {
            AddRect(x, y, w, h, c);
            return;
        }

        radius = Math.Min(radius, Math.Min(w / 2f, h / 2f));
        AddRect(x + radius, y, w - radius * 2, h, c);
        AddRect(x, y + radius, radius, h - radius * 2, c);
        AddRect(x + w - radius, y + radius, radius, h - radius * 2, c);
        AddCorner(x + radius, y + radius, radius, c, -1, -1);
        AddCorner(x + w - radius, y + radius, radius, c, 1, -1);
        AddCorner(x + w - radius, y + h - radius, radius, c, 1, 1);
        AddCorner(x + radius, y + h - radius, radius, c, -1, 1);
    }

    /// <summary>
    /// A rounded rect whose corners are anti-aliased: the body as three rects and each corner
    /// as the AA disc sprite (type 1). <see cref="AddRoundRect" />'s triangle-fan corners are
    /// hard-edged, which read as notched on a small chip. OPAQUE colours only — the discs
    /// overlap the body, so a translucent fill would darken where they do.
    /// </summary>
    public void AddRoundRectAa(float x, float y, float w, float h, float radius, Color c)
    {
        radius = Math.Min(radius, Math.Min(w / 2f, h / 2f));
        if (radius < 1f)
        {
            AddRect(x, y, w, h, c);
            return;
        }

        var d = radius * 2f;
        AddRect(x + radius, y, w - d, h, c);
        AddRect(x, y + radius, radius, h - d, c);
        AddRect(x + w - radius, y + radius, radius, h - d, c);
        AddSprite(x, y, d, d, c, 1f);
        AddSprite(x + w - d, y, d, d, c, 1f);
        AddSprite(x + w - d, y + h - d, d, d, c, 1f);
        AddSprite(x, y + h - d, d, d, c, 1f);
    }

    private void AddCorner(float cx, float cy, float r, Color c, float dirX, float dirY)
    {
        float cr = c.R / 255f, cg = c.G / 255f, cb = c.B / 255f, ca = c.A / 255f;
        for (var i = 0; i < CornerCos.Length - 1; i++)
        {
            var x1 = cx + CornerCos[i] * r * dirX;
            var y1 = cy + CornerSin[i] * r * dirY;
            var x2 = cx + CornerCos[i + 1] * r * dirX;
            var y2 = cy + CornerSin[i + 1] * r * dirY;
            AddSolidTriangleRaw(cx, cy, x1, y1, x2, y2, cr, cg, cb, ca);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void AddRect(float x, float y, float w, float h, Color c)
    {
        AddSolidQuadRaw(x, y, x, y + h, x + w, y + h, x + w, y,
            c.R / 255f, c.G / 255f, c.B / 255f, c.A / 255f);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void AddThickLine(float x1, float y1, float x2, float y2, float thickness, Color c)
    {
        AddThickLineRaw(x1, y1, x2, y2, thickness,
            c.R / 255f, c.G / 255f, c.B / 255f, c.A / 255f);
    }

    /// <summary>
    /// Hot-path overload — takes pre-divided 0..1 colour components so the
    /// caller can amortise a single Color → float conversion across many
    /// lines (waveform, grid, etc.).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void AddThickLineRaw(float x1, float y1, float x2, float y2, float thickness,
        float r, float g, float b, float a)
    {
        var dx = x2 - x1;
        var dy = y2 - y1;
        float len;

        if (Math.Abs(y1 - y2) < 0.001f) len = Math.Abs(dx);
        else if (Math.Abs(x1 - x2) < 0.001f) len = Math.Abs(dy);
        else len = (float)Math.Sqrt(dx * dx + dy * dy);

        if (len == 0) return;

        var nx = -dy / len;
        var ny = dx / len;

        var t = thickness / 2f;
        var pad = 1.0f;

        float ix1 = x1 + nx * t, iy1 = y1 + ny * t;
        float ix2 = x1 - nx * t, iy2 = y1 - ny * t;
        float ix3 = x2 - nx * t, iy3 = y2 - ny * t;
        float ix4 = x2 + nx * t, iy4 = y2 + ny * t;

        float ox1 = x1 + nx * (t + pad), oy1 = y1 + ny * (t + pad);
        float ox2 = x1 - nx * (t + pad), oy2 = y1 - ny * (t + pad);
        float ox3 = x2 - nx * (t + pad), oy3 = y2 - ny * (t + pad);
        float ox4 = x2 + nx * (t + pad), oy4 = y2 + ny * (t + pad);

        AddSolidQuadRaw(ix1, iy1, ix2, iy2, ix3, iy3, ix4, iy4, r, g, b, a);
        AddSolidQuadGradientRaw(ox1, oy1, ix1, iy1, ix4, iy4, ox4, oy4,
            r, g, b, 0f, r, g, b, a, r, g, b, a, r, g, b, 0f);
        AddSolidQuadGradientRaw(ix2, iy2, ox2, oy2, ox3, oy3, ix3, iy3,
            r, g, b, a, r, g, b, 0f, r, g, b, 0f, r, g, b, a);
    }

    /// <summary>
    /// Emits a miter-joined ribbon (a continuous thick polyline) into the solid
    /// buffer. Unlike calling <see cref="AddThickLineRaw"/> once per segment —
    /// where every segment offsets its own copy of the shared vertex by its OWN
    /// normal, leaving a gap/overlap notch at each bend — consecutive segments
    /// here SHARE their edge vertices, offset along the averaged, miter-limited
    /// normal of the adjacent pair (see <see cref="PolylineRibbonMath"/>). The
    /// stroke therefore reads as ONE smooth line at high zoom instead of a row
    /// of chunky per-segment slabs.
    /// <para>
    /// Each segment emits the SAME three quads as <see cref="AddThickLineRaw"/>
    /// — an opaque core plus two 1-px alpha-to-0 AA fades — so total quads =
    /// 3 × (<paramref name="pointCount"/> − 1). For a straight run the geometry
    /// is byte-identical to per-segment thick lines (the bisector equals the
    /// normal), so only actual bends change. Points are interleaved x,y in
    /// <paramref name="pts"/>; the array is only read, never retained.
    /// Zero-allocation.
    /// </para>
    /// </summary>
    public void AddPolylineRibbon(float[] pts, int pointCount, float thickness, float pad,
        float r, float g, float b, float a, float miterLimit)
    {
        if (pointCount < 2) return;
        SwitchKind(BatchKind.Solid);
        EnsureSolidCapacity((pointCount - 1) * 3 * 6);

        var halfThick = thickness / 2f;

        // Vertex 0 — endpoint: a plain butt offset from the first segment normal
        // (Miter with the same normal twice gives normal × halfThick).
        var p0x = pts[0];
        var p0y = pts[1];
        if (!PolylineRibbonMath.SegmentNormal(p0x, p0y, pts[2], pts[3], out var prevNx, out var prevNy))
        {
            prevNx = 0f;
            prevNy = 1f;
        }

        PolylineRibbonMath.Miter(prevNx, prevNy, prevNx, prevNy, halfThick, miterLimit,
            out var prevOx, out var prevOy, out var prevUx, out var prevUy);
        var prevPx = p0x;
        var prevPy = p0y;

        for (var i = 1; i < pointCount; i++)
        {
            var px = pts[i * 2];
            var py = pts[i * 2 + 1];

            float curOx, curOy, curUx, curUy;
            float curNx = prevNx, curNy = prevNy;
            if (i < pointCount - 1)
            {
                // Interior vertex: miter between the incoming and outgoing edge.
                if (!PolylineRibbonMath.SegmentNormal(px, py, pts[i * 2 + 2], pts[i * 2 + 3],
                        out curNx, out curNy))
                {
                    curNx = prevNx;
                    curNy = prevNy;
                }

                PolylineRibbonMath.Miter(prevNx, prevNy, curNx, curNy, halfThick, miterLimit,
                    out curOx, out curOy, out curUx, out curUy);
            }
            else
            {
                // Last vertex — endpoint: butt offset from the incoming segment.
                PolylineRibbonMath.Miter(prevNx, prevNy, prevNx, prevNy, halfThick, miterLimit,
                    out curOx, out curOy, out curUx, out curUy);
            }

            EmitRibbonSegment(prevPx, prevPy, prevOx, prevOy, prevUx, prevUy,
                px, py, curOx, curOy, curUx, curUy, pad, r, g, b, a);

            prevPx = px;
            prevPy = py;
            prevOx = curOx;
            prevOy = curOy;
            prevUx = curUx;
            prevUy = curUy;
            prevNx = curNx;
            prevNy = curNy;
        }
    }

    /// <summary>
    /// One ribbon segment: an opaque core quad spanning the two shared miter
    /// offsets plus the two 1-px AA fade quads, matching the fade structure of
    /// <see cref="AddThickLineRaw"/>. Vertices A/B are the segment endpoints;
    /// (aOx,aOy)/(bOx,bOy) are the miter offset vectors and (aUx,aUy)/(bUx,bUy)
    /// the unit miter directions the fades extend along.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void EmitRibbonSegment(
        float ax, float ay, float aOx, float aOy, float aUx, float aUy,
        float bx, float by, float bOx, float bOy, float bUx, float bUy,
        float pad, float r, float g, float b, float a)
    {
        // Core edge points (± the miter offset).
        float aOutX = ax + aOx, aOutY = ay + aOy;
        float aInX = ax - aOx, aInY = ay - aOy;
        float bOutX = bx + bOx, bOutY = by + bOy;
        float bInX = bx - bOx, bInY = by - bOy;

        // Fade edge points — one pad further out along the unit miter direction.
        float aOutFX = aOutX + aUx * pad, aOutFY = aOutY + aUy * pad;
        float aInFX = aInX - aUx * pad, aInFY = aInY - aUy * pad;
        float bOutFX = bOutX + bUx * pad, bOutFY = bOutY + bUy * pad;
        float bInFX = bInX - bUx * pad, bInFY = bInY - bUy * pad;

        // Opaque core.
        AddSolidQuadRaw(aOutX, aOutY, aInX, aInY, bInX, bInY, bOutX, bOutY, r, g, b, a);
        // Outer AA fade: 0 → a → a → 0.
        AddSolidQuadGradientRaw(aOutFX, aOutFY, aOutX, aOutY, bOutX, bOutY, bOutFX, bOutFY,
            r, g, b, 0f, r, g, b, a, r, g, b, a, r, g, b, 0f);
        // Inner AA fade: a → 0 → 0 → a.
        AddSolidQuadGradientRaw(aInX, aInY, aInFX, aInFY, bInFX, bInFY, bInX, bInY,
            r, g, b, a, r, g, b, 0f, r, g, b, 0f, r, g, b, a);
    }

    /// <summary>
    /// Public gradient-quad entry point. Only supports solid (type=0)
    /// geometry because gradient blending is only used by thick-line
    /// fades, all of which are flat-shaded.
    /// </summary>
    public void AddQuadGradient(float x1, float y1, float x2, float y2, float x3, float y3, float x4, float y4,
        Color c1, Color c2, Color c3, Color c4)
    {
        AddSolidQuadGradientRaw(x1, y1, x2, y2, x3, y3, x4, y4,
            c1.R / 255f, c1.G / 255f, c1.B / 255f, c1.A / 255f,
            c2.R / 255f, c2.G / 255f, c2.B / 255f, c2.A / 255f,
            c3.R / 255f, c3.G / 255f, c3.B / 255f, c3.A / 255f,
            c4.R / 255f, c4.G / 255f, c4.B / 255f, c4.A / 255f);
    }

    /// <summary>
    /// Hot-path gradient overload — takes pre-divided 0..1 per-vertex colour
    /// components so tight loops (waveform body) can amortise the Color →
    /// float conversions across the whole loop. Same flat-shaded solid
    /// buffer as <see cref="AddQuadGradient"/>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void AddQuadGradientRaw(float x1, float y1, float x2, float y2, float x3, float y3, float x4, float y4,
        float r1, float g1, float b1, float a1,
        float r2, float g2, float b2, float a2,
        float r3, float g3, float b3, float a3,
        float r4, float g4, float b4, float a4)
    {
        AddSolidQuadGradientRaw(x1, y1, x2, y2, x3, y3, x4, y4,
            r1, g1, b1, a1, r2, g2, b2, a2, r3, g3, b3, a3, r4, g4, b4, a4);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void AddQuad(float x1, float y1, float x2, float y2, float x3, float y3, float x4, float y4, Color c,
        float type, float u1, float v1, float u2, float v2)
    {
        AddQuadRaw(x1, y1, x2, y2, x3, y3, x4, y4,
            c.R / 255f, c.G / 255f, c.B / 255f, c.A / 255f,
            type, u1, v1, u2, v2);
    }

    /// <summary>
    /// General quad entry — routes to the solid buffer when type==0
    /// (flat-shaded colour, no UV needed by the solid shader) and to the
    /// general buffer otherwise (circle SDF, AA line, font texture).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void AddQuadRaw(float x1, float y1, float x2, float y2, float x3, float y3, float x4, float y4,
        float r, float g, float b, float a,
        float type, float u1, float v1, float u2, float v2)
    {
        if (type == 0f)
            AddSolidQuadRaw(x1, y1, x2, y2, x3, y3, x4, y4, r, g, b, a);
        else
            AddGeneralQuadRaw(x1, y1, x2, y2, x3, y3, x4, y4, r, g, b, a, type, u1, v1, u2, v2);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void AddSolidQuadRaw(float x1, float y1, float x2, float y2, float x3, float y3, float x4, float y4,
        float r, float g, float b, float a)
    {
        SwitchKind(BatchKind.Solid);
        EnsureSolidCapacity(6);
        var idx = SolidVertexCount * 6;
        var buf = SolidBuffer;

        buf[idx++] = x1;
        buf[idx++] = y1;
        buf[idx++] = r;
        buf[idx++] = g;
        buf[idx++] = b;
        buf[idx++] = a;
        buf[idx++] = x2;
        buf[idx++] = y2;
        buf[idx++] = r;
        buf[idx++] = g;
        buf[idx++] = b;
        buf[idx++] = a;
        buf[idx++] = x3;
        buf[idx++] = y3;
        buf[idx++] = r;
        buf[idx++] = g;
        buf[idx++] = b;
        buf[idx++] = a;

        buf[idx++] = x1;
        buf[idx++] = y1;
        buf[idx++] = r;
        buf[idx++] = g;
        buf[idx++] = b;
        buf[idx++] = a;
        buf[idx++] = x3;
        buf[idx++] = y3;
        buf[idx++] = r;
        buf[idx++] = g;
        buf[idx++] = b;
        buf[idx++] = a;
        buf[idx++] = x4;
        buf[idx++] = y4;
        buf[idx++] = r;
        buf[idx++] = g;
        buf[idx++] = b;
        buf[idx] = a;

        SolidVertexCount += 6;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void AddSolidQuadGradientRaw(float x1, float y1, float x2, float y2, float x3, float y3, float x4, float y4,
        float r1, float g1, float b1, float a1,
        float r2, float g2, float b2, float a2,
        float r3, float g3, float b3, float a3,
        float r4, float g4, float b4, float a4)
    {
        SwitchKind(BatchKind.Solid);
        EnsureSolidCapacity(6);
        var idx = SolidVertexCount * 6;
        var buf = SolidBuffer;

        buf[idx++] = x1;
        buf[idx++] = y1;
        buf[idx++] = r1;
        buf[idx++] = g1;
        buf[idx++] = b1;
        buf[idx++] = a1;
        buf[idx++] = x2;
        buf[idx++] = y2;
        buf[idx++] = r2;
        buf[idx++] = g2;
        buf[idx++] = b2;
        buf[idx++] = a2;
        buf[idx++] = x3;
        buf[idx++] = y3;
        buf[idx++] = r3;
        buf[idx++] = g3;
        buf[idx++] = b3;
        buf[idx++] = a3;

        buf[idx++] = x1;
        buf[idx++] = y1;
        buf[idx++] = r1;
        buf[idx++] = g1;
        buf[idx++] = b1;
        buf[idx++] = a1;
        buf[idx++] = x3;
        buf[idx++] = y3;
        buf[idx++] = r3;
        buf[idx++] = g3;
        buf[idx++] = b3;
        buf[idx++] = a3;
        buf[idx++] = x4;
        buf[idx++] = y4;
        buf[idx++] = r4;
        buf[idx++] = g4;
        buf[idx++] = b4;
        buf[idx] = a4;

        SolidVertexCount += 6;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void AddGeneralQuadRaw(float x1, float y1, float x2, float y2, float x3, float y3, float x4, float y4,
        float r, float g, float b, float a,
        float type, float u1, float v1, float u2, float v2)
    {
        SwitchKind(BatchKind.General);
        EnsureGeneralCapacity(6);
        var idx = GeneralVertexCount * 9;
        var buf = GeneralBuffer;

        buf[idx++] = x1;
        buf[idx++] = y1;
        buf[idx++] = r;
        buf[idx++] = g;
        buf[idx++] = b;
        buf[idx++] = a;
        buf[idx++] = u1;
        buf[idx++] = v1;
        buf[idx++] = type;
        buf[idx++] = x2;
        buf[idx++] = y2;
        buf[idx++] = r;
        buf[idx++] = g;
        buf[idx++] = b;
        buf[idx++] = a;
        buf[idx++] = u1;
        buf[idx++] = v2;
        buf[idx++] = type;
        buf[idx++] = x3;
        buf[idx++] = y3;
        buf[idx++] = r;
        buf[idx++] = g;
        buf[idx++] = b;
        buf[idx++] = a;
        buf[idx++] = u2;
        buf[idx++] = v2;
        buf[idx++] = type;

        buf[idx++] = x1;
        buf[idx++] = y1;
        buf[idx++] = r;
        buf[idx++] = g;
        buf[idx++] = b;
        buf[idx++] = a;
        buf[idx++] = u1;
        buf[idx++] = v1;
        buf[idx++] = type;
        buf[idx++] = x3;
        buf[idx++] = y3;
        buf[idx++] = r;
        buf[idx++] = g;
        buf[idx++] = b;
        buf[idx++] = a;
        buf[idx++] = u2;
        buf[idx++] = v2;
        buf[idx++] = type;
        buf[idx++] = x4;
        buf[idx++] = y4;
        buf[idx++] = r;
        buf[idx++] = g;
        buf[idx++] = b;
        buf[idx++] = a;
        buf[idx++] = u2;
        buf[idx++] = v1;
        buf[idx] = type;

        GeneralVertexCount += 6;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void AddTriangle(float x1, float y1, float x2, float y2, float x3, float y3, Color c) => AddSolidTriangleRaw(x1, y1, x2, y2, x3, y3, c.R / 255f, c.G / 255f, c.B / 255f, c.A / 255f);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void AddSolidTriangleRaw(float x1, float y1, float x2, float y2, float x3, float y3,
        float r, float g, float b, float a)
    {
        SwitchKind(BatchKind.Solid);
        EnsureSolidCapacity(3);
        var idx = SolidVertexCount * 6;
        var buf = SolidBuffer;

        buf[idx++] = x1;
        buf[idx++] = y1;
        buf[idx++] = r;
        buf[idx++] = g;
        buf[idx++] = b;
        buf[idx++] = a;
        buf[idx++] = x2;
        buf[idx++] = y2;
        buf[idx++] = r;
        buf[idx++] = g;
        buf[idx++] = b;
        buf[idx++] = a;
        buf[idx++] = x3;
        buf[idx++] = y3;
        buf[idx++] = r;
        buf[idx++] = g;
        buf[idx++] = b;
        buf[idx] = a;

        SolidVertexCount += 3;
    }
}
