using Avalonia.Input;
using DotCharter.UI.Controls;
using DotCharter.UI.ViewModels.Panels;

namespace DotCharter.Tests.UI;

/// <summary>
///     A left press that grabbed NOTHING opened a bend-editor transaction that
///     could never be closed, and the editor went silently dead for the rest of
///     the session.
///     <para>
///         The old bend graph's <c>OnCanvasPointerPressed</c> called <c>BeginTransaction()</c> and set
///         <c>_dragActive</c> BEFORE working out what the press had hit. A press
///         in the canvas padding — inside the control, outside the plot — hits no
///         point and yields no slot, so no drag point was created and the pointer
///         was never captured (<c>if (_dragPoint != null) e.Pointer.Capture</c>).
///         Releasing off the canvas then never reached
///         <c>OnCanvasPointerReleased</c>, so <c>EndTransaction</c> was never
///         called: <c>_transactionDepth</c> stayed at 1 and every later edit fell
///         into <c>Commit()</c>'s <c>_transactionDepth &gt; 0</c> branch, which
///         only sets a dirty flag. Each further press/release pair then moved the
///         depth 1→2→1 and never back to zero, so the graph kept responding while
///         nothing was ever written to the chart.
///     </para>
/// </summary>
public sealed class BendEditorTransactionTests
{
    /// <summary>The mechanism, at the view model: an unbalanced transaction eats
    /// every write. This is why the press path may not open one speculatively.</summary>
    [Fact]
    public void AnUnbalancedTransaction_SwallowsEveryLaterEdit()
    {
        var writes = 0;
        var vm = Editor(() => writes++);

        vm.BeginTransaction(); // the press that grabbed nothing
        vm.AddOrMovePoint(3, 2.0);
        Assert.Equal(0, writes);

        // Every later gesture is balanced, and still never commits.
        vm.BeginTransaction();
        vm.AddOrMovePoint(6, 1.0);
        vm.EndTransaction();
        Assert.Equal(0, writes);
    }

    [Fact]
    public void ABalancedGesture_CommitsExactlyOnce()
    {
        var writes = 0;
        var vm = Editor(() => writes++);

        vm.BeginTransaction();
        vm.AddOrMovePoint(3, 2.0);
        vm.AddOrMovePoint(6, 1.0);
        vm.EndTransaction();

        Assert.Equal(1, writes);
    }

    /// <summary>
    ///     The fix, where it lives now: the Note tab's bend plot (<see cref="ColumnFlowBendPlot" />, #751) opens a
    ///     transaction only on a left press, and that press ALWAYS hands back a point to drag (the one it hit, or the
    ///     one it added), so the caller captures the pointer and its release closes the transaction. A press that
    ///     grabbed nothing would strand the transaction and kill the editor.
    /// </summary>
    [Fact]
    public void ALeftPress_AlwaysGrabsThePointItsTransactionIsClosedBy()
    {
        var writes = 0;
        var vm = Editor(() => writes++);

        var added = ColumnFlowBendPlot.Press(vm, null, 3, 2.0, PointerUpdateKind.LeftButtonPressed, false);
        Assert.NotNull(added);
        vm.EndTransaction();
        Assert.Equal(1, writes);

        var hit = ColumnFlowBendPlot.Press(vm, added, 3, 2.0, PointerUpdateKind.LeftButtonPressed, false);
        Assert.Same(added, hit);
        vm.EndTransaction();
        var closed = writes;
        vm.AddOrMovePoint(6, 1.0);
        Assert.Equal(closed + 1, writes); // nothing stranded: the next edit writes at once

        Assert.Null(ColumnFlowBendPlot.Press(vm, null, 9, 1.0, PointerUpdateKind.MiddleButtonPressed, false));
        vm.AddOrMovePoint(9, 1.0);
        Assert.Equal(closed + 2, writes); // a press that grabs nothing opens no transaction
    }

    /// <summary>The mechanism the cleanup relies on: a second EndTransaction after
    /// the gesture closed (release, then the capture-loss it raises) is a no-op.</summary>
    [Fact]
    public void ASecondEnd_AfterTheGestureClosed_DoesNotCommitAgain()
    {
        var writes = 0;
        var vm = Editor(() => writes++);

        vm.BeginTransaction();
        vm.AddOrMovePoint(3, 2.0);
        vm.EndTransaction();
        vm.EndTransaction();
        vm.AddOrMovePoint(6, 1.0);

        Assert.Equal(2, writes);
    }

    private static BendEditorViewModel Editor(Action onWrite) =>
        new(() => [],
            _ => onWrite(),
            () => new FractionalPosition(0),
            () => new FractionalPosition(4),
            () => { }, FakeLocalization.English());
}
