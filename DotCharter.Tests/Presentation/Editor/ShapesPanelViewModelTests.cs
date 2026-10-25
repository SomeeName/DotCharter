using DotCharter.Presentation.Contracts.Editor;
using DotCharter.Presentation.Screens.Editor;
using DotCharter.Services.Editing.ChordShapes;
using DotCharter.Tests.Presentation.Dialogs;
using DotCharter.Tests.Services.Editing.ChordShapes;

namespace DotCharter.Tests.Presentation.Editor;

/// <summary>The Shapes panel over a real chart editor and undo history.</summary>
public sealed class ShapesPanelViewModelTests
{
    private readonly ChartEditHarness _chart = new();
    private readonly ChordShapesFixture _f = new();
    private readonly ShapesPanelViewModel _vm;
    private readonly ScriptedDialogs _dialogs;
    private Action<object> _script = screen =>
    {
        if (screen is IShapeCleanupScreen cleanup) cleanup.ApplyCommand.Execute(null);
        else ((IShapeEditScreen)screen).CancelCommand.Execute(null);
    };

    public ShapesPanelViewModelTests()
    {
        // Two shapes called "Am" would read alike in every sentence below.
        _f.AmAlt.Name = "Am (2)";
        _chart.Project.Arrangements.Add(_f.Arrangement);
        _chart.Editor.ActiveArrangement = _f.Arrangement;
        _chart.State.ActiveLevel = _f.Level1;
        _dialogs = new ScriptedDialogs(
            type => type == typeof(IShapeCleanupScreen)
                ? new ShapeCleanupViewModel(_chart.Services)
                : new ShapeEditViewModel(_chart.Services, new DotCharter.UI.ViewModels.Editor.ChordUseMarks(_chart.Editor)),
            screen => _script(screen));
        _vm = new ShapesPanelViewModel(_chart.Editor, _chart.History, _chart.State, FakeLocalization.English(),
            new TestUiDispatcher(),
            new ShapesPanelParts(ChordShapePlacementServiceTests.Build(_chart.State, _chart.Editor.Selection, _chart.History),
                _chart.Transport, _dialogs));
    }

    private ShapePanelRowViewModel Row(ChordTemplate template) =>
        _vm.AllRows.Single(r => ReferenceEquals(r.Template, template));

    private string Status => _chart.State.MainDisplayMessage;

    [Fact]
    public void OneCardPerTemplate_WithItsUses_ItsIssue_AndTheSummary()
    {
        Assert.Equal(_f.Arrangement.ChordTemplates.Count, _vm.Rows.Count);
        Assert.True(_vm.HasRows);
        Assert.Equal("6 shapes · needing attention: 2", _vm.Summary);

        var am = Row(_f.Am);
        Assert.Equal("4 chords · 1 hand shape", am.UsesText);
        Assert.False(am.HasIssue);
        Assert.NotSame(_f.Am, am.Diagram);
        Assert.Equal("Am", am.NameText);
        Assert.Equal("x 0 2 2 1 0", am.FretsText);

        var copy = Row(_f.AmAlt);
        Assert.True(copy.HasIssue);
        Assert.Equal("Same frets as “Am”", copy.IssueText);
        Assert.Equal("Merge", copy.FixLabel);
        Assert.True(copy.CanMerge);
        Assert.False(Row(_f.AmArp).CanMerge);

        var c = Row(_f.C);
        Assert.Equal("No fingering", c.IssueText);
        Assert.Equal("Finger", c.FixLabel);

        var g = Row(_f.G);
        Assert.True(g.IsUnused);
        Assert.Equal("Unused", g.UsesText);
        Assert.False(g.HasIssue);
    }

    [Fact]
    public void TheSummary_SaysTidyWhenNothingNeedsAttention()
    {
        _f.Arrangement.ChordTemplates.Remove(_f.AmAlt);
        _f.Arrangement.ChordTemplates.Remove(_f.C);
        _vm.Refresh();

        Assert.Equal("4 shapes · nothing needs attention", _vm.Summary);
    }

    [Fact]
    public void TheSearch_FiltersByNameOrFrets_AndSaysWhenNothingMatches()
    {
        _vm.SearchText = "am";
        Assert.Equal(["Am", "Am (2)", "Am-arp"], _vm.Rows.Cast<ShapePanelRowViewModel>().Select(r => r.Title));

        _vm.SearchText = "3 2 0 1 0";
        Assert.Equal([_f.C], _vm.Rows.Cast<ShapePanelRowViewModel>().Select(r => r.Template));

        _vm.SearchText = "zzz";
        Assert.False(_vm.HasRows);
        Assert.Equal("No shape matches the search.", _vm.EmptyText);

        _vm.SearchText = "";
        Assert.Equal(6, _vm.Rows.Count);
    }

    [Fact]
    public void TheMergeFix_WaitsInTheBar_ChangesNothing_AndOneYesMergesAsOneUndoStep()
    {
        var copy = Row(_f.AmAlt);
        copy.FixCommand.Execute(null);

        Assert.True(_vm.HasPending);
        Assert.False(_vm.IsPendingDestructive);
        Assert.Equal("Merge", _vm.PendingConfirmLabel);
        Assert.Equal("Merge “Am (2)” into “Am”. Moved onto “Am”: 3 chords · 1 hand shape. “Am (2)” is removed, " +
                     "and “Am” keeps its own fingers and name. One undo step.", _vm.PendingText);
        Assert.Equal(6, _f.Arrangement.ChordTemplates.Count);
        Assert.False(_chart.History.CanUndo);
        Assert.True(copy.IsMergeSource);
        Assert.True(Row(_f.Am).IsMergeTarget);

        _vm.ConfirmPendingCommand.Execute(null);

        Assert.False(_vm.HasPending);
        Assert.DoesNotContain(_f.AmAlt, _f.Arrangement.ChordTemplates);
        Assert.Equal(5, _vm.Rows.Count);
        Assert.Equal("7 chords · 2 hand shapes", Row(_f.Am).UsesText);
        Assert.Equal("Merged into Am: 3 chords · 1 hand shape moved.", Status);

        _chart.History.Undo();
        Assert.Equal(6, _vm.Rows.Count);
        Assert.False(_chart.History.CanUndo);
    }

    [Fact]
    public void Cancel_DropsTheMerge_WithoutTouchingTheChart()
    {
        Row(_f.AmAlt).FixCommand.Execute(null);
        _vm.CancelPendingCommand.Execute(null);

        Assert.False(_vm.HasPending);
        Assert.Equal("", _vm.PendingText);
        Assert.All(_vm.AllRows, r => Assert.False(r.IsMergeSource || r.IsMergeTarget));
        Assert.Contains(_f.AmAlt, _f.Arrangement.ChordTemplates);
        Assert.False(_chart.History.CanUndo);
    }

    [Fact]
    public void MergeThenMergeHere_OffersOnlyTheShapesWithTheSameFrets_ThenAsksOnce()
    {
        var copy = Row(_f.AmAlt);
        copy.BeginMergeCommand.Execute(null);

        Assert.True(_vm.IsChoosingMergeTarget);
        Assert.Equal("Choose the shape “Am (2)” should become. Only shapes with the same frets are offered.", _vm.MergeHintText);
        Assert.True(copy.IsMergeSource);
        Assert.True(Row(_f.Am).CanMergeHere);
        Assert.False(Row(_f.C).CanMergeHere);
        Assert.False(Row(_f.AmArp).CanMergeHere);
        Assert.False(copy.CanMergeHere);
        Assert.False(_vm.HasPending);

        // A card that cannot take it ignores the press.
        Row(_f.C).MergeHereCommand.Execute(null);
        Assert.False(_vm.HasPending);

        Row(_f.Am).MergeHereCommand.Execute(null);
        Assert.True(_vm.HasPending);
        Assert.False(_vm.IsChoosingMergeTarget);
        Assert.Contains("into “Am”", _vm.PendingText, StringComparison.Ordinal);
        Assert.All(_vm.AllRows, r => Assert.False(r.CanMergeHere));
    }

    [Fact]
    public void MergePressedAgain_LeavesTheChoice()
    {
        var copy = Row(_f.AmAlt);
        copy.BeginMergeCommand.Execute(null);
        copy.BeginMergeCommand.Execute(null);

        Assert.False(_vm.IsChoosingMergeTarget);
        Assert.False(copy.IsMergeSource);
    }

    [Fact]
    public void ADeleteOfAnUnusedShape_GoesAtOnce_AndOfAUsedOneAsksWhereItsUsesGo()
    {
        Row(_f.G).DeleteCommand.Execute(null);
        Assert.False(_vm.HasPending);
        Assert.DoesNotContain(_f.G, _f.Arrangement.ChordTemplates);
        Assert.Equal("Deleted G.", Status);

        var copy = Row(_f.AmAlt);
        copy.DeleteCommand.Execute(null);

        Assert.True(_vm.HasPending);
        Assert.True(_vm.IsPendingDestructive);
        Assert.Equal("Delete and move 4 uses", _vm.PendingConfirmLabel);
        Assert.Equal("“Am (2)” is still used by 3 chords · 1 hand shape. Deleting it moves those onto “Am” (same frets) " +
                     "and keeps their notes. One undo step.", _vm.PendingText);
        Assert.Contains(_f.AmAlt, _f.Arrangement.ChordTemplates);

        _vm.ConfirmPendingCommand.Execute(null);

        Assert.DoesNotContain(_f.AmAlt, _f.Arrangement.ChordTemplates);
        Assert.Equal("Deleted Am (2); 3 chords · 1 hand shape moved onto Am.", Status);
    }

    [Fact]
    public void ADeleteOfAUsedShapeWithNoCopy_SaysToMergeItFirst()
    {
        Row(_f.C).DeleteCommand.Execute(null);

        Assert.False(_vm.HasPending);
        Assert.Contains(_f.C, _f.Arrangement.ChordTemplates);
        Assert.Equal("This shape is still used. Merge it into another shape first, then delete it.", Status);
    }

    [Fact]
    public void ATypedName_IsAppliedOnCommit_ToEveryUse_AsOneUndoStep()
    {
        var c = Row(_f.C);
        c.NameText = "Cadd9";
        Assert.Equal("C", _f.C.Name);

        c.CommitNameCommand.Execute(null);

        Assert.Equal("Cadd9", _f.C.Name);
        Assert.False(_f.C.Unnamed);
        Assert.Equal("Renamed to Cadd9.", Status);
        c.CommitNameCommand.Execute(null);
        _chart.History.Undo();
        Assert.Equal("C", _f.C.Name);
        Assert.Equal("C", c.NameText);
        Assert.False(_chart.History.CanUndo);
    }

    [Fact]
    public void ANameBeingTyped_SurvivesARefresh_AndAnUnusedBoxFollowsTheShape()
    {
        var c = Row(_f.C);
        c.NameText = "Cm";
        _vm.Refresh();
        Assert.Equal("Cm", c.NameText);

        var g = Row(_f.G);
        _f.G.Name = "G5";
        _vm.Refresh();
        Assert.Equal("G5", g.NameText);
    }

    [Fact]
    public void NoName_ClearsTheNameOnPurpose_AndUnticking_OffersTheSuggestedOne()
    {
        var c = Row(_f.C);
        Assert.False(c.IsNoName);

        c.IsNoName = true;

        Assert.True(_f.C.Unnamed);
        Assert.Equal("", _f.C.Name);
        Assert.True(c.IsNoName);
        Assert.Equal("", c.NameText);
        Assert.Equal("x 3 2 0 1 0", c.NamePlaceholder);
        Assert.Equal("“x 3 2 0 1 0” has no name now.", Status);

        c.IsNoName = false;

        Assert.False(_f.C.Unnamed);
        Assert.Equal(NewChordNames.For(_f.Arrangement, _f.C.Frets), _f.C.Name);
        Assert.False(c.IsNoName);

        _chart.History.Undo();
        _chart.History.Undo();
        Assert.Equal("C", _f.C.Name);
        Assert.False(_chart.History.CanUndo);
    }

    [Fact]
    public void TheFingerFix_SetsTheSuggestedFingering_AsOneUndoStep()
    {
        var c = Row(_f.C);
        Assert.Empty(_f.C.Fingers);

        c.FixCommand.Execute(null);

        Assert.NotEmpty(_f.C.Fingers);
        Assert.False(c.HasIssue);
        Assert.Equal("Fingered C.", Status);
        _chart.History.Undo();
        Assert.Empty(_f.C.Fingers);
        Assert.True(Row(_f.C).HasIssue);
    }

    [Fact]
    public void TheFingerFix_LeavesAPinnedShapeAlone_AndSaysSo()
    {
        _f.C.Pinned = true;
        _vm.Refresh();

        Row(_f.C).FixCommand.Execute(null);

        Assert.Empty(_f.C.Fingers);
        Assert.Equal("This shape is pinned, so its fingers are left as they are.", Status);
        Assert.False(_chart.History.CanUndo);
    }

    [Fact]
    public void CleanUp_IsOfferedWithWhatItFound_AndOpensTheDialog_ThatAppliesInOneStep()
    {
        // The unused G and the "Am (2)" that differs from "Am" by a finger: two things to look at.
        Assert.True(_vm.HasCleanUp);
        Assert.Equal("Clean up (2)", _vm.CleanUpLabel);

        _vm.CleanUpCommand.Execute(null);

        Assert.IsType<ShapeCleanupViewModel>(Assert.Single(_dialogs.Opened.OfType<IShapeCleanupScreen>()));
        Assert.DoesNotContain(_f.G, _f.Arrangement.ChordTemplates);
        Assert.Equal("Cleaned up the shapes: 1 shape removed.", Status);
        Assert.True(_vm.HasCleanUp);
        Assert.Equal("Clean up (1)", _vm.CleanUpLabel);
    }

    [Fact]
    public void CleanUp_IsNotOffered_WhenTheShapesAreTidy()
    {
        foreach (var level in _f.Arrangement.Levels)
        {
            foreach (var chord in level.Chords.Where(c => c.TemplateId == _f.AmAlt.Id).ToList()) level.Chords.Remove(chord);
            foreach (var shape in level.HandShapes.Where(h => h.TemplateId == _f.AmAlt.Id).ToList()) level.HandShapes.Remove(shape);
        }

        _f.Arrangement.ChordTemplates.Remove(_f.AmAlt);
        _f.Arrangement.ChordTemplates.Remove(_f.G);
        _vm.Refresh();

        Assert.False(_vm.HasCleanUp);
        Assert.Equal("", _vm.CleanUpLabel);
    }

    [Fact]
    public void AnUndoThatTakesAMergedShapeAway_DropsTheBarThatWasAboutIt()
    {
        Row(_f.AmAlt).FixCommand.Execute(null);
        Assert.True(_vm.HasPending);

        _f.Arrangement.ChordTemplates.Remove(_f.AmAlt);
        _vm.Refresh();

        Assert.False(_vm.HasPending);
        Assert.False(_vm.IsChoosingMergeTarget);
    }

    [Fact]
    public void SelectUses_SelectsTheLevelOnScreen_AndCountsTheOtherLevels()
    {
        Row(_f.Am).SelectUsesCommand.Execute(null);

        var selection = _chart.Editor.Selection;
        Assert.Equal(_f.Level1.Chords.Where(c => c.TemplateId == _f.Am.Id), selection.SelectedChords);
        Assert.Empty(selection.SelectedHandShapes);
        Assert.Equal("Selected 3 chords on this level. Other levels: 2 uses.", Status);
    }

    [Fact]
    public void ClickingACardsDiagram_PlacesThatChordAtThePlayhead()
    {
        _chart.State.CurrentTimeMs = new FractionalPosition(15).GetPositionMs(_chart.State.Beats);

        Row(_f.G).PlaceCommand.Execute(null);

        var placed = _f.Level1.Chords.Single(c => c.Position == new FractionalPosition(15));
        Assert.Equal(_f.G.Id, placed.TemplateId);
        Assert.Equal("Placed G at the playhead.", Status);
        Assert.Equal("1 chord", Row(_f.G).UsesText);
    }

    [Fact]
    public void Place_IsOneUndoStep_AtThePen_AndReshapesTheSelectionWhenThereIsOne()
    {
        _chart.State.CurrentTimeMs = new FractionalPosition(15).GetPositionMs(_chart.State.Beats);
        Row(_f.G).PlaceCommand.Execute(null);
        Assert.Single(_f.Level1.Chords, c => c.Position == new FractionalPosition(15));
        _chart.History.Undo();
        Assert.DoesNotContain(_f.Level1.Chords, c => c.Position == new FractionalPosition(15));
        Assert.False(_chart.History.CanUndo);

        var chord = _f.Level1.Chords[0];
        _chart.Editor.Selection.SelectedChords.Add(chord);
        _chart.Editor.Selection.SelectedChord = chord;
        Row(_f.C).PlaceCommand.Execute(null);
        Assert.Equal(_f.C.Id, chord.TemplateId);
        Assert.DoesNotContain(_f.Level1.Chords, c => c.Position == new FractionalPosition(15));
        _chart.History.Undo();
        Assert.Equal(_f.Am.Id, chord.TemplateId);
        Assert.False(_chart.History.CanUndo);
    }

    [Fact]
    public void NextUse_StepsAfterThePlayhead_AndWraps()
    {
        var am = Row(_f.Am);
        am.NextUseCommand.Execute(null);
        Assert.Same(_f.Level1.Chords[1], _chart.Editor.Selection.SelectedChord);
        Assert.Equal("Use 2 of 3 on this level.", Status);

        _chart.State.CurrentTimeMs = 99_000;
        am.NextUseCommand.Execute(null);
        Assert.Same(_f.Level1.Chords[0], _chart.Editor.Selection.SelectedChord);

        Row(_f.G).NextUseCommand.Execute(null);
        Assert.StartsWith("No chord or hand shape on this level", Status, StringComparison.Ordinal);
    }

    [Fact]
    public void SplitOffSelection_IsOfferedOnlyForTheSelectionsShape_AndMintsACopyAsOneUndoStep()
    {
        Assert.All(_vm.AllRows, r => Assert.False(r.CanSplitSelection));
        var chord = _f.Level1.Chords[0];
        _chart.Editor.Selection.SelectedChords.Add(chord);
        _chart.Editor.Selection.SelectedChord = chord;
        _chart.Editor.Selection.SelectedChords.Add(_f.Level1.Chords[1]);

        Assert.True(Row(_f.Am).CanSplitSelection);
        Assert.False(Row(_f.AmAlt).CanSplitSelection);
        Assert.True(Row(_f.Am).SplitSelectionCommand.CanExecute(null));

        Row(_f.Am).SplitSelectionCommand.Execute(null);
        Assert.Equal(7, _f.Arrangement.ChordTemplates.Count);
        Assert.Equal(7, _vm.Rows.Count);
        Assert.Equal(_f.Arrangement.ChordTemplates[^1].Id, chord.TemplateId);

        _chart.History.Undo();
        Assert.Equal(6, _vm.Rows.Count);
        Assert.Equal(_f.Am.Id, chord.TemplateId);
        Assert.False(_chart.History.CanUndo);
    }

    [Fact]
    public void TheNeedsAttentionToggle_ListsOnlyTheCardsWithAProblem_AndCombinesWithTheSearch()
    {
        Assert.True(_vm.HasAttentionFilter);
        Assert.Equal("Needs attention (2)", _vm.AttentionFilterLabel);

        _vm.ShowAttentionOnly = true;
        Assert.Equal([_f.AmAlt, _f.C], _vm.Rows.Cast<ShapePanelRowViewModel>().Select(r => r.Template));

        _vm.SearchText = "c";
        Assert.Equal([_f.C], _vm.Rows.Cast<ShapePanelRowViewModel>().Select(r => r.Template));

        // Fixing the last problem leaves the toggle on (so it can be switched off) and the list empty.
        _vm.SearchText = "";
        Row(_f.C).FixCommand.Execute(null);
        Row(_f.AmAlt).FixCommand.Execute(null);
        _vm.ConfirmPendingCommand.Execute(null);
        Assert.False(_vm.HasRows);
        Assert.True(_vm.HasAttentionFilter);

        _vm.ShowAttentionOnly = false;
        Assert.False(_vm.HasAttentionFilter);
        Assert.Equal(_f.Arrangement.ChordTemplates.Count, _vm.Rows.Count);
    }

    [Fact]
    public void RepeatPreviousChord_PlacesTheEarlierShape_AndARefusalSaysWhy()
    {
        _chart.State.CurrentTimeMs = 0;
        _vm.RepeatPreviousChordCommand.Execute(null);
        Assert.Equal("No chord before the playhead to repeat.", Status);

        _chart.State.CurrentTimeMs = new FractionalPosition(15).GetPositionMs(_chart.State.Beats);
        _vm.RepeatPreviousChordCommand.Execute(null);

        Assert.Equal(_f.Open.Id, _f.Level1.Chords.Single(c => c.Position == new FractionalPosition(15)).TemplateId);
        Assert.Equal("Repeated E5 open at the playhead.", Status);
    }

    [Fact]
    public void NoArrangement_EmptiesTheListAndSaysWhy()
    {
        _chart.Editor.ActiveArrangement = null;
        _vm.Refresh();

        Assert.False(_vm.HasRows);
        Assert.Equal("Open a chart to see its chord shapes.", _vm.EmptyText);
        Assert.False(_vm.HasCleanUp);
    }

    private void Tick(params ChordTemplate[] templates)
    {
        foreach (var template in templates) Row(template).IsSelected = true;
    }

    [Fact]
    public void TickingCards_OffersTheBulkBar_WithTheCountsOfTheTickedShapes()
    {
        Assert.All(_vm.AllRows, r => Assert.True(r.CanTick));
        Assert.False(_vm.HasSelection);
        Assert.False(_vm.EditSelectedCommand.CanExecute(null));

        Tick(_f.Am);
        Assert.True(_vm.HasSelection);
        Assert.Equal("1 shape selected · 4 chords", _vm.SelectionText);
        Assert.True(_vm.EditSelectedCommand.CanExecute(null));
        Assert.True(_vm.DeleteSelectedCommand.CanExecute(null));
        Assert.False(_vm.MergeSelectedCommand.CanExecute(null));

        Tick(_f.AmAlt);
        Assert.Equal("2 shapes selected · 7 chords", _vm.SelectionText);
        Assert.True(_vm.MergeSelectedCommand.CanExecute(null));

        _vm.ClearSelectionCommand.Execute(null);
        Assert.False(_vm.HasSelection);
        Assert.Equal("", _vm.SelectionText);
        Assert.All(_vm.AllRows, r => Assert.False(r.IsSelected));
    }

    [Fact]
    public void EditTogether_OpensTheEditorOnEveryTickedShape_AndApplyIsOneUndoStep_ThatLeavesOneShape()
    {
        string scopes = "", opened = "", apply = "", result = "";
        _script = screen =>
        {
            var editor = (IShapeEditScreen)screen;
            scopes = string.Join("|", editor.Scopes.Select(sc => $"{sc.Label}:{sc.Detail}:{sc.IsSelected}"));
            (opened, apply, result) = (editor.OpenedFromText, editor.ApplyLabel, editor.ResultText);
            editor.ApplyCommand.Execute(null);
        };
        Tick(_f.AmAlt, _f.Am);

        _vm.EditSelectedCommand.Execute(null);

        Assert.Equal("This shape:4 chords on 2 levels:False|2 selected shapes:7 chords · 2 hand shapes:True", scopes);
        Assert.Equal("Opened on 2 ticked shapes in the Shapes panel", opened);
        Assert.Equal("Apply to 2 shapes (7 chords)", apply);
        Assert.Equal("The 2 shapes end up identical, so they become one shape; the 1 emptied copy is removed in the same undo step.", result);
        Assert.Equal(5, _vm.Rows.Count);
        Assert.Equal("7 chords · 2 hand shapes", Row(_f.Am).UsesText);
        Assert.False(_vm.HasSelection);

        _chart.History.Undo();
        Assert.Equal(6, _vm.Rows.Count);
        Assert.False(_chart.History.CanUndo);
    }

    [Fact]
    public void EditTogether_CancelledChangesNothing_AndKeepsTheTicks()
    {
        Tick(_f.Am, _f.AmAlt);

        _vm.EditSelectedCommand.Execute(null);

        Assert.Single(_dialogs.Opened);
        Assert.False(_chart.History.CanUndo);
        Assert.True(_vm.HasSelection);
        Assert.Equal(6, _vm.Rows.Count);
    }

    [Fact]
    public void MergeIntoOne_WaitsInTheBar_ThenMergesAllTickedCopiesAsOneUndoStep()
    {
        Tick(_f.Am, _f.AmAlt);
        _vm.MergeSelectedCommand.Execute(null);

        Assert.True(_vm.HasPending);
        Assert.False(_vm.IsPendingDestructive);
        Assert.Equal("Merge", _vm.PendingConfirmLabel);
        Assert.Equal("Merge 2 shapes into “Am” (4 chords · 1 hand shape). Moved onto it: 3 chords · 1 hand shape; 1 copy is removed. " +
                     "Fingers and name stay those of the kept shape. One undo step.", _vm.PendingText);
        Assert.Equal(6, _f.Arrangement.ChordTemplates.Count);
        Assert.False(_chart.History.CanUndo);

        _vm.ConfirmPendingCommand.Execute(null);

        Assert.False(_vm.HasPending);
        Assert.False(_vm.HasSelection);
        Assert.DoesNotContain(_f.AmAlt, _f.Arrangement.ChordTemplates);
        Assert.Equal("7 chords · 2 hand shapes", Row(_f.Am).UsesText);
        Assert.Equal("Merged into Am: 3 chords · 1 hand shape moved.", Status);

        _chart.History.Undo();
        Assert.Equal(6, _vm.Rows.Count);
        Assert.False(_chart.History.CanUndo);
    }

    [Fact]
    public void MergeIntoOne_OfShapesWithDifferentFrets_SaysSo_AndChangesNothing()
    {
        Tick(_f.Am, _f.C);
        _vm.MergeSelectedCommand.Execute(null);

        Assert.False(_vm.HasPending);
        Assert.StartsWith("Not merged: the ticked shapes do not all have the same frets", Status, StringComparison.Ordinal);
        Assert.False(_chart.History.CanUndo);
    }

    [Fact]
    public void ABarForTickedShapes_IsDropped_WhenCancelledOrTheTicksChange()
    {
        Tick(_f.Am, _f.AmAlt);
        _vm.MergeSelectedCommand.Execute(null);
        _vm.CancelPendingCommand.Execute(null);
        Assert.False(_vm.HasPending);
        Assert.True(_vm.HasSelection);

        _vm.MergeSelectedCommand.Execute(null);
        Assert.True(_vm.HasPending);
        Row(_f.AmAlt).IsSelected = false;
        Assert.False(_vm.HasPending);
        Assert.Equal("", _vm.PendingText);
    }

    [Fact]
    public void DeleteTicked_MovesTheUsesOfUsedShapesOntoTheirCopy_RemovesTheUnusedOne_AsOneUndoStep()
    {
        Tick(_f.AmAlt, _f.G);
        _vm.DeleteSelectedCommand.Execute(null);

        Assert.True(_vm.HasPending);
        Assert.True(_vm.IsPendingDestructive);
        Assert.Equal("Delete 2 shapes", _vm.PendingConfirmLabel);
        Assert.Equal("Delete 2 shapes? Their uses move onto the nearest shape with the same frets: 3 chords · 1 hand shape. " +
                     "The notes stay. One undo step.", _vm.PendingText);
        Assert.Equal(6, _f.Arrangement.ChordTemplates.Count);

        _vm.ConfirmPendingCommand.Execute(null);

        Assert.Equal(4, _f.Arrangement.ChordTemplates.Count);
        Assert.Equal("7 chords · 2 hand shapes", Row(_f.Am).UsesText);
        Assert.False(_vm.HasSelection);
        Assert.Equal("Deleted 2 shapes; 3 chords · 1 hand shape moved onto the shapes with the same frets.", Status);

        _chart.History.Undo();
        Assert.Equal(6, _vm.Rows.Count);
        Assert.False(_chart.History.CanUndo);
    }

    [Fact]
    public void DeleteTicked_OfUnusedShapesOnly_GoesAtOnce()
    {
        _f.Arrangement.ChordTemplates.Add(new ChordTemplate { Id = 6, Name = "Spare", Frets = new Dictionary<int, int> { [0] = 1 } });
        _vm.Refresh();
        Tick(_f.G, _f.Arrangement.ChordTemplates[6]);

        _vm.DeleteSelectedCommand.Execute(null);

        Assert.False(_vm.HasPending);
        Assert.Equal(5, _f.Arrangement.ChordTemplates.Count);
        Assert.Equal("Removed 2 unused shapes.", Status);
        _chart.History.Undo();
        Assert.Equal(7, _vm.Rows.Count);
        Assert.False(_chart.History.CanUndo);
    }

    [Fact]
    public void DeleteTicked_WhileAUsedShapeHasNoOtherCopy_IsRefusedWithItsReason()
    {
        Tick(_f.Am, _f.AmAlt);
        _vm.DeleteSelectedCommand.Execute(null);

        Assert.False(_vm.HasPending);
        Assert.StartsWith("Not deleted: a ticked shape is still used", Status, StringComparison.Ordinal);
        Assert.Equal(6, _f.Arrangement.ChordTemplates.Count);
        Assert.False(_chart.History.CanUndo);
    }

    [Fact]
    public void DeleteTicked_OfOneUsedShape_AsksLikeTheCardsOwnDelete()
    {
        Tick(_f.AmAlt);
        _vm.DeleteSelectedCommand.Execute(null);

        Assert.True(_vm.HasPending);
        Assert.StartsWith("“Am (2)” is still used by", _vm.PendingText, StringComparison.Ordinal);
    }

    [Fact]
    public void EditOnACard_OpensTheShapeEditorOnThatShape()
    {
        var row = _vm.AllRows.First(r => r.Template == _f.C);

        Assert.True(row.CanEdit);
        row.EditCommand.Execute(null);

        var opened = Assert.IsType<ShapeEditViewModel>(Assert.Single(_dialogs.Opened));
        Assert.StartsWith("C ·", opened.ShapeTitle, StringComparison.Ordinal);
        Assert.Equal("From the Chord shapes list", opened.OpenedFromText);
    }

}
