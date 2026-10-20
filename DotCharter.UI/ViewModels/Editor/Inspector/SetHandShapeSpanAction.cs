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
public sealed class SetHandShapeSpanAction : IChartAction
{
    private readonly HandShape _handShape;
    private readonly FractionalPosition _oldStart;
    private readonly FractionalPosition _oldEnd;
    private readonly FractionalPosition _newStart;
    private readonly FractionalPosition _newEnd;

    public SetHandShapeSpanAction(HandShape handShape,
        FractionalPosition oldStart, FractionalPosition oldEnd,
        FractionalPosition newStart, FractionalPosition newEnd)
    {
        _handShape = handShape;
        _oldStart = oldStart;
        _oldEnd = oldEnd;
        _newStart = newStart;
        _newEnd = newEnd;
    }

    public bool HasEffect => _oldStart != _newStart || _oldEnd != _newEnd;

    public void Execute()
    {
        _handShape.Position = _newStart;
        _handShape.EndPosition = _newEnd;
    }

    public void Undo()
    {
        _handShape.Position = _oldStart;
        _handShape.EndPosition = _oldEnd;
    }
}
