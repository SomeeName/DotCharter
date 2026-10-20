namespace DotCharter.Services.Editing.FretAnchors;

public interface IFretAnchorGenerationService
{
    /// <summary>
    ///     Generates fret-hand-position anchors for a level.
    /// </summary>
    /// <param name="level">Level whose notes/chords/hand shapes drive the walk.</param>
    /// <param name="rangeStart">Inclusive first position to emit for; null = from the start.</param>
    /// <param name="rangeEnd">Inclusive last position to emit for; null = to the end.</param>
    /// <param name="chordTemplates">
    ///     Templates backing the level's chords, used for the fingering-aware
    ///     root rules; null = no fingering information.
    /// </param>
    /// <param name="capo">
    ///     Capo fret of the owning arrangement. Frets are ABSOLUTE on the
    ///     un-capoed neck, so a capo makes every fret at or below it "open" and
    ///     no anchor may sit there (inspector L02). <c>0</c> (the default) means
    ///     no capo and is a strict no-op.
    /// </param>
    /// <param name="bassArrangement">
    ///     True when the level belongs to a bass arrangement: the "hand
    ///     abandonment" re-anchor looks 6 positions ahead instead of 14, forced
    ///     up-shifts are pulled back to where the climb started, and no double
    /// stop is held. Each rule is measured per instrument.
    /// </param>
    /// <param name="freeGrip">
    ///     Per chord template: the BASE grip of a fingering nobody authored (the
    ///     suggestion, possibly shifted up the hand by an earlier pass), so the
    /// hand may stay where it is and the chord follow it; null for an
    ///     authored one. Build it with <see cref="ChordFingeringFollower.FreeGripResolver" />
    ///     and pair it with <see cref="ChordFingeringFollower" />, which re-fingers
    ///     those chords. Null (the default) treats every fingering as authored.
    /// </param>
    /// <param name="tuning">
    ///     The owning arrangement, read for its TUNING only: a bass octave figure
    ///     must SOUND an octave apart (drop D's low string + 2 frets on the D
    ///     string is a ninth). Null recognises it by fret shape alone.
    /// </param>
    /// <returns>The generated anchors, ordered by position.</returns>
    IReadOnlyList<FretAnchor> GenerateAnchors(
        ChartLevel level,
        FractionalPosition? rangeStart = null,
        FractionalPosition? rangeEnd = null,
        IReadOnlyList<ChordTemplate>? chordTemplates = null,
        int capo = 0,
        bool bassArrangement = false,
        Func<ChordTemplate, ChordTemplate?>? freeGrip = null,
        Arrangement? tuning = null);
}
