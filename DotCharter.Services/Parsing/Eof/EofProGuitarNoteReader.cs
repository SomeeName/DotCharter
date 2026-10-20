namespace DotCharter.Services.Parsing.Eof;

/// <summary>
///     Reads a single Pro Guitar note structure from an EOF stream.
///     Mirrors <c>eof_read_pro_guitar_note</c> in <c>song.c</c> exactly.
/// </summary>
internal static class EofProGuitarNoteReader
{
    public static EofRawProGuitarNote Read(EofPackReader r)
    {
        var note = new EofRawProGuitarNote
        {
            Name = r.ReadString()
        };
        r.ReadU8(); // chord number (unused)
        note.Difficulty = r.ReadU8();
        note.NoteBitmask = r.ReadU8();
        note.GhostBitmask = r.ReadU8();

        for (var s = 0; s < 8; s++)
        {
            if ((note.NoteBitmask & (1 << s)) != 0)
                note.Frets[s] = r.ReadU8();
        }

        note.LegacyMask = r.ReadU8();
        note.PositionMs = r.ReadU32();
        note.LengthMs = r.ReadU32();
        note.Flags = r.ReadU32();

        if ((note.Flags & EofConstants.PgFlagRsNotation) != 0)
        {
            if ((note.Flags & (EofConstants.PgFlagSlideUp | EofConstants.PgFlagSlideDown)) != 0)
                note.SlideEndFret = r.ReadU8();
            if ((note.Flags & EofConstants.PgFlagBend) != 0)
                note.BendStrength = r.ReadU8();
        }

        if ((note.Flags & EofConstants.PgFlagUnpitchedSlide) != 0)
            note.UnpitchedSlideEndFret = r.ReadU8();

        if ((note.Flags & EofConstants.NoteFlagExtended) != 0)
            note.ExtendedFlags = r.ReadU32();

        return note;
    }

    /// <summary>Reads-and-discards a pro guitar note for stream alignment.</summary>
    public static void SkipNote(EofPackReader r) => Read(r);
}

internal sealed class EofRawProGuitarNote
{
    public string Name { get; set; } = "";
    public byte Difficulty { get; set; }
    public byte NoteBitmask { get; set; }
    public byte GhostBitmask { get; set; }
    public byte[] Frets { get; } = new byte[8];
    public byte LegacyMask { get; set; }
    public uint PositionMs { get; set; }
    public uint LengthMs { get; set; }
    public uint Flags { get; set; }
    public byte SlideEndFret { get; set; }
    public byte BendStrength { get; set; }
    public byte UnpitchedSlideEndFret { get; set; }
    public uint ExtendedFlags { get; set; }
}
