using DotCharter.Services.Editing.Fingering;
using DotCharter.Services.State;

namespace DotCharter.Services.Ai.Chart;

/// <summary>
///     Turns accepted plans into ONE undoable action on an arrangement and level — the live
///     ones for the editor, a scratch copy for <see cref="AiChartRuleGate" />, so what the
///     rules approved is exactly what gets written.
///     <para>
///         <b>Shared templates are never touched (editing-invariants § chord templates).</b>
///         A chord whose fingering or name changes is REPOINTED at a template that already
///         is that shape + fingering + name (<see cref="IFingeringGenerationService.FindIdenticalTemplate" />),
///         or at a new one minted from a clone of its old template; ten chords re-fingered
///         the same way converge on ONE minted template. A hand shape over those chords that
///         pointed at the same old template follows them, when every such chord under it
///         went to the same new template (a chord, its hand shape and its anchor are one
///         decision — <c>SoundCompanions</c>).
///     </para>
///     <para>
///         <b>One gesture, one node:</b> the anchor removals and additions, the minted
///         templates, the repointed chords and hand shapes are one <see cref="CompositeAction" />.
///         A plan whose chords differ from the level (it was edited while the model was
///         thinking) is dropped whole and counted as stale.
///     </para>
///     <para>
///         <b>FHP only (<see cref="AiChartChunkPlan.FingersFollowAnchors" />):</b> the model
///         was never asked about fingers, so a FREE chord the new anchors govern is
///         re-fingered by the deterministic <see cref="ChordFingeringFollower" /> (its base
///         grip shifted to the new anchor, resolved through
///         <see cref="IFingeringGenerationService.FindIdenticalTemplate" /> or minted as a
///         variant) in this same node. An authored fingering is never touched.
///     </para>
/// </summary>
public static class AiChartApplier
{
    /// <summary>The history label of the composite.</summary>
    public const string HistoryLabelKey = "History_AiChartPass";

    /// <summary>Builds the action for <paramref name="plans" />, or null when nothing would
    /// change. Nothing is executed.</summary>
    /// <param name="arrangement">Owns the templates.</param>
    /// <param name="level">Owns the anchors, chords and hand shapes.</param>
    /// <param name="plans">Accepted plans, in chunk order.</param>
    /// <param name="fingering">Resolves identical templates and the next free id.</param>
    /// <param name="summary">What the action changes.</param>
    /// <param name="checkGuards">Re-check each FHP-only plan's <see cref="AiChartChunkPlan.Guards" />
    /// against the level. True for the live write; the rule gate passes false, because there
    /// an earlier chunk's follower may legitimately have repointed a later chunk's chords.</param>
    public static IChartAction? Build(
        Arrangement arrangement, ChartLevel level, IReadOnlyList<AiChartChunkPlan> plans,
        IFingeringGenerationService fingering, out AiChartApplySummary summary, bool checkGuards = true)
    {
        var composition = Compose(arrangement, level, plans, fingering, checkGuards);
        summary = composition.Summary;
        var composite = new CompositeAction(composition.Actions) { LabelKey = HistoryLabelKey };
        return composite.HasEffect ? composite : null;
    }

    /// <summary>
    ///     What <see cref="Build" /> would change for <paramref name="plans" /> on the LIVE
    ///     level, read off the same composition, without building or executing anything:
    /// the review's marks. Stale plans are left out, exactly as the write leaves
    ///     them out.
    /// </summary>
    /// <param name="arrangement">Owns the templates.</param>
    /// <param name="level">Owns the anchors, chords and hand shapes.</param>
    /// <param name="plans">The plans to preview, in chunk order.</param>
    /// <param name="fingering">Resolves identical templates and the next free id.</param>
    /// <param name="summary">What the write would change.</param>
    public static AiChartApplyTargets Preview(
        Arrangement arrangement, ChartLevel level, IReadOnlyList<AiChartChunkPlan> plans,
        IFingeringGenerationService fingering, out AiChartApplySummary summary)
    {
        var composition = Compose(arrangement, level, plans, fingering, checkGuards: true);
        summary = composition.Summary;
        return composition.Targets;
    }

    private static Composition Compose(
        Arrangement arrangement, ChartLevel level, IReadOnlyList<AiChartChunkPlan> plans,
        IFingeringGenerationService fingering, bool checkGuards)
    {
        ArgumentNullException.ThrowIfNull(arrangement);
        ArgumentNullException.ThrowIfNull(level);
        ArgumentNullException.ThrowIfNull(plans);
        ArgumentNullException.ThrowIfNull(fingering);

        var templatesById = new Dictionary<int, ChordTemplate>();
        foreach (var t in arrangement.ChordTemplates) templatesById.TryAdd(t.Id, t);
        var fresh = plans.Where(plan => IsFresh(plan, level, templatesById, checkGuards)).ToList();
        var stale = plans.Count - fresh.Count;

        // ---- anchors -----------------------------------------------------------------
        var removed = new List<FretAnchor>();
        var added = new List<FretAnchor>();
        foreach (var plan in fresh)
        {
            if (plan.Anchors is null) continue;
            var span = new AiChartChunk(plan.ChunkIndex, plan.Start, plan.End, 0, [], [], [], "");
            removed.AddRange(level.Anchors.Where(a => span.Contains(a.Position)));
            added.AddRange(plan.Anchors.Select(a => new FretAnchor(a.Position, a.Fret, a.Width)));
        }

        var chords = new Retargets<Chord>(c => c.Clone(), (c, id) => c.TemplateId = id);
        var minted = new List<ChordTemplate>();
        var (refingered, renamed) = ResolveChordTemplates(arrangement, level, fresh, fingering, templatesById, chords, minted);

        // ---- FHP only: free fingerings follow the new anchors ---------------------------
        var followedShapes = new List<(HandShape Shape, int NewId)>();
        if (fresh.Any(p => p.FingersFollowAnchors && p.Anchors is not null))
        {
            refingered += FollowAnchors(arrangement, level, removed, added, fingering, templatesById, chords, minted,
                followedShapes);
        }

        var shapes = HandShapesFollowingChords(level, followedShapes, chords.NewIdOf);
        var anchorsChanged = CountAnchorChanges(removed, added);
        var summary = new AiChartApplySummary(anchorsChanged, refingered, renamed, minted.Count, shapes.Targets.Count, stale);

        var actions = new List<IChartAction>();
        if (anchorsChanged > 0)
        {
            if (removed.Count > 0) actions.Add(new RemoveAnchorsAction(level.Anchors, removed));
            if (added.Count > 0) actions.Add(new AddAnchorsAction(level.Anchors, added));
        }

        if (minted.Count > 0) actions.Add(new AddChordTemplatesAction(arrangement.ChordTemplates, minted));
        if (chords.Targets.Count > 0)
            actions.Add(new MutateChordsAction(level.Chords, chords.Targets, chords.OldStates, chords.NewStates));
        if (shapes.Targets.Count > 0)
            actions.Add(new MutateHandShapesAction(level.HandShapes, shapes.Targets, shapes.OldStates, shapes.NewStates));

        // The marks: the chords and hand shapes whose template CHANGES, and the anchors that
        // differ by value. Every target above already changes its template id.
        var (goneAnchors, newAnchors) = anchorsChanged > 0
            ? AnchorDifference(removed, added)
            : (new List<FretAnchor>(), new List<FretAnchor>());
        var marks = new AiChartApplyTargets(chords.Targets, shapes.Targets, goneAnchors, newAnchors);
        return new Composition(actions, summary, marks);
    }

    /// <summary>A plan is fresh when every chord (and, if checked, every guard and anchor) is still the one the model saw.</summary>
    private static bool IsFresh(AiChartChunkPlan plan, ChartLevel level, Dictionary<int, ChordTemplate> templatesById,
        bool checkGuards)
    {
        var guarded = !checkGuards || ((plan.Guards ?? []).All(g =>
            StillMatches(g.ChordIndex, g.Position, g.FretSignature, g.TemplateId, level, templatesById)) &&
            SameAnchors(plan, level));
        return guarded && plan.Chords.All(c =>
            StillMatches(c.ChordIndex, c.Position, c.FretSignature, c.OldTemplateId, level, templatesById));
    }

    /// <summary>
    ///     Templates: resolve or mint, never edit. The probe holds the arrangement's templates
    ///     plus everything minted so far, so the second chord re-fingered the same way finds
    ///     the first one's template. Only templates a chord actually ends up on are kept in
    ///     <paramref name="minted" /> (a resolve that landed back on the chord's own template
    ///     mints nothing it needs).
    /// </summary>
    private static (int Refingered, int Renamed) ResolveChordTemplates(
        Arrangement arrangement, ChartLevel level, List<AiChartChunkPlan> fresh, IFingeringGenerationService fingering,
        Dictionary<int, ChordTemplate> templatesById, Retargets<Chord> chords, List<ChordTemplate> minted)
    {
        var probe = new Arrangement();
        foreach (var t in arrangement.ChordTemplates) probe.ChordTemplates.Add(t);
        var refingered = 0;
        var renamed = 0;
        foreach (var change in fresh.SelectMany(p => p.Chords))
        {
            var chord = level.Chords[change.ChordIndex];
            var old = templatesById[change.OldTemplateId];
            // A pinned grip (authored, or holding ghost gems) is never re-fingered or renamed.
            if (old.IsPinned) continue;
            var wanted = old.Clone();
            wanted.Fingers = new Dictionary<int, int>(change.Fingers);
            wanted.FollowedFingering = false; // a proposed fingering, not the follower's shift
            wanted.Name = change.Name;

            var target = fingering.FindIdenticalTemplate(probe, wanted);
            if (target is null)
            {
                wanted.Id = fingering.NextTemplateId(probe);
                probe.ChordTemplates.Add(wanted);
                minted.Add(wanted);
                target = wanted;
            }

            if (target.Id == chord.TemplateId) continue;
            if (!MapEquality.SameEntries(NonNegative(old.Fingers), NonNegative(target.Fingers))) refingered++;
            if (!string.Equals(old.Name, target.Name, StringComparison.Ordinal)) renamed++;
            chords.Add(chord, target.Id);
        }

        var used = chords.NewStates.Select(c => c.TemplateId).ToHashSet();
        minted.RemoveAll(t => !used.Contains(t.Id));
        return (refingered, renamed);
    }

    /// <summary>
    ///     FHP only: the follower re-fingers the free chords the new anchors govern, unless
    ///     the reply already retargeted them. Returns how many of those changed fingering.
    /// </summary>
    private static int FollowAnchors(
        Arrangement arrangement, ChartLevel level, List<FretAnchor> removed, List<FretAnchor> added,
        IFingeringGenerationService fingering, Dictionary<int, ChordTemplate> templatesById, Retargets<Chord> chords,
        List<ChordTemplate> minted, List<(HandShape Shape, int NewId)> followedShapes)
    {
        var refingered = 0;
        var follow = Follow(arrangement, level, removed, added, fingering, minted);
        var followMinted = follow.MintedTemplates.ToDictionary(t => t.Id);
        foreach (var (chord, oldId, newId) in follow.Chords)
        {
            if (chords.Contains(chord)) continue;
            var old = templatesById[oldId];
            var target = followMinted.GetValueOrDefault(newId) ?? templatesById.GetValueOrDefault(newId) ??
                minted.First(t => t.Id == newId);
            if (!MapEquality.SameEntries(NonNegative(old.Fingers), NonNegative(target.Fingers))) refingered++;
            chords.Add(chord, newId);
        }

        var kept = follow.Chords.Select(c => c.NewId).ToHashSet();
        minted.AddRange(follow.MintedTemplates.Where(t => kept.Contains(t.Id)));
        followedShapes.AddRange(follow.HandShapes.Select(h => (h.Shape, h.NewId)));
        return refingered;
    }

    /// <summary>
    ///     Hand shapes follow their chords: the follower's own, then every shape whose
    ///     chords under it (on its template) were ALL retargeted to ONE new template.
    /// </summary>
    private static Retargets<HandShape> HandShapesFollowingChords(ChartLevel level,
        List<(HandShape Shape, int NewId)> followedShapes, Func<Chord, int?> newIdOf)
    {
        var shapes = new Retargets<HandShape>(h => h.Clone(), (h, id) => h.TemplateId = id);
        foreach (var (shape, newId) in followedShapes)
        {
            if (!shapes.Contains(shape))
                shapes.Add(shape, newId);
        }

        foreach (var shape in level.HandShapes)
        {
            if (shape.TemplateId < 0 || shapes.Contains(shape)) continue;
            var under = level.Chords.Where(c => c.TemplateId == shape.TemplateId && shape.Covers(c.Position)).ToList();
            if (under.Count == 0 || under.Any(c => newIdOf(c) is null)) continue;
            var destinations = under.Select(c => newIdOf(c)!.Value).Distinct().ToList();
            if (destinations.Count != 1) continue;

            shapes.Add(shape, destinations[0]);
        }

        return shapes;
    }

    /// <summary>0 when the added anchors restate the removed ones exactly; otherwise the larger of the two counts.</summary>
    private static int CountAnchorChanges(List<FretAnchor> removed, List<FretAnchor> added) =>
        removed.Count == added.Count &&
        removed.OrderBy(a => a.Position).Zip(added.OrderBy(a => a.Position))
            .All(p => p.First.Position == p.Second.Position && p.First.Fret == p.Second.Fret &&
                      p.First.Width == p.Second.Width)
            ? 0
            : Math.Max(added.Count, removed.Count);

    /// <summary>
    ///     The chords or hand shapes whose template id changes, each with its state before
    ///     and after, in the order they were retargeted.
    /// </summary>
    private sealed class Retargets<T>(Func<T, T> clone, Action<T, int> setTemplateId) where T : class
    {
        private readonly Dictionary<T, int> _newIds = new(ReferenceEqualityComparer.Instance);

        public List<T> Targets { get; } = [];

        public List<T> OldStates { get; } = [];

        public List<T> NewStates { get; } = [];

        public bool Contains(T item) => _newIds.ContainsKey(item);

        public int? NewIdOf(T item) => _newIds.TryGetValue(item, out var id) ? id : null;

        public void Add(T item, int newTemplateId)
        {
            Targets.Add(item);
            OldStates.Add(clone(item));
            var after = clone(item);
            setTemplateId(after, newTemplateId);
            NewStates.Add(after);
            _newIds[item] = newTemplateId;
        }
    }

    /// <summary>The removed anchors no added one restates (same position, fret and width), and
    /// the added anchors no removed one already was: the anchors that change by VALUE.</summary>
    private static (List<FretAnchor> Removed, List<FretAnchor> Added) AnchorDifference(
        List<FretAnchor> removed, List<FretAnchor> added)
    {
        var unmatched = added.ToList();
        var gone = new List<FretAnchor>();
        foreach (var r in removed)
        {
            var twin = unmatched.FindIndex(a => a.Position == r.Position && a.Fret == r.Fret && a.Width == r.Width);
            if (twin >= 0) unmatched.RemoveAt(twin);
            else gone.Add(r);
        }

        return (gone, unmatched);
    }

    /// <summary>True when the level's anchors inside the plan's span are still the ones the
    /// model was shown (<see cref="AiChartChunkPlan.SeenAnchors" />; null = not checked).</summary>
    private static bool SameAnchors(AiChartChunkPlan plan, ChartLevel level)
    {
        if (plan.SeenAnchors is not { } seen) return true;
        var span = new AiChartChunk(plan.ChunkIndex, plan.Start, plan.End, 0, [], [], [], "");
        var live = level.Anchors.Where(a => span.Contains(a.Position)).OrderBy(a => a.Position).ToList();
        if (live.Count != seen.Count) return false;
        for (var i = 0; i < live.Count; i++)
        {
            if (live[i].Position != seen[i].Position || live[i].Fret != seen[i].Fret || live[i].Width != seen[i].Width)
                return false;
        }

        return true;
    }

    private sealed record Composition(List<IChartAction> Actions, AiChartApplySummary Summary, AiChartApplyTargets Targets);

    private static bool StillMatches(
        int chordIndex, FractionalPosition position, string signature, int templateId, ChartLevel level,
        Dictionary<int, ChordTemplate> templates)
    {
        if (chordIndex < 0 || chordIndex >= level.Chords.Count) return false;
        var chord = level.Chords[chordIndex];
        return chord.Position == position && chord.TemplateId == templateId &&
               templates.TryGetValue(chord.TemplateId, out var t) &&
               string.Equals(AiChartValidator.Signature(t), signature, StringComparison.Ordinal);
    }

    /// <summary>
    ///     The follower's plan over the level with <paramref name="removed" /> replaced by
    ///     <paramref name="added" />, kept to the chords an ADDED anchor governs: a chord
    ///     still under an anchor the reply did not touch is not this pass's business, even
    ///     where its fingering already disagreed.
    /// </summary>
    private static FingeringFollowPlan Follow(
        Arrangement arrangement, ChartLevel level, List<FretAnchor> removed, List<FretAnchor> added,
        IFingeringGenerationService fingering, List<ChordTemplate> pending)
    {
        var gone = removed.ToHashSet(ReferenceEqualityComparer.Instance);
        // The follower's own order: stable by position, so on a tie the later in the list wins.
        var after = level.Anchors.Where(a => !gone.Contains(a)).Concat(added).OrderBy(a => a.Position).ToList();
        var plan = ChordFingeringFollower.Plan(arrangement, level, after,
            ChordFingeringFollower.FreeGripResolver(arrangement), fingering, pending);
        if (plan.IsEmpty) return plan;

        var fresh = added.ToHashSet(ReferenceEqualityComparer.Instance);
        var chords = plan.Chords.Where(c => Governing(after, c.Chord.Position) is { } a && fresh.Contains(a)).ToList();
        var keptChords = chords.Select(c => c.Chord).ToHashSet(ReferenceEqualityComparer.Instance);
        var shapes = plan.HandShapes.Where(h =>
            level.Chords.Any(c => keptChords.Contains(c) && h.Shape.Covers(c.Position))).ToList();
        return new FingeringFollowPlan(plan.MintedTemplates, chords, shapes);
    }

    private static FretAnchor? Governing(List<FretAnchor> sorted, FractionalPosition position)
    {
        FretAnchor? found = null;
        foreach (var a in sorted)
        {
            if (a.Position > position) break;
            found = a;
        }

        return found;
    }

    private static Dictionary<int, int> NonNegative(Dictionary<int, int> fingers)
        => fingers.Where(kv => kv.Value >= 0).ToDictionary(kv => kv.Key, kv => kv.Value);
}
