using Avalonia;
using DotCharter.Presentation.Contracts.Dialogs;
using DotCharter.Presentation.Screens.Dialogs;
using DotCharter.UI.Input;

namespace DotCharter.Tests.Presentation.Dialogs;

/// <summary>Pointer head decisions reach both editors without changing the anchor spanning a new placement.</summary>
public sealed class FhpLaneEditingTests
{
    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 0)]
    [InlineData(false, 11)]
    [InlineData(true, 11)]
    public void TailPlacementAddsOneAnchor_AndOneUndoRemovesOnlyThatAnchor(bool dialog, int previewFret)
    {
        var h = new ChartEditHarness();
        var earlier = new FretAnchor(new FractionalPosition(1), 3, 5);
        h.Editor.Anchors.Add(earlier);
        h.State.PreviewAnchorFret = previewFret;
        h.Editor.PlacedNotes.Add(new GuitarNote { Position = h.Editor.SnapToGrid(1250), Fret = 7 });
        var hit = Hit(h, 1250);
        Assert.Null(hit);

        if (dialog)
        {
            var vm = new FhpViewModel(h.Services);
            vm.Configure(new FhpRequest(1250, hit));
            Assert.Equal(previewFret > 0 ? previewFret : 7, vm.Fret);
            Assert.Equal(5, vm.Width);
            vm.Fret = 9;
            vm.SaveCommand.Execute(null);
        }
        else
        {
            var vm = new FhpInlineEditorViewModel(h.Services);
            vm.Begin(1250, hit);
            Assert.False(vm.IsEditingExisting);
            Assert.Equal(h.Editor.SnapToGrid(1250).GetPositionMs(h.Project.Beats), vm.PlacementMs);
            Assert.Equal(previewFret > 0 ? "11" : "7", vm.FretText);
            Assert.Equal("5", vm.WidthText);
            vm.FretText = "9";
            Assert.True(vm.Commit());
        }

        var added = Assert.Single(h.Editor.Anchors, a => !ReferenceEquals(a, earlier));
        Assert.Equal((h.Editor.SnapToGrid(1250), 9, 5), (added.Position, added.Fret, added.Width));
        Assert.Equal((new FractionalPosition(1), 3, 5), (earlier.Position, earlier.Fret, earlier.Width));
        h.History.Undo();
        Assert.Same(earlier, Assert.Single(h.Editor.Anchors));
        Assert.Equal((new FractionalPosition(1), 3, 5), (earlier.Position, earlier.Fret, earlier.Width));
        Assert.False(h.History.CanUndo);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void HeadOrOccupiedSnappedPositionEditsTheAnchorInOneUndoStep(bool dialog, bool occupiedSnap)
    {
        var h = new ChartEditHarness();
        h.Transport.GridDivisor = 1;
        var earlier = new FretAnchor(new FractionalPosition(1), 3, 5);
        h.Editor.Anchors.Add(earlier);
        const double tailTime = 1400;
        var target = occupiedSnap ? new FretAnchor(h.Editor.SnapToGrid(tailTime), 12, 6) : earlier;
        var time = occupiedSnap ? tailTime : 510;
        if (occupiedSnap) h.Editor.Anchors.Add(target);
        var hit = Hit(h, time);
        if (occupiedSnap)
            Assert.Null(hit);
        else
            Assert.Same(target, hit);

        var before = target.Clone();
        if (dialog)
        {
            var vm = new FhpViewModel(h.Services);
            vm.Configure(new FhpRequest(time, hit));
            Assert.Equal(target.Fret, vm.Fret);
            vm.Fret = 14;
            vm.SaveCommand.Execute(null);
        }
        else
        {
            var vm = new FhpInlineEditorViewModel(h.Services);
            vm.Begin(time, hit);
            Assert.True(vm.IsEditingExisting);
            vm.FretText = "14";
            Assert.True(vm.Commit());
        }

        Assert.Equal(occupiedSnap ? 2 : 1, h.Editor.Anchors.Count);
        Assert.Contains(target, h.Editor.Anchors);
        Assert.Equal((before.Position, 14, before.Width), (target.Position, target.Fret, target.Width));
        h.History.Undo();
        Assert.Equal((before.Position, before.Fret, before.Width), (target.Position, target.Fret, target.Width));
        Assert.False(h.History.CanUndo);
    }

    private static FretAnchor? Hit(ChartEditHarness h, double timeMs) =>
        ChartHitTester.HitTestAnchorHead(new Point(timeMs, 109), h.Editor.Anchors, 450, 1, 1600, 100,
            h.Project.Beats, _ => 16f);
}
