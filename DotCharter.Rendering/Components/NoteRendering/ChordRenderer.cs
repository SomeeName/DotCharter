using System.Numerics;
using Avalonia.Media;
using DotCharter.Rendering.Data;
using DotCharter.Rendering.Data.Render;
using DotCharter.Rendering.Scene;

namespace DotCharter.Rendering.Components.NoteRendering;

internal static class ChordRenderer
{
    /// <summary>A technique on ANY chord note keeps EVERY sibling's tail visible:
    /// the strum physically sustains as one unit, so a bend on the G string must not
    /// leave the B string's sustain invisible (only the
    /// technique-bearing string would render).</summary>
    private const NoteFlags TailTechniqueFlags = NoteFlags.HasSlide | NoteFlags.UnpitchedSlide |
                                                 NoteFlags.Vibrato | NoteFlags.Tremolo | NoteFlags.HasBends;

    /// <summary>Every chord, farthest first: the transparent pass writes no depth,
    /// so a nearer box staged earlier would be painted over by a farther one.
    /// <see cref="NotesRenderComponent" /> interleaves the same per-chord step with
    /// the single notes and hand shapes in ONE farthest-first walk.</summary>
    public static void Render(RenderContext context, RenderResources resources)
    {
        for (var i = context.Snapshot.ChordCount - 1; i >= 0; i--) RenderChord(context, resources, i);
    }

    /// <summary>Chord <paramref name="index" />'s box, fill and heads (or nothing,
    /// when it is out of view). Expects the pass untextured and hands it back so.</summary>
    public static void RenderChord(RenderContext context, RenderResources resources, int index)
    {
        var chord = context.Snapshot.Chords[index];
        double maxSustain = 0;
        var isFullyMuted = chord.NoteCount > 0;
        var isPalmMutedAll = chord.NoteCount > 0;
        var isLinkPrevious = false;
        var hasTechniques = false;

        for (var j = 0; j < chord.NoteCount; j++)
        {
            var cn = context.Snapshot.ChordNotes[chord.NoteOffset + j];
            if (cn.Sustain > maxSustain) maxSustain = cn.Sustain;
            if (cn.Mute != (byte)MuteType.Full) isFullyMuted = false;
            if (cn.Mute != (byte)MuteType.Palm) isPalmMutedAll = false;
            if ((cn.Flags & NoteFlags.LinkPrevious) != 0) isLinkPrevious = true;
            if (cn.Sustain > 0) hasTechniques = true;
            if ((cn.Flags & (NoteFlags.LinkNext | NoteFlags.HasSlide | NoteFlags.UnpitchedSlide
                             | NoteFlags.Vibrato | NoteFlags.Tremolo | NoteFlags.HasBends)) != 0)
            {
                hasTechniques = true;
            }
        }

        if (chord.Time + maxSustain < context.TimeMs || chord.Time >= context.TimeMs + context.HorizonMs) return;
        var zStart = context.GetZDepthForTime(chord.Time, context.TimeMs);
        if (zStart > context.FadeEndZ) return;

        RenderHandShape? activeHs = null;
        for (var h = 0; h < context.Snapshot.HandShapeCount; h++)
        {
            var hs = context.Snapshot.HandShapes[h];
            if (chord.Time >= hs.Time && chord.Time < hs.EndTime)
            {
                activeHs = hs;
                break;
            }
        }

        // Arpeggio hand shapes get the Rocksmith bracket presentation
        // (HandShapeRenderer): the chord keeps only a slim outline frame
        // — no solid gradient fill — and its notes render individually.
        var isArpeggio = false;
        if (activeHs is { } tplHs && tplHs.TemplateId >= 0 && tplHs.TemplateId < context.Snapshot.TemplateCount)
            isArpeggio = context.Snapshot.Templates[tplHs.TemplateId].Arpeggio;

        // The repeat box is DATA: ChordFlags.Repeat says the export
        // ships this chord without chord notes, which is exactly what the
        // game draws as the frame-only box. The rule lives with the export
        // (Services' RepeatChords over RsChordDensity); the renderer must not
        // run its own port of RsCharter's Level.shouldChordShowNotes, a
        // different algorithm, or the preview and the game would disagree.
        var isRepeat = (chord.Flags & ChordFlags.Repeat) != 0;
        // A split chord ships as single notes (Services' LinkLandingBreakup), so
        // the game draws no box: heads and tails as single notes draw them.
        var isSplit = (chord.Flags & ChordFlags.SplitIntoNotes) != 0;

        // Otherwise RsCharter's Chord.chordNotesVisibility: a fully
        // fret-hand-muted strum with no technique is the muted box (the X
        // below); everything else shows its notes.
        bool drawNotes;
        if (isRepeat) drawNotes = false;
        else if (isArpeggio || hasTechniques) drawNotes = true;
        else drawNotes = !isFullyMuted;

        // RsCharter chord box: floor (chartboard) up to one string above top string,
        // half-height when there are no notes drawn (onlyBox).
        var yBottom = FretboardConstants.ChartboardYPosition;
        var yTopFull = FretboardConstants.BottomStringPosition +
                       context.Snapshot.StringCount * FretboardConstants.StringDistance;
        var onlyBox = !drawNotes;
        var yTop = onlyBox ? (yBottom + yTopFull) * 0.5f : yTopFull;

        // Match RsCharter Preview3DChordBoxDrawer: span = current FHP/anchor range,
        // not extended by the chord template's own frets.
        int boxMinFret, boxMaxFret;
        if (activeHs.HasValue)
            (boxMinFret, boxMaxFret) = NoteRenderMath.GetAnchorFretRange(context, activeHs.Value.Time);
        else
            (boxMinFret, boxMaxFret) = NoteRenderMath.GetAnchorFretRange(context, chord.Time);

        var x0 = FretboardConstants.GetLinearFretPosition(boxMinFret - 1);
        var x1 = FretboardConstants.GetLinearFretPosition(boxMaxFret);

        // RsCharter PREVIEW_3D_CHORD_BOX (0,210,213) and PREVIEW_3D_CHORD_BOX_DARK (0,60,61).
        // A chord replayed by a resume rewind is already judged and inert — the
        // whole box (frame, fill, heads) dims so it reads as dead, not as due.
        var dead = NoteRenderMath.IsDeadNote(context, chord.Time);
        var fillColor = Color.FromArgb(32, 0, 210, 213);
        var fillColorDark = Color.FromArgb(32, 0, 60, 61);
        var frameColor = Color.FromArgb(128, 0, 210, 213);
        var frameColorDark = Color.FromArgb(128, 0, 60, 61);
        // An accented chord: Rocksmith's white frame, and every head gets the
        // accent rim single notes have (NoteHeadRenderer.AccentRimScale).
        var chordAccent = (chord.Flags & ChordFlags.Accent) != 0;
        if (chordAccent)
        {
            frameColor = Color.FromArgb(255, 255, 255, 255);
            frameColorDark = Color.FromArgb(255, 150, 150, 150);
        }

        if (dead)
        {
            fillColor = DeadNoteMath.Dim(fillColor);
            fillColorDark = DeadNoteMath.Dim(fillColorDark);
            frameColor = DeadNoteMath.Dim(frameColor);
            frameColorDark = DeadNoteMath.Dim(frameColorDark);
        }

        // Arpeggios keep the note heads fully visible: slimmer frame,
        // no fill panel behind them.
        var edgeThickness = isArpeggio ? 0.04f : 0.075f;
        var paneZ = zStart;
        var edgeZ = zStart - 0.001f;
        var noteZ = zStart - 0.002f;

        // Tails are NOT drawn here: SustainRenderer.Render stages every tail,
        // single-note and chord, in one bottom-lane-first order before any
        // head or box. The per-string rule is TailsInView + ShowsTail.

        // A link LANDING ships as loose single notes (Services'
        // LinkLandingBreakup), so the game draws no box and nothing for the
        // strings that arrive linked — but a string RE-PICKED there is a fresh
        // note it draws as a single head. Skipping this would drop every head of the
        // landing, so the re-picked 8 of "5 slides to 6 over a re-picked 8"
        // would never appear.
        if ((isLinkPrevious || isSplit) && zStart >= 0f)
            DrawRepickedLandingHeads(context, resources, chord, chordAccent);

        // Strictly at-or-ahead of the hit plane: a negative zStart put the
        // box/heads BEHIND the fretboard (between board and camera), where
        // they projected huge over the strings (visible whenever playback
        // paused inside the old -1.0f grace window). Single note heads
        // already cull at the playhead; chords now match.
        if (!isLinkPrevious && !isSplit && zStart >= 0f)
        {
            // Filling: horizontal gradient light → dark → light (RsCharter drawChordBoxFilling).
            // Vertex order: p1=top-left, p2=top-right, p3=bottom-right, p4=bottom-left.
            // Suppressed for arpeggios — the individual note heads carry
            // the shape, only the outline frame remains.
            var midX = (x0 + x1) * 0.5f;
            var n = new Vector3(0, 0, -1);
            if (!isArpeggio)
            {
                context.QuadBatcher.AddQuad(new Vector3(x0, yTop, paneZ), new Vector3(midX, yTop, paneZ),
                    new Vector3(midX, yBottom, paneZ), new Vector3(x0, yBottom, paneZ), n,
                    fillColor, fillColorDark, fillColorDark, fillColor);
                context.QuadBatcher.AddQuad(new Vector3(midX, yTop, paneZ), new Vector3(x1, yTop, paneZ),
                    new Vector3(x1, yBottom, paneZ), new Vector3(midX, yBottom, paneZ), n,
                    fillColorDark, fillColor, fillColor, fillColorDark);
            }

            // Bottom (floor) bar — always present
            context.QuadBatcher.AddQuad(new Vector3(x0, yBottom + edgeThickness, edgeZ),
                new Vector3(midX, yBottom + edgeThickness, edgeZ),
                new Vector3(midX, yBottom, edgeZ),
                new Vector3(x0, yBottom, edgeZ), n, frameColor, frameColorDark, frameColorDark, frameColor);
            context.QuadBatcher.AddQuad(new Vector3(midX, yBottom + edgeThickness, edgeZ),
                new Vector3(x1, yBottom + edgeThickness, edgeZ),
                new Vector3(x1, yBottom, edgeZ),
                new Vector3(midX, yBottom, edgeZ), n, frameColorDark, frameColor, frameColor, frameColorDark);

            if (!onlyBox)
            {
                // Top bar
                context.QuadBatcher.AddQuad(new Vector3(x0, yTop + edgeThickness, edgeZ),
                    new Vector3(midX, yTop + edgeThickness, edgeZ),
                    new Vector3(midX, yTop, edgeZ),
                    new Vector3(x0, yTop, edgeZ), n, frameColor, frameColorDark, frameColorDark, frameColor);
                context.QuadBatcher.AddQuad(new Vector3(midX, yTop + edgeThickness, edgeZ),
                    new Vector3(x1, yTop + edgeThickness, edgeZ),
                    new Vector3(x1, yTop, edgeZ),
                    new Vector3(midX, yTop, edgeZ), n, frameColorDark, frameColor, frameColor, frameColorDark);

                // Side posts (full height)
                context.QuadBatcher.AddQuad(new Vector3(x0 - edgeThickness, yTop, edgeZ),
                    new Vector3(x0 + edgeThickness, yTop, edgeZ),
                    new Vector3(x0 + edgeThickness, yBottom, edgeZ),
                    new Vector3(x0 - edgeThickness, yBottom, edgeZ), n, frameColor);
                context.QuadBatcher.AddQuad(new Vector3(x1 - edgeThickness, yTop, edgeZ),
                    new Vector3(x1 + edgeThickness, yTop, edgeZ),
                    new Vector3(x1 + edgeThickness, yBottom, edgeZ),
                    new Vector3(x1 - edgeThickness, yBottom, edgeZ), n, frameColor);
            }
            else
            {
                // Short side posts that fade to transparent at the top.
                var fadedFrame = Color.FromArgb(0, frameColor.R, frameColor.G, frameColor.B);
                var fadeY = (yTop + yBottom) * 0.5f;
                context.QuadBatcher.AddQuad(new Vector3(x0 - edgeThickness, fadeY, edgeZ),
                    new Vector3(x0 + edgeThickness, fadeY, edgeZ),
                    new Vector3(x0 + edgeThickness, yBottom, edgeZ),
                    new Vector3(x0 - edgeThickness, yBottom, edgeZ), n, frameColor);
                context.QuadBatcher.AddQuad(new Vector3(x0 - edgeThickness, yTop, edgeZ),
                    new Vector3(x0 + edgeThickness, yTop, edgeZ),
                    new Vector3(x0 + edgeThickness, fadeY, edgeZ),
                    new Vector3(x0 - edgeThickness, fadeY, edgeZ), n, fadedFrame, fadedFrame, frameColor,
                    frameColor);
                context.QuadBatcher.AddQuad(new Vector3(x1 - edgeThickness, fadeY, edgeZ),
                    new Vector3(x1 + edgeThickness, fadeY, edgeZ),
                    new Vector3(x1 + edgeThickness, yBottom, edgeZ),
                    new Vector3(x1 - edgeThickness, yBottom, edgeZ), n, frameColor);
                context.QuadBatcher.AddQuad(new Vector3(x1 - edgeThickness, yTop, edgeZ),
                    new Vector3(x1 + edgeThickness, yTop, edgeZ),
                    new Vector3(x1 + edgeThickness, fadeY, edgeZ),
                    new Vector3(x1 - edgeThickness, fadeY, edgeZ), n, fadedFrame, fadedFrame, frameColor,
                    frameColor);
            }

            // Mute X is only drawn for onlyBox boxes (per RsCharter chordBox.mute set
            // only when visibility==NONE).
            if (onlyBox && (isFullyMuted || isPalmMutedAll))
            {
                var muteColor = Color.FromArgb(255, 128, 216, 255);
                context.QuadBatcher.AddQuad(new Vector3(x0, yBottom, paneZ), new Vector3(x1, yTop, paneZ),
                    new Vector3(x1 - edgeThickness, yTop, paneZ), new Vector3(x0 - edgeThickness, yBottom, paneZ),
                    Vector3.UnitZ, muteColor);
                context.QuadBatcher.AddQuad(new Vector3(x0, yTop, paneZ), new Vector3(x1, yBottom, paneZ),
                    new Vector3(x1 - edgeThickness, yBottom, paneZ), new Vector3(x0 - edgeThickness, yTop, paneZ),
                    Vector3.UnitZ, muteColor);
            }

            if (drawNotes)
            {
                context.QuadBatcher.SetTexture(resources.NotesTexture);
                // Iterate strings high-index first → low-index last so that when
                // bends/vibrato push two chord-notes onto the same Y, the lower
                // string index (closer to the treble side, drawn later) wins
                // the alpha-blend. Without this, e.g. blue (string 2) would
                // cover yellow (string 1) merely because of source ordering.
                for (var j = chord.NoteCount - 1; j >= 0; j--)
                {
                    var cn = context.Snapshot.ChordNotes[chord.NoteOffset + j];
                    int actualFret = cn.Fret;
                    var bendYOffset =
                        NoteRenderMath.GetBendOffset(context, cn.BendOffset, cn.BendCount, chord.Time, chord.Time);
                    var nY = FretboardConstants.BottomStringPosition +
                             FretboardConstants.StringLaneIndex(cn.StringIndex, context.Snapshot.StringCount,
                                 context.Snapshot.InvertStrings) *
                             FretboardConstants.StringDistance + bendYOffset * NoteRenderMath.BendMultiplier;
                    var nc = FretboardConstants.StringColorFor(
                        cn.StringIndex, context.Snapshot.StringCount);
                    if (dead) nc = DeadNoteMath.Dim(nc);
                    var nX = NoteRenderMath.GetNoteX(context, chord.Time, actualFret);
                    var hw = FretboardConstants.NoteHalfWidth;
                    var flatMatrix = FretboardMath.GetFlatNoteModelMatrix(new Vector3(nX, nY, noteZ));
                    var accent = chordAccent || (cn.Flags & NoteFlags.Accent) != 0;

                    if (actualFret <= context.Snapshot.Capo)
                    {
                        if (accent)
                            NoteHeadRenderer.DrawAccentOpenBar(context, chord.Time, cn.StringIndex, nY, noteZ, dead);
                        NoteRenderMath.DrawOpenNoteBar(context, chord.Time, cn.StringIndex, nc, nY, noteZ);
                    }
                    else
                    {
                        if (accent) NoteHeadRenderer.DrawAccentRim(context, flatMatrix, hw, 0, dead);
                        context.QuadBatcher.AddTexturedQuad_Matrix(flatMatrix, hw, nc, 0.0f, 0.25f, 0.0f, 0.25f,
                            3.0f);
                    }

                    NoteHeadRenderer.ApplyOverlays(context, nX, nY, noteZ, cn.Mute, cn.Hopo, cn.Harmonic,
                        cn.BassPicking, hw, nc);
                }

                context.QuadBatcher.SetTexture(null);
            }
        }
    }

    /// <summary>
    ///     Whether <paramref name="chord" /> can show a tail this frame (in the
    ///     window, not past the draw distance, not a repeat), and the two facts about
    ///     the whole chord that <see cref="ShowsTail" /> reads. The tails themselves
    ///     are staged by <see cref="SustainRenderer.Render" />, in one order with the
    ///     single-note tails.
    /// </summary>
    internal static bool TailsInView(RenderContext context, in RenderChord chord, out ChordTailFacts facts)
    {
        facts = default;
        // A repeat ships without a sustain (CompactRepeatSustainTests), so it draws none.
        if ((chord.Flags & ChordFlags.Repeat) != 0) return false;
        double maxSustain = 0;
        var anyTechnique = false;
        var linkPrevious = false;
        for (var j = 0; j < chord.NoteCount; j++)
        {
            ref readonly var cn = ref context.Snapshot.ChordNotes[chord.NoteOffset + j];
            if (cn.Sustain > maxSustain) maxSustain = cn.Sustain;
            if ((cn.Flags & NoteFlags.LinkPrevious) != 0) linkPrevious = true;
            if ((cn.Flags & TailTechniqueFlags) != 0) anyTechnique = true;
        }

        if (chord.Time + maxSustain < context.TimeMs || chord.Time >= context.TimeMs + context.HorizonMs) return false;
        if (context.GetZDepthForTime(chord.Time, context.TimeMs) > context.FadeEndZ) return false;
        facts = new ChordTailFacts(anyTechnique, linkPrevious);
        return true;
    }

    /// <summary>Whether chord note <paramref name="cn" /> of a chord that passed
    /// <see cref="TailsInView" /> shows its sustain tail.</summary>
    internal static bool ShowsTail(RenderContext context, in RenderChord chord, in RenderChordNote cn,
        ChordTailFacts facts)
    {
        if (cn.Sustain <= 0) return false;
        var hasTechnique = (cn.Flags & TailTechniqueFlags) != 0;
        var linkTargetTechnique = (cn.Flags & NoteFlags.LinkTargetHasTechnique) != 0;
        if ((cn.Flags & NoteFlags.LinkNext) != 0 && !hasTechnique && !linkTargetTechnique && !facts.AnyTechnique)
            return false;

        // A link-PREVIOUS note is the landing of a slide/link chain (chord slides
        // always sit inside a hand shape): its ring-out sustain must stay visible;
        // only PLAIN chords let the hand shape stand in for their sustain. And a
        // chord that is a link LANDING ships as single notes, and a single note
        // always shows its sustain, so a string re-picked on the landing keeps its
        // tail like its linked sibling.
        var inHandShape = (chord.Flags & ChordFlags.InHandShape) != 0;
        var isLinkTarget = (cn.Flags & NoteFlags.LinkPrevious) != 0;
        var isSplit = (chord.Flags & ChordFlags.SplitIntoNotes) != 0;
        if (inHandShape && !hasTechnique && !linkTargetTechnique && !isLinkTarget && !facts.AnyTechnique &&
            !facts.LinkPrevious && !isSplit)
        {
            return false;
        }

        var zStart = Math.Max(0f, context.GetZDepthForTime(chord.Time, context.TimeMs));
        var zEnd = Math.Max(0f, context.GetZDepthForTime(chord.Time + cn.Sustain, context.TimeMs));
        return zStart < context.FadeEndZ && zEnd > zStart;
    }

    /// <summary>The heads of a link landing's strings that do NOT arrive linked,
    /// drawn the way single notes are (<see cref="NoteHeadRenderer.DrawHead" />).</summary>
    private static void DrawRepickedLandingHeads(RenderContext context, RenderResources resources, RenderChord chord,
        bool chordAccent)
    {
        var bound = false;
        var zStart = context.GetZDepthForTime(chord.Time, context.TimeMs);
        for (var j = chord.NoteCount - 1; j >= 0; j--)
        {
            var cn = context.Snapshot.ChordNotes[chord.NoteOffset + j];
            if ((cn.Flags & NoteFlags.LinkPrevious) != 0) continue;
            if (!bound)
            {
                context.QuadBatcher.SetTexture(resources.NotesTexture);
                bound = true;
            }

            NoteHeadRenderer.DrawHead(context, new RenderNote
            {
                Time = chord.Time, Sustain = cn.Sustain, StringIndex = cn.StringIndex, Fret = cn.Fret,
                Mute = cn.Mute, Hopo = cn.Hopo, Harmonic = cn.Harmonic, BassPicking = cn.BassPicking,
                SlideTo = cn.SlideTo, BendOffset = cn.BendOffset, BendCount = cn.BendCount,
                Flags = chordAccent ? cn.Flags | NoteFlags.Accent : cn.Flags
            }, zStart);
        }

        if (bound) context.QuadBatcher.SetTexture(null);
    }

    /// <summary>What <see cref="ShowsTail" /> needs to know about the whole chord.</summary>
    internal readonly record struct ChordTailFacts(bool AnyTechnique, bool LinkPrevious);
}
