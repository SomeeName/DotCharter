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
