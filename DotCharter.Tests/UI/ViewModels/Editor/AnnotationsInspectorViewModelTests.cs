using DotCharter.Tests.Presentation.Dialogs;
using DotCharter.UI.ViewModels.Editor.Inspector;

namespace DotCharter.Tests.UI.ViewModels.Editor;

/// <summary>
///     The Notes tab's annotation verbs, driven on the extracted
///     <see cref="AnnotationsInspectorViewModel" /> over a real chart editor and
///     the real undo history: every add and remove is ONE undo node, and the
///     jump seeks the playhead.
/// </summary>
public sealed class AnnotationsInspectorViewModelTests
{
    [Fact]
    public void AddingAtThePlayhead_IsOneUndoNode_AndClearsTheComposer()
    {
        var harness = new ChartEditHarness();
        harness.State.CurrentTimeMs = 1500;
        var vm = new AnnotationsInspectorViewModel(harness.State, harness.History, harness.Editor)
        {
            NewAnnotationText = "  check the bend  ",
            NewAnnotationKindIndex = 9
        };

        vm.AddAnnotationAtPlayheadCommand.Execute(null);

        var added = Assert.Single(harness.Project.Annotations);
        Assert.Equal(1500, added.TimeMs);
        Assert.Equal("check the bend", added.Text);
        Assert.Equal((ChartAnnotationKind)2, added.Kind);
        Assert.Equal(string.Empty, vm.NewAnnotationText);
        Assert.True(harness.History.CanUndo);

        harness.History.Undo();

        Assert.Empty(harness.Project.Annotations);
        Assert.False(harness.History.CanUndo);
    }

    [Fact]
    public void ABlankComposer_AddsNothing()
    {
        var harness = new ChartEditHarness();
        var vm = new AnnotationsInspectorViewModel(harness.State, harness.History, harness.Editor)
        {
            NewAnnotationText = "   "
        };

        vm.AddAnnotationAtPlayheadCommand.Execute(null);

        Assert.Empty(harness.Project.Annotations);
        Assert.False(harness.History.CanUndo);
    }

    [Fact]
    public void Removing_IsOneUndoNode()
    {
        var harness = new ChartEditHarness();
        var annotation = new ChartAnnotation { TimeMs = 2000, Text = "a" };
        harness.Project.Annotations.Add(annotation);
        var vm = new AnnotationsInspectorViewModel(harness.State, harness.History, harness.Editor);

        vm.RemoveAnnotationCommand.Execute(annotation);

        Assert.Empty(harness.Project.Annotations);

        harness.History.Undo();

        Assert.Same(annotation, Assert.Single(harness.Project.Annotations));
        Assert.False(harness.History.CanUndo);
    }

    [Fact]
    public void Jumping_SeeksThePlayheadToTheAnnotation()
    {
        var harness = new ChartEditHarness();
        var vm = new AnnotationsInspectorViewModel(harness.State, harness.History, harness.Editor);

        vm.JumpToAnnotationCommand.Execute(new ChartAnnotation { TimeMs = 2500, Text = "b" });

        Assert.Equal(2500, harness.State.CurrentTimeMs);
        Assert.False(harness.History.CanUndo);
    }
}
