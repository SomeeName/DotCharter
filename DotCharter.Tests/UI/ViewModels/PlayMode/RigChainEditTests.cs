using DotCharter.Tone.Audio.Dsp;
using DotCharter.Tone.UI.ViewModels.PlayMode;

namespace DotCharter.Tests.UI.ViewModels.PlayMode;

/// <summary>
///     The board's three composition gestures beyond add/move:
///     <c>DuplicateCommand</c> (another one of these, here, carrying everything
///     the unit was set to), <c>ReplaceCommand</c> (swap the module in the slot,
///     keeping the slot's lane and bypass) and <c>RemoveCommand</c>'s one level of
///     undo (<c>UndoRemoveCommand</c>).
///     <para>
///         Every case asserts against the CHAIN — the fake service's own node list
///         and values — not merely against the view model's mirror, because
///         "applied natively first, mirrored on success" is the rule these edits
///         have to keep, and a mirror-only assertion passes for an edit the engine
///         refused.
///     </para>
/// </summary>
public sealed class RigChainEditTests
{
    // The default fake catalogue has no routing nodes and no string params; both
    // matter here (a junction is never duplicated, and a duplicated capture
    // player has to bring its capture).
    private static IReadOnlyList<DspModuleType> Catalog() =>
    [
        new DspModuleType("overdrive", "Overdrive",
        [
            new DspParamInfo("drive", "Drive", 0f, 1f, 0.5f),
            new DspParamInfo("level", "Level", 0f, 1f, 0.8f)
        ]),
        new DspModuleType("distortion", "Distortion", [new DspParamInfo("drive", "Drive", 0f, 1f, 0.5f)]),
        new DspModuleType("delay", "Delay", [new DspParamInfo("mix", "Mix", 0f, 1f, 0.3f)]),
        new DspModuleType("amp", "Amp", [new DspParamInfo("gain", "Gain", 0f, 1f, 0.5f)]),
        new DspModuleType("namamp", "Neural Amp", [new DspParamInfo("gain", "Gain", 0f, 1f, 0.5f)])
            { StringParams = [new DspStringParamInfo("modelPath", "Capture", "*.nam;*.aidax")] },
        new DspModuleType("splitter", "Splitter", [new DspParamInfo("enabledBranches", "Branches", 2f, 3f, 2f)]),
        new DspModuleType("merger", "Merger", [new DspParamInfo("mode", "Mode", 0f, 1f, 0f)])
    ];

    private static PedalboardViewModel NewRig(out FakeDspChainService dsp)
    {
        dsp = new FakeDspChainService(Catalog());
        return TestRigBuilders.Pedalboard(dsp, new FakeDspTonePresetService(), FakeLocalization.English());
    }

    private static PedalNodeViewModel Add(PedalboardViewModel rig, string id) =>
        rig.AddGear(rig.AvailableModules.First(m => m.Id == id))!;

    private static void AssertMirrorsChain(PedalboardViewModel rig, FakeDspChainService dsp) =>
        Assert.Equal(
            dsp.GetNodes().Select(n => n.NodeId).ToArray(),
            rig.Nodes.Select(n => n.NodeId).ToArray());

    // ── duplicate ────────────────────────────────────────────────────────────

    [Fact]
    public void Duplicate_SeatsACopyRightAfterTheUnit_AndCarriesEveryValue()
    {
        var rig = NewRig(out var dsp);
        var od = Add(rig, "overdrive");
        Add(rig, "amp");

        od.Params.First(p => p.Id == "drive").Value = 0.91f;
        od.Params.First(p => p.Id == "level").Value = 0.22f;
        od.Bypassed = true;

        rig.DuplicateCommand.Execute(od);

        // Right after the original, and the amp is pushed along behind it.
        Assert.Equal(["overdrive", "overdrive", "amp"],
            dsp.GetNodes().Select(n => n.ModuleTypeId).ToArray());
        AssertMirrorsChain(rig, dsp);

        var copy = rig.Nodes[1];
        Assert.NotSame(od, copy);
        Assert.Same(copy, rig.SelectedNode);

        // …and the copy is the same UNIT: the chain itself carries the values.
        var copied = dsp.GetNodes()[1];
        Assert.Equal(0.91f, copied.Values["drive"], 5);
        Assert.Equal(0.22f, copied.Values["level"], 5);
        Assert.True(copied.Bypassed);
    }

    [Fact]
    public void Duplicate_CarriesTheLoadedCapture_AndTheLaneTag()
    {
        var rig = NewRig(out var dsp);
        var amp = Add(rig, "namamp");
        // Through the card's own load path, so the chain and the mirror agree —
        // exactly what the picker and "load into rig" do.
        amp.StringParams.Single().LoadFile(@"C:\captures\plexi.nam");
        Assert.True(rig.DropToRow(amp, 0, branch: 2));

        rig.DuplicateCommand.Execute(amp);

        var copy = dsp.GetNodes()[1];
        // Dropping the string params would restore a MODELLESS neural amp, i.e. a
        // silent one — the DcRigSnapshot lesson, one level down.
        Assert.Equal(@"C:\captures\plexi.nam", copy.StringValues["modelPath"]);
        Assert.Equal(2, copy.Branch);
    }

    [Fact]
    public void Duplicate_IsRefusedForARoutingJunction()
    {
        var rig = NewRig(out _);
        Add(rig, "splitter"); // auto-pairs a merger
        var splitter = rig.Nodes.First(n => n.IsSplitter);
        var merger = rig.Nodes.First(n => n.IsMerger);
        var before = rig.Nodes.Count;

        Assert.False(rig.DuplicateCommand.CanExecute(splitter));
        Assert.False(rig.DuplicateCommand.CanExecute(merger));
        Assert.Null(rig.DuplicateNode(splitter));
        Assert.Null(rig.DuplicateNode(merger));
        Assert.Equal(before, rig.Nodes.Count);
    }

    [Fact]
    public void Duplicate_ThatTheChainRefuses_ChangesNothing()
    {
        var rig = NewRig(out var dsp);
        var od = Add(rig, "overdrive");
        var before = dsp.GetNodes().Count;

        dsp.FailAdd = true;
        Assert.Null(rig.DuplicateNode(od));

        Assert.Equal(before, dsp.GetNodes().Count);
        Assert.Single(rig.Nodes);
        AssertMirrorsChain(rig, dsp);
    }

    // ── replace ──────────────────────────────────────────────────────────────

    [Fact]
    public void Replace_SwapsTheModuleInPlace_KeepingTheLaneAndTheBypass()
    {
        var rig = NewRig(out var dsp);
        Add(rig, "delay");
        var od = Add(rig, "overdrive");
        Add(rig, "amp");
        od.Bypassed = true;
        Assert.True(rig.DropToRow(od, 1, branch: 3));
        rig.SelectedNode = od;

        var distortion = rig.AvailableModules.First(m => m.Id == "distortion");
        rig.ReplaceCommand.Execute(new RigModuleReplacement(od, distortion));

        Assert.Equal(["delay", "distortion", "amp"],
            dsp.GetNodes().Select(n => n.ModuleTypeId).ToArray());
        AssertMirrorsChain(rig, dsp);

        var fresh = dsp.GetNodes()[1];
        Assert.True(fresh.Bypassed);       // the SLOT was switched out, not the module
        Assert.Equal(3, fresh.Branch);     // …and it was running on lane C
        Assert.Same(rig.Nodes[1], rig.SelectedNode);
        Assert.Equal("distortion", rig.SelectedNode!.ModuleTypeId);
    }

    [Fact]
    public void Replace_RefusesAJunction_OnEitherSide()
    {
        var rig = NewRig(out _);
        Add(rig, "splitter");
        var splitter = rig.Nodes.First(n => n.IsSplitter);
        var od = Add(rig, "overdrive");
        var distortion = rig.AvailableModules.First(m => m.Id == "distortion");
        var splitterType = rig.AvailableModules.First(m => m.Id == "splitter");
        var mergerType = rig.AvailableModules.First(m => m.Id == "merger");

        Assert.False(rig.ReplaceCommand.CanExecute(new RigModuleReplacement(splitter, distortion)));
        Assert.False(rig.ReplaceCommand.CanExecute(new RigModuleReplacement(od, splitterType)));
        Assert.False(rig.ReplaceCommand.CanExecute(new RigModuleReplacement(od, mergerType)));

        var before = rig.Nodes.Select(n => n.ModuleTypeId).ToArray();
        Assert.Null(rig.ReplaceNode(splitter, distortion));
        Assert.Null(rig.ReplaceNode(od, splitterType));
        Assert.Null(rig.ReplaceNode(od, mergerType));
        Assert.Equal(before, rig.Nodes.Select(n => n.ModuleTypeId).ToArray());
    }

    [Fact]
    public void Replace_WhoseRemovalIsRefused_LeavesTheBoardAsItWas()
    {
        var rig = NewRig(out var dsp);
        var od = Add(rig, "overdrive");
        Add(rig, "amp");
        var distortion = rig.AvailableModules.First(m => m.Id == "distortion");

        // The swap's OWN removal is refused; the rollback's is not (the fake's one
        // flag would otherwise refuse both, which is a different story — there the
        // chain keeps two units and the mirror still matches it).
        var removals = 0;
        dsp.FailRemove = true;
        dsp.BeforeRemoveNode = () =>
        {
            if (++removals >= 2) dsp.FailRemove = false;
        };

        Assert.Null(rig.ReplaceNode(od, distortion));

        // The add is rolled back, so the board never shows two units in one slot.
        Assert.Equal(2, removals);
        Assert.Equal(["overdrive", "amp"], dsp.GetNodes().Select(n => n.ModuleTypeId).ToArray());
        AssertMirrorsChain(rig, dsp);
    }

    // ── remove + undo ────────────────────────────────────────────────────────

    [Fact]
    public void UndoRemove_PutsTheUnitBackWhereItWas_WithEverythingItCarried()
    {
        var rig = NewRig(out var dsp);
        Add(rig, "delay");
        var od = Add(rig, "overdrive");
        Add(rig, "amp");
        od.Params.First(p => p.Id == "drive").Value = 0.77f;
        od.Bypassed = true;
        Assert.True(rig.DropToRow(od, 1, branch: 2));
        rig.SelectedNode = od;

        Assert.False(rig.CanUndoRemove);
        rig.RemoveCommand.Execute(od);

        Assert.True(rig.CanUndoRemove);
        Assert.Equal("overdrive", rig.LastRemoved!.ModuleTypeId);
        Assert.Equal(1, rig.LastRemoved.Index);
        Assert.True(rig.LastRemoved.WasSelected);
        Assert.Equal(["delay", "amp"], dsp.GetNodes().Select(n => n.ModuleTypeId).ToArray());

        rig.UndoRemoveCommand.Execute(null);

        Assert.Equal(["delay", "overdrive", "amp"],
            dsp.GetNodes().Select(n => n.ModuleTypeId).ToArray());
        AssertMirrorsChain(rig, dsp);
        var restored = dsp.GetNodes()[1];
        Assert.Equal(0.77f, restored.Values["drive"], 5);
        Assert.True(restored.Bypassed);
        Assert.Equal(2, restored.Branch);
        Assert.Same(rig.Nodes[1], rig.SelectedNode);

        // One level, and it is spent: a second press may not add a second copy.
        Assert.False(rig.CanUndoRemove);
        Assert.Null(rig.LastRemoved);
        Assert.False(rig.UndoRemoveCommand.CanExecute(null));
        rig.UndoRemoveCommand.Execute(null);
        Assert.Equal(3, dsp.GetNodes().Count);
    }

    [Fact]
    public void UndoRemove_RestoresTheCapture_OfARemovedNeuralAmp()
    {
        var rig = NewRig(out var dsp);
        var amp = Add(rig, "namamp");
        amp.StringParams.Single().LoadFile(@"C:\captures\tweed.nam");

        rig.RemoveCommand.Execute(amp);
        rig.UndoRemoveCommand.Execute(null);

        Assert.Equal(@"C:\captures\tweed.nam", dsp.GetNodes().Single().StringValues["modelPath"]);
    }

    [Fact]
    public void RemovingAJunction_RecordsNothing_BecauseHalfAPairCannotBePutBack()
    {
        var rig = NewRig(out _);
        Add(rig, "splitter");
        var merger = rig.Nodes.First(n => n.IsMerger);

        rig.RemoveCommand.Execute(merger);

        Assert.False(rig.CanUndoRemove);
        Assert.Null(rig.LastRemoved);
    }

    [Fact]
    public void ARemovalTheChainRefuses_RecordsNoUndo()
    {
        var rig = NewRig(out var dsp);
        var od = Add(rig, "overdrive");

        dsp.FailRemove = true;
        rig.RemoveCommand.Execute(od);

        Assert.Single(rig.Nodes);
        Assert.False(rig.CanUndoRemove);
    }

    [Fact]
    public void TheUndoSnapshot_IsDroppedWhenTheBoardItBelongedToIsGone()
    {
        // A seat repoint / profile switch / engine rebuild all land on ResyncAll,
        // and the recorded chain index means nothing against another board.
        var rig = NewRig(out _);
        var od = Add(rig, "overdrive");
        rig.RemoveCommand.Execute(od);
        Assert.True(rig.CanUndoRemove);

        rig.ResyncAll();

        Assert.False(rig.CanUndoRemove);
        Assert.Null(rig.LastRemoved);
    }

    [Fact]
    public void TheUndoSnapshot_IsDroppedByAPresetApply()
    {
        var presets = new FakeDspTonePresetService();
        var dsp = new FakeDspChainService(Catalog());
        var rig = TestRigBuilders.Pedalboard(dsp, presets, FakeLocalization.English());
        var od = Add(rig, "overdrive");
        rig.RemoveCommand.Execute(od);
        Assert.True(rig.CanUndoRemove);

        rig.Browser.ApplyPreset(presets.AllPresets[0]);

        Assert.False(rig.CanUndoRemove);
    }

    // ── the editor header's per-unit actions ─────────────────────────────────

    [Fact]
    public void TheDockedUnit_CarriesTheRigsOwnEditCommands_AndSameIdentityReplacements()
    {
        var rig = NewRig(out _);
        var screen = new AmpFxRigViewModel(rig);
        var od = Add(rig, "overdrive");
        rig.SelectedNode = od;

        var dock = screen.DockUnit!;
        Assert.True(dock.CanEditComposition);
        // The SAME command objects, not a second door onto them.
        Assert.Same(rig.DuplicateCommand, dock.DuplicateCommand);
        Assert.Same(rig.RemoveCommand, dock.RemoveCommand);

        Assert.True(dock.HasReplaceOptions);
        Assert.Contains(dock.ReplaceOptions, o => o.Request.Type!.Id == "distortion");
        Assert.DoesNotContain(dock.ReplaceOptions, o => o.Request.Type!.Id == "overdrive");
        Assert.All(dock.ReplaceOptions, o =>
        {
            Assert.Same(rig.ReplaceCommand, o.Command);
            Assert.Same(od, o.Request.Node);
        });

        // …and the option really performs the swap.
        var option = dock.ReplaceOptions.First(o => o.Request.Type!.Id == "distortion");
        option.Command.Execute(option.Request);
        Assert.Equal("distortion", rig.Nodes.Single().ModuleTypeId);
    }

    [Fact]
    public void ADockedJunction_OffersNoCompositionActions()
    {
        var rig = NewRig(out _);
        var screen = new AmpFxRigViewModel(rig);
        Add(rig, "splitter");
        rig.SelectedNode = rig.Nodes.First(n => n.IsSplitter);

        var dock = screen.DockUnit!;
        Assert.False(dock.CanEditComposition);
        Assert.False(dock.HasReplaceOptions);
    }

    [Fact]
    public void TheScreensUndoChip_FollowsTheRigsSnapshot()
    {
        var rig = NewRig(out _);
        var screen = new AmpFxRigViewModel(rig);
        Assert.False(screen.CanUndoRemove);

        var raised = 0;
        screen.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(AmpFxRigViewModel.CanUndoRemove)) raised++;
        };

        rig.RemoveCommand.Execute(Add(rig, "overdrive"));

        Assert.True(screen.CanUndoRemove);
        Assert.True(raised > 0);
        Assert.Same(rig.UndoRemoveCommand, screen.UndoRemoveCommand);
        Assert.Equal(rig.UndoRemoveLabel, screen.UndoRemoveLabel);
    }

    [Fact]
    public void TheUndoLabel_NamesTheModule_AndIsEmptyWithNothingToUndo()
    {
        var rig = NewRig(out _);
        Assert.Equal(string.Empty, rig.UndoRemoveLabel);

        rig.RemoveCommand.Execute(Add(rig, "overdrive"));

        Assert.Contains("Overdrive", rig.UndoRemoveLabel);
    }
}
