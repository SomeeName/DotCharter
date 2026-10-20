using System.Text.Json;
using DotCharter.Tone.Audio.Dsp.Presets;
using DotCharter.Tone.Audio.Dsp.RsToneConversion;
using DotCharter.Tone.Audio.Dsp.Scenes;
using DotCharter.Tone.Audio.Midi;

namespace DotCharter.Tests.Audio.Dsp.Scenes;

/// <summary>
///     The rig's SCENES as data: the pure rules (<see cref="RigScenes" />), how a
///     rig carries them through the applier, the snapshot and the preparation
///     pipeline, their persisted shape (a tone saved before scenes reads back
///     unchanged), the tone-file import, and the MIDI decode/routing the scene CC
///     and program changes go through. The rig surface over them is
///     <c>UI/RigScenesTests</c>; the live-rig persistence is
///     <c>Audio/Dsp/ToneSessionScenesTests</c>.
/// </summary>
public sealed class RigSceneModelTests
{
    private static DcRigModule Module(string type, float drive, bool bypassed = false, string? path = null, int branch = 0) =>
        new(type, new Dictionary<string, float> { ["drive"] = drive, ["level"] = 0.5f }, bypassed)
        {
            StringParams = path is null ? null : new Dictionary<string, string> { ["irPath"] = path },
            Branch = branch
        };

    private static DcRigPreset Rig(params DcRigModule[] modules) => new(modules);

    // ── switching ────────────────────────────────────────────────────────────

    [Fact]
    public void SwitchingToAnUnusedScene_ForksWhatIsPlaying_AndChangesNoValue()
    {
        var live = Rig(Module("overdrive", 0.3f), Module("delay", 0.1f));

        var next = RigScenes.Switch(live, 2);

        Assert.NotNull(next);
        Assert.Same(live.Modules, next.Modules); // nothing to write: the sound does not change
        var set = next.Scenes!;
        Assert.Equal(2, set.Active);
        Assert.True(set.Slots[0].IsStored, "the scene being left keeps what was playing");
        Assert.True(set.Slots[2].IsStored, "the fork holds the live values");
        Assert.All(new[] { 1, 3, 4, 5, 6, 7 }, i => Assert.False(set.Slots[i].IsStored));
    }

    [Fact]
    public void SceneAOfARigWithoutScenes_IsNoSwitch_AndNeitherIsTheScenePlaying()
    {
        var live = Rig(Module("overdrive", 0.3f));
        Assert.Null(RigScenes.Switch(live, 0));

        var onB = RigScenes.Switch(live, 1)!;
        Assert.Null(RigScenes.Switch(onB, 1));
        Assert.Null(RigScenes.Switch(onB, 8));
        Assert.Null(RigScenes.Switch(onB, -1));
    }

    [Fact]
    public void SwitchingToAStoredScene_WritesItsValuesAndBypass_AndNeverTheStructure()
    {
        // scene B: the drive up and the delay off
        var a = Rig(Module("overdrive", 0.3f, path: "cab.wav", branch: 2), Module("delay", 0.1f));
        var onB = RigScenes.Switch(a, 1)!;
        var bLive = onB with { Modules = [Module("overdrive", 0.9f, path: "cab.wav", branch: 2), Module("delay", 0.1f, bypassed: true)] };
        var folded = bLive with { Scenes = RigScenes.Fold(bLive.Scenes, bLive.Modules) };

        var backOnA = RigScenes.Switch(folded, 0)!;

        Assert.Equal(0, backOnA.Scenes!.Active);
        Assert.Equal(["overdrive", "delay"], backOnA.Modules.Select(m => m.ModuleTypeId));
        Assert.Equal(0.3f, backOnA.Modules[0].Params["drive"]);
        Assert.False(backOnA.Modules[1].Bypassed);
        // the structure is the LIVE rig's, never the scene's
        Assert.Equal("cab.wav", backOnA.Modules[0].StringParams!["irPath"]);
        Assert.Equal(2, backOnA.Modules[0].Branch);

        // and B kept the edit made while it was playing
        var toB = RigScenes.Switch(backOnA, 1)!;
        Assert.Equal(0.9f, toB.Modules[0].Params["drive"]);
        Assert.True(toB.Modules[1].Bypassed);
    }

    [Fact]
    public void AScene_MatchesModulesByTypeAndOccurrence_SoAUnitAddedElsewhereTakesNothing()
    {
        var scene = new RigScene("Lead", RigScenes.Capture([Module("overdrive", 0.8f), Module("overdrive", 0.2f)]));
        // a delay was put in FRONT since the scene was stored
        var live = new[] { Module("delay", 0.6f), Module("overdrive", 0.5f), Module("overdrive", 0.5f) };

        var projected = RigScenes.Project(live, scene);

        Assert.Equal(0.6f, projected[0].Params["drive"]); // the delay keeps its own
        Assert.Equal(0.8f, projected[1].Params["drive"]); // first overdrive ← first
        Assert.Equal(0.2f, projected[2].Params["drive"]); // second ← second
    }

    [Fact]
    public void Fold_PutsTheLiveValuesIntoTheScenePlaying_AndASetThatSaysNothingMoreIsNoScenes()
    {
        var live = Rig(Module("overdrive", 0.4f));
        var onB = RigScenes.Switch(live, 1)!.Scenes!;

        var edited = new[] { Module("overdrive", 0.7f) };
        var folded = RigScenes.Fold(onB, edited)!;
        Assert.Equal(0.7f, folded.Slots[1].Modules![0].Params["drive"]);
        Assert.Equal(0.4f, folded.Slots[0].Modules![0].Params["drive"]);

        // every other scene cleared: exactly a rig without scenes again
        Assert.Null(RigScenes.Fold(RigScenes.Clear(folded, 0), edited));
        // …unless one is NAMED — a name is worth keeping
        Assert.NotNull(RigScenes.Fold(RigScenes.Rename(RigScenes.Clear(folded, 0), 1, "Solo"), edited));
    }

    [Fact]
    public void StoreRenameAndClear_FollowTheRules()
    {
        var set = RigScenes.Start([Module("overdrive", 0.4f)]);

        set = RigScenes.Store(set, 3, [Module("overdrive", 0.9f)]);
        Assert.Equal(0.9f, set.Slots[3].Modules![0].Params["drive"]);

        set = RigScenes.Rename(set, 3, "   A really long scene name that goes on   ");
        Assert.Equal(RigSceneSet.MaxNameLength, set.Slots[3].Name!.Length);
        Assert.Null(RigScenes.Rename(set, 3, "  ").Slots[3].Name);

        Assert.Same(set, RigScenes.Clear(set, set.Active)); // never the one playing
        Assert.False(RigScenes.Clear(set, 3).Slots[3].IsStored);
    }

    [Fact]
    public void StartOn_ProjectsTheScene_AndMarksItPlaying_OrLeavesTheRigAlone()
    {
        var live = Rig(Module("overdrive", 0.2f));
        var saved = live with { Scenes = RigScenes.Store(RigScenes.Start(live.Modules), 1, [Module("overdrive", 0.95f)]) };

        var onB = RigScenes.StartOn(saved, 1);
        Assert.Equal(1, onB.Scenes!.Active);
        Assert.Equal(0.95f, onB.Modules[0].Params["drive"]);

        Assert.Same(saved, RigScenes.StartOn(saved, 5));                 // unused
        var plain = Rig(Module("overdrive", 0.2f));
        Assert.Same(plain, RigScenes.StartOn(plain, 1));                 // no scenes
    }

    [Fact]
    public void SameScenes_ComparesNamesValuesAndThePlayingSlot_UnlessToldToIgnoreIt()
    {
        var set = RigScenes.Start([Module("overdrive", 0.4f)]);
        var onC = set with { Active = 2 };
        Assert.False(RigScenes.SameScenes(set, onC));
        Assert.True(RigScenes.SameScenes(set, onC, ignoreActive: true));
        Assert.False(RigScenes.SameScenes(set, RigScenes.Rename(set, 0, "Clean"), ignoreActive: true));
        Assert.True(RigScenes.SameScenes(set, RigScenes.Store(set, 0, [Module("overdrive", 0.40001f)])));
        Assert.False(RigScenes.SameScenes(set, RigScenes.Store(set, 0, [Module("overdrive", 0.5f)])));
        Assert.True(RigScenes.SameScenes(null, null));
        Assert.False(RigScenes.SameScenes(set, null));
    }

    // ── the chain, the snapshot and the pipeline carry them ─────────────────

    [Fact]
    public void TheApplier_PutsARigsScenesOnTheChain_AndARigWithoutScenesClearsThem()
    {
        var chain = new FakeDspChainService();
        var applier = new DcRigApplier(chain);
        var withScenes = RigScenes.Switch(Rig(Module("overdrive", 0.4f)), 1)!;

        applier.Apply(withScenes);
        Assert.Same(withScenes.Scenes, chain.Scenes);

        applier.Apply(Rig(Module("delay", 0.4f)));
        Assert.Null(chain.Scenes); // another tone's scenes never land on this one
    }

    [Fact]
    public void TheSnapshot_CarriesTheChainsScenes_WithTheLiveValuesInTheScenePlaying()
    {
        var chain = new FakeDspChainService();
        var node = chain.AddNode("overdrive")!;
        chain.Scenes = RigScenes.Switch(DcRigSnapshot.FromNodes(chain.GetNodes()), 1)!.Scenes;
        chain.SetParam(node.NodeId, "drive", 0.95f);

        var rig = DcRigSnapshot.FromChain(chain);

        Assert.Equal(1, rig.Scenes!.Active);
        Assert.Equal(0.95f, rig.Scenes.Slots[1].Modules![0].Params["drive"]);
        Assert.Equal(0.5f, rig.Scenes.Slots[0].Modules![0].Params["drive"]);
    }

    [Fact]
    public void ThePreparationPipeline_KeepsAUserTonesScenes()
    {
        var rig = RigScenes.Switch(new DcRigPreset([new DcRigModule("overdrive", new Dictionary<string, float>())]), 1)!;
        var prepared = DcRigPresetPreparation.Prepare(
            new DspTonePreset("t", "T", DspToneCategory.Clean, "", rig, DspToneOrigin.User), namCaptureDirectory: "C:\\nam");

        Assert.NotEmpty(prepared.Modules[0].Params); // defaults were filled…
        Assert.Same(rig.Scenes, prepared.Scenes);    // …and the scenes survived it
    }

    // ── persisted ────────────────────────────────────────────────────────────

    [Fact]
    public void AToneSavedBeforeScenes_ReadsBackWithoutScenes_AndWritesNoScenesKey()
    {
        const string old = """{"Id":"u1","Name":"Old","Category":0,"Description":"","Modules":[{"ModuleTypeId":"overdrive","Params":{"drive":0.4},"Bypassed":false}]}""";
        var dto = JsonSerializer.Deserialize<UserTonePresetDto>(old)!;

        var preset = dto.ToPreset();
        Assert.Null(preset.Rig.Scenes);
        Assert.DoesNotContain("Scenes", JsonSerializer.Serialize(UserTonePresetDto.FromPreset(preset)));
    }

    [Fact]
    public void Scenes_RoundTripThroughTheSavedShape_NamesValuesAndThePlayingOne()
    {
        var rig = RigScenes.Switch(Rig(Module("overdrive", 0.3f), Module("delay", 0.1f)), 2)!;
        rig = rig with { Scenes = RigScenes.Rename(RigScenes.Store(rig.Scenes!, 5, [Module("overdrive", 0.8f), Module("delay", 0.2f, bypassed: true)]), 5, "Solo") };
        var json = JsonSerializer.Serialize(UserTonePresetDto.FromPreset(
            new DspTonePreset("u", "U", DspToneCategory.Lead, "", rig, DspToneOrigin.User)));

        var back = JsonSerializer.Deserialize<UserTonePresetDto>(json)!.ToPreset().Rig.Scenes!;

        Assert.True(RigScenes.SameScenes(rig.Scenes, back));
        Assert.Equal(2, back.Active);
        Assert.Equal("Solo", back.Slots[5].Name);
        Assert.True(back.Slots[5].Modules![1].Bypassed);
        Assert.False(back.Slots[6].IsStored);
    }

    [Fact]
    public void AHandEditedSceneSet_IsNormalized_EightSlotsAndAPlayingOneInRange()
    {
        var dto = new RigSceneSetDto { Active = 42, Slots = [new RigSceneDto { Name = "  x  " }, null] };
        var set = dto.ToSet();
        Assert.Equal(RigSceneSet.Count, set.Slots.Count);
        Assert.Equal(RigSceneSet.Count - 1, set.Active);
        Assert.Equal("x", set.Slots[0].Name);
    }

    [Fact]
    public void AnImportedTonesScenes_AreClampedIntoRange_AndUnknownModulesDropped()
    {
        Assert.True(DcRigTargetCatalog.TryGetParam("overdrive", "drive", out var drive));
        var json = $$$"""
            {"Id":"x","Name":"Shared","Modules":[{"ModuleTypeId":"overdrive","Params":{"drive":0.5}}],
             "Scenes":{"Active":1,"Slots":[null,{"Name":"Hot","Modules":[
                 {"ModuleTypeId":"overdrive","Params":{"drive":{{{drive.Max * 10}}}}},
                 {"ModuleTypeId":"no-such-pedal","Params":{"x":1}}]}]}}
            """;

        var import = TonePresetJsonFile.Parse(json);

        Assert.Equal(TonePresetImportFault.None, import.Fault);
        var scene = import.Preset!.Rig.Scenes!.Slots[1];
        Assert.Equal("Hot", scene.Name);
        var only = Assert.Single(scene.Modules!);
        Assert.Equal(drive.Max, only.Params["drive"]);
    }

    // ── MIDI ─────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(new byte[] { 0xC0, 5 }, true, 0, 5)]
    [InlineData(new byte[] { 0xCF, 127 }, true, 15, 127)]
    [InlineData(new byte[] { 0xC0, 128 }, false, 0, 0)]
    [InlineData(new byte[] { 0xB0, 69, 2 }, false, 0, 0)]   // a CC is not a program change
    [InlineData(new byte[] { 0xC0 }, false, 0, 0)]
    public void AProgramChange_IsTwoBytes_0xCn(byte[] bytes, bool ok, int channel, int program)
    {
        Assert.Equal(ok, RigMidiProgramMessage.TryDecode(bytes, out var ch, out var pc));
        Assert.Equal(channel, ch);
        Assert.Equal(program, pc);
        // …and the CC decoder, which is untouched, still refuses it
        Assert.False(ok && RigMidiControlMessage.TryDecode(bytes, out _, out _, out _));
    }

    [Fact]
    public void TheRouting_FiltersTheChannel_TheSceneCcAndItsValues()
    {
        var omni = RigMidiRouting.Default;
        Assert.True(omni.IsSceneSelect(9, 69));
        Assert.False(omni.IsSceneSelect(0, 70));
        Assert.True(omni.AcceptsProgram(15));

        var ch2 = new RigMidiRouting(2, false, -1);
        Assert.True(ch2.Listens(1) && !ch2.Listens(0));
        Assert.False(ch2.AcceptsProgram(1)); // program change off
        Assert.False(ch2.IsSceneSelect(1, 69)); // scene CC off

        Assert.Equal(7, RigMidiRouting.SceneFor(7));
        Assert.Null(RigMidiRouting.SceneFor(8));   // a pedal's 127 "on" never jumps to H
        Assert.Null(RigMidiRouting.SceneFor(127));

        var config = new FakeConfigurationService().LoadAppConfig();
        config.RigMidiChannel = 99;
        config.RigMidiSceneCc = 500;
        var clamped = RigMidiRouting.From(config);
        Assert.Equal(16, clamped.Channel);
        Assert.Equal(-1, clamped.SceneCc);
    }
}
