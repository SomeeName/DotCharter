using System.Text.Json;
using DotCharter.Tone.Audio.Dsp;
using DotCharter.Tone.Audio.Dsp.RsToneConversion;
using DotCharter.Tone.UI.ViewModels.PlayMode;

namespace DotCharter.Tests.UI;

public sealed class RigLaneMixerTests
{
    private static IReadOnlyList<DspModuleType> Catalog(bool switches = true)
    {
        var parameters = Enumerable.Range(1, 3).SelectMany(i => new[]
        {
            new DspParamInfo("level" + i, "Level", 0, 2, 1),
            new DspParamInfo("pan" + i, "Pan", -1, 1, 0)
        }).ToList();
        if (switches)
        {
            parameters.AddRange(Enumerable.Range(1, 3).SelectMany(i => new[]
            {
                new DspParamInfo("mute" + i, "Mute", 0, 1, 0),
                new DspParamInfo("solo" + i, "Solo", 0, 1, 0)
            }));
        }

        parameters.Add(new DspParamInfo("mode", "Mode", 0, 1, 0));
        return [new("splitter", "Splitter", [new("enabledBranches", "Branches", 2, 3, 2)]),
            new("merger", "Merger", parameters), new("gain", "Gain", [new("gainDb", "Gain", -60, 24, 0)])];
    }

    private static (FakeDspChainService Dsp, PedalboardViewModel Rig, AmpFxRigViewModel Screen) Create(bool switches = true)
    {
        var dsp = new FakeDspChainService(Catalog(switches));
        var rig = TestRigBuilders.Pedalboard(dsp, new FakeDspTonePresetService(), FakeLocalization.English());
        var screen = new AmpFxRigViewModel(rig);
        rig.AddGear(rig.AvailableModules.First(m => m.Id == "splitter"));
        return (dsp, rig, screen);
    }
    private static RigSplitSegment Split(AmpFxRigViewModel screen) => Assert.Single(screen.BoardSegments.OfType<RigSplitSegment>());

    [Fact]
    public void LaneControls_WriteTheirOwnMergerParams_WithoutChangingLevelsOrBypasses()
    {
        var (dsp, _, screen) = Create();
        var split = Split(screen);
        var lane = split.Lanes[1].Mixer;
        Assert.True(lane.CanMuteSolo);
        lane.Level!.Value = .37f;
        lane.Pan!.Value = -.4f;
        var before = dsp.BypassSets.Count;
        lane.IsMuted = true;
        lane.IsSoloed = true;
        Assert.Equal(1f, dsp.GetParam(split.Merger!.NodeId, "mute2"));
        Assert.Equal(1f, dsp.GetParam(split.Merger.NodeId, "solo2"));
        Assert.Equal(0f, dsp.GetParam(split.Merger.NodeId, "mute1"));
        Assert.Equal(.37f, dsp.GetParam(split.Merger.NodeId, "level2"));
        lane.IsMuted = false;
        lane.IsSoloed = false;
        Assert.Equal(.37f, lane.Level.Value);
        Assert.Equal(-.4f, lane.Pan.Value);
        Assert.Equal(before, dsp.BypassSets.Count);
    }

    [Fact]
    public void PresetMirror_UpdatesBothHosts_WithoutReplacingLaneOrEchoingWrites()
    {
        var (dsp, rig, screen) = Create();
        var second = new AmpFxRigViewModel(rig);
        var lane = Split(screen).Lanes[0];
        var notifications = new List<string?>();
        lane.Mixer.PropertyChanged += (_, e) => notifications.Add(e.PropertyName);
        dsp.SetParam(Split(screen).Merger!.NodeId, "mute1", 1);
        dsp.SetParam(Split(screen).Merger!.NodeId, "solo1", 1);
        var writes = dsp.ParamSets.Count;
        rig.Sync();
        Assert.Same(lane, Split(screen).Lanes[0]);
        Assert.True(lane.Mixer.IsMuted);
        Assert.True(Split(second).Lanes[0].Mixer.IsSoloed);
        Assert.Contains(nameof(RigLaneMixerViewModel.IsMuted), notifications);
        Assert.Equal(writes, dsp.ParamSets.Count);
    }

    [Fact]
    public void SerializedPresetAndEngineRebuild_RestoreSwitchesAndOriginalLevel_OnNewNodes()
    {
        var (dsp, rig, screen) = Create();
        var old = Split(screen).Lanes[0].Mixer;
        old.Level!.Value = .42f;
        old.IsMuted = true;
        old.IsSoloed = true;
        var preset = new DcRigPreset(dsp.GetNodes().Select(n => new DcRigModule(n.ModuleTypeId,
            new Dictionary<string, float>(n.Values), n.Bypassed)
        { Branch = n.Branch }).ToArray());
        var restored = JsonSerializer.Deserialize<DcRigPreset>(JsonSerializer.Serialize(preset))!;
        dsp.AttachEngine(91);
        new DcRigApplier(dsp).Apply(restored);
        rig.ResyncAll();
        var current = Split(screen).Lanes[0].Mixer;
        Assert.NotSame(old, current);
        Assert.False(old.CanMuteSolo);
        Assert.True(current.IsMuted);
        Assert.True(current.IsSoloed);
        current.IsMuted = false;
        current.IsSoloed = false;
        Assert.Equal(.42f, dsp.GetParam(Split(screen).Merger!.NodeId, "level1"));
    }

    [Fact]
    public void ImplicitMerge_DisablesMixer_AndDetachedMixerCannotWrite()
    {
        var (dsp, _, screen) = Create();
        var split = Split(screen);
        var mixer = split.Lanes[0].Mixer;
        split.Merger!.Bypassed = true;
        Assert.Same(mixer, Split(screen).Lanes[0].Mixer);
        Assert.False(mixer.HasExplicitMerger);
        Assert.False(mixer.CanMix);
        var writes = dsp.ParamSets.Count;
        mixer.IsMuted = true;
        mixer.IsSoloed = true;
        Assert.Equal(writes, dsp.ParamSets.Count);
    }

    [Fact]
    public void OlderEngine_ExposesGainAndPan_ButNeverPretendsToMute()
    {
        var (dsp, _, screen) = Create(switches: false);
        var mixer = Split(screen).Lanes[0].Mixer;
        Assert.True(mixer.CanMix);
        Assert.False(mixer.CanMuteSolo);
        Assert.True(mixer.RequiresUpdatedEngine);
        var writes = dsp.ParamSets.Count;
        mixer.IsMuted = true;
        Assert.False(mixer.IsMuted);
        Assert.Equal(writes, dsp.ParamSets.Count);
    }
}
