using DotCharter.Tone.Audio.Dsp;
using DotCharter.Tone.UI.Controls.Dsp;
using DotCharter.Tone.UI.ViewModels.PlayMode;

namespace DotCharter.Tests.UI.ViewModels.PlayMode;

/// <summary>
/// Pure VM tests for the tone-rig pedalboard against an in-memory fake
/// <see cref="IDspChainService" />. No Avalonia instance: exercises add /
/// reorder / remove / bypass / param-set and asserts the ordered list mirrors
/// the service chain.
/// </summary>
public sealed class PedalboardViewModelTests
{
    private static PedalboardViewModel NewVm(out FakeDspChainService dsp)
    {
        dsp = new FakeDspChainService();
        return TestRigBuilders.Pedalboard(dsp, new FakeDspTonePresetService(), FakeLocalization.English());
    }

    private static PedalNodeViewModel Add(PedalboardViewModel vm, string moduleTypeId)
    {
        var node = vm.AddGear(vm.AvailableModules.First(m => m.Id == moduleTypeId));
        Assert.NotNull(node);
        return node!;
    }

    private static void AssertMirrors(PedalboardViewModel vm, FakeDspChainService dsp)
    {
        Assert.Equal(
            dsp.GetNodes().Select(n => n.NodeId).ToArray(),
            vm.Nodes.Select(n => n.NodeId).ToArray());
    }

    [Fact]
    public void NeuralAmp_card_reports_model_loaded_and_rate_mismatch()
    {
        var catalog = FakeDspChainService.DefaultCatalog()
            .Append(new DspModuleType("namamp", "NAM Amp", []))
            .ToList();
        var dsp = new FakeDspChainService(catalog);
        var vm = TestRigBuilders.Pedalboard(dsp, new FakeDspTonePresetService(), FakeLocalization.English());

        var amp = vm.AddGear(vm.AvailableModules.First(m => m.Id == "namamp"))!;
        Assert.True(amp.IsNeuralAmp);

        // Fresh node: no model loaded yet → warning ("silence is never mysterious").
        Assert.True(amp.NamModelMissing);
        Assert.False(amp.NamModelLoaded);
        Assert.True(amp.NamHasWarning);

        // Simulate the async model load + a 48 kHz mismatch, then refresh (Sync path).
        dsp.SetParam(amp.NodeId, "loaded", 1f);
        dsp.SetParam(amp.NodeId, "rateMismatch", 1f);
        vm.RefreshNeuralAmpStatus();
        Assert.True(amp.NamModelLoaded);
        Assert.True(amp.NamRateMismatch);
        Assert.True(amp.NamHasWarning);   // a rate mismatch still silences a loaded model

        // Rate resolves → loaded + no mismatch = no warning.
        dsp.SetParam(amp.NodeId, "rateMismatch", 0f);
        vm.RefreshNeuralAmpStatus();
        Assert.False(amp.NamRateMismatch);
        Assert.False(amp.NamHasWarning);

        // A non-namamp card never shows the neural-amp status.
        var od = vm.AddGear(vm.AvailableModules.First(m => m.Id == "overdrive"))!;
        Assert.False(od.IsNeuralAmp);
        Assert.False(od.NamHasWarning);
    }

    // ── string params: file pickers + miku lyric editor + durability ─────────

    private static IReadOnlyList<DspModuleType> StringParamCatalog() =>
        FakeDspChainService.DefaultCatalog()
            .Append(new DspModuleType("namamp", "NAM Amp", [])
            { StringParams = [new DspStringParamInfo("modelPath", "NAM Model", "*.nam;*.aidax")] })
            .Append(new DspModuleType("miku", "Miku", [])
            { StringParams = [new DspStringParamInfo("phrases", "Phrases", "")] })
            .ToList();

    [Fact]
    public void StringParam_card_classifies_filePicker_vs_lyrics_and_seeds_value()
    {
        var dsp = new FakeDspChainService(StringParamCatalog());
        var vm = TestRigBuilders.Pedalboard(dsp, new FakeDspTonePresetService(), FakeLocalization.English());

        var amp = vm.AddGear(vm.AvailableModules.First(m => m.Id == "namamp"))!;
        Assert.True(amp.HasStringParams);
        var model = amp.StringParams.Single();
        Assert.True(model.IsFilePicker);
        Assert.False(model.IsLyrics);

        var miku = vm.AddGear(vm.AvailableModules.First(m => m.Id == "miku"))!;
        var phrases = miku.StringParams.Single();
        Assert.True(phrases.IsLyrics);
        Assert.False(phrases.IsFilePicker);
    }

    [Fact]
    public void StringParam_filePicker_clear_pushes_revert_and_updates_label()
    {
        var dsp = new FakeDspChainService(StringParamCatalog());
        var node = dsp.AddNode("namamp")!;
        var info = new DspStringParamInfo("modelPath", "NAM Model", "*.nam;*.aidax");
        var sp = new PedalStringParamViewModel(dsp, node.NodeId, info,
            System.IO.Path.Combine("C:\\x", "marshall.nam"), files: null, localization: FakeLocalization.English());

        Assert.True(sp.HasValue);
        Assert.Equal("marshall.nam", sp.FileLabel);

        sp.ClearCommand.Execute(null);

        Assert.Equal((node.NodeId, "modelPath", ""), dsp.StringSets.Last());
        Assert.False(sp.HasValue);
        Assert.Equal(string.Empty, sp.Value);
    }

    [Fact]
    public void StringParam_lyrics_save_pushes_text_and_counts_syllables()
    {
        var dsp = new FakeDspChainService(StringParamCatalog());
        var node = dsp.AddNode("miku")!;
        var sp = new PedalStringParamViewModel(dsp, node.NodeId,
            new DspStringParamInfo("phrases", "Phrases", ""), string.Empty, files: null, localization: FakeLocalization.English());

        Assert.True(sp.IsLyrics);
        sp.LyricsDraft = "mi ku mi-ku da-yo";       // 6 tokens (split on space + hyphen)
        Assert.Contains("6", sp.SyllableHint);

        sp.SaveLyricsCommand.Execute(null);
        Assert.Contains((node.NodeId, "phrases", "mi ku mi-ku da-yo"), dsp.StringSets);
        Assert.Equal("mi ku mi-ku da-yo", sp.Value);
    }

    [Fact]
    public void StringParam_syllable_count_caps_at_64()
    {
        var many = string.Join(' ', Enumerable.Range(0, 200).Select(i => "la"));
        Assert.Equal(64, PedalStringParamViewModel.CountSyllables(many));
        Assert.Equal(0, PedalStringParamViewModel.CountSyllables("   ,  - "));
        Assert.Equal(0, PedalStringParamViewModel.CountSyllables(""));
    }

    [Fact]
    public void BuildCurrentRig_carries_manual_string_params_for_durability()
    {
        var dsp = new FakeDspChainService(StringParamCatalog());
        var vm = TestRigBuilders.Pedalboard(dsp, new FakeDspTonePresetService(), FakeLocalization.English());
        var amp = vm.AddGear(vm.AvailableModules.First(m => m.Id == "namamp"))!;
        dsp.SetNodeString(amp.NodeId, "modelPath", "C:\\lib\\amp.nam");

        var rig = vm.BuildCurrentRig();

        var mod = rig.Modules.Single(m => m.ModuleTypeId == "namamp");
        Assert.NotNull(mod.StringParams);
        Assert.Equal("C:\\lib\\amp.nam", mod.StringParams!["modelPath"]);

        // A module with no manual string param carries a null map (stays compact).
        var od = vm.AddGear(vm.AvailableModules.First(m => m.Id == "overdrive"))!;
        Assert.Null(vm.BuildCurrentRig().Modules.Single(m => m.ModuleTypeId == "overdrive").StringParams);
    }

    [Fact]
    public void Ctor_exposes_catalogue_and_builds_gear_rail()
    {
        var vm = NewVm(out var dsp);

        Assert.Equal(dsp.Catalog, vm.AvailableModules);
        Assert.True(vm.HasModules);
        Assert.False(vm.HasNodes);
        Assert.Empty(vm.Nodes);

        // The gear rail exposes every catalogue module as a card on the All tab.
        Assert.True(vm.GearRail.HasCards);
        Assert.Equal(dsp.Catalog.Count, vm.GearRail.Cards.Count);
        Assert.Null(vm.GearRail.SelectedTab?.Category); // All tab first + selected
    }

    [Fact]
    public void GearRail_card_click_adds_at_chain_end()
    {
        var vm = NewVm(out var dsp);
        Add(vm, "overdrive");

        var card = vm.GearRail.Cards.First(c => c.ModuleTypeId == "delay");
        vm.GearRail.AddGearCommand.Execute(card);

        Assert.Equal(new[] { "overdrive", "delay" },
            vm.Nodes.Select(n => n.ModuleTypeId).ToArray());
        AssertMirrors(vm, dsp);
    }

    [Fact]
    public void GearRail_search_and_tabs_slice_the_catalogue()
    {
        var vm = NewVm(out var dsp);

        vm.GearRail.SearchText = "del";
        Assert.Equal(new[] { "delay" },
            vm.GearRail.Cards.Select(c => c.ModuleTypeId).ToArray());

        vm.GearRail.SearchText = string.Empty;
        vm.GearRail.SelectedTab = vm.GearRail.Tabs
            .First(t => t.Category == DspModuleCategory.Drive);
        Assert.Equal(new[] { "overdrive" },
            vm.GearRail.Cards.Select(c => c.ModuleTypeId).ToArray());

        // Only categories present in the catalogue get a tab (plus All first).
        Assert.Null(vm.GearRail.Tabs[0].Category);
        Assert.DoesNotContain(vm.GearRail.Tabs, t => t.Category == DspModuleCategory.Modulation);
        _ = dsp;
    }

    [Fact]
    public void AddModule_appends_node_and_calls_service()
    {
        var vm = NewVm(out var dsp);

        var node = Add(vm, "overdrive");

        Assert.Single(vm.Nodes);
        Assert.True(vm.HasNodes);
        Assert.Equal(new[] { "overdrive" }, dsp.Adds);
        Assert.Equal("overdrive", node.ModuleTypeId);
        Assert.Equal("Overdrive", node.DisplayName);
        AssertMirrors(vm, dsp);
    }

    [Fact]
    public void AddModule_builds_generic_param_sliders_from_metadata()
    {
        var vm = NewVm(out _);

        var node = Add(vm, "delay");

        // Sliders are materialized purely from DspParamInfo — no per-module UI.
        Assert.Equal(new[] { "time", "feedback", "mix" }, node.Params.Select(p => p.Id).ToArray());
        var time = node.Params.First(p => p.Id == "time");
        Assert.Equal(0f, time.Minimum);
        Assert.Equal(2000f, time.Maximum);
        Assert.Equal(350f, time.Default);
        Assert.Equal(350f, time.Value);
    }

    [Fact]
    public void AddGearAt_inserts_at_the_drop_slot()
    {
        var vm = NewVm(out var dsp);
        var od = Add(vm, "overdrive");
        var dl = Add(vm, "delay");

        var type = vm.AvailableModules.First(m => m.Id == "reverb");
        var rv = vm.AddGearAt(type, 1);

        Assert.NotNull(rv);
        Assert.Equal(new[] { od.NodeId, rv!.NodeId, dl.NodeId },
            vm.Nodes.Select(n => n.NodeId).ToArray());
        AssertMirrors(vm, dsp);
    }

    [Fact]
    public void AddGearAt_out_of_range_degrades_to_append()
    {
        var vm = NewVm(out var dsp);
        var od = Add(vm, "overdrive");

        var type = vm.AvailableModules.First(m => m.Id == "delay");
        var dl = vm.AddGearAt(type, 99);

        Assert.NotNull(dl);
        Assert.Equal(new[] { od.NodeId, dl!.NodeId }, vm.Nodes.Select(n => n.NodeId).ToArray());
        AssertMirrors(vm, dsp);
    }

    [Fact]
    public void AddGearAt_on_empty_board_lands_at_slot_zero()
    {
        var vm = NewVm(out var dsp);

        var type = vm.AvailableModules.First(m => m.Id == "overdrive");
        var od = vm.AddGearAt(type, 0);

        Assert.NotNull(od);
        Assert.Equal(new[] { od!.NodeId }, vm.Nodes.Select(n => n.NodeId).ToArray());
        AssertMirrors(vm, dsp);
    }

    [Fact]
    public void Ordered_list_mirrors_chain_across_adds()
    {
        var vm = NewVm(out var dsp);

        var od = Add(vm, "overdrive");
        var dl = Add(vm, "delay");

        Assert.Equal(new[] { od.NodeId, dl.NodeId }, vm.Nodes.Select(n => n.NodeId).ToArray());
        AssertMirrors(vm, dsp);
    }

    [Fact]
    public void MoveDown_then_MoveUp_reorders_vm_and_service()
    {
        var vm = NewVm(out var dsp);
        var od = Add(vm, "overdrive");
        var dl = Add(vm, "delay");

        vm.MoveDownCommand.Execute(od);
        Assert.Equal(new[] { dl.NodeId, od.NodeId }, vm.Nodes.Select(n => n.NodeId).ToArray());
        Assert.Equal((od.NodeId, 1), dsp.Moves.Single());
        AssertMirrors(vm, dsp);

        vm.MoveUpCommand.Execute(od);
        Assert.Equal(new[] { od.NodeId, dl.NodeId }, vm.Nodes.Select(n => n.NodeId).ToArray());
        AssertMirrors(vm, dsp);
    }

    [Fact]
    public void MoveUp_on_first_node_is_a_noop()
    {
        var vm = NewVm(out var dsp);
        var od = Add(vm, "overdrive");
        Add(vm, "delay");

        vm.MoveUpCommand.Execute(od);

        Assert.Empty(dsp.Moves);
        AssertMirrors(vm, dsp);
    }

    [Fact]
    public void Remove_drops_node_from_vm_and_service()
    {
        var vm = NewVm(out var dsp);
        var od = Add(vm, "overdrive");
        var dl = Add(vm, "delay");

        vm.RemoveCommand.Execute(od);

        Assert.Equal(new[] { dl.NodeId }, vm.Nodes.Select(n => n.NodeId).ToArray());
        Assert.Equal(od.NodeId, dsp.Removes.Single());
        AssertMirrors(vm, dsp);
    }

    [Fact]
    public void Bypass_toggle_propagates_to_service()
    {
        var vm = NewVm(out var dsp);
        var od = Add(vm, "overdrive");

        od.Bypassed = true;

        Assert.Equal((od.NodeId, true), dsp.BypassSets.Single());
        Assert.True(dsp.GetNodes().Single(n => n.NodeId == od.NodeId).Bypassed);
    }

    [Fact]
    public void Bypass_toggle_reverts_when_service_rejects()
    {
        var vm = NewVm(out var dsp);
        var od = Add(vm, "overdrive");
        dsp.FailBypass = true;

        od.Bypassed = true;

        // Service refused → the toggle must snap back so it never lies.
        Assert.False(od.Bypassed);
        Assert.Contains((od.NodeId, true), dsp.BypassSets);
    }

    [Fact]
    public void Param_slider_change_propagates_to_service()
    {
        var vm = NewVm(out var dsp);
        var od = Add(vm, "overdrive");
        var drive = od.Params.First(p => p.Id == "drive");

        drive.Value = 0.9f;

        Assert.Equal((od.NodeId, "drive", 0.9f), dsp.ParamSets.Single());
        Assert.Equal(0.9f, dsp.GetParam(od.NodeId, "drive"));
    }

    [Fact]
    public void Amp_model_plate_shows_the_voicing_label_and_follows_it_live()
    {
        var vm = NewVm(out _);
        var amp = Add(vm, "amp");

        // v24: the voicing is the model PLATE (not a knob, not a badge).
        Assert.NotNull(amp.AmpModelSelector);
        Assert.True(amp.HasAmpModelSelector);
        Assert.False(amp.HasBadge);                       // the plate shows the name, not a badge
        Assert.DoesNotContain(amp.KnobParams, p => p.Id == "voicing");
        Assert.Equal("Plexi 50", amp.AmpModelSelector!.CurrentLabel); // default voicing 1

        amp.Params.First(p => p.Id == "voicing").Value = 2f; // Recto Dual
        Assert.Equal("Recto Dual", amp.AmpModelSelector.CurrentLabel);
    }

    [Fact]
    public void Cab_badge_shows_the_model_label_from_the_abi()
    {
        var vm = NewVm(out _);
        var cab = Add(vm, "cab");

        // The badge reads the CURRENT model name from the v24 labels (replacing
        // the old hardcoded 0–6 map), and the model is a compact selector.
        Assert.True(cab.HasBadge);
        Assert.Equal("1x12 Combo", cab.BadgeText); // default model 0
        Assert.Single(cab.ModelSelectors);
        Assert.DoesNotContain(cab.KnobParams, p => p.Id == "model");

        var badgeChanges = 0;
        cab.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(PedalNodeViewModel.BadgeText)) badgeChanges++;
        };
        cab.Params.First(p => p.Id == "model").Value = 2f; // 4x12 Scooped
        Assert.True(badgeChanges > 0);
        Assert.Equal("4x12 Scooped", cab.BadgeText);
    }

    [Fact]
    public void Stompbox_has_no_badge()
    {
        var vm = NewVm(out _);
        var od = Add(vm, "overdrive");
        Assert.False(od.HasBadge);
        Assert.Equal(string.Empty, od.BadgeText);
    }

    [Fact]
    public void SelectNodeAt_highlights_one_node_and_clears_on_out_of_range()
    {
        var vm = NewVm(out _);
        var od = Add(vm, "overdrive");
        var dl = Add(vm, "delay");

        Assert.Same(od, vm.SelectNodeAt(0));
        Assert.True(od.IsSelected);
        Assert.False(dl.IsSelected);

        Assert.Same(dl, vm.SelectNodeAt(1));
        Assert.False(od.IsSelected);
        Assert.True(dl.IsSelected);

        Assert.Null(vm.SelectNodeAt(99));
        Assert.False(dl.IsSelected);
    }

    [Fact]
    public void Remove_clears_selection_of_the_removed_node()
    {
        var vm = NewVm(out _);
        var od = Add(vm, "overdrive");
        vm.SelectNodeAt(0);

        vm.RemoveCommand.Execute(od);

        Assert.Null(vm.SelectedNode);
    }

    [Fact]
    public void Selecting_a_pitch_pedal_surfaces_the_rig_tuner()
    {
        var vm = NewVm(out _);
        Add(vm, "overdrive");
        Add(vm, "octaver");

        Assert.False(vm.RigTuner.IsOpen);
        vm.SelectNodeAt(0);           // Drive pedal — no tuner
        Assert.False(vm.RigTuner.IsOpen);

        vm.SelectNodeAt(1);           // Pitch pedal — AmpliTube parity
        Assert.True(vm.RigTuner.IsOpen);
    }

    [Fact]
    public void RigTuner_never_polls_without_a_live_surface()
    {
        var vm = NewVm(out _);

        vm.RigTuner.Open();
        Assert.True(vm.RigTuner.IsOpen);
        Assert.False(vm.RigTuner.IsPolling); // open but no attached+visible view

        vm.RigTuner.Close();
        Assert.False(vm.RigTuner.IsPolling);
    }

    [Fact]
    public void RigTuner_without_audio_service_stays_inert()
    {
        // The board always hands its tuner a transport (#661), but the tuner
        // itself still accepts none — a live surface must not start polling.
        var tuner = new RigTunerViewModel(audio: null, FakeLocalization.English());
        tuner.Open();
        tuner.SetSurfaceLive(this, true);
        Assert.False(tuner.IsPolling);
        tuner.SetSurfaceLive(this, false);
    }

    /// <summary>Since the incremental Sync (0.59) the selection follows its
    /// NODE, not the resync: a surviving node stays selected (that is what
    /// keeps the module editor dock stable across a preset apply), and only a
    /// node that is actually gone clears it.</summary>
    [Fact]
    public void Sync_keeps_the_selection_while_its_node_survives_and_clears_it_when_gone()
    {
        var vm = NewVm(out var dsp);
        var od = Add(vm, "overdrive");
        vm.SelectNodeAt(0);
        Assert.NotNull(vm.SelectedNode);

        vm.Sync();
        Assert.Same(od, vm.SelectedNode);

        // Removed behind the VM's back (as a preset apply would) → cleared.
        Assert.True(dsp.RemoveNode(od.NodeId));
        vm.Sync();
        Assert.Null(vm.SelectedNode);
    }

    [Fact]
    public void Sync_rebuilds_nodes_from_service_chain()
    {
        var vm = NewVm(out var dsp);

        // Build a chain directly on the service (as if populated elsewhere),
        // then Sync — the VM should reflect it.
        var a = dsp.AddNode("overdrive")!;
        var b = dsp.AddNode("delay")!;
        Assert.Empty(vm.Nodes);

        vm.Sync();

        Assert.Equal(new[] { a.NodeId, b.NodeId }, vm.Nodes.Select(n => n.NodeId).ToArray());
        Assert.True(vm.HasNodes);
        AssertMirrors(vm, dsp);
    }
}
