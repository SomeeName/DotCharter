using DotCharter.Core.Linking;
using DotCharter.Services.Editing.HandShapes;
using DotCharter.Services.State;

namespace DotCharter.Services.Editing.Validation;

/// <summary>
///     The spacing questions shared by <c>SustainSpacingRule</c> (D90/D91), <c>HandShapeGapRule</c> (D15)
///     and their one-click fixes, so a finding and its fix cannot disagree.
///     <para>
///         The NEXT SOUND after a note or chord is the next position, on any string, where a note or
///         chord of the same level starts: the one <c>ChartSpacingEnforcer</c> spaces sustains against.
///         The gap is a 32nd note (<see cref="SpacingGap.ThirtySecondNote" />). A sound that starts less
///         than one gap before the next sound has no room for a sustain that leaves it (a 32nd-note
///         strum keeps its short strike sustain), so it is never reported.
///     </para>
///     <para>
///         Every fix is one undo step, computed on the live level, and offered only where it is
///         unambiguous: a slide, an unpitched slide or a bend point past the new end needs a charter's
///         choice, and a hand shape that cannot end before its last chord keeps its end.
///     </para>
/// </summary>
public static class ChartSpacingFix
{
    /// <summary>A sustain under this many milliseconds is a leftover (D91).</summary>
    public const double MinSustainMs = 5.0;

    /// <summary>Every distinct note and chord position of <paramref name="level" />, sorted.</summary>
    internal static FractionalPosition[] SoundPositions(ChartLevel level) =>
        [.. level.Notes.Select(n => n.Position).Concat(level.Chords.Select(c => c.Position)).Distinct().Order()];

    /// <summary>The first position in <paramref name="sorted" /> after <paramref name="position" />, or null.</summary>
    internal static FractionalPosition? After(FractionalPosition[] sorted, FractionalPosition position)
    {
        int lo = 0, hi = sorted.Length;
        while (lo < hi)
        {
            var mid = (lo + hi) >> 1;
            if (sorted[mid] <= position) lo = mid + 1;
            else hi = mid;
        }

        return lo < sorted.Length ? sorted[lo] : null;
    }

    /// <summary>
    ///     The longest sustain a sound at <paramref name="position" /> may keep and still leave a 32nd
    ///     note before <paramref name="next" />, or null when there is no room for any.
    /// </summary>
    internal static FractionalPosition? LongestSustain(FractionalPosition position, FractionalPosition next,
        IList<BeatMarker> beats)
    {
        var longest = next - position - SpacingGap.ThirtySecondNote(position, beats);
        return longest > FractionalPosition.Zero ? longest : null;
    }

    /// <summary>Whether a sustain shorter than <see cref="MinSustainMs" /> (but not zero).</summary>
    internal static bool IsUnderFiveMs(FractionalPosition position, FractionalPosition sustain, IList<BeatMarker> beats) =>
        sustain > FractionalPosition.Zero &&
        (position + sustain).GetPositionMs(beats) - position.GetPositionMs(beats) < MinSustainMs;

    /// <summary>
    ///     One undo step that fixes a D15, D90 or D91 finding on <paramref name="level" />, or null when
    ///     it has none (see the class remarks). A finding gone stale since validation gets null.
    /// </summary>
    public static IChartAction? For(ChartLevel level, ArrangementIssue issue, IList<BeatMarker> beats) => issue.Type switch
    {
        ArrangementIssueType.SustainTooCloseToNext => TrimSustain(level, issue, beats),
        ArrangementIssueType.SustainUnderFiveMs => ClearSustain(level, issue, beats),
        ArrangementIssueType.HandShapeGap => TrimShape(level, issue, beats),
        _ => null
    };

    private static IChartAction? TrimSustain(ChartLevel level, ArrangementIssue issue, IList<BeatMarker> beats)
    {
        var positions = SoundPositions(level);
        if (issue.Note is { } note && level.Notes.Contains(note))
        {
            if (TrimmedNote(positions, note, beats) is not { } length) return null;
            return new MutateNotesAction(level.Notes, [note], n => n.Sustain = length);
        }

        if (issue.Chord is not { } chord || !level.Chords.Contains(chord)) return null;
        if (TrimmedChord(positions, chord, beats) is not { } chordLength) return null;
        var trimmed = chord.Clone();
        Trim(trimmed, chordLength);
        return new MutateChordsAction(level.Chords, [chord], [chord.Clone()], [trimmed]);
    }

    /// <summary>
    ///     Trims, in place, every sustain on <paramref name="level" /> that D90 would report and its fix
    ///     would trim, to the same length: a 32nd note before the next sound. Only shortens; a slide, a
    ///     Link-Next note, a bend past the new end, a chord that lets other notes pass and a sustain
    ///     under 5 ms (D91's) keep theirs. The Guitar Pro import's last pass, so a fresh import opens
    ///     without a D90 per sustained note.
    /// </summary>
    /// <returns>The notes and chords trimmed.</returns>
    public static int TrimSustainsToGap(ChartLevel level, IList<BeatMarker> beats)
    {
        var positions = SoundPositions(level);
        var trimmed = 0;
        foreach (var note in level.Notes)
        {
            if (IsUnderFiveMs(note.Position, note.Sustain, beats) || TrimmedNote(positions, note, beats) is not { } length)
                continue;
            note.Sustain = length;
            trimmed++;
        }

        foreach (var chord in level.Chords)
        {
            if (chord.PassOtherNotes || TrimmedChord(positions, chord, beats) is not { } length) continue;
            Trim(chord, length);
            trimmed++;
        }

        return trimmed;
    }

    private static FractionalPosition? TrimmedNote(FractionalPosition[] positions, GuitarNote note, IList<BeatMarker> beats) =>
        Trimmed(positions, note.Position, note.Sustain, beats) is { } length && CanEnd(note, note.Position + length)
            ? length
            : null;

    private static FractionalPosition? TrimmedChord(FractionalPosition[] positions, Chord chord, IList<BeatMarker> beats) =>
        Trimmed(positions, chord.Position, chord.EndPosition - chord.Position, beats) is { } length &&
        chord.ChordNotes.All(cn => CanEnd(cn, chord.Position + length))
            ? length
            : null;

    private static void Trim(Chord chord, FractionalPosition length)
    {
        foreach (var chordNote in chord.ChordNotes)
            chordNote.Sustain = FractionalPosition.Min(chordNote.Sustain, length);
    }

    /// <summary>The sustain to trim to, or null when the sound is not too close (any more) or has no room.</summary>
    private static FractionalPosition? Trimmed(FractionalPosition[] positions, FractionalPosition position,
        FractionalPosition sustain, IList<BeatMarker> beats) =>
        After(positions, position) is { } next && LongestSustain(position, next, beats) is { } longest && sustain > longest
            ? longest
            : null;

    private static bool CanEnd(GuitarNote note, FractionalPosition end) =>
        !note.LinkNext && !note.SlideTo.HasValue && !note.UnpitchedSlide && note.BendValues.All(b => b.Position < end);

    private static bool CanEnd(ChordNote note, FractionalPosition end) =>
        !note.LinkNext && !note.SlideTo.HasValue && !note.UnpitchedSlide && note.BendValues.All(b => b.Position < end);

    private static IChartAction? ClearSustain(ChartLevel level, ArrangementIssue issue, IList<BeatMarker> beats)
    {
        if (issue.Note is not { } note || !level.Notes.Contains(note) || !IsUnderFiveMs(note.Position, note.Sustain, beats))
            return null;
        if (note.LinkNext || note.SlideTo.HasValue || note.UnpitchedSlide || note.BendValues.Count > 0) return null;
        return new MutateNotesAction(level.Notes, [note], n => n.Sustain = FractionalPosition.Zero);
    }

    private static IChartAction? TrimShape(ChartLevel level, ArrangementIssue issue, IList<BeatMarker> beats)
    {
        if (issue.HandShape is not { } shape || !level.HandShapes.Contains(shape)) return null;
        var trimmed = shape.Clone();
        if (!HandShapeGap.For(level, beats, SpacingGap.AppDefault).TrimToGap(trimmed)) return null;
        return new MutateHandShapesAction(level.HandShapes, [shape], [shape.Clone()], [trimmed]);
    }
}
