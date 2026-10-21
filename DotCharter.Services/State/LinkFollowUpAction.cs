using DotCharter.Core.Linking;

namespace DotCharter.Services.State;

/// <summary>
///     What linked measures did after one history step: the copies propagated to other placements
///     and the groups as they were before and after (unlinks, splits, dissolved groups). The
///     history keeps it IN the step's node, so undo reverts it first (then the edit), redo
///     re-applies it last (after the edit), and neither ever reconciles again.
///     <para>
///         A gesture's ticks fold into one node; their follow-ups fold the same way
///         (<see cref="Absorb" />): the groups' "before" stays the first tick's, the copies
///         accumulate in order, and undo reverts them newest first.
///     </para>
/// </summary>
public sealed class LinkFollowUpAction : IChartAction, ILinkPolicyAction
{
    private readonly ChartProject _project;
    private readonly List<LinkGroup> _before;
    private readonly List<LinkCopyStep> _copies;
    private readonly List<LinkPropagationReport> _reports;
    private List<LinkGroup> _after;

    internal LinkFollowUpAction(ChartProject project, List<LinkGroup> before, List<LinkGroup> after,
        List<LinkCopyStep> copies, int propagated, int unlinked, IEnumerable<LinkPropagationReport> reports)
    {
        _project = project;
        _before = before;
        _after = after;
        _copies = [.. copies];
        _reports = [.. reports];
        Propagated = propagated;
        Unlinked = unlinked;
    }

    /// <summary>Per group: where the edit was made and which placements it was copied to.</summary>
    public IReadOnlyList<LinkPropagationReport> Reports => _reports;

    /// <summary>Placements the step's edit was copied to.</summary>
    public int Propagated { get; private set; }

    /// <summary>Placements the step unlinked (diverged, invalid, or whose arrangement went away).</summary>
    public int Unlinked { get; private set; }

    /// <summary>It only replays what the reconciler decided; it is never reconciled itself.</summary>
    public LinkPolicy LinkPolicy => LinkPolicy.Retain;

    public void Execute()
    {
        foreach (var copy in _copies) copy.Apply();

        Restore(_project, _after);
    }

    public void Undo()
    {
        for (var i = _copies.Count - 1; i >= 0; i--) _copies[i].Revert();

        Restore(_project, _before);
    }

    /// <summary>Folds a later tick's follow-up of the same gesture into this one.</summary>
    internal void Absorb(LinkFollowUpAction next)
    {
        _copies.AddRange(next._copies);
        foreach (var report in next._reports)
        {
            var known = _reports.FindIndex(r => r.GroupId == report.GroupId);
            if (known < 0) _reports.Add(report);
            else _reports[known] = _reports[known].Merge(report);
        }

        _after = next._after;
        Propagated += next.Propagated;
        Unlinked += next.Unlinked;
    }

    /// <summary>A detached copy of the project's link groups.</summary>
    internal static List<LinkGroup> Capture(ChartProject project) => [.. project.LinkGroups.Select(g => g.Clone())];

    /// <summary>Puts <paramref name="state" />'s groups back (as fresh clones, so the record is never aliased).</summary>
    internal static void Restore(ChartProject project, List<LinkGroup> state)
    {
        project.LinkGroups.Clear();
        foreach (var group in state) project.LinkGroups.Add(group.Clone());
    }

    /// <summary>
    ///     "Only here" for <paramref name="report" />: keeps the edit at its source, puts the
    ///     placements it was copied to back as they were, and unlinks the source placement.
    ///     Null when this step did not propagate into that group.
    /// </summary>
    internal IChartAction? OnlyHere(LinkPropagationReport report)
    {
        var steps = _copies.Where(c => c.GroupId == report.GroupId).ToList();
        if (steps.Count == 0) return null;
        var before = Capture(_project);
        var after = Capture(_project);
        if (after.FirstOrDefault(g => g.Id == report.GroupId) is { } group)
        {
            var source = group.Placements.FirstOrDefault(p =>
                ReferenceEquals(p.Arrangement, report.Source.Arrangement) && p.Position == report.Source.Position);
            if (source != null) group.Placements.Remove(source);
            if (group.Placements.Count < 2) after.Remove(group);
        }

        return new OnlyHereAction(_project, steps, before, after);
    }

    /// <summary>Undoes one group's propagated copies and unlinks the placement the edit was made in.</summary>
    private sealed class OnlyHereAction(ChartProject project, List<LinkCopyStep> steps, List<LinkGroup> before,
        List<LinkGroup> after) : IChartAction, ILinkPolicyAction
    {
        /// <summary>It sets the groups itself; the copies it restores were equal before the edit.</summary>
        public LinkPolicy LinkPolicy => LinkPolicy.Retain;

        public void Execute()
        {
            for (var i = steps.Count - 1; i >= 0; i--) steps[i].Revert();
            Restore(project, after);
        }

        public void Undo()
        {
            foreach (var step in steps) step.Apply();
            Restore(project, before);
        }
    }
}

/// <summary>
///     One group's propagation in one history step: the placement the edit was made in and the
///     placements it was copied to (detached clones; their arrangement is the live reference), and
///     the policy that copied it (an editing gesture, or a generator sharing its result).
/// </summary>
public sealed record LinkPropagationReport(
    string GroupId,
    string Letter,
    string Name,
    LinkPlacement Source,
    IReadOnlyList<LinkPlacement> Targets,
    LinkPolicy Policy = LinkPolicy.Propagate)
{
    /// <summary>A later tick of the same gesture: the first source stays, the targets add up.</summary>
    internal LinkPropagationReport Merge(LinkPropagationReport later) =>
        this with
        {
            Targets = [.. Targets.Concat(later.Targets.Where(t => !Targets.Any(k =>
                ReferenceEquals(k.Arrangement, t.Arrangement) && k.Position == t.Position)))]
        };
}

/// <summary>
///     One propagated copy and the clean-up pass over its target level that follows it. Applying
///     re-runs the pass (it is deterministic, so a redo lands where the do did) and records what it
///     changed OUTSIDE the copy; reverting puts that back and takes the copy out.
/// </summary>
internal sealed class LinkCopyStep(LinkedRunCopy copy, Action<ChartLevel> cleanUp, string groupId)
{
    private SpacingCollateral? _collateral;

    /// <summary>The link group the copy was made for.</summary>
    public string GroupId { get; } = groupId;

    public LinkedRunCopy Copy { get; } = copy;

    public void Apply()
    {
        Copy.Apply();
        using (var recorder = SpacingCollateralRecorder.Begin())
        {
            cleanUp(Copy.Level);
            _collateral = recorder.Build(Copy.AddedElements);
        }

        Copy.Target.MarkEdited();
    }

    public void Revert()
    {
        _collateral?.Restore();
        _collateral = null;
        Copy.Revert();
        Copy.Target.MarkEdited();
    }
}
