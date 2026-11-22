using DotCharter.Tone.Audio.Dsp;
using DotCharter.Tone.Audio.Dsp.RsToneConversion;
using DotCharter.Tone.Audio.Dsp.Scenes;

namespace DotCharter.Tests.Audio.Dsp.Scenes;

/// <summary>#401: a scene's stored values follow the PEDAL they were stored for
/// through a reorder, a remove and an add, not the type-and-occurrence slot.</summary>
public sealed class RigSceneIdentityTests
{
    private static DcRigModule Delay(float mix) =>
        new("delay", new Dictionary<string, float> { ["mix"] = mix });

    private static RigSceneSet SceneBStoredAs(params float[] mixes)
    {
        var slots = Enumerable.Repeat(RigScene.Empty, RigSceneSet.Count).ToArray();
        slots[1] = new RigScene(null, RigScenes.Capture(mixes.Select(Delay).ToList()));
        return new RigSceneSet(slots, 0);
    }

    private static float MixOf(IReadOnlyList<DcRigModule> modules, int i) => modules[i].Params["mix"];

    [Fact]
    public void Reorder_KeepsEachPedalsOwnStoredValues()
    {
        var set = SceneBStoredAs(0.2f, 0.8f);
        RigSceneSlot[] before = [new(1, "delay"), new(2, "delay")];
        RigSceneSlot[] after = [new(2, "delay"), new(1, "delay")];

        var reconciled = RigScenes.Reconcile(set, before, after, [Delay(0.5f), Delay(0.5f)])!;
        var projected = RigScenes.Project([Delay(0.5f), Delay(0.5f)], reconciled.Slots[1]);

        Assert.Equal(0.8f, MixOf(projected, 0)); // D2 is first now and keeps ITS 0.8
        Assert.Equal(0.2f, MixOf(projected, 1));
    }

    [Fact]
    public void Remove_LeavesTheSurvivorItsOwnValues()
    {
        var set = SceneBStoredAs(0.2f, 0.8f);
        RigSceneSlot[] before = [new(1, "delay"), new(2, "delay")];
        RigSceneSlot[] after = [new(2, "delay")];

        var reconciled = RigScenes.Reconcile(set, before, after, [Delay(0.5f)])!;
        var projected = RigScenes.Project([Delay(0.5f)], reconciled.Slots[1]);

        Assert.Equal(0.8f, MixOf(projected, 0));
    }

    [Fact]
    public void AddedPedal_TakesItsLiveValues_AndShiftsNoOneElse()
    {
        var set = SceneBStoredAs(0.2f, 0.8f);
        RigSceneSlot[] before = [new(1, "delay"), new(2, "delay")];
        RigSceneSlot[] after = [new(3, "delay"), new(1, "delay"), new(2, "delay")];

        var reconciled = RigScenes.Reconcile(set, before, after, [Delay(0.5f), Delay(0.1f), Delay(0.1f)])!;
        var projected = RigScenes.Project([Delay(0.5f), Delay(0.1f), Delay(0.1f)], reconciled.Slots[1]);

        Assert.Equal(0.5f, MixOf(projected, 0));
        Assert.Equal(0.2f, MixOf(projected, 1));
        Assert.Equal(0.8f, MixOf(projected, 2));
    }

    [Fact]
    public void ARigWithoutScenes_StaysWithout()
        => Assert.Null(RigScenes.Reconcile(null, [], [], []));

    [Fact]
    public void ThePedalboardMove_RekeysTheChainsScenes()
    {
        var dsp = new FakeDspChainService([
            new DspModuleType("delay", "Delay", [new DspParamInfo("mix", "Mix", 0f, 1f, 0.3f)])]);
        var rig = TestRigBuilders.Pedalboard(dsp, new FakeDspTonePresetService(), FakeLocalization.English());
        var delay = rig.AvailableModules.Single();
        var d1 = rig.AddGear(delay)!;
        var d2 = rig.AddGear(delay)!;
        dsp.SetParam(d1.NodeId, "mix", 0.2f);
        dsp.SetParam(d2.NodeId, "mix", 0.8f);
        dsp.Scenes = SceneBStoredAs(0.2f, 0.8f);

        rig.MoveUpCommand.Execute(d2);

        var live = DcRigSnapshot.Modules(dsp.GetNodes());
        var projected = RigScenes.Project(live, dsp.Scenes!.Slots[1]);
        Assert.Equal(d2.NodeId, dsp.GetNodes()[0].NodeId);
        Assert.Equal(0.8f, MixOf(projected, 0));
        Assert.Equal(0.2f, MixOf(projected, 1));
    }
}
