using System.Collections.ObjectModel;

namespace DotCharter.Tests.Services.State;

/// <summary>
///     Charting annotations are persisted chart content (the cloner and
///     <c>ChartProjectRestorer</c> both carry them), yet the Notes tab used to
///     mutate <c>ChartProject.Annotations</c> directly — an add or the one-click
///     ✕ delete was invisible to Ctrl+Z and never marked the session dirty.
///     These pin the two actions' semantics and, as source, that the inspector
///     actually routes both verbs through <c>IHistoryService</c>.
/// </summary>
public sealed class AnnotationActionsTests
{
    private static ObservableCollection<ChartAnnotation> ThreeOrdered() =>
    [
        new ChartAnnotation { TimeMs = 1000, Text = "a" },
        new ChartAnnotation { TimeMs = 2000, Text = "b" },
        new ChartAnnotation { TimeMs = 3000, Text = "c" }
    ];

    [Fact]
    public void Add_InsertsTimeOrdered_AndUndoRemovesIt()
    {
        var list = ThreeOrdered();
        var history = new HistoryService(new FakeAppLogger());
        var annotation = new ChartAnnotation { TimeMs = 1500, Text = "between" };

        Assert.True(history.Do(new AddAnnotationAction(list, annotation)));

        Assert.Equal(new[] { "a", "between", "b", "c" }, list.Select(a => a.Text));
        Assert.True(history.CanUndo);

        history.Undo();
        Assert.Equal(new[] { "a", "b", "c" }, list.Select(a => a.Text));
    }

    [Fact]
    public void Remove_IsUndoable_AndUndoRestoresTheOriginalSlot()
    {
        var list = ThreeOrdered();
        var history = new HistoryService(new FakeAppLogger());
        var middle = list[1];

        Assert.True(history.Do(new RemoveAnnotationAction(list, middle)));
        Assert.Equal(new[] { "a", "c" }, list.Select(a => a.Text));

        history.Undo();
        Assert.Equal(new[] { "a", "b", "c" }, list.Select(a => a.Text));

        history.Redo();
        Assert.Equal(new[] { "a", "c" }, list.Select(a => a.Text));
    }

    [Fact]
    public void RemovingAnAnnotationNotInTheList_RecordsNothing()
    {
        var list = ThreeOrdered();
        var history = new HistoryService(new FakeAppLogger());

        Assert.False(history.Do(new RemoveAnnotationAction(list, new ChartAnnotation { TimeMs = 9 })));
        Assert.False(history.CanUndo);
        Assert.Equal(3, list.Count);
    }

    /// <summary>The direct-mutation bug cannot come back silently: both verbs in
    /// the Notes tab must reach the history service, and the raw collection
    /// verbs may not appear outside the action types.</summary>
    [Fact]
    public void TheInspectorTab_RoutesBothVerbsThroughHistory()
    {
        var tab = UiSourceTree.ReadAllText(UiSourceTree.Resolve(Path.Combine(
            TestPaths.RepoRoot(), "DotCharter.UI", "ViewModels", "Editor", "Inspector", "AnnotationsInspectorViewModel.cs")));

        Assert.Contains("_historyService.Do(new AddAnnotationAction(", tab);
        Assert.Contains("_historyService.Do(new RemoveAnnotationAction(", tab);
        Assert.DoesNotContain("Annotations.Insert", tab);
        Assert.DoesNotContain("Annotations.Remove", tab);
    }
}
