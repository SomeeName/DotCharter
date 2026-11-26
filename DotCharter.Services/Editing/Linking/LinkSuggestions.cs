using System.Diagnostics;
using DotCharter.Core.Linking;
using DotCharter.Services.Parsing.GuitarPro;
using DotCharter.Services.State;

namespace DotCharter.Services.Editing.Linking;

/// <summary>
///     One group the app proposes to link: <c>MeasureCount</c> measures at each of <c>Runs</c>, named
///     after the section the first run starts in. <c>FirstMeasures</c> holds the 0-based first measure of
///     each run, in the order of <c>Runs</c>; <c>FromRepeats</c> says it was found in the score's own
///     repeat structure rather than by comparing content. A suggestion with <c>Join</c> adds its runs to
///     that existing group instead of creating a new one. A suggestion with <c>RequiresReview</c> is a NEAR
///     MATCH: its runs almost play the same, <c>Mismatch</c> says where they first differ, and linking skips it.
/// </summary>
public sealed record LinkSuggestion(
    string Name,
    int MeasureCount,
    IReadOnlyList<LinkRunRequest> Runs,
    IReadOnlyList<int> FirstMeasures,
    bool FromRepeats)
{
    public int AlignedEdges { get; init; }
    public bool RequiresReview { get; init; }
    public LinkMismatch? Mismatch { get; init; }
    public LinkJoinTarget? Join { get; init; }
}

/// <summary>The existing group a suggestion's runs join: they play the same as its copies under its rule.</summary>
public sealed record LinkJoinTarget(string GroupId, string Letter);

public enum LinkDetectionScope
{
    UnlinkedOnly,
    All
}

/// <summary>
///     Finds what can be linked in arrangements that were just imported, and links it as one undo step.
///     <para>
///         Searches compatible arrangements for the longest repeated runs, with clean barline edges.
///         Each ranked proposal includes all free copies and records its supporting boundaries and source repeats.
///     </para>
/// </summary>
public static class LinkSuggestionFinder
{
    public const string HistoryLabelKey = "History_LinkSuggestions";

    /// <summary>The comparison rule every app-created group uses under <paramref name="config" />.</summary>
    public static MeasureContentOptions RuleFor(AppConfig config) =>
        MeasureContentOptions.Detection with { Spacing = ChartSpacingEnforcer.SpacingGapOf(config) };

    /// <summary>Detects ranked runs in one arrangement without changing the chart.</summary>
    public static IReadOnlyList<LinkSuggestion> Detect(ChartProject project, Arrangement arrangement,
        LinkDetectionScope scope, MeasureContentOptions rule) => Find(project, [arrangement], null, rule, scope);

    /// <summary>
    ///     The suggestions in <paramref name="arrangements" /> (those the import added).
    ///     <paramref name="sourceBars" /> holds, per measure of the project's grid, the written bar it
    ///     plays (-1 unknown). Null uses the project's saved provenance if still aligned.
    /// </summary>
    public static IReadOnlyList<LinkSuggestion> Find(ChartProject project, IReadOnlyList<Arrangement> arrangements,
        IReadOnlyList<int>? sourceBars, MeasureContentOptions rule) => Find(project, arrangements, sourceBars, rule, CancellationToken.None);

    public static IReadOnlyList<LinkSuggestion> Find(ChartProject project, IReadOnlyList<Arrangement> arrangements,
        IReadOnlyList<int>? sourceBars, MeasureContentOptions rule, CancellationToken cancellation, Action<string>? report = null) =>
        Find(project, arrangements, sourceBars, rule, LinkDetectionScope.UnlinkedOnly, new LinkDetectionWork(cancellation), report);

    internal static IReadOnlyList<LinkSuggestion> Find(ChartProject project, IReadOnlyList<Arrangement> arrangements,
        IReadOnlyList<int>? sourceBars, MeasureContentOptions rule, LinkDetectionScope scope, LinkDetectionWork? work = null,
        Action<string>? report = null)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(arrangements);
        ArgumentNullException.ThrowIfNull(rule);
        work ??= new LinkDetectionWork(CancellationToken.None);
        work.Charge(0);
        var measures = MeasureGrid.Build(project.Beats);
        var classes = arrangements
            .Where(a => project.Arrangements.Contains(a) && LinkCompatibility.Signature(a) != null)
            .Distinct()
            .GroupBy(a => LinkCompatibility.Signature(a)!, StringComparer.Ordinal)
            .Select(g => g.ToList())
            .ToList();
        if (measures.Count == 0 || classes.Count == 0) return [];

        IReadOnlyList<GpSourceBar> provenance = sourceBars is { Count: 0 } || !GpSourceMap.Matches(project.GpSourceBars, measures)
            ? [] : project.GpSourceBars;
        var source = sourceBars ?? (provenance.Count > 0 ? provenance.Select(b => b.WrittenBar).ToArray() : null);
        if (source?.Count != measures.Count) source = null;
        var suggestions = new List<LinkSuggestion>();
        foreach (var part in classes)
        {
            if (!WithinBudget(part.Count, measures.Count))
            {
                Report(report, $"Link detection skipped a compatibility class ({part.Count} arrangements, {measures.Count} measures) over MaxPairs ({LinkCandidateDetector.MaxPairs}).");
                continue;
            }

            try
            {
                work.Charge((int)((long)part.Count * measures.Count));
                suggestions.AddRange(new RankedLinkSuggestions(project, measures, part, source, provenance, rule, scope).Find(work));
            }
            catch (LinkDetectionWork.LimitReached)
            {
                Report(report, "Link detection reached its search limit. Returning the verified proposals found so far.");
                break;
            }
        }

        var joins = JoinSuggestions(project, arrangements, measures, work);
        var joined = joins.SelectMany(j => j.Runs).ToHashSet();
        var check = new LinkSuggestionCheck(project, measures, rule);
        return [.. suggestions
            .Select(s => WithoutJoined(s, joined))
            .OfType<LinkSuggestion>()
            .Select(s => Strict(s, check))
            .Concat(joins.SelectMany(j => StrictJoin(project, j, check)))
            .OrderByDescending(s => s.MeasureCount)
            .ThenByDescending(s => s.Join != null)
            .ThenByDescending(s => s.AlignedEdges)
            .ThenByDescending(s => s.Runs.Count)
            .ThenByDescending(s => s.FromRepeats)];
    }

    /// <summary>
    ///     One "join group X" suggestion per existing group with unlinked runs in <paramref name="arrangements" />
    ///     that play the same as its copies under its own rule (<see cref="LinkRelinkSuggestions" />).
    /// </summary>
    private static List<LinkSuggestion> JoinSuggestions(ChartProject project, IReadOnlyList<Arrangement> arrangements,
        IReadOnlyList<MeasureSpan> measures, LinkDetectionWork work)
    {
        var joins = new List<LinkSuggestion>();
        foreach (var group in project.LinkGroups.ToList())
        {
            work.Charge(0);
            var runs = LinkRelinkSuggestions.Find(project, group).Where(r => arrangements.Contains(r.Arrangement)).ToList();
            var firsts = new List<int>();
            var count = 0;
            foreach (var run in runs)
            {
                var placement = new LinkPlacement { Position = run.Start, EndPosition = run.End };
                if (!LinkRuns.TryResolve(measures, placement, out var first, out count)) break;
                firsts.Add(first);
            }

            if (runs.Count == 0 || firsts.Count != runs.Count) continue;
            joins.Add(new LinkSuggestion(group.Name, count, runs, firsts, FromRepeats: false)
            {
                Join = new LinkJoinTarget(group.Id, group.Letter)
            });
        }

        return joins;
    }

    /// <summary>
    ///     <paramref name="suggestion" /> as linking will treat it: one <see cref="LinkGroupCreator.TryBuild" /> or the
    ///     verification would refuse becomes a near match naming its first difference, never a plain suggestion.
    /// </summary>
    private static LinkSuggestion Strict(LinkSuggestion suggestion, LinkSuggestionCheck check) =>
        check.Check(suggestion.Runs, suggestion.Name) is { } mismatch
            ? suggestion with { RequiresReview = true, Mismatch = mismatch }
            : suggestion;

    /// <summary>
    ///     <paramref name="join" /> as <see cref="LinkAll" /> will treat it: the runs it links stay a plain join, and the
    ///     runs whose edge cuts a sound or hand shape crossing the barline, or that do not qualify for the group, become a
    ///     near match naming the first of them.
    /// </summary>
    private static IEnumerable<LinkSuggestion> StrictJoin(ChartProject project, LinkSuggestion join, LinkSuggestionCheck check)
    {
        var good = new List<int>();
        var bad = new List<int>();
        LinkMismatch? mismatch = null;
        for (var i = 0; i < join.Runs.Count; i++)
        {
            var run = join.Runs[i];
            var kind = !check.IsSafe(run) ? LinkMismatchKind.Edge
                : LinkGroupEdits.AddPlacementWithoutEdgeEdits(project, join.Join!.GroupId, run) == null ? LinkMismatchKind.Join
                : (LinkMismatchKind?)null;
            if (kind == null)
            {
                good.Add(i);
                continue;
            }

            bad.Add(i);
            mismatch ??= new LinkMismatch(kind.Value, check.MeasureOf(run));
        }

        if (bad.Count == 0) return [join];
        var near = Subset(join, bad) with { RequiresReview = true, Mismatch = mismatch };
        return good.Count == 0 ? [near] : [Subset(join, good), near];
    }

    private static LinkSuggestion Subset(LinkSuggestion suggestion, List<int> keep) => suggestion with
    {
        Runs = [.. keep.Select(i => suggestion.Runs[i])],
        FirstMeasures = [.. keep.Select(i => suggestion.FirstMeasures[i])]
    };

    /// <summary>The near matches among <paramref name="suggestions" />: shown with their difference, never linked.</summary>
    public static IEnumerable<LinkSuggestion> NearMatches(IEnumerable<LinkSuggestion> suggestions) =>
        suggestions.Where(s => s.RequiresReview);

    /// <summary>A new-group suggestion without the runs that join an existing group; null when fewer than two remain.</summary>
    private static LinkSuggestion? WithoutJoined(LinkSuggestion suggestion, HashSet<LinkRunRequest> joined)
    {
        var keep = Enumerable.Range(0, suggestion.Runs.Count).Where(i => !joined.Contains(suggestion.Runs[i])).ToList();
        if (keep.Count == suggestion.Runs.Count) return suggestion;
        return keep.Count < 2 ? null : Subset(suggestion, keep);
    }

    private static void Report(Action<string>? report, string message)
    {
        if (report != null) report(message);
        else Trace.TraceWarning(message);
    }

    private static bool WithinBudget(int parts, int measures)
    {
        var count = (long)parts * measures + parts - 1;
        return count <= 1 || count <= (2L * LinkCandidateDetector.MaxPairs + 1) / (count - 1);
    }

    /// <summary>
    ///     Links every suggestion as ONE undo step (a <see cref="CompositeAction" />), each group with
    ///     its own letter; a join suggestion adds each run that still qualifies to its existing group.
    ///     The action is null when none can be linked any more; every suggestion (or join run) it did not
    ///     link is in <see cref="LinkAllResult.Skipped" /> with the reason, never dropped silently. The
    ///     project is left as it was: the history runs the step.
    /// </summary>
    public static LinkAllResult LinkAll(ChartProject project, IEnumerable<LinkSuggestion> suggestions,
        MeasureContentOptions rule)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(suggestions);
        var measures = MeasureGrid.Build(project.Beats);
        var skipped = new List<LinkSkip>();
        if (measures.Count == 0)
        {
            skipped.AddRange(suggestions.Select(s => new LinkSkip(s, new LinkMismatch(LinkMismatchKind.Refused, 0)
            {
                Refusal = LinkCreateRefusal.NotWholeMeasures
            })));
            return new LinkAllResult(null, skipped);
        }

        var check = new LinkSuggestionCheck(project, measures, rule);
        var actions = new List<IChartAction>();
        try
        {
            // Each group must see the ones before it, for its letter and the overlap check.
            foreach (var suggestion in suggestions)
            {
                if (suggestion.Join is { } join)
                {
                    if (suggestion.RequiresReview)
                    {
                        skipped.Add(new LinkSkip(suggestion, suggestion.Mismatch ?? new LinkMismatch(LinkMismatchKind.Join, suggestion.FirstMeasures.FirstOrDefault())));
                        continue;
                    }

                    foreach (var run in suggestion.Runs)
                    {
                        if (!check.IsSafe(run) || LinkGroupEdits.AddPlacementWithoutEdgeEdits(project, join.GroupId, run) is not { } add)
                        {
                            skipped.Add(new LinkSkip(suggestion, new LinkMismatch(LinkMismatchKind.Join, check.MeasureOf(run))));
                            continue;
                        }

                        add.Execute();
                        actions.Add(add);
                    }

                    continue;
                }

                var mismatch = check.Check(suggestion.Runs, suggestion.Name);
                if (mismatch == null && suggestion.RequiresReview)
                    mismatch = suggestion.Mismatch ?? new LinkMismatch(LinkMismatchKind.Content, suggestion.FirstMeasures.FirstOrDefault());
                if (mismatch == null)
                {
                    var refusal = LinkGroupCreator.TryBuildWithoutEdgeEdits(project, suggestion.Runs, suggestion.Name, rule, out var action);
                    if (refusal == LinkCreateRefusal.None)
                    {
                        action!.Execute();
                        actions.Add(action);
                        continue;
                    }

                    mismatch = new LinkMismatch(LinkMismatchKind.Refused, suggestion.FirstMeasures.FirstOrDefault()) { Refusal = refusal };
                }

                skipped.Add(new LinkSkip(suggestion, mismatch));
            }
        }
        finally
        {
            for (var i = actions.Count - 1; i >= 0; i--) actions[i].Undo();
        }

        return new LinkAllResult(actions.Count == 0 ? null : new CompositeAction(actions) { LabelKey = HistoryLabelKey }, skipped);
    }
}
