namespace DotCharter.UI.ViewModels.Panels;

/// <summary>One removable chip under the browser's filter strip: which filter it
/// stands for (<see cref="Kind" /> is a <see cref="BrowserFacet.Key" />, or the
/// two non-facet specials <c>"search"</c> / <c>"lessons"</c>), the canonical
/// VALUE its ✕ deselects, and the label it prints. The sibling of Play Mode's
/// <c>SongFilterChip</c>: the two screens share a grammar, not a filter set.</summary>
public sealed record BrowserFilterChip(string Kind, string Value, string Label);

/// <summary>
///     Pure projection of the browser's filter state into the chips the strip
///     shows — since the datagrid rework, one chip per SELECTED FACET OPTION
///     (multi-select: three tunings ticked = three chips, each removable on its
///     own) plus the search text and the non-default lessons toggle. Every chip
///     is derived from state the predicate actually reads, so a chip can never
///     claim a filter the list is not applying.
///     <para>
///         NON-DEFAULT state only: the lessons toggle defaults to TRUE, so the
///         chip it earns is "lessons shown", not "lessons hidden"; a facet with
///         nothing selected is inert and earns none.
///     </para>
/// </summary>
public static class BrowserFilterChips
{
    /// <summary>The two non-facet chip kinds.</summary>
    public const string SearchKind = "search";

    /// <inheritdoc cref="SearchKind" />
    public const string LessonsKind = "lessons";

    /// <summary>Guitarcade lesson entries are hidden by default — the resting
    /// state of <c>SongBrowserViewModel.HideLessons</c>, and therefore the state
    /// that must NOT produce a chip.</summary>
    public const bool HideLessonsDefault = true;

    /// <summary>The active chips: search first, then each facet's selected
    /// options in the facet's own option order (the popover's order, so the
    /// chips read the way the checkboxes do), then the lessons special.</summary>
    public static IReadOnlyList<BrowserFilterChip> Build(
        string search, IEnumerable<BrowserFacet> facets, bool hideLessons, ILocalizationService loc)
    {
        var chips = new List<BrowserFilterChip>();

        // Trimmed, because the predicate trims: "  " narrows nothing and must
        // not print a chip that claims it does.
        var trimmed = search.Trim();
        if (trimmed.Length > 0)
            chips.Add(new BrowserFilterChip(SearchKind, trimmed, trimmed));

        foreach (var facet in facets)
        {
            foreach (var option in facet.Options)
            {
                if (option.IsChecked)
                    chips.Add(new BrowserFilterChip(facet.Key, option.Value, option.Label));
            }
        }

        if (hideLessons != HideLessonsDefault)
            chips.Add(new BrowserFilterChip(LessonsKind, "", loc["SongBrowser_Chip_LessonsShown"]));

        return chips;
    }
}
