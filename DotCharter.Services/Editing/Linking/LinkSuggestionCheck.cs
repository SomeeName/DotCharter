using DotCharter.Core.Linking;
using DotCharter.Services.State;

namespace DotCharter.Services.Editing.Linking;

/// <summary>Why a link suggestion cannot be linked as shown.</summary>
public enum LinkMismatchKind
{
    /// <summary>The runs' content differs (<see cref="LinkMismatch.Difference" /> says where).</summary>
    Content,

    /// <summary>A run edge would cut a sound or hand shape crossing the barline.</summary>
    Edge,

    /// <summary><see cref="LinkGroupCreator.TryBuild" /> refuses it for another reason (<see cref="LinkMismatch.Refusal" />).</summary>
    Refused,

    /// <summary>A run does not qualify for the existing group it would join.</summary>
    Join
}

/// <summary>
///     Why a suggestion is a NEAR MATCH rather than linkable: <c>Measure</c> is the 0-based measure of the
///     chart where it shows (for content, where the first difference falls in the differing run);
///     <c>Difference</c> is that first difference, reference run first.
/// </summary>
public sealed record LinkMismatch(LinkMismatchKind Kind, int Measure)
{
    public MeasureContentDifference? Difference { get; init; }
    public LinkCreateRefusal Refusal { get; init; }
}

/// <summary>One suggestion <see cref="LinkSuggestionFinder.LinkAll" /> did not link, and why.</summary>
public sealed record LinkSkip(LinkSuggestion Suggestion, LinkMismatch Mismatch);

/// <summary>What <see cref="LinkSuggestionFinder.LinkAll" /> built (null when nothing) and every suggestion it skipped.</summary>
public sealed record LinkAllResult(IChartAction? Action, IReadOnlyList<LinkSkip> Skipped);

/// <summary>
///     The one "can these runs be linked as one new group?" answer shared by the suggestion finder and
///     <see cref="LinkSuggestionFinder.LinkAll" />: clean barline edges, <see cref="LinkGroupCreator.Check" /> (the
///     strict rules of <see cref="LinkGroupCreator.TryBuild" />), and the exact auto-link verification. Caches
///     per arrangement, so build one per chart state.
/// </summary>
internal sealed class LinkSuggestionCheck(ChartProject project, IReadOnlyList<MeasureSpan> measures, MeasureContentOptions rule)
{
    private readonly Dictionary<Arrangement, AutoLinkEdges> _edges = [];
    private readonly Dictionary<Arrangement, AutoLinkVerification> _verification = [];
    private readonly Dictionary<Arrangement, MeasureContentIndex> _indexes = [];

    public bool IsSafe(LinkRunRequest run)
    {
        if (!_edges.TryGetValue(run.Arrangement, out var safe))
            _edges[run.Arrangement] = safe = new AutoLinkEdges(run.Arrangement, measures);
        return safe.IsSafe(run.Start) && safe.IsSafe(run.End);
    }

    public int MeasureOf(LinkRunRequest run) =>
        LinkRuns.TryResolve(measures, new LinkPlacement { Position = run.Start, EndPosition = run.End }, out var first, out _) ? first : 0;

    /// <summary>Null when <paramref name="runs" /> can be linked as one group named <paramref name="name" />.</summary>
    public LinkMismatch? Check(IReadOnlyList<LinkRunRequest> runs, string name)
    {
        if (runs.FirstOrDefault(r => !IsSafe(r)) is { } cut) return new LinkMismatch(LinkMismatchKind.Edge, MeasureOf(cut));
        var refusal = LinkGroupCreator.Check(project, runs, name, rule, measures, _indexes, out _);
        if (refusal is not (LinkCreateRefusal.None or LinkCreateRefusal.DifferentContent))
            return new LinkMismatch(LinkMismatchKind.Refused, runs.Count > 0 ? MeasureOf(runs[0]) : 0) { Refusal = refusal };

        var keys = new List<(int First, MeasureContentKey Rule, MeasureContentKey Exact)>(runs.Count);
        foreach (var run in runs)
        {
            var placement = new LinkPlacement { Position = run.Start, EndPosition = run.End };
            if (!LinkRuns.TryResolve(measures, placement, out var first, out var count))
                return new LinkMismatch(LinkMismatchKind.Refused, 0) { Refusal = LinkCreateRefusal.NotWholeMeasures };
            if (!_verification.TryGetValue(run.Arrangement, out var verification))
                _verification[run.Arrangement] = verification = new AutoLinkVerification(run.Arrangement, project.Beats, rule);
            var (ruleKey, exact) = verification.KeyFor(first, count);
            keys.Add((first, ruleKey, exact));
        }

        foreach (var key in keys.Skip(1))
        {
            var difference = MeasureContentDifference.Between(keys[0].Rule, key.Rule) ??
                MeasureContentDifference.Between(keys[0].Exact, key.Exact);
            if (difference == null) continue;
            return new LinkMismatch(LinkMismatchKind.Content, key.First + difference.Measure)
            {
                Difference = difference,
                Refusal = LinkCreateRefusal.DifferentContent
            };
        }

        return null;
    }
}
