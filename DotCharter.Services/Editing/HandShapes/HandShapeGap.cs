using DotCharter.Core.Linking;

namespace DotCharter.Services.Editing.HandShapes;

/// <summary>
///     THE gap a hand shape leaves before the next attacked note or chord, shared by the D15
///     check (<c>HandShapeGapRule</c>) and Tools ▸ Improve Chart's hand-shape lengths, so the pass
///     never produces a shape the check reports. Gaps are measured in grid beats; a minimum note
///     distance set in milliseconds is converted at the shape start, so it depends on tempo.
///     <para>
///         A shape TOUCHES the next attack when it ends on it or closer to it than the app's
///         minimum note distance (<see cref="SpacingGap" />, 1/64 of a measure by default: 1/16
///         beat in 4/4). That is the gap <c>ChartSpacingEnforcer</c> parks every shape at before
///         the next sound, so a shape the clean-up spaced is never reported (a fixed 1/8 beat
///         flagged every one of them). On the finished-chart corpus almost every gap shorter
///         than 1/8 beat is zero. A held chord gets no larger gap.
///     </para>
///     <para>
///         An ATTACK is a note or chord the player strikes. A note that only continues a linked
///         note on its string (the previous event on that string has <c>LinkNext</c>) is not one; a
///         chord is one as long as one of its strings is struck anew. Built once per level; it reads
///         note and chord positions only, so changing a shape's end never invalidates it.
///     </para>
/// </summary>
public sealed class HandShapeGap
{
    private readonly FractionalPosition[] _attacks;
    private readonly FractionalPosition[] _chordPositions;
    private readonly IList<BeatMarker> _beats;
    private readonly SpacingGap _spacing;

    private HandShapeGap(FractionalPosition[] attacks, Chord[] chords, IList<BeatMarker> beats, SpacingGap spacing)
    {
        _attacks = attacks;
        _chordPositions = [.. chords.Select(c => c.Position)];
        _beats = beats;
        _spacing = spacing;
    }

    /// <summary>The gaps of <paramref name="level" /> under the minimum note distance
    /// <paramref name="spacing" /> (the app's setting, <c>ChartSpacingEnforcer.SpacingGapOf</c>).</summary>
    public static HandShapeGap For(ChartLevel level, IList<BeatMarker> beats, SpacingGap spacing)
    {
        var continuations = Continuations(level);
        var attacks = new List<FractionalPosition>(level.Notes.Count + level.Chords.Count);
        foreach (var note in level.Notes)
        {
            if (!continuations.Contains(note)) attacks.Add(note.Position);
        }

        foreach (var chord in level.Chords)
        {
            if (chord.ChordNotes.Count == 0 || chord.ChordNotes.Any(n => !continuations.Contains(n)))
                attacks.Add(chord.Position);
        }

        attacks.Sort();
        return new HandShapeGap([.. attacks], [.. level.Chords.OrderBy(c => c.Position)], beats, spacing);
    }

    /// <summary>The first attack at or after <paramref name="position" />, or null.</summary>
    public FractionalPosition? NextAttackFrom(FractionalPosition position)
    {
        var at = LowerBound(_attacks, position);
        return at < _attacks.Length ? _attacks[at] : null;
    }

    /// <summary>
    ///     The gap <paramref name="shape" /> must leave before the next attack: the minimum note
    ///     distance at the shape's start (where <c>ChartSpacingEnforcer</c> measures it).
    /// </summary>
    public FractionalPosition RequiredGap(HandShape shape) => _spacing.At(shape.Position, _beats);

    /// <summary>Whether the next attack at or after the shape's end comes on it or before the gap is over.</summary>
    public bool IsTooClose(HandShape shape) =>
        shape.EndPosition > shape.Position && NextAttackFrom(shape.EndPosition) is { } next &&
        (next == shape.EndPosition || next < shape.EndPosition + RequiredGap(shape));

    /// <summary>
    ///     Shortens the shape until it leaves the gap, never past its last chord or its start (a
    ///     shape that cannot leave the gap keeps its end). Repeats because a shorter shape can
    ///     expose an attack it covered before. Returns whether the end moved.
    /// </summary>
    public bool TrimToGap(HandShape shape)
    {
        var moved = false;
        while (IsTooClose(shape))
        {
            var latest = NextAttackFrom(shape.EndPosition)!.Value - RequiredGap(shape);
            var lastChord = LowerBound(_chordPositions, shape.EndPosition) - 1;
            var floor = lastChord >= 0 && _chordPositions[lastChord] >= shape.Position
                ? _chordPositions[lastChord]
                : shape.Position;
            if (latest <= floor || latest >= shape.EndPosition) break;
            shape.EndPosition = latest;
            moved = true;
        }

        return moved;
    }

    /// <summary>Every note and chord note that only continues the linked event before it on its string.</summary>
    private static HashSet<object> Continuations(ChartLevel level)
    {
        var events = new List<(int String, FractionalPosition Position, bool LinkNext, object Owner)>();
        foreach (var note in level.Notes) events.Add((note.StringIndex, note.Position, note.LinkNext, note));
        foreach (var chord in level.Chords)
        {
            foreach (var n in chord.ChordNotes) events.Add((n.StringIndex, chord.Position, n.LinkNext, n));
        }

        events.Sort((a, b) => a.String != b.String ? a.String.CompareTo(b.String) : a.Position.CompareTo(b.Position));
        var continuations = new HashSet<object>(ReferenceEqualityComparer.Instance);
        for (var i = 1; i < events.Count; i++)
        {
            if (events[i - 1].String == events[i].String && events[i - 1].LinkNext) continuations.Add(events[i].Owner);
        }

        return continuations;
    }

    /// <summary>First index whose value is at or after <paramref name="value" />.</summary>
    private static int LowerBound(FractionalPosition[] sorted, FractionalPosition value)
    {
        int lo = 0, hi = sorted.Length;
        while (lo < hi)
        {
            var mid = (lo + hi) >> 1;
            if (sorted[mid].CompareTo(value) < 0) lo = mid + 1;
            else hi = mid;
        }

        return lo;
    }
}
