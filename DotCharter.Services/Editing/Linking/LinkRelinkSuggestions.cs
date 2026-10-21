using DotCharter.Core.Linking;

namespace DotCharter.Services.Editing.Linking;

/// <summary>
///     Runs that could join a link group again: in an arrangement that may share with the group,
///     over measures with the group's geometry, held by no group, and equal to the group's copies
///     under the group's own rule (for example a placement unlinked with "Only here" whose edit
///     was later taken back by hand). Runs are taken left to right without overlapping each other.
///     Builds one content index per compatible arrangement: call it when the link panel opens or
///     re-reads, never per frame.
/// </summary>
public static class LinkRelinkSuggestions
{
    public static IReadOnlyList<LinkRunRequest> Find(ChartProject project, LinkGroup group)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(group);
        if (Reference.Of(project, group) is not { } reference) return [];

        var found = new List<LinkRunRequest>();
        var taken = project.LinkGroups.SelectMany(g => g.Placements).ToList();
        var count = reference.Count;
        foreach (var arrangement in project.Arrangements)
        {
            if (!LinkCompatibility.CanShare(reference.Arrangement, arrangement)) continue;
            var index = LinkRuns.IndexFor(project, arrangement, reference.Rule, reference.Indexes);
            var measures = index.Measures;
            for (var m = 0; m + count <= measures.Count; m++)
            {
                var run = new LinkRunRequest(arrangement, measures[m].Start, measures[m + count - 1].End);
                if (Overlaps(taken, run) || Overlaps(found, run)) continue;
                if (!reference.Matches(measures, index, m)) continue;
                found.Add(run);
                m += count - 1;
            }
        }

        return found;
    }

    /// <summary>
    ///     Does <paramref name="run" /> still qualify for <paramref name="group" />: a live, compatible
    ///     arrangement, whole measures with the group's geometry, no overlap with any placement, and
    ///     equal content under the group's rule? Checked again right before a run is added, because
    ///     the chart may have changed since it was suggested.
    /// </summary>
    public static bool Qualifies(ChartProject project, LinkGroup group, LinkRunRequest run)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(group);
        ArgumentNullException.ThrowIfNull(run);
        if (!project.Arrangements.Contains(run.Arrangement)) return false;
        if (Reference.Of(project, group) is not { } reference) return false;
        if (!LinkCompatibility.CanShare(reference.Arrangement, run.Arrangement)) return false;
        if (Overlaps(project.LinkGroups.SelectMany(g => g.Placements), run)) return false;

        var index = LinkRuns.IndexFor(project, run.Arrangement, reference.Rule, reference.Indexes);
        var probe = new LinkPlacement { Arrangement = run.Arrangement, Position = run.Start, EndPosition = run.End };
        return LinkRuns.TryResolve(index.Measures, probe, out var first, out var count) && count == reference.Count &&
               reference.Matches(index.Measures, index, first);
    }

    private static bool Overlaps(IEnumerable<LinkPlacement> placements, LinkRunRequest run) =>
        placements.Any(p => ReferenceEquals(p.Arrangement, run.Arrangement) && p.Position < run.End && run.Start < p.EndPosition);

    private static bool Overlaps(IEnumerable<LinkRunRequest> runs, LinkRunRequest run) =>
        runs.Any(r => ReferenceEquals(r.Arrangement, run.Arrangement) && r.Start < run.End && run.Start < r.End);

    /// <summary>What a run must match: the group's first live placement — its key, geometry and length.</summary>
    private sealed record Reference(Arrangement Arrangement, MeasureContentOptions Rule, MeasureContentKey Key,
        string Geometry, int Count, Dictionary<Arrangement, MeasureContentIndex> Indexes)
    {
        public static Reference? Of(ChartProject project, LinkGroup group)
        {
            if (LinkRuns.RuleOf(group) is not { } rule) return null;
            var indexes = new Dictionary<Arrangement, MeasureContentIndex>();
            foreach (var placement in group.Placements)
            {
                if (!LinkRuns.IsLive(project, placement)) continue;
                var index = LinkRuns.IndexFor(project, placement.Arrangement!, rule, indexes);
                if (!LinkRuns.TryResolve(index.Measures, placement, out var first, out var count)) continue;
                var key = index.KeyFor(first, count);
                if (key.IsEmpty || LinkRuns.Geometry(index.Measures, placement) is not { } geometry) continue;
                return new Reference(placement.Arrangement!, rule, key, geometry, count, indexes);
            }

            return null;
        }

        public bool Matches(IReadOnlyList<MeasureSpan> measures, MeasureContentIndex index, int first)
        {
            var probe = new LinkPlacement
            {
                Arrangement = index.Arrangement, Position = measures[first].Start, EndPosition = measures[first + Count - 1].End
            };
            return LinkRuns.Geometry(measures, probe) == Geometry && index.KeyFor(first, Count).Equals(Key);
        }
    }
}
