using DotCharter.Services.Export.Rocksmith;

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
    /// <paramref name="position"/>. Nothing is taken unchecked: the phrase name is
    /// the trimmed marker text in the spelling Rocksmith accepts
    /// (<see cref="RsPhraseName.Sanitize"/>), and the section is the catalog name the
    /// marker reads as once its numbering and punctuation are set aside
    /// (<see cref="SectionFor"/>), else none (phrase-only). Returns null for a blank
    /// name.</summary>
    public static EventPoint? CreateMarkerEvent(FractionalPosition position, string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var trimmed = name.Trim();
        return new EventPoint
        {
            Position = position,
            Phrase = RsPhraseName.Sanitize(trimmed, 0),
            Section = SectionFor(trimmed) ?? ""
        };
    }

    /// <summary>The <see cref="RsSectionCatalog"/> name a marker reads as: "Verse 2",
    /// "Pre-Chorus" and "CHORUS #3" are verse, prechorus and chorus. Only digits,
    /// spaces and punctuation are set aside, never a word, so "Guitar Solo" stays
    /// phrase-only. Null when no section matches.</summary>
    public static string? SectionFor(string? marker)
    {
        if (string.IsNullOrWhiteSpace(marker)) return null;
        if (RsSectionCatalog.Normalize(marker) is { } exact) return exact;
        var letters = new string(marker.Where(char.IsLetter).ToArray());
        return RsSectionCatalog.Normalize(letters);
    }

    /// <summary>Adds a marker's EventPoint and phrase name. A position that already
    /// carries a phrase keeps it: a GP bar can hold a section marker and a beat text
    /// at once, and one position holds one phrase.</summary>
    public static void AddMarker(Arrangement arrangement, EventPoint? ev)
    {
        ArgumentNullException.ThrowIfNull(arrangement);
        if (ev == null) return;
        if (arrangement.EventPoints.Any(e => e.Position.CompareTo(ev.Position) == 0 && !string.IsNullOrEmpty(e.Phrase)))
            return;

        arrangement.EventPoints.Add(ev);
        if (!arrangement.Phrases.Contains(ev.Phrase)) arrangement.Phrases.Add(ev.Phrase);
    }
}
