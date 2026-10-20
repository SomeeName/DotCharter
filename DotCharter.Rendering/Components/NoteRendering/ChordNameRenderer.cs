using System.Numerics;
using Avalonia.Media;
using DotCharter.Rendering.Data;
using DotCharter.Rendering.Data.Render;
using DotCharter.Rendering.Scene;

namespace DotCharter.Rendering.Components.NoteRendering;

/// <summary>
///     Rocksmith-style chord names on the 3D highway: the shape's name rides at
///     the nut-side edge of its own hand position, above the strings and at the
///     shape's own depth, so it flies in with the chord and reads as a label ON it.
///     <para>
///         <b>Why not centred over the span.</b> That is where it started, and it
///         put the text directly over the incoming note heads of a wide shape —
///         the one place a charter is already looking. Sitting at the left edge
///         keeps it clear of the gems while staying attached to the shape.
///     </para>
///     <para>
///         <b>The x is relative to the ANCHOR.</b> The camera follows the hand, so
///         a fixed world x is off-screen above the first position.
///     </para>
///     <para>
///         <b>This label is only visible because of the pass it draws in, not
///         its geometry.</b> Drawing it from <c>NotesRenderComponent</c>,
///         which lives in the TRANSPARENT pass — i.e. before the engine calls
///         <c>TextBatcher.SetState(atlas, sceneMvp)</c> — would stage the glyphs
///         against a null atlas (frame 1: <c>DrawText3D</c> returns immediately)
///         and then against the previous frame's ortho HUD matrix, and no change
///         to this label's x, y or scale could fix that. It therefore draws from
///         <see cref="ChordNameComponent" /> in the Ui pass; the placement below
///         is verified by a golden frame
///         (<c>HeadlessChordNameTests</c>) rather than by argument.
///     </para>
///     <para>
///         Names come from the chord TEMPLATE, so everything the naming rules
///         already decided applies here for free — most visibly, a two-string
///         double stop has no name (<c>DoubleStopNameOptions</c>) and therefore
///         draws no label, which is also what Rocksmith does.
///     </para>
/// </summary>
internal static class ChordNameRenderer
{
    private static readonly Color LabelColor = Color.Parse("#44DDFF");

    /// <summary>
    ///     The camera's own minimum span. <c>CalculateCameraMvp</c> widens any
    ///     anchor narrower than this (<c>if (maxFret - minFret &lt; 3) maxFret =
    ///     minFret + 3</c>), so the label has to reason about the span the CAMERA
    ///     framed, not the one the anchor declared — otherwise a width-1 anchor
    ///     computes a centre the camera is not looking at.
    /// </summary>
    private const int CameraMinSpanFrets = 4;

    /// <summary>
    ///     Height above the top string. The strings span only 1.75 units
    ///     (<c>5 × StringDistance</c>), so this sits the name just clear of the
    ///     top string and of the incoming note heads — BESIDE the shape, not
    ///     floating over it.
    ///     <para>
    ///         2.0 put it a whole neck above the board and, combined with the
    ///         centred x below, left it stranded in open sky far from the chord
    ///         it names (owner: "not middle center top that far away"). In
    ///         Rocksmith and rscharter the name hugs its shape.
    ///     </para>
    /// </summary>
    private const float HeightAboveStrings = 0.85f;

    /// <summary>
    ///     Roughly twice the fret numbers (0.003–0.005) — those sit at the board
    ///     at a fixed short distance while a chord name rides its own chord's
    ///     DEPTH, so it needs a head start to stay readable further out.
    ///     <para>
    ///         <b>0.026 is far too big</b>
    ///         (the chord names read as gigantic). The mistake to avoid is
    ///         calibrating on an 800x450 headless dump, where everything looks
    ///         small, and over-correcting for a label that is not
    ///         rendering at all. In Rocksmith and rscharter the chord name is a
    ///         modest label near the shape, not a billboard over the neck — check
    ///         it against a 1600x900 frame, which is closer to how the window is
    ///         actually sized. 0.010 is still too heavy once the label sits in
    ///         beside the shape, where it competes with the fingering numbers.
    ///     </para>
    /// </summary>
    private const float Scale = 0.0065f;

    public static void Render(RenderContext context)
    {
        var top = FretboardConstants.BottomStringPosition +
                  (context.Snapshot.StringCount - 1) * FretboardConstants.StringDistance;
        var y = top + HeightAboveStrings;

        // ONE LABEL PER HAND SHAPE, at the shape's first chord.
        //
        // A hand shape is one hand position, so it gets one name — that is what
        // Rocksmith does and what the owner described ("the chord name is
        // rendered only start of handshape"). Labelling every CHORD instead put
        // the same name on screen once per strum: a shape holding two strums of
        // A7 drew "A7" twice, at two depths, which is what the owner reported.
        //
        // A chord with no shape over it still labels itself — plenty of charts
        // have bare chords — so the rule is "first chord in its shape, or no
        // shape at all", not "only chords under a shape".
        for (var i = 0; i < context.Snapshot.ChordCount; i++)
        {
            var chord = context.Snapshot.Chords[i];
            // A split chord ships as single notes: the game names nothing there.
            if ((chord.Flags & ChordFlags.SplitIntoNotes) != 0) continue;
            if (!IsShapeLeadChord(context.Snapshot.Chords, context.Snapshot.ChordCount,
                    context.Snapshot.HandShapes, context.Snapshot.HandShapeCount, i))
            {
                continue;
            }

            if (!TryLabel(context, chord.TemplateId, chord.Time, out var name, out var z)) continue;
            Draw(context, name, chord.Time, z, y);
        }

        for (var i = 0; i < context.Snapshot.HandShapeCount; i++)
        {
            var hs = context.Snapshot.HandShapes[i];
            if (CoversAChord(context.Snapshot.Chords, context.Snapshot.ChordCount, hs.Time, hs.EndTime)) continue;
            if (!TryLabel(context, hs.TemplateId, hs.Time, out var name, out var z)) continue;
            Draw(context, name, hs.Time, z, y);
        }
    }

    /// <summary>
    ///     Whether the chord at <paramref name="chordIndex" /> is the one that
    ///     carries its hand shape's name: the FIRST chord inside the shape
    ///     covering it, or any chord with no shape over it at all.
    ///     <para>
    ///         This is what stops a shape holding several strums of the same
    ///         chord from drawing its name once per strum (owner report: "A7"
    ///         twice inside one hand shape). Coverage is <c>HandShape.Covers</c> —
    ///         start inclusive, end EXCLUSIVE — the same span rule the matcher,
    ///         the anchor generator and <see cref="CoversAChord" /> use; shapes
    ///         abut, so a closed reading would attribute a chord to two of them.
    ///     </para>
    ///     <para>
    ///         Pure and allocation-free: it runs per chord per frame.
    ///     </para>
    /// </summary>
    internal static bool IsShapeLeadChord(
        ReadOnlySpan<RenderChord> chords, int chordCount,
        ReadOnlySpan<RenderHandShape> shapes, int shapeCount, int chordIndex)
    {
        if (chordIndex < 0 || chordIndex >= chordCount || chordIndex >= chords.Length) return false;
        var time = chords[chordIndex].Time;

        // The shape covering this chord, if any.
        var start = 0.0;
        var end = 0.0;
        var covered = false;
        for (var i = 0; i < shapeCount && i < shapes.Length; i++)
        {
            if (time < shapes[i].Time || time >= shapes[i].EndTime) continue;
            start = shapes[i].Time;
            end = shapes[i].EndTime;
            covered = true;
            break;
        }

        if (!covered) return true; // a bare chord names itself

        // Only the earliest chord in that shape carries the name. Compared by
        // TIME rather than index because the snapshot's chord array is windowed
        // and GC-reused — index order is not guaranteed to be time order.
        for (var i = 0; i < chordCount && i < chords.Length; i++)
        {
            if (i == chordIndex) continue;
            var other = chords[i].Time;
            if (other < start || other >= end) continue;
            if (other < time) return false;
            // Exact tie: the lowest index wins, so a tie cannot silence both.
            if (other == time && i < chordIndex) return false;
        }

        return true;
    }

    /// <summary>
    ///     Whether a chord starts inside this hand shape — i.e. whether the chord
    ///     pass has already labelled this template at this depth.
    ///     <para>
    ///         Span-based and allocation-free because it runs per hand shape per
    ///         frame, and pure so the "chords win" rule is testable without a GPU.
    ///     </para>
    /// </summary>
    /// <param name="chords">The snapshot's chord array (may be longer than <paramref name="count" />).</param>
    /// <param name="count">Live entries in <paramref name="chords" />.</param>
    /// <param name="start">Hand-shape start, inclusive.</param>
    /// <param name="end">Hand-shape end, EXCLUSIVE — the span rule the matcher and anchors use.</param>
    internal static bool CoversAChord(ReadOnlySpan<RenderChord> chords, int count, double start, double end)
    {
        for (var i = 0; i < count && i < chords.Length; i++)
        {
            var time = chords[i].Time;
            if (time >= start && time < end) return true;
        }

        return false;
    }

    /// <summary>
    ///     How far LEFT of the framed span's centre the label sits, as a fraction
    ///     of the span's width. The name belongs BESIDE its shape on the nut side,
    ///     which is where Rocksmith and rscharter put it — dead-centre above the
    ///     shape reads as a caption floating in the sky rather than a label on the
    ///     chord (owner: "move the chord names left beside the chord not middle
    ///     center").
    ///     <para>
    ///         Expressed as a FRACTION rather than world units on purpose: the
    ///         camera frames the span, so a fixed offset would be a different
    ///         apparent distance at every neck position, and up near fret 24 —
    ///         where the frets bunch — a unit offset walks the label clean off
    ///         screen. 0.42 keeps it inside the framed window at every anchor,
    ///         which the golden frames check by asserting the label touches no
    ///         frame edge.
    ///     </para>
    /// </summary>
    private const float LeftOfCentreFraction = 0.42f;

    /// <summary>
    ///     World x for a label whose shape is governed by an anchor opening on
    ///     <paramref name="anchorFret" /> and <paramref name="anchorWidth" />
    ///     frets wide: LEFT of the centre of the span the camera framed.
    ///     <para>
    ///         The centre is <c>middleFhpPosition</c>, which is definitionally
    ///         what the camera points at (<c>Fretboard3DEngine.CalculateCameraMvp</c>
    ///         centres on <c>middleFhpPosition * 0.9 + weightedPosition * 0.1</c>,
    ///         so the span's own EDGES are not reliably on screen — the 0.1 pull
    ///         toward the neck's centre shifts the window off the span). Working
    ///         out from that centre is what keeps a nut-side offset on screen;
    ///         v0.41.1 measured from the span instead and shipped a label nobody
    ///         ever saw.
    ///     </para>
    ///     <para>
    ///         Pure so the placement is testable without a GPU — a SECOND line of
    ///         defence now rather than the only one, since
    ///         <c>HeadlessChordNameTests</c> renders the label for real and proves
    ///         it lands inside the viewport.
    ///     </para>
    /// </summary>
    internal static float LabelX(int anchorFret, int anchorWidth)
    {
        var minFret = Math.Max(1, anchorFret);
        var maxFret = minFret + Math.Max(1, anchorWidth) - 1;
        // The camera's own widening, mirrored — see CameraMinSpanFrets.
        if (maxFret - minFret < CameraMinSpanFrets - 1) maxFret = minFret + CameraMinSpanFrets - 1;
        maxFret = Math.Min(24, maxFret);

        var right = FretboardConstants.GetLinearFretPosition(maxFret);
        var left = FretboardConstants.GetLinearFretPosition(minFret - 1);
        var centre = (right + left) / 2f;

        // Nut-side of centre. Clamped at the nut so a first-position shape cannot
        // push the label off the board's near end.
        return Math.Max(0f, centre - (right - left) * LeftOfCentreFraction);
    }

    private static bool TryLabel(
        RenderContext context, int templateId, double time, out string name, out float z)
    {
        name = string.Empty;
        z = 0;

        if (time < context.TimeMs || time >= context.TimeMs + context.HorizonMs) return false;
        if (templateId < 0 || templateId >= context.Snapshot.TemplateCount) return false;

        var template = context.Snapshot.Templates[templateId];
        if (string.IsNullOrEmpty(template.Name)) return false;

        z = context.GetZDepthForTime(time, context.TimeMs);
        if (z > context.FadeEndZ) return false;

        name = template.Name;
        return true;
    }

    private static void Draw(RenderContext context, string name, double time, float z, float y)
    {
        // The anchor governing THIS shape, not the one under the playhead — the
        // label has to sit beside its own chord as that chord flies in, and the
        // hand may be somewhere else entirely by the time it arrives.
        var baseFret = 1;
        var baseWidth = CameraMinSpanFrets;
        for (var a = context.Snapshot.AnchorCount - 1; a >= 0; a--)
        {
            if (context.Snapshot.Anchors[a].Time > time) continue;
            baseFret = Math.Max(1, (int)context.Snapshot.Anchors[a].Fret);
            baseWidth = Math.Max(1, (int)context.Snapshot.Anchors[a].Width);
            break;
        }

        context.TextBatcher.DrawText3D(
            name, Matrix4x4.CreateTranslation(LabelX(baseFret, baseWidth), y, z), LabelColor, Scale);
    }
}
