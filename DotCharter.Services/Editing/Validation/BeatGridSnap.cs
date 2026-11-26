using DotCharter.Services.State;

namespace DotCharter.Services.Editing.Validation;

/// <summary>
///     The beat grid D40/D41 judge against, shared by <c>OffGridRule</c> and its snap. A position is ON the grid
///     when its fraction of the beat is a multiple of 1/16 (straight subdivisions: halves, quarters, eighths,
///     sixteenths of a beat) or of 1/24 (triplet subdivisions: thirds, sixths, twelfths, twenty-fourths of a
///     beat). Swing therefore counts as on the grid: a triplet-feel tab places its off-beats at 2/3 and its
///     swung sixteenths at 1/6 or 5/6, all multiples of 1/24. Quintuplets, septuplets and finer tuplets are not
///     grid lines.
///     <para>
///         How far a position is off is measured in milliseconds to the nearest grid line, through the beat
///         map, so a fast tempo and a slow one are judged alike. Under <see cref="MinOffMs" /> is the rounding
///         of a millisecond timestamp and counts as on the grid; up to <see cref="MaxSnapMs" /> is a slip the
///         snap repairs; beyond it the note is placed off the grid on purpose or wrongly, and only the charter
///         can tell which.
///     </para>
/// </summary>
public static class BeatGridSnap
{
    /// <summary>Below this a position counts as on the grid (timestamp rounding).</summary>
    public const double MinOffMs = 1.0;

    /// <summary>Up to this a position off the grid is snapped by the fix.</summary>
    public const double MaxSnapMs = 3.0;

    /// <summary>The straight and the triplet subdivisions of one beat.</summary>
    private static readonly int[] Subdivisions = [16, 24];

    /// <summary>Tolerance for the millisecond edges, so exactly 1 ms or 3 ms off is judged as written.</summary>
    private const double EdgeEpsilonMs = 1e-6;

    /// <summary>True when <paramref name="position" /> lies on a straight or triplet subdivision of its beat.</summary>
    public static bool IsOnGrid(FractionalPosition position) =>
        Subdivisions.Any(d => d % position.Fraction.Denominator == 0);

    /// <summary>
    ///     The nearest grid line to <paramref name="position" /> and how many milliseconds away it is, or null
    ///     when the position is on the grid or outside the beat map.
    /// </summary>
    public static (FractionalPosition Nearest, double OffMs)? Nearest(FractionalPosition position, IList<BeatMarker> beats)
    {
        if (beats.Count < 2 || position.BeatId < 0 || position.BeatId >= beats.Count - 1) return null;
        if (IsOnGrid(position)) return null;

        var timeMs = position.GetPositionMs(beats);
        (FractionalPosition Nearest, double OffMs)? best = null;
        foreach (var d in Subdivisions)
        {
            var below = (int)Math.Floor(position.Fraction.DoubleValue * d);
            foreach (var k in new[] { below, below + 1 })
            {
                var candidate = new FractionalPosition(position.BeatId, new Fraction(k, d));
                var off = Math.Abs(candidate.GetPositionMs(beats) - timeMs);
                if (best is not { } b || off < b.OffMs) best = (candidate, off);
            }
        }

        return best;
    }

    /// <summary>The finding a position earns: D40 when it is 1 to 3 ms off the grid, D41 beyond, null otherwise.</summary>
    public static ArrangementIssueType? Judge(double offMs) => offMs < MinOffMs - EdgeEpsilonMs ? null
        : offMs <= MaxSnapMs + EdgeEpsilonMs ? ArrangementIssueType.NoteNearlyOnGrid
        : ArrangementIssueType.NoteOffGrid;

    /// <summary>
    ///     One undo step that moves the note or chord of a D40 finding onto its nearest grid line, together with
    ///     the hand shapes and anchors that start with it; a sustain keeps its end. Null when the finding has no
    ///     unambiguous snap: a stale finding, a bend (its points are absolute), a sound that ends where this one
    ///     starts (a Link-Next or legato join), or a grid line that another sound already holds.
    /// </summary>
    public static IChartAction? Snap(ChartLevel level, ArrangementIssue issue, IList<BeatMarker> beats)
    {
        if (issue.Type != ArrangementIssueType.NoteNearlyOnGrid || Source(level, issue) is not { } from) return null;
        if (Nearest(from, beats) is not { } near || Judge(near.OffMs) != ArrangementIssueType.NoteNearlyOnGrid) return null;
        var to = near.Nearest;

        var notes = level.Notes.Where(n => n.Position == from).ToList();
        var chords = level.Chords.Where(c => c.Position == from).ToList();
        if (notes.Any(n => n.BendValues.Count > 0) || chords.Any(c => c.ChordNotes.Any(cn => cn.BendValues.Count > 0))) return null;
        if (level.Notes.Any(n => n.Position == to) || level.Chords.Any(c => c.Position == to)) return null;
        if (JoinedFromBefore(level, from)) return null;

        var actions = new List<IChartAction>();
        if (notes.Count > 0) actions.Add(new MutateNotesAction(level.Notes, notes, n => Move(n, to)));
        if (chords.Count > 0)
            actions.Add(new MutateChordsAction(level.Chords, chords, chords.Select(c => c.Clone()).ToList(), [.. chords.Select(c => Moved(c, to))]));

        var shapes = level.HandShapes.Where(h => h.Position == from).ToList();
        if (shapes.Count > 0)
        {
            actions.Add(new MutateHandShapesAction(level.HandShapes, shapes, shapes.Select(h => h.Clone()).ToList(),
                [.. shapes.Select(h => Moved(h, to))]));
        }

        var anchors = level.Anchors.Where(a => a.Position == from).ToList();
        if (anchors.Count > 0)
        {
            actions.Add(new MutateAnchorsAction(level.Anchors, anchors, anchors.Select(a => a.Clone()).ToList(),
                [.. anchors.Select(a => new FretAnchor(to, a.Fret, a.Width))]));
        }

        return actions.Count == 1 ? actions[0] : new CompositeAction(actions);
    }

    /// <summary>Where the finding's note or chord stands on the live level, or null when it is gone.</summary>
    private static FractionalPosition? Source(ChartLevel level, ArrangementIssue issue) =>
        issue.Note is { } note && level.Notes.Contains(note) ? note.Position
        : issue.Chord is { } chord && level.Chords.Contains(chord) ? chord.Position
        : null;

    /// <summary>A Link-Next or a sustain from an earlier sound reaches <paramref name="from" />: moving it would part the join.</summary>
    private static bool JoinedFromBefore(ChartLevel level, FractionalPosition from) =>
        level.Notes.Any(n => n.Position < from && (n.LinkNext ? n.EndPosition >= from : n.Sustain != FractionalPosition.Zero && n.EndPosition == from))
        || level.Chords.Any(c => c.Position < from && (c.HasLinkNext() ? c.EndPosition >= from : c.EndPosition == from));

    private static Chord Moved(Chord chord, FractionalPosition to)
    {
        var clone = chord.Clone();
        clone.Position = to;
        foreach (var chordNote in clone.ChordNotes)
        {
            if (chordNote.Sustain != FractionalPosition.Zero) chordNote.Sustain = chord.Position + chordNote.Sustain - to;
        }

        return clone;
    }

    private static HandShape Moved(HandShape shape, FractionalPosition to)
    {
        var clone = shape.Clone();
        clone.Position = to;
        return clone;
    }

    private static void Move(GuitarNote note, FractionalPosition to)
    {
        if (note.Sustain != FractionalPosition.Zero) note.Sustain = note.EndPosition - to;
        note.Position = to;
    }
}
