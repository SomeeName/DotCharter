using System.Numerics;
using Avalonia.Media;
using DotCharter.Rendering.Components.NoteRendering;
using DotCharter.Rendering.Data;
using DotCharter.Rendering.Data.Render;
using DotCharter.Rendering.Scene;
using DotCharter.Rendering.Vulkan;

namespace DotCharter.Rendering.Components;

public class BoardGeometryComponent : IRenderable
{
    // Constant capo bar hue hoisted out of the per-frame draw path.
    private static readonly Color CapoColor = Color.Parse("#CCCCCC");

    // Board-face depth stack (opaque pass, all near z≈0; more-negative Z = nearer
    // the anchor-follow camera). Each class is kept a hair apart so no two
    // coplanar surfaces z-fight — the owner-reported flicker was the fret bars and
    // the charter inlay dots BOTH sitting at z=-0.01 and fighting for the depth
    // buffer everywhere they overlapped. The metal fret bars now sit proud of the
    // inlay dots, which in turn sit proud of the string plane (z=0). The offsets
    // are sub-pixel at the board's ~2.5-unit camera distance, so nothing shifts
    // visibly; they only break the tie.
    private const float FretBarZ = -0.01f;
    private const float InlayZ = -0.005f;

    // Upcoming-string highlight: a string an upcoming note will be played on
    // brightens as that note nears. Buffer is stack-allocated per frame; sized
    // generously past any real tuning (StringColors tops out at 7).
    private const int MaxStrings = 12;

    private RenderResources _resources = null!;

    public void Initialize(IVulkanRenderService vulkanRenderer, RenderResources resources) => _resources = resources;

    public void Render(RenderContext context)
    {
        var minFret = CalculateMinFret(context);
        var maxFret = CalculateMaxFret(context, minFret);

        context.QuadBatcher.SetTexture(null);
        RenderBackdrop(context);
        RenderStrings(context);
        RenderFretBars(context, minFret, maxFret);
        RenderCapo(context);

        context.QuadBatcher.SetTexture(_resources.InlayTexture);
        RenderCharterInlays(context);
    }

    private int CalculateMinFret(RenderContext context)
    {
        RenderAnchor? currentAnchor = null;
        for (var i = context.Snapshot.AnchorCount - 1; i >= 0; i--)
        {
            if (context.Snapshot.Anchors[i].Time <= context.TimeMs)
            {
                currentAnchor = context.Snapshot.Anchors[i];
                break;
            }
        }

        int minFret = currentAnchor?.Fret ?? 1;
        return minFret <= 0 ? 1 : minFret;
    }

    private int CalculateMaxFret(RenderContext context, int minFret)
    {
        RenderAnchor? currentAnchor = null;
        for (var i = context.Snapshot.AnchorCount - 1; i >= 0; i--)
        {
            if (context.Snapshot.Anchors[i].Time <= context.TimeMs)
            {
                currentAnchor = context.Snapshot.Anchors[i];
                break;
            }
        }

        RenderAnchor? nextAnchor = null;
        for (var i = 0; i < context.Snapshot.AnchorCount; i++)
        {
            if (context.Snapshot.Anchors[i].Time > context.TimeMs &&
                context.Snapshot.Anchors[i].Time <= context.TimeMs + 3000)
            {
                nextAnchor = context.Snapshot.Anchors[i];
                break;
            }
        }

        var currentWidth = Math.Max(1, (int)(currentAnchor?.Width ?? 4));
        var maxFret = minFret + currentWidth - 1;
        if (nextAnchor.HasValue)
        {
            var nextWidth = Math.Max(1, (int)nextAnchor.Value.Width);
            maxFret = Math.Max(maxFret, nextAnchor.Value.Fret + nextWidth - 1);
        }

        if (maxFret <= 0) maxFret = 4;
        if (maxFret - minFret < 3) maxFret = minFret + 3;
        return Math.Min(24, maxFret);
    }

    /// <summary>
    ///     Scene backdrop: a subtle floor sheen under the board as a lane
    ///     depth cue. The gradient "wall" this originally drew was a finite
    ///     quad at a fixed Z whose edges showed up as a floating box at the
    ///     horizon — a proper sky gradient needs to be screen-space, so the
    ///     wall is gone until then and the void stays clean black.
    /// </summary>
    private void RenderBackdrop(RenderContext context)
    {
        // Floor sheen: a faint indigo wash under the near board that dissolves
        // toward the horizon — anchors the highway to a "stage floor". The wash
        // must never end in a visible boundary: a finite quad whose outer edge
        // still carries alpha reads as a hard diagonal seam against the void
        // (the black wedge under the board's near
        // end). So the full-strength core is flanked by two skirt quads whose
        // outer edges fade to fully transparent; every silhouette edge of the
        // sheen is alpha-0 regardless of camera fill or aspect.
        const float coreMin = -40f, coreMax = 70f;
        const float skirt = 55f;
        var y = FretboardConstants.ChartboardYPosition - 0.03f;
        var farZ = context.FadeEndZ;
        var floorNear = Color.FromArgb(96, 30, 24, 62);
        var floorFar = Color.FromArgb(0, 30, 24, 62);
        var clearNear = Color.FromArgb(0, 30, 24, 62);
        var n = new Vector3(0, 1, 0);

        // Core (unchanged coverage): near full-strength → far transparent.
        context.QuadBatcher.AddSafeFlatQuad(coreMin, coreMax, 0f, farZ, y, floorNear, floorFar);
        // Left skirt: outer edge transparent → core-strength at the core seam.
        context.QuadBatcher.AddQuad(
            new Vector3(coreMin - skirt, y, 0f), new Vector3(coreMin, y, 0f),
            new Vector3(coreMin, y, farZ), new Vector3(coreMin - skirt, y, farZ),
            n, clearNear, floorNear, floorFar, floorFar);
        // Right skirt, mirrored.
        context.QuadBatcher.AddQuad(
            new Vector3(coreMax, y, 0f), new Vector3(coreMax + skirt, y, 0f),
            new Vector3(coreMax + skirt, y, farZ), new Vector3(coreMax, y, farZ),
            n, floorNear, clearNear, floorFar, floorFar);
    }

    private void RenderStrings(RenderContext context)
    {
        var stringCount = context.Snapshot.StringCount;
        var thinStringThickness = FretboardConstants.StringThickness;

        // Which strings have an upcoming note in the lead window, and how near it
        // is (0 → 1 across the window). Reuses the anchor-preview lead so this cue
        // shares the app's single look-ahead knob (default 1000 ms). Allocation-
        // free; a string with nothing upcoming stays g=0 and renders byte-for-byte
        // as before.
        Span<float> stringGlow = stackalloc float[MaxStrings];
        ComputeUpcomingStringGlow(context, stringGlow, stringCount);

        for (var i = 0; i < stringCount; i++)
        {
            var y = FretboardConstants.BottomStringPosition +
                    FretboardConstants.StringLaneIndex(i, stringCount, context.Snapshot.InvertStrings) *
                    FretboardConstants.StringDistance;
            var sc = FretboardConstants.StringColorFor(i, stringCount);
            var g = i < MaxStrings ? stringGlow[i] : 0f;

            // Soft halo above/below the core: two flanking quads whose colour
            // fades from a translucent string tint to fully transparent, giving
            // each string a neon glow instead of a hard 1px wire. An active
            // string's halo brightens and widens (still its OWN colour — colour-
            // blind presets stay intact) as the upcoming note nears.
            var glowAlpha = (byte)Math.Min(255f, 70f + 120f * g);
            var glow = Color.FromArgb(glowAlpha, sc.R, sc.G, sc.B);
            var clear = Color.FromArgb(0, sc.R, sc.G, sc.B);
            var halo = thinStringThickness * (6f + 5f * g);
            context.QuadBatcher.AddQuad(
                new Vector3(0, y + halo, 0), new Vector3(context.BoardWidth, y + halo, 0),
                new Vector3(context.BoardWidth, y, 0), new Vector3(0, y, 0),
                new Vector3(0, 0, -1), clear, clear, glow, glow);
            context.QuadBatcher.AddQuad(
                new Vector3(0, y, 0), new Vector3(context.BoardWidth, y, 0),
                new Vector3(context.BoardWidth, y - halo, 0), new Vector3(0, y - halo, 0),
                new Vector3(0, 0, -1), glow, glow, clear, clear);

            // Bright core, nudged toward white so it reads as a lit metal wire.
            // Vertex mode 12 (plan 5.7): identical to the solid mode unless
            // the string-shimmer effect bit is set, in which case the shader
            // runs a subtle uTime light band along the wire. An active string
            // pushes further toward white and thickens slightly.
            var whiteMix = 0.18f + 0.34f * g;
            var core = Color.FromArgb(255,
                (byte)(sc.R + (255 - sc.R) * whiteMix),
                (byte)(sc.G + (255 - sc.G) * whiteMix),
                (byte)(sc.B + (255 - sc.B) * whiteMix));
            var coreThick = thinStringThickness * (1f + 0.5f * g);
            context.QuadBatcher.AddQuad(
                new Vector3(0, y + coreThick, 0), new Vector3(context.BoardWidth, y + coreThick, 0),
                new Vector3(context.BoardWidth, y - coreThick, 0), new Vector3(0, y - coreThick, 0),
                new Vector3(0, 0, -1), core, 12.0f);
        }
    }

    /// <summary>
    ///     Per-string upcoming-note intensity for the active-string highlight.
    ///     For each string, the nearest note (single or chord note) whose time is
    ///     inside the lead window scores 0 (a full window away) → 1 (at the
    ///     playhead). Allocation-free windowed scan; indexed by string, so the
    ///     buffer must be at least <see cref="MaxStrings" /> wide.
    /// </summary>
    private static void ComputeUpcomingStringGlow(RenderContext context, Span<float> glow, int stringCount)
    {
        glow.Clear();
        double lead = context.Snapshot.AnchorPreviewLeadMs;
        if (lead <= 0) return;
        var t = context.TimeMs;
        var end = t + lead;

        var notes = context.Snapshot.Notes;
        for (var i = 0; i < context.Snapshot.NoteCount; i++)
        {
            var n = notes[i];
            if (n.Time < t || n.Time > end) continue;
            int si = n.StringIndex;
            if (si >= glow.Length || si >= stringCount) continue;
            var v = (float)(1.0 - (n.Time - t) / lead);
            if (v > glow[si]) glow[si] = v;
        }

        var chords = context.Snapshot.Chords;
        var chordNotes = context.Snapshot.ChordNotes;
        for (var i = 0; i < context.Snapshot.ChordCount; i++)
        {
            var c = chords[i];
            if (c.Time < t || c.Time > end) continue;
            var v = (float)(1.0 - (c.Time - t) / lead);
            for (var j = 0; j < c.NoteCount; j++)
            {
                int si = chordNotes[c.NoteOffset + j].StringIndex;
                if (si >= glow.Length || si >= stringCount) continue;
                if (v > glow[si]) glow[si] = v;
            }
        }
    }

    private void RenderFretBars(RenderContext context, int minFret, int maxFret)
    {
        var stringCount = context.Snapshot.StringCount;
        var fY0 = FretboardConstants.BottomStringPosition - FretboardConstants.StringDistance / 2f;
        var fY1 = FretboardConstants.BottomStringPosition + FretboardConstants.StringDistance * (stringCount - 0.5f);
        // RsCharter Preview3DStringsFretsDrawer palette:
        //   PREVIEW_3D_FRET           (32,32,32)   � inactive bar
        //   PREVIEW_3D_ACTIVE_FRET    (192,192,192) � bar inside active FHP
        //   PREVIEW_3D_HIGHLIGHTED_FRET (255,160,0) � flash on note hit
        // Slightly brighter/cooler than the RsCharter values so bars stay
        // legible against the new gradient backdrop (was 192/192/192 active,
        // 32/32/32 inactive).
        var activeFretC = Color.FromArgb(255, 208, 212, 220);
        var inactiveFretC = Color.FromArgb(255, 52, 54, 62);
        var highlightFretC = Color.FromArgb(255, 255, 160, 0);

        // Fret highlight pulse driven by recently struck notes/chords (RsCharter
        // Preview3DStringsFretsDrawer: 100ms decay, sqrt fade, marks fret-1..fret).
        const double highlightWindowMs = 100.0;
        Span<float> highlight = stackalloc float[25];
        var timeMs = context.TimeMs;
        var notes = context.Snapshot.Notes;
        for (var i = 0; i < context.Snapshot.NoteCount; i++)
        {
            var note = notes[i];
            var dt = timeMs - note.Time;
            if (dt is < 0 or > highlightWindowMs) continue;
            var v = (float)Math.Sqrt((highlightWindowMs - dt) / highlightWindowMs);
            if (note.Fret == 0)
            {
                // Open note: highlight only the boundary frets of the active
                // FHP span � the lowest and highest fret bar of the anchor,
                // matching RsCharter Preview3DStringsFretsDrawer.
                var (anchorMin, anchorMax) = NoteRenderMath.GetAnchorFretRange(context, note.Time);
                ApplyHighlight(highlight, anchorMin - 1, v);
                ApplyHighlight(highlight, anchorMax, v);
            }
            else
            {
                ApplyHighlight(highlight, note.Fret - 1, v);
                ApplyHighlight(highlight, note.Fret, v);
            }
        }
        var chords = context.Snapshot.Chords;
        for (var i = 0; i < context.Snapshot.ChordCount; i++)
        {
            var c = chords[i];
            var dt = timeMs - c.Time;
            if (dt is < 0 or > highlightWindowMs) continue;
            var v = (float)Math.Sqrt((highlightWindowMs - dt) / highlightWindowMs);
            // RsCharter highlights only the boundary frets of the active FHP
            // anchor span on chord hits � the leftmost and rightmost fret bar
            // of the playable hand zone.
            var (anchorMin, anchorMax) = NoteRenderMath.GetAnchorFretRange(context, c.Time);
            ApplyHighlight(highlight, anchorMin - 1, v);
            ApplyHighlight(highlight, anchorMax, v);
        }

        for (var i = 0; i <= 24; i++)
        {
            var x = FretboardConstants.GetLinearFretPosition(i);
            var fc = i >= minFret - 1 && i <= maxFret ? activeFretC : inactiveFretC;
            var hl = highlight[i];
            if (hl > 0)
            {
                fc = Color.FromArgb(255,
                    (byte)(fc.R + (highlightFretC.R - fc.R) * hl),
                    (byte)(fc.G + (highlightFretC.G - fc.G) * hl),
                    (byte)(fc.B + (highlightFretC.B - fc.B) * hl));
            }
            var t = FretboardConstants.FretThickness * (1f + 3f * hl);
            // Vertical two-tone: brighter at the top edge, darker toward the
            // board — a cheap metallic light-catch that lifts the bars off the
            // backdrop without extra geometry.
            var fcTop = Color.FromArgb(fc.A,
                (byte)Math.Min(255, fc.R + (255 - fc.R) * 0.22f),
                (byte)Math.Min(255, fc.G + (255 - fc.G) * 0.22f),
                (byte)Math.Min(255, fc.B + (255 - fc.B) * 0.22f));
            var fcBottom = Color.FromArgb(fc.A, (byte)(fc.R * 0.72f), (byte)(fc.G * 0.72f), (byte)(fc.B * 0.72f));
            context.QuadBatcher.AddQuad(
                new Vector3(x - t, fY1, FretBarZ),
                new Vector3(x + t, fY1, FretBarZ),
                new Vector3(x + t, fY0, FretBarZ),
                new Vector3(x - t, fY0, FretBarZ),
                new Vector3(0, 0, -1), fcTop, fcTop, fcBottom, fcBottom);
        }
    }

    private static void ApplyHighlight(Span<float> arr, int idx, float v)
    {
        if (idx < 0 || idx >= arr.Length) return;
        if (v > arr[idx]) arr[idx] = v;
    }

    private void RenderCharterInlays(RenderContext context)
    {
        if (_resources.InlayTexture == null) return;
        var stringCount = context.Snapshot.StringCount;
        var y0 = FretboardConstants.BottomStringPosition - FretboardConstants.StringDistance / 2f;
        var y1 = FretboardConstants.BottomStringPosition + FretboardConstants.StringDistance * (stringCount - 0.5f);
        var color = Colors.White;

        for (var i = 0; i <= 24; i++)
        {
            var xMin = FretboardConstants.GetLinearFretPosition(i);
            var xMax = FretboardConstants.GetLinearFretPosition(i + 1);
            var tx0 = i % 8 / 8.0f;
            var tx1 = tx0 + 0.125f;
            // ReSharper disable once PossibleLossOfFraction
            var ty0 = i / 8 / 4.0f; // integer row index (0–3) mapped to V coordinate
            var ty1 = ty0 + 0.25f;
            const float z = InlayZ; // a hair behind the fret bars so the two never z-fight

            context.QuadBatcher.AddQuadUv(
                new Vector3(xMin, y0, z), new Vector3(xMin, y1, z),
                new Vector3(xMax, y1, z), new Vector3(xMax, y0, z),
                Vector3.UnitZ, color,
                new Vector2(tx0, ty0), new Vector2(tx0, ty1),
                new Vector2(tx1, ty1), new Vector2(tx1, ty0), 4.0f);
        }
    }

    private void RenderCapo(RenderContext context)
    {
        var capo = context.Snapshot.Capo;
        var stringCount = context.Snapshot.StringCount;
        if (capo > 0)
        {
            var fretMax = FretboardConstants.GetLinearFretPosition(capo);
            var fretMin = FretboardConstants.GetLinearFretPosition(capo - 1);
            var cx = fretMax - (fretMax - fretMin) * 0.85f;
            var y0 = FretboardConstants.BottomStringPosition - FretboardConstants.StringDistance / 2f;
            var y1 = FretboardConstants.BottomStringPosition + FretboardConstants.StringDistance * (stringCount - 0.5f);
            var capoColor = CapoColor;

            context.QuadBatcher.AddQuad(
                new Vector3(cx - 0.08f, y1 + 0.1f, -0.05f), new Vector3(cx + 0.08f, y1 + 0.1f, -0.05f),
                new Vector3(cx + 0.08f, y0 - 0.1f, -0.05f), new Vector3(cx - 0.08f, y0 - 0.1f, -0.05f),
                new Vector3(0, 0, -1), capoColor);
        }
    }
}
