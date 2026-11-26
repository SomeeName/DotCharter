using DotCharter.Presentation.Screens.Editor;
using DotCharter.Tests.Presentation.Dialogs;
using DotCharter.Tests.Services.Editing.ChordShapes;

namespace DotCharter.Tests.Presentation.Editor;

/// <summary>
///     The Chord shapes tab's pins (#1019): a card's Pin grip pins and unpins its shape, one undo step each, and
///     "Pin all grips" pins every unpinned fingered shape in one undo step, shows how many, and is off with its
///     reason once none is left.
/// </summary>
public sealed class ShapesPanelPinTests
{
    private readonly ChartEditHarness _chart = new();
    private readonly ChordShapesFixture _f = new();
    private readonly ShapesPanelViewModel _vm;

    public ShapesPanelPinTests()
    {
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
    public void ACardsPinGrip_PinsAndUnpinsItsShape_OneUndoStepEach()
    {
        var row = Row(_f.Am);
        Assert.False(row.IsPinned);
        Assert.True(row.CanTogglePin);

        row.IsPinned = true;
        Assert.True(_f.Am.Pinned);
        Assert.True(row.IsPinned);
        Assert.False(_f.AmAlt.Pinned);

        row.IsPinned = false;
        Assert.False(_f.Am.Pinned);

        _chart.History.Undo();
        _vm.Refresh();
        Assert.True(_f.Am.Pinned);
        Assert.True(row.IsPinned);
        _chart.History.Undo();
        _vm.Refresh();
        Assert.False(_f.Am.Pinned);
        Assert.False(row.IsPinned);
    }

    [Fact]
    public void AGhostGrip_IsShownPinned_AndItsPinCannotChange()
    {
        _f.C.GhostMask = 1;
        _vm.Refresh();
        var row = Row(_f.C);
        Assert.True(row.IsPinned);
        Assert.False(row.CanTogglePin);

        row.IsPinned = false;

        Assert.True(row.IsPinned);
        Assert.False(_chart.History.CanUndo);
    }

    [Fact]
    public void PinAll_PinsEveryUnpinnedFingeredShape_InOneUndoStep()
    {
        _f.AmArp.Pinned = true;
        _vm.Refresh();
        // Fingered: Am, AmAlt, AmArp (pinned already), G; C and the open shape have no fingers.
        Assert.True(_vm.HasPinAll);
        Assert.Equal("Pin all grips (3)", _vm.PinAllLabel);

        _vm.PinAllCommand.Execute(null);

        Assert.True(_f.Am.Pinned && _f.AmAlt.Pinned && _f.AmArp.Pinned && _f.G.Pinned);
        Assert.False(_f.C.Pinned || _f.Open.Pinned);
        Assert.False(_vm.HasPinAll);
        Assert.Equal("Pin all grips", _vm.PinAllLabel);
        Assert.Equal(FakeLocalization.English()["Shapes_PinAll_NoneTip"], _vm.PinAllTip);
        Assert.Equal("Pinned 3 grips.", _chart.State.MainDisplayMessage);

        _chart.History.Undo();
        Assert.False(_f.Am.Pinned || _f.AmAlt.Pinned || _f.G.Pinned);
        Assert.True(_f.AmArp.Pinned);
        Assert.False(_chart.History.CanUndo);
        _vm.Refresh();
        Assert.True(_vm.HasPinAll);
        Assert.Equal(FakeLocalization.English()["Shapes_PinAll_Tip"], _vm.PinAllTip);
    }
}
