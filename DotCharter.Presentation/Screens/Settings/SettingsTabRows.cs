using DotCharter.Localization;

namespace DotCharter.Presentation.Screens.Settings;

/// <summary>One row a tab declares to the search: its key after the tab prefix, and the resx keys of its label and description.</summary>
public sealed record SettingsRowSpec(string Key, string LabelKey, string? DescriptionKey = null);

/// <summary>One group a tab declares: the resx keys of its title and subtitle, and its rows.</summary>
public sealed record SettingsGroupSpec(string TitleKey, string? SubtitleKey, params SettingsRowSpec[] Rows);

/// <summary>A tab's search catalogue as resx keys, localized on every read; its keys are the ones the view hides by.</summary>
public sealed class SettingsTabRows(string prefix, IReadOnlyList<SettingsGroupSpec> groups)
{
    public string Prefix { get; } = prefix;

    public IReadOnlyList<SettingsGroupSpec> Specs { get; } = groups;

    public string GroupKey(int index) => Prefix + "group." + index;

    public string RowKey(string key) => Prefix + key;

    public IReadOnlyList<SettingsSearchGroup> Localize(ILocalizationService loc) =>
    [
        .. Specs.Select(g => new SettingsSearchGroup(
            loc[g.TitleKey],
            g.SubtitleKey is null ? null : loc[g.SubtitleKey],
            [.. g.Rows.Select(r => new SettingsSearchRow(
                Prefix + r.Key, loc[r.LabelKey], r.DescriptionKey is null ? null : loc[r.DescriptionKey]))]))
    ];

    /// <summary>What <paramref name="query" /> hides; <paramref name="kept" /> says per group whether a row survived.</summary>
    public IReadOnlySet<string> Hidden(ILocalizationService loc, string query, out bool[] kept) =>
        SettingsSearch.HiddenKeys(Localize(loc), query, GroupKey, out kept);
}
