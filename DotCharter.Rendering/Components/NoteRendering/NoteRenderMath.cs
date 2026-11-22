using System.Numerics;
using Avalonia.Media;
using DotCharter.Rendering.Data;
using DotCharter.Rendering.Scene;

namespace DotCharter.Rendering.Components.NoteRendering;

internal static class NoteRenderMath
{
    public const float BendMultiplier = FretboardConstants.StringDistance * RenderingConstants.BendMultiplierFactor;

    /// <summary>True only while a resume-rewind replay is passing over this sound —
    /// it is already judged and can never be judged again, so it draws dimmed. See
    /// <see cref="DeadNoteMath"/>.</summary>
    public static bool IsDeadNote(RenderContext context, double noteTimeMs)
        => DeadNoteMath.IsDead(noteTimeMs, context.TimeMs, context.Snapshot.LivePlayResolvedThroughMs);

    /// <summary>
    ///     THE open-note rule on the highway: a fret at or below the capo is
    ///     stopped BY the capo, so it sounds and draws as an OPEN note — a bar
    ///     spanning the active anchor, not a gem at that fret's middle. Note
    ///     heads, chord heads and sustain tails have always read it this way;
    ///     the hit-feedback components tested <c>fret == 0</c> instead and so
    ///     fired a point burst at <c>GetLinearFretMiddle(fret)</c> while the
    ///     note it belonged to was drawn as a bar somewhere else entirely.
    /// </summary>
    public static bool IsOpenFret(RenderContext context, int fret) => fret <= context.Snapshot.Capo;

    /// <summary>
    ///     Bend height (in steps) of a sound at <paramref name="targetTime" />,
    ///     interpolated across its bend points.
    ///     <para>
    ///         <paramref name="startTime" /> is the SOUND's own onset and is the
    ///         origin of the first ramp (the string is unbent when it is struck).
    ///         It must NOT be <c>context.TimeMs</c> — the playhead — which would make the
    ///         ramp's origin move every frame: a note whose first bend point sits
    ///         200 ms into its sustain would be drawn at ~95% of full bend height while
    ///         the playhead was still seconds away, and would sink onto its string as it
    ///         flies in. Bend height is a function of the CHART, not of the
    ///         transport.
    ///     </para>
    /// </summary>
    // startStep: the bend the tail starts at — a linked sound's final bend carried
    // into this one (RenderNote.BendCarry), held until the first bend point and
    // moved from towards it.
    public static float GetBendOffset(RenderContext context, int offset, int count, double startTime,
        double targetTime, float startStep = 0f)
    {
        if (count == 0) return startStep;
        var prevTime = startTime;
        var prevVal = (double)startStep;
        for (var i = 0; i < count; i++)
        {
            var bend = context.Snapshot.BendValues[offset + i];
            if (bend.Time >= targetTime)
            {
                var range = bend.Time - prevTime;
                if (range <= 0) return bend.Step;
                var t = (targetTime - prevTime) / range;
                return (float)(prevVal + (bend.Step - prevVal) * t);
            }

            prevTime = bend.Time;
            prevVal = bend.Step;
        }

        return (float)prevVal;
    }

    /// <summary>The anchor span the hand is in at <paramref name="atTime" />.
    /// Delegates to <see cref="AnchorFretSpan" /> so every "where is the hand"
    /// answer on the highway comes out of ONE clamp.</summary>
    public static (int minFret, int maxFret) GetAnchorFretRange(RenderContext context, double atTime)
        => AnchorFretSpan.FretRangeAt(context.Snapshot.Anchors, context.Snapshot.AnchorCount, atTime);

    public static (float x0, float x1) GetAnchorSpan(RenderContext context, double atTime)
        => AnchorFretSpan.WorldSpanAt(context.Snapshot.Anchors, context.Snapshot.AnchorCount, atTime);

    public static float GetNoteX(RenderContext context, double time, int fret)
    {
        if (fret > context.Snapshot.Capo) return FretboardConstants.GetLinearFretMiddle(fret);
        var (spanX0, spanX1) = GetAnchorSpan(context, time);
        return (spanX0 + spanX1) / 2f;
    }

    /// <summary>Half the height of an open-string bar, in world units.</summary>
    internal const float OpenNoteBarHalfHeight = 0.025f;

    public static void DrawOpenNoteBar(RenderContext context, double time, int stringIndex, Color nc, float nY,
        float zStart, float halfHeight = OpenNoteBarHalfHeight)
    {
        var (spanX0, spanX1) = GetAnchorSpan(context, time);
        var spanWidth = spanX1 - spanX0;
        var cx = (spanX0 + spanX1) / 2f;
        var flatMatrix = FretboardMath.GetFlatNoteModelMatrix(new Vector3(cx, nY, zStart));
        var v1 = Vector3.Transform(new Vector3(-spanWidth / 2f, halfHeight, 0), flatMatrix);
        var v2 = Vector3.Transform(new Vector3(spanWidth / 2f, halfHeight, 0), flatMatrix);
        var v3 = Vector3.Transform(new Vector3(spanWidth / 2f, -halfHeight, 0), flatMatrix);
        var v4 = Vector3.Transform(new Vector3(-spanWidth / 2f, -halfHeight, 0), flatMatrix);
        context.QuadBatcher.AddQuad(v1, v2, v3, v4, Vector3.UnitZ, nc, RenderingConstants.OpenNoteBarVertexType);
    }
}
