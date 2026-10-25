using DotCharter.Presentation.Screens.Editor;
using DotCharter.Tests.Presentation.Dialogs;
using DotCharter.Tests.Services.Editing.ChordShapes;

namespace DotCharter.Tests.Presentation.Editor;

/// <summary>
///     Drag one Shapes card onto another to merge: the card's four drag commands over a real chart editor and
///     undo history. A drop lands in the same confirmation bar Merge… then Merge here uses, so the plan,
///     the sentence and the one undo step are those the keyboard path already has.
/// </summary>
public sealed class ShapesDragMergeViewModelTests
{
    private readonly ChartEditHarness _chart = new();
    private readonly ChordShapesFixture _f = new();
    private readonly ShapesPanelViewModel _vm;

    public ShapesDragMergeViewModelTests()
    {
        _f.AmAlt.Name = "Am (2)";
        _chart.Project.Arrangements.Add(_f.Arrangement);
        _chart.Editor.ActiveArrangement = _f.Arrangement;
        _chart.State.ActiveLevel = _f.Level1;
        var dialogs = new ScriptedDialogs(
            _ => new ShapeEditViewModel(_chart.Services, new DotCharter.UI.ViewModels.Editor.ChordUseMarks(_chart.Editor)),
            _ => { });
        _vm = new ShapesPanelViewModel(_chart.Editor, _chart.History, _chart.State, FakeLocalization.English(),
            new TestUiDispatcher(),
            new ShapesPanelParts(ChordShapePlacementServiceTests.Build(_chart.State, _chart.Editor.Selection, _chart.History),
                _chart.Transport, dialogs));
    }

    private ShapePanelRowViewModel Row(ChordTemplate template) =>
        _vm.AllRows.Single(r => ReferenceEquals(r.Template, template));

    private string Status => _chart.State.MainDisplayMessage;

    [Fact]
    public void ADrag_PicksTheCard_NamesTheDropHint_AndLightsOnlyTheCardsThatCanTakeIt()
    {
        var copy = Row(_f.AmAlt);
        copy.BeginDragCommand.Execute(null);

        Assert.True(_vm.IsChoosingMergeTarget);
        Assert.Equal("Drop “Am (2)” on the shape it should become. Only shapes with the same frets can take it.", _vm.MergeHintText);
        Assert.True(copy.IsMergeSource);
        Assert.True(Row(_f.Am).CanMergeHere);
        Assert.False(Row(_f.C).CanMergeHere);
        Assert.False(_vm.HasPending);

        // The card under the drag lights only while it can take the dragged one.
        Row(_f.Am).DragOverCommand.Execute(true);
        Assert.True(Row(_f.Am).IsMergeTarget);
        Row(_f.C).DragOverCommand.Execute(true);
        Assert.False(Row(_f.C).IsMergeTarget);
        Assert.False(Row(_f.Am).IsMergeTarget);
        Row(_f.Am).DragOverCommand.Execute(true);
        Row(_f.Am).DragOverCommand.Execute(false);
        Assert.False(Row(_f.Am).IsMergeTarget);
        Assert.False(_chart.History.CanUndo);
    }

    [Fact]
    public void ADrop_WaitsInTheBarWithTheMergePreview_ChangesNothing_AndOneYesMergesAsOneUndoStep()
    {
        var copy = Row(_f.AmAlt);
        copy.BeginDragCommand.Execute(null);
        Row(_f.Am).DragOverCommand.Execute(true);
        Row(_f.Am).DropCommand.Execute(null);
        copy.EndDragCommand.Execute(null);

        Assert.True(_vm.HasPending);
        Assert.False(_vm.IsChoosingMergeTarget);
        Assert.False(_vm.IsPendingDestructive);
        Assert.Equal("Merge", _vm.PendingConfirmLabel);
        Assert.Equal("Merge “Am (2)” into “Am”. Moved onto “Am”: 3 chords · 1 hand shape. “Am (2)” is removed, " +
                     "and “Am” keeps its own fingers and name. One undo step.", _vm.PendingText);
        Assert.True(copy.IsMergeSource);
        Assert.True(Row(_f.Am).IsMergeTarget);
        Assert.Equal(6, _f.Arrangement.ChordTemplates.Count);
        Assert.False(_chart.History.CanUndo);

        _vm.ConfirmPendingCommand.Execute(null);

        Assert.False(_vm.HasPending);
        Assert.DoesNotContain(_f.AmAlt, _f.Arrangement.ChordTemplates);
        Assert.Equal("7 chords · 2 hand shapes", Row(_f.Am).UsesText);
        Assert.Equal("Merged into Am: 3 chords · 1 hand shape moved.", Status);

        _chart.History.Undo();
        Assert.Equal(6, _vm.Rows.Count);
        Assert.Contains(_f.AmAlt, _f.Arrangement.ChordTemplates);
        Assert.False(_chart.History.CanUndo);
    }

    [Fact]
    public void ADropOnACardWithOtherFrets_SaysWhyItCannotMerge_AndChangesNothing()
    {
        var copy = Row(_f.AmAlt);
        copy.BeginDragCommand.Execute(null);
        Row(_f.C).DropCommand.Execute(null);
        copy.EndDragCommand.Execute(null);

        Assert.False(_vm.HasPending);
        Assert.False(_vm.IsChoosingMergeTarget);
        Assert.False(copy.IsMergeSource);
        Assert.Equal("Not merged: the two shapes have different frets.", Status);
        Assert.Equal(6, _f.Arrangement.ChordTemplates.Count);
        Assert.False(_chart.History.CanUndo);
    }

    [Fact]
    public void ADrop_OnAnArpeggioCopy_SaysItIsNotTheSameKindOfShape()
    {
        var copy = Row(_f.AmAlt);
        copy.BeginDragCommand.Execute(null);
        Row(_f.AmArp).DropCommand.Execute(null);

        Assert.False(_vm.HasPending);
        Assert.Equal("Not merged: one shape is an arpeggio and the other is not.", Status);
    }

    [Fact]
    public void AnUnusedShapeDragged_IsRefusedWithItsReason()
    {
        var unused = Row(_f.G);
        unused.BeginDragCommand.Execute(null);
        Row(_f.Am).DropCommand.Execute(null);

        // G and Am have other frets, so that is the first thing said; the point is that nothing moved.
        Assert.False(_vm.HasPending);
        Assert.Equal(6, _f.Arrangement.ChordTemplates.Count);
        Assert.False(_chart.History.CanUndo);
    }

    [Fact]
    public void AReleaseOverNothing_OrOverTheCardItself_OrEsc_DropsThePick()
    {
        var copy = Row(_f.AmAlt);

        copy.BeginDragCommand.Execute(null);
        copy.EndDragCommand.Execute(null);
        Assert.False(_vm.IsChoosingMergeTarget);
        Assert.False(copy.IsMergeSource);
        Assert.Equal("", _vm.MergeHintText);

        copy.BeginDragCommand.Execute(null);
        copy.DropCommand.Execute(null);
        copy.EndDragCommand.Execute(null);
        Assert.False(_vm.IsChoosingMergeTarget);
        Assert.False(_vm.HasPending);
        Assert.False(copy.IsMergeSource);
        Assert.False(_chart.History.CanUndo);
    }

    [Fact]
    public void ADropWithNoDragUnderWay_IsIgnored()
    {
        Row(_f.Am).DropCommand.Execute(null);
        Row(_f.Am).DragOverCommand.Execute(true);

        Assert.False(_vm.HasPending);
        Assert.False(Row(_f.Am).IsMergeTarget);
        Assert.False(_chart.History.CanUndo);
    }

    [Fact]
    public void TheKeyboardPath_IsUnchanged_AndUsesTheSameBar()
    {
        var copy = Row(_f.AmAlt);
        copy.BeginMergeCommand.Execute(null);
        Assert.Equal("Choose the shape “Am (2)” should become. Only shapes with the same frets are offered.", _vm.MergeHintText);

        Row(_f.Am).MergeHereCommand.Execute(null);
        Assert.StartsWith("Merge “Am (2)” into “Am”.", _vm.PendingText, StringComparison.Ordinal);
    }

    [Fact]
    public void AMergedAwayCard_DraggedOver_DoesNotBreakTheNextDrag()
    {
        var copy = Row(_f.AmAlt);
        copy.BeginDragCommand.Execute(null);
        Row(_f.Am).DropCommand.Execute(null);
        _vm.ConfirmPendingCommand.Execute(null);

        var am = Row(_f.Am);
        am.BeginDragCommand.Execute(null);
        Assert.True(_vm.IsChoosingMergeTarget);
        am.EndDragCommand.Execute(null);
        Assert.False(_vm.IsChoosingMergeTarget);
    }
}
