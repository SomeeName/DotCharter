using System.Text.Json;
using DotCharter.Tone.Audio.Dsp;
using DotCharter.Tone.Audio.Dsp.Presets;
using DotCharter.Tone.Audio.Dsp.RsToneConversion;
using DotCharter.Tone.UI.ViewModels.PlayMode;

namespace DotCharter.Tests.UI;

public sealed class RigControlAssignmentTests
{
    private static readonly DspModuleType Module = new("gain", "Gain", [new("gainDb", "Gain", -60, 24, 0)]);
    private static RigControlAssignment Mapping(int macro = 0) => new("gainDb", macro, -20, 20, 2, 7);
    private static (FakeDspChainService Dsp, PedalboardViewModel Rig, PedalNodeViewModel Node) Create()
    {
        var dsp = new FakeDspChainService([Module]);
        var rig = TestRigBuilders.Pedalboard(dsp, new FakeDspTonePresetService(), FakeLocalization.English());
        return (dsp, rig, rig.AddGear(Module)!);
    }
    [Fact]
    public void SnapshotDtoAndReusedNode_RestoreAndClearMappingsWithoutPersistingNodeIdentity()
    {
        var (dsp, _, node) = Create();
        var input = new[] { Mapping() };
        Assert.True(dsp.SetControlAssignments(node.NodeId, input));
        input[0] = Mapping(3);
        var snapshot = DcRigSnapshot.FromNodes(dsp.GetNodes());
        var dto = UserToneModuleDto.FromModule(snapshot.Modules[0]);
        var json = JsonSerializer.Serialize(dto);
        Assert.DoesNotContain("NodeId", json);
        var restored = JsonSerializer.Deserialize<UserToneModuleDto>(json)!.Clone().ToModule();
        Assert.Equal(Mapping(), Assert.Single(restored.ControlAssignments));
        var applier = new DcRigApplier(dsp);
        applier.Apply(new([restored with { ControlAssignments = [] }]));
        Assert.Equal(node.NodeId, Assert.Single(dsp.GetNodes()).NodeId);
        Assert.Empty(dsp.GetNodes()[0].ControlAssignments);
        applier.Apply(new([restored]));
        Assert.Equal(Mapping(), Assert.Single(dsp.GetNodes()[0].ControlAssignments));
        dsp.RemoveNode(node.NodeId);
        applier.Apply(new([restored]));
        Assert.NotEqual(node.NodeId, dsp.GetNodes()[0].NodeId);
        Assert.Equal(Mapping(), Assert.Single(dsp.GetNodes()[0].ControlAssignments));
    }
    [Fact]
    public void ValidationRejectsInvalidRangesDuplicatesAndOversizedLists_WithoutMutation()
    {
        var (dsp, _, node) = Create();
        Assert.True(dsp.SetControlAssignments(node.NodeId, [Mapping()]));
        foreach (var a in new[] { Mapping() with { Min = float.NaN }, Mapping() with { Max = 25 },
                     Mapping() with { MacroIndex = 4 }, Mapping() with { MidiChannel = -1 },
                     Mapping() with { ParameterId = "missing" } })
        {
            Assert.False(dsp.SetControlAssignments(node.NodeId, [a]));
        }

        Assert.False(dsp.SetControlAssignments(node.NodeId, [Mapping(), Mapping()]));
        Assert.False(dsp.SetControlAssignments(node.NodeId, Enumerable.Repeat(Mapping(), 65).ToArray()));
        Assert.Equal(Mapping(), Assert.Single(dsp.GetNodes()[0].ControlAssignments));
        Assert.False(new NullDspChainService().SetControlAssignments(node.NodeId, [Mapping()]));
    }
    [Fact]
    public void MacroAndMidiReachRealParameters_AndFailedWritesDoNotChangeVisibleState()
    {
        var (dsp, rig, node) = Create();
        var bank = rig.Controls;
        Assert.Same(bank, rig.Controls);
        Assert.False(bank.Macros[0].CanControl);
        dsp.SetControlAssignments(node.NodeId, [Mapping()]);
        bank.Macros[0].Value = .75f;
        Assert.Equal(10f, dsp.GetParam(node.NodeId, "gainDb"));
        Assert.Equal(10f, node.Params[0].Value);
        bank.HandleMidiControl(1, 7, 0);
        Assert.Equal(10f, node.Params[0].Value);
        bank.HandleMidiControl(2, 7, 0);
        Assert.Equal(-20f, node.Params[0].Value);
        dsp.FailParam = true;
        bank.Macros[0].Value = 1;
        Assert.Equal(-20f, node.Params[0].Value);
        Assert.Equal(0f, bank.Macros[0].Value);
    }
    [Fact]
    public void LearningConsumesFirstCc_PersistsMapping_AndTargetChangeCancels()
    {
        var (dsp, rig, node) = Create();
        var bank = rig.Controls;
        bank.Edit(node);
        var editor = bank.Editor;
        editor.MacroIndex = 1; editor.Min = -10; editor.Max = 10;
        var starts = 0;
        bank.LearningStarted += () => starts++;
        editor.BeginMidiLearnCommand.Execute(null);
        Assert.True(editor.IsLearning);
        bank.HandleMidiControl(3, 11, 127);
        Assert.Equal(1, starts);
        Assert.False(editor.IsLearning);
        Assert.Equal(0f, node.Params[0].Value);
        Assert.Equal(new RigControlAssignment("gainDb", 1, -10, 10, 3, 11),
            Assert.Single(dsp.GetNodes()[0].ControlAssignments));
        bank.HandleMidiControl(3, 11, 127);
        Assert.Equal(10f, node.Params[0].Value);
        editor.BeginMidiLearnCommand.Execute(null);
        rig.ResyncAll();
        Assert.False(editor.IsLearning);
        Assert.False(editor.CanEdit);
    }
    [Fact]
    public void ReorderKeepsAssignments_AndEditComparisonIncludesMetadata()
    {
        var (dsp, rig, node) = Create();
        dsp.SetControlAssignments(node.NodeId, [Mapping()]);
        var saved = DcRigSnapshot.FromNodes(dsp.GetNodes());
        Assert.False(RigEditState.IsEdited(DcRigSnapshot.FromNodes(dsp.GetNodes()), saved));
        dsp.SetControlAssignments(node.NodeId, [Mapping(1)]);
        Assert.True(RigEditState.IsEdited(DcRigSnapshot.FromNodes(dsp.GetNodes()), saved));
        rig.AddGear(Module);
        dsp.MoveNode(node.NodeId, 1); rig.Sync();
        rig.Controls.Macros[1].Value = 1;
        Assert.Equal(20f, dsp.GetParam(node.NodeId, "gainDb"));
        Assert.Empty(dsp.GetNodes()[0].ControlAssignments);
    }
    [Fact]
    public void PreparationAndNormalizationPreserveMappings_AndImportedMalformedMetadataIsIgnored()
    {
        var mod = new DcRigModule("gain", new Dictionary<string, float> { ["gainDb"] = 0 })
        { ControlAssignments = [Mapping()], Branch = 2 };
        var preset = new DcRigPreset([mod]);
        Assert.Equal(Mapping(), Assert.Single(DcRigTargetCatalog.FillDefaults(preset).Modules[0].ControlAssignments));
        var normalized = DcRigLoudnessNormalizer.Normalize(preset).Modules[0];
        Assert.Equal(Mapping(), Assert.Single(normalized.ControlAssignments));
        Assert.Equal(2, normalized.Branch);
        var malformed = new UserToneModuleDto
        {
            ModuleTypeId = "gain",
            ControlAssignments = [Mapping() with { Min = float.NaN }]
        };
        Assert.Empty(malformed.ToModule().ControlAssignments);
    }

    [Fact]
    public void CompareSlotsCarryAssignmentsThroughTheRealBoardSnapshotEntryPoint()
    {
        var dsp = new FakeDspChainService([Module]);
        var presets = new FakeDspTonePresetService();
        var rig = TestRigBuilders.Pedalboard(dsp, presets, FakeLocalization.English());
        var node = rig.AddGear(Module)!;
        dsp.SetControlAssignments(node.NodeId, [Mapping(0)]);
        rig.SelectCompareSlotCommand.Execute("B");
        dsp.SetControlAssignments(node.NodeId, [Mapping(2)]);
        rig.SelectCompareSlotCommand.Execute("A");
        var restored = Assert.Single(presets.AppliedPresetsPerPlayer).Preset.Rig;
        Assert.Equal(Mapping(0), Assert.Single(restored.Modules[0].ControlAssignments));
    }

    [Fact]
    public void ClearingSelectionCancelsLearn_AndNewSelectionTargetsItsOwnParameter()
    {
        var (dsp, rig, first) = Create();
        var second = rig.AddGear(Module)!;
        var bank = rig.Controls;
        rig.SelectedNode = first;
        bank.Editor.BeginMidiLearnCommand.Execute(null);
        Assert.True(bank.Editor.IsLearning);
        rig.SelectedNode = null;
        Assert.False(bank.Editor.IsLearning);
        Assert.False(bank.Editor.CanEdit);
        bank.HandleMidiControl(0, 12, 127);
        Assert.All(dsp.GetNodes(), n => Assert.Empty(n.ControlAssignments));
        rig.SelectedNode = second;
        Assert.Same(second.Params[0], bank.Editor.SelectedParameter);
        bank.Editor.MacroIndex = 3;
        bank.Editor.AssignCommand.Execute(null);
        Assert.Empty(dsp.GetNodes().Single(n => n.NodeId == first.NodeId).ControlAssignments);
        Assert.Equal(3, Assert.Single(dsp.GetNodes().Single(n => n.NodeId == second.NodeId).ControlAssignments).MacroIndex);
    }

    [Fact]
    public void EverySuccessfulMutationNotifiesCommit_EvenWhenStatusRemainsSaved()
    {
        var (dsp, rig, node) = Create();
        var bank = rig.Controls;
        bank.Edit(node);
        var editor = bank.Editor;
        var commits = 0;
        editor.AssignmentCommitted += () => commits++;
        editor.MacroIndex = 0;
        editor.AssignCommand.Execute(null);
        editor.MacroIndex = 1;
        editor.AssignCommand.Execute(null);
        Assert.Equal(2, commits);
        editor.Min = float.NaN;
        editor.AssignCommand.Execute(null);
        Assert.Equal(2, commits);
        editor.Min = node.Params[0].Minimum;
        editor.BeginMidiLearnCommand.Execute(null);
        bank.HandleMidiControl(0, 5, 0);
        Assert.Equal(3, commits);
        editor.ClearMidiCommand.Execute(null);
        Assert.Equal(4, commits);
        Assert.Equal(-1, Assert.Single(dsp.GetNodes()[0].ControlAssignments).MidiController);
        editor.RemoveCommand.Execute(null);
        Assert.Equal(5, commits);
        Assert.Empty(dsp.GetNodes()[0].ControlAssignments);
    }

    private sealed class Chains : IPlayerDspChains
    {
        public IReadOnlyList<IDspChainService> All { get; } = [new FakeDspChainService([Module]), new FakeDspChainService([Module])];
        public int MaxPlayers => All.Count;
        public IDspChainService For(int player) => All[player];
        public void AttachEngineToAll(nint engine) { }
    }
    [Fact]
    public void SharedBankRebindsSeats_EvenWhenNativeNodeIdsCollide()
    {
        var chains = new Chains();
        var rig = TestRigBuilders.Pedalboard(chains.For(0), new FakeDspTonePresetService(), FakeLocalization.English(), chains: chains);
        var a = rig.AddGear(Module)!;
        chains.For(0).SetControlAssignments(a.NodeId, [Mapping()]);
        var bank = rig.Controls;
        bank.Edit(a); bank.Editor.BeginMidiLearnCommand.Execute(null);
        rig.SetSeats([new(0, "A"), new(1, "B")]);
        rig.SelectedSeat = rig.Seats[1];
        var b = rig.AddGear(Module)!;
        Assert.Equal(a.NodeId, b.NodeId);
        Assert.False(bank.Editor.IsLearning);
        bank.HandleMidiControl(2, 7, 127);
        Assert.Equal(0f, chains.For(0).GetParam(a.NodeId, "gainDb"));
        Assert.Equal(0f, chains.For(1).GetParam(b.NodeId, "gainDb"));
        chains.For(1).SetControlAssignments(b.NodeId, [Mapping()]);
        bank.HandleMidiControl(2, 7, 127);
        Assert.Equal(20f, chains.For(1).GetParam(b.NodeId, "gainDb"));
        Assert.Equal(0f, chains.For(0).GetParam(a.NodeId, "gainDb"));
    }
}
