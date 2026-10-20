using DotCharter.Presentation.Screens.Dialogs;
using DotCharter.Services.Plugins;
using DotCharter.Tone.Audio.Dsp;
using DotCharter.Tone.Audio.Dsp.RsToneConversion;
using DotCharter.Services.Editing;
using DotCharter.Services.Editing.Tones;
using DotCharter.Services.Export.Validation;
using DotCharter.Tone.Audio.Dsp.Tones;
using DotCharter.Tone.Infrastructure.Storage;
using DotCharter.Tone.Parsing.Psarc;

namespace DotCharter.Tests.Presentation.Dialogs;

/// <summary>
///     The Tone designer, one instance per open: project tone CRUD and gear/knob editing
///     committed through the history (one undo step per tone), slot assignment committed
///     on close, the live-preview snapshot/restore over a fake DSP chain, and the imported
///     tones kept in <see cref="DialogMemory" /> for the export. No Avalonia instance.
/// </summary>
public sealed partial class ToneDesignerViewModelTests : IDisposable
{
    private readonly string _tempDir = Directory.CreateTempSubdirectory("dotcharter-tonesvm-").FullName;

    public void Dispose() => BestEffortDelete.Directory(_tempDir);

    // ---- the generated tone's SLOT is an undoable edit -----------------------

    /// <summary>
    ///     A generated tone is declared in a free A–D slot, and that write used to go
    ///     straight into the arrangement: undo removed the body and left the slot
    ///     declared, and nothing marked the project dirty for it.
    /// </summary>
    [Fact]
    public void AGeneratedTonesSlot_IsOneUndoableEdit()
    {
        var arrangement = new Arrangement { Name = "Lead", ToneBase = "clean", ToneA = "clean" };
        arrangement.ToneNames.Add("clean");
        arrangement.StartingTone = "clean";

        var before = ToneSlotAssignment.Capture(arrangement);
        ToneGenerationRun.DeclareInAFreeSlot(arrangement, "Clean Verse");
        ToneGenerationRun.DeclareInAFreeSlot(arrangement, "Lead Solo");

        var action = ToneGenerationRun.BuildSlotAssignmentAction(arrangement, before);
        Assert.NotNull(action);
        Assert.Equal("", arrangement.ToneB);   // rewound: nothing applies until Do

        var history = new HistoryService(new FakeAppLogger());
        Assert.True(history.Do(action!));
        Assert.Equal("Clean Verse", arrangement.ToneB);
        Assert.Equal("Lead Solo", arrangement.ToneC);
        Assert.Equal(["clean", "Clean Verse", "Lead Solo"], arrangement.ToneNames);
        Assert.Equal("clean", arrangement.StartingTone);

        history.Undo();
        Assert.Equal("", arrangement.ToneB);
        Assert.Equal("", arrangement.ToneC);
        Assert.Equal(["clean"], arrangement.ToneNames);
    }

    [Fact]
    public void AGenerationThatMovedNoSlot_RecordsNothing()
    {
        var arrangement = new Arrangement { Name = "Lead", ToneA = "clean" };
        arrangement.ToneNames.Add("clean");
        var before = ToneSlotAssignment.Capture(arrangement);
        ToneGenerationRun.DeclareInAFreeSlot(arrangement, "clean");
        Assert.Null(ToneGenerationRun.BuildSlotAssignmentAction(arrangement, before));
    }

    [Fact]
    public void PastFourSlots_TheDeclarationStops_WithoutOverwriting()
    {
        var arrangement = new Arrangement { Name = "Lead", ToneA = "a", ToneB = "b", ToneC = "c", ToneD = "d" };
        ToneGenerationRun.DeclareInAFreeSlot(arrangement, "fifth");
        Assert.Equal(["a", "b", "c", "d"], [arrangement.ToneA, arrangement.ToneB, arrangement.ToneC, arrangement.ToneD]);
    }

    // ---- a greyed action owes its reason -------------------------------------

    [Fact]
    public void WithNoToneSelected_PreviewIsGreyed_AndSaysWhy()
    {
        var harness = new Harness(CreateProject());
        var vm = harness.Open();
        vm.SelectedTone = null;

        Assert.False(vm.HasSelectedTone);
        Assert.True(vm.Preview.IsAvailable, "the fake chain is a real rig");
        Assert.False(vm.CanPreviewNow);
        Assert.Equal(FakeLocalization.English()["Dialog_Tones_PickAToneFirst"], vm.ToneActionUnavailableReason);
        vm.IsPreviewOn = true;
        Assert.False(vm.IsPreviewOn);
    }

    [Fact]
    public void WithAToneSelected_NothingIsGreyed_AndNoReasonIsPrinted()
    {
        var vm = new Harness(CreateProject()).Open();
        vm.AddToneCommand.Execute(null);

        Assert.True(vm.HasSelectedTone);
        Assert.True(vm.CanPreviewNow);
        Assert.Equal("", vm.ToneActionUnavailableReason);
        Assert.True(vm.DuplicateToneCommand.CanExecute(null));
    }

    [Fact]
    public void WithNoNativeRig_TheReasonIsTheRig_NotTheSelection()
    {
        var vm = new Harness(CreateProject(), new NullDspChainService()).Open();
        Assert.False(vm.CanPreviewNow);
        Assert.Equal(FakeLocalization.English()["Dialog_Tones_PreviewUnavailable"], vm.ToneActionUnavailableReason);
    }

    // ---- the gear catalogue is built on first read ---------------------------

    /// <summary>The editor may not touch the gear catalogue (a 144 KB deserialize) until something reads the slots.</summary>
    [Fact]
    public void The_editor_builds_its_gear_slots_on_first_read()
    {
        var editor = new ToneEditorViewModel(FakeLocalization.English(), () => []);
        Assert.False(editor.GearSlotsAreBuilt);
        Assert.Equal(14, editor.GearSlots.Count);
        Assert.True(editor.GearSlotsAreBuilt);
    }

    [Fact]
    public void Opening_seeds_the_picker_on_the_amp()
    {
        var vm = new Harness(CreateProject()).Open();
        Assert.Same(vm.Editor.GearSlots[0], vm.Editor.SelectedGearSlot);
    }

    // ---------------------------------------------------------------- opening

    [Fact]
    public void Opening_without_a_project_says_so_and_edits_nothing()
    {
        var vm = new Harness(null).Open();
        Assert.Equal(FakeLocalization.English()["Status_Export_NoProject"], vm.ImportStatus);
        Assert.Empty(vm.ProjectTones);
        vm.AddToneCommand.Execute(null);
        Assert.Empty(vm.ProjectTones);
    }

    [Fact]
    public void Open_lists_project_tones_and_loads_the_first_into_the_editor()
    {
        var project = CreateProject();
        project.ToneDefinitions.Add(AuthoredTone("clean", "Clean"));
        project.ToneDefinitions.Add(AuthoredTone("dist", "Distorted"));
        var vm = new Harness(project).Open();

        Assert.Equal(["clean", "dist"], vm.ProjectTones.Select(t => t.Key));
        Assert.Equal("clean", vm.SelectedTone?.Key);
        Assert.Equal("clean", vm.Editor.EditKey);
        Assert.Equal("Clean", vm.Editor.EditName);
        Assert.Equal(RsToneAuthoring.DefaultVolumeDb, vm.Editor.VolumeDb, 3);
        Assert.True(vm.Editor.GearSlots[0].HasGear);
        Assert.NotEmpty(vm.Editor.GearSlots[0].Knobs);
        Assert.Equal(["clean", "dist"], vm.AuthoredToneKeys);
    }

    [Fact]
    public void Open_prefills_arrangement_rows_from_the_tone_slots()
    {
        var vm = new Harness(CreateProject()).Open();
        Assert.True(vm.HasArrangements);
        Assert.Equal(["Lead", "Bass"], vm.Arrangements.Select(a => a.Name));
        var lead = vm.Arrangements[0];
        Assert.Equal(("clean", "clean", "dist", ""), (lead.ToneBase, lead.ToneA, lead.ToneB, lead.ToneC));
    }

    /// <summary>An arrangement named after its type shows no type tag: the cards read "Lead [Lead]"
    /// (gallery capture, 2026-09-24), the same doubling the export dialog had.</summary>
    [Fact]
    public void An_arrangement_named_after_its_type_carries_no_type_tag()
    {
        var vm = new Harness(CreateProject()).Open();
        Assert.All(vm.Arrangements, a => Assert.Equal("", a.TypeLabel));
    }

    /// <summary>The row's own slot shape: distinct non-empty slots, the base falling back to the first.</summary>
    [Fact]
    public void An_arrangement_row_reads_as_distinct_slots_and_a_base()
    {
        var row = new ToneDesignerArrangementItem
        {
            Name = "Lead", TypeLabel = "Lead", Arrangement = new Arrangement(), ToneBase = " ",
            ToneA = "  lead_a ", ToneB = "lead_a", ToneC = "lead_c"
        };
        var (toneBase, slots) = row.ToToneSlots();
        Assert.Equal("lead_a", toneBase);
        Assert.Equal(["lead_a", "lead_c"], slots);
    }

    // -------------------------------------------------------------- tone CRUD

    [Fact]
    public void A_null_written_into_a_gear_slot_is_coerced_and_close_cannot_crash()
    {
        var vm = new Harness(CreateProject()).Open();
        vm.AddToneCommand.Execute(null);
        var ampSlot = vm.Editor.GearSlots[0];
        var ampBefore = ampSlot.SelectedOption;

        ampSlot.SelectedOption = null!;

        Assert.Same(ampBefore, ampSlot.SelectedOption);
        vm.CloseCommand.Execute(null);
    }

    [Fact]
    public void AddTone_creates_an_undoable_project_tone()
    {
        var project = CreateProject();
        var harness = new Harness(project);
        var vm = harness.Open();
        vm.AddToneCommand.Execute(null);

        var def = Assert.Single(project.ToneDefinitions);
        Assert.Equal("tone", def.Key);
        Assert.True(RsToneJsonParser.TryParse(def.Json, out _));
        Assert.Equal("tone", vm.SelectedTone?.Key);
        harness.History.Undo();
        Assert.Empty(project.ToneDefinitions);
    }

    [Fact]
    public void AddTone_twice_uniquifies_the_key()
    {
        var project = CreateProject();
        var vm = new Harness(project).Open();
        vm.AddToneCommand.Execute(null);
        vm.AddToneCommand.Execute(null);
        Assert.Equal(["tone", "tone_2"], project.ToneDefinitions.Select(t => t.Key));
    }

    [Fact]
    public void DuplicateTone_copies_the_json_under_a_fresh_key()
    {
        var project = CreateProject();
        project.ToneDefinitions.Add(AuthoredTone("clean", "Clean"));
        var vm = new Harness(project).Open();
        vm.DuplicateToneCommand.Execute(null);

        Assert.Equal(["clean", "clean_2"], project.ToneDefinitions.Select(t => t.Key));
        Assert.True(RsToneJsonParser.TryParse(project.ToneDefinitions[1].Json, out var copy));
        Assert.Equal("clean_2", copy!.Key);
        Assert.Equal("clean_2", vm.SelectedTone?.Key);
    }

    [Fact]
    public void DeleteTone_removes_and_undo_restores_at_the_same_position()
    {
        var project = CreateProject();
        foreach (var key in new[] { "a", "b", "c" }) project.ToneDefinitions.Add(AuthoredTone(key, key.ToUpperInvariant()));
        var harness = new Harness(project);
        var vm = harness.Open();
        vm.SelectedTone = vm.ProjectTones[1];
        vm.DeleteToneCommand.Execute(null);

        Assert.Equal(["a", "c"], project.ToneDefinitions.Select(t => t.Key));
        Assert.Equal(["a", "c"], vm.ProjectTones.Select(t => t.Key));
        harness.History.Undo();
        Assert.Equal(["a", "b", "c"], project.ToneDefinitions.Select(t => t.Key));
    }

    // ------------------------------------------------------------ editor commit

    [Fact]
    public void A_knob_edit_commits_as_one_undoable_action_on_close()
    {
        var project = CreateProject();
        project.ToneDefinitions.Add(AuthoredTone("clean", "Clean"));
        var originalJson = project.ToneDefinitions[0].Json;
        var harness = new Harness(project);
        var vm = harness.Open();

        var knob = vm.Editor.GearSlots[0].Knobs.First(k => Math.Abs(k.Knob.DefaultValue - k.Maximum) > 0.01);
        knob.Value = knob.Maximum;
        Assert.Equal(originalJson, project.ToneDefinitions[0].Json);   // buffered

        vm.CloseCommand.Execute(null);
        Assert.True(RsToneJsonParser.TryParse(project.ToneDefinitions[0].Json, out var edited));
        Assert.Equal((float)knob.Maximum, edited!.Amp.KnobValues[knob.Knob.Key]);
        harness.History.Undo();
        Assert.Equal(originalJson, project.ToneDefinitions[0].Json);
    }

    /// <summary>Esc commits exactly as Done does: an edit is never lost by leaving.</summary>
    [Fact]
    public async Task Esc_commits_the_pending_edit_too()
    {
        var project = CreateProject();
        project.ToneDefinitions.Add(AuthoredTone("clean", "Clean"));
        var vm = new Harness(project).Open();
        vm.Editor.EditName = "Clean edited";

        Assert.True(vm.TryCancel());

        Assert.Equal("Clean edited", project.ToneDefinitions[0].Name);
        Assert.False(await vm.Result.WaitAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public void A_tone_switch_commits_the_previous_tones_pending_edit()
    {
        var project = CreateProject();
        project.ToneDefinitions.Add(AuthoredTone("a", "A"));
        project.ToneDefinitions.Add(AuthoredTone("b", "B"));
        var vm = new Harness(project).Open();
        vm.Editor.EditName = "A edited";
        vm.SelectedTone = vm.ProjectTones[1];

        Assert.Equal("A edited", project.ToneDefinitions[0].Name);
        Assert.True(RsToneJsonParser.TryParse(project.ToneDefinitions[0].Json, out var tone));
        Assert.Equal("A edited", tone!.Name);
    }

    [Fact]
    public void A_valid_key_rename_commits_and_an_invalid_one_reverts_to_the_committed_key()
    {
        var project = CreateProject();
        project.ToneDefinitions.Add(AuthoredTone("clean", "Clean"));
        var harness = new Harness(project);
        var vm = harness.Open();
        vm.Editor.EditKey = "clean_v2";
        Assert.False(vm.Editor.HasKeyError);
        vm.CloseCommand.Execute(null);
        Assert.Equal("clean_v2", project.ToneDefinitions[0].Key);

        vm = harness.Open();
        vm.Editor.EditKey = "1 not valid!";
        Assert.True(vm.Editor.HasKeyError);
        vm.CloseCommand.Execute(null);
        Assert.Equal("clean_v2", project.ToneDefinitions[0].Key);
    }

    /// <summary>
    ///     #181: a key edit used to replace the definition and nothing else, so the slot rows
    ///     beside it (committed unchanged on close), the starting tone, the declared names and
    ///     every tone change stayed on the old key, and the export found no definition for it.
    ///     It is now ONE undo step that moves all of them.
    /// </summary>
    [Fact]
    public void A_key_edit_renames_the_tone_everywhere_as_one_undo_step()
    {
        var project = CreateProject();   // Lead: base clean, A clean, B dist
        project.ToneDefinitions.Add(AuthoredTone("clean", "Clean"));
        project.ToneDefinitions.Add(AuthoredTone("dist", "Dist"));
        var lead = project.Arrangements[0];
        lead.StartingTone = "clean";
        lead.ToneNames.Add("clean");
        lead.ToneNames.Add("dist");
        lead.ToneChanges.Add(new ToneChange { Position = TimelineMath.PositionFromMs(1000, project.Beats), ToneName = "clean" });
        var level = new ChartLevel { DifficultyIndex = 0 };   // the export check stops at "no notes" without one
        level.Notes.Add(new GuitarNote { Position = TimelineMath.PositionFromMs(2000, project.Beats), StringIndex = 0, Fret = 3 });
        lead.Levels.Add(level);
        var before = ChartProjectCloner.Clone(project);
        var harness = new Harness(project);
        var vm = harness.Open();

        vm.Editor.EditKey = "clean_v2";
        vm.CloseCommand.Execute(null);

        Assert.Equal(["clean_v2", "dist"], project.ToneDefinitions.Select(t => t.Key));
        Assert.Equal(["clean_v2", "clean_v2", "dist"], [lead.ToneBase, lead.ToneA, lead.ToneB]);
        Assert.Equal("clean_v2", lead.StartingTone);
        Assert.Equal(["clean_v2", "dist"], lead.ToneNames);
        Assert.Equal("clean_v2", Assert.Single(lead.ToneChanges).ToneName);
        Assert.DoesNotContain(new ChartExportValidator().Validate(project),
            f => f.MessageKey == ChartExportValidator.KeyToneNotDefined);

        harness.History.Undo();
        Assert.False(harness.History.CanUndo, "the rename and its slot rows must be ONE undo step");
        Assert.Empty(DeepCompare.Diff("project", before, project, new HashSet<string>()));
    }

    /// <summary>A deleted tone leaves no slot, declared name or tone change naming a body that is gone.</summary>
    [Fact]
    public void Deleting_a_tone_clears_its_slots_and_changes_too()
    {
        var project = CreateProject();
        project.ToneDefinitions.Add(AuthoredTone("clean", "Clean"));
        var lead = project.Arrangements[0];
        lead.ToneChanges.Add(new ToneChange { Position = TimelineMath.PositionFromMs(1000, project.Beats), ToneName = "clean" });
        var harness = new Harness(project);
        var vm = harness.Open();

        vm.DeleteToneCommand.Execute(null);
        vm.CloseCommand.Execute(null);

        Assert.Empty(project.ToneDefinitions);
        Assert.Empty(lead.ToneChanges);
        Assert.Equal(["", ""], [lead.ToneBase, lead.ToneA]);
        Assert.Equal("dist", lead.ToneB);
        harness.History.Undo();
        Assert.Equal("clean", lead.ToneBase);
        Assert.Single(lead.ToneChanges);
    }

    /// <summary>
    ///     #195: a slot naming a key the project has no tone for is marked in place. The mark follows
    ///     the slot's text as it is typed, and the project's tones as they are added or renamed.
    /// </summary>
    [Fact]
    public void A_slot_naming_an_undefined_key_is_marked_and_the_mark_follows_the_tones()
    {
        var project = CreateProject();   // Lead: base clean, A clean, B dist
        project.ToneDefinitions.Add(AuthoredTone("clean", "Clean"));
        var vm = new Harness(project).Open();
        var lead = vm.Arrangements[0];
        Assert.Equal((false, false, true, false, false),
            (lead.IsToneBaseUndefined, lead.IsToneAUndefined, lead.IsToneBUndefined, lead.IsToneCUndefined, lead.IsToneDUndefined));

        var raised = new List<string?>();
        lead.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        lead.ToneC = "typo";
        Assert.True(lead.IsToneCUndefined);
        Assert.Contains(nameof(lead.IsToneCUndefined), raised);
        lead.ToneC = " CLEAN ";   // trim- and case-insensitive, like every tone-key match
        Assert.False(lead.IsToneCUndefined);

        // A new tone renamed to the slot's key defines it, and the mark goes.
        raised.Clear();
        vm.AddToneCommand.Execute(null);
        vm.Editor.EditKey = "dist";
        vm.SelectedTone = vm.ProjectTones[0];   // a switch commits the key edit
        Assert.Contains(project.ToneDefinitions, t => t.Key == "dist");
        Assert.False(lead.IsToneBUndefined);
        Assert.Contains(nameof(lead.IsToneBUndefined), raised);
    }

    [Fact]
    public void A_duplicate_key_is_flagged_and_never_committed()
    {
        var project = CreateProject();
        project.ToneDefinitions.Add(AuthoredTone("a", "A"));
        project.ToneDefinitions.Add(AuthoredTone("b", "B"));
        var vm = new Harness(project).Open();
        vm.Editor.EditKey = "B";
        Assert.True(vm.Editor.HasKeyError);
        vm.CloseCommand.Execute(null);
        Assert.Equal(["a", "b"], project.ToneDefinitions.Select(t => t.Key));
    }

    [Fact]
    public void A_gear_change_rebuilds_knobs_at_catalogue_defaults_and_serializes()
    {
        var project = CreateProject();
        project.ToneDefinitions.Add(AuthoredTone("clean", "Clean"));
        var vm = new Harness(project).Open();
        var ampSlot = vm.Editor.GearSlots[0];
        var otherAmp = ampSlot.Options.First(o => o.Gear is { } g && g.Key != ampSlot.SelectedOption.Gear!.Key);
        ampSlot.SelectedOption = otherAmp;

        Assert.Equal(otherAmp.Gear!.Knobs!.Count, ampSlot.Knobs.Count);
        Assert.All(ampSlot.Knobs, k => Assert.Equal(k.Knob.DefaultValue, k.Value, 3));
        vm.CloseCommand.Execute(null);
        Assert.True(RsToneJsonParser.TryParse(project.ToneDefinitions[0].Json, out var tone));
        Assert.Equal(otherAmp.Gear!.Key, tone!.Amp.GearKey);
    }

    [Fact]
    public void A_pedal_slot_round_trips_through_the_manifest_json()
    {
        var project = CreateProject();
        project.ToneDefinitions.Add(AuthoredTone("clean", "Clean"));
        var vm = new Harness(project).Open();
        var pre2 = vm.Editor.GearSlots[3];   // 0 amp, 1 cab, 2..5 pre, 6..9 loop, 10..13 rack
        Assert.False(pre2.HasGear);
        pre2.SelectedOption = pre2.Options.First(o => o.Gear is not null);
        var pedalKey = pre2.SelectedOption.Gear!.Key;
        vm.CloseCommand.Execute(null);

        Assert.True(RsToneJsonParser.TryParse(project.ToneDefinitions[0].Json, out var tone));
        var pedal = Assert.Single(tone!.PrePedals);
        Assert.Equal(pedalKey, pedal.GearKey);
        Assert.Equal(2, pedal.SlotIndex);
    }

    // --------------------------------------------------------- slot assignment

    [Fact]
    public void Close_commits_edited_slot_assignments_with_tonenames_and_undo()
    {
        var project = CreateProject();
        var lead = project.Arrangements[0];
        var harness = new Harness(project);
        var vm = harness.Open();
        vm.Arrangements[0].ToneC = "lead_x";
        vm.CloseCommand.Execute(null);

        Assert.Equal("lead_x", lead.ToneC);
        Assert.Equal("clean", lead.ToneBase);
        Assert.Equal("clean", lead.StartingTone);
        Assert.Equal(["clean", "dist", "lead_x"], lead.ToneNames);
        harness.History.Undo();
        Assert.Equal("", lead.ToneC);
        Assert.Empty(lead.ToneNames);
    }

    [Fact]
    public void Close_without_slot_edits_touches_nothing()
    {
        var project = CreateProject();
        var harness = new Harness(project);
        harness.Open().CloseCommand.Execute(null);
        Assert.False(harness.History.CanUndo);
        Assert.Empty(project.Arrangements[0].ToneNames);
    }

    // ----------------------------------------------------------------- preview

    [Fact]
    public void Preview_applies_the_converted_tone_and_restores_the_chain_on_off()
    {
        var project = CreateProject();
        project.ToneDefinitions.Add(AuthoredTone("clean", "Clean"));
        var chain = new FakeDspChainService(RigCatalog());
        var userNode = chain.AddNode("overdrive")!;
        chain.SetParam(userNode.NodeId, "drive", 0.9f);
        var vm = new Harness(project, chain).Open();

        vm.IsPreviewOn = true;
        var previewIds = chain.GetNodes().Select(n => n.ModuleTypeId).ToArray();
        Assert.Contains("amp", previewIds);   // the authored tone's amp: generic, with no game tables loaded (#755)
        Assert.Contains("cab", previewIds);
        Assert.Contains("gain", previewIds);

        vm.IsPreviewOn = false;
        var restored = Assert.Single(chain.GetNodes());
        Assert.Equal("overdrive", restored.ModuleTypeId);
        Assert.Equal(0.9f, restored.Values["drive"], 3);
    }

    [Fact]
    public void Preview_restores_on_close_too()
    {
        var project = CreateProject();
        project.ToneDefinitions.Add(AuthoredTone("clean", "Clean"));
        var chain = new FakeDspChainService(RigCatalog());
        chain.AddNode("overdrive");
        var vm = new Harness(project, chain).Open();
        vm.IsPreviewOn = true;
        Assert.NotEqual(["overdrive"], chain.GetNodes().Select(n => n.ModuleTypeId));

        vm.CloseCommand.Execute(null);
        Assert.False(vm.IsPreviewOn);
        Assert.Equal(["overdrive"], chain.GetNodes().Select(n => n.ModuleTypeId));
    }

    /// <summary>The host disposes the scope however the dialog closed; a preview still running must hand the rig back.</summary>
    [Fact]
    public void Preview_restores_when_the_host_disposes_the_dialog()
    {
        var project = CreateProject();
        project.ToneDefinitions.Add(AuthoredTone("clean", "Clean"));
        var chain = new FakeDspChainService(RigCatalog());
        chain.AddNode("overdrive");
        var vm = new Harness(project, chain).Open();
        vm.IsPreviewOn = true;

        vm.Dispose();

        Assert.Equal(["overdrive"], chain.GetNodes().Select(n => n.ModuleTypeId));
    }

    [Fact]
    public void Preview_stays_off_on_the_null_chain()
    {
        var project = CreateProject();
        project.ToneDefinitions.Add(AuthoredTone("clean", "Clean"));
        var vm = new Harness(project, new NullDspChainService()).Open();
        vm.IsPreviewOn = true;
        Assert.False(vm.IsPreviewOn);
    }

    // ------------------------------------------------------------------ import

    [Fact]
    public async Task Import_reads_tone_definitions_from_a_package_and_adds_to_project()
    {
        const string toneJson =
            """{"GearList":{},"IsCustom":true,"Volume":"-12.000","Key":"dc_clean","Name":"DC Clean","SortOrder":0.0}""";
        var project = CreateProjectWithChart();
        // The package reader is an optional plugin (#889): its importer stands in here, and the
        // plugin's own tests read real packages.
        var psarc = Path.Combine(_tempDir, "tones_p.psarc");
        using var importer = PackageTones.UseForTesting(new FakeToneImporter(path =>
            path == psarc ? [new PsarcToneDefinition("dc_clean", "DC Clean", toneJson)] : []));

        var harness = new Harness(project);
        var vm = harness.Open();
        harness.Storage.NextOpenFile = psarc;
        await ((CommunityToolkit.Mvvm.Input.IAsyncRelayCommand)vm.ImportTonesFromPsarcCommand).ExecuteAsync(null);

        var imported = Assert.Single(vm.ImportedTones);
        Assert.Equal(("dc_clean", "DC Clean"), (imported.Key, imported.Name));
        Assert.Contains("1", vm.ImportStatus);
        var body = harness.Memory.ImportedToneBodiesFor(project)["dc_clean"];
        using (var storedDoc = System.Text.Json.JsonDocument.Parse(body))
            Assert.Equal("-12.000", storedDoc.RootElement.GetProperty("Volume").GetString());

        await ((CommunityToolkit.Mvvm.Input.IAsyncRelayCommand)vm.ImportTonesFromPsarcCommand).ExecuteAsync(null);
        Assert.Single(vm.ImportedTones);

        vm.AddImportedToProjectCommand.Execute(imported);
        var def = Assert.Single(project.ToneDefinitions);
        Assert.Equal(("dc_clean", body), (def.Key, def.Json));
        Assert.Equal("dc_clean", vm.SelectedTone?.Key);
        vm.AddImportedToProjectCommand.Execute(imported);
        Assert.Single(project.ToneDefinitions);
        harness.History.Undo();
        Assert.Empty(project.ToneDefinitions);
    }

    /// <summary>The imported list belongs to the project it was imported for: a reopen keeps it, another project clears it.</summary>
    [Fact]
    public void Reopening_the_same_project_keeps_the_imported_tones_but_a_different_one_clears_them()
    {
        var harness = new Harness(CreateProject());
        harness.Open().CloseCommand.Execute(null);
        harness.Memory.ImportedTones.Add(new ToneDefinition { Key = "dc_clean", Name = "DC Clean", Json = "{}" });

        Assert.Single(harness.Open().ImportedTones);

        harness.State.ActiveProject = CreateProject();
        var other = harness.Open();
        Assert.Empty(other.ImportedTones);
        Assert.False(other.HasImportedTones);
    }

    /// <summary>The memory holds its project WEAKLY: a closed project's chart graph is not kept alive for its imported tones.</summary>
    [Fact]
    public void Switching_projects_leaves_the_designed_project_collectable()
    {
        var (harness, probe) = OpenTheDesignerForAProjectThenAbandonIt();
        harness.State.ActiveArrangement = null;
        harness.State.ActiveProject = new ChartProject();

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        Assert.False(probe.IsAlive, "the dialog memory is still holding the project the designer was last opened for");
    }

    private static (Harness Harness, WeakReference Probe) OpenTheDesignerForAProjectThenAbandonIt()
    {
        var project = CreateProject();
        var harness = new Harness(project);
        harness.State.ActiveArrangement = project.Arrangements[0];
        harness.Open().CloseCommand.Execute(null);
        harness.Forget();
        return (harness, new WeakReference(project));
    }

    /// <summary>Records what the designer hands the chart's tone playback (#139).</summary>
    private sealed class RecordingLiveEdit : DotCharter.Services.Audio.Coordination.IChartToneLiveEdit
    {
        public List<(string Key, string Json)> Edits { get; } = [];
        public int Ends { get; private set; }

        public void PreviewToneEdit(string key, string json) => Edits.Add((key, json));

        public void EndToneEditPreview() => Ends++;
    }

    /// <summary>#139: an edit is handed to the chart's tone playback at once, under the
    /// tone's COMMITTED key, with the buffered body — Volume included — and closing ends it.</summary>
    [Fact]
    public async Task AnEdit_IsHeardLive_UnderTheCommittedKey_UntilTheDesignerCloses()
    {
        var project = CreateProject();
        project.ToneDefinitions.Add(AuthoredTone("clean", "Clean"));
        var harness = new Harness(project) { LiveEdit = new RecordingLiveEdit() };
        var vm = harness.Open();
        vm.SelectedTone = vm.ProjectTones[0];
        harness.LiveEdit.Edits.Clear();

        vm.Editor.VolumeDb = -9.5;

        var (key, json) = Assert.Single(harness.LiveEdit.Edits);
        Assert.Equal("clean", key);
        Assert.Contains("-9.5", json, StringComparison.Ordinal);
        Assert.NotEqual(json, project.ToneDefinitions[0].Json);   // not committed yet

        Assert.True(vm.TryCancel());
        await vm.Result.WaitAsync(TestContext.Current.CancellationToken);
        Assert.Equal(1, harness.LiveEdit.Ends);
    }

    /// <summary>The dialog's own Preview holds the rig with the edited tone alone; the
    /// chart's playback is not driven underneath it (two writers, one rig).</summary>
    [Fact]
    public void WhileTheDesignersOwnPreviewIsOn_NoLiveEditIsPushed()
    {
        var project = CreateProject();
        project.ToneDefinitions.Add(AuthoredTone("clean", "Clean"));
        var harness = new Harness(project) { LiveEdit = new RecordingLiveEdit() };
        var vm = harness.Open();
        vm.SelectedTone = vm.ProjectTones[0];
        vm.IsPreviewOn = true;
        harness.LiveEdit.Edits.Clear();

        vm.Editor.VolumeDb = -9.5;

        Assert.Empty(harness.LiveEdit.Edits);
    }

    private sealed class Harness
    {
        private readonly IDspChainService _chain;
        private ToneDesignerViewModel? _vm;

        public Harness(ChartProject? project, IDspChainService? chain = null)
        {
            State.ActiveProject = project;
            _chain = chain ?? new FakeDspChainService(RigCatalog());
            History = new HistoryService(new FakeAppLogger());
        }

        public ProjectState State { get; } = new();
        public FakeConfigurationService Config { get; } = new();
        public FakeFileStorageService Storage { get; } = new();
        public HistoryService History { get; }
        public DialogMemory Memory { get; } = new();

        /// <summary>What the designer opens over itself (the AI tone creator); nothing opens by default.</summary>
        public DotCharter.Presentation.Dialogs.IDialogs Dialogs { get; set; } = new DotCharter.Presentation.Dialogs.DialogsSlot();

        /// <summary>#139: where the designer's live edits go; null = not wired.</summary>
        public RecordingLiveEdit? LiveEdit { get; set; }

        /// <summary>One open: a fresh view model, as the dialog host builds one per open.</summary>
        public ToneDesignerViewModel Open()
        {
            var logger = new FakeAppLogger();
            var services = new ChartEditDialogServices(RecordingProxy.Create<IChartEditorSurface>().Proxy, History, State, Config,
                FakeLocalization.English(), logger);
            _vm = new ToneDesignerViewModel(services, Storage, FakeTonelabSession.Targets(new TonePreviewSession(_chain, new DcRigApplier(_chain))),
                new ToneDesignerTools(new ToneGenerationRun(History, new DotCharter.Tone.Infrastructure.Storage.ExportTargetWriter(), logger), Dialogs),
                Memory,
                DotCharter.Services.Plugins.PackageFeatures.All, LiveEdit);
            _vm.Preview.OffThreadRunner = action =>
            {
                action();
                return Task.CompletedTask;
            };
            return _vm;
        }

        public void Forget() => _vm = null;
    }

    private static IReadOnlyList<DspModuleType> RigCatalog() =>
    [
        Module("amp", ("gain", 0.5f), ("bass", 0.5f), ("mid", 0.5f), ("treble", 0.5f), ("master", 0.5f)),
        Module("rsamp", [("model", 0f), .. Enumerable.Range(1, 10).Select(k => ("k" + k, 0.5f))]),
        Module("cab", ("model", 0f)),
        Module("gain", ("gainDb", 0f)),
        Module("overdrive", ("drive", 0.5f), ("tone", 0.7f), ("level", 0.8f)),
        Module("reverb", ("mix", 0.3f)),
        Module("delay", ("timeMs", 350f), ("mix", 0.3f)),
        Module("compressor", ("ratio", 4f)),
        Module("eq", ("midGainDb", 0f))
    ];

    private static DspModuleType Module(string id, params (string Id, float Def)[] ps) =>
        new(id, id, ps.Select(p => new DspParamInfo(p.Id, p.Id, -10000f, 10000f, p.Def)).ToList());

    private static ChartProject CreateProject()
    {
        var project = new ChartProject { Chart = { SongName = "Tone Song", Artist = "DotCharter" } };
        for (var i = 0; i < 8; i++) project.Beats.Add(new BeatMarker { Time = i * 500.0, IsMeasure = i % 4 == 0, Bpm = 120 });
        project.Arrangements.Add(new Arrangement
        { ArrangementType = ArrangementType.Lead, Name = "Lead", ToneBase = "clean", ToneA = "clean", ToneB = "dist" });
        project.Arrangements.Add(new Arrangement { ArrangementType = ArrangementType.Bass, Name = "Bass" });
        project.Arrangements.Add(new Arrangement { ArrangementType = ArrangementType.Vocal, Name = "Vocals" });
        return project;
    }

    private static ToneDefinition AuthoredTone(string key, string name) =>
        new() { Key = key, Name = name, Json = RsToneJsonWriter.Write(RsToneAuthoring.CreateDefaultTone(key, name, RsGearCatalog.Shared)) };

    /// <summary>Enough chart content for the package builder to produce a Lead arrangement.</summary>
    private static ChartProject CreateProjectWithChart()
    {
        var project = new ChartProject { Bpm = 120, Chart = { SongName = "Tone Song", Artist = "DotCharter" } };
        for (var i = 0; i < 64; i++)
            project.Beats.Add(new BeatMarker { Time = i * 500.0, IsMeasure = i % 4 == 0, Bpm = 120, BeatsInMeasure = 4, NoteDenominator = 4 });
        var beats = project.Beats;
        var lead = new Arrangement { ArrangementType = ArrangementType.Lead, Name = "Lead", StringCount = 6 };
        for (var i = 0; i < 6; i++) lead.TuningPitches.Add(new TuningOffset(0));
        lead.Phrases.AddRange(["COUNT", "verse", "END"]);
        lead.PhraseIterations.Add(new PhraseIteration(TimelineMath.PositionFromMs(0, beats), 0));
        lead.PhraseIterations.Add(new PhraseIteration(TimelineMath.PositionFromMs(2000, beats), 1));
        lead.PhraseIterations.Add(new PhraseIteration(TimelineMath.PositionFromMs(30000, beats), 2));
        var level = new ChartLevel { DifficultyIndex = 0 };
        level.Anchors.Add(new FretAnchor(TimelineMath.PositionFromMs(2000, beats), 1, 4));
        level.Notes.Add(new GuitarNote { Position = TimelineMath.PositionFromMs(2000, beats), StringIndex = 0, Fret = 3 });
        lead.Levels.Add(level);
        project.Arrangements.Add(lead);
        return project;
    }

    private sealed class FakeToneImporter(Func<string, IReadOnlyList<PsarcToneDefinition>> read) : IPackageToneImporter
    {
        public IReadOnlyList<PsarcToneDefinition> ReadToneDefinitions(string packagePath) => read(packagePath);
    }
}
