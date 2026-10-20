using DotCharter.Tone.Audio.Dsp;
using DotCharter.Tone.Audio.Dsp.Presets;
using DotCharter.Tone.Audio.Dsp.RsToneConversion;
using DotCharter.Tone.UI.ViewModels.PlayMode;

namespace DotCharter.Tests.UI;

/// <summary>
///     The rig's SCENES (A–H) through the rig surface, over a fake chain with the
///     REAL preset service, asserted against the CHAIN: a switch writes params and
///     bypass on the nodes that exist and never adds or removes one, edits go into
///     the scene that is playing, a switch is one undo step, scenes save with a
///     tone and come back with it, and a scene switch is not an edit. The data
///     rules are <c>Audio/Dsp/Scenes/RigSceneModelTests</c>; the bar and the keys
///     are <c>Perf/RigSceneBarUiPerfSmokeTests</c>.
/// </summary>
public sealed class RigScenesTests
{
    // Module ids the REAL target catalogue does not know, on purpose: the preset
    // pipeline fills a KNOWN module's missing params from that catalogue, which the
    // fake chain (with its two-param modules) would then carry as extra values —
    // an artefact no real engine has, whose nodes always report every param.
    private static readonly DspModuleType Drive = new("scenedrive", "Overdrive", [new("drive", "Drive", 0, 1, 0.5f), new("level", "Level", 0, 1, 0.5f)]);
    private static readonly DspModuleType Delay = new("scenedelay", "Delay", [new("mix", "Mix", 0, 1, 0.3f)]);

    private sealed class Clock
    {
        public long Now = 1_000_000;
        public void Pause() => Now += 10_000;
    }

    private static (PedalboardViewModel Rig, FakeDspChainService Chain, Clock Clock) NewRig()
    {
        var chain = new FakeDspChainService(new[] { Drive, Delay });
        var rig = TestRigBuilders.Pedalboard(chain, new DspTonePresetService(chain, new FakeConfigurationService()), FakeLocalization.English());
        var clock = new Clock();
        rig.HistoryClock = () => clock.Now;
        rig.AddGear(Drive);
        rig.AddGear(Delay);
        clock.Pause();
        return (rig, chain, clock);
    }

    private static float DriveOf(FakeDspChainService chain) => chain.GetNodes()[0].Values["drive"];
    private static bool DelayOff(FakeDspChainService chain) => chain.GetNodes()[1].Bypassed;

    private static void SetDrive(PedalboardViewModel rig, float value) =>
        rig.Nodes[0].Params.Single(p => p.Id == "drive").Value = value;

    [Fact]
    public void ASceneSwitch_WritesParamsAndBypass_AndNeverAddsOrRemovesAUnit()
    {
        var (rig, chain, clock) = NewRig();
        using var _ = rig;
        rig.SelectSceneAt(1);                       // B: a fork of A
        SetDrive(rig, 0.9f);
        rig.Nodes[1].Bypassed = true;
        clock.Pause();
        var ids = chain.GetNodes().Select(n => n.NodeId).ToList();
        var adds = chain.Adds.Count;
        var removes = chain.Removes.Count;

        rig.SelectSceneAt(0);                       // back to A

        Assert.Equal(0.5f, DriveOf(chain), 3);
        Assert.False(DelayOff(chain));
        Assert.Equal(ids, chain.GetNodes().Select(n => n.NodeId)); // the SAME nodes: tails ring on
        Assert.Equal(adds, chain.Adds.Count);
        Assert.Equal(removes, chain.Removes.Count);
        // and the board mirrors it without rebuilding a card
        Assert.Equal(0.5f, rig.Nodes[0].Params.Single(p => p.Id == "drive").Value, 3);
        Assert.Equal(0, rig.ActiveSceneIndex);
        Assert.True(rig.SceneSlots[0].IsActive);
        Assert.False(rig.SceneSlots[1].IsActive);
    }

    [Fact]
    public void EditsGoIntoTheScenePlaying_SoSwitchingAwayAndBackLosesNothing()
    {
        var (rig, chain, clock) = NewRig();
        using var _ = rig;
        rig.SelectSceneAt(2);
        SetDrive(rig, 0.8f);                        // edited on C, never "stored"
        clock.Pause();
        rig.SelectSceneAt(0);
        SetDrive(rig, 0.1f);                        // edited on A
        clock.Pause();

        rig.SelectSceneAt(2);
        Assert.Equal(0.8f, DriveOf(chain), 3);
        rig.SelectSceneAt(0);
        Assert.Equal(0.1f, DriveOf(chain), 3);
    }

    [Fact]
    public void ASceneSwitch_IsOneUndoStep_EvenRightAfterAKnobDrag()
    {
        var (rig, chain, clock) = NewRig();
        using var _ = rig;
        rig.SelectSceneAt(1);
        SetDrive(rig, 0.9f);
        clock.Pause();
        rig.SelectSceneAt(0);
        clock.Pause();
        SetDrive(rig, 0.2f);                        // a drag on A…
        rig.SelectSceneAt(1);                       // …and a switch INSIDE its burst window

        Assert.Equal(1, rig.ActiveSceneIndex);
        rig.UndoCommand.Execute(null);              // the switch alone
        Assert.Equal(0, rig.ActiveSceneIndex);
        Assert.Equal(0.2f, DriveOf(chain), 3);
        rig.UndoCommand.Execute(null);              // then the drag
        Assert.Equal(0.5f, DriveOf(chain), 3);
        rig.RedoCommand.Execute(null);
        rig.RedoCommand.Execute(null);
        Assert.Equal(1, rig.ActiveSceneIndex);
        Assert.Equal(0.9f, DriveOf(chain), 3);
    }

    [Fact]
    public void RenameStoreAndClear_ActOnTheScenes_EachOneUndoStep()
    {
        var (rig, chain, clock) = NewRig();
        using var _ = rig;
        var c = rig.SceneSlots[2];
        Assert.False(c.IsStored);

        c.BeginRenameCommand.Execute(null);
        c.EditName = "Solo";
        c.CommitRenameCommand.Execute(null);
        Assert.Equal("Solo", c.Name);
        clock.Pause();

        SetDrive(rig, 0.7f);
        clock.Pause();
        c.StoreCommand.Execute(null);               // "store current sound here"
        Assert.True(c.IsStored);
        Assert.Equal(0.7f, chain.Scenes!.Slots[2].Modules![0].Params["drive"], 3);
        Assert.False(rig.SceneSlots[0].ClearCommand.CanExecute(null)); // never the one playing

        c.ClearCommand.Execute(null);
        Assert.False(c.IsStored);
        Assert.Null(c.Name);

        rig.UndoCommand.Execute(null);              // the clear
        Assert.True(rig.SceneSlots[2].IsStored);
        Assert.Equal("Solo", rig.SceneSlots[2].Name);
    }

    [Fact]
    public void ScenesSaveWithAUserTone_LeaveWithTheNextTone_AndComeBackWithTheirs()
    {
        var (rig, chain, clock) = NewRig();
        using var _ = rig;
        rig.SelectSceneAt(1);
        SetDrive(rig, 0.9f);
        rig.RenameScene(1, "Lead");
        clock.Pause();
        rig.Browser.SaveName = "Two scenes";
        rig.Browser.ConfirmSave();
        var saved = rig.Browser.ActivePreset!;
        Assert.NotNull(saved.Rig.Scenes);

        // another tone: its (absent) scenes replace these
        rig.Browser.ApplyPreset(new DspTonePreset("plain", "Plain", DspToneCategory.Clean, "",
            new DcRigPreset([new DcRigModule("scenedrive", new Dictionary<string, float> { ["drive"] = 0.3f })]), DspToneOrigin.User));
        Assert.Null(chain.Scenes);
        Assert.Equal(0, rig.ActiveSceneIndex);
        Assert.Null(rig.SceneSlots[1].Name);

        rig.Browser.ApplyPreset(saved);
        Assert.Equal(1, rig.ActiveSceneIndex);
        Assert.Equal("Lead", rig.SceneSlots[1].Name);
        rig.SelectSceneAt(0);
        Assert.Equal(0.5f, DriveOf(chain), 3);
    }

    [Fact]
    public void SwitchingScenes_IsNotAnEdit_ButAKnobMovedInAnyScene_Is()
    {
        var (rig, _, clock) = NewRig();
        using var _ = rig;
        rig.SelectSceneAt(1);
        SetDrive(rig, 0.9f);
        clock.Pause();
        rig.Browser.SaveName = "Scenes";
        rig.Browser.ConfirmSave();
        var applied = rig.Browser.ActivePreset!.Rig;

        Assert.False(RigEditState.IsEdited(rig.BuildCurrentRig(), applied));
        rig.SelectSceneAt(0);
        Assert.False(RigEditState.IsEdited(rig.BuildCurrentRig(), applied), "switching scene read as an edit");
        SetDrive(rig, 0.2f);
        Assert.True(RigEditState.IsEdited(rig.BuildCurrentRig(), applied), "a knob moved in scene A did not");
    }

    [Fact]
    public void ATone_WithoutScenes_BehavesAsBefore_SceneAPlaying_NothingEdited()
    {
        var (rig, chain, _) = NewRig();
        using var _ = rig;
        var plain = rig.BuildCurrentRig();

        Assert.Null(plain.Scenes);
        Assert.Null(chain.Scenes);
        Assert.True(rig.SceneSlots[0].IsActive);
        Assert.All(rig.SceneSlots.Skip(1), s => Assert.False(s.IsStored));
        rig.SelectSceneAt(0);                       // A of a scene-less rig: nothing happens
        Assert.Null(chain.Scenes);
        Assert.False(RigEditState.IsEdited(rig.BuildCurrentRig(), plain));
    }

    [Fact]
    public void ASceneSwitch_TurnsTheMasterBypassOff()
    {
        var (rig, chain, _) = NewRig();
        using var _ = rig;
        rig.SelectSceneAt(1);
        rig.ToggleMasterBypassCommand.Execute(null);
        Assert.True(chain.GetNodes().All(n => n.Bypassed));

        rig.SelectSceneAt(0);

        Assert.False(rig.IsMasterBypassed);
        Assert.DoesNotContain(chain.GetNodes(), n => n.Bypassed);
    }

    [Fact]
    public void AnEngineRebuild_DropsTheOldChainsScenes_AndTheBoardFollows()
    {
        var (rig, chain, _) = NewRig();
        using var _ = rig;
        chain.AttachEngine(0x10);                   // the first engine: an empty chain
        rig.AddGear(Drive);
        rig.SelectSceneAt(3);
        Assert.Equal(3, rig.ActiveSceneIndex);

        chain.AttachEngine(0x20);                   // a new engine: empty chain, no scenes

        Assert.Null(chain.Scenes);
        Assert.Equal(0, rig.ActiveSceneIndex);
    }
}
