using System.Collections.ObjectModel;

namespace DotCharter.UI.Input.Actions;

/// <summary>
///     One tick of a hand-shape resize: the bar's new end, and the grip rebuilt
///     from the sounds it now covers.
///     <para>
///         Both halves have to live in ONE action. The resize is a merged
///         gesture — dozens of wheel ticks folded into a single undo node — so a
///         separate template edit per tick would push a node each and undo would
///         take the fingering back without the length (one real session logged a
///         111-node run before the merge verb existed). Absorbing keeps this
///         action's ORIGIN and takes the later tick's destination, grip included.
///     </para>
///     <para>
///         The grip is only ever rewritten in place on a template the bar owns
///         alone — <see cref="HandShapeAutoTemplate.OwnsItsTemplateAlone" /> — or
///         on one minted here for a bar that had none. A shared template belongs
///         to a real chord and is left untouched, which is why this is not the
///         general "mutate a ChordTemplate" action ChartActions.cs refuses to
///         have.
///     </para>
/// </summary>
public sealed class ResizeHandShapesAction : IChartAction, IMergeableAction, ILinkPolicyAction, ITemplateUseReporter
{
    /// <summary>Linked measures: an editing gesture, copied to every linked placement.</summary>
    public LinkPolicy LinkPolicy => LinkPolicy.Propagate;

    private readonly ObservableCollection<ChordTemplate> _templates;
    private readonly MutateHandShapesAction _spans;
    private readonly List<GripRewrite> _grips;
    private readonly List<ChordTemplate> _minted;

    public ResizeHandShapesAction(
        ObservableCollection<HandShape> collection,
        IReadOnlyList<HandShape> targets,
        IReadOnlyList<HandShape> oldStates,
        IReadOnlyList<HandShape> newStates,
        ObservableCollection<ChordTemplate> templates,
        IEnumerable<GripRewrite> grips,
        IEnumerable<ChordTemplate> minted,
        Action? onPostExecute)
    {
        _templates = templates;
        _grips = grips.ToList();
        _minted = minted.ToList();
        _spans = new MutateHandShapesAction(collection, targets, oldStates, newStates, onPostExecute,
            MergeVerbs.HandShapeLength);
    }

    /// <summary>A template rewritten in place: what it held, and what it becomes.</summary>
    public sealed record GripRewrite(ChordTemplate Template, ChordTemplate Before, ChordTemplate After);

    public string MergeVerb => MergeVerbs.HandShapeLength;

    public bool HasEffect => _spans.HasEffect || _grips.Count > 0 || _minted.Count > 0;

    /// <summary>A bar that moves onto the template of the chord it now covers leaves its own behind.</summary>
    public void ReportTemplateUses(TemplateUseDelta delta) => _spans.ReportTemplateUses(delta);

    public void Execute()
    {
        // Templates first: the span action's post-execute is the spacing
        // enforcer, which resolves template ids.
        // ContainsInstance, not Contains: ChordTemplate has VALUE equality that
        // excludes Id, so a plain Contains answers "a template like this one"
        // and skipped the Add whenever the arrangement already drew this exact
        // grip under another id — leaving the bar pointing at an id no template
        // carries. See ChordTemplateCollectionOps.
        foreach (var template in _minted)
        {
            if (!ChordTemplateCollectionOps.ContainsInstance(_templates, template))
                _templates.Add(template);
        }

        foreach (var grip in _grips) Apply(grip.Template, grip.After);
        _spans.Execute();
    }

    public void Undo()
    {
        _spans.Undo();
        foreach (var grip in _grips) Apply(grip.Template, grip.Before);
        // …and RemoveInstance for the same reason: Remove deletes the FIRST
        // value-equal template, which is somebody else's still-referenced grip.
        foreach (var template in _minted) ChordTemplateCollectionOps.RemoveInstance(_templates, template);
    }

    public bool CanAbsorb(IChartAction next) =>
        next is ResizeHandShapesAction other &&
        ReferenceEquals(_templates, other._templates) &&
        _spans.CanAbsorb(other._spans);

    public void Absorb(IChartAction next)
    {
        var other = (ResizeHandShapesAction)next;
        _spans.Absorb(other._spans);

        // A template minted by a LATER tick joins ours: the bar had none when
        // the gesture began, and its first fretted span is what created one.
        foreach (var template in other._minted)
        {
            if (!ChordTemplateCollectionOps.ContainsInstance(_minted, template))
                _minted.Add(template);
        }

        // Each template keeps the BEFORE this gesture found it in and takes the
        // newest AFTER — the same origin-and-destination rule the spans follow.
        foreach (var grip in other._grips)
        {
            var index = _grips.FindIndex(g => ReferenceEquals(g.Template, grip.Template));
            if (index < 0) _grips.Add(grip);
            else _grips[index] = _grips[index] with { After = grip.After };
        }
    }

    private static void Apply(ChordTemplate target, ChordTemplate source)
    {
        target.Name = source.Name;
        target.Arpeggio = source.Arpeggio;
        target.Frets = new Dictionary<int, int>(source.Frets);
        target.Fingers = new Dictionary<int, int>(source.Fingers);
    }
}
