using Chord = DotCharter.Core.Domain.Guitar.Chord;
using GuitarNote = DotCharter.Core.Domain.Guitar.GuitarNote;
using HandShape = DotCharter.Core.Domain.Guitar.HandShape;

namespace DotCharter.Services.Editing.Targets;

/// <summary>
///     THE answer to "what does this keyboard verb act on" — one ladder, in
///     front of every <c>IHighwayEditingService</c> verb and the pen.
///     <para>
///         A pen that resolves its own target (selection, else the
///         sound at the playhead) while every OTHER keyboard verb acts on the
///         selection and on nothing else would make the same keystroke reach a
///         different sound depending on which verb it was. This type removes
///         that inconsistency; it is rscharter's <c>GuitarModeHandler
///         .toggleString</c> order (<c>:337-355</c>) with EOF's implied
///         selection (<c>menu/note.c:11065-11112</c>) as the bottom rung.
///     </para>
///     <para>
///         <b>PURE.</b> No services, no ambient state, no clock, no
///         <c>ObservableCollection</c> — every input is a parameter, for the
///         same reason <c>ChartPen</c> and <c>GlobalChordGate</c> are pure.
///         Stage 5 builds the inspector's own target on this, so the contract
///         matters more than the call sites.
///     </para>
/// </summary>
public static class EditTargetResolver
{
    /// <summary>
    ///     Resolves in the order <b>selection → hand shape → pen</b>:
    ///     <list type="number">
    ///         <item>any note or chord selected ⇒ exactly those sounds;</item>
    ///         <item>
    ///             else any hand shape selected ⇒ the sounds those shapes SPAN
    ///             (<c>HandShape.Covers</c>, whose end is EXCLUSIVE);
    ///         </item>
    ///         <item>
    ///             else the sound under the pen at <paramref name="penPosition" />
    ///             — the chord there, else every note there;
    ///         </item>
    ///         <item>else <see cref="EditTarget.None" />.</item>
    ///     </list>
    ///     <para>
    ///         <b>A rung claims the resolution as soon as its own selector is
    ///         non-empty, even when it yields no sounds.</b> A selected hand
    ///         shape over an empty bar therefore resolves to
    ///         <see cref="EditTargetSource.HandShape" /> with nothing to edit
    ///         rather than falling through to whatever sits under the playhead —
    ///         which is rscharter's unconditional <c>return</c> on its
    ///         <c>HAND_SHAPE</c> branch, and the only reading under which the
    ///         user's own selection cannot be silently overruled.
    ///     </para>
    ///     <para>
    ///         <paramref name="penPosition" /> is <c>null</c> when there is no
    ///         pen position to speak of (no chart / no beat grid), which simply
    ///         removes the bottom rung.
    ///     </para>
    /// </summary>
    public static EditTarget Resolve(
        IReadOnlyList<GuitarNote> selectedNotes,
        IReadOnlyList<Chord> selectedChords,
        IReadOnlyList<HandShape> selectedHandShapes,
        IReadOnlyList<GuitarNote> levelNotes,
        IReadOnlyList<Chord> levelChords,
        FractionalPosition? penPosition)
    {
        // 1. The selection wins. Snapshotted (never the live collections): the
        //    actions capture the instances and the spacing enforcer running in
        //    their post-execute can mutate the selection while they run.
        if (selectedNotes.Count > 0 || selectedChords.Count > 0)
        {
            return new EditTarget(EditTargetSource.Selection,
                [.. selectedNotes], [.. selectedChords], []);
        }

        // 2. A selected hand shape means "the run under this bar".
        if (selectedHandShapes.Count > 0)
        {
            var shapes = new List<HandShape>(selectedHandShapes);
            var notes = new List<GuitarNote>();
            var chords = new List<Chord>();

            foreach (var note in levelNotes)
            {
                if (CoveredByAny(shapes, note.Position))
                    notes.Add(note);
            }

            foreach (var chord in levelChords)
            {
                if (CoveredByAny(shapes, chord.Position))
                    chords.Add(chord);
            }

            return new EditTarget(EditTargetSource.HandShape, notes, chords, shapes);
        }

        // 3. The pen: the sound at the playhead, EOF's implied selection.
        if (penPosition is { } position)
        {
            foreach (var chord in levelChords)
            {
                if (chord.Position == position)
                    // A chord and loose notes cannot legally share an instant on
                    // the same strings, and the chord is the richer object — the
                    // pen's own resolution has always preferred it.
                    return new EditTarget(EditTargetSource.Pen, [], [chord], []);
            }

            var atPen = new List<GuitarNote>();
            foreach (var note in levelNotes)
            {
                if (note.Position == position)
                    atPen.Add(note);
            }

            if (atPen.Count > 0)
                return new EditTarget(EditTargetSource.Pen, atPen, [], []);
        }

        return EditTarget.None;
    }

    /// <summary><c>HandShape.EndPosition</c> is EXCLUSIVE — shapes abut, so a
    /// sound sitting exactly on one shape's end belongs to the NEXT one
    /// (<c>Covers</c> is the single reading of that; two refret sites that
    /// open-coded <c>&lt;=</c> produced real bugs).</summary>
    private static bool CoveredByAny(List<HandShape> shapes, FractionalPosition position)
    {
        foreach (var shape in shapes)
        {
            if (shape.Covers(position))
                return true;
        }

        return false;
    }
}
