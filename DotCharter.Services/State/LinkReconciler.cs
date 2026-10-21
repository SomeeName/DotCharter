using DotCharter.Core.Linking;

namespace DotCharter.Services.State;

/// <summary>
///     Keeps linked measures equal, from the history's single funnel: after every action
///     <see cref="HistoryService" /> commits, it compares each placement's copy with the copy it
///     held before the action and applies the action's <see cref="LinkPolicy" />. Whatever it
///     changes is returned as one <see cref="LinkFollowUpAction" /> that lives in the SAME
///     undo node as the edit, so one Ctrl+Z takes back the edit and its propagation together.
///     Undo and redo never reconcile; they restore a state that was consistent when it was
///     recorded, and the reconciler only re-reads it (<see cref="Rebuild" />).
///     <para>
///         The order inside one commit: the action (and its own clean-up post-pass) → drop dead
///         placements → unlink every placement whose measures changed (geometry) → compare the
///         copies under the group's rule → propagate or split → re-read each target and unlink
///         any copy that still does not match (a copy is never left linked and different).
///     </para>
///     <para>
///         Cost: only arrangements whose <see cref="Arrangement.GetRevision" /> moved are
///         re-read, one <see cref="MeasureContentIndex" /> each; a project with no groups costs
///         one count check.
///     </para>
/// </summary>
internal sealed class LinkReconciler(IAppLogger logger)
{
    private readonly Dictionary<LinkPlacement, MeasureContentKey?> _keys = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<LinkGroup, string?> _geometry = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<Arrangement, long> _revisions = new(ReferenceEqualityComparer.Instance);
    private ChartProject? _project;

    /// <summary>Forgets everything (a new project, a cleared history).</summary>
    public void Reset()
    {
        _project = null;
        _keys.Clear();
        _geometry.Clear();
        _revisions.Clear();
    }

    /// <summary>Makes sure the "before" state of the next action is known: re-reads the project
    /// when it is not the one last read.</summary>
    public void EnsureBaseline(ChartProject? project)
    {
        if (!ReferenceEquals(project, _project)) Rebuild(project);
    }

    /// <summary>Re-reads every copy of <paramref name="project" /> as the new "before" state, and logs
    /// copies that disagree (after an undo or redo that must not happen; nothing is changed).</summary>
    public void Rebuild(ChartProject? project)
    {
        Reset();
        _project = project;
        if (project == null || project.LinkGroups.Count == 0) return;

        var measures = MeasureGrid.Build(project.Beats);
        var indexes = new Dictionary<(Arrangement, string), MeasureContentIndex>();
        foreach (var group in project.LinkGroups)
        {
            var rule = RuleOf(group);
            var distinct = new HashSet<string>(StringComparer.Ordinal);
            foreach (var placement in group.Placements)
            {
                var key = Read(project, placement, rule, indexes);
                _keys[placement] = key;
                if (key != null) distinct.Add(key.Canonical);
            }

            _geometry[group] = MajorityGeometry(measures, group);
            if (distinct.Count > 1)
            {
                logger.Warning(AppLogCategory.History,
                    $"Link group {group.Letter} has {distinct.Count} different copies after an undo/redo; left as it is.");
            }
        }

        foreach (var arrangement in project.Arrangements) _revisions[arrangement] = arrangement.GetRevision();
    }

    /// <summary>
    ///     Applies <paramref name="action" />'s link policy to the chart it just changed. Returns the
    ///     follow-up (already applied) or null when the links needed nothing.
    /// </summary>
    public LinkFollowUpAction? Reconcile(ChartProject? project, IChartAction action)
    {
        if (project == null || project.LinkGroups.Count == 0)
        {
            if (!ReferenceEquals(project, _project) || _keys.Count > 0) Rebuild(project);
            return null;
        }

        var cold = !ReferenceEquals(project, _project);
        var policy = LinkPolicies.Of(action);
        var before = LinkFollowUpAction.Capture(project);
        var run = new Run(project, policy);
        try
        {
            foreach (var group in project.LinkGroups.ToList())
                ReconcileGroup(run, group, cold);
        }
        catch (Exception ex)
        {
            // A fault in the link layer must never cost the edit: put the copies and the groups
            // back as the action left them, and carry on without a follow-up.
            logger.Error(AppLogCategory.History, "Link reconcile failed; the edit is kept, its copies are not touched.", ex);
            for (var i = run.Copies.Count - 1; i >= 0; i--) run.Copies[i].Revert();
            LinkFollowUpAction.Restore(project, before);
            Rebuild(project);
            return null;
        }

        Rebuild(project);
        if (!run.Changed) return null;

        var followUp = new LinkFollowUpAction(project, before, LinkFollowUpAction.Capture(project), run.Copies,
            run.Propagated, run.Unlinked, run.Reports);
        logger.Info(AppLogCategory.History,
            $"Linked measures ({policy}): copied to {run.Propagated} placement(s), unlinked {run.Unlinked}.");
        return followUp;
    }

    private void ReconcileGroup(Run run, LinkGroup group, bool cold)
    {
        var project = run.Project;
        var rule = RuleOf(group);

        foreach (var placement in group.Placements.ToList())
        {
            if (LinkRuns.IsLive(project, placement)) continue;
            group.Placements.Remove(placement);
            run.Unlinked++;
        }

        // Arrangements that cannot share (a tuning, capo or string count changed), then
        // geometry — both under EVERY policy: a placement whose measures changed (a beat inserted
        // or removed inside it, a time signature) is not the same run any more.
        run.Unlinked += LinkRuns.DropIncompatible(group);
        var measures = run.Measures;
        var geometry = _geometry.TryGetValue(group, out var known) && !cold ? known : MajorityGeometry(measures, group);
        foreach (var placement in group.Placements.ToList())
        {
            if (LinkRuns.Geometry(measures, placement) == geometry && geometry != null) continue;
            group.Placements.Remove(placement);
            run.Unlinked++;
        }

        if (LinkRuns.DissolveIfAlone(project, group)) return;
        if (cold || !_geometry.ContainsKey(group))
        {
            // No "before" to compare with (a project this history has not read yet, or a group the
            // action just made): nothing can be propagated, but copies that already differ must not
            // stay linked.
            SplitIfCopiesDiffer(run, group, rule);
            return;
        }

        var now = new Dictionary<LinkPlacement, MeasureContentKey>();
        var changed = new List<LinkPlacement>();
        foreach (var placement in group.Placements)
        {
            var cached = _keys.GetValueOrDefault(placement);
            var moved = !_revisions.TryGetValue(placement.Arrangement!, out var revision) ||
                        revision != placement.Arrangement!.GetRevision() || cached == null;
            var key = moved ? Read(project, placement, rule, run.Indexes) : cached;
            if (key == null) continue;
            now[placement] = key;
            if (!key.Equals(cached)) changed.Add(placement);
        }

        if (changed.Count == 0 || run.Policy == LinkPolicy.Retain) return;

        // A generator's result is kept once per group: the first copy it changed, in group order.
        if (run.Policy == LinkPolicy.ShareFirstChanged)
        {
            Propagate(run, group, rule, changed[0], now);
            return;
        }

        var changedClasses = changed.Select(p => now[p].Canonical).Distinct(StringComparer.Ordinal).Count();
        if (run.Policy == LinkPolicy.Propagate && changedClasses == 1)
        {
            Propagate(run, group, rule, changed[0], now);
            return;
        }

        // Split by content: the copies the action did NOT change keep the group.
        var classOf = now.ToDictionary(p => p.Key, p => p.Value.Canonical);
        var unchanged = group.Placements.FirstOrDefault(p => now.ContainsKey(p) && !changed.Contains(p));
        var keep = unchanged != null ? classOf[unchanged] : LargestClass(group, classOf);
        if (classOf.Values.Distinct(StringComparer.Ordinal).Count() <= 1) return;
        run.Unlinked += LinkRuns.Partition(project, group, classOf, keep);
    }

    private void SplitIfCopiesDiffer(Run run, LinkGroup group, MeasureContentOptions rule)
    {
        var classOf = new Dictionary<LinkPlacement, string>();
        foreach (var placement in group.Placements)
        {
            if (Read(run.Project, placement, rule, run.Indexes) is { } key) classOf[placement] = key.Canonical;
        }

        if (classOf.Values.Distinct(StringComparer.Ordinal).Count() <= 1) return;
        logger.Warning(AppLogCategory.History,
            $"Link group {group.Letter}: its copies differ with no earlier state to compare; the odd ones are unlinked.");
        run.Unlinked += LinkRuns.Partition(run.Project, group, classOf, LargestClass(group, classOf));
    }

    private void Propagate(Run run, LinkGroup group, MeasureContentOptions rule, LinkPlacement source,
        Dictionary<LinkPlacement, MeasureContentKey> now)
    {
        var project = run.Project;
        var wanted = now[source];
        var sourceIndex = MeasureContentIndex.ForHardestLevel(source.Arrangement!, project.Beats, rule);
        var targets = group.Placements.Where(p => !ReferenceEquals(p, source) && now.TryGetValue(p, out var k) && !k.Equals(wanted)).ToList();
        foreach (var target in targets)
        {
            if (target.Arrangement!.Levels.MaxBy(l => l.DifficultyIndex) is not { } level) continue;
            var copy = LinkedRunCopier.Plan(sourceIndex, source.Position, source.EndPosition, target.Arrangement!, level,
                target.Position, project.Beats, rule);
            var step = new LinkCopyStep(copy, CleanUp(project, rule), group.Id);
            step.Apply();
            run.Copies.Add(step);
            run.Propagated++;
            // The source index reads the source arrangement; a target in the same arrangement
            // just changed it, so the next target must read it afresh.
            if (ReferenceEquals(target.Arrangement, source.Arrangement))
                sourceIndex = MeasureContentIndex.ForHardestLevel(source.Arrangement!, project.Beats, rule);
        }

        // Verify: a copy that still does not read as the source is unlinked, never left linked and different.
        run.Indexes.Clear();
        foreach (var target in targets)
        {
            var key = Read(project, target, rule, run.Indexes);
            if (key != null && key.Equals(Read(project, source, rule, run.Indexes))) continue;
            logger.Warning(AppLogCategory.History,
                $"Link group {group.Letter}: the copy at {target.Position} in '{target.Arrangement?.Name}' does not match after propagation; unlinked.");
            group.Placements.Remove(target);
            run.Unlinked++;
        }

        var reached = targets.Where(t => group.Placements.Contains(t)).Select(t => t.Clone()).ToList();
        if (reached.Count > 0) run.Reports.Add(new LinkPropagationReport(group.Id, group.Letter, group.Name, source.Clone(), reached, run.Policy));
        LinkRuns.DissolveIfAlone(project, group);
    }

    /// <summary>
    ///     The editor's spacing post-pass over a target level, at the gap the group's rule was made
    ///     with — the same pass every gesture runs over its own level, so a propagated copy is
    ///     cleaned up exactly as if it had been edited there.
    /// </summary>
    private static Action<ChartLevel> CleanUp(ChartProject project, MeasureContentOptions rule)
    {
        var config = new AppConfig
        {
            MinNoteDistanceMode = rule.Spacing.Mode switch
            {
                SpacingGapMode.Milliseconds => MinNoteDistanceMode.Milliseconds,
                SpacingGapMode.BeatFraction => MinNoteDistanceMode.BeatFraction,
                _ => MinNoteDistanceMode.MeasureFraction
            },
            MinNoteDistanceMs = rule.Spacing.Milliseconds,
            MinNoteDistanceDivisor = rule.Spacing.Divisor
        };
        return level => ChartSpacingEnforcer.EnforceLevel(level, project.Beats, config);
    }

    private static string? LargestClass(LinkGroup group, Dictionary<LinkPlacement, string> classOf) =>
        classOf.Count == 0
            ? null
            : classOf.GroupBy(c => c.Value, StringComparer.Ordinal)
                .OrderByDescending(g => g.Count())
                .ThenBy(g => g.Min(c => group.Placements.IndexOf(c.Key)))
                .First().Key;

    private static string? MajorityGeometry(IReadOnlyList<MeasureSpan> measures, LinkGroup group) =>
        group.Placements
            .Select(p => LinkRuns.Geometry(measures, p))
            .Where(g => g != null)
            .GroupBy(g => g, StringComparer.Ordinal)
            .OrderByDescending(g => g.Count())
            .Select(g => g.Key)
            .FirstOrDefault();

    private static MeasureContentOptions RuleOf(LinkGroup group) => LinkRuns.RuleOf(group) ?? MeasureContentOptions.Detection;

    private static MeasureContentKey? Read(ChartProject project, LinkPlacement placement, MeasureContentOptions rule,
        Dictionary<(Arrangement, string), MeasureContentIndex> indexes)
    {
        if (!LinkRuns.IsLive(project, placement)) return null;
        var cacheKey = (placement.Arrangement!, rule.Header());
        if (!indexes.TryGetValue(cacheKey, out var index))
        {
            index = MeasureContentIndex.ForHardestLevel(placement.Arrangement!, project.Beats, rule);
            indexes[cacheKey] = index;
        }

        return LinkRuns.TryResolve(index.Measures, placement, out var first, out var count) ? index.KeyFor(first, count) : null;
    }

    /// <summary>The state of one reconcile pass.</summary>
    private sealed class Run(ChartProject project, LinkPolicy policy)
    {
        public ChartProject Project { get; } = project;
        public LinkPolicy Policy { get; } = policy;
        public IReadOnlyList<MeasureSpan> Measures { get; } = MeasureGrid.Build(project.Beats);
        public Dictionary<(Arrangement, string), MeasureContentIndex> Indexes { get; } = [];
        public List<LinkCopyStep> Copies { get; } = [];
        public List<LinkPropagationReport> Reports { get; } = [];
        public int Propagated { get; set; }
        public int Unlinked { get; set; }
        public bool Changed => Propagated > 0 || Unlinked > 0;
    }
}
