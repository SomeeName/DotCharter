using Avalonia;
using DotCharter.UI.Constants;

namespace DotCharter.UI.Input;

public static class ChartHitTester
{
    /// <summary>
    ///     Visible time window for the 2D timeline. <paramref name="viewAnchorMs" />
    ///     is the chart time at the fixed anchor column
    ///     (<see cref="ChartEditorConstants.PlayheadX" />) — callers MUST pass
    ///     <c>ProjectState.TimelineViewAnchorEffectiveMs</c> (the playhead while
    ///     Follow is on, the pan anchor while it is off) so hit-testing always
    ///     maps time-to-screen exactly like the renderer (0.16 6.6).
    /// </summary>
    public static ViewportBounds GetViewportBounds(double viewAnchorMs, double pixelsPerMs, double boundsWidth)
    {
        var viewStartMs = viewAnchorMs - ChartEditorConstants.PlayheadX / pixelsPerMs;
        return new ViewportBounds(viewStartMs, viewStartMs + boundsWidth / pixelsPerMs);
    }

    /// <summary>Lane-center Y for a string. <paramref name="leftyFlip" /> must
    /// carry the SAME flag the renderer draws with
    /// (<c>ChartRenderContext.LaneTopFor</c>) or clicks land on the wrong
    /// string — that is why it is not defaulted.</summary>
    public static double GetLaneY(double boundsHeight, double topMargin, double bottomMargin, int stringCount,
        int stringIndex, bool leftyFlip)
    {
        var laneAreaHeight = boundsHeight - topMargin - bottomMargin;
        var lHeight = laneAreaHeight / stringCount;
        var lane = leftyFlip ? stringCount - 1 - stringIndex : stringIndex;
        return topMargin + lane * lHeight + lHeight / 2;
    }

    // Shared screen-rect builders so point hit-testing (below) and the marquee
    // rect-intersection tester (MarqueeHitTester) agree on what a note/chord
    // occupies on screen.
    public static Rect NoteHeadRect(double startX, double laneY)
    {
        return new Rect(
            startX + ChartEditorConstants.NoteHeadOffsetX,
            laneY + ChartEditorConstants.NoteHeadOffsetY,
            ChartEditorConstants.NoteHeadWidth,
            ChartEditorConstants.NoteHeadHeight);
    }

    public static Rect ChordHeadRect(double startX, double laneY)
    {
        return new Rect(
            startX + ChartEditorConstants.ChordHeadOffsetX,
            laneY + ChartEditorConstants.ChordHeadOffsetY,
            ChartEditorConstants.ChordHeadWidth,
            ChartEditorConstants.ChordHeadHeight);
    }

    public static Rect SustainRect(double startX, double laneY, double width)
    {
        return new Rect(startX, laneY + ChartEditorConstants.SustainOffsetY, width,
            ChartEditorConstants.SustainHeight);
    }

    public static GuitarNote? HitTestNote(Point p, IList<GuitarNote>? notes, double viewAnchorMs, double pixelsPerMs,
        double boundsWidth, double boundsHeight, double topMargin, double bottomMargin, int stringCount,
        IList<BeatMarker> beats, bool leftyFlip)
        => HitTestNote(p, notes, viewAnchorMs, pixelsPerMs, boundsWidth, boundsHeight, topMargin, bottomMargin,
            stringCount, beats, leftyFlip, out _);

    /// <summary>As <see cref="HitTestNote(Point,IList{GuitarNote},double,double,double,double,double,double,int,IList{BeatMarker},bool)" />,
    /// reporting whether the pointer landed INSIDE the gem (or its sustain) or
    /// only within <see cref="ChartEditorConstants.NearMissHitTolerance" /> of
    /// it. A caller resolving two object classes at one point needs the
    /// difference: a near miss must never outrank an exact hit.</summary>
    public static GuitarNote? HitTestNote(Point p, IList<GuitarNote>? notes, double viewAnchorMs, double pixelsPerMs,
        double boundsWidth, double boundsHeight, double topMargin, double bottomMargin, int stringCount,
        IList<BeatMarker> beats, bool leftyFlip, out bool exact)
    {
        exact = false;
        if (notes == null || notes.Count == 0 || p.Y < topMargin || p.Y > boundsHeight - bottomMargin) return null;

        var bounds = GetViewportBounds(viewAnchorMs, pixelsPerMs, boundsWidth);

        TimelineMath.GetVisibleRange(notes, bounds.StartMs, bounds.EndMs, beats,
            ChartEditorConstants.VisibleRangeBufferMs, out var start, out var end);

        if (end < 0) return null;

        for (var i = end; i >= start; i--)
        {
            var note = notes[i];
            if (note.StringIndex < 0 || note.StringIndex >= stringCount) continue;

            var startX = (note.Position.GetPositionMs(beats) - bounds.StartMs) * pixelsPerMs;
            var y = GetLaneY(boundsHeight, topMargin, bottomMargin, stringCount, note.StringIndex, leftyFlip);

            if (NoteHeadRect(startX, y).Contains(p))
            {
                exact = true;
                return note;
            }

            if (note.Sustain.Fraction.Numerator > 0 || note.Sustain.BeatId > 0)
            {
                var width = (note.EndPosition.GetPositionMs(beats) - note.Position.GetPositionMs(beats)) * pixelsPerMs;
                if (SustainRect(startX, y, width).Contains(p))
                {
                    exact = true;
                    return note;
                }
            }
        }

        // NEAR MISS. Nothing was hit exactly, so take the closest head within
        // the tolerance rather than reporting empty highway — which arms the
        // marquee and clears the whole selection. See NearMissHitTolerance.
        GuitarNote? nearest = null;
        var nearestDistance = double.MaxValue;
        for (var i = end; i >= start; i--)
        {
            var note = notes[i];
            if (note.StringIndex < 0 || note.StringIndex >= stringCount) continue;

            var startX = (note.Position.GetPositionMs(beats) - bounds.StartMs) * pixelsPerMs;
            var y = GetLaneY(boundsHeight, topMargin, bottomMargin, stringCount, note.StringIndex, leftyFlip);
            var distance = DistanceToRect(p, NoteHeadRect(startX, y));
            if (distance > ChartEditorConstants.NearMissHitTolerance || distance >= nearestDistance) continue;

            nearest = note;
            nearestDistance = distance;
        }

        return nearest;
    }

    /// <summary>Distance from <paramref name="p" /> to the nearest point of
    /// <paramref name="rect" />; 0 when inside.</summary>
    internal static double DistanceToRect(Point p, Rect rect)
    {
        var dx = Math.Max(Math.Max(rect.X - p.X, 0), p.X - (rect.X + rect.Width));
        var dy = Math.Max(Math.Max(rect.Y - p.Y, 0), p.Y - (rect.Y + rect.Height));
        return Math.Sqrt(dx * dx + dy * dy);
    }

    public static Chord? HitTestChord(Point p, IList<Chord>? chords, double viewAnchorMs, double pixelsPerMs,
        double boundsWidth, double boundsHeight, double topMargin, double bottomMargin, int stringCount,
        IList<BeatMarker> beats, bool leftyFlip)
        => HitTestChord(p, chords, viewAnchorMs, pixelsPerMs, boundsWidth, boundsHeight, topMargin, bottomMargin,
            stringCount, beats, leftyFlip, out _);

    /// <summary>As <see cref="HitTestChord(Point,IList{Chord},double,double,double,double,double,double,int,IList{BeatMarker},bool)" />,
    /// reporting whether the hit was exact or a near miss. See the note
    /// overload — the chord tester runs FIRST at every press, so without this
    /// its 6 px near-miss ring beat an exact click on a single note beside the
    /// chord wherever the lanes are shorter than a gem (a six-string chart in a
    /// short stacked row: 26 px gems in ~20 px lanes overlap by design).</summary>
    public static Chord? HitTestChord(Point p, IList<Chord>? chords, double viewAnchorMs, double pixelsPerMs,
        double boundsWidth, double boundsHeight, double topMargin, double bottomMargin, int stringCount,
        IList<BeatMarker> beats, bool leftyFlip, out bool exact)
    {
        exact = false;
        if (chords == null || chords.Count == 0 || p.Y < topMargin || p.Y > boundsHeight - bottomMargin) return null;

        var bounds = GetViewportBounds(viewAnchorMs, pixelsPerMs, boundsWidth);

        TimelineMath.GetVisibleRange(chords, bounds.StartMs, bounds.EndMs, beats,
            ChartEditorConstants.VisibleRangeBufferMs, out var start, out var end);

        if (end < 0) return null;

        for (var i = end; i >= start; i--)
        {
            var chord = chords[i];
            var startX = (chord.Position.GetPositionMs(beats) - bounds.StartMs) * pixelsPerMs;

            if (p.X >= startX - ChartEditorConstants.ChordXHitTolerance &&
                p.X <= startX + ChartEditorConstants.ChordXHitTolerance)
            {
                for (var j = 0; j < chord.ChordNotes.Count; j++)
                {
                    var cn = chord.ChordNotes[j];
                    if (cn.StringIndex < 0 || cn.StringIndex >= stringCount) continue;

                    var y = GetLaneY(boundsHeight, topMargin, bottomMargin, stringCount, cn.StringIndex, leftyFlip);

                    if (ChordHeadRect(startX, y).Contains(p))
                    {
                        exact = true;
                        return chord;
                    }

                    if (cn.Sustain.Fraction.Numerator > 0 || cn.Sustain.BeatId > 0)
                    {
                        var width = ((chord.Position + cn.Sustain).GetPositionMs(beats) -
                                     chord.Position.GetPositionMs(beats)) * pixelsPerMs;
                        if (SustainRect(startX, y, width).Contains(p))
                        {
                            exact = true;
                            return chord;
                        }
                    }
                }
            }
        }

        // NEAR MISS — same rule as HitTestNote. A chord head is 24x16 and the
        // owner's log is full of grazing clicks that read as empty highway and
        // wiped the selection, then landed on the retry at the same position.
        Chord? nearest = null;
        var nearestDistance = double.MaxValue;
        for (var i = end; i >= start; i--)
        {
            var chord = chords[i];
            var startX = (chord.Position.GetPositionMs(beats) - bounds.StartMs) * pixelsPerMs;

            for (var j = 0; j < chord.ChordNotes.Count; j++)
            {
                var cn = chord.ChordNotes[j];
                if (cn.StringIndex < 0 || cn.StringIndex >= stringCount) continue;

                var y = GetLaneY(boundsHeight, topMargin, bottomMargin, stringCount, cn.StringIndex, leftyFlip);
                var distance = DistanceToRect(p, ChordHeadRect(startX, y));
                if (distance > ChartEditorConstants.NearMissHitTolerance || distance >= nearestDistance) continue;

                nearest = chord;
                nearestDistance = distance;
            }
        }

        return nearest;
    }

    public static HandShape? HitTestHandShape(Point p, IList<HandShape>? handShapes, double viewAnchorMs,
        double pixelsPerMs, double boundsWidth, double boundsHeight, double topMargin, double bottomMargin,
        IList<BeatMarker> beats)
    {
        if (handShapes == null || handShapes.Count == 0) return null;

        var laneAreaHeight = boundsHeight - topMargin - bottomMargin;
        var barY = topMargin + laneAreaHeight + ChartEditorConstants.RenderMetrics.HandShapeBarYOffset;

        if (p.Y < barY || p.Y > barY + ChartEditorConstants.RenderMetrics.HandShapeActiveBarHeight + 4) return null;

        var bounds = GetViewportBounds(viewAnchorMs, pixelsPerMs, boundsWidth);

        TimelineMath.GetVisibleRange(handShapes, bounds.StartMs, bounds.EndMs, beats,
            ChartEditorConstants.VisibleRangeBufferMs, out var start, out var end);
        if (end < 0) return null;
        start = Math.Max(0, start - 1); // Expand start backward to catch overlapping tails

        for (var i = end; i >= start; i--)
        {
            var hs = handShapes[i];
            var startX = (hs.Position.GetPositionMs(beats) - bounds.StartMs) * pixelsPerMs;
            var endX = (hs.EndPosition.GetPositionMs(beats) - bounds.StartMs) * pixelsPerMs;
            var w = Math.Max(endX - startX, ChartEditorConstants.HandShapeMinHitWidth);

            var rect = new Rect(startX, barY, w, ChartEditorConstants.RenderMetrics.HandShapeActiveBarHeight + 4);
            if (rect.Contains(p)) return hs;
        }

        return null;
    }

    public static FretAnchor? HitTestAnchor(Point p, IList<FretAnchor>? anchors, double viewAnchorMs,
        double pixelsPerMs, double boundsWidth, double fhpY, IList<BeatMarker> beats)
    {
        if (anchors == null || anchors.Count == 0) return null;
        if (p.Y < fhpY || p.Y > fhpY + ChartEditorConstants.RenderMetrics.AnchorTailHeight) return null;

        var bounds = GetViewportBounds(viewAnchorMs, pixelsPerMs, boundsWidth);
        TimelineMath.GetVisibleRange(anchors, bounds.StartMs, bounds.EndMs, beats,
            ChartEditorConstants.VisibleRangeBufferMs, out var start, out var end);
        if (end < 0) return null;
        start = Math.Max(0, start - 1); // Expand start backward to catch overlapping tails

        for (var i = end; i >= start; i--)
        {
            var a = anchors[i];
            var startX = (a.Position.GetPositionMs(beats) - bounds.StartMs) * pixelsPerMs;
            var endTimeMs = i + 1 < anchors.Count
                ? anchors[i + 1].Position.GetPositionMs(beats)
                : Math.Max(bounds.EndMs,
                    a.Position.GetPositionMs(beats) + ChartEditorConstants.RenderMetrics.AnchorTailMaxMs);
            var endX = (endTimeMs - bounds.StartMs) * pixelsPerMs;
            var w = Math.Max(endX - startX, 15f);

            var rect = new Rect(startX, fhpY, w, ChartEditorConstants.RenderMetrics.AnchorTailHeight);
            if (rect.Contains(p)) return a;
        }

        return null;
    }

    /// <summary>
    ///     Resolves the note and chord testers' two answers the way a press has
    ///     to: an EXACT hit outranks a NEAR MISS whichever class it is in.
    ///     <para>
    ///         Every caller runs the chord tester first and lets a chord win
    ///         unconditionally, which is right for two exact candidates (the
    ///         chord IS the sound at that column) and wrong the moment the
    ///         chord's answer came from the 6 px near-miss ring: a gem is 26 px
    ///         across while the lane pitch is the lane area over the string
    ///         count, so in any short viewport — a stacked track row, a
    ///         six-string chart in a small pane — the gems are taller than their
    ///         lanes and a neighbouring chord note is always inside the ring.
    ///         Clicking a single note dead centre then selected the chord above
    ///         it, and every verb (drag, delete, the context menu) followed the
    ///         wrong sound.
    ///     </para>
    ///     <para>
    ///         The near-miss pass keeps its whole purpose — deciding between two
    ///         INEXACT candidates — because that is the only case this changes.
    ///     </para>
    /// </summary>
    public static (Chord? Chord, GuitarNote? Note) ResolveSound(
        Chord? chord, bool chordExact, GuitarNote? note, bool noteExact)
        => chord != null && !chordExact && noteExact ? (null, note) : (chord, note);

    public static int GetLaneIndexAtY(double y, double boundsHeight, double topMargin, double bottomMargin,
        int stringCount, bool leftyFlip)
    {
        var laneAreaHeight = boundsHeight - topMargin - bottomMargin;
        var laneHeight = laneAreaHeight / stringCount;
        var lane = (int)((y - topMargin) / laneHeight);
        var stringClicked = leftyFlip ? stringCount - 1 - lane : lane;
        return Math.Max(0, Math.Min(stringCount - 1, stringClicked));
    }

    public readonly record struct ViewportBounds(double StartMs, double EndMs);
}
