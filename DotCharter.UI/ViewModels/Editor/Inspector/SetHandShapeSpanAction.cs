using System.Collections.ObjectModel;

namespace DotCharter.UI.ViewModels.Editor.Inspector;

/// <summary>
///     Moves a hand shape's start and/or end. The undoable half of the Span card
///     (04-inspector-redesign.md §4.3), which closes the audit's "no edge drag,
///     no numeric length; the wheel moves only the end; the start cannot move
///     independently" WITHOUT adding a single timeline gesture.
///     <para>
///         <c>EndPosition</c> is EXCLUSIVE (Core invariant E.4) — the caller has
///         already refused an end at or before the start, because a zero-width
///         shape is not a shorter shape, it is a broken one.
///     </para>
/// </summary>
public sealed class SetHandShapeSpanAction : IChartAction, ILinkPolicyAction
{
    /// <summary>Linked measures: an editing gesture, copied to every linked placement.</summary>
    public LinkPolicy LinkPolicy => LinkPolicy.Propagate;

    private readonly MutateHandShapesAction _mutation;

    public SetHandShapeSpanAction(ObservableCollection<HandShape> handShapes, HandShape handShape,
        FractionalPosition oldStart, FractionalPosition oldEnd,
        FractionalPosition newStart, FractionalPosition newEnd)
    {
        var oldState = handShape.Clone();
        oldState.Position = oldStart;
        oldState.EndPosition = oldEnd;
        var newState = handShape.Clone();
        newState.Position = newStart;
        newState.EndPosition = newEnd;
        _mutation = new MutateHandShapesAction(handShapes, [handShape], [oldState], [newState]);
    }

    public bool HasEffect => _mutation.HasEffect;

    public void Execute() => _mutation.Execute();

    public void Undo() => _mutation.Undo();
}
