namespace DotCharter.Core.Domain.Guitar;

/// <summary>
///     The arpeggio FLAVOUR of a <see cref="ChordTemplate" /> — both flags,
///     carried together.
///     <para>
///         <b>Two flags spell ARPEGGIO and each import path sets only one of
///         them.</b> <c>RsXmlSongReader.ReadChordTemplates</c> sets
///         <see cref="ChordTemplate.ForceArpeggioInRs" /> (from a <c>-arp</c>
///         displayName) and never <see cref="ChordTemplate.Arpeggio" />;
///         the package reader's SNG mapper sets
///         <see cref="ChordTemplate.Arpeggio" /> (from chord mask 0x1) and never
///         the other. So code that reads ONE flag works on psarc imports and is
///         silently dead on every DLC-Builder / EOF chart, or the reverse.
///     </para>
///     <para>
///         There are exactly two questions, and they are not the same one:
///         <list type="bullet">
///             <item>
///                 <b>"Is this an arpeggio?"</b> — <see cref="IsArpeggio" /> (or
///                 <see cref="ChordTemplate.IsArpeggio" />). Either flag answers
///                 yes; both spellings export as <c>-arp</c> /
///                 <c>ChordMask.Arpeggio</c>.
///             </item>
///             <item>
///                 <b>"Is this the same arpeggio flavour?"</b> —
///                 <see cref="Matches" />, which compares BOTH flags exactly.
///                 That is the half of the find-or-create key that is not the
///                 frets, and it is deliberately the conservative comparison: a
///                 false negative mints one extra template, while a false
///                 positive repoints a chord onto a template that exports under
///                 a different name.
///             </item>
///         </list>
///     </para>
/// </summary>
/// <param name="Arpeggio">
///     <see cref="ChordTemplate.Arpeggio" /> — the flag the psarc/SNG import
///     path sets and the one the renderers and the <c>.eof</c> exporter read.
/// </param>
/// <param name="ForceArpeggioInRs">
///     <see cref="ChordTemplate.ForceArpeggioInRs" /> — the flag the RS-XML
///     reader sets from a <c>-arp</c> displayName, which the RS-XML writer
///     re-appends the suffix from.
/// </param>
public readonly record struct ArpeggioFlavour(bool Arpeggio, bool ForceArpeggioInRs)
{
    /// <summary>Not an arpeggio in either spelling.</summary>
    public static ArpeggioFlavour Plain => default;

    /// <summary>Both flags of <paramref name="template" />; <see cref="Plain" />
    /// for a null template (a chord whose <c>TemplateId</c> is the -1 sentinel
    /// or dangles is not an arpeggio).</summary>
    public static ArpeggioFlavour Of(ChordTemplate? template) => template is null ? default : new ArpeggioFlavour(template.Arpeggio, template.ForceArpeggioInRs);

    /// <summary>"Is this an arpeggio?" — either flag says yes.</summary>
    public bool IsArpeggio => Arpeggio || ForceArpeggioInRs;

    /// <summary>"Is this the same arpeggio flavour?" — BOTH flags, exactly. The
    /// non-fret half of every find-or-create key.</summary>
    public bool Matches(ChordTemplate? template) => Of(template) == this;

    /// <summary>Stamps both flags onto a freshly minted template, so a mint can
    /// never carry half a flavour.</summary>
    public void ApplyTo(ChordTemplate template)
    {
        ArgumentNullException.ThrowIfNull(template);
        template.Arpeggio = Arpeggio;
        template.ForceArpeggioInRs = ForceArpeggioInRs;
    }
}
