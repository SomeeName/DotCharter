namespace DotCharter.UI.ViewModels.Editor.Inspector;

/// <summary>
///     One validation finding, projected onto the field it belongs to
///     (04-inspector-redesign.md §9).
///     <para>
///         <see cref="BlocksExport" /> is the difference between advice and a
///         wall, and it is shown as such: the chip reads danger and its tooltip
///         is prefixed "blocks export". A charter must not see <c>L15</c> and
///         <c>L01</c> as chips that block nothing and only learn they were
///         export ERRORS after pressing "check export readiness".
///     </para>
///     <para>
/// One row is one PROBLEM, not one note: the lint emits a finding
///         per chord note, so a three-note chord outside its anchor would print
///         the same sentence three times. <see cref="Fold" /> merges findings with
///         the same rule and sentence, and <see cref="Detail" /> names the strings
///         they came from.
///     </para>
/// </summary>
/// <param name="Field">Which control the marker sits on.</param>
/// <param name="BlocksExport">True when the export would refuse the package.</param>
/// <param name="Code">The short code (<c>L01</c>, <c>I34</c>, …) so the finding
/// is greppable against the external tools it mirrors.</param>
/// <param name="Message">The localized finding text.</param>
/// <param name="StringIndex">The offending string when exactly one string is
/// named — what puts a pip on the chord box's rail.</param>
/// <param name="Count">How many lint findings this row stands for.</param>
/// <param name="Detail">"— strings E (6), A (5)" when the row folds findings
/// on several strings; empty otherwise.</param>
public readonly record struct InspectorFieldIssue(
    InspectorField Field,
    bool BlocksExport,
    string Code,
    string Message,
    int? StringIndex,
    int Count = 1,
    string Detail = "")
{
    /// <summary>The localized export consequence the tooltip leads with; <see cref="Fold" />
    /// fills it from its localization.</summary>
    public string BlocksExportLabel { get; init; } = string.Empty;

    /// <summary>Tooltip text: the code, the message, and the export consequence
    /// FIRST when there is one.</summary>
    public string Tooltip => BlocksExport
        ? $"{BlocksExportLabel} · {Code} · {Message}{DetailSuffix}"
        : $"{Code} · {Message}{DetailSuffix}";

    private string DetailSuffix => Detail.Length == 0 ? string.Empty : " " + Detail;

    /// <summary>
    ///     The selection's findings, one row per (rule, sentence), in the order
    ///     the lint first reported each. Pure, so the default gate pins it.
    /// </summary>
    /// <param name="issues">The findings that back-reference the selection.</param>
    /// <param name="stringList">Names a set of strings
    /// (<see cref="GuitarStringLabel.List" /> over the arrangement's tuning).</param>
    /// <param name="localization">Supplies <c>Inspector_Issue_FoldedStringsFmt</c>.</param>
    public static IReadOnlyList<InspectorFieldIssue> Fold(
        IEnumerable<ArrangementIssue> issues,
        Func<IReadOnlyList<int>, string> stringList,
        ILocalizationService localization)
    {
        var groups = new List<(ArrangementIssue First, List<ArrangementIssue> All)>();
        var index = new Dictionary<(ArrangementIssueType, string), int>();
        foreach (var issue in issues)
        {
            var key = (issue.Type, issue.Message);
            if (index.TryGetValue(key, out var at))
            {
                groups[at].All.Add(issue);
                continue;
            }

            index[key] = groups.Count;
            groups.Add((issue, [issue]));
        }

        var rows = new InspectorFieldIssue[groups.Count];
        for (var g = 0; g < groups.Count; g++)
        {
            var (first, all) = groups[g];
            var strings = all.Where(i => i.StringIndex.HasValue).Select(i => i.StringIndex!.Value)
                .Distinct().Order().ToArray();
            var detail = strings.Length > 1
                ? localization.Format("Inspector_Issue_FoldedStringsFmt", stringList(strings))
                : string.Empty;
            rows[g] = new InspectorFieldIssue(
                InspectorIssueFieldMap.Map(first.Type),
                InspectorIssueFieldMap.BlocksExport(first.Type),
                first.Type.GetCode(),
                first.Message,
                strings.Length == 1 ? strings[0] : null,
                all.Count,
                detail)
            {
                BlocksExportLabel = localization["Inspector_Issue_BlocksExport"],
            };
        }

        return rows;
    }
}
