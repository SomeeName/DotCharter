using System.Diagnostics;
using DotCharter.Core.Linking;
using DotCharter.Services.Parsing.GuitarPro;
using DotCharter.Services.State;

namespace DotCharter.Services.Editing.Linking;

/// <summary>
///     One group the app proposes to link: <c>MeasureCount</c> measures at each of <c>Runs</c>, named
///     after the section the first run starts in. <c>FirstMeasures</c> holds the 0-based first measure of
///     each run, in the order of <c>Runs</c>; <c>FromRepeats</c> says it was found in the score's own
///     repeat structure rather than by comparing content.
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
}

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

        return [.. suggestions
            .OrderByDescending(s => s.MeasureCount)
            .ThenByDescending(s => s.AlignedEdges)
            .ThenByDescending(s => s.Runs.Count)
            .ThenByDescending(s => s.FromRepeats)];
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
    ///     its own letter; null when none can be linked any more. The project is left as it was: the
    ///     history runs the step.
    /// </summary>
    public static IChartAction? LinkAll(ChartProject project, IEnumerable<LinkSuggestion> suggestions,
        MeasureContentOptions rule)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(suggestions);
        var measures = MeasureGrid.Build(project.Beats);
        if (measures.Count == 0) return null;
        var edges = new Dictionary<Arrangement, AutoLinkEdges>();
        var verification = new Dictionary<Arrangement, AutoLinkVerification>();
        var actions = new List<IChartAction>();
        try
        {
            // Each group must see the ones before it, for its letter and the overlap check.
            foreach (var suggestion in suggestions)
            {
                if (suggestion.RequiresReview || !suggestion.Runs.All(r => Safe(r, edges, measures))) continue;
                if (!Verified(project, suggestion.Runs, measures, rule, verification)) continue;
                if (LinkGroupCreator.TryBuildWithoutEdgeEdits(project, suggestion.Runs, suggestion.Name, rule, out var action) !=
                    LinkCreateRefusal.None)
                {
                    continue;
                }

                action!.Execute();
                actions.Add(action);
            }
        }
        finally
        {
            for (var i = actions.Count - 1; i >= 0; i--) actions[i].Undo();
        }

        return actions.Count == 0 ? null : new CompositeAction(actions) { LabelKey = HistoryLabelKey };
    }

    private static bool Safe(LinkRunRequest run, Dictionary<Arrangement, AutoLinkEdges> edges,
        IReadOnlyList<MeasureSpan> measures)
    {
        if (!edges.TryGetValue(run.Arrangement, out var safe))
            edges[run.Arrangement] = safe = new AutoLinkEdges(run.Arrangement, measures);
        return safe.IsSafe(run.Start) && safe.IsSafe(run.End);
    }

    private static bool Verified(ChartProject project, IReadOnlyList<LinkRunRequest> runs,
        IReadOnlyList<MeasureSpan> measures, MeasureContentOptions rule, Dictionary<Arrangement, AutoLinkVerification> cache)
    {
        (MeasureContentKey Rule, MeasureContentKey Exact)? previous = null;
        foreach (var run in runs)
        {
            var placement = new LinkPlacement { Position = run.Start, EndPosition = run.End };
            if (!LinkRuns.TryResolve(measures, placement, out var first, out var count)) return false;
            if (!cache.TryGetValue(run.Arrangement, out var verification))
                cache[run.Arrangement] = verification = new AutoLinkVerification(run.Arrangement, project.Beats, rule);
            var key = verification.KeyFor(first, count);
            if (key.Rule.IsEmpty || previous is { } prior && !prior.Equals(key)) return false;
            previous = key;
        }

        return previous != null;
    }
}
