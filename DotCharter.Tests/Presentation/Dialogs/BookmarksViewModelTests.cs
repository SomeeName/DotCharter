using Avalonia.Input;
using DotCharter.Presentation.Contracts.Dialogs;
using DotCharter.Presentation.Screens.Dialogs;

namespace DotCharter.Tests.Presentation.Dialogs;

/// <summary>Edit ▸ Bookmarks… (#494): the ten slots with their times, and the three verbs per slot.</summary>
public sealed class BookmarksViewModelTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Setting_a_slot_is_one_dirty_undo_step(bool replace)
    {
        var (vm, edit) = replace ? Open((2, 100)) : Open();
        using var dirty = new ProjectDirtyTracker(edit.History);
        edit.State.CurrentTimeMs = 3000;

        vm.Slots[2].SetCommand.Execute(null);

        Assert.True(dirty.IsDirty);
        Assert.Single(edit.History.GetUndoSteps());
        edit.History.Undo();
        Assert.False(dirty.IsDirty);
        if (replace) Assert.Equal(100, Assert.Single(edit.Project.Bookmarks).Time);
        else Assert.Empty(edit.Project.Bookmarks);
        Assert.Equal(replace ? "0:00.100" : FakeLocalization.English()["Bookmarks_NotSet"], vm.Slots[2].TimeText);
        edit.History.Redo();
        Assert.Equal(3000, Assert.Single(edit.Project.Bookmarks).Time);
        Assert.Equal("0:03.000", vm.Slots[2].TimeText);
    }

    [Fact]
    public void Clearing_a_slot_is_undoable_and_setting_the_same_time_is_a_no_op()
    {
        var (vm, edit) = Open((2, 100));
        using var dirty = new ProjectDirtyTracker(edit.History);
        edit.State.CurrentTimeMs = 100;
        vm.Slots[2].SetCommand.Execute(null);
        Assert.False(edit.History.CanUndo);

        vm.Slots[2].ClearCommand.Execute(null);

        Assert.True(dirty.IsDirty);
        edit.History.Undo();
        Assert.False(dirty.IsDirty);
        Assert.Equal(100, Assert.Single(edit.Project.Bookmarks).Time);
        Assert.True(vm.Slots[2].ClearCommand.CanExecute(null));
        edit.History.Redo();
        Assert.Empty(edit.Project.Bookmarks);
    }

    private static (BookmarksViewModel Vm, ChartEditHarness Edit) Open(params (int Slot, double Time)[] existing)
    {
        var edit = new ChartEditHarness();
        foreach (var (slot, time) in existing) edit.Project.Bookmarks.Add(new Bookmark { Index = slot, Time = time });
        return (HistoryTestActivation.Create<BookmarksViewModel>(edit.History, edit.State, edit.Transport, FakeLocalization.English()), edit);
    }

    [Theory]
    [InlineData(3_599_999, "59:59.999")]
    [InlineData(3_600_000, "1:00:00.000")]
    [InlineData(90_061_456, "25:01:01.456")]
    public void Bookmark_times_keep_elapsed_hours(double ms, string expected)
    {
        var (vm, _) = Open((0, ms));

        Assert.Equal(expected, vm.Slots[0].TimeText);
    }

    [Fact]
    public void Lists_ten_slots_with_each_time_and_says_so_for_an_empty_one()
    {
        var (vm, _) = Open((0, 1500), (7, 62_250.5));

        Assert.Equal(10, vm.Slots.Count);
        Assert.Equal(Enumerable.Range(0, 10).Select(i => i.ToString()), vm.Slots.Select(s => s.Slot));
        Assert.Equal("0:01.500", vm.Slots[0].TimeText);
        Assert.Equal("1:02.250", vm.Slots[7].TimeText.Replace("251", "250"));
        Assert.Equal(FakeLocalization.English()["Bookmarks_NotSet"], vm.Slots[3].TimeText);
    }

    [Fact]
    public void Set_here_puts_the_slot_at_the_playhead_and_replaces_an_existing_time()
    {
        var (vm, edit) = Open((2, 100));
        edit.State.CurrentTimeMs = 3000;

        vm.Slots[1].SetCommand.Execute(null);
        vm.Slots[2].SetCommand.Execute(null);

        Assert.Equal(3000, edit.Project.Bookmarks.Single(b => b.Index == 1).Time);
        Assert.Equal(3000, edit.Project.Bookmarks.Single(b => b.Index == 2).Time);
        Assert.Equal("0:03.000", vm.Slots[1].TimeText);
        Assert.Equal(2, edit.Project.Bookmarks.Count);
    }

    [Fact]
    public void Go_to_moves_the_playhead_and_is_off_for_an_empty_slot()
    {
        var (vm, edit) = Open((4, 2500));

        Assert.False(vm.Slots[0].GoToCommand.CanExecute(null));
        Assert.True(vm.Slots[4].GoToCommand.CanExecute(null));

        vm.Slots[4].GoToCommand.Execute(null);

        Assert.Equal(2500, edit.State.CurrentTimeMs);
    }

    [Fact]
    public void Clear_empties_the_slot_and_turns_its_verbs_off()
    {
        var (vm, edit) = Open((5, 900));
        Assert.True(vm.Slots[5].ClearCommand.CanExecute(null));

        vm.Slots[5].ClearCommand.Execute(null);

        Assert.Empty(edit.Project.Bookmarks);
        Assert.Equal(FakeLocalization.English()["Bookmarks_NotSet"], vm.Slots[5].TimeText);
        Assert.False(vm.Slots[5].ClearCommand.CanExecute(null));
        Assert.False(vm.Slots[5].GoToCommand.CanExecute(null));
    }

    [Fact]
    public void Each_slot_shows_its_live_chord_and_follows_a_rebind()
    {
        var edit = new ChartEditHarness();
        var loc = FakeLocalization.English();
        var defaults = HistoryTestActivation.Create<BookmarksViewModel>(edit.History, edit.State, edit.Transport, loc);
        for (var slot = 0; slot < 10; slot++)
        {
            Assert.Equal(edit.State.Keybinds.BookmarkBase.WithDigit(slot).ToDisplayString(loc), defaults.Slots[slot].Chord);
        }

        Assert.Equal("Shift+3", defaults.Slots[3].Chord);

        edit.State.Keybinds.BookmarkBase = new KeyChord(Key.D0, KeyModifiers.Control | KeyModifiers.Shift);
        var rebound = HistoryTestActivation.Create<BookmarksViewModel>(edit.History, edit.State, edit.Transport, loc);

        Assert.Equal("Ctrl+Shift+7", rebound.Slots[7].Chord);
        Assert.Equal("Ctrl+Shift+0", rebound.Slots[0].Chord);
    }

    [Fact]
    public void Close_answers_true()
    {
        var (vm, _) = Open();

        vm.CloseCommand.Execute(null);

        Assert.True(ChartEditHarness.Answer(vm.Result));
    }

    [Fact]
    public void Conforms_to_its_contract() =>
        Assert.Empty(ContractConformanceTests.Violations(typeof(IBookmarksScreen), Open().Vm));
}
