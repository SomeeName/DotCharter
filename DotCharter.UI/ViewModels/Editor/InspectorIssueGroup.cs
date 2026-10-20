namespace DotCharter.UI.ViewModels.Editor;

/// <summary>
///     A severity-keyed bucket of <see cref="ArrangementIssue" />s for the FEHLER
///     inspector triage surface. Pure data — the view renders a header
///     (severity dot + localized label + count badge) and a click-to-jump list
///     per group. Ordering is fixed (Important before Warning) so the most
///     actionable problems always sit at the top.
/// </summary>
public sealed class InspectorIssueGroup
{
    public InspectorIssueGroup(ArrangementIssueSeverity severity, IReadOnlyList<ArrangementIssue> issues)
    {
        Severity = severity;
        Issues = issues;
    }

    public ArrangementIssueSeverity Severity { get; }

    public IReadOnlyList<ArrangementIssue> Issues { get; }

    public int Count => Issues.Count;

    public bool IsImportant => Severity == ArrangementIssueSeverity.Important;
}

/// <summary>
///     Pure severity-grouping for the FEHLER inspector. Deterministic and
///     UI-free so it can be unit-tested (<c>IssueGroupingTests</c>).
/// </summary>
public static class IssueGrouping
{
    /// <summary>
    ///     Buckets issues by severity. Groups are ordered Important first, then
    ///     Warning; empty severities are omitted; the original issue order is
    ///     preserved inside each group.
    /// </summary>
    public static IReadOnlyList<InspectorIssueGroup> Build(IEnumerable<ArrangementIssue> issues)
    {
        // Materialize once so we can partition without re-enumerating a lazy source.
        var all = issues as IReadOnlyList<ArrangementIssue> ?? issues.ToList();

        var groups = new List<InspectorIssueGroup>(2);
        // Fixed severity order — most actionable first. Iterating the enum in
        // this explicit order (rather than GroupBy) keeps the result stable.
        foreach (var severity in new[] { ArrangementIssueSeverity.Important, ArrangementIssueSeverity.Warning })
        {
            var bucket = all.Where(i => i.Severity == severity).ToList();
            if (bucket.Count > 0)
                groups.Add(new InspectorIssueGroup(severity, bucket));
        }

        return groups;
    }

    /// <summary>
    ///     The severity the FEHLER header's dot should show for
    ///     <paramref name="groups" /> — the worst one present, or
    ///     <see cref="ArrangementIssueSeverity.Warning" /> when there is nothing
    ///     to report.
    /// </summary>
    /// <remarks>
    ///     A named function rather than <c>groups[0].Severity</c> at the call
    ///     site, because the EMPTY case is the whole point: the dot must not bind
    ///     <c>IssueGroups[0].Severity</c> straight from the markup, and that
    ///     collection is cleared and refilled on every recompute — so the
    ///     all-clear state would produce an out-of-range binding error rather than a
    ///     value. "No issues" must never resolve to the danger colour, which is
    ///     why the fallback is the warning severity and not
    ///     <c>default</c>-whatever-the-enum-starts-at.
    /// </remarks>
    public static ArrangementIssueSeverity TopSeverity(IReadOnlyList<InspectorIssueGroup> groups)
        => groups.Count > 0 ? groups[0].Severity : ArrangementIssueSeverity.Warning;

    /// <summary>
    ///     The same answer straight from the issues — the worst severity present,
    ///     <see cref="ArrangementIssueSeverity.Warning" /> when there is nothing —
    ///     so the header dot does not need the groups built to be right. Defined
    ///     as "what <see cref="Build" /> + the groups overload would say", and
    ///     pinned to it by <c>IssueGroupingTests</c>.
    /// </summary>
    public static ArrangementIssueSeverity TopSeverity(IEnumerable<ArrangementIssue> issues)
    {
        foreach (var issue in issues)
        {
            if (issue.Severity == ArrangementIssueSeverity.Important)
                return ArrangementIssueSeverity.Important;
        }

        return ArrangementIssueSeverity.Warning;
    }
}
