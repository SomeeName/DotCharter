using DotCharter.UI.ViewModels.PlayMode;

namespace DotCharter.UI.ViewModels.Panels;

/// <summary>
///     The derivations BOTH library surfaces print on a song row: Play Mode's
///     Song Library (<c>PlaySongItem</c>) and the editor's Rocksmith song
///     browser (<see cref="PsarcSongListItem" />).
///     <para>
///         They existed only on the first of the two, and that is the
///         inconsistency the 0.49 browser redesign was commissioned to remove:
///         the library folded tuning AND capo into one fixed-width cell and
///         went amber when either meant you had to do something to the guitar,
///         while the browser printed a tuning chip always and a capo chip only
///         when there WAS a capo — so the same song read two different ways
///         depending on which screen you opened it from.
///     </para>
///     <para>
///         Pure: no Avalonia, no services, no localization lookup with one
///         deliberate exception noted per member. Everything here is a function
///         of the library index fields (<c>TuningSummary</c>,
///         <c>CapoSummary</c>, <c>ArrangementSummary</c>).
///     </para>
/// </summary>
public static class SongRowFacts
{
    /// <summary>The one tuning that needs nothing done to the guitar. Compared
    /// against <c>DotCharter.Core.Utilities.TuningNameResolver.Resolve</c>'s canonical name, so it
    /// is an identifier and not a user-visible string.</summary>
    public const string StandardTuningName = "E Standard";

    /// <summary>Compact capo label ("Capo 2"); empty for no/unknown capo.
    /// Rocksmith's own vocabulary — deliberately not localized, exactly like the
    /// path names (Lead/Rhythm/Bass) beside it.</summary>
    public static string CapoLabel(int? capo, ILocalizationService localization) =>
        capo is > 0 ? localization.Format("SongBrowser_Facet_CapoAt", capo.Value) : "";

    /// <summary>The tuning name when the letters resolve to one ("Drop C"), else
    /// the raw letters. DISPLAY ONLY — every tuning FILTER still matches the raw
    /// summary.</summary>
    public static string TuningDisplay(string? tuningSummary) =>
        string.IsNullOrWhiteSpace(tuningSummary)
            ? ""
            : DotCharter.Core.Utilities.TuningNameResolver.Resolve(tuningSummary) ?? tuningSummary;

    /// <summary>Not plain E standard. UNKNOWN (an empty summary — an own
    /// chart) is NOT "non-standard": a value we cannot judge never raises
    /// an alarm.</summary>
    public static bool IsNonStandardTuning(string? tuningSummary) =>
        !string.IsNullOrWhiteSpace(tuningSummary) &&
        !string.Equals(DotCharter.Core.Utilities.TuningNameResolver.Resolve(tuningSummary), StandardTuningName, StringComparison.Ordinal);

    /// <summary>Amber-worthy: a non-standard tuning or a capo. Colour is
    /// semantic on the Fire layer, and amber means "this needs your attention
    /// before you can play it" — which is exactly what a retune or a capo is.</summary>
    public static bool NeedsAttention(string? tuningSummary, int? capo) =>
        IsNonStandardTuning(tuningSummary) || capo is > 0;

    /// <summary>The ONE fixed-width tuning cell a library row prints: the tuning
    /// with the capo folded in ("E Standard · Capo 2"). They share a cell rather
    /// than a column each because they answer one question — what do I have to do
    /// to the guitar before I press play — and one cell is what keeps the column
    /// alignment down the whole list.</summary>
    public static string TuningCell(string? tuningSummary, int? capo, ILocalizationService localization)
    {
        var tuning = TuningDisplay(tuningSummary);
        var capoLabel = CapoLabel(capo, localization);
        if (capoLabel.Length == 0) return tuning;
        return tuning.Length == 0 ? capoLabel : $"{tuning} · {capoLabel}";
    }

    /// <summary>Splits a comma-joined arrangement summary into its distinct,
    /// order-preserving, trimmed labels. Empty in ⇒ empty out.</summary>
    public static IReadOnlyList<string> ParseArrangementBadges(string? summary)
    {
        if (string.IsNullOrWhiteSpace(summary)) return [];

        var seen = new List<string>();
        foreach (var part in summary.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var label = part.Trim();
            if (label.Length == 0) continue;
            if (!seen.Contains(label, StringComparer.OrdinalIgnoreCase)) seen.Add(label);
        }

        return seen;
    }

    /// <summary>The LIST-ROW spelling of the badges: one single-letter chip per
    /// arrangement, keeping the full word as its tooltip. The initial comes from
    /// the CATALOGUE's label (Rocksmith's path names are English), so it does not
    /// move with the UI language.</summary>
    public static IReadOnlyList<ArrangementChip> Chips(string? summary) => Chips(ParseArrangementBadges(summary));

    /// <summary><see cref="Chips(string?)" /> over labels already parsed.</summary>
    public static IReadOnlyList<ArrangementChip> Chips(IReadOnlyList<string> badges)
    {
        if (badges.Count == 0) return [];

        var chips = new List<ArrangementChip>(badges.Count);
        foreach (var label in badges)
            chips.Add(new ArrangementChip(char.ToUpperInvariant(label[0]).ToString(), label));
        return chips;
    }
}
