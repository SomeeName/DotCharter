using System.Collections.ObjectModel;

namespace DotCharter.Tests.UI.Input;

/// <summary>
///     A POINTER PRESS and a POINTER RELEASE are the two moments a timeline
///     gesture ends deterministically, and both must close the open history
///     gesture.
///     <para>
///         <c>IHistoryService.EndGesture</c> is documented as the escape hatch
///         for "gestures that know their own end" — and until this guard it had
///         <b>no production caller at all</b> (only <c>HistoryService</c>'s own
///         <c>Clear</c>/<c>Undo</c>/<c>Redo</c>). Every gesture in the editor was
///         therefore left to <c>HistoryService.MergeWindow</c>'s 900 ms timer,
///         which is deliberately erring LONG: the comment on it says two
///         deliberate taps merging is the cheap direction *because* gestures that
///         know their own end do not depend on it.
///     </para>
///     <para>
///         <c>ToolCoordinator</c> is the single funnel for all four timeline
///         tools but takes a <c>MainWindowViewModel</c>, so it has no behavioural
///         test — the call site is pinned by a source lint, the same shape as
///         <c>ResizeLyricGestureMergeTests.TheWheelCallSitePassesTheMergeVerb</c>,
///         and the BEHAVIOUR it buys is pinned below against the real
///         <c>HistoryService</c>.
///     </para>
/// </summary>
public sealed class PointerGestureBoundaryTests
{
    private static readonly TimeSpan Window = TimeSpan.FromMilliseconds(900);

    private static HandShape ShapeAt(int beat, int endBeat) => new()
    {
        Position = new FractionalPosition(beat),
        EndPosition = new FractionalPosition(endBeat),
        Name = "Chord"
    };

    /// <summary>One tick of the hand-shape resize wheel run
    /// (<c>TimelineWheelAdjuster.TryAdjustHandShapeLength</c>).</summary>
    private static MutateHandShapesAction ResizeTick(
        ObservableCollection<HandShape> collection, HandShape target, int fromEnd, int toEnd)
    {
        var oldState = target.Clone();
        oldState.EndPosition = new FractionalPosition(fromEnd);
        var newState = target.Clone();
        newState.EndPosition = new FractionalPosition(toEnd);
        return new MutateHandShapesAction(
            collection, [target], [oldState], [newState], null, MergeVerbs.HandShapeLength);
    }

    /// <summary>
    ///     The regression, stated as the editor produces it: wheel a hand shape
    ///     longer, CLICK it, wheel again — same collection, same instance, same
    ///     verb, and nothing in between pushed a node to move the merge anchor,
    ///     so the second gesture folds into the first and one Ctrl+Z reverts
    ///     both. A pointer press is the boundary that separates them.
    /// </summary>
    [Fact]
    public void TwoWheelRunsSeparatedByAPointerPressAreTwoUndoNodes()
    {
        var shape = ShapeAt(4, 8);
        var collection = new ObservableCollection<HandShape> { shape };
        var history = new HistoryService(new FakeAppLogger(), mergeWindow: Window);

        history.Do(ResizeTick(collection, shape, 8, 9));
        history.Do(ResizeTick(collection, shape, 9, 10));

        // …the user clicks the shape. ToolCoordinator.HandlePointerPressed.
        history.EndGesture();

        history.Do(ResizeTick(collection, shape, 10, 11));
        history.Do(ResizeTick(collection, shape, 11, 12));

        Assert.Equal(2, history.GetUndoSteps().Count);

        // One Ctrl+Z takes back the SECOND run only, leaving the first standing.
        history.Undo();
        Assert.Equal(new FractionalPosition(10), shape.EndPosition);
    }

    /// <summary>
    ///     The same run WITHOUT the boundary — proof the assertion above is not
    ///     vacuous. Delete either <c>EndGesture()</c> call from
    ///     <c>ToolCoordinator</c> and the editor behaves like this: four ticks,
    ///     two deliberate gestures, ONE undo node that reverts all of it.
    /// </summary>
    [Fact]
    public void WithoutTheBoundaryTheTwoRunsCollapseIntoOne()
    {
        var shape = ShapeAt(4, 8);
        var collection = new ObservableCollection<HandShape> { shape };
        var history = new HistoryService(new FakeAppLogger(), mergeWindow: Window);

        history.Do(ResizeTick(collection, shape, 8, 9));
        history.Do(ResizeTick(collection, shape, 9, 10));
        history.Do(ResizeTick(collection, shape, 10, 11));
        history.Do(ResizeTick(collection, shape, 11, 12));

        Assert.Single(history.GetUndoSteps());
        history.Undo();
        Assert.Equal(new FractionalPosition(8), shape.EndPosition);
    }

    /// <summary>
    ///     Source lint: the funnel every timeline pointer event passes through
    ///     must close the gesture on BOTH ends. Only pressed would leave a
    ///     drag's own commit open for the next wheel run; only released would
    ///     leave a keyboard run open across the click that starts a drag.
    /// </summary>
    [Fact]
    public void TheToolCoordinatorClosesTheGestureOnPressAndOnRelease()
    {
        var source = UiSourceTree.ReadAllText(RepoPath("DotCharter.UI/Input/Tools/ToolCoordinator.cs"));
        var body = StripComments(source);

        foreach (var handler in new[] { "HandlePointerPressed", "HandlePointerReleased" })
        {
            var start = body.IndexOf("public void " + handler, StringComparison.Ordinal);
            Assert.True(start >= 0, $"ToolCoordinator.{handler} has moved or been renamed — repoint this lint.");

            var next = body.IndexOf("public void ", start + 1, StringComparison.Ordinal);
            var scope = next < 0 ? body[start..] : body[start..next];

            Assert.True(scope.Contains("_history.EndGesture()", StringComparison.Ordinal),
                $"ToolCoordinator.{handler} must call _history.EndGesture(). A pointer press/release is where a " +
                "timeline gesture ends DETERMINISTICALLY; without it the 900 ms merge window decides, and two " +
                "deliberate gestures over the same targets collapse into one undo node.");
        }
    }

    /// <summary>Comments must not satisfy the lint — a <c>see cref</c> is exactly
    /// as inert as a dead registration.</summary>
    private static string StripComments(string source)
    {
        var sb = new System.Text.StringBuilder(source.Length);
        var inBlock = false;
        foreach (var rawLine in source.Split('\n'))
        {
            var line = rawLine;
            if (inBlock)
            {
                var close = line.IndexOf("*/", StringComparison.Ordinal);
                if (close < 0) continue;
                line = line[(close + 2)..];
                inBlock = false;
            }

            var open = line.IndexOf("/*", StringComparison.Ordinal);
            if (open >= 0)
            {
                inBlock = true;
                line = line[..open];
            }

            var slash = line.IndexOf("//", StringComparison.Ordinal);
            if (slash >= 0) line = line[..slash];
            sb.Append(line).Append('\n');
        }

        return sb.ToString();
    }

    private static string RepoPath(string relative) =>
        Path.Combine(TestPaths.RepoRoot(), relative.Replace('/', Path.DirectorySeparatorChar));
}
