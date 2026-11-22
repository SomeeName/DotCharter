using DotCharter.Presentation.Screens.Editor;
using DotCharter.Tests.Presentation.Dialogs;
using DotCharter.Tests.Services.Editing.ChordShapes;

namespace DotCharter.Tests.Presentation.Editor;

/// <summary>
///     The Shapes panel's current shape and its Uses page (inspector design D): the current line follows the list,
///     the uses are every chord and hand shape of the shape on the level on screen in time order, a tick IS the
///     editor's selection (both ways), Split uses gives the ticked uses their own copy, and a row's press selects that
///     use alone and seeks to it.
/// </summary>
public sealed class ShapesPanelUsesTests
{
    private readonly ChartEditHarness _chart = new();
    private readonly ChordShapesFixture _f = new();
    private readonly ShapesPanelViewModel _vm;

    public ShapesPanelUsesTests()
    {
        _f.AmAlt.Name = "Am (2)";
        _chart.Project.Arrangements.Add(_f.Arrangement);
        _chart.Editor.ActiveArrangement = _f.Arrangement;
        _chart.State.ActiveLevel = _f.Level1;
        var dialogs = new ScriptedDialogs(_ => throw new InvalidOperationException("no dialog here"), _ => { });
        _vm = new ShapesPanelViewModel(_chart.Editor, _chart.History, _chart.State, FakeLocalization.English(),
            new TestUiDispatcher(),
            new ShapesPanelParts(ChordShapePlacementServiceTests.Build(_chart.State, _chart.Editor.Selection, _chart.History),
                _chart.Transport, dialogs));
    }

    private ShapePanelRowViewModel Row(ChordTemplate template) => _vm.AllRows.Single(r => r.Template.Id == template.Id);

    [Fact]
    public void TheFirstLine_IsCurrent_AndAPickedLineTakesOver()
    {
        Assert.True(_vm.HasCurrentRow);
        Assert.Same(_vm.Rows[0], _vm.CurrentRow);
        Assert.True(((ShapePanelRowViewModel)_vm.Rows[0]).IsCurrent);

        _vm.CurrentRow = Row(_f.C);
        Assert.True(Row(_f.C).IsCurrent);
        Assert.Single(_vm.AllRows, r => r.IsCurrent);
    }

    [Fact]
    public void ASearchThatHidesTheCurrentLine_MovesCurrentToTheFirstShown()
    {
        _vm.CurrentRow = Row(_f.G);
        _vm.SearchText = "Am";
        Assert.DoesNotContain(Row(_f.G), _vm.Rows);
        Assert.Same(_vm.Rows[0], _vm.CurrentRow);
        Assert.False(Row(_f.G).IsCurrent);
    }

    [Fact]
    public void TheUses_AreTheShapesChordsAndHandShapes_OnTheLevelOnScreen_InTimeOrder()
    {
        _vm.CurrentRow = Row(_f.AmAlt);
        Assert.True(_vm.HasUses);
        Assert.Equal(["Chord 1", "Hand shape 1", "Chord 2"], _vm.Uses.Select(u => u.Label));
        Assert.Equal("0 of 3 selected", _vm.UsesSummary);

        _vm.CurrentRow = Row(_f.G);
        Assert.False(_vm.HasUses);
        Assert.Empty(_vm.Uses);
    }

    [Fact]
    public void ATick_IsTheSelection_BothWays()
    {
        _vm.CurrentRow = Row(_f.AmAlt);
        var selection = _chart.Editor.Selection;
        _vm.Uses[2].IsChecked = true;
        Assert.Single(selection.SelectedChords);
        Assert.Empty(selection.SelectedHandShapes);
        Assert.Equal("1 of 3 selected", _vm.UsesSummary);

        _vm.Uses[2].IsChecked = false;
        Assert.Empty(selection.SelectedChords);

        selection.ClearSelections();
        Assert.All(_vm.Uses, u => Assert.False(u.IsChecked));
        var later = _f.Level1.Chords.Last(c => c.TemplateId == _f.AmAlt.Id);
        selection.SelectedChords.Add(later);
        Assert.True(_vm.Uses[2].IsChecked);
    }

    /// <summary>
    ///     A chord and the hand shape covering it are one grip: ticking either ticks both, unticking either unticks
    ///     both, and Split uses moves them together, so no chord ends up on one shape and its hand shape on another.
    /// </summary>
    [Fact]
    public void AChordAndTheHandShapeCoveringIt_TickTogether_AndSplitTogether()
    {
        _vm.CurrentRow = Row(_f.AmAlt);
        var selection = _chart.Editor.Selection;
        var chord = _f.Level1.Chords.First(c => c.TemplateId == _f.AmAlt.Id);
        var shape = _f.Level1.HandShapes.Single(h => h.TemplateId == _f.AmAlt.Id);

        _vm.Uses[0].IsChecked = true;
        Assert.Same(chord, Assert.Single(selection.SelectedChords));
        Assert.Same(shape, Assert.Single(selection.SelectedHandShapes));
        Assert.Equal([true, true, false], _vm.Uses.Select(u => u.IsChecked));
        Assert.Equal("2 of 3 selected", _vm.UsesSummary);

        _vm.Uses[1].IsChecked = false;
        Assert.Empty(selection.SelectedChords);
        Assert.Empty(selection.SelectedHandShapes);
        Assert.All(_vm.Uses, u => Assert.False(u.IsChecked));

        _vm.Uses[1].IsChecked = true;
        Assert.Equal([true, true, false], _vm.Uses.Select(u => u.IsChecked));
        _vm.SplitUsesCommand.Execute(null);
        Assert.NotEqual(_f.AmAlt.Id, chord.TemplateId);
        Assert.Equal(chord.TemplateId, shape.TemplateId);
    }

    [Fact]
    public void SplitUses_IsOfferedForSomeButNotEveryUse_AndGivesTheTickedOnesTheirOwnCopy()
    {
        _vm.CurrentRow = Row(_f.Am);
        Assert.False(_vm.CanSplitUses);
        Assert.False(_vm.SplitUsesCommand.CanExecute(null));

        _vm.Uses[0].IsChecked = true;
        Assert.True(_vm.CanSplitUses);
        Assert.True(_vm.SplitUsesCommand.CanExecute(null));

        var templates = _f.Arrangement.ChordTemplates.Count;
        var first = _f.Level1.Chords.First(c => c.TemplateId == _f.Am.Id);
        _vm.SplitUsesCommand.Execute(null);
        Assert.Equal(templates + 1, _f.Arrangement.ChordTemplates.Count);
        Assert.NotEqual(_f.Am.Id, first.TemplateId);
        Assert.Contains(_f.Level1.Chords, c => c.TemplateId == _f.Am.Id);
    }

    /// <summary>
    ///     Split uses counts what the split counts, every level: every use on the level on screen ticked still splits
    ///     while the shape has a use on another level; a shape used only here is not split off whole.
    /// </summary>
    [Fact]
    public void SplitUses_WithEveryUseOnThisLevelTicked_IsOfferedOnlyWhileAnotherLevelKeepsAUse()
    {
        _vm.CurrentRow = Row(_f.AmAlt);
        foreach (var use in _vm.Uses) use.IsChecked = true;
        Assert.True(_vm.CanSplitUses, "the AmAlt chord on level 0 stays behind, so the split is possible");

        var templates = _f.Arrangement.ChordTemplates.Count;
        _vm.SplitUsesCommand.Execute(null);
        Assert.Equal(templates + 1, _f.Arrangement.ChordTemplates.Count);
        Assert.DoesNotContain(_f.Level1.Chords, c => c.TemplateId == _f.AmAlt.Id);
        Assert.Contains(_f.Level0.Chords, c => c.TemplateId == _f.AmAlt.Id);

        _vm.CurrentRow = Row(_f.C);
        _vm.Uses[0].IsChecked = true;
        Assert.False(_vm.CanSplitUses, "every use of C is ticked: nothing would stay behind");
    }

    [Fact]
    public void SplitUses_UndoesInOneStep()
    {
        _vm.CurrentRow = Row(_f.AmAlt);
        _vm.Uses[0].IsChecked = true;
        var chord = _f.Level1.Chords.First(c => c.TemplateId == _f.AmAlt.Id);
        var shape = _f.Level1.HandShapes.Single(h => h.TemplateId == _f.AmAlt.Id);
        var templates = _f.Arrangement.ChordTemplates.Count;

        _vm.SplitUsesCommand.Execute(null);
        Assert.Equal(templates + 1, _f.Arrangement.ChordTemplates.Count);

        _chart.History.Undo();
        Assert.Equal(templates, _f.Arrangement.ChordTemplates.Count);
        Assert.Equal(_f.AmAlt.Id, chord.TemplateId);
        Assert.Equal(_f.AmAlt.Id, shape.TemplateId);
        Assert.False(_chart.History.CanUndo);
    }

    /// <summary>Hidden, the tab does not re-read its ticks on every selection change; shown again, it reads them once.</summary>
    [Fact]
    public void WhileHidden_TheTicksWait_AndAreReadWhenTheTabComesBack()
    {
        _vm.CurrentRow = Row(_f.AmAlt);
        var later = _f.Level1.Chords.Last(c => c.TemplateId == _f.AmAlt.Id);
        _vm.OnHidden();
        _chart.Editor.Selection.SelectedChords.Add(later);
        Assert.False(_vm.Uses[2].IsChecked);
        Assert.Equal("0 of 3 selected", _vm.UsesSummary);

        _vm.OnShown();
        Assert.True(_vm.Uses[2].IsChecked);
        Assert.Equal("1 of 3 selected", _vm.UsesSummary);
    }

    [Fact]
    public void ARowsPress_SelectsThatUseAlone_SeeksToIt_AndSaysItDrivesTheSelection()
    {
        _vm.CurrentRow = Row(_f.AmAlt);
        var selection = _chart.Editor.Selection;
        selection.SelectedChords.Add(_f.Level1.Chords.First(c => c.TemplateId == _f.Am.Id));
        var driving = new List<bool>();
        selection.SelectionChanged += (_, _) => driving.Add(_vm.IsDrivingSelection);

        _vm.Uses[2].JumpCommand.Execute(null);

        var later = _f.Level1.Chords.Last(c => c.TemplateId == _f.AmAlt.Id);
        Assert.Same(later, Assert.Single(selection.SelectedChords));
        Assert.Same(later, selection.SelectedChord);
        Assert.Equal(later.GetTimeMs(_chart.State.Beats), _chart.State.CurrentTimeMs, 1);
        Assert.NotEmpty(driving);
        Assert.All(driving, Assert.True);
        Assert.False(_vm.IsDrivingSelection);
        Assert.True(_vm.Uses[2].IsChecked);
    }
}
