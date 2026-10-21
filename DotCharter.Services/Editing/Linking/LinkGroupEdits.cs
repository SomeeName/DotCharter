using DotCharter.Core.Linking;
using DotCharter.Services.State;

namespace DotCharter.Services.Editing.Linking;

/// <summary>
///     The link panel's group edits: unlink placements, make a new group from some of them,
///     split a group by arrangement, rename or delete a group, add a run that plays the same again.
///     Each is ONE undo step that only changes the groups (the copies are already equal, so
///     nothing is copied); null when the edit would change nothing.
/// </summary>
public static class LinkGroupEdits
{
    /// <summary>Takes <paramref name="placements" /> out of the group (a group left with one dissolves).</summary>
    public static IChartAction? Unlink(ChartProject project, string groupId, IEnumerable<LinkPlacement> placements) =>
        Edit(project, groupId, (groups, group) =>
        {
            var removed = Matching(group, placements);
            if (removed.Count == 0) return false;
            foreach (var placement in removed) group.Placements.Remove(placement);
            if (group.Placements.Count < 2) groups.Remove(group);
            return true;
        });

    /// <summary>
    ///     Moves <paramref name="placements" /> (two or more, not all of them) into a new group with
    ///     the next free letter; the rest keep the group.
    /// </summary>
    public static IChartAction? NewGroupFrom(ChartProject project, string groupId, IEnumerable<LinkPlacement> placements) =>
        Edit(project, groupId, (groups, group) =>
        {
            var moved = Matching(group, placements);
            if (moved.Count < 2 || moved.Count == group.Placements.Count) return false;
            var split = new LinkGroup { Letter = LinkRuns.NextLetter(groups), Name = group.Name, Rule = group.Rule };
            foreach (var placement in moved)
            {
                group.Placements.Remove(placement);
                split.Placements.Add(placement);
            }

            groups.Insert(groups.IndexOf(group) + 1, split);
            if (group.Placements.Count < 2) groups.Remove(group);
            return true;
        });

    /// <summary>
    ///     One group per arrangement: the first arrangement keeps the group, every other one with
    ///     two or more placements gets a new letter, and a placement alone in its arrangement is unlinked.
    /// </summary>
    public static IChartAction? SplitByArrangement(ChartProject project, string groupId) =>
        Edit(project, groupId, (groups, group) =>
        {
            var byArrangement = group.Placements.GroupBy(p => p.Arrangement).ToList();
            if (byArrangement.Count < 2) return false;
            var at = groups.IndexOf(group);
            foreach (var part in byArrangement.Skip(1))
            {
                foreach (var placement in part) group.Placements.Remove(placement);
                if (part.Count() < 2) continue;
                var split = new LinkGroup { Letter = LinkRuns.NextLetter(groups), Name = group.Name, Rule = group.Rule };
                foreach (var placement in part) split.Placements.Add(placement);
                groups.Insert(++at, split);
            }

            if (group.Placements.Count < 2) groups.Remove(group);
            return true;
        });

    /// <summary>
    ///     Adds <paramref name="run" /> to the group as a new placement (a re-link suggestion,
    ///     <see cref="LinkRelinkSuggestions" />). Null unless the run still qualifies — compatible,
    ///     the group's measures, overlapping no placement and equal under the group's rule — checked
    ///     against the chart as it is now. Like creating a group, the run's edges are normalised in the
    ///     same step (hand shapes split at its edges, the hand position pinned at its start).
    /// </summary>
    public static IChartAction? AddPlacement(ChartProject project, string groupId, LinkRunRequest run)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(run);
        if (project.LinkGroups.FirstOrDefault(g => g.Id == groupId) is not { } group) return null;
        if (!LinkRelinkSuggestions.Qualifies(project, group, run)) return null;

        var placement = new LinkPlacement { Arrangement = run.Arrangement, Position = run.Start, EndPosition = run.End };
        var before = LinkFollowUpAction.Capture(project);
        var after = LinkFollowUpAction.Capture(project);
        after.First(g => g.Id == groupId).Placements.Add(placement);
        return new AddLinkPlacementAction(project, before, after, LinkRunEdges.Plan([placement]), run.Arrangement);
    }

    /// <summary>Unlinks every placement of the group.</summary>
    public static IChartAction? Delete(ChartProject project, string groupId) =>
        Edit(project, groupId, (groups, group) => groups.Remove(group));

    /// <summary>Renames the group (a blank name is refused).</summary>
    public static IChartAction? Rename(ChartProject project, string groupId, string name) =>
        Edit(project, groupId, (_, group) =>
        {
            var trimmed = name.Trim();
            if (trimmed.Length == 0 || trimmed == group.Name) return false;
            group.Name = trimmed;
            return true;
        });

    private static IChartAction? Edit(ChartProject project, string groupId, Func<List<LinkGroup>, LinkGroup, bool> change)
    {
        ArgumentNullException.ThrowIfNull(project);
        var before = LinkFollowUpAction.Capture(project);
        var after = LinkFollowUpAction.Capture(project);
        if (after.FirstOrDefault(g => g.Id == groupId) is not { } group || !change(after, group)) return null;
        return new SetLinkGroupsAction(project, before, after);
    }

    /// <summary>The group's own placements that match <paramref name="wanted" /> by arrangement and start.</summary>
    private static List<LinkPlacement> Matching(LinkGroup group, IEnumerable<LinkPlacement> wanted)
    {
        var list = wanted.ToList();
        return
        [
            .. group.Placements.Where(p => list.Any(w =>
                ReferenceEquals(w.Arrangement, p.Arrangement) && w.Position == p.Position))
        ];
    }

    /// <summary>Swaps the project's link groups between two recorded states.</summary>
    private sealed class SetLinkGroupsAction(ChartProject project, List<LinkGroup> before, List<LinkGroup> after)
        : IChartAction, ILinkPolicyAction
    {
        /// <summary>It sets the groups itself; nothing is copied or compared.</summary>
        public LinkPolicy LinkPolicy => LinkPolicy.Retain;

        public void Execute() => Apply(after);

        public void Undo() => Apply(before);

        private void Apply(List<LinkGroup> state)
        {
            LinkFollowUpAction.Restore(project, state);
            foreach (var arrangement in state.Concat(before).SelectMany(g => g.Placements).Select(p => p.Arrangement).Distinct())
                arrangement?.MarkEdited();
        }
    }

    /// <summary>Adds one placement to a group and normalises its edges; undo takes both back.</summary>
    private sealed class AddLinkPlacementAction(ChartProject project, List<LinkGroup> before, List<LinkGroup> after,
        List<LinkRunEdges.Edit> edges, Arrangement arrangement) : IChartAction, ILinkPolicyAction
    {
        /// <summary>The run already plays the same; nothing is copied, compared or unlinked.</summary>
        public LinkPolicy LinkPolicy => LinkPolicy.Retain;

        public void Execute()
        {
            LinkRunEdges.Apply(edges);
            LinkFollowUpAction.Restore(project, after);
            arrangement.MarkEdited();
        }

        public void Undo()
        {
            LinkFollowUpAction.Restore(project, before);
            LinkRunEdges.Undo(edges);
            arrangement.MarkEdited();
        }
    }
}
