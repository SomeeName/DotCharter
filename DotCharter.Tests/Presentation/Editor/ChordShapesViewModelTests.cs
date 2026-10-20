using DotCharter.Presentation.Screens.Editor;
using DotCharter.Tests.Presentation.Dialogs;
using DotCharter.Tests.Services.Editing.ChordShapes;

namespace DotCharter.Tests.Presentation.Editor;

/// <summary>The Chord shapes screen over a real chart editor and undo history (#486).</summary>
public sealed class ChordShapesViewModelTests
{
    private readonly ChartEditHarness _chart = new();
    private readonly ChordShapesFixture _f = new();
    private readonly ChordShapesViewModel _vm;

    public ChordShapesViewModelTests()
    {
        _chart.Project.Arrangements.Add(_f.Arrangement);
        _chart.Editor.ActiveArrangement = _f.Arrangement;
        _chart.State.ActiveLevel = _f.Level1;
        _vm = new ChordShapesViewModel(_chart.Editor, _chart.History, _chart.State, FakeLocalization.English(),
            new TestUiDispatcher(),
            ChordShapePlacementServiceTests.Build(_chart.State, _chart.Editor.Selection, _chart.History),
            _chart.Transport);
    }

    private ChordShapeRowViewModel Row(ChordTemplate template) =>
        _vm.AllRows.Single(r => ReferenceEquals(r.Template, template));

    [Fact]
    public void OneRowPerTemplate_WithItsCounts_FingerState_AndDuplicates()
    {
        Assert.Equal(_f.Arrangement.ChordTemplates.Count, _vm.Rows.Count);
        Assert.True(_vm.HasRows);
        Assert.Contains("6 shapes", _vm.Summary, StringComparison.Ordinal);
        Assert.Contains("2 duplicates", _vm.Summary, StringComparison.Ordinal);

        var am = Row(_f.Am);
        Assert.Equal("This level: 3 chords · 0 hand shapes", am.LevelUsesText);
        Assert.Equal("All levels: 4 chords · 1 hand shape", am.AllUsesText);
        Assert.True(am.IsFingeringSuggested);
        Assert.True(am.IsDuplicate);
        Assert.Single(am.MergeTargets);
        Assert.True(Row(_f.C).IsFingeringMissing);
        Assert.True(Row(_f.G).IsUnused);
        Assert.True(Row(_f.AmArp).IsArpeggio);
        Assert.False(Row(_f.AmArp).IsDuplicate);
        Assert.NotSame(_f.Am, am.Diagram);
    }

    [Fact]
    public void TheFiltersNarrowTheList_AndSayHowMany()
    {
        Assert.Equal("Duplicates 2", _vm.DuplicatesFilterLabel);

        _vm.ShowDuplicatesCommand.Execute(null);
        Assert.Equal(["Am", "Am"], _vm.Rows.Select(r => r.Name));
        Assert.True(_vm.IsShowingDuplicates);

        _vm.ShowMissingFingeringCommand.Execute(null);
        Assert.Equal(["C"], _vm.Rows.Select(r => r.Name));

        _f.Arrangement.ChordTemplates.Remove(_f.C);
        _vm.Refresh();
        Assert.False(_vm.HasRows);
        Assert.Equal("No chord shape matches this filter.", _vm.EmptyText);

        _vm.ShowAllCommand.Execute(null);
        Assert.Equal(5, _vm.Rows.Count);
    }

    [Fact]
    public void MergeInto_MovesEveryUse_AsOneUndoStep_AndTheRowsUpdateInPlace()
    {
        var alt = _vm.AllRows[1];
        var rows = _vm.Rows;
        alt.ChooseMergeTargetCommand.Execute(null);
        Assert.True(alt.IsChoosingMergeTarget);

        alt.MergeTargets.Single().MergeCommand.Execute(null);

        Assert.True(alt.IsUnused);
        Assert.Same(rows, _vm.Rows);
        Assert.Equal("All levels: 7 chords · 2 hand shapes", Row(_f.Am).AllUsesText);
        Assert.Contains("Merged into Am", _chart.State.MainDisplayMessage, StringComparison.Ordinal);

        _chart.History.Undo();
        Assert.False(alt.IsUnused);
        Assert.False(_chart.History.CanUndo);
    }

    [Fact]
    public void SelectUses_SelectsTheLevelOnScreen_AndCountsTheOtherLevels()
    {
        Row(_f.Am).SelectUsesCommand.Execute(null);

        var selection = _chart.Editor.Selection;
        Assert.Equal(_f.Level1.Chords.Where(c => c.TemplateId == _f.Am.Id), selection.SelectedChords);
        Assert.Empty(selection.SelectedHandShapes);
        Assert.Equal("Selected 3 chords · 0 hand shapes on this level. Other levels: 2 uses.", _chart.State.MainDisplayMessage);
    }

    [Fact]
    public void NextUse_StepsAfterThePlayhead_AndWraps()
    {
        var am = Row(_f.Am);
        am.NextUseCommand.Execute(null);
        Assert.Same(_f.Level1.Chords[1], _chart.Editor.Selection.SelectedChord);
        Assert.Equal("Use 2 of 3 on this level.", _chart.State.MainDisplayMessage);

        _chart.State.CurrentTimeMs = 99_000;
        am.NextUseCommand.Execute(null);
        Assert.Same(_f.Level1.Chords[0], _chart.Editor.Selection.SelectedChord);
    }

    [Fact]
    public void SplitOffSelection_IsOfferedOnlyForTheSelectionsTemplate_AndMintsACopy()
    {
        Assert.All(_vm.AllRows, r => Assert.False(r.CanSplitSelection));
        _chart.Editor.Selection.SelectedChords.Add(_f.Level1.Chords[0]);
        _chart.Editor.Selection.SelectedChord = _f.Level1.Chords[0];

        Assert.True(Row(_f.Am).CanSplitSelection);
        Assert.False(Row(_f.AmAlt).CanSplitSelection);

        Row(_f.Am).SplitSelectionCommand.Execute(null);
        Assert.Equal(7, _f.Arrangement.ChordTemplates.Count);
        Assert.Equal(7, _vm.Rows.Count);
        Assert.Equal(_f.Arrangement.ChordTemplates[^1].Id, _f.Level1.Chords[0].TemplateId);
    }

    [Fact]
    public void Rename_EditsTheSharedName_AndARefusedMergeSaysWhy()
    {
        var c = Row(_f.C);
        c.BeginRenameCommand.Execute(null);
        Assert.True(c.IsRenaming);
        c.NameDraft = "Cadd9";
        c.CommitRenameCommand.Execute(null);

        Assert.Equal("Cadd9", _f.C.Name);
        Assert.Equal("Cadd9", c.Name);
        Assert.False(c.IsRenaming);

        // The planner refuses a different-fret merge; the screen prints the reason.
        ((IChordShapeRowOwner)_vm).Merge(c, _f.Am);
        Assert.Equal("Not merged: the two shapes have different frets.", _chart.State.MainDisplayMessage);
    }

    [Fact]
    public void ClickingARowsChordBox_PlacesThatChordAtThePlayhead_AndFillsTheRecentStrip()
    {
        _chart.State.CurrentTimeMs = new FractionalPosition(15).GetPositionMs(_chart.State.Beats);
        Assert.False(_vm.HasRecentRows);

        Row(_f.G).PlaceCommand.Execute(null);

        var placed = _f.Level1.Chords.Single(c => c.Position == new FractionalPosition(15));
        Assert.Equal(_f.G.Id, placed.TemplateId);
        Assert.Equal("Placed G at the playhead.", _chart.State.MainDisplayMessage);
        Assert.Equal("This level: 1 chord · 0 hand shapes", Row(_f.G).LevelUsesText);
        Assert.Equal([Row(_f.G)], _vm.RecentRows);
        Assert.True(_vm.HasRecentRows);

        // A tile of the strip is the row itself: pressing it places again.
        _chart.Editor.Selection.ClearSelections();
        _chart.State.CurrentTimeMs = new FractionalPosition(1).GetPositionMs(_chart.State.Beats);
        Row(_f.C).PlaceCommand.Execute(null);
        Assert.Equal([Row(_f.C), Row(_f.G)], _vm.RecentRows);
    }

    [Fact]
    public void RepeatPreviousChord_PlacesTheEarlierShape_AndARefusalSaysWhy()
    {
        _chart.State.CurrentTimeMs = 0;
        _vm.RepeatPreviousChordCommand.Execute(null);
        Assert.Equal("No chord before the playhead to repeat.", _chart.State.MainDisplayMessage);

        _chart.State.CurrentTimeMs = new FractionalPosition(15).GetPositionMs(_chart.State.Beats);
        _vm.RepeatPreviousChordCommand.Execute(null);

        Assert.Equal(_f.Open.Id, _f.Level1.Chords.Single(c => c.Position == new FractionalPosition(15)).TemplateId);
        Assert.Equal("Repeated E5 open at the playhead.", _chart.State.MainDisplayMessage);
    }

    [Fact]
    public void AnUnnamedShape_IsTitledByItsFrets_AndAllLevelsShowsOnlyWhenOtherLevelsAddToIt()
    {
        _f.C.Name = "";
        _vm.Refresh();

        var c = Row(_f.C);
        Assert.True(c.IsUnnamed);
        Assert.Equal("x 3 2 0 1 0", c.Name);
        Assert.False(Row(_f.Am).IsUnnamed);

        // Every use of C is on the level on screen, so a second line would only repeat the first.
        Assert.Equal("", c.AllUsesText);
        Assert.Equal("All levels: 4 chords · 1 hand shape", Row(_f.Am).AllUsesText);

        // The status rail names it the same way, never "(no name)".
        _chart.State.CurrentTimeMs = new FractionalPosition(15).GetPositionMs(_chart.State.Beats);
        c.PlaceCommand.Execute(null);
        Assert.Equal("Placed x 3 2 0 1 0 at the playhead.", _chart.State.MainDisplayMessage);
    }

    [Fact]
    public void NoArrangement_EmptiesTheListAndSaysWhy()
    {
        _chart.Editor.ActiveArrangement = null;

        Assert.False(_vm.HasRows);
        Assert.Equal("Open a chart to see its chord shapes.", _vm.EmptyText);
    }

}
