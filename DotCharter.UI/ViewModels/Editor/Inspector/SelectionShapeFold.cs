
namespace DotCharter.UI.ViewModels.Editor.Inspector;

/// <summary>
///     Everything the multi-selection's two summary strings print, folded in ONE
///     pass over the selection.
///
///     <para>
///         <c>InspectorViewModel.SelectionShapeSummary</c> and
///         <c>.BendSummary</c> are both BOUND, so Avalonia re-evaluates each of
///         them on every <c>NotifySelectionKindChanged</c> — and that fires from
///         all three inspector syncs, several times per click. Between them they
///         would make FOUR passes over the whole selection per evaluation (a
///         fret list, a string set, a bend count, a shape projection), plus
///         <c>frets.Min()</c> and <c>frets.Max()</c> as two more, plus a
///         <see cref="List{T}" /> of one <c>int</c> per selected sound. On a
///         20,000-sound selection that is the click's whole frame budget.
///     </para>
///     <para>
///         The shape count was the expensive half on its own: it built a
///         <c>string.Join</c> of <c>"string:fret"</c> pairs PER SELECTED CHORD
///         and de-duplicated the strings — one allocation per chord per
///         evaluation, ~2,000 of them on a dense selection. The pack below is the
///         same identity without the strings; the string form is kept as a
///         FALLBACK for the shapes it cannot represent, so the answer is
///         unchanged rather than approximated.
///     </para>
///     <para>
///         Same shape and same reason as <c>NoteFlagAgreement.Over</c> /
///         <c>ChordFlagAgreement.Over</c> beside it: a pure fold with no snapshot,
///         unit-tested against the expressions it replaced.
///     </para>
/// </summary>
internal readonly record struct SelectionShapeFold(
    bool HasSounds, int MinFret, int MaxFret, int MinString, int MaxString,
    int DistinctShapes, int WithBends, int Total)
{
    /// <summary>Strings this pack can represent (one 7-bit slot each in a
    /// <see cref="ulong" />: 9 × 7 = 63 bits).</summary>
    private const int MaxPackedStrings = 9;

    /// <summary>Frets this pack can represent. Stored as <c>fret + 1</c> so 0
    /// means "string not sounded", which is what makes an absent string
    /// distinguishable from an open one.</summary>
    private const int MaxPackedFret = 126;

    public static SelectionShapeFold Over(IReadOnlyList<GuitarNote> notes, IReadOnlyList<Chord> chords)
    {
        var has = false;
        var minFret = int.MaxValue;
        var maxFret = int.MinValue;
        var minString = int.MaxValue;
        var maxString = int.MinValue;
        var withBends = 0;

        foreach (var n in notes)
        {
            has = true;
            if (n.Fret < minFret) minFret = n.Fret;
            if (n.Fret > maxFret) maxFret = n.Fret;
            if (n.StringIndex < minString) minString = n.StringIndex;
            if (n.StringIndex > maxString) maxString = n.StringIndex;
            if (n.BendValues.Count > 0) withBends++;
        }

        // Lazily created: a note-only selection allocates neither set, and a
        // selection of identical chords keeps one entry.
        HashSet<ulong>? packedShapes = null;
        HashSet<string>? spelledShapes = null;

        foreach (var chord in chords)
        {
            var chordNotes = chord.ChordNotes;
            var bent = false;
            foreach (var cn in chordNotes)
            {
                has = true;
                if (cn.Fret < minFret) minFret = cn.Fret;
                if (cn.Fret > maxFret) maxFret = cn.Fret;
                if (cn.StringIndex < minString) minString = cn.StringIndex;
                if (cn.StringIndex > maxString) maxString = cn.StringIndex;
                if (!bent && cn.BendValues.Count > 0) bent = true;
            }

            if (bent) withBends++;

            if (TryPack(chordNotes, out var packed)) (packedShapes ??= []).Add(packed);
            else (spelledShapes ??= []).Add(Spell(chordNotes));
        }

        // A packed shape and a spelled one can never be the same shape: TryPack
        // refuses exactly the shapes Spell is kept for, so the two sets are
        // disjoint by construction and their counts add.
        var shapes = (packedShapes?.Count ?? 0) + (spelledShapes?.Count ?? 0);

        return new SelectionShapeFold(
            has,
            has ? minFret : 0, has ? maxFret : 0,
            has ? minString : -1, has ? maxString : -1,
            shapes, withBends, notes.Count + chords.Count);
    }

    /// <summary>
    ///     Packs a grip into one <see cref="ulong" />: <c>fret + 1</c> in the
    ///     7-bit slot of its string. Refuses anything the slots cannot hold
    ///     EXACTLY — a string or fret out of range, and a REPEATED string, which
    ///     would overwrite its own slot and make two different grips pack alike.
    /// </summary>
    private static bool TryPack(IReadOnlyList<ChordNote> chordNotes, out ulong packed)
    {
        packed = 0;
        foreach (var cn in chordNotes)
        {
            if (cn.StringIndex is < 0 or >= MaxPackedStrings) return false;
            if (cn.Fret is < 0 or > MaxPackedFret) return false;

            var shift = cn.StringIndex * 7;
            if (((packed >> shift) & 0x7F) != 0) return false; // repeated string
            packed |= (ulong)(cn.Fret + 1) << shift;
        }

        return true;
    }

    /// <summary>The original identity, kept verbatim for the grips
    /// <see cref="TryPack" /> refuses — an eight-string tuning past slot 8, a
    /// fret beyond 126, a malformed chord with two notes on one string.</summary>
    private static string Spell(IReadOnlyList<ChordNote> chordNotes)
        => string.Join(",", chordNotes.OrderBy(n => n.StringIndex).Select(n => $"{n.StringIndex}:{n.Fret}"));
}
