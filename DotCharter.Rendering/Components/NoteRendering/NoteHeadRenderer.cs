using System.Numerics;
using Avalonia.Media;
using DotCharter.Rendering.Data;
using DotCharter.Rendering.Data.Render;
using DotCharter.Rendering.Scene;

namespace DotCharter.Rendering.Components.NoteRendering;

internal static class NoteHeadRenderer
{
    /// <summary>
    ///     Depth of the landing-target frame a note paints on the board as it
    ///     arrives. It sat at exactly z = 0 — the string plane, whose opaque string
    ///     and halo quads are depth-written there — so the frame z-fought them:
    ///     its edges broke into teeth along every string and whole sides dropped
    ///     out. A hair in front of the strings and still BEHIND the inlay dots
    ///     (-0.005) and fret bars (-0.01), so the wires keep crossing over it like
    ///     paint on the board. See the board-face depth stack in
    ///     <c>BoardGeometryComponent</c>.
    /// </summary>
    internal const float AnticipationZ = -0.0025f;

    /// <summary>
    ///     Rocksmith draws an ACCENTED note with a white rim around the gem. The
    ///     rim is the head's own sprite in white, this much larger, staged right
    ///     behind the head so only its border shows. The flag reached the frame
    ///     snapshot but no 3D renderer read it, so accents looked like plain notes.
    /// </summary>
    internal const float AccentRimScale = 1.3f;

    /// <summary>An accented open-string bar: a white bar this much taller, behind the coloured one.</summary>
    internal const float AccentOpenBarScale = 1.9f;

    internal static readonly Color AccentRimColor = Color.FromArgb(255, 255, 255, 255);

    internal static Color AccentRim(bool dead) => dead ? DeadNoteMath.Dim(AccentRimColor) : AccentRimColor;

    /// <summary>Stages the accent rim behind a fretted head drawn with <paramref name="headMatrix" />.</summary>
    internal static void DrawAccentRim(RenderContext context, Matrix4x4 headMatrix, float hw, int baseCol, bool dead) =>
        context.QuadBatcher.AddTexturedQuad_Matrix(headMatrix, hw * AccentRimScale, AccentRim(dead), GetU(baseCol),
            GetU(baseCol + 1), GetV(0), GetV(1), 3.0f);

    /// <summary>Stages the accent bar behind an open-string bar.</summary>
    internal static void DrawAccentOpenBar(RenderContext context, double time, int stringIndex, float nY, float z,
        bool dead) =>
        NoteRenderMath.DrawOpenNoteBar(context, time, stringIndex, AccentRim(dead), nY, z,
            NoteRenderMath.OpenNoteBarHalfHeight * AccentOpenBarScale);

    /// <summary>Every single-note head, FARTHEST first: the transparent pass
    /// writes no depth, so a head staged after a nearer one paints over it.
    /// <see cref="NotesRenderComponent" /> interleaves the same per-note step with
    /// the chords and hand shapes in ONE farthest-first walk.</summary>
    public static void Render(RenderContext context)
    {
        for (var i = context.Snapshot.NoteCount - 1; i >= 0; i--) RenderNote(context, i);
    }

    /// <summary>Single note <paramref name="index" />'s head (or nothing, when it
    /// is out of view). Expects the notes texture bound.</summary>
    public static void RenderNote(RenderContext context, int index)
    {
        var note = context.Snapshot.Notes[index];
        if (note.Time < context.TimeMs || note.Time >= context.TimeMs + context.HorizonMs) return;

        var zStart = context.GetZDepthForTime(note.Time, context.TimeMs);
        if (zStart > context.FadeEndZ) return;
        if ((note.Flags & NoteFlags.LinkPrevious) != 0) return;

        DrawHead(context, note, zStart);
    }

    /// <summary>
    ///     One single-note head at <paramref name="zStart" />: the gem (or open bar),
    ///     its halo, accent rim, technique overlay and anticipation frame. Also how
    ///     <see cref="ChordRenderer" /> draws a RE-PICKED string of a link landing:
    ///     the export writes that landing as loose single notes, so the game shows
    ///     the re-picked string as a note head, not as a chord.
    /// </summary>
    internal static void DrawHead(RenderContext context, in RenderNote note, float zStart)
    {
        var bendYOffset = NoteRenderMath.GetBendOffset(context, note.BendOffset, note.BendCount, note.Time, note.Time);
        var nY = FretboardConstants.BottomStringPosition +
                 FretboardConstants.StringLaneIndex(note.StringIndex, context.Snapshot.StringCount,
                     context.Snapshot.InvertStrings) * FretboardConstants.StringDistance +
                 bendYOffset * NoteRenderMath.BendMultiplier;
        // Resume-rewind replay: this note is already judged and inert. Dim it,
        // and skip the "get ready" cues (halo, anticipation flash) — they would
        // promise a hit that cannot happen.
        var dead = NoteRenderMath.IsDeadNote(context, note.Time);
        var nc = FretboardConstants.StringColorFor(note.StringIndex, context.Snapshot.StringCount);
        if (dead) nc = DeadNoteMath.Dim(nc);
        var isOpen = note.Fret <= context.Snapshot.Capo;
        var nX = NoteRenderMath.GetNoteX(context, note.Time, note.Fret);
        var hw = FretboardConstants.NoteHalfWidth;
        var accent = (note.Flags & NoteFlags.Accent) != 0;

        if (isOpen)
        {
            var (barX0, barX1) = NoteRenderMath.GetAnchorSpan(context, note.Time);
            NoteStemRenderer.DrawOpen(context, barX0, barX1, nY, zStart, nc);
            if (accent) DrawAccentOpenBar(context, note.Time, note.StringIndex, nY, zStart, dead);
            NoteRenderMath.DrawOpenNoteBar(context, note.Time, note.StringIndex, nc, nY, zStart);
        }
        else
        {
            NoteStemRenderer.DrawFretted(context, nX, nY, zStart, note.Fret, nc);
            var noteMatrix =
                FretboardMath.GetFrettedNoteRotatedModelMatrix(new Vector3(nX, nY, zStart), context.TimeMs,
                    note.Time);
            var isTech = note.Mute == (byte)MuteType.Full || note.Hopo == (byte)HopoType.HammerOn ||
                         note.Hopo == (byte)HopoType.PullOff || note.Hopo == (byte)HopoType.Tap;
            var baseCol = isTech ? 2 : 0;

            // Soft radial halo in the string colour behind the head — makes
            // the plastic note sprite read as an emissive marker against the
            // dark backdrop (shader mode 8 ignores the bound texture).
            if (!dead)
            {
                var haloColor = Color.FromArgb(88, nc.R, nc.G, nc.B);
                context.QuadBatcher.AddTexturedQuad_Matrix(noteMatrix, hw * 1.65f, haloColor, 0f, 1f, 0f, 1f, 8.0f);
            }

            if (accent) DrawAccentRim(context, noteMatrix, hw, baseCol, dead);
            context.QuadBatcher.AddTexturedQuad_Matrix(noteMatrix, hw, nc, GetU(baseCol), GetU(baseCol + 1),
                GetV(0), GetV(1), 3.0f);
        }

        // Allow Mutes, HOPOs, etc. to be rendered cleanly ON TOP of the note (even for Open notes)
        ApplyOverlays(context, nX, nY, zStart, note.Mute, note.Hopo, note.Harmonic, note.BassPicking, hw, nc);

        var anticipationMs = context.AnticipationMs;
        if (!dead && note.Time - context.TimeMs < anticipationMs)
        {
            var dt = note.Time - context.TimeMs;
            var scale = Math.Min(1, 1.0 - 0.5 * (dt - anticipationMs * 0.5) / anticipationMs);
            scale *= scale;
            // Ramps 0 → 255 over the second half of the cue. Expressed
            // against the (possibly shortened) window rather than a baked
            // 500, so the fade keeps its shape on a fast highway.
            var alpha = (byte)Math.Clamp(255.0 * (1.0 - dt / anticipationMs) * 2.0, 0, 255);
            var antColor = Color.FromArgb(alpha, nc.R, nc.G, nc.B);

            if (isOpen)
            {
                var (spanX0, spanX1) = NoteRenderMath.GetAnchorSpan(context, note.Time);
                var spanWidth = spanX1 - spanX0;
                var cx = (spanX0 + spanX1) / 2f;
                var flatMatrix = Matrix4x4.CreateScale(1f, (float)scale, 1f) *
                                 FretboardMath.GetFlatNoteModelMatrix(new Vector3(cx, nY, AnticipationZ));

                var v1 = Vector3.Transform(new Vector3(-spanWidth / 2f, 0.025f, 0), flatMatrix);
                var v2 = Vector3.Transform(new Vector3(spanWidth / 2f, 0.025f, 0), flatMatrix);
                var v3 = Vector3.Transform(new Vector3(spanWidth / 2f, -0.025f, 0), flatMatrix);
                var v4 = Vector3.Transform(new Vector3(-spanWidth / 2f, -0.025f, 0), flatMatrix);

                context.QuadBatcher.AddQuad(v1, v2, v3, v4, Vector3.UnitZ, antColor,
                    RenderingConstants.OpenNoteBarVertexType);
            }
            else
            {
                var antMatrix = Matrix4x4.CreateScale((float)scale, (float)scale, 1f) *
                                Matrix4x4.CreateTranslation(nX, nY, AnticipationZ);
                context.QuadBatcher.AddTexturedQuad_Matrix(antMatrix, hw, antColor, GetU(1), GetU(2), GetV(0),
                    GetV(1), 3.0f);
            }
        }
    }

    public static void ApplyOverlays(RenderContext context, float nX, float nY, float zStart, byte mute, byte hopo,
        byte harmonic, byte bassPicking, float hw, Color nc)
    {
        int modCol = -1, modRow = -1;
        if (mute == (byte)MuteType.Full)
        {
            modCol = 1;
            modRow = 2;
        }
        else if (mute == (byte)MuteType.Palm)
        {
            modCol = 0;
            modRow = 2;
        }
        else if (hopo == (byte)HopoType.HammerOn)
        {
            modCol = 0;
            modRow = 1;
        }
        else if (hopo == (byte)HopoType.PullOff)
        {
            modCol = 1;
            modRow = 1;
        }
        else if (hopo == (byte)HopoType.Tap)
        {
            modCol = 2;
            modRow = 1;
        }
        else if (harmonic == (byte)HarmonicType.Normal)
        {
            modCol = 0;
            modRow = 3;
        }
        else if (harmonic == (byte)HarmonicType.Pinch)
        {
            modCol = 1;
            modRow = 3;
        }
        else if (bassPicking == 2)
        {
            modCol = 2;
            modRow = 3;
        }
        else if (bassPicking == 1)
        {
            modCol = 3;
            modRow = 3;
        }

        if (modCol >= 0)
        {
            var overlayMatrix = FretboardMath.GetFrettedNoteRotatedModelMatrix(
                new Vector3(nX, nY, zStart + FretboardConstants.NoteHeadOffset), context.TimeMs, context.TimeMs);
            context.QuadBatcher.AddTexturedQuad_Matrix(overlayMatrix, hw, nc, GetU(modCol), GetU(modCol + 1),
                GetV(modRow), GetV(modRow + 1), 3.0f);
        }
    }

    private static float GetU(int c) => c * 0.25f;

    private static float GetV(int r) => r * 0.25f;
}
