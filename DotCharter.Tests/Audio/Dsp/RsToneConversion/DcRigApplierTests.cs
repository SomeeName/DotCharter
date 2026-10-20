using DotCharter.Tone.Audio.Dsp;
using DotCharter.Tone.Audio.Dsp.RsToneConversion;

namespace DotCharter.Tests.Audio.Dsp.RsToneConversion;

/// <summary>
/// Unit tests for the minimal-diff engine. Every case asserts BOTH the resulting
/// chain state and the churn tally, and the no-op path is verified to issue zero
/// native calls (the coordinator re-applies the active tone on every seek/pre-roll,
/// so a matching preset MUST be free).
/// </summary>
public sealed class DcRigApplierTests
{
    private static IReadOnlyList<DspModuleType> Catalog() =>
    [
        Module("amp", ("gain", 0.5f), ("bass", 0.5f), ("master", 0.5f)),
        Module("cab", ("model", 0f)),
        Module("overdrive", ("drive", 0.5f), ("tone", 0.7f), ("level", 0.8f)),
        Module("distortion", ("gain", 0.5f), ("tone", 0.5f), ("level", 0.5f)),
        Module("delay", ("timeMs", 350f), ("feedback", 0.35f), ("mix", 0.3f)),
        Module("reverb", ("roomSize", 0.5f), ("mix", 0.3f)),
        Module("eq", ("lowGainDb", 0f), ("midGainDb", 0f), ("highGainDb", 0f)),
        Module("gain", ("gainDb", 0f)),
        Module("miku"), // vocal-synth stomp; its "phrases" string param is not a DcRig knob
    ];

    private static DspModuleType Module(string id, params (string Id, float Def)[] ps) =>
        new(id, id, ps.Select(p => new DspParamInfo(p.Id, p.Id, -100f, 100f, p.Def)).ToList());

    private static FakeDspChainService NewChain() => new(Catalog());

    private static DcRigModule Mod(string type, params (string Id, float Value)[] ps) =>
        new(type, ps.ToDictionary(p => p.Id, p => p.Value, StringComparer.Ordinal));

    private static DcRigPreset Preset(params DcRigModule[] mods) => new(mods);

    private static string[] Order(IDspChainService chain) =>
        chain.GetNodes().Select(n => n.ModuleTypeId).ToArray();

    // ------------------------------------------------------------------ no-op

    [Fact]
    public void EmptyPreset_OnEmptyChain_IsNoOp()
    {
        var chain = NewChain();
        var applier = new DcRigApplier(chain);

        var r = applier.Apply(Preset());

        Assert.True(r.NoOp);
        Assert.Empty(chain.GetNodes());
        Assert.Empty(chain.Adds);
        Assert.Empty(chain.Removes);
    }

    [Fact]
    public void MatchingPreset_ReAppliedIsNoOp_WithZeroNativeCalls()
    {
        var chain = NewChain();
        var applier = new DcRigApplier(chain);
        var preset = Preset(
            Mod("amp", ("gain", 0.8f), ("master", 0.6f)),
            Mod("cab", ("model", 1f)),
            Mod("gain", ("gainDb", -3f)));

        applier.Apply(preset); // build it
        chain.Adds.Clear();
        chain.Removes.Clear();
        chain.Moves.Clear();
        chain.ParamSets.Clear();
        chain.BypassSets.Clear();

        var r = applier.Apply(preset); // re-apply the SAME preset

        Assert.True(r.NoOp);
        Assert.Empty(chain.Adds);
        Assert.Empty(chain.Removes);
        Assert.Empty(chain.Moves);
        Assert.Empty(chain.ParamSets);
        Assert.Empty(chain.BypassSets);
    }

    // -------------------------------------------------------------------- add

    [Fact]
    public void AddsModules_OntoEmptyChain_InOrder_WithParams()
    {
        var chain = NewChain();
        var applier = new DcRigApplier(chain);

        var r = applier.Apply(Preset(
            Mod("overdrive", ("drive", 0.9f)),
            Mod("amp", ("gain", 0.7f)),
            Mod("cab"),
            Mod("gain", ("gainDb", -6f))));

        Assert.Equal(new[] { "overdrive", "amp", "cab", "gain" }, Order(chain));
        Assert.Equal(4, r.Added);
        Assert.Equal(0, r.Removed);
        Assert.Equal(0, r.Moved);
        // Params set while the node was at the tail (before any move).
        Assert.Contains(chain.ParamSets, p => p.ParamId == "drive" && Math.Abs(p.Value - 0.9f) < 1e-6);
        Assert.Contains(chain.ParamSets, p => p.ParamId == "gainDb" && Math.Abs(p.Value - -6f) < 1e-6);
    }

    // ----------------------------------------------------------------- remove

    [Fact]
    public void RemovesSurplusModules_KeepingReusedTail()
    {
        var chain = NewChain();
        var applier = new DcRigApplier(chain);
        applier.Apply(Preset(Mod("overdrive"), Mod("amp"), Mod("cab"), Mod("gain")));
        var ampId = chain.GetNodes().Single(n => n.ModuleTypeId == "amp").NodeId;

        var r = applier.Apply(Preset(Mod("amp"), Mod("cab"), Mod("gain")));

        Assert.Equal(new[] { "amp", "cab", "gain" }, Order(chain));
        Assert.Equal(1, r.Removed);
        Assert.Equal(0, r.Added);
        // The amp survived in place — same native node id (minimal churn).
        Assert.Equal(ampId, chain.GetNodes().Single(n => n.ModuleTypeId == "amp").NodeId);
    }

    // ---------------------------------------------------------------- reorder

    [Fact]
    public void ReordersSameModules_ByMovingOnly_NoAddOrRemove()
    {
        var chain = NewChain();
        var applier = new DcRigApplier(chain);
        applier.Apply(Preset(Mod("amp"), Mod("delay")));
        var ampId = chain.GetNodes().Single(n => n.ModuleTypeId == "amp").NodeId;
        var delayId = chain.GetNodes().Single(n => n.ModuleTypeId == "delay").NodeId;

        var r = applier.Apply(Preset(Mod("delay"), Mod("amp")));

        Assert.Equal(new[] { "delay", "amp" }, Order(chain));
        Assert.Equal(0, r.Added);
        Assert.Equal(0, r.Removed);
        Assert.True(r.Moved >= 1);
        // Both nodes were reused (ids preserved) — a pure reorder, not remove+add.
        Assert.Equal(ampId, chain.GetNodes().Single(n => n.ModuleTypeId == "amp").NodeId);
        Assert.Equal(delayId, chain.GetNodes().Single(n => n.ModuleTypeId == "delay").NodeId);
    }

    // ------------------------------------------------------------- param-only

    [Fact]
    public void ParamOnlyChange_SetsOnlyTheChangedParam()
    {
        var chain = NewChain();
        var applier = new DcRigApplier(chain);
        applier.Apply(Preset(Mod("amp", ("gain", 0.5f), ("master", 0.5f))));
        chain.ParamSets.Clear();

        var r = applier.Apply(Preset(Mod("amp", ("gain", 0.8f), ("master", 0.5f))));

        Assert.Equal(0, r.Added);
        Assert.Equal(0, r.Removed);
        Assert.Equal(0, r.Moved);
        Assert.Equal(1, r.ParamsSet);
        Assert.Single(chain.ParamSets);
        Assert.Equal("gain", chain.ParamSets[0].ParamId);
    }

    [Fact]
    public void BypassChange_OnReusedNode_IsApplied()
    {
        var chain = NewChain();
        var applier = new DcRigApplier(chain);
        applier.Apply(Preset(Mod("overdrive")));
        chain.BypassSets.Clear();

        var r = applier.Apply(Preset(new DcRigModule("overdrive",
            new Dictionary<string, float>(StringComparer.Ordinal), Bypassed: true)));

        Assert.Equal(1, r.BypassSet);
        Assert.True(chain.GetNodes().Single().Bypassed);
    }

    // ----------------------------------------------------- idempotency + fail

    [Fact]
    public void ApplyingTwice_SecondTimeIsNoOp()
    {
        var chain = NewChain();
        var applier = new DcRigApplier(chain);
        var preset = Preset(Mod("overdrive", ("drive", 0.6f)), Mod("amp"), Mod("gain"));

        applier.Apply(preset);
        var r = applier.Apply(preset);

        Assert.True(r.NoOp);
    }

    [Fact]
    public void AddFailure_DoesNotThrow_AndSkipsTheModule()
    {
        var chain = NewChain();
        chain.FailAdd = true;
        var applier = new DcRigApplier(chain);

        var ex = Record.Exception(() => applier.Apply(Preset(Mod("amp"), Mod("gain"))));

        Assert.Null(ex);
        Assert.Empty(chain.GetNodes()); // both adds failed, chain stays empty — no crash
    }

    [Fact]
    public void FailedAdds_AreCountedInFailedOps_AndLogged()
    {
        var chain = NewChain();
        chain.FailAdd = true;
        var logger = new FakeAppLogger();
        var applier = new DcRigApplier(chain, logger);

        var r = applier.Apply(Preset(Mod("amp"), Mod("gain")));

        Assert.Equal(0, r.Added);
        Assert.Equal(2, r.FailedOps);
        Assert.True(logger.HasEntryContaining("WARN", "AddNode('amp') failed"));
    }

    [Fact]
    public void FailedParamSets_OnReusedNodes_AreCountedAndLogged()
    {
        var chain = NewChain();
        var logger = new FakeAppLogger();
        var applier = new DcRigApplier(chain, logger);
        applier.Apply(Preset(Mod("amp", ("gain", 0.5f), ("master", 0.5f))));
        chain.FailParam = true;

        var r = applier.Apply(Preset(Mod("amp", ("gain", 0.9f), ("master", 0.1f))));

        Assert.Equal(0, r.ParamsSet); // both rejected — nothing counted as set
        Assert.Equal(2, r.FailedOps);
        // One aggregated warning naming the rejected params (not one line each).
        var warn = Assert.Single(logger.Entries, e => e.Level == "WARN");
        Assert.Contains("amp.gain", warn.Message);
        Assert.Contains("amp.master", warn.Message);
    }

    [Fact]
    public void FullySuccessfulApply_ReportsZeroFailedOps_AndNoWarnings()
    {
        var chain = NewChain();
        var logger = new FakeAppLogger();
        var applier = new DcRigApplier(chain, logger);

        var r = applier.Apply(Preset(Mod("amp", ("gain", 0.7f)), Mod("cab")));

        Assert.Equal(0, r.FailedOps);
        Assert.DoesNotContain(logger.Entries, e => e.Level == "WARN");
    }

    [Fact]
    public void SwitchingTones_ReusesSharedTail_ChurnsOnlyTheDrive()
    {
        // Clean tone: amp + cab + gain. High-gain tone adds a distortion in front
        // and keeps the same cab + gain tail — those must NOT be torn down.
        var chain = NewChain();
        var applier = new DcRigApplier(chain);
        applier.Apply(Preset(Mod("amp", ("gain", 0.3f)), Mod("cab"), Mod("gain")));
        var cabId = chain.GetNodes().Single(n => n.ModuleTypeId == "cab").NodeId;
        var gainId = chain.GetNodes().Single(n => n.ModuleTypeId == "gain").NodeId;

        var r = applier.Apply(Preset(
            Mod("distortion", ("gain", 0.9f)),
            Mod("amp", ("gain", 0.8f)),
            Mod("cab"),
            Mod("gain")));

        Assert.Equal(new[] { "distortion", "amp", "cab", "gain" }, Order(chain));
        Assert.Equal(1, r.Added); // only the distortion is new
        Assert.Equal(0, r.Removed);
        Assert.Equal(cabId, chain.GetNodes().Single(n => n.ModuleTypeId == "cab").NodeId);
        Assert.Equal(gainId, chain.GetNodes().Single(n => n.ModuleTypeId == "gain").NodeId);
    }

    // A complete preset must have the same custom content on add and reuse.
    // Omission clears previous content; explicit strings survive re-creation.

    [Fact]
    public void StringParam_OmittedFromPreset_IsResetOnReusedNode()
    {
        var chain = NewChain();
        var applier = new DcRigApplier(chain);
        applier.Apply(Preset(Mod("amp", ("gain", 0.3f)), Mod("cab"), Mod("miku")));

        var cab = chain.GetNodes().Single(n => n.ModuleTypeId == "cab");
        var miku = chain.GetNodes().Single(n => n.ModuleTypeId == "miku");
        chain.SetNodeString(cab.NodeId, "irPath", "C:/irs/user-cab.wav");
        chain.SetNodeString(miku.NodeId, "phrases", "mi ku da-yo");

        // Reuse native nodes without retaining the previous tone's custom content.
        var r = applier.Apply(Preset(Mod("amp", ("gain", 0.9f)), Mod("cab", ("model", 2f)), Mod("miku")));

        Assert.Equal(0, r.Added);
        Assert.Equal(0, r.Removed);
        Assert.Equal(cab.NodeId, chain.GetNodes().Single(n => n.ModuleTypeId == "cab").NodeId);
        Assert.Equal(miku.NodeId, chain.GetNodes().Single(n => n.ModuleTypeId == "miku").NodeId);
        Assert.Equal(string.Empty, chain.GetNodeString(cab.NodeId, "irPath"));
        Assert.Equal(string.Empty, chain.GetNodeString(miku.NodeId, "phrases"));
    }

    [Fact]
    public void StringParam_IsLost_WhenApplyReAddsAFreshNode()
    {
        // Documented limitation for a preset that does NOT carry the string param:
        // the string lives only on the native node, so removing then re-adding a
        // module of the same type yields a FRESH node back at the built-in default.
        // (A preset that DOES carry StringParams re-pushes it — see the tests
        // below.) The UI must re-push a manually-set modelPath/phrases after such a
        // change.
        var chain = NewChain();
        var applier = new DcRigApplier(chain);
        applier.Apply(Preset(Mod("amp"), Mod("cab")));
        var originalCab = chain.GetNodes().Single(n => n.ModuleTypeId == "cab");
        chain.SetNodeString(originalCab.NodeId, "irPath", "C:/irs/user-cab.wav");

        // Switch to a tone with no cab (cab removed), then back to one WITH a cab.
        applier.Apply(Preset(Mod("amp")));
        Assert.DoesNotContain("cab", Order(chain));
        applier.Apply(Preset(Mod("amp"), Mod("cab")));

        var freshCab = chain.GetNodes().Single(n => n.ModuleTypeId == "cab");
        Assert.NotEqual(originalCab.NodeId, freshCab.NodeId); // a brand-new node
        Assert.Null(chain.GetNodeString(freshCab.NodeId, "irPath")); // string did NOT carry over
    }

    // ---- StringParams carried BY the preset (ABI v22 capture-backed presets) ----
    // A DcRigModule may carry StringParams (e.g. a namamp modelPath). The applier
    // pushes them via SetNodeString after node creation, so a capture-backed preset
    // works even across an add/re-add — and an identical re-apply stays a NoOp.

    private static DcRigModule ModStr(
        string type, IReadOnlyDictionary<string, string> strings, params (string Id, float Value)[] ps) =>
        new(type, ps.ToDictionary(p => p.Id, p => p.Value, StringComparer.Ordinal)) { StringParams = strings };

    [Fact]
    public void PresetStringParam_IsPushed_WhenNodeIsAdded()
    {
        var chain = NewChain();
        var applier = new DcRigApplier(chain);

        var r = applier.Apply(Preset(
            ModStr("cab", new Dictionary<string, string> { ["irPath"] = "captures/deluxe.nam" }, ("model", 1f))));

        var cab = chain.GetNodes().Single(n => n.ModuleTypeId == "cab");
        Assert.Equal("captures/deluxe.nam", chain.GetNodeString(cab.NodeId, "irPath"));
        Assert.Contains(chain.StringSets, s => s.ParamId == "irPath" && s.Value == "captures/deluxe.nam");
        Assert.False(r.NoOp); // a pushed string counts as work
    }

    [Fact]
    public void PresetStringParam_IsRePushed_WhenNodeIsReAdded()
    {
        var chain = NewChain();
        var applier = new DcRigApplier(chain);
        var withCap = Preset(Mod("amp"),
            ModStr("cab", new Dictionary<string, string> { ["irPath"] = "captures/deluxe.nam" }));

        applier.Apply(withCap);
        applier.Apply(Preset(Mod("amp")));          // cab removed
        applier.Apply(withCap);                      // fresh cab node — preset re-pushes the string

        var freshCab = chain.GetNodes().Single(n => n.ModuleTypeId == "cab");
        Assert.Equal("captures/deluxe.nam", chain.GetNodeString(freshCab.NodeId, "irPath"));
    }

    [Fact]
    public void PresetStringParam_IdenticalReapply_IsNoOp_NoRedundantStringSet()
    {
        var chain = NewChain();
        var applier = new DcRigApplier(chain);
        var preset = Preset(ModStr("cab", new Dictionary<string, string> { ["irPath"] = "captures/deluxe.nam" }));

        applier.Apply(preset);
        chain.StringSets.Clear();
        var r = applier.Apply(preset); // reused node, unchanged string

        Assert.True(r.NoOp);
        Assert.Empty(chain.StringSets); // diffed against the node cache — no native call
    }

    [Fact]
    public void PresetStringParam_ChangedOnReuse_IsRePushed()
    {
        var chain = NewChain();
        var applier = new DcRigApplier(chain);
        applier.Apply(Preset(ModStr("cab", new Dictionary<string, string> { ["irPath"] = "captures/one.nam" })));
        var cab = chain.GetNodes().Single(n => n.ModuleTypeId == "cab");

        applier.Apply(Preset(ModStr("cab", new Dictionary<string, string> { ["irPath"] = "captures/two.nam" })));

        Assert.Equal(cab.NodeId, chain.GetNodes().Single(n => n.ModuleTypeId == "cab").NodeId); // reused
        Assert.Equal("captures/two.nam", chain.GetNodeString(cab.NodeId, "irPath"));
    }

    // ----------------------------------------------------- branch-tag replay (v23)
    // A preset's split/parallel branch tag survives a re-add (native tags do NOT),
    // is pushed on add and diffed on reuse (identical reapply = NoOp).

    private static DcRigModule ModBranch(string type, int branch, params (string Id, float Value)[] ps) =>
        new(type, ps.ToDictionary(p => p.Id, p => p.Value, StringComparer.Ordinal)) { Branch = branch };

    [Fact]
    public void PresetBranch_IsPushed_WhenNodeIsAdded()
    {
        var chain = NewChain();
        var applier = new DcRigApplier(chain);

        var r = applier.Apply(Preset(ModBranch("overdrive", 2)));

        var od = chain.GetNodes().Single(n => n.ModuleTypeId == "overdrive");
        Assert.Equal(2, chain.GetNodeBranch(od.NodeId));
        Assert.Contains(chain.BranchSets, s => s.Branch == 2);
        Assert.False(r.NoOp); // a pushed branch counts as work
    }

    [Fact]
    public void PresetBranch_Zero_OnAdd_IsNotPushed()
    {
        // A fresh node is already branch 0 native-side, so a branch-0 module makes
        // no SetNodeBranch call (keeps a plain linear preset free of routing noise).
        var chain = NewChain();
        var applier = new DcRigApplier(chain);

        applier.Apply(Preset(Mod("overdrive")));

        Assert.Empty(chain.BranchSets);
    }

    [Fact]
    public void PresetBranch_IsRePushed_WhenNodeIsReAdded()
    {
        var chain = NewChain();
        var applier = new DcRigApplier(chain);
        var withBranch = Preset(Mod("amp"), ModBranch("overdrive", 3));

        applier.Apply(withBranch);
        applier.Apply(Preset(Mod("amp")));   // overdrive removed
        applier.Apply(withBranch);           // fresh overdrive node — branch re-pushed

        var fresh = chain.GetNodes().Single(n => n.ModuleTypeId == "overdrive");
        Assert.Equal(3, chain.GetNodeBranch(fresh.NodeId));
    }

    [Fact]
    public void PresetBranch_IdenticalReapply_IsNoOp_NoRedundantBranchSet()
    {
        var chain = NewChain();
        var applier = new DcRigApplier(chain);
        var preset = Preset(ModBranch("overdrive", 2));

        applier.Apply(preset);
        chain.BranchSets.Clear();
        var r = applier.Apply(preset); // reused node, unchanged branch

        Assert.True(r.NoOp);
        Assert.Empty(chain.BranchSets); // diffed against the mirror — no native call
    }

    [Fact]
    public void PresetBranch_ChangedOnReuse_IsRePushed()
    {
        var chain = NewChain();
        var applier = new DcRigApplier(chain);
        applier.Apply(Preset(ModBranch("overdrive", 1)));
        var od = chain.GetNodes().Single(n => n.ModuleTypeId == "overdrive");

        applier.Apply(Preset(ModBranch("overdrive", 2)));

        Assert.Equal(od.NodeId, chain.GetNodes().Single(n => n.ModuleTypeId == "overdrive").NodeId); // reused
        Assert.Equal(2, chain.GetNodeBranch(od.NodeId));
    }
}
