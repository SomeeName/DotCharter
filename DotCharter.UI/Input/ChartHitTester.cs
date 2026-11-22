using Avalonia;
using DotCharter.Core.Graphics;
using DotCharter.UI.Constants;
using DotCharter.UI.Controls.Layers;
using DotCharter.UI.Controls.Layers.Helpers;

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
    /// is the legacy parameter name for the timeline's InvertStrings value,
    /// independent of profile handedness. It must carry the SAME flag the renderer draws with
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
        => HitTestNote(p, notes, viewAnchorMs, pixelsPerMs, boundsWidth, boundsHeight, topMargin, bottomMargin,
            stringCount, beats, leftyFlip, out exact, out _);

    /// <summary>Reports head hits separately so every head outranks an overlapping sustain.</summary>
    public static GuitarNote? HitTestNote(Point p, IList<GuitarNote>? notes, double viewAnchorMs, double pixelsPerMs,
        double boundsWidth, double boundsHeight, double topMargin, double bottomMargin, int stringCount,
        IList<BeatMarker> beats, bool leftyFlip, out bool exact, out bool head)
    {
        exact = false;
        head = false;
        if (notes == null || notes.Count == 0 || p.Y < topMargin || p.Y > boundsHeight - bottomMargin) return null;

        var bounds = GetViewportBounds(viewAnchorMs, pixelsPerMs, boundsWidth);

        TimelineMath.GetVisibleRange(notes, bounds.StartMs, bounds.EndMs, beats,
            ChartEditorConstants.VisibleRangeBufferMs, out var start, out var end);

        if (end < 0) return null;

        GuitarNote? tail = null;
        for (var i = end; i >= start; i--)
        {
            var note = notes[i];
            if (note.StringIndex < 0 || note.StringIndex >= stringCount) continue;

            var startX = (note.Position.GetPositionMs(beats) - bounds.StartMs) * pixelsPerMs;
            var y = GetLaneY(boundsHeight, topMargin, bottomMargin, stringCount, note.StringIndex, leftyFlip);

            if (NoteHeadRect(startX, y).Contains(p))
            {
                exact = true;
                head = true;
                return note;
            }

            if (note.Sustain.Fraction.Numerator > 0 || note.Sustain.BeatId > 0)
            {
                var width = (note.EndPosition.GetPositionMs(beats) - note.Position.GetPositionMs(beats)) * pixelsPerMs;
                if (SustainRect(startX, y, width).Contains(p))
                    tail ??= note;
            }
        }

        if (tail != null)
        {
            exact = true;
            return tail;
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
        => HitTestChord(p, chords, viewAnchorMs, pixelsPerMs, boundsWidth, boundsHeight, topMargin, bottomMargin,
            stringCount, beats, leftyFlip, out exact, out _);

    /// <summary>Reports head hits separately so every head outranks an overlapping sustain.</summary>
    public static Chord? HitTestChord(Point p, IList<Chord>? chords, double viewAnchorMs, double pixelsPerMs,
        double boundsWidth, double boundsHeight, double topMargin, double bottomMargin, int stringCount,
        IList<BeatMarker> beats, bool leftyFlip, out bool exact, out bool head)
    {
        exact = false;
        head = false;
        if (chords == null || chords.Count == 0 || p.Y < topMargin || p.Y > boundsHeight - bottomMargin) return null;

        var bounds = GetViewportBounds(viewAnchorMs, pixelsPerMs, boundsWidth);

        TimelineMath.GetVisibleRange(chords, bounds.StartMs, bounds.EndMs, beats,
            ChartEditorConstants.VisibleRangeBufferMs, out var start, out var end);

        if (end < 0) return null;

        Chord? tail = null;
        for (var i = end; i >= start; i--)
        {
            var chord = chords[i];
            var startX = (chord.Position.GetPositionMs(beats) - bounds.StartMs) * pixelsPerMs;

            // The X tolerance gates the HEADS only. A sustain tail runs far past
            // it, and nesting the tail test inside it made every chord tail
            // unclickable beyond the first 16 px.
            var nearHead = p.X >= startX - ChartEditorConstants.ChordXHitTolerance &&
                           p.X <= startX + ChartEditorConstants.ChordXHitTolerance;
            for (var j = 0; j < chord.ChordNotes.Count; j++)
            {
                var cn = chord.ChordNotes[j];
                if (cn.StringIndex < 0 || cn.StringIndex >= stringCount) continue;

                var y = GetLaneY(boundsHeight, topMargin, bottomMargin, stringCount, cn.StringIndex, leftyFlip);

                if (nearHead && ChordHeadRect(startX, y).Contains(p))
                {
                    exact = true;
                    head = true;
                    return chord;
                }

                if (cn.Sustain.Fraction.Numerator > 0 || cn.Sustain.BeatId > 0)
                {
                    var width = ((chord.Position + cn.Sustain).GetPositionMs(beats) -
                                 chord.Position.GetPositionMs(beats)) * pixelsPerMs;
                    if (SustainRect(startX, y, width).Contains(p))
                        tail ??= chord;
                }
            }
        }

        if (tail != null)
        {
            exact = true;
            return tail;
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

    /// <summary>
    ///     Finds a drawn fret badge or an unlabelled row's marker. Tails remain selectable through
    ///     <see cref="HitTestAnchor" />; a null text measurement means labels are hidden.
    /// </summary>
    public static FretAnchor? HitTestAnchorHead(Point p, IList<FretAnchor>? anchors, double viewAnchorMs,
        double pixelsPerMs, double boundsWidth, double fhpY, IList<BeatMarker> beats,
        Func<string, float?> measureTextWidth, double currentTimeMs = 0)
    {
        if (anchors == null || anchors.Count == 0) return null;
        if (p.Y < fhpY || p.Y > fhpY + ChartEditorConstants.RenderMetrics.AnchorTailHeight) return null;

        var bounds = GetViewportBounds(viewAnchorMs, pixelsPerMs, boundsWidth);
        TimelineMath.GetVisibleRange(anchors, bounds.StartMs, bounds.EndMs, beats,
            ChartEditorConstants.VisibleRangeBufferMs, out var start, out var end);
        start = Math.Max(0, start - 1);
        for (var i = end; i >= start; i--)
        {
            var anchor = anchors[i];
            var textWidth = measureTextWidth(RenderStringCache.Fret(anchor.Fret));
            var timeMs = anchor.Position.GetPositionMs(beats);
            var endMs = AnchorSpan.EndTimeMs(timeMs,
                i + 1 < anchors.Count ? anchors[i + 1].Position.GetPositionMs(beats) : null, currentTimeMs);
            var x = (float)((timeMs - bounds.StartMs) * pixelsPerMs);
            var endX = (float)((endMs - bounds.StartMs) * pixelsPerMs);
            var head = FretAnchorLayer.GetHeadBounds(x, endX, (float)fhpY, textWidth);
            if (head.Contains(p)) return anchor;
        }

        return null;
    }

    public static FretAnchor? HitTestAnchor(Point p, IList<FretAnchor>? anchors, double viewAnchorMs,
        double pixelsPerMs, double boundsWidth, double fhpY, IList<BeatMarker> beats, double currentTimeMs = 0)
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
            var endTimeMs = AnchorSpan.EndTimeMs(a.Position.GetPositionMs(beats),
                i + 1 < anchors.Count ? anchors[i + 1].Position.GetPositionMs(beats) : null, currentTimeMs);
            var endX = (endTimeMs - bounds.StartMs) * pixelsPerMs;
            var w = Math.Max(endX - startX, FretAnchorLayer.MinHitWidth);

            var rect = new Rect(startX, fhpY, w, ChartEditorConstants.RenderMetrics.AnchorTailHeight);
            if (rect.Contains(p)) return a;
        }

        return null;
    }

    /// <summary>
    ///     Resolves the two sound classes for both press and hover: heads beat
    ///     sustains, and exact hits beat near misses. Chords retain precedence
    ///     between equally ranked candidates.
    /// </summary>
    public static (Chord? Chord, GuitarNote? Note) ResolveSound(
        Chord? chord, bool chordExact, GuitarNote? note, bool noteExact, bool chordHead = false, bool noteHead = false)
    {
        if (noteHead && !chordHead) return (null, note);
        if (chordHead && !noteHead) return (chord, null);
        return chord != null && !chordExact && noteExact ? (null, note) : (chord, note);
    }

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
