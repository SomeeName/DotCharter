namespace DotCharter.Services.Editing.Anchors;

/// <summary>
///     What fret a NEW hand position should start on, derived from the notes it
///     will actually govern.
///     <para>
///         <b>The old default was "whatever the previous anchor said", and it was
///         wrong essentially every time.</b> In one owner session all
///         six anchors created by right-click were re-opened and corrected within
///         3.4 seconds, five of them on the fret — the dialog round trip was
///         effectively part of the create gesture. Copying the previous fret is a
///         guess about the PAST; the notes under the new anchor are a fact about
///         the present.
///     </para>
///     <para>
///         Deliberately NOT the group-descend machinery in
///         <c>FretAnchorGenerationService</c>. That answers a different question —
///         "where should every anchor in this arrangement go" — and it is measured
///         against the EOF corpus as a whole-chart generator. Here the charter has
///         already decided WHERE the hand moves and is only being offered a
///         starting fret they can override in the same gesture, so the cheap,
///         explainable rule is the right one: the lowest fretted note the anchor
///         governs is the fret the index finger has to reach.
///     </para>
/// </summary>
public static class AnchorFretSuggester
{
    /// <summary>Fallback when the span contains nothing fretted.</summary>
    public const int DefaultFret = 1;

    /// <summary>The last fret an anchor may open on — the same ceiling
    /// <c>HighwayEditingService.MaxAnchorFret</c> and
    /// <c>ChartImprovementService.MaxFret</c> use.</summary>
    private const int MaxFret = 24;

    /// <summary>
    ///     The lowest fret an FHP anchor may open on: <c>capo + 1</c>, clamped
    ///     into the neck.
    ///     <para>
    ///         An anchor on or below the capo is inspector rule <c>L02</c>
    ///         (<c>CapoAnchorRangeRule</c>) — the hand cannot be there. The clamp
    ///         is load-bearing and not defensive: the capo spinner goes to 24, and
    ///         an unclamped <c>capo + 1</c> inverts a <see cref="Math.Clamp(int,int,int)" />
    ///         range and THROWS.
    ///     </para>
    ///     <para>
    ///         Public because the floor belongs to every path that produces an
    ///         anchor fret, not only to the suggester: the UI's PREVIEW anchor
    ///         (the wheel over the FHP band, and the fret the placement verb takes
    ///         from it) clamped at a bare <c>1</c> and so armed an L02 on any
    ///         capo'd chart, while the keyboard nudge beside it already read the
    ///         capo. Mirrored — with that stated — by
    ///         <c>FretAnchorGenerationService.MinAnchorFret</c> and
    ///         <c>ChartImprovementService.MinAnchorFret</c>.
    ///     </para>
    /// </summary>
    public static int MinFret(int capo) => Math.Clamp(Math.Max(0, capo) + 1, 1, MaxFret);

    /// <summary>
    ///     The fret to open a new anchor at <paramref name="position" /> on.
    ///     <para>
    ///         Scope is <paramref name="position" /> up to (excluding) the next
    ///         anchor — exactly what this anchor will govern once it exists. An
    ///         empty span keeps <paramref name="previousFret" />: with nothing to
    ///         derive from, continuing the hand position is the least surprising
    ///         answer and matches what the user got before.
    ///     </para>
    ///     <para>
    ///         OPEN strings never constrain the hand and are skipped — a fret at
    ///         or below the capo sounds as the open string
    ///         (<c>max(fret, capo)</c>), so it says nothing about where the hand
    ///         is. The floor is <c>capo + 1</c> for the same reason: an anchor on
    ///         or below the capo is inspector rule L02.
    ///     </para>
    /// </summary>
    public static int Suggest(
        FractionalPosition position,
        IEnumerable<GuitarNote> notes,
        IEnumerable<Chord> chords,
        IEnumerable<FretAnchor> anchors,
        int capo = 0,
        int? previousFret = null)
    {
        var floor = MinFret(capo);
        var end = NextAnchorPosition(position, anchors);

        var lowest = int.MaxValue;

        foreach (var note in notes)
        {
            if (!note.Position.IsWithin(position, end)) continue;
            if (note.Fret > capo && note.Fret < lowest) lowest = note.Fret;
        }

        foreach (var chord in chords)
        {
            if (!chord.Position.IsWithin(position, end)) continue;
            foreach (var chordNote in chord.ChordNotes)
            {
                if (chordNote.Fret > capo && chordNote.Fret < lowest)
                    lowest = chordNote.Fret;
            }
        }

        if (lowest == int.MaxValue)
            return Math.Max(floor, previousFret ?? DefaultFret);

        return Math.Max(floor, lowest);
    }

    private static FractionalPosition? NextAnchorPosition(
        FractionalPosition position, IEnumerable<FretAnchor> anchors)
    {
        FractionalPosition? next = null;
        foreach (var anchor in anchors)
        {
            if (anchor.Position <= position) continue;
            if (next == null || anchor.Position < next.Value) next = anchor.Position;
        }

        return next;
    }
}
