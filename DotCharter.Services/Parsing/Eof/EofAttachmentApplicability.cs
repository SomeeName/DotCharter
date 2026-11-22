namespace DotCharter.Services.Parsing.Eof;

/// <summary>The read-side application rules, also used before deleting a synthesized ornament.</summary>
internal static class EofAttachmentApplicability
{
    public static bool TechNoteApplies(EofRawProGuitarNote tech, EofRawProGuitarNote sound, int stringIndex)
    {
        return stringIndex is >= 0 and < 8 && tech.Difficulty == sound.Difficulty &&
               (tech.NoteBitmask & (1 << stringIndex)) != 0 && tech.PositionMs >= sound.PositionMs &&
               tech.PositionMs <= (ulong)sound.PositionMs + sound.LengthMs;
    }

    public static bool SectionApplies((int diff, uint startMs, uint endMs) section, EofRawProGuitarNote sound) =>
        section.diff == sound.Difficulty && sound.PositionMs >= section.startMs && sound.PositionMs <= section.endMs;
}
