using DotCharter.Core.Linking;
using DotCharter.Services.State;

namespace DotCharter.Services.Editing.Linking;

/// <summary>One run to link: whole measures <c>[Start, End)</c> of <paramref name="Arrangement" />'s hardest level.</summary>
public sealed record LinkRunRequest(Arrangement Arrangement, FractionalPosition Start, FractionalPosition End);

/// <summary>Why <see cref="LinkGroupCreator.TryBuild" /> refused.</summary>
public enum LinkCreateRefusal
{
    None,
    TooFewRuns,
    ArrangementNotInProject,
    IncompatibleArrangements,
    NotWholeMeasures,
    DifferentMeasures,
    Overlap,
    Empty,
    DifferentContent
}

/// <summary>
///     Creates a link group from runs that already hold the same content: the strict rules
///     are checked here — at least two runs, whole measures with the same time-signature
///     sequence, arrangements that may share (<see cref="LinkCompatibility" />: same strings,
///     tuning and capo, never bass with guitar, never play-only), no overlap with each other, no
///     partial overlap with any existing placement (a run may sit whole inside another group's
///     placement, or hold one whole: <see cref="LinkRuns.Conflict" />), not empty, and equal under the comparison rule. The action
///     also normalises the runs' edges, as one undo step: a hand shape crossing a run edge is
///     split there, and the fret-hand position in effect at each run start is pinned with an
///     anchor.
/// </summary>
public static class LinkGroupCreator
{
    public static LinkCreateRefusal TryBuild(ChartProject project, IReadOnlyList<LinkRunRequest> runs, string name,
        MeasureContentOptions rule, out IChartAction? action) =>
        Build(project, runs, name, rule, normaliseEdges: true, out action);

    internal static LinkCreateRefusal TryBuildWithoutEdgeEdits(ChartProject project, IReadOnlyList<LinkRunRequest> runs,
        string name, MeasureContentOptions rule, out IChartAction? action) =>
        Build(project, runs, name, rule, normaliseEdges: false, out action);

    private static LinkCreateRefusal Build(ChartProject project, IReadOnlyList<LinkRunRequest> runs, string name,
        MeasureContentOptions rule, bool normaliseEdges, out IChartAction? action)
    {
        ArgumentNullException.ThrowIfNull(project);
        action = null;
        var refusal = Check(project, runs, name, rule, MeasureGrid.Build(project.Beats), [], out var group);
        if (refusal != LinkCreateRefusal.None) return refusal;
        var placements = group!.Placements.ToList();
        action = new CreateLinkGroupAction(project, group, normaliseEdges ? LinkRunEdges.Plan(placements) : []);
        return LinkCreateRefusal.None;
    }

    /// <summary>
    ///     The strict rules <see cref="TryBuild" /> applies, without building the action: link suggestions run the
    ///     same check so they never offer what linking refuses. <paramref name="indexes" /> caches one content
    ///     index per arrangement across calls on one chart state; <paramref name="group" /> is the group to add.
    /// </summary>
    internal static LinkCreateRefusal Check(ChartProject project, IReadOnlyList<LinkRunRequest> runs, string name,
        MeasureContentOptions rule, IReadOnlyList<MeasureSpan> measures, Dictionary<Arrangement, MeasureContentIndex> indexes,
        out LinkGroup? group)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(runs);
        ArgumentNullException.ThrowIfNull(rule);
        group = null;
        if (runs.Count < 2) return LinkCreateRefusal.TooFewRuns;
        if (runs.Any(r => !project.Arrangements.Contains(r.Arrangement))) return LinkCreateRefusal.ArrangementNotInProject;
        if (runs.Any(r => !LinkCompatibility.CanShare(runs[0].Arrangement, r.Arrangement)))
            return LinkCreateRefusal.IncompatibleArrangements;

        var placements = runs.Select(r => new LinkPlacement { Arrangement = r.Arrangement, Position = r.Start, EndPosition = r.End }).ToList();
        var geometry = placements.Select(p => LinkRuns.Geometry(measures, p)).ToList();
        if (geometry.Any(g => g == null)) return LinkCreateRefusal.NotWholeMeasures;
        if (geometry.Distinct(StringComparer.Ordinal).Count() > 1) return LinkCreateRefusal.DifferentMeasures;

        var existing = project.LinkGroups.SelectMany(g => g.Placements).ToList();
        foreach (var p in placements)
        {
            if (placements.Any(t => !ReferenceEquals(t, p) && Conflict(t, p, sameGroup: true)) ||
                existing.Any(t => Conflict(t, p, sameGroup: false)))
            {
                return LinkCreateRefusal.Overlap;
            }
        }

        var candidate = new LinkGroup { Letter = LinkRuns.NextLetter(project), Name = name, Rule = rule.Header() };
        foreach (var placement in placements) candidate.Placements.Add(placement);
        var keys = LinkRuns.Keys(project, candidate, rule, indexes);
        if (keys.Values.Any(k => k is null or { IsEmpty: true })) return LinkCreateRefusal.Empty;
        if (keys.Values.Select(k => k!.Canonical).Distinct(StringComparer.Ordinal).Count() > 1)
            return LinkCreateRefusal.DifferentContent;

        group = candidate;
        return LinkCreateRefusal.None;
    }

    private static bool Conflict(LinkPlacement a, LinkPlacement b, bool sameGroup) =>
        ReferenceEquals(a.Arrangement, b.Arrangement) &&
        LinkRuns.Conflict(a.Position, a.EndPosition, b.Position, b.EndPosition, sameGroup);

    /// <summary>Normalises the run edges and adds the group; undo takes both back.</summary>
    private sealed class CreateLinkGroupAction(ChartProject project, LinkGroup group, List<LinkRunEdges.Edit> edits)
        : IChartAction, ILinkPolicyAction
    {
        /// <summary>The new group is read as it is; nothing is propagated or unlinked by creating it.</summary>
        public LinkPolicy LinkPolicy => LinkPolicy.Retain;

        public void Execute()
        {
            LinkRunEdges.Apply(edits);
            project.LinkGroups.Add(group);
            foreach (var placement in group.Placements) placement.Arrangement?.MarkEdited();
        }

        public void Undo()
        {
            project.LinkGroups.Remove(group);
            LinkRunEdges.Undo(edits);
            foreach (var placement in group.Placements) placement.Arrangement?.MarkEdited();
        }
    }
}
