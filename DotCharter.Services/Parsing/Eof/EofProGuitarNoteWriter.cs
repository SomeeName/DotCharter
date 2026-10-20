namespace DotCharter.Services.Parsing.Eof;

/// <summary>
///     Writes a single Pro Guitar note structure to an EOF stream.
///     Mirrors <c>eof_write_pro_guitar_note</c> in <c>song.c</c> exactly —
///     the write-side counterpart of <see cref="EofProGuitarNoteReader"/>.
///     TRAP: several fields are conditional on the flags — the slide end fret
///     and bend strength only exist under RS_NOTATION (and their respective
///     slide/bend flags), the unpitched slide end fret only under
///     UNPITCH_SLIDE, and the extended flags dword only when extended flags
///     are non-zero (which also toggles the 0x80000000 marker bit).
/// </summary>
internal static class EofProGuitarNoteWriter
{
    public static void Write(EofPackWriter w, EofRawProGuitarNote note)
    {
        w.WriteString(note.Name);
        w.WriteU8(0); // Chord number (not supported by EOF yet).
        w.WriteU8(note.Difficulty);
        w.WriteU8(note.NoteBitmask);
        w.WriteU8(note.GhostBitmask);

        for (var s = 0; s < 8; s++)
        {
            if ((note.NoteBitmask & (1 << s)) != 0)
                w.WriteU8(note.Frets[s]);
        }

        w.WriteU8(note.LegacyMask);
        w.WriteU32(note.PositionMs);
        w.WriteU32(note.LengthMs);

        var flags = note.Flags;
        if (note.ExtendedFlags != 0) flags |= EofConstants.NoteFlagExtended;
        else flags &= ~EofConstants.NoteFlagExtended;
        w.WriteU32(flags);

        if ((flags & EofConstants.PgFlagRsNotation) != 0)
        {
            if ((flags & (EofConstants.PgFlagSlideUp | EofConstants.PgFlagSlideDown)) != 0)
                w.WriteU8(note.SlideEndFret);
            if ((flags & EofConstants.PgFlagBend) != 0)
                w.WriteU8(note.BendStrength);
        }

        if ((flags & EofConstants.PgFlagUnpitchedSlide) != 0)
            w.WriteU8(note.UnpitchedSlideEndFret);

        if ((flags & EofConstants.NoteFlagExtended) != 0)
            w.WriteU32(note.ExtendedFlags);
    }
}
