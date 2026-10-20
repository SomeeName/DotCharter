using DotCharter.Tone.Audio.Dsp.Presets;
using DotCharter.Tone.Audio.Dsp.RsToneConversion;
using DotCharter.Tone.UI.Controls.Dsp;
using DotCharter.Tone.UI.ViewModels.PlayMode;

namespace DotCharter.Tests.UI.ViewModels.PlayMode;

/// <summary>
/// VM tests for the tone-preset browser against a fake
/// <see cref="IDspTonePresetService" />: category filtering fills the card grid,
/// applying a card calls the service + refreshes the board, and
/// save-as/overwrite/delete drive the service and refresh the user list. No
/// Avalonia instance.
/// </summary>
public sealed class PresetBrowserViewModelTests
{
    private static PresetBrowserViewModel NewVm(out FakeDspTonePresetService svc, out int applyCount)
    {
        svc = new FakeDspTonePresetService();
        var appliedCounter = 0;
        // captured rig is irrelevant to these tests; hand back an empty rig.
        var vm = new PresetBrowserViewModel(svc, FakeLocalization.English(), () => new DcRigPreset([]), () => appliedCounter++);
        applyCount = appliedCounter;
        return vm;
    }

    private static PresetBrowserViewModel NewVm(out FakeDspTonePresetService svc, out System.Func<int> applied)
    {
        svc = new FakeDspTonePresetService();
        var counter = 0;
        var s = svc;
        var vm = new PresetBrowserViewModel(s, FakeLocalization.English(), () => new DcRigPreset([]), () => counter++);
        applied = () => counter;
        return vm;
    }

    private static PresetRailRow Rail(PresetBrowserViewModel vm, PresetFilterKind kind, DspToneCategory cat = default)
        => vm.Rail.First(r => r.Kind == kind && (kind != PresetFilterKind.Category || r.Category == cat));

    [Fact]
    public void Card_module_glyph_strip_mirrors_the_rig_chain_in_order()
    {
        var preset = new DspTonePreset("p", "Chain", DspToneCategory.Crunch, "", new DcRigPreset(
        [
            new DcRigModule("overdrive", new System.Collections.Generic.Dictionary<string, float>(), false),
            new DcRigModule("amp", new System.Collections.Generic.Dictionary<string, float>(), false),
            new DcRigModule("cab", new System.Collections.Generic.Dictionary<string, float>(), false)
        ]));

        var card = new PresetCardViewModel(preset, "Crunch");

        Assert.Equal(new[] { "overdrive", "amp", "cab" },
            card.ModuleGlyphs.Select(g => g.ModuleTypeId).ToArray());
        // no display-name seam here: each unit falls back to its own id
        Assert.Equal(new[] { "overdrive", "amp", "cab" },
            card.ModuleGlyphs.Select(g => g.Name).ToArray());
    }

    /// <summary>
    ///     The search box may not rebuild the 83-card grid once per keystroke —
    ///     the <c>CoalescedFilterPass</c> rule the song library already follows.
    ///     A typing burst schedules ONE pass (run when the injected post
    ///     drains), and a rail click runs immediately, dropping the pending one.
    /// </summary>
    [Fact]
    public void Typing_a_search_burst_schedules_one_refresh_not_one_per_key()
    {
        var queue = new List<System.Action>();
        var svc = new FakeDspTonePresetService();
        var vm = new PresetBrowserViewModel(svc, FakeLocalization.English(), () => new DcRigPreset([]), () => { },
            searchPost: queue.Add);
        queue.Clear(); // ctor-time refreshes are not the claim

        var baseline = vm.Cards.Count;
        vm.SearchText = "z";
        vm.SearchText = "zz";
        vm.SearchText = "zzz-no-such-preset";

        Assert.Single(queue);                 // three keystrokes, one scheduled pass
        Assert.Equal(baseline, vm.Cards.Count); // nothing rebuilt mid-burst

        queue[0]();
        Assert.Empty(vm.Cards);              // the drained pass applied the filter

        // A rail click is discrete: it refreshes NOW, without waiting for a post.
        queue.Clear();
        vm.SearchText = string.Empty;        // schedules (pending)
        var category = svc.Presets[0].Category;
        vm.SelectedRail = Rail(vm, PresetFilterKind.Category, category);
        Assert.True(vm.Cards.Count > 0,
            "a rail click must refresh immediately, not wait for the post to drain");
    }

    [Fact]
    public void Ctor_builds_rail_all_categories_and_user()
    {
        var vm = NewVm(out _, out int _);

        // All + 8 categories + Legacy + My Tones = 11 rail rows.
        Assert.Equal(11, vm.Rail.Count);
        Assert.Equal(PresetFilterKind.All, vm.Rail.First().Kind);
        Assert.Equal(PresetFilterKind.User, vm.Rail.Last().Kind);
        // The Legacy shelf sits between the categories and My Tones: it is a
        // browsable shelf of the algorithmic originals, not a hidden option.
        Assert.Contains(vm.Rail, r => r.Kind == PresetFilterKind.Legacy);
        // Default selection = All → every preset shown.
        Assert.Equal(vm.Rail.First(), vm.SelectedRail);
        Assert.Equal(6, vm.Cards.Count);
    }

    [Fact]
    public void Selecting_category_filters_cards()
    {
        var vm = NewVm(out _, out int _);

        vm.SelectedRail = Rail(vm, PresetFilterKind.Category, DspToneCategory.Clean);

        Assert.Equal(2, vm.Cards.Count);
        Assert.All(vm.Cards, c => Assert.Equal(DspToneCategory.Clean, c.Category));
        Assert.True(vm.SelectedRail!.IsSelected);
    }

    [Fact]
    public void Selecting_user_rail_shows_only_user_presets()
    {
        var vm = NewVm(out var svc, out int _);
        svc.SaveUser("Mine", DspToneCategory.Lead, "", new DcRigPreset([]));

        vm.SelectedRail = Rail(vm, PresetFilterKind.User);

        Assert.Single(vm.Cards);
        Assert.True(vm.Cards[0].IsUser);
    }

    [Fact]
    public void ApplyCard_calls_service_and_refreshes_board()
    {
        var vm = NewVm(out var svc, out System.Func<int> applied);
        var card = vm.Cards.First();

        vm.ApplyCard(card);

        Assert.Equal(card.Id, svc.Applied.Single());
        Assert.Equal(card, vm.SelectedCard);
        Assert.True(card.IsSelected);
        Assert.Equal(1, applied());
    }

    [Fact]
    public void SelectedCard_selection_is_exclusive()
    {
        var vm = NewVm(out _, out int _);
        var a = vm.Cards[0];
        var b = vm.Cards[1];

        vm.ApplyCard(a);
        vm.ApplyCard(b);

        Assert.False(a.IsSelected);
        Assert.True(b.IsSelected);
    }

    [Fact]
    public void SaveAsNew_adds_a_user_preset_and_selects_it()
    {
        var vm = NewVm(out var svc, out int _);
        vm.SaveName = "Chunky Rhythm";
        vm.SelectedSaveCategory = vm.SaveCategories.First(o => o.Value == DspToneCategory.HighGain);

        vm.ConfirmSave();

        var saved = svc.UserPresets.Single();
        Assert.Equal("Chunky Rhythm", saved.Name);
        Assert.Equal(DspToneCategory.HighGain, saved.Category);
        Assert.Null(svc.Saves.Single().OverwriteId); // a create, not an overwrite
        // Jumped to My Tones with the new card selected.
        Assert.Equal(PresetFilterKind.User, vm.SelectedRail!.Kind);
        Assert.Equal(saved.Id, vm.SelectedCard!.Id);
        Assert.False(vm.IsSaveDialogOpen);
    }

    [Fact]
    public void SaveAsNew_blank_name_uses_default()
    {
        var vm = NewVm(out var svc, out int _);
        vm.SaveName = "   ";

        vm.ConfirmSave();

        Assert.False(string.IsNullOrWhiteSpace(svc.UserPresets.Single().Name));
    }

    [Fact]
    public void Overwrite_replaces_user_preset_in_place()
    {
        var vm = NewVm(out var svc, out int _);
        var existing = svc.SaveUser("Old Name", DspToneCategory.Lead, "d", new DcRigPreset([]));
        vm.SelectedRail = Rail(vm, PresetFilterKind.User);
        var card = vm.Cards.Single(c => c.Id == existing.Id);
        vm.SelectedCard = card;

        Assert.True(vm.CanOverwrite);
        vm.Overwrite();

        // Same id — replaced in place, not a new preset.
        Assert.Single(svc.UserPresets);
        Assert.Equal(existing.Id, svc.Saves.Last().OverwriteId);
        Assert.Equal(existing.Id, svc.UserPresets[0].Id);
    }

    [Fact]
    public void Overwrite_disabled_for_factory_selection()
    {
        var vm = NewVm(out _, out int _);
        vm.SelectedCard = vm.Cards.First(); // a factory preset

        Assert.False(vm.CanOverwrite);
        Assert.False(vm.OverwriteCommand.CanExecute(null));
    }

    [Fact]
    public void Delete_removes_user_preset_and_clears_selection()
    {
        var vm = NewVm(out var svc, out int _);
        var existing = svc.SaveUser("Doomed", DspToneCategory.Bass, "", new DcRigPreset([]));
        vm.SelectedRail = Rail(vm, PresetFilterKind.User);
        vm.SelectedCard = vm.Cards.Single(c => c.Id == existing.Id);

        Assert.True(vm.CanDelete);
        vm.Delete();
        // the head's Delete… asks first, like the card menu's
        Assert.Empty(svc.Deletes);
        Assert.True(vm.IsDeleteConfirmOpen);
        vm.ConfirmDelete();

        Assert.Equal(existing.Id, svc.Deletes.Single());
        Assert.Empty(svc.UserPresets);
        Assert.Empty(vm.Cards);
        Assert.Null(vm.SelectedCard);
    }

    [Fact]
    public void Delete_disabled_for_factory_selection()
    {
        var vm = NewVm(out _, out int _);
        vm.SelectedCard = vm.Cards.First();

        Assert.False(vm.CanDelete);
        Assert.False(vm.DeleteCommand.CanExecute(null));
    }

    [Fact]
    public void Search_filters_current_rail()
    {
        var vm = NewVm(out _, out int _);

        vm.SearchText = "clean";

        Assert.All(vm.Cards, c =>
            Assert.Contains("clean", c.Name, System.StringComparison.OrdinalIgnoreCase));
        Assert.NotEmpty(vm.Cards);
    }

    [Fact]
    public void NextPreset_cycles_and_applies()
    {
        var vm = NewVm(out var svc, out int _);
        vm.ApplyCard(vm.Cards[0]);
        svc.Applied.Clear();

        vm.NextPresetCommand.Execute(null);

        Assert.Equal(vm.Cards[1], vm.SelectedCard);
        Assert.Equal(vm.Cards[1].Id, svc.Applied.Single());
    }

    [Fact]
    public void PrevPreset_wraps_to_last()
    {
        var vm = NewVm(out _, out int _);
        vm.ApplyCard(vm.Cards[0]);

        vm.PrevPresetCommand.Execute(null);

        Assert.Equal(vm.Cards.Last(), vm.SelectedCard);
    }

    // ---- the tone card's right-click menu ---------------------------------------

    private static DcRigPreset RigOf(params string[] modules) =>
        new(modules.Select(m => new DcRigModule(m, new System.Collections.Generic.Dictionary<string, float> { ["x"] = 0.3f })).ToList());

    /// <summary>A browser whose LIVE rig is a distinctive single "live-unit", so a
    /// test can tell the live rig from a card's own rig.</summary>
    private static PresetBrowserViewModel NewVmWithLiveRig(out FakeDspTonePresetService svc)
    {
        svc = new FakeDspTonePresetService();
        return new PresetBrowserViewModel(svc, FakeLocalization.English(), () => RigOf("live-unit"), () => { });
    }

    private static PresetCardViewModel UserCard(PresetBrowserViewModel vm, DspTonePreset saved)
    {
        vm.SelectedRail = Rail(vm, PresetFilterKind.User);
        return vm.Cards.Single(c => c.Id == saved.Id);
    }

    [Fact]
    public void EditDetails_keeps_the_id_and_the_rig_and_changes_name_description_category()
    {
        var vm = NewVmWithLiveRig(out var svc);
        var saved = svc.SaveUser("Old Name", DspToneCategory.Lead, "old note", RigOf("overdrive", "amp"));
        var card = UserCard(vm, saved);

        // The dialog's words are compared across its two modes, never against a
        // resx read: another collection may switch the culture mid-test.
        var saveTitle = vm.SaveDialogTitle;
        var saveConfirm = vm.SaveDialogConfirmText;
        Assert.True(vm.BeginEditCardCommand.CanExecute(card));
        vm.BeginEditCardCommand.Execute(card);

        // the ONE dialog opens pre-filled, in EDIT mode, and says so
        Assert.True(vm.IsSaveDialogOpen);
        Assert.True(vm.IsEditingDetails);
        Assert.Equal("Old Name", vm.SaveName);
        Assert.Equal("old note", vm.SaveDescription);
        Assert.Equal(DspToneCategory.Lead, vm.SelectedSaveCategory!.Value);
        Assert.NotEqual(saveTitle, vm.SaveDialogTitle);
        Assert.NotEqual(saveConfirm, vm.SaveDialogConfirmText);

        vm.SaveName = "  New Name  ";
        vm.SaveDescription = "new note";
        vm.SelectedSaveCategory = vm.SaveCategories.First(o => o.Value == DspToneCategory.Ambient);
        vm.ConfirmSave();

        var save = svc.Saves.Last();
        Assert.Equal(saved.Id, save.OverwriteId);                 // the SAME preset…
        Assert.Equal(new[] { "overdrive", "amp" },                // …with its OWN rig, not the live one
            save.Rig.Modules.Select(m => m.ModuleTypeId).ToArray());
        var edited = Assert.Single(svc.UserPresets);
        Assert.Equal(saved.Id, edited.Id);
        Assert.Equal("New Name", edited.Name);
        Assert.Equal("new note", edited.Description);
        Assert.Equal(DspToneCategory.Ambient, edited.Category);

        Assert.False(vm.IsSaveDialogOpen);
        Assert.False(vm.IsEditingDetails);
        Assert.Equal(saveTitle, vm.SaveDialogTitle);
        Assert.Equal(saved.Id, vm.SelectedCard!.Id);
        Assert.Equal("New Name", vm.SelectedCard.Name);
    }

    [Fact]
    public void EditDetails_of_the_active_tone_renames_the_active_tone()
    {
        var vm = NewVmWithLiveRig(out var svc);
        var saved = svc.SaveUser("Before", DspToneCategory.Clean, "", RigOf("amp"));
        var card = UserCard(vm, saved);
        vm.ApplyCard(card);

        vm.BeginEditCardCommand.Execute(card);
        vm.SaveName = "After";
        vm.ConfirmSave();

        Assert.Equal("After", vm.ActivePresetName);
    }

    [Fact]
    public void Cancelling_an_edit_leaves_edit_mode_so_SaveAsNew_is_a_create_again()
    {
        var vm = NewVmWithLiveRig(out var svc);
        var saved = svc.SaveUser("Mine", DspToneCategory.Clean, "", RigOf("amp"));
        var card = UserCard(vm, saved);

        vm.BeginEditCardCommand.Execute(card);
        vm.CancelSaveCommand.Execute(null);
        Assert.False(vm.IsEditingDetails);

        // …and a host's Esc, which writes the flag directly, leaves it too
        vm.BeginEditCardCommand.Execute(card);
        vm.IsSaveDialogOpen = false;
        Assert.False(vm.IsEditingDetails);

        vm.BeginSaveAsNewCommand.Execute(null);
        vm.SaveName = "Fresh";
        vm.ConfirmSave();
        Assert.Null(svc.Saves.Last().OverwriteId);
        Assert.Equal("live-unit", svc.Saves.Last().Rig.Modules.Single().ModuleTypeId);
        Assert.Equal(2, svc.UserPresets.Count);
    }

    [Fact]
    public void Duplicate_of_a_user_tone_creates_a_new_user_preset_with_the_same_rig_and_the_copy_name()
    {
        var vm = NewVmWithLiveRig(out var svc);
        var saved = svc.SaveUser("Crunchy", DspToneCategory.Crunch, "note", RigOf("overdrive", "amp", "cab"));
        var card = UserCard(vm, saved);
        svc.Applied.Clear();

        vm.DuplicateCardCommand.Execute(card);

        Assert.Equal(2, svc.UserPresets.Count);
        var copy = svc.UserPresets.Single(p => p.Id != saved.Id);
        Assert.Null(svc.Saves.Last().OverwriteId);                // a create
        Assert.StartsWith("Crunchy", copy.Name, System.StringComparison.Ordinal);
        Assert.NotEqual("Crunchy", copy.Name);
        Assert.Equal(DspToneCategory.Crunch, copy.Category);
        Assert.Equal(new[] { "overdrive", "amp", "cab" }, copy.Rig.Modules.Select(m => m.ModuleTypeId).ToArray());
        // selected in My Tones, NOT applied
        Assert.Equal(PresetFilterKind.User, vm.SelectedRail!.Kind);
        Assert.Equal(copy.Id, vm.SelectedCard!.Id);
        Assert.Empty(svc.Applied);
    }

    [Fact]
    public void Duplicate_of_a_built_in_makes_it_the_players_own()
    {
        var vm = NewVmWithLiveRig(out var svc);
        var builtIn = vm.Cards.First(c => !c.IsUser);

        Assert.True(vm.DuplicateCardCommand.CanExecute(builtIn));
        vm.DuplicateCardCommand.Execute(builtIn);

        var copy = Assert.Single(svc.UserPresets);
        Assert.True(copy.IsUser);
        Assert.StartsWith(builtIn.Name, copy.Name, System.StringComparison.Ordinal);
        Assert.NotEqual(builtIn.Name, copy.Name);
        Assert.Same(builtIn.Preset.Rig, copy.Rig);
        Assert.True(vm.SelectedCard!.IsUser);
        Assert.True(PresetBrowserViewModel.CanChangeCard(vm.SelectedCard));
    }

    [Fact]
    public void OverwriteCard_writes_the_live_rig_into_THAT_card_not_the_selection()
    {
        var vm = NewVmWithLiveRig(out var svc);
        var a = svc.SaveUser("A", DspToneCategory.Clean, "", RigOf("amp"));
        var b = svc.SaveUser("B", DspToneCategory.Lead, "keep", RigOf("amp"));
        vm.SelectedRail = Rail(vm, PresetFilterKind.User);
        vm.SelectedCard = vm.Cards.Single(c => c.Id == a.Id);

        vm.OverwriteCardCommand.Execute(vm.Cards.Single(c => c.Id == b.Id));

        var save = svc.Saves.Last();
        Assert.Equal(b.Id, save.OverwriteId);
        Assert.Equal("live-unit", save.Rig.Modules.Single().ModuleTypeId);
        var written = svc.UserPresets.Single(p => p.Id == b.Id);
        Assert.Equal("B", written.Name);
        Assert.Equal("keep", written.Description);
        Assert.Equal("amp", svc.UserPresets.Single(p => p.Id == a.Id).Rig.Modules.Single().ModuleTypeId);
    }

    [Fact]
    public void Delete_asks_first_and_nothing_is_deleted_until_it_is_confirmed()
    {
        var vm = NewVmWithLiveRig(out var svc);
        var saved = svc.SaveUser("Doomed", DspToneCategory.Bass, "", RigOf("amp"));
        var card = UserCard(vm, saved);
        vm.ApplyCard(card);

        vm.RequestDeleteCardCommand.Execute(card);

        Assert.True(vm.IsDeleteConfirmOpen);
        Assert.Same(card, vm.PendingDelete);
        Assert.Contains("Doomed", vm.DeleteConfirmTitle);
        Assert.Empty(svc.Deletes);                                // ASKED, not deleted
        Assert.Single(svc.UserPresets);

        vm.ConfirmDeleteCommand.Execute(null);

        Assert.Equal(saved.Id, svc.Deletes.Single());
        Assert.Empty(svc.UserPresets);
        Assert.False(vm.IsDeleteConfirmOpen);
        Assert.DoesNotContain(vm.Cards, c => c.Id == saved.Id);
        // it was the active tone, so nothing is active any more
        Assert.DoesNotContain("Doomed", vm.ActivePresetName, System.StringComparison.Ordinal);
    }

    [Fact]
    public void Cancelling_the_delete_confirmation_keeps_the_tone()
    {
        var vm = NewVmWithLiveRig(out var svc);
        var saved = svc.SaveUser("Keeper", DspToneCategory.Bass, "", RigOf("amp"));
        var card = UserCard(vm, saved);

        vm.RequestDeleteCardCommand.Execute(card);
        vm.CancelDeleteCommand.Execute(null);

        Assert.False(vm.IsDeleteConfirmOpen);
        Assert.Empty(svc.Deletes);
        Assert.Single(svc.UserPresets);
        Assert.Contains(vm.Cards, c => c.Id == saved.Id);

        // a stale confirm after a cancel deletes nothing either
        vm.ConfirmDeleteCommand.Execute(null);
        Assert.Empty(svc.Deletes);
    }

    [Fact]
    public void Rename_overwrite_and_delete_refuse_a_built_in_and_say_why()
    {
        var vm = NewVmWithLiveRig(out var svc);
        var builtIn = vm.Cards.First(c => !c.IsUser);

        Assert.False(PresetBrowserViewModel.CanChangeCard(builtIn));
        Assert.False(vm.BeginEditCardCommand.CanExecute(builtIn));
        Assert.False(vm.OverwriteCardCommand.CanExecute(builtIn));
        Assert.False(vm.RequestDeleteCardCommand.CanExecute(builtIn));
        Assert.False(string.IsNullOrWhiteSpace(vm.LockedReason(builtIn)));

        // …and executing them anyway (a stale menu) changes nothing
        vm.BeginEditCardCommand.Execute(builtIn);
        vm.OverwriteCardCommand.Execute(builtIn);
        vm.RequestDeleteCardCommand.Execute(builtIn);
        Assert.False(vm.IsSaveDialogOpen);
        Assert.False(vm.IsDeleteConfirmOpen);
        Assert.Empty(svc.Saves);
        Assert.Empty(svc.Deletes);

        // a user tone's items are live and carry no reason
        var mine = UserCard(vm, svc.SaveUser("Mine", DspToneCategory.Clean, "", RigOf("amp")));
        Assert.True(vm.OverwriteCardCommand.CanExecute(mine));
        Assert.Null(vm.LockedReason(mine));
    }
}
