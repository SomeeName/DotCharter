using System.Numerics;
using Avalonia.Media;
using DotCharter.Rendering.Data;
using DotCharter.Rendering.Data.Render;
using DotCharter.Rendering.Scene;

namespace DotCharter.Rendering.Components.NoteRendering;

/// <summary>
///     Rocksmith-style finger numbers on the fretboard for the grip the player
///     is about to hold: one numbered disc per finger at its fret, a capsule
///     for a barre.
///     <para>
///         Exactly ONE grip is shown at a time (<see cref="TrySelectGrip" />):
///         the hand shape or bare chord that started earliest among those still
///         sounding or starting within <see cref="LeadMs" />. Drawing
///         every hand shape and chord inside a 200-250 ms window would make the
///         overlay effectively invisible — a paused editor would show nothing unless
///         the cursor sat in the last fifth of a second before a chord, and at
///         the strike the note heads, drawn after it, would cover every number.
///     </para>
/// </summary>
public static class FingeringRenderer
{
    // Toggle for the 3D fingering overlay. Default off so users opt in.
    // Persisted via AppConfig.Show3DFingering: Settings > Graphics ("Show
    // fingering overlay on 3D preview") or the Toggle3DFingering keybind (F8,
    // the EOF "Fingering view" key; it was Ctrl+F before the keybind wave).
    public static bool Enabled { get; set; }

    /// <summary>How far ahead of a grip its fingering appears.</summary>
    internal const double LeadMs = 750;

    /// <summary>The first part of <see cref="LeadMs" /> is a fade-in, so a
    /// grip does not pop onto the neck.</summary>
    internal const double FadeInMs = 250;

    /// <summary>How long a bare chord (no hand shape around it) keeps its
    /// fingering after the strum when its notes do not sustain longer.</summary>
    internal const double ChordHoldMs = 150;

    /// <summary>Alpha of the disc/capsule body. Below 1 so a note head landing
    /// on the same spot still reads through; the number itself stays opaque.</summary>
    internal const byte ShapeAlpha = 215;

    // The overlay draws on the single Vulkan render thread (one Preview3D
    // instance; headless GPU tests are serialized), so these scratch buffers are
    // reused across frames rather than reallocated per call.
    private static readonly int[] MinStringForFinger = new int[5];
    private static readonly int[] MaxStringForFinger = new int[5];
    private static readonly int[] FretsForFinger = new int[5];

    // Fixed atlas UVs (0.25-cell grid) — computed once, never per finger/frame.
    // Row 0: single disc, capsule end (rounded TOP), capsule middle, "T".
    // Row 1: "1".."4".
    private static readonly Vector2 ShapeSingleUv = Uv(0, 0);
    private static readonly Vector2 ShapeEndUv = Uv(1, 0);
    private static readonly Vector2 ShapeMiddleUv = Uv(2, 0);
    private static readonly Vector2[] FingerLetterUvs = [Uv(3, 0), Uv(0, 1), Uv(1, 1), Uv(2, 1), Uv(3, 1)];

    private static Vector2 Uv(int tx, int ty) => new(tx * 0.25f, ty * 0.25f);

    public static void Render(RenderContext context, RenderResources resources)
    {
        if (!Enabled) return;
        if (resources.FingeringTexture == null) return;
        RenderCore(context);
    }

    /// <summary>The overlay without the <see cref="Enabled" /> gate or the
    /// texture check — what the tests drive, so none of them has to flip the
    /// process-wide static.</summary>
    internal static void RenderCore(RenderContext context)
    {
        if (!TrySelectGrip(context.Snapshot, context.TimeMs, out var templateId, out var startMs)) return;

        var template = FindTemplate(context.Snapshot, templateId);
        if (!template.HasValue) return;

        DrawTemplateFingering(context, template.Value, FadeAlpha(startMs, context.TimeMs));
    }

    /// <summary>
    ///     The one grip whose fingering is on the neck at <paramref name="timeMs" />:
    ///     among the hand shapes and the bare chords (a chord inside any hand
    ///     shape is that shape's, not its own) that have not ended yet and start
    ///     within <see cref="LeadMs" />, the one that started EARLIEST — so a held
    ///     shape keeps its numbers until it ends, and only then does the next
    ///     grip take over.
    /// </summary>
    internal static bool TrySelectGrip(FrameSnapshot snapshot, double timeMs, out int templateId, out double startMs)
    {
        templateId = -1;
        startMs = double.MaxValue;

        for (var i = 0; i < snapshot.HandShapeCount; i++)
        {
            var hs = snapshot.HandShapes[i];
            if (hs.EndTime < timeMs || hs.Time - LeadMs > timeMs) continue;
            if (hs.Time >= startMs) continue;
            templateId = hs.TemplateId;
            startMs = hs.Time;
        }

        for (var i = 0; i < snapshot.ChordCount; i++)
        {
            var chord = snapshot.Chords[i];
            // A split chord ships as single notes, with no grip to show.
            if ((chord.Flags & ChordFlags.SplitIntoNotes) != 0) continue;
            if (chord.Time - LeadMs > timeMs || chord.Time >= startMs) continue;
            if (ChordEnd(snapshot, chord) < timeMs) continue;
            if (IsInsideHandShape(snapshot, chord.Time)) continue;
            templateId = chord.TemplateId;
            startMs = chord.Time;
        }

        return templateId >= 0;
    }

    private static double ChordEnd(FrameSnapshot snapshot, in RenderChord chord)
    {
        var sustain = 0.0;
        var end = Math.Min(chord.NoteOffset + chord.NoteCount, snapshot.ChordNoteCount);
        for (var n = chord.NoteOffset; n < end; n++)
            sustain = Math.Max(sustain, snapshot.ChordNotes[n].Sustain);
        return chord.Time + Math.Max(ChordHoldMs, sustain);
    }

    private static bool IsInsideHandShape(FrameSnapshot snapshot, double time)
    {
        for (var i = 0; i < snapshot.HandShapeCount; i++)
        {
            var hs = snapshot.HandShapes[i];
            if (time >= hs.Time && time <= hs.EndTime) return true;
        }

        return false;
    }

    /// <summary>0 at <see cref="LeadMs" /> out, 1 from <see cref="LeadMs" /> −
    /// <see cref="FadeInMs" /> on (smoothstep in between).</summary>
    internal static float FadeAlpha(double startMs, double timeMs)
    {
        var t = (startMs - timeMs - (LeadMs - FadeInMs)) / FadeInMs; // 0 = fully in, 1 = LeadMs out
        var x = Math.Clamp(1.0 - t, 0.0, 1.0);
        return (float)(x * x * (3 - 2 * x));
    }

    private static RenderChordTemplate? FindTemplate(FrameSnapshot snapshot, int templateId)
    {
        for (var t = 0; t < snapshot.TemplateCount; t++)
        {
            if (snapshot.Templates[t].Id == templateId)
                return snapshot.Templates[t];
        }

        return null;
    }

    private static void DrawTemplateFingering(RenderContext context, RenderChordTemplate template, float alpha)
    {
        if (alpha <= 0f) return;

        var minStringForFinger = MinStringForFinger;
        var maxStringForFinger = MaxStringForFinger;
        var fretsForFinger = FretsForFinger;
        for (var f = 0; f < 5; f++)
        {
            minStringForFinger[f] = 99;
            maxStringForFinger[f] = -1;
            fretsForFinger[f] = 1;
        }

        for (var s = 0; s < context.Snapshot.StringCount; s++)
        {
            var finger = template.GetFinger(s);
            var fret = template.GetFret(s);
            if (finger is >= 0 and <= 4 && fret > 0)
            {
                if (s < minStringForFinger[finger]) minStringForFinger[finger] = s;
                if (s > maxStringForFinger[finger]) maxStringForFinger[finger] = s;
                fretsForFinger[finger] = fret;
            }
        }

        for (var f = 0; f < 5; f++)
        {
            if (maxStringForFinger[f] >= 0)
                DrawFinger(context, f, fretsForFinger[f], minStringForFinger[f], maxStringForFinger[f], alpha);
        }
    }

    private static void DrawFinger(RenderContext context, int fingerIndex, int fret, int minString, int maxString,
        float alpha)
    {
        var letterUv = FingerLetterUvs[fingerIndex];
        var shapeColor = Color.FromArgb((byte)(ShapeAlpha * alpha), 255, 255, 255);
        var letterColor = Color.FromArgb((byte)(255 * alpha), 255, 255, 255);

        if (minString == maxString)
        {
            DrawFingeringQuad(context, fret, maxString, ShapeSingleUv, false, shapeColor);
            DrawLetter(context, fret, maxString, letterUv, letterColor);
            return;
        }

        // A barre: rounded caps on the two OUTER lanes, straight segments
        // between. Which string is on top depends on the string order setting,
        // so the caps follow the LANES, not the string indices — with inverted
        // strings the lowest string index sits at the bottom and its cap must
        // round downward. The number goes on the upper cap.
        var count = context.Snapshot.StringCount;
        var invert = context.Snapshot.InvertStrings;
        var minLane = FretboardConstants.StringLaneIndex(minString, count, invert);
        var maxLane = FretboardConstants.StringLaneIndex(maxString, count, invert);
        var (topString, bottomString) = minLane > maxLane ? (minString, maxString) : (maxString, minString);

        DrawFingeringQuad(context, fret, topString, ShapeEndUv, false, shapeColor);
        for (var s = minString + 1; s < maxString; s++)
            DrawFingeringQuad(context, fret, s, ShapeMiddleUv, false, shapeColor);
        DrawFingeringQuad(context, fret, bottomString, ShapeEndUv, true, shapeColor);
        DrawLetter(context, fret, topString, letterUv, letterColor);
    }

    // Lefty mode mirrors the scene's X through the camera matrix; a glyph drawn
    // with the normal U range would come out as mirror writing. Swapping U for
    // the letter quad cancels the mirror (the disc and capsule are symmetric).
    private static void DrawLetter(RenderContext context, int fret, int stringIndex, Vector2 uv, Color color) =>
        DrawFingeringQuad(context, fret, stringIndex, uv, false, color, context.Snapshot.LeftyFlip);

    private static void DrawFingeringQuad(RenderContext context, int fret, int stringIndex, Vector2 uvStart,
        bool invertY, Color color, bool invertX = false)
    {
        var x = FretboardConstants.GetLinearFretMiddle(fret);
        var size = FretboardConstants.StringDistance / 2f;
        var x0 = x - size;
        var x1 = x + size;
        var yString = FretboardConstants.BottomStringPosition +
                      FretboardConstants.StringLaneIndex(stringIndex, context.Snapshot.StringCount,
                          context.Snapshot.InvertStrings) * FretboardConstants.StringDistance;
        var y0 = yString + size;
        var y1 = yString - size;
        const float z = -0.01f;

        var u0 = uvStart.X + 0.001f;
        var u1 = uvStart.X + 0.249f;
        var v0 = uvStart.Y + 0.001f;
        var v1 = uvStart.Y + 0.249f;
        if (invertY) (v0, v1) = (v1, v0);
        if (invertX) (u0, u1) = (u1, u0);
        context.QuadBatcher.AddQuadUv(
            new Vector3(x0, y0, z), new Vector3(x0, y1, z),
            new Vector3(x1, y1, z), new Vector3(x1, y0, z),
            Vector3.UnitZ, color,
            new Vector2(u0, v0), new Vector2(u0, v1),
            new Vector2(u1, v1), new Vector2(u1, v0), 4.0f);
    }
}
