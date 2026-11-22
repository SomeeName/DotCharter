using DotCharter.Presentation.Screens.Dialogs;

namespace DotCharter.Tests.UI.Input.Tools;

/// <summary>Actual pointer handlers retain the pressed row through hover, editor opening, commit and undo.</summary>
public sealed class FretAnchorEditRoutingTests
{
    [Theory]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(false, true)]
    public void TailPressDraftsInThePressedRow_AndCommitAndUndoStayThere(bool labels, bool dialog)
    {
        var h = new FretAnchorToolHarness(labels);
        var activeAnchors = h.Chart.Editor.Anchors;
        var activeBefore = activeAnchors.ToArray();
        h.Hover(1250);
        Assert.Null(h.Track.Selection.HoveredAnchor);
        Assert.Equal(new FractionalPosition(2, new Fraction(1, 2)), Assert.Single(h.Track.Selection.PreviewAnchors).Position);
        h.Press(1250);
        var request = Assert.Single(h.Launcher.Requests);
        Assert.Null(request.Anchor);
        Assert.Same(h.Track, request.Target!.Track);
        Assert.Equal(new FractionalPosition(2, new Fraction(1, 2)), request.Target.Position);
        if (dialog)
        {
            var vm = new FhpViewModel(h.Chart.Services);
            vm.Configure(request);
            vm.Fret = 9;
            vm.SaveCommand.Execute(null);
        }
        else
        {
            var vm = new FhpInlineEditorViewModel(h.Chart.Services);
            vm.Begin(request);
            Assert.False(vm.IsEditingExisting);
            vm.FretText = "9";
            Assert.True(vm.Commit());
        }
        var added = Assert.Single(h.Track.Anchors, a => !ReferenceEquals(a, h.Anchor));
        Assert.Equal((request.Target.Position, 9, 5), (added.Position, added.Fret, added.Width));
        Assert.Equal(3, h.Anchor.Fret);
        if (!labels) Assert.Equal(activeBefore, activeAnchors);
        h.Chart.History.Undo();
        Assert.Same(h.Anchor, Assert.Single(h.Track.Anchors));
        Assert.False(h.Chart.History.CanUndo);
        if (!labels) Assert.Equal(activeBefore, activeAnchors);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void BadgeOrUnlabelledMarkerPressAndHoverNameTheSameAnchor(bool labels)
    {
        var h = new FretAnchorToolHarness(labels);
        h.Hover(508);
        Assert.Same(h.Anchor, h.Track.Selection.HoveredAnchor);
        Assert.Empty(h.Track.Selection.PreviewAnchors);
        h.Press(508);
        var request = Assert.Single(h.Launcher.Requests);
        Assert.Same(h.Anchor, request.Anchor);
        Assert.Same(h.Track, request.Target!.Track);
        var editor = new FhpViewModel(h.Chart.Services);
        editor.Configure(request);
        editor.Fret = 11;
        editor.SaveCommand.Execute(null);
        Assert.Equal(11, Assert.Single(h.Track.Anchors).Fret);
        if (!labels) Assert.Empty(h.Chart.Editor.Anchors);
        h.Chart.History.Undo();
        Assert.Equal(3, h.Anchor.Fret);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void DoubleClickOnTheTailDoesNotEditTheEarlierAnchor(bool labels)
    {
        var h = new FretAnchorToolHarness(labels);
        h.Press(1250, right: false);
        h.Press(1250, right: false, clicks: 2);
        var request = Assert.Single(h.Launcher.Requests);
        Assert.Null(request.Anchor);
        Assert.Same(h.Track, request.Target!.Track);
        Assert.Equal(new FractionalPosition(2, new Fraction(1, 2)), request.Target.Position);
        Assert.Equal(3, Assert.Single(h.Track.Anchors).Fret);
    }

    [Fact]
    public void SnappingUsesThePressedRowsSoundsInsteadOfTheActiveRowsMagnet()
    {
        var h = new FretAnchorToolHarness(false);
        h.Chart.Editor.PlacedNotes.Add(new GuitarNote { Position = new FractionalPosition(2, new Fraction(12, 25)), Fret = 7 });
        h.Press(1240);
        Assert.Equal(new FractionalPosition(2, new Fraction(1, 2)), Assert.Single(h.Launcher.Requests).Target!.Position);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OccupiedSnapsAndPendingCommitsKeepTheClickedCollectionWhenItsLevelChanges(bool dialog)
    {
        var h = new FretAnchorToolHarness(false);
        var position = new FractionalPosition(2, new Fraction(1, 2));
        var active = new FretAnchor(position, 20, 4);
        var clicked = new FretAnchor(position, 12, 6);
        h.Chart.Editor.Anchors.Add(active);
        var original = h.Track.Anchors;
        original.Add(clicked);
        h.Press(1240);
        var request = Assert.Single(h.Launcher.Requests);
        Assert.Null(request.Anchor);
        var replacement = new ChartLevel();
        h.Track.Arrangement.Levels.Add(replacement);
        if (dialog)
        {
            var vm = new FhpViewModel(h.Chart.Services);
            vm.Configure(request);
            Assert.Equal(12, vm.Fret);
            h.Track.Level = replacement;
            vm.Fret = 9;
            vm.SaveCommand.Execute(null);
        }
        else
        {
            var vm = new FhpInlineEditorViewModel(h.Chart.Services);
            vm.Begin(request);
            Assert.True(vm.IsEditingExisting);
            Assert.Equal("12", vm.FretText);
            h.Track.Level = replacement;
            vm.FretText = "9";
            Assert.True(vm.Commit());
        }

        Assert.Equal(9, clicked.Fret);
        Assert.Contains(clicked, original);
        Assert.Empty(replacement.Anchors);
        Assert.Equal(20, active.Fret);
        h.Chart.History.Undo();
        Assert.Equal(12, clicked.Fret);
        Assert.Equal(20, active.Fret);
        Assert.Empty(replacement.Anchors);
    }
}
