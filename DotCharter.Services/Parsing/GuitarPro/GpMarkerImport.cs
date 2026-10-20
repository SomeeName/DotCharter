namespace DotCharter.Services.Parsing.GuitarPro;

/// <summary>
///     Shared helpers for the two Editor-on-Fire text/marker import settings:
///     <list type="bullet">
///         <item>Beat text "T"/"S"/"P" → tap / slap / pop technique
///         (<see cref="TryParseTechnique"/>).</item>
///         <item>Section markers / free text → RS phrase + (when the name is a
///         valid RS section) section EventPoint
///         (<see cref="CreateMarkerEvent"/>).</item>
///     </list>
/// </summary>
public static class GpMarkerImport
{
    /// <summary>A single note technique a beat text can request.</summary>
    public enum BeatTextTechnique
    {
        Tap,
        Slap,
        Pop
    }

    /// <summary>True when <paramref name="name"/> is a section Rocksmith will
    /// render. The vocabulary is the shared <see cref="RsSectionCatalog"/> — the
    /// SAME list the event dialog offers, so a name you can author is a name an
    /// import accepts. A marker outside it imports phrase-only.</summary>
    public static bool IsRsSectionName(string? name)
        => RsSectionCatalog.IsValid(name);

    /// <summary>Maps a beat text to a tap/slap/pop technique. "T"/"S"/"P"
    /// (case-insensitive, trimmed) only — any other text is not a technique
    /// (it may still be a section/phrase marker).</summary>
    public static bool TryParseTechnique(string? text, out BeatTextTechnique technique)
    {
        technique = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        switch (text.Trim().ToUpperInvariant())
        {
            case "T": technique = BeatTextTechnique.Tap; return true;
            case "S": technique = BeatTextTechnique.Slap; return true;
            case "P": technique = BeatTextTechnique.Pop; return true;
            default: return false;
        }
    }

    /// <summary>Applies a beat-text technique to a parsed sound. Tap maps to
    /// <see cref="HopoType.Tap"/>; slap/pop map to <see cref="BassPickingType"/>.</summary>
    public static void ApplyTechnique(IGuitarSound sound, BeatTextTechnique technique)
    {
        switch (technique)
        {
            case BeatTextTechnique.Tap:
                sound.Hopo = HopoType.Tap;
                break;
            case BeatTextTechnique.Slap:
                sound.BassPicking = BassPickingType.Slap;
                break;
            case BeatTextTechnique.Pop:
                sound.BassPicking = BassPickingType.Pop;
                break;
        }
    }

    /// <summary>Builds the phrase/section EventPoint for a marker at
    /// <paramref name="position"/>. The phrase name is always the trimmed marker
    /// text; the section is set only when the name is a valid RS section
    /// (else phrase-only). Returns null for a blank name.</summary>
    public static EventPoint? CreateMarkerEvent(FractionalPosition position, string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var trimmed = name.Trim();
        return new EventPoint
        {
            Position = position,
            Phrase = trimmed,
            Section = IsRsSectionName(trimmed) ? trimmed.ToLowerInvariant() : ""
        };
    }
}
