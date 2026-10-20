using System.Text.RegularExpressions;
using DotCharter.UI.ViewModels.Panels;

namespace DotCharter.Tests.UI;

/// <summary>
///     A left press that grabbed NOTHING opened a bend-editor transaction that
///     could never be closed, and the editor went silently dead for the rest of
///     the session.
///     <para>
///         <c>OnCanvasPointerPressed</c> called <c>BeginTransaction()</c> and set
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
    ///     The fix, where it lives: the press decides what it grabbed FIRST, and
    ///     a press that grabbed nothing returns without opening a transaction.
    ///     The capture is unconditional afterwards, because by then there is
    ///     always a drag point to release.
    /// </summary>
    [Fact]
    public void ThePressPath_OpensNoTransactionUntilItHasSomethingToDrag()
    {
        var body = BodyOf(ControlSource(),
            "private void OnCanvasPointerPressed(object? sender, PointerPressedEventArgs e)");

        var bail = body.IndexOf("if (hit == null && !slot.HasValue)", StringComparison.Ordinal);
        var begin = body.IndexOf("_vm.BeginTransaction();", StringComparison.Ordinal);
        Assert.True(bail >= 0, "The press path no longer bails out on a press that grabbed nothing.");
        Assert.True(begin > bail,
            "BeginTransaction runs before the press knows whether it grabbed anything — " +
            "a press in the padding then strands the transaction and kills the editor.");

        Assert.DoesNotContain("if (_dragPoint != null) e.Pointer.Capture(_canvas);", body);
        Assert.Contains("e.Pointer.Capture(_canvas);", body);
    }

    private static BendEditorViewModel Editor(Action onWrite) =>
        new(() => [],
            _ => onWrite(),
            () => new FractionalPosition(0),
            () => new FractionalPosition(4),
            () => { });

    private static string ControlSource()
    {
        var path = UiSourceTree.Resolve(Path.Combine(TestPaths.RepoRoot(), "DotCharter.UI", "Views", "Panels", "BendEditorControl.axaml.cs"));
        Assert.True(File.Exists(path), $"{path} moved — repoint this lint.");
        return Regex.Replace(UiSourceTree.ReadAllText(path), @"^\s*//.*$", "", RegexOptions.Multiline);
    }

    private static string BodyOf(string src, string signature)
    {
        var start = src.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, $"BendEditorControl no longer declares '{signature}' — repoint this lint.");

        var open = src.IndexOf('{', start);
        var depth = 0;
        for (var i = open; i < src.Length; i++)
        {
            if (src[i] == '{') depth++;
            else if (src[i] == '}' && --depth == 0) return src[open..i];
        }

        throw new InvalidOperationException($"Unbalanced braces after '{signature}'.");
    }
}
