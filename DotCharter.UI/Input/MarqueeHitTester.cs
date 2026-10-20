using Avalonia;
using DotCharter.UI.Constants;

namespace DotCharter.UI.Input;

/// <summary>
///     Pure geometry for the 2D-timeline marquee (rubber-band) selection:
///     rect ↔ note/chord intersection over the visible lanes. Screen rects come
///     from the same <see cref="ChartHitTester" /> builders point hit-testing
///     uses, so a note the user can click is exactly a note the marquee can
///     catch. No Avalonia control state, no services — unit-tested in
///     MarqueeHitTesterTests.
/// </summary>
public static class MarqueeHitTester
{
    /// <summary>Pixel distance either axis must exceed before a press+move
    /// counts as a marquee drag rather than a click.</summary>
    public const double DragThresholdPx = 3.0;

    /// <summary>Normalized rect spanned by two drag corners (any order).</summary>
    public static Rect Normalize(Point a, Point b)
    {
        return new Rect(
            new Point(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y)),
            new Point(Math.Max(a.X, b.X), Math.Max(a.Y, b.Y)));
    }

    /// <summary>Clamps a marquee rect to the viewport so a drag that leaves the
    /// control never paints (or selects) outside it.</summary>
    public static Rect Clamp(Rect r, double viewportWidth, double viewportHeight)
    {
        var x0 = Math.Clamp(r.X, 0, Math.Max(0, viewportWidth));
        var y0 = Math.Clamp(r.Y, 0, Math.Max(0, viewportHeight));
        var x1 = Math.Clamp(r.Right, 0, Math.Max(0, viewportWidth));
        var y1 = Math.Clamp(r.Bottom, 0, Math.Max(0, viewportHeight));
        return new Rect(new Point(x0, y0), new Point(x1, y1));
    }

    public static bool IsDrag(Point origin, Point current)
    {
        return Math.Abs(current.X - origin.X) >= DragThresholdPx ||
               Math.Abs(current.Y - origin.Y) >= DragThresholdPx;
    }

    /// <summary>
    ///     Adds every note whose head or sustain rect intersects
    ///     <paramref name="marquee" /> (screen px) to <paramref name="results" />.
    /// </summary>
    public static void CollectNotes(Rect marquee, IList<GuitarNote>? notes, double viewAnchorMs,
        double pixelsPerMs, double boundsWidth, double boundsHeight, double topMargin, double bottomMargin,
        int stringCount, IList<BeatMarker> beats, ICollection<GuitarNote> results, bool leftyFlip)
    {
        if (notes == null || notes.Count == 0 || (marquee.Width <= 0 && marquee.Height <= 0)) return;

        var bounds = ChartHitTester.GetViewportBounds(viewAnchorMs, pixelsPerMs, boundsWidth);
        TimelineMath.GetVisibleRange(notes, bounds.StartMs, bounds.EndMs, beats,
            ChartEditorConstants.VisibleRangeBufferMs, out var start, out var end);
        if (end < 0) return;

        for (var i = start; i <= end; i++)
        {
            var note = notes[i];
            if (note.StringIndex < 0 || note.StringIndex >= stringCount) continue;

            var startX = (note.Position.GetPositionMs(beats) - bounds.StartMs) * pixelsPerMs;
            var y = ChartHitTester.GetLaneY(boundsHeight, topMargin, bottomMargin, stringCount, note.StringIndex,
                leftyFlip);

            if (marquee.Intersects(ChartHitTester.NoteHeadRect(startX, y)))
            {
                results.Add(note);
                continue;
            }

            if (note.Sustain.Fraction.Numerator > 0 || note.Sustain.BeatId > 0)
            {
                var width = (note.EndPosition.GetPositionMs(beats) - note.Position.GetPositionMs(beats)) * pixelsPerMs;
                if (marquee.Intersects(ChartHitTester.SustainRect(startX, y, width)))
                    results.Add(note);
            }
        }
    }

    /// <summary>
    ///     Adds every chord with at least one chord-note head or sustain rect
    ///     intersecting <paramref name="marquee" /> to <paramref name="results" />.
    /// </summary>
    public static void CollectChords(Rect marquee, IList<Chord>? chords, double viewAnchorMs,
        double pixelsPerMs, double boundsWidth, double boundsHeight, double topMargin, double bottomMargin,
        int stringCount, IList<BeatMarker> beats, ICollection<Chord> results, bool leftyFlip)
    {
        if (chords == null || chords.Count == 0 || (marquee.Width <= 0 && marquee.Height <= 0)) return;

        var bounds = ChartHitTester.GetViewportBounds(viewAnchorMs, pixelsPerMs, boundsWidth);
        TimelineMath.GetVisibleRange(chords, bounds.StartMs, bounds.EndMs, beats,
            ChartEditorConstants.VisibleRangeBufferMs, out var start, out var end);
        if (end < 0) return;

        for (var i = start; i <= end; i++)
        {
            var chord = chords[i];
            var startX = (chord.Position.GetPositionMs(beats) - bounds.StartMs) * pixelsPerMs;

            for (var j = 0; j < chord.ChordNotes.Count; j++)
            {
                var cn = chord.ChordNotes[j];
                if (cn.StringIndex < 0 || cn.StringIndex >= stringCount) continue;

                var y = ChartHitTester.GetLaneY(boundsHeight, topMargin, bottomMargin, stringCount, cn.StringIndex,
                    leftyFlip);

                var hit = marquee.Intersects(ChartHitTester.ChordHeadRect(startX, y));
                if (!hit && (cn.Sustain.Fraction.Numerator > 0 || cn.Sustain.BeatId > 0))
                {
                    var width = ((chord.Position + cn.Sustain).GetPositionMs(beats) -
                                 chord.Position.GetPositionMs(beats)) * pixelsPerMs;
                    hit = marquee.Intersects(ChartHitTester.SustainRect(startX, y, width));
                }

                if (hit)
                {
                    results.Add(chord);
                    break;
                }
            }
        }
    }
}
