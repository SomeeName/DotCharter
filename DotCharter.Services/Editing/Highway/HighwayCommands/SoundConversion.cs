namespace DotCharter.Services.Editing.Highway.HighwayCommands;

/// <summary>
///     The ONE answer to "re-express this loose note as a chord note".
///     <para>
///         A note and a chord note are the same sound written two ways, and the
///         editor converts between them constantly: placing a second string at a
///         position that already holds loose notes PROMOTES them into a chord,
///         removing the second-to-last string of a chord COLLAPSES it back, and a
///         drag that drops sounds onto one position merges them. Each of those
///         must not build the destination field by field, because every hand-written
///         list is a chance to forget one — a collapse
///         branch carrying only string/fret/sustain, or a
///         PROMOTION branch doing the same, would turn a palm-muted,
///         bent, link-next note into a plain open chord note the moment a
///         second string joined it.
///     </para>
///     <para>
///         Bends are CLONED: the note this came from is still on the undo stack,
///         and a later edit to the chord note would otherwise reach back and
///         rewrite the reverted state too.
///     </para>
/// </summary>
public static class SoundConversion
{
    /// <summary>Every field <see cref="ChordNote" /> and <see cref="GuitarNote" />
    /// share. The ones a chord note does not have (Position, PassOtherNotes,
    /// IgnoreDerived, NotationDuration, GraceSlide) belong to the CHORD or to the
    /// note's place in the level, and are the caller's business.</summary>
    public static ChordNote ToChordNote(GuitarNote note)
    {
        ArgumentNullException.ThrowIfNull(note);

        var chordNote = new ChordNote
        {
            StringIndex = note.StringIndex,
            Fret = note.Fret,
            Sustain = note.Sustain,
            BassPicking = note.BassPicking,
            Mute = note.Mute,
            Hopo = note.Hopo,
            Harmonic = note.Harmonic,
            Vibrato = note.Vibrato,
            VibratoStrength = note.VibratoStrength,
            Tremolo = note.Tremolo,
            LinkNext = note.LinkNext,
            SlideTo = note.SlideTo,
            UnpitchedSlide = note.UnpitchedSlide,
            Accent = note.Accent,
            Ignore = note.Ignore,
            GraceFret = note.GraceFret
        };
        foreach (var bend in note.BendValues) chordNote.BendValues.Add(bend.Clone());
        return chordNote;
    }
}
