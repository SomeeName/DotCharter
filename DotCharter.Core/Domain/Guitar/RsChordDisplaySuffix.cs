namespace DotCharter.Core.Domain.Guitar;

/// <summary>
///     The Rocksmith chord flags that interchange formats spell as TEXT on the end
///     of a chord's display name. Inside the app each one is a
/// <see cref="ChordTemplate" /> field, never name text: a reader FOLDS
///     the suffix into the field and a writer derives the suffix back from it, so
///     renaming a chord cannot switch a flag on or off.
///     <para>
///         <c>-arp</c> is <see cref="ChordTemplate.ForceArpeggioInRs" /> (its fold
///         lives in <c>RsXmlSongReader</c> and <c>ChartSpacingEnforcer</c>);
///         <c>-nop</c> is <see cref="ChordTemplate.NopInRs" />. A further flag
///         spelled this way follows the same pair: a <c>Fold…</c> for every
///         reader and a <c>ForExport</c> answer for every writer.
///     </para>
/// </summary>
public static class RsChordDisplaySuffix
{
    public const string Arpeggio = "-arp";
    public const string Nop = "-nop";

    /// <summary>
    ///     Moves a trailing <c>-nop</c> out of <see cref="ChordTemplate.Name" /> into
    ///     <see cref="ChordTemplate.NopInRs" />. Ordinal, as the game's converters
    ///     match it. Unlike <c>-arp</c> it may leave an EMPTY name: Rocksmith's own
    ///     double stops ship unnamed with the flag set. Returns whether it folded.
    /// </summary>
    public static bool FoldNop(ChordTemplate template)
    {
        ArgumentNullException.ThrowIfNull(template);
        if (!template.Name.EndsWith(Nop, StringComparison.Ordinal)) return false;

        template.Name = template.Name[..^Nop.Length];
        template.NopInRs = true;
        return true;
    }

    /// <summary>
    ///     What an exporter writes for <paramref name="template" />: the name without
    ///     the suffix, and whether the Nop flag ships. The FIELD decides; a
    ///     <c>-nop</c> still sitting on a name that no reader has folded yet (typed
    ///     before the first save) counts too, and is never written twice.
    /// </summary>
    public static (string Name, bool Nop) ForExport(ChordTemplate template)
    {
        ArgumentNullException.ThrowIfNull(template);
        return template.Name.EndsWith(Nop, StringComparison.Ordinal)
            ? (template.Name[..^Nop.Length], true)
            : (template.Name, template.NopInRs);
    }

    /// <summary>
    ///     The RS-XML <c>displayName</c> for an exported <paramref name="name" />. One
    ///     suffix fits: Rocksmith2014.NET and the Custom Song Toolkit test <c>-arp</c>
    ///     first and only then <c>-nop</c>, so an arpeggio wins. The SNG mask carries
    ///     both bits (official charts have mask 3).
    /// </summary>
    public static string DisplayName(string name, bool arpeggio, bool nop) =>
        name + (arpeggio ? Arpeggio : nop ? Nop : "");
}
