using DotCharter.Services.State;

namespace DotCharter.Services.Editing.Validation;

/// <summary>What kind of boundary a sound or shape runs across.</summary>
public enum BoundaryKind
{
    Phrase,
    Section,

    /// <summary>The END phrase: nothing after it is played.</summary>
    End
}

/// <summary>One phrase start, section start or the END phrase.</summary>
public readonly record struct ChartBoundary(FractionalPosition Position, BoundaryKind Kind, string Name);

/// <summary>
///     The D17/D18 question, shared by <c>BoundaryCrossingRule</c> and its one-click trim, so the
///     finding and the fix cannot disagree: which phrase start, section start or END does a sustain, a
///     Link-Next chain or a hand shape run across?
///     <para>
///         A boundary lying STRICTLY inside the span is crossed; a sound or shape ending exactly on it is
///         not. A Link-Next sound is the exception: its chain continues into the next sound, so ending ON
///         the boundary still carries the chain across it. Phrases come from both stores
///         (<see cref="PhraseMarkers.All" />), sections from <see cref="EventPoint.Section" />. Where a
///         phrase and a section share a position the phrase names it, and END wins over both.
///     </para>
///     <para>
///         The trim is offered only where it is unambiguous: a plain sustain (no Link-Next, no slide, no
///         bend point at or past the boundary) is shortened to end on the first boundary it crosses; a hand
///         shape is shortened the same way when no note or chord of its level starts between that boundary
///         and the shape's end. Anything else (a chain, a slide, a shape whose content goes on past the
///         boundary) needs a charter's choice and gets the finding without a fix.
///     </para>
/// </summary>
public static class BoundaryCrossing
{
    /// <summary>Every boundary of <paramref name="arrangement" />, ordered by position, one per position.</summary>
    public static IReadOnlyList<ChartBoundary> Of(Arrangement arrangement)
    {
        var byPosition = new SortedDictionary<FractionalPosition, ChartBoundary>();
        foreach (var point in arrangement.EventPoints)
        {
            if (string.IsNullOrWhiteSpace(point.Section)) continue;
            byPosition.TryAdd(point.Position, new ChartBoundary(point.Position, BoundaryKind.Section, point.Section.Trim()));
        }

        foreach (var phrase in PhraseMarkers.All(arrangement))
        {
            var kind = phrase.Name.Equals("END", StringComparison.OrdinalIgnoreCase) ? BoundaryKind.End : BoundaryKind.Phrase;
            byPosition[phrase.Position] = new ChartBoundary(phrase.Position, kind, phrase.Name);
        }

        return [.. byPosition.Values];
    }

    /// <summary>
    ///     The first boundary inside <c>(start, end)</c>, or inside <c>(start, end]</c> when
    ///     <paramref name="linked" />; an END anywhere in that window is preferred, since running past
    ///     END is the louder fault.
    /// </summary>
    public static ChartBoundary? Crossed(IReadOnlyList<ChartBoundary> boundaries, FractionalPosition start,
        FractionalPosition end, bool linked)
    {
        ChartBoundary? first = null;
        foreach (var boundary in boundaries)
        {
            if (boundary.Position <= start) continue;
            if (boundary.Position > end || (!linked && boundary.Position == end)) break;
            if (boundary.Kind == BoundaryKind.End) return boundary;
            first ??= boundary;
        }

        return first;
    }

    /// <summary>The first boundary strictly inside <c>(start, end)</c>: where the trim ends the span.</summary>
    private static ChartBoundary? FirstInside(IReadOnlyList<ChartBoundary> boundaries, FractionalPosition start,
        FractionalPosition end) =>
        boundaries.Select(b => (ChartBoundary?)b).FirstOrDefault(b => b!.Value.Position > start && b.Value.Position < end);

    /// <summary>
    ///     One undo step that trims the note, chord or hand shape of a D17/D18 finding to the first
    ///     boundary it crosses, or null when the finding has no unambiguous trim (see the class remarks).
    ///     Computed on the live arrangement, so a finding gone stale since validation gets null.
    /// </summary>
    public static IChartAction? Trim(Arrangement arrangement, ChartLevel level, ArrangementIssue issue)
    {
        if (issue.Type is not (ArrangementIssueType.SustainCrossesBoundary or ArrangementIssueType.HandShapeCrossesBoundary))
            return null;

        var boundaries = Of(arrangement);
        if (issue.Note is { } note && level.Notes.Contains(note))
        {
            if (note.LinkNext || note.SlideTo.HasValue || note.UnpitchedSlide) return null;
            if (FirstInside(boundaries, note.Position, note.EndPosition) is not { } at) return null;
            if (note.BendValues.Any(b => b.Position >= at.Position)) return null;
            return new MutateNotesAction(level.Notes, [note], n => n.Sustain = at.Position - n.Position);
        }

        if (issue.Chord is { } chord && level.Chords.Contains(chord))
        {
            if (chord.ChordNotes.Any(cn => cn.LinkNext || cn.SlideTo.HasValue || cn.UnpitchedSlide)) return null;
            if (FirstInside(boundaries, chord.Position, chord.EndPosition) is not { } at) return null;
            if (chord.ChordNotes.Any(cn => cn.BendValues.Any(b => b.Position >= at.Position))) return null;
            var trimmed = chord.Clone();
            var length = at.Position - chord.Position;
            foreach (var chordNote in trimmed.ChordNotes)
                chordNote.Sustain = FractionalPosition.Min(chordNote.Sustain, length);
            return new MutateChordsAction(level.Chords, [chord], [chord.Clone()], [trimmed]);
        }

        if (issue.HandShape is { } shape && level.HandShapes.Contains(shape))
        {
            if (FirstInside(boundaries, shape.Position, shape.EndPosition) is not { } at) return null;
            if (level.Notes.Any(n => n.Position >= at.Position && n.Position < shape.EndPosition)
                || level.Chords.Any(c => c.Position >= at.Position && c.Position < shape.EndPosition))
            {
                return null;
            }

            var trimmed = shape.Clone();
            trimmed.EndPosition = at.Position;
            return new MutateHandShapesAction(level.HandShapes, [shape], [shape.Clone()], [trimmed]);
        }

        return null;
    }
}
