using DotCharter.Services.Editing.ChordShapes;
using DotCharter.Services.Editing.Fingering;
using DotCharter.Services.State;
using Chord = DotCharter.Core.Domain.Guitar.Chord;
using HandShape = DotCharter.Core.Domain.Guitar.HandShape;

namespace DotCharter.Services.Editing.HandShapes;

/// <summary>The bars <see cref="ChordHandShapePlanner" /> proposes for one level.</summary>
public sealed record LevelHandShapePlan(ChartLevel Level, IReadOnlyList<PlannedHandShape> HandShapes);

/// <summary>
///     A whole arrangement's plan: the bars per level and the templates they mint.
///     Every level shares one mint list, so a shape that appears on several difficulty
///     levels mints ONE template.
/// </summary>
public sealed record ArrangementHandShapePlan(
    IReadOnlyList<ChordTemplate> MintedTemplates,
    IReadOnlyList<LevelHandShapePlan> Levels)
{
    public int HandShapeCount => Levels.Sum(l => l.HandShapes.Count);

    public int ChordCount => Levels.Sum(l => l.HandShapes.Sum(h => h.Chords.Count));

    public bool IsEmpty => HandShapeCount == 0;
}

/// <summary>
///     "Cover every chord no hand shape covers yet, on every difficulty level, and
///     never touch a bar that exists" — the editor's
/// Tools ▸ Hand shapes ▸ Generate hand shapes (arrangement) and the
/// auto-fix for D11, <see cref="ArrangementIssueType.ChordOutsideHandShape" />.
///     <para>
///         The grouping is <see cref="ChordHandShapePlanner" />'s, the same one the tab
///         importer runs; templates come from <see cref="ResolvedGripSource" />. Unlike
///         the importer this keeps every authored SUSTAIN: on a chart someone wrote in
///         the editor (or in EOF / DLC Builder) a chord's tail is a decision, not a
///         notated duration. A bar over a tail-less chord still covers it
///         (<see cref="ChordHandShapePlanOptions.CoverEveryChord" />), and no bar is
///         drawn across an FHP anchor that is already there
///         (<see cref="ChordHandShapePlanOptions.KeepAnchorsOutside" />, I20).
///     </para>
///     <para>
///         A chord still on the <c>-1</c> sentinel (or naming a template that does not
///         exist) is pointed at its bar's template, in the same step; a chord that
///         names a real template keeps it.
///     </para>
/// </summary>
public static class ArrangementHandShapeGenerator
{
    private static readonly ChordHandShapePlanOptions EditorOptions = new(CoverEveryChord: true, KeepAnchorsOutside: true);

    public static ArrangementHandShapePlan Plan(Arrangement arrangement, ArrangementFingeringStyle? songStyle = null)
    {
        ArgumentNullException.ThrowIfNull(arrangement);
        var grips = new ResolvedGripSource(arrangement, songStyle);
        var minted = new List<ChordTemplate>();
        var levels = new List<LevelHandShapePlan>();
        foreach (var level in arrangement.Levels)
        {
            var bars = ChordHandShapePlanner.Plan(level, grips, minted, EditorOptions);
            if (bars.Count > 0) levels.Add(new LevelHandShapePlan(level, bars));
        }

        return new ArrangementHandShapePlan(minted, levels);
    }

    /// <summary>
    ///     The plan as ONE undo node's actions: the minted templates FIRST (an add or
    ///     mutate action's post-execute resolves template ids), then the bars, then the
    ///     sentinel chords' repoints. The caller wraps them in one
    ///     <see cref="CompositeAction" />, so one Ctrl+Z takes back every bar AND every
    ///     template (the <c>HandShapeTemplateMintTests</c> rule).
    /// </summary>
    public static List<IChartAction> BuildActions(Arrangement arrangement, ArrangementHandShapePlan plan)
    {
        ArgumentNullException.ThrowIfNull(arrangement);
        ArgumentNullException.ThrowIfNull(plan);

        var actions = new List<IChartAction>();
        var known = KnownIds(arrangement);
        var repeats = RepeatedChordNames.Enabled ? RepeatsOf(arrangement, plan, known) : null;
        var added = repeats == null ? plan.MintedTemplates.ToList() : plan.MintedTemplates.Concat(repeats.Twins).ToList();
        if (added.Count > 0)
            actions.Add(new AddChordTemplatesAction(arrangement.ChordTemplates, added));

        var repointTo = new Dictionary<Chord, int>(ReferenceEqualityComparer.Instance);
        if (repeats != null)
        {
            foreach (var (chord, id) in repeats.Repoints) repointTo[chord] = id;
        }

        foreach (var levelPlan in plan.Levels)
        {
            var level = levelPlan.Level;
            actions.Add(new AddHandShapesAction(level.HandShapes, levelPlan.HandShapes.Select(h => h.ToHandShape())));

            var targets = new List<Chord>();
            var oldStates = new List<Chord>();
            var newStates = new List<Chord>();
            foreach (var bar in levelPlan.HandShapes)
            {
                foreach (var chord in bar.Chords)
                {
                    if (known.Contains(chord.TemplateId)) continue;
                    var repointed = chord.Clone();
                    repointed.TemplateId = repointTo.TryGetValue(chord, out var twin) ? twin : bar.GripOf(chord).Id;
                    targets.Add(chord);
                    oldStates.Add(chord.Clone());
                    newStates.Add(repointed);
                }
            }

            if (targets.Count > 0)
                actions.Add(new MutateChordsAction(level.Chords, targets, oldStates, newStates));
        }

        return actions;
    }

    /// <summary>
    ///     Applies the plan IN PLACE and returns the number of bars added — for a caller
    ///     that already owns the undo node, i.e. auto-fix, which works on a cloned
    ///     project inside one snapshot step.
    /// </summary>
    public static int ApplyInPlace(Arrangement arrangement, ArrangementHandShapePlan plan)
    {
        ArgumentNullException.ThrowIfNull(arrangement);
        ArgumentNullException.ThrowIfNull(plan);

        // Taken BEFORE the mint: a dangling id that happens to equal a minted
        // template's id still names nothing, and must be repointed, not adopted.
        var known = KnownIds(arrangement);
        foreach (var template in plan.MintedTemplates) arrangement.ChordTemplates.Add(template);

        var added = 0;
        foreach (var levelPlan in plan.Levels)
        {
            foreach (var bar in levelPlan.HandShapes)
            {
                InsertSorted(levelPlan.Level.HandShapes, bar.ToHandShape());
                added++;
                foreach (var chord in bar.Chords)
                {
                    if (!known.Contains(chord.TemplateId)) chord.TemplateId = bar.Template.Id;
                }
            }
        }

        return added;
    }

    /// <summary>The ids the arrangement declares BEFORE this step. A chord naming
    /// none of them is on the sentinel or dangling, and takes its bar's template.</summary>
    // Only the first chord of a bar shows its name; a later chord this run assigns plays an unnamed twin.
    private static RepeatedChordNamePlan RepeatsOf(Arrangement arrangement, ArrangementHandShapePlan plan, HashSet<int> known)
    {
        var templates = arrangement.ChordTemplates.Concat(plan.MintedTemplates).ToList();
        var nextId = templates.Count == 0 ? 0 : templates.Max(t => t.Id) + 1;
        var shapes = plan.Levels.SelectMany(l => l.HandShapes).Select(bar =>
            (IReadOnlyList<ShapeChord>)bar.Chords
                .Select(chord => new ShapeChord(chord, bar.GripOf(chord), !known.Contains(chord.TemplateId)))
                .ToList());
        return RepeatedChordNames.Plan(templates, shapes, nextId);
    }

    private static HashSet<int> KnownIds(Arrangement arrangement)
    {
        var ids = new HashSet<int>();
        foreach (var template in arrangement.ChordTemplates)
        {
            if (template.Id >= 0) ids.Add(template.Id);
        }

        return ids;
    }

    /// <summary>After the last shape starting at or before the new one, so equal
    /// starts keep their order.</summary>
    private static void InsertSorted(IList<HandShape> shapes, HandShape shape)
    {
        var at = shapes.Count;
        while (at > 0 && shapes[at - 1].Position.CompareTo(shape.Position) > 0) at--;
        shapes.Insert(at, shape);
    }
}
