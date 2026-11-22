using DotCharter.Services.Editing.ChordShapes;
using DotCharter.UI.ViewModels.Editor;
using DotCharter.UI.ViewModels.Editor.Inspector;

namespace DotCharter.UI.ViewModels.Panels;

/// <summary>
///     Preserves the ghost strings of the hand shapes a grip edit reaches. A fret or finger edit to a
///     ghosted bar reaches what the Apply-to rung reaches for a bar without ghosts: the bars of the rung
///     (resolved by <see cref="GripInspectorTargets" />, the grip page's own target rule) are rebound, each
///     keeping its own opening ghost mask; on Every use and Same grip the chords on the grip follow it; on
///     Same grip a fingering also reaches the other templates of the grip (same frets, or moved along the
///     neck) in place, as <see cref="ChordTemplateEditPlanner" /> writes them. One undo step.
/// </summary>
internal sealed class HandShapeGripGrid(
    ChartEditorViewModel editor, IHistoryService history, IFingeringGenerationService fingering)
{
    private HandShape? SelectedShape(ChordTemplate grip) =>
        editor.Selection.SelectedChords.Count == 0 &&
        editor.Selection.SelectedHandShapes.FirstOrDefault() is { } bar && bar.TemplateId == grip.Id ? bar : null;

    public bool HasGhosts(ChordTemplate grip) => (SelectedShape(grip)?.GetGhostMask(grip) ?? grip.GhostMask) != 0;

    public ChordTemplate? Apply(ChordTemplate loaded, ChordTemplate next, ChordTemplateEditKind kind, ChordApplyRung rung)
    {
        if (kind is not (ChordTemplateEditKind.Fingering or ChordTemplateEditKind.Shape)) return null;
        if (SelectedShape(loaded) is not { } bar || editor.ActiveArrangement is not { } arrangement ||
            editor.ActiveLevel is not { } level)
        {
            return null;
        }

        if (bar.GetGhostMask(loaded) == 0 && !bar.GhostMask.HasValue) return null;

        var minted = new List<ChordTemplate>();
        var actions = new List<IChartAction>();
        var shown = RebindShapes(arrangement, level, bar, loaded, next, kind, rung, minted, actions);
        if (rung is ChordApplyRung.EveryUse or ChordApplyRung.SameGrip)
            actions.AddRange(RebindChords(arrangement, loaded, next, minted));
        if (rung == ChordApplyRung.SameGrip && kind == ChordTemplateEditKind.Fingering)
            actions.AddRange(RefingerOtherGrips(arrangement, loaded, next));

        if (actions.Count > 0)
        {
            if (minted.Count > 0) actions.Insert(0, new AddChordTemplatesAction(arrangement.ChordTemplates, minted));
            history.Do(StrandedShapes.Wrap(arrangement, new CompositeAction(actions), editor.RequestRedraw));
        }

        return shown;
    }

    private ChordTemplate? RebindShapes(Arrangement arrangement, ChartLevel level, HandShape bar, ChordTemplate loaded,
        ChordTemplate next, ChordTemplateEditKind kind, ChordApplyRung rung, List<ChordTemplate> minted,
        List<IChartAction> actions)
    {
        var targets = GripInspectorTargets.Resolve(arrangement, level, bar, null,
                editor.Selection.SelectedHandShapes, [], rung).Shapes
            .Where(t => t.Fields == GripShapeFields.All && t.Shape.TemplateId == loaded.Id &&
                        t.Level.HandShapes.Contains(t.Shape))
            .ToArray();
        ChordTemplate? shown = null;
        foreach (var group in targets.GroupBy(t => t.Level))
        {
            var changes = new List<(HandShape Shape, HandShape State)>();
            foreach (var target in group)
            {
                var mask = target.Shape.GetGhostMask(loaded);
                if (kind == ChordTemplateEditKind.Shape)
                    mask = EditedGhostMask(group.Key, target.Shape, loaded, next, mask);
                var (grip, state) = Rebind(target.Shape, next.Clone(), arrangement, mask, minted);
                if (ReferenceEquals(target.Shape, bar)) shown = grip;
                if (!ChartActionStateComparer.HandShapesEqual(state, target.Shape)) changes.Add((target.Shape, state));
            }

            if (changes.Count == 0) continue;
            actions.Add(new MutateHandShapesAction(group.Key.HandShapes, changes.Select(c => c.Shape),
                changes.Select(c => c.Shape.Clone()), changes.Select(c => c.State), editor.RequestRedraw));
        }

        return shown;
    }

    /// <summary>The chords on the edited grip, on every level, move to its ghost-free form (chords hold no ghosts).
    /// A grip edit never adds a note: a chord that does not sound a string the edit newly frets moves onto the grip
    /// as far as its own strings go.</summary>
    private IEnumerable<IChartAction> RebindChords(Arrangement arrangement, ChordTemplate loaded, ChordTemplate next,
        List<ChordTemplate> minted)
    {
        var grip = Authored(next.Clone());
        grip.GhostMask = 0;
        // One template per set of the grip's strings a chord leaves out: chords that sound the same strings resolve once,
        // not once per chord (Resolve scans the arrangement's templates).
        var bySubset = new Dictionary<string, ChordTemplate>(StringComparer.Ordinal);
        foreach (var level in arrangement.Levels)
        {
            var chords = level.Chords.Where(c => c.TemplateId == loaded.Id).ToArray();
            if (chords.Length == 0) continue;
            var after = chords.Select(c =>
            {
                var clone = c.Clone();
                var key = LeftOut(grip, clone);
                if (!bySubset.TryGetValue(key, out var target))
                {
                    target = Resolve(arrangement, OnItsStrings(grip, clone), minted);
                    bySubset[key] = target;
                }

                clone.TemplateId = target.Id;
                clone.ChordNotes = ChordNoteProjection.OnTo(clone, target);
                return clone;
            }).ToArray();
            yield return new MutateChordsAction(level.Chords, chords, chords.Select(c => c.Clone()), after, editor.RequestRedraw);
        }
    }

    /// <summary>The grip without the strings <paramref name="chord" /> does not sound (the grip itself when it sounds them all).</summary>
    /// <summary>The grip's strings <paramref name="chord" /> does not sound, as a key ("" when it sounds them all).</summary>
    private static string LeftOut(ChordTemplate grip, Chord chord)
    {
        var sounded = chord.ChordNotes.Select(n => n.StringIndex).ToHashSet();
        return string.Join(",", grip.Frets.Keys.Where(s => !sounded.Contains(s)).Order());
    }

    private static ChordTemplate OnItsStrings(ChordTemplate grip, Chord chord)
    {
        var sounded = chord.ChordNotes.Select(n => n.StringIndex).ToHashSet();
        if (grip.Frets.Keys.All(sounded.Contains)) return grip;
        var own = grip.Clone();
        foreach (var stringIndex in grip.Frets.Keys.Where(s => !sounded.Contains(s)))
        {
            own.Frets.Remove(stringIndex);
            own.Fingers.Remove(stringIndex);
        }

        return own;
    }

    /// <summary>Same grip: the fingering copied IN PLACE onto the other templates holding the grip, which keeps
    /// every ghost mask on them and reaches their chords and bars alike.</summary>
    private IEnumerable<IChartAction> RefingerOtherGrips(Arrangement arrangement, ChordTemplate loaded, ChordTemplate next)
    {
        foreach (var grip in ChordGripMatches.Find(arrangement, loaded).For(ChordTemplateEditKind.Fingering))
        {
            var fingers = next.Fingers.Where(f => grip.Frets.ContainsKey(f.Key)).ToDictionary(f => f.Key, f => f.Value);
            var refinger = new SetTemplateFingersAction(grip, new Dictionary<int, int>(grip.Fingers), fingers, editor);
            if (refinger.HasEffect) yield return refinger;
        }
    }

    /// <summary>
    ///     A bar WITHOUT ghost strings still narrows a shape or fingering edit to itself when other bars share
    ///     its grip; a ghosted bar honours the rung, because <see cref="Apply" /> rebinds every target itself.
    /// </summary>
    public ChordApplyRung IsolateSharedBar(ChordTemplate? template, ChordApplyRung rung)
    {
        if (template == null || SelectedShape(template) is not { } bar) return rung;
        if (bar.GetGhostMask(template) != 0 || bar.GhostMask.HasValue) return rung;

        return editor.ActiveArrangement?.Levels.Any(level =>
            level.HandShapes.Any(h => !ReferenceEquals(h, bar) && h.TemplateId == template.Id)) == true
            ? ChordApplyRung.ThisChord : rung;
    }

    private (ChordTemplate Grip, HandShape State) Rebind(HandShape bar, ChordTemplate next, Arrangement arrangement,
        int mask, List<ChordTemplate> minted)
    {
        var state = bar.Clone();
        if (bar.GhostMask.HasValue) state.GhostMask = mask;
        else next.GhostMask = mask;
        var target = Resolve(arrangement, Authored(next), minted);
        state.TemplateId = target.Id;
        return (target, state);
    }

    // A fret or fingering edit is the charter's grip: pinned, and off the generator's fingering.
    private static ChordTemplate Authored(ChordTemplate next)
    {
        next.Pinned = true;
        next.FollowedFingering = false;
        if (next.Fingers.Count > 0) next.Fingerless = false;
        return next;
    }

    private ChordTemplate Resolve(Arrangement arrangement, ChordTemplate next, List<ChordTemplate> minted) =>
        fingering.FindIdenticalTemplate(arrangement, next)
        ?? minted.FirstOrDefault(t => ChordTemplateResolver.MatchesAuthoredState(t, next))
        ?? ChordTemplateResolver.ResolveAuthored(arrangement, next, minted);

    private static int EditedGhostMask(ChartLevel level, HandShape bar, ChordTemplate loaded, ChordTemplate next,
        int previousMask)
    {
        var openingGhosts = GhostGems.OpeningGhostMask(level, bar, next);
        var mask = 0;
        foreach (var s in next.Frets.Keys)
        {
            var source = loaded.Frets.ContainsKey(s) ? previousMask : openingGhosts;
            mask |= source & ChordTemplate.GhostBit(s);
        }
        return mask;
    }
}
