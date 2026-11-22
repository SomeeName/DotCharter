using DotCharter.Tests.Presentation.Dialogs;
using DotCharter.UI.ViewModels.Editor.Inspector;

namespace DotCharter.Tests.UI.ViewModels.Editor;

/// <summary>
///     The Annotations tab in inspector design D's cards (#751): the list's filter, the selection it never leaves
///     hidden, the details card's type and text edits (one undo node each) and the "Add at playhead" draft.
/// </summary>
public sealed class AnnotationsInspectorCardsTests
{
    [Fact]
    public void TheFilter_ShowsOpenOrResolved_AndMovesAHiddenSelectionToTheFirstVisibleRow()
    {
        var (harness, vm, open, resolved) = Seeded();
        Assert.Equal([open, resolved], vm.VisibleAnnotations);
        vm.SelectedAnnotation = resolved;

        vm.FilterIndex = AnnotationsInspectorViewModel.ShowOpen;
        Assert.Equal([open], vm.VisibleAnnotations);
        Assert.Same(open, vm.SelectedAnnotation);

        vm.FilterIndex = AnnotationsInspectorViewModel.ShowResolved;
        Assert.Equal([resolved], vm.VisibleAnnotations);
        Assert.Same(resolved, vm.SelectedAnnotation);

        // resolving the open one (an undo node) brings it into the Resolved list
        vm.ToggleAnnotationResolvedCommand.Execute(open);
        Assert.Equal([open, resolved], vm.VisibleAnnotations);
        harness.History.Undo();
        Assert.Equal([resolved], vm.VisibleAnnotations);
    }

    [Fact]
    public void TheDetails_RewordAndRetype_EachAsOneUndoNode()
    {
        var (harness, vm, open, _) = Seeded();
        vm.SelectedAnnotation = open;
        Assert.Equal("check the bend", vm.SelectedText);
        Assert.Equal("At 0:01.000", vm.SelectedHeading);

        vm.SelectedText = "  release it later  ";
        Assert.Equal("check the bend", open.Text); // typing writes nothing
        vm.CommitSelectedTextCommand.Execute(null);
        Assert.Equal("release it later", open.Text);
        Assert.Single(harness.History.GetUndoSteps());

        vm.SelectedKindIndex = (int)ChartAnnotationKind.Mark;
        Assert.Equal(ChartAnnotationKind.Mark, open.Kind);
        Assert.Equal(2, harness.History.GetUndoSteps().Count);

        harness.History.Undo();
        harness.History.Undo();
        Assert.Equal(("check the bend", ChartAnnotationKind.Note), (open.Text, open.Kind));
        Assert.Equal("check the bend", vm.SelectedText); // the card follows the undo
        Assert.Equal((int)ChartAnnotationKind.Note, vm.SelectedKindIndex);

        vm.SelectedText = "   ";
        vm.CommitSelectedTextCommand.Execute(null);
        Assert.Equal("check the bend", vm.SelectedText); // an empty text is no edit
        Assert.False(harness.History.CanUndo);
    }

    [Fact]
    public void AddAtPlayhead_OpensADraft_AddWritesAndSelectsIt_CancelDropsIt()
    {
        var (harness, vm, _, _) = Seeded();
        harness.State.CurrentTimeMs = 5000;

        vm.StartDraftCommand.Execute(null);
        Assert.True(vm.IsDrafting);
        Assert.False(vm.ShowsSelection);
        Assert.Equal("New at 0:05.000", vm.DraftHeading);
        vm.NewAnnotationText = "tone for the solo";
        vm.CancelDraftCommand.Execute(null);
        Assert.False(vm.IsDrafting);
        Assert.Equal(2, harness.Project.Annotations.Count);
        Assert.Equal(string.Empty, vm.NewAnnotationText);

        vm.StartDraftCommand.Execute(null);
        vm.NewAnnotationText = "tone for the solo";
        vm.AddAnnotationAtPlayheadCommand.Execute(null);
        var added = harness.Project.Annotations.Single(a => a.Text == "tone for the solo");
        Assert.Equal(5000, added.TimeMs);
        Assert.False(vm.IsDrafting);
        Assert.Same(added, vm.SelectedAnnotation);
        Assert.Contains(added, vm.VisibleAnnotations);
    }

    [Fact]
    public void DeleteSelected_IsOneUndoNode_AndTheSelectionMovesOn()
    {
        var (harness, vm, open, resolved) = Seeded();
        vm.SelectedAnnotation = open;

        vm.DeleteSelectedCommand.Execute(null);

        Assert.Equal([resolved], harness.Project.Annotations);
        Assert.Same(resolved, vm.SelectedAnnotation);
        harness.History.Undo();
        Assert.Equal(2, vm.VisibleAnnotations.Count);
    }

    /// <summary>The draft is placed at the time its heading shows (the playhead when "Add at playhead" was pressed),
    ///     not wherever the playhead has moved by the time the text is typed.</summary>
    [Fact]
    public void ADraft_IsAddedAtTheTimeItsHeadingShows_WhileThePlayheadMovesOn()
    {
        var (harness, vm, _, _) = Seeded();
        harness.State.CurrentTimeMs = 5000;
        vm.StartDraftCommand.Execute(null);
        Assert.Equal("New at 0:05.000", vm.DraftHeading);

        harness.State.CurrentTimeMs = 9000; // playback runs on while the text is typed
        vm.NewAnnotationText = "tone for the solo";
        vm.AddAnnotationAtPlayheadCommand.Execute(null);

        Assert.Equal(5000, harness.Project.Annotations.Single(a => a.Text == "tone for the solo").TimeMs);
    }

    /// <summary>Another project drops the draft and its typed text.</summary>
    [Fact]
    public void AnotherProject_DropsTheDraftAndItsText()
    {
        var (harness, vm, _, _) = Seeded();
        vm.StartDraftCommand.Execute(null);
        vm.NewAnnotationText = "half typed";

        harness.State.ActiveProject = new ChartProject();

        Assert.False(vm.IsDrafting);
        Assert.Equal("", vm.NewAnnotationText);
    }

    /// <summary>The Resolved card lists every resolved annotation whatever the filter shows, and follows resolve,
    ///     reopen and undo.</summary>
    [Fact]
    public void TheResolvedCard_ListsEveryResolvedAnnotation_WhateverTheFilter()
    {
        var (harness, vm, open, resolved) = Seeded();
        Assert.Equal([resolved], vm.ResolvedAnnotations);
        Assert.True(vm.HasResolved);

        vm.FilterIndex = AnnotationsInspectorViewModel.ShowOpen;
        Assert.Equal([resolved], vm.ResolvedAnnotations);

        vm.FilterIndex = AnnotationsInspectorViewModel.ShowAll;
        vm.ToggleAnnotationResolvedCommand.Execute(open);
        Assert.Equal([open, resolved], vm.ResolvedAnnotations);

        vm.ToggleAnnotationResolvedCommand.Execute(resolved); // reopen from the card's row
        Assert.Equal([open], vm.ResolvedAnnotations);
        harness.History.Undo();
        harness.History.Undo();
        Assert.Equal([resolved], vm.ResolvedAnnotations);
    }

    private static (ChartEditHarness Harness, AnnotationsInspectorViewModel Vm, ChartAnnotation Open, ChartAnnotation Resolved) Seeded()
    {
        var harness = new ChartEditHarness();
        var open = new ChartAnnotation { TimeMs = 1000, Text = "check the bend" };
        var resolved = new ChartAnnotation { TimeMs = 2000, Text = "re-time", Resolved = true };
        harness.Project.Annotations.Add(open);
        harness.Project.Annotations.Add(resolved);
        var vm = new AnnotationsInspectorViewModel(harness.State, harness.History, harness.Editor, FakeLocalization.English());
        return (harness, vm, open, resolved);
    }
}
