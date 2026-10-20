using DotCharter.Tests.Presentation.Dialogs;
using DotCharter.UI.Controls;
using DotCharter.UI.ViewModels.Editor.Inspector;

namespace DotCharter.Tests.UI.ViewModels.Editor;

/// <summary>
///     The Span card, driven on the extracted <see cref="HandShapeEditorViewModel" />
///     over a real chart editor and the real undo history: a sync reads the
///     selected shape and folds the enclosed count, a typed end is ONE undo
///     node, a garbage end restores the box without touching history, fit-to-
///     sounds is ONE undo node with an EXCLUSIVE end, and every sync raises
///     <see cref="HandShapeEditorViewModel.Synced" /> — the inspector's tie back
///     to its selection-issue memo and card matrix.
/// </summary>
public sealed class HandShapeEditorViewModelTests
{
    private static (ChartEditHarness Harness, HandShape Shape, HandShapeEditorViewModel Vm) Selected()
    {
        var harness = new ChartEditHarness();
        var level = harness.Project.Arrangements[0].Levels[0];
        level.Notes.Add(new GuitarNote { Position = new FractionalPosition(2), StringIndex = 0, Fret = 3 });
        level.Notes.Add(new GuitarNote { Position = new FractionalPosition(3), StringIndex = 1, Fret = 5 });
        level.Notes.Add(new GuitarNote { Position = new FractionalPosition(6), StringIndex = 1, Fret = 5 });
        var shape = new HandShape { Position = new FractionalPosition(1), EndPosition = new FractionalPosition(5) };
        level.HandShapes.Add(shape);
        harness.Editor.Selection.SelectedHandShapes.Add(shape);

        var vm = new HandShapeEditorViewModel(harness.Editor, harness.History, harness.Transport,
            FakeLocalization.KeysOnly());
        vm.SyncHandShapeProperties();
        return (harness, shape, vm);
    }

    [Fact]
    public void ASync_ReadsTheSelectedShape_AndFoldsTheEnclosedSounds()
    {
        var (_, shape, vm) = Selected();

        Assert.Same(shape, vm.SelectedHandShape);
        Assert.Equal(2, vm.HandShapeEnclosedCount);
        Assert.Equal(FractionalLengthFormat.Format(1.0), vm.HandShapeStartText);
        Assert.Equal(FractionalLengthFormat.Format(5.0), vm.HandShapeEndText);
        Assert.False(vm.HandShapeRebindsChords);
    }

    [Fact]
    public void ATypedEnd_IsOneUndoNode()
    {
        var (harness, shape, vm) = Selected();

        vm.HandShapeEndText = "4";

        Assert.Equal(4.0, shape.EndPosition.DoubleValue);
        Assert.Equal(1.0, shape.Position.DoubleValue);
        Assert.True(harness.History.CanUndo);

        harness.History.Undo();

        Assert.Equal(5.0, shape.EndPosition.DoubleValue);
        Assert.False(harness.History.CanUndo);
    }

    [Fact]
    public void AnEndBeforeTheStart_IsRefused_AndTheBoxIsRestored()
    {
        var (harness, shape, vm) = Selected();

        vm.HandShapeEndText = "0.5";

        Assert.Equal(5.0, shape.EndPosition.DoubleValue);
        Assert.False(harness.History.CanUndo);
        Assert.Equal(FractionalLengthFormat.Format(5.0), vm.HandShapeEndText);
    }

    [Fact]
    public void Fit_SnapsToTheEnclosedSounds_WithAnExclusiveEnd_AsOneUndoNode()
    {
        var (harness, shape, vm) = Selected();

        vm.FitHandShapeToSoundsCommand.Execute(null);

        Assert.Equal(2.0, shape.Position.DoubleValue);
        Assert.Equal(3.0 + 1.0 / Math.Max(1, harness.Transport.GridDivisor), shape.EndPosition.DoubleValue, 6);
        Assert.True(harness.History.CanUndo);

        harness.History.Undo();

        Assert.Equal(1.0, shape.Position.DoubleValue);
        Assert.Equal(5.0, shape.EndPosition.DoubleValue);
        Assert.False(harness.History.CanUndo);
    }

    [Fact]
    public void EverySync_RaisesSynced_IncludingTheOneACommitRuns()
    {
        var (_, _, vm) = Selected();
        var raised = 0;
        vm.Synced += () => raised++;

        vm.SyncHandShapeProperties();
        vm.HandShapeEndText = "4";

        Assert.Equal(2, raised);
    }
}
