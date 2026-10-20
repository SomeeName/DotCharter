using System.Numerics;
using Avalonia.Media;
using DotCharter.Rendering.Data;
using DotCharter.Rendering.Data.Render;
using DotCharter.Rendering.Scene;

namespace DotCharter.Rendering.Components.NoteRendering;

internal static class SustainRenderer
{
    /// <summary>
    ///     THE tail pass: every sustain/slide tail on the highway, single-note AND
    ///     chord, staged in ONE order by the height it is drawn at, lowest first
    ///     A tail is a flat ribbon at its string's height and the
    ///     camera looks down on every string (<see cref="FretboardConstants.CameraBaseHeight" />
    ///     is above the top lane), so where two tails overlap on screen the higher
    ///     one is the nearer. The pass writes no depth, so staging order IS
    ///     occlusion: in snapshot order (time, then string) the A tail painted over
    ///     the low E's, and while <see cref="ChordRenderer" /> staged chord tails
    ///     after every single-note tail, a chord's A tail still did.
    ///     <para>
    ///         Each pass stages one <see cref="DrawKey" /> (a lane, or the gap above
    ///         it that a bend lifts a stretch of tail into) and returns the next
    ///         occupied key, so a frame costs one walk per occupied height plus one
    ///         and allocates nothing. It runs before every note head and chord box.
    ///     </para>
    /// </summary>
    public static void Render(RenderContext context)
    {
        var key = Pass(context, int.MinValue);
        while (key != int.MaxValue) key = Pass(context, key);
    }

    /// <summary>The string's row in the highway stack, bottom = 0 (see <see cref="Render" />).</summary>
    internal static int Lane(RenderContext context, int stringIndex) => FretboardConstants.StringLaneIndex(stringIndex,
        context.Snapshot.StringCount, context.Snapshot.InvertStrings);

    /// <summary>
    ///     Where a stretch of tail is drawn, as a staging order: <c>2 * lane</c> ON a
    ///     string, odd in the gap above it. A bend lifts its tail
    ///     <see cref="RenderingConstants.BendMultiplierFactor" /> of a lane per
    ///     half-step, so a full bend reaches the next string's height and a 1 1/2-step
    ///     bend passes it. Vibrato is left out on purpose: its wobble
    ///     (<see cref="RenderingConstants.VibratoAmplitude" />, about a third of a
    ///     lane) never reaches a neighbouring string.
    /// </summary>
    internal static int DrawKey(int lane, float bendSteps)
    {
        var lift = bendSteps * RenderingConstants.BendMultiplierFactor;
        var whole = MathF.Floor(lift);
        return 2 * (lane + (int)whole) + (lift > whole ? 1 : 0);
    }

    /// <summary>Stages every stretch of tail whose draw key is <paramref name="key" />
    /// and returns the smallest occupied key above it (<see cref="int.MaxValue" />
    /// when none is left). Single notes in snapshot order, then chords farthest
    /// first, as <see cref="ChordRenderer" /> walks them.</summary>
    private static int Pass(RenderContext context, int key)
    {
        var snapshot = context.Snapshot;
        var next = int.MaxValue;
        for (var i = 0; i < snapshot.NoteCount; i++)
        {
            ref readonly var note = ref snapshot.Notes[i];
            if (!HasVisibleTail(context, note)) continue;
            next = Visit(context, key, next, new Tail(note.Time, note.Sustain, note.StringIndex, note.Flags,
                note.SlideTo, note.BendOffset, note.BendCount, note.Fret, note.BendCarry));
        }

        for (var c = snapshot.ChordCount - 1; c >= 0; c--)
        {
            ref readonly var chord = ref snapshot.Chords[c];
            if (!ChordRenderer.TailsInView(context, chord, out var facts)) continue;
            for (var j = 0; j < chord.NoteCount; j++)
            {
                ref readonly var cn = ref snapshot.ChordNotes[chord.NoteOffset + j];
                if (!ChordRenderer.ShowsTail(context, chord, cn, facts)) continue;
                next = Visit(context, key, next, new Tail(chord.Time, cn.Sustain, cn.StringIndex, cn.Flags,
                    cn.SlideTo, cn.BendOffset, cn.BendCount, cn.Fret, cn.BendCarry));
            }
        }

        return next;
    }

    private static int Visit(RenderContext context, int key, int next, in Tail tail)
    {
        var lane = Lane(context, tail.StringIndex);
        var (keyMin, keyMax) = KeyRange(context, tail, lane);
        if (key >= keyMin && key <= keyMax) DrawTail(context, tail, lane, new KeySlice(key, keyMin, keyMax));
        return keyMax > key ? Math.Min(next, Math.Max(keyMin, key + 1)) : next;
    }

    /// <summary>The lowest and highest draw key any stretch of this tail can take:
    /// its lane, unless a bend (or a bend carried in by a link) lifts it.</summary>
    private static (int Min, int Max) KeyRange(RenderContext context, in Tail tail, int lane)
    {
        if (!tail.Bends) return (2 * lane, 2 * lane);
        var low = tail.BendCarry;
        var high = tail.BendCarry;
        for (var b = 0; b < tail.BendCount; b++)
        {
            var step = context.Snapshot.BendValues[tail.BendOffset + b].Step;
            low = Math.Min(low, step);
            high = Math.Max(high, step);
        }

        return (DrawKey(lane, low), DrawKey(lane, high));
    }

    private static bool HasVisibleTail(RenderContext context, in RenderNote note) => note.Sustain > 0 &&
        note.Time + note.Sustain >= context.TimeMs && note.Time < context.TimeMs + context.HorizonMs;

    /// <summary>
    ///     The vibrato / tremolo weight at <paramref name="absTime" />: 1, except that a
    ///     link to a tail without the wobble eases it in (<see cref="NoteFlags.RampIn" />)
    ///     or out (<see cref="NoteFlags.RampOut" />) over <see cref="RenderingConstants.LinkRampMs" />,
    ///     so linked tails meet on the centre line.
    /// </summary>
    internal static float WobbleEnvelope(NoteFlags flags, double noteTime, double noteSustain, double absTime)
    {
        if ((flags & (NoteFlags.RampIn | NoteFlags.RampOut)) == 0) return 1f;
        var ramp = Math.Min(RenderingConstants.LinkRampMs, noteSustain / 2.0);
        if (ramp <= 0) return 1f;
        var env = 1.0;
        if ((flags & NoteFlags.RampIn) != 0) env *= Smooth((absTime - noteTime) / ramp);
        if ((flags & NoteFlags.RampOut) != 0) env *= Smooth((noteTime + noteSustain - absTime) / ramp);
        return (float)env;

        static double Smooth(double t)
        {
            t = Math.Clamp(t, 0.0, 1.0);
            return t * t * (3.0 - 2.0 * t);
        }
    }

    /// <summary>Stages the stretches of one tail that sit at <paramref name="slice" />'s
    /// key (all of it, unless a bend spreads it over several).</summary>
    private static void DrawTail(RenderContext context, in Tail tail, int lane, KeySlice slice)
    {
        if (tail.Fret <= context.Snapshot.Capo)
        {
            DrawOpenTail(context, tail, lane, slice);
            return;
        }

        var noteTime = tail.Time;
        var noteSustain = tail.Sustain;
        var stringIndex = tail.StringIndex;
        var flags = tail.Flags;
        var actualFret = tail.Fret;
        var hasTechnique =
            (flags & (NoteFlags.HasSlide | NoteFlags.Vibrato | NoteFlags.Tremolo | NoteFlags.HasBends)) != 0;
        var baseNy = FretboardConstants.BottomStringPosition + lane * FretboardConstants.StringDistance;
        var nc = FretboardConstants.StringColorFor(stringIndex, context.Snapshot.StringCount);
        if (NoteRenderMath.IsDeadNote(context, noteTime)) nc = DeadNoteMath.Dim(nc);
        var tailC = Color.FromArgb(RenderingConstants.BarAlpha, nc.R, nc.G, nc.B);
        var innerAlphaC = Color.FromArgb(RenderingConstants.TailInnerAlpha, tailC.R, tailC.G, tailC.B);
        var nX = NoteRenderMath.GetNoteX(context, noteTime, actualFret);
        var segments = hasTechnique
            ? RenderingConstants.TechniqueSegmentCount
            : RenderingConstants.BasicSegmentCount;
        var endX = (flags & NoteFlags.HasSlide) != 0 ? NoteRenderMath.GetNoteX(context, noteTime, tail.SlideTo) : nX;

        for (var s = 0; s < segments; s++)
        {
            var t1 = (float)s / segments;
            var t2 = (float)(s + 1) / segments;
            var absTime1 = noteTime + noteSustain * t1;
            var absTime2 = noteTime + noteSustain * t2;
            var absZ1 = context.GetZDepthForTime(absTime1, context.TimeMs);
            var absZ2 = context.GetZDepthForTime(absTime2, context.TimeMs);
            if (absZ1 > context.FadeEndZ || absZ2 <= 0f) continue;
            if (!slice.Holds(context, tail, lane, absTime1, absTime2, out var bend1, out var bend2)) continue;
            var z1 = Math.Max(0f, absZ1);
            var z2 = Math.Max(0f, absZ2);
            var cx1 = nX;
            var cx2 = endX;
            var cy1 = baseNy;
            var cy2 = baseNy;

            if ((flags & NoteFlags.HasSlide) != 0)
            {
                var ease1 = (flags & NoteFlags.UnpitchedSlide) != 0
                    ? 1f - (float)Math.Sin((1f - t1) * Math.PI / 2.0)
                    : (float)Math.Pow(Math.Sin(t1 * Math.PI / 2.0), RenderingConstants.SlideEasePower);
                var ease2 = (flags & NoteFlags.UnpitchedSlide) != 0
                    ? 1f - (float)Math.Sin((1f - t2) * Math.PI / 2.0)
                    : (float)Math.Pow(Math.Sin(t2 * Math.PI / 2.0), RenderingConstants.SlideEasePower);
                cx1 = nX + (endX - nX) * ease1;
                cx2 = nX + (endX - nX) * ease2;
            }

            var env1 = WobbleEnvelope(flags, noteTime, noteSustain, absTime1);
            var env2 = WobbleEnvelope(flags, noteTime, noteSustain, absTime2);
            if ((flags & NoteFlags.Vibrato) != 0)
            {
                cy1 += MathF.Sin((float)(absTime1 / RenderingConstants.MillisecondsToSeconds *
                                         RenderingConstants.VibratoFrequencyHz)) *
                       RenderingConstants.VibratoAmplitude * env1;
                cy2 += MathF.Sin((float)(absTime2 / RenderingConstants.MillisecondsToSeconds *
                                         RenderingConstants.VibratoFrequencyHz)) *
                       RenderingConstants.VibratoAmplitude * env2;
            }

            if (tail.Bends)
            {
                cy1 += bend1 * NoteRenderMath.BendMultiplier;
                cy2 += bend2 * NoteRenderMath.BendMultiplier;
            }

            if ((flags & NoteFlags.Tremolo) != 0)
            {
                cx1 += (s % 2 == 0 ? RenderingConstants.TremoloAmplitude : -RenderingConstants.TremoloAmplitude) * env1;
                cx2 += ((s + 1) % 2 == 0 ? RenderingConstants.TremoloAmplitude : -RenderingConstants.TremoloAmplitude) *
                       env2;
            }

            var tailX01 = cx1 - FretboardConstants.TailHalfWidth;
            var tailX11 = cx1 - FretboardConstants.TailHalfWidth * RenderingConstants.TailHalfWidthMultiplier;
            var tailX21 = cx1 + FretboardConstants.TailHalfWidth * RenderingConstants.TailHalfWidthMultiplier;
            var tailX31 = cx1 + FretboardConstants.TailHalfWidth;
            var tailX02 = cx2 - FretboardConstants.TailHalfWidth;
            var tailX12 = cx2 - FretboardConstants.TailHalfWidth * RenderingConstants.TailHalfWidthMultiplier;
            var tailX22 = cx2 + FretboardConstants.TailHalfWidth * RenderingConstants.TailHalfWidthMultiplier;
            var tailX32 = cx2 + FretboardConstants.TailHalfWidth;

            context.QuadBatcher.AddQuad(new Vector3(tailX01, cy1, z1), new Vector3(tailX11, cy1, z1),
                new Vector3(tailX12, cy2, z2), new Vector3(tailX02, cy2, z2), Vector3.UnitY, tailC);
            context.QuadBatcher.AddQuad(new Vector3(tailX11, cy1, z1), new Vector3(tailX21, cy1, z1),
                new Vector3(tailX22, cy2, z2), new Vector3(tailX12, cy2, z2), Vector3.UnitY, innerAlphaC);
            context.QuadBatcher.AddQuad(new Vector3(tailX21, cy1, z1), new Vector3(tailX31, cy1, z1),
                new Vector3(tailX32, cy2, z2), new Vector3(tailX22, cy2, z2), Vector3.UnitY, tailC);

            if ((flags & NoteFlags.Vibrato) != 0)
            {
                var shineC = Color.FromArgb(RenderingConstants.VibratoShineAlpha, 255, 255, 255);
                context.QuadBatcher.AddQuad(
                    new Vector3(cx1 - RenderingConstants.VibratoShineWidth, cy1,
                        z1 - RenderingConstants.VibratoShineZOffset),
                    new Vector3(cx1 + RenderingConstants.VibratoShineWidth, cy1,
                        z1 - RenderingConstants.VibratoShineZOffset),
                    new Vector3(cx2 + RenderingConstants.VibratoShineWidth, cy2,
                        z2 - RenderingConstants.VibratoShineZOffset),
                    new Vector3(cx2 - RenderingConstants.VibratoShineWidth, cy2,
                        z2 - RenderingConstants.VibratoShineZOffset),
                    Vector3.UnitY, shineC);
            }
        }

        if ((flags & NoteFlags.HasBends) != 0 && slice.IsLast)
            BendRenderer.RenderPeaks(context, tail.BendOffset, tail.BendCount, baseNy, nX);
    }

    private static void DrawOpenTail(RenderContext context, in Tail tail, int lane, KeySlice slice)
    {
        var noteTime = tail.Time;
        var noteSustain = tail.Sustain;
        var stringIndex = tail.StringIndex;
        var flags = tail.Flags;
        var slideTo = tail.SlideTo;
        var baseNy = FretboardConstants.BottomStringPosition + lane * FretboardConstants.StringDistance;
        var nc = FretboardConstants.StringColorFor(stringIndex, context.Snapshot.StringCount);
        if (NoteRenderMath.IsDeadNote(context, noteTime)) nc = DeadNoteMath.Dim(nc);
        var edgeC = Color.FromArgb(RenderingConstants.OpenTailEdgeAlpha, nc.R, nc.G, nc.B);
        var (spanX0, spanX1) = NoteRenderMath.GetAnchorSpan(context, noteTime);
        var spanCenter = (spanX0 + spanX1) / 2f;
        var spanWidth = Math.Max(0.01f, spanX1 - spanX0);

        var halfWidth = spanWidth / 2f;

        var hasTechnique =
            (flags & (NoteFlags.HasSlide | NoteFlags.Vibrato | NoteFlags.Tremolo | NoteFlags.HasBends)) != 0;
        var segments = hasTechnique
            ? RenderingConstants.TechniqueSegmentCount
            : RenderingConstants.BasicSegmentCount * 2;
        var slideEndX = (flags & NoteFlags.HasSlide) != 0 && slideTo > 0
            ? NoteRenderMath.GetNoteX(context, noteTime, slideTo)
            : spanCenter;

        for (var s = 0; s < segments; s++)
        {
            var t1 = (float)s / segments;
            var t2 = (float)(s + 1) / segments;
            var absTime1 = noteTime + noteSustain * t1;
            var absTime2 = noteTime + noteSustain * t2;
            var absZ1 = context.GetZDepthForTime(absTime1, context.TimeMs);
            var absZ2 = context.GetZDepthForTime(absTime2, context.TimeMs);
            if (absZ1 > context.FadeEndZ || absZ2 <= 0f) continue;
            if (!slice.Holds(context, tail, lane, absTime1, absTime2, out var bend1, out var bend2)) continue;
            var z1 = Math.Max(0f, absZ1);
            var z2 = Math.Max(0f, absZ2);
            var cx1 = spanCenter;
            var cx2 = slideEndX;
            var cy1 = baseNy;
            var cy2 = baseNy;

            if ((flags & NoteFlags.HasSlide) != 0)
            {
                var ease1 = (flags & NoteFlags.UnpitchedSlide) != 0
                    ? 1f - (float)Math.Sin((1f - t1) * Math.PI / 2.0)
                    : (float)Math.Pow(Math.Sin(t1 * Math.PI / 2.0), RenderingConstants.SlideEasePower);
                var ease2 = (flags & NoteFlags.UnpitchedSlide) != 0
                    ? 1f - (float)Math.Sin((1f - t2) * Math.PI / 2.0)
                    : (float)Math.Pow(Math.Sin(t2 * Math.PI / 2.0), RenderingConstants.SlideEasePower);
                cx1 = spanCenter + (slideEndX - spanCenter) * ease1;
                cx2 = spanCenter + (slideEndX - spanCenter) * ease2;
            }

            var env1 = WobbleEnvelope(flags, noteTime, noteSustain, absTime1);
            var env2 = WobbleEnvelope(flags, noteTime, noteSustain, absTime2);
            if ((flags & NoteFlags.Vibrato) != 0)
            {
                cy1 += MathF.Sin((float)(absTime1 / RenderingConstants.MillisecondsToSeconds *
                                         RenderingConstants.VibratoFrequencyHz)) *
                       RenderingConstants.VibratoAmplitude * env1;
                cy2 += MathF.Sin((float)(absTime2 / RenderingConstants.MillisecondsToSeconds *
                                         RenderingConstants.VibratoFrequencyHz)) *
                       RenderingConstants.VibratoAmplitude * env2;
            }

            if (tail.Bends)
            {
                cy1 += bend1 * NoteRenderMath.BendMultiplier;
                cy2 += bend2 * NoteRenderMath.BendMultiplier;
            }

            if ((flags & NoteFlags.Tremolo) != 0)
            {
                cx1 += (s % 2 == 0 ? RenderingConstants.TremoloAmplitude : -RenderingConstants.TremoloAmplitude) * env1;
                cx2 += ((s + 1) % 2 == 0 ? RenderingConstants.TremoloAmplitude : -RenderingConstants.TremoloAmplitude) *
                       env2;
            }

            DrawOpenTailRail(context, cx1 - halfWidth, cy1, z1, cx2 - halfWidth, cy2, z2, edgeC);
            DrawOpenTailRail(context, cx1 + halfWidth, cy1, z1, cx2 + halfWidth, cy2, z2, edgeC);
        }

        // The peak marker + step label, exactly as the fretted path draws them.
        // This call was missing here, so an open-string bend displaced its tail
        // (the loop above applies GetBendOffset per segment) while never saying
        // HOW FAR — and since the highway's open rule is `fret <= Capo`, every
        // bend at the capo's fret took this path and lost its label too.
        if ((flags & NoteFlags.HasBends) != 0 && slice.IsLast)
            BendRenderer.RenderPeaks(context, tail.BendOffset, tail.BendCount, baseNy, spanCenter);
    }

    private static void DrawOpenTailRail(RenderContext context, float x1, float y1, float z1, float x2, float y2,
        float z2, Color color)
    {
        var halfWidth = FretboardConstants.FretThickness * 1.5f;
        context.QuadBatcher.AddQuad(
            new Vector3(x1 - halfWidth, y1, z1), new Vector3(x1 + halfWidth, y1, z1),
            new Vector3(x2 + halfWidth, y2, z2), new Vector3(x2 - halfWidth, y2, z2),
            Vector3.UnitY, color);
    }

    /// <summary>One tail's inputs, the same for a single note and a chord note.</summary>
    private readonly record struct Tail(double Time, double Sustain, int StringIndex, NoteFlags Flags, int SlideTo,
        int BendOffset, int BendCount, int Fret, float BendCarry)
    {
        /// <summary>A bend, or a bend carried in by a link, moves this tail off its lane.</summary>
        public bool Bends => (Flags & NoteFlags.HasBends) != 0 || BendCarry != 0f;
    }

    /// <summary>The draw key one pass stages, inside the range one tail spans.</summary>
    private readonly record struct KeySlice(int Key, int Min, int Max)
    {
        /// <summary>The pass that stages the tail's highest stretch: it draws the bend
        /// peak markers, so they are staged once, over the tail.</summary>
        public bool IsLast => Key == Max;

        /// <summary>
        ///     Whether the segment from <paramref name="absTime1" /> to
        ///     <paramref name="absTime2" /> belongs to this pass, by the bend at its
        ///     middle; hands back the bend (in steps) at both ends so the caller lifts
        ///     the segment without computing it again. Every segment lands in exactly
        ///     one pass: its key is clamped into the range the passes walk.
        /// </summary>
        public bool Holds(RenderContext context, in Tail tail, int lane, double absTime1, double absTime2,
            out float bend1, out float bend2)
        {
            bend1 = 0f;
            bend2 = 0f;
            if (!tail.Bends) return true;
            bend1 = NoteRenderMath.GetBendOffset(context, tail.BendOffset, tail.BendCount, tail.Time, absTime1,
                tail.BendCarry);
            bend2 = NoteRenderMath.GetBendOffset(context, tail.BendOffset, tail.BendCount, tail.Time, absTime2,
                tail.BendCarry);
            return Min == Max || Math.Clamp(DrawKey(lane, (bend1 + bend2) * 0.5f), Min, Max) == Key;
        }
    }
}
