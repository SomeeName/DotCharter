namespace DotCharter.Presentation.Screens.Settings;

public sealed record SettingsSearchRow(string Key, string Label, string? Description = null);

/// <summary>A titled group of rows; a group with no rows is one setting whose label IS its title.</summary>
public sealed record SettingsSearchGroup(string Title, string? Subtitle, IReadOnlyList<SettingsSearchRow> Rows);

public sealed record SettingsSearchSource(string TabKey, string TabName, IReadOnlyList<SettingsSearchGroup> Groups);

/// <summary>A hit: the row's label, its group (null for a rowless group) and the tab it lives on.</summary>
public sealed record SettingsSearchHit(string Label, string? GroupName, string TabKey, string TabName, string? RowKey);

/// <summary>
///     The cross-tab search over what each tab declares: a row matches on its
///     label or description, a group whose title or subtitle matches shows every
///     row in it, and a rowless group is reachable by its title.
/// </summary>
public static class SettingsSearch
{
    public const int MaxHits = 60;

    public static bool Matches(string? text, string query) =>
        !string.IsNullOrEmpty(text) && text.ToLowerInvariant().Contains(query.ToLowerInvariant(), StringComparison.Ordinal);

    public static bool GroupMatches(SettingsSearchGroup group, string query) =>
        Matches(group.Title, query) || Matches(group.Subtitle, query);

    public static bool RowMatches(SettingsSearchRow row, string query) =>
        Matches(row.Label, query) || Matches(row.Description, query);

    /// <summary>
    ///     What one tab hides for <paramref name="query" />: every row that does
    ///     not match (a matching group keeps all its rows), plus the key of every
    ///     group left with none. <paramref name="kept" /> says, per group, whether
    ///     it still shows a row. An empty query hides nothing.
    /// </summary>
    public static HashSet<string> HiddenKeys(
        IReadOnlyList<SettingsSearchGroup> groups, string query, Func<int, string> groupKey, out bool[] kept)
    {
        var hidden = new HashSet<string>(StringComparer.Ordinal);
        kept = new bool[groups.Count];
        for (var g = 0; g < groups.Count; g++)
        {
            var groupMatches = query.Length > 0 && GroupMatches(groups[g], query);
            foreach (var row in groups[g].Rows)
            {
                if (query.Length == 0 || groupMatches || RowMatches(row, query)) kept[g] = true;
                else hidden.Add(row.Key);
            }

            if (!kept[g]) hidden.Add(groupKey(g));
        }

        return hidden;
    }

    public static List<SettingsSearchHit> Hits(IEnumerable<SettingsSearchSource> sources, string query)
    {
        var hits = new List<SettingsSearchHit>();
        if (string.IsNullOrWhiteSpace(query)) return hits;

        foreach (var source in sources)
        {
            foreach (var group in source.Groups)
            {
                var groupMatches = GroupMatches(group, query);
                foreach (var row in group.Rows)
                {
                    if (hits.Count >= MaxHits) return hits;
                    if (groupMatches || RowMatches(row, query))
                        hits.Add(new SettingsSearchHit(row.Label, group.Title, source.TabKey, source.TabName, row.Key));
                }
            }

            foreach (var group in source.Groups)
            {
                if (hits.Count >= MaxHits) return hits;
                if (group.Rows.Count == 0 && Matches(group.Title, query))
                    hits.Add(new SettingsSearchHit(group.Title, null, source.TabKey, source.TabName, null));
            }
        }

        return hits;
    }
}
