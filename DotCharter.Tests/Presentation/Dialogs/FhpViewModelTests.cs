using DotCharter.Presentation.Contracts.Dialogs;
using DotCharter.Presentation.Screens.Dialogs;

namespace DotCharter.Tests.Presentation.Dialogs;

/// <summary>
///     The fret-hand position editor: it edits the anchor it is given or the
///     one already on the snapped grid position, else creates one seeded from
///     the anchor spanning that position — and every save is one undo step.
/// </summary>
public sealed class FhpViewModelTests
{
    private static FhpViewModel Open(ChartEditHarness h, FhpRequest request)
    {
        var vm = new FhpViewModel(h.Services);
        vm.Configure(request);
        return vm;
    }

    [Fact]
    public void CreatingAnAnchor_ThenSwitchingArrangements_UndoesAndRedoesInItsOriginalCollection()
    {
        var h = new ChartEditHarness();
        var original = h.Editor.Anchors;
        var vm = Open(h, new FhpRequest(1000));
        vm.SaveCommand.Execute(null);
        var added = Assert.Single(original);
        var other = new Arrangement { Name = "Rhythm" };
        other.Levels.Add(new ChartLevel());
        h.Project.Arrangements.Add(other);
        h.Editor.ActiveArrangement = other;
        h.State.ActiveLevel = other.Levels[0];
        var otherAnchors = h.Editor.Anchors;
        Assert.NotSame(original, otherAnchors);

        h.History.Undo();
        Assert.Empty(original);
        Assert.Empty(otherAnchors);
        h.History.Redo();
        Assert.Same(added, Assert.Single(original));
        Assert.Empty(otherAnchors);
    }

    [Fact]
    public void MovingAnAnchor_ThenSwitchingLevels_ResortsItsOriginalCollectionOnUndoAndRedo()
    {
        var h = new ChartEditHarness();
        var original = h.Editor.Anchors;
        var first = new FretAnchor { Position = new FractionalPosition(1), Fret = 3 };
        var second = new FretAnchor { Position = new FractionalPosition(2), Fret = 5 };
        original.Add(first);
        original.Add(second);
        var after = first.Clone();
        after.Position = new FractionalPosition(3);
        h.History.Do(FhpAnchorEdit.Mutate(h.Editor, first, first.Clone(), after));
        Assert.Equal([second, first], original);
        var other = new ChartLevel();
        h.State.ActiveArrangement!.Levels.Add(other);
        h.State.ActiveLevel = other;

        h.History.Undo();
        Assert.Equal([first, second], original);
        Assert.Empty(other.Anchors);
        h.History.Redo();
        Assert.Equal([second, first], original);
        Assert.Empty(other.Anchors);
    }

    [Fact]
    public void A_new_anchor_starts_from_the_one_spanning_its_position_and_saves_as_one_undo_step()
    {
        var h = new ChartEditHarness();
        var earlier = new FretAnchor { Position = h.Editor.SnapToGrid(0), Fret = 7, Width = 5 };
        h.Editor.Anchors.Add(earlier);

        var vm = Open(h, new FhpRequest(2000));
        Assert.Equal(7, vm.Fret);
        Assert.Equal(5, vm.Width);

        vm.Fret = 9;
        vm.SaveCommand.Execute(null);

        Assert.True(ChartEditHarness.Answer(vm.Result));
        Assert.Equal(2, h.Editor.Anchors.Count);
        var added = h.Editor.Anchors[1];
        Assert.Equal(9, added.Fret);
        Assert.Equal(h.Editor.SnapToGrid(2000), added.Position);

        h.History.Undo();
        Assert.Same(earlier, Assert.Single(h.Editor.Anchors));
    }

    [Fact]
    public void A_new_anchor_starts_from_the_latest_anchor_by_position_not_the_last_one_stored()
    {
        var h = new ChartEditHarness();
        h.Editor.Anchors.Add(new FretAnchor { Position = h.Editor.SnapToGrid(1000), Fret = 9, Width = 5 });
        h.Editor.Anchors.Add(new FretAnchor { Position = h.Editor.SnapToGrid(0), Fret = 2, Width = 4 });

        var vm = Open(h, new FhpRequest(2000));

        Assert.Equal(9, vm.Fret);
        Assert.Equal(5, vm.Width);
    }

    [Fact]
    public void An_existing_anchor_is_edited_in_place_and_undo_puts_it_back()
    {
        var h = new ChartEditHarness();
        var anchor = new FretAnchor { Position = h.Editor.SnapToGrid(1000), Fret = 3, Width = 4 };
        h.Editor.Anchors.Add(anchor);

        var vm = Open(h, new FhpRequest(1000, anchor));
        Assert.Equal(3, vm.Fret);
        vm.Width = 6;
        vm.SaveCommand.Execute(null);

        Assert.Equal(6, Assert.Single(h.Editor.Anchors).Width);
        h.History.Undo();
        Assert.Equal(4, anchor.Width);
    }

    [Fact]
    public void The_anchor_already_on_the_snapped_position_is_found_without_being_named()
    {
        var h = new ChartEditHarness();
        var anchor = new FretAnchor { Position = h.Editor.SnapToGrid(1500), Fret = 12, Width = 4 };
        h.Editor.Anchors.Add(anchor);

        var vm = Open(h, new FhpRequest(1500));
        vm.Fret = 14;
        vm.SaveCommand.Execute(null);

        Assert.Equal(14, Assert.Single(h.Editor.Anchors).Fret);
    }

    [Fact]
    public void With_no_anchor_to_start_from_it_takes_the_selected_notes_fret()
    {
        var h = new ChartEditHarness();
        h.Editor.Selection.SelectedNote = new GuitarNote { Fret = 11 };

        Assert.Equal(11, Open(h, new FhpRequest(500)).Fret);
    }

    [Fact]
    public void Esc_answers_false_and_adds_nothing()
    {
        var h = new ChartEditHarness();

        var vm = Open(h, new FhpRequest(500));
        Assert.True(vm.TryCancel());

        Assert.False(ChartEditHarness.Answer(vm.Result));
        Assert.Empty(h.Editor.Anchors);
    }

    [Fact]
    public void Conforms_to_its_contract()
    {
        var h = new ChartEditHarness();
        Assert.Empty(ContractConformanceTests.Violations(typeof(IFhpScreen), Open(h, new FhpRequest(500))));
    }
}
