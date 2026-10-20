using System.Collections.ObjectModel;

namespace DotCharter.Tests.Services.State;

/// <summary>
///     A remove action restores on Undo ONLY what its Execute removed.
///     <para>
///         The bug: right-click delete of a hand shape removed it from the level
///         but left it in <c>SelectedHandShapes</c>; the next Delete built a
///         <c>RemoveHandShapesAction</c> over the still-selected shape, whose
///         Execute removed nothing (the loop's <c>Remove</c> returns false) and
///         whose Undo re-inserted it — a shape deleted two gestures ago came back
///         on an unrelated Ctrl+Z. The delete sites now clear the selection, but
///         the action is the last line: it must not resurrect what it never
///         removed, and a request naming nothing present makes no undo node.
///     </para>
/// </summary>
public sealed class RemoveActionAbsentItemsTests
{
    private static HandShape Shape(int beat) => new()
    {
        Position = new FractionalPosition(beat),
        EndPosition = new FractionalPosition(beat + 1)
    };

    [Fact]
    public void AnItemAlreadyGone_IsNotResurrectedByUndo()
    {
        var shapes = new ObservableCollection<HandShape> { Shape(0), Shape(4), Shape(8) };
        var doomed = shapes[1];
        var history = new HistoryService(new FakeAppLogger());

        // Gesture 1: the right-click delete.
        Assert.True(history.Do(new RemoveHandShapesAction(shapes, [doomed])));
        Assert.DoesNotContain(doomed, shapes);

        // Gesture 2: Delete over a selection that (before the fix) still named it.
        var stale = new RemoveHandShapesAction(shapes, [doomed]);
        Assert.False(stale.HasEffect, "a remove that would remove nothing has no effect");
        Assert.False(history.Do(stale));
        Assert.Equal(2, shapes.Count);

        // Undo the gesture-2 shape by hand too, in case a caller bypasses HasEffect.
        stale.Execute();
        stale.Undo();
        Assert.Equal(2, shapes.Count);
        Assert.DoesNotContain(doomed, shapes);
    }

    [Fact]
    public void AMixedRequest_RemovesWhatIsThere_AndUndoRestoresExactlyThat()
    {
        var shapes = new ObservableCollection<HandShape> { Shape(0), Shape(4), Shape(8) };
        var present = shapes[2];
        var absent = Shape(12);
        var action = new RemoveHandShapesAction(shapes, [absent, present]);

        Assert.True(action.HasEffect);
        action.Execute();
        Assert.Equal(2, shapes.Count);
        Assert.DoesNotContain(present, shapes);

        action.Undo();
        Assert.Equal(3, shapes.Count);
        Assert.Contains(present, shapes);
        Assert.DoesNotContain(absent, shapes);
        // …and re-doing lands on the same answer.
        action.Execute();
        Assert.Equal(2, shapes.Count);
    }

    /// <summary>The bulk path (≥16 items) takes the hashed presence check; same contract.</summary>
    [Fact]
    public void ABulkRequest_WithAbsentItems_KeepsTheSameContract()
    {
        var shapes = new ObservableCollection<HandShape>(Enumerable.Range(0, 40).Select(i => Shape(i * 2)));
        var targets = shapes.Where((_, i) => i % 2 == 0).ToList();          // 20 present
        var ghosts = Enumerable.Range(0, 5).Select(i => Shape(100 + i)).ToList(); // 5 absent
        var action = new RemoveHandShapesAction(shapes, targets.Concat(ghosts));

        action.Execute();
        Assert.Equal(20, shapes.Count);
        Assert.All(targets, t => Assert.DoesNotContain(t, shapes));

        action.Undo();
        Assert.Equal(40, shapes.Count);
        Assert.All(ghosts, g => Assert.DoesNotContain(g, shapes));
        Assert.All(targets, t => Assert.Contains(t, shapes));
    }
}
