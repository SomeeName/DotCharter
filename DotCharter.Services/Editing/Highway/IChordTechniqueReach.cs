namespace DotCharter.Services.Editing.Highway;

/// <summary>
/// The inspector's "Apply to" ladder as the KEYBOARD's technique verbs see it: a mute,
///     HOPO, harmonic, picking, vibrato, tremolo, link-next, accent, ignore or pass-other key on a
///     chord reaches the same chords a click on the same technique in the inspector would — the
///     rung's chords on every level, with the ladder's marks on screen — in ONE undo step.
///     <para>
///         The ladder lives in <c>DotCharter.UI</c> (<c>ChordApplyScope</c>), above this layer, so
///         the UI hands an implementation to <see cref="IHighwayEditingService.TechniqueReach" />;
///         with none set every verb acts on the selection, exactly as before.
///     </para>
/// </summary>
public interface IChordTechniqueReach
{
    /// <summary>
    ///     The chords a technique verb on <paramref name="selectedChords" /> (the user's own
    ///     selection, never an implied target) writes, on every level, when a ladder on screen
    ///     reaches past that selection; null keeps the verb on the selection.
    /// </summary>
    IReadOnlyList<Chord>? ChordsFor(IReadOnlyList<Chord> selectedChords);

    /// <summary>After a verb wrote <paramref name="chords" /> through the ladder: the status rail
    /// says how far it went, as it does for the inspector's own click.</summary>
    void Reached(IReadOnlyList<Chord> chords);
}
