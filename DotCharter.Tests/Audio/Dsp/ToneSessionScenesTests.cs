using DotCharter.Tone.Audio.Dsp;
using DotCharter.Tone.Audio.Dsp.Presets;
using DotCharter.Tone.Audio.Dsp.RsToneConversion;
using DotCharter.Tone.Audio.Dsp.Scenes;
using DotCharter.Tone.Infrastructure.Storage;

namespace DotCharter.Tests.Audio.Dsp;

/// <summary>
///     The live rig's SCENES outlive the session and every engine rebuild: they
///     are managed state beside the chain (<see cref="IDspChainService.Scenes" />),
///     so <see cref="ToneSessionService" /> has to persist them with the modules,
///     notice a change that touched only them, and put them back on the restore.
///     A scene switch is params and bypass, so the chain replay covers the SOUND;
///     this is what covers the scenes themselves.
/// </summary>
public sealed class ToneSessionScenesTests : IDisposable
{
    private const nint EngineA = 0x1000;
    private const nint EngineB = 0x2000;

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "DotCharterTests", Guid.NewGuid().ToString("N"));

    public void Dispose() => BestEffortDelete.Directory(_dir);

    private ProfileToneRigStore NewStore() => new(_dir);

    [Fact]
    public void TheScenes_ArePersistedWithTheLiveRig_AndComeBackOnAnEngineRebuild()
    {
        var chain = new FakeDspChainService();
        var config = new FakeConfigurationService();
        config.Update(c => c.DefaultTonePresetId = "");
        using var session = new ToneSessionService(chain, config, new DspTonePresetService(chain, config), NewStore());
        session.Start();
        chain.AttachEngine(EngineA);
        var node = chain.AddNode("overdrive")!;
        var live = DcRigSnapshot.FromChain(chain);
        chain.Scenes = RigScenes.Rename(RigScenes.Switch(live, 2)!.Scenes!, 2, "Lead");
        chain.SetParam(node.NodeId, "drive", 0.9f);  // an edit on C
        session.Flush();

        var stored = NewStore().GetRig(0)!.Scenes!.ToSet();
        Assert.Equal(2, stored.Active);
        Assert.Equal("Lead", stored.Slots[2].Name);
        Assert.Equal(0.9f, stored.Slots[2].Modules![0].Params["drive"], 3);

        chain.AttachEngine(EngineB);                  // a device change: empty chain, no scenes…

        Assert.Equal(2, chain.Scenes!.Active);        // …and the restore brings them back
        Assert.Equal("Lead", chain.Scenes.Slots[2].Name);
        Assert.Equal(0.9f, chain.GetNodes()[0].Values["drive"], 3);
    }

    [Fact]
    public void RenamingAScene_WithNoKnobMoved_IsStillAChangeWorthWriting()
    {
        var chain = new FakeDspChainService();
        var config = new FakeConfigurationService();
        config.Update(c => c.DefaultTonePresetId = "");
        using var session = new ToneSessionService(chain, config, new DspTonePresetService(chain, config), NewStore());
        session.Start();
        chain.AttachEngine(EngineA);
        chain.AddNode("overdrive");
        chain.Scenes = RigScenes.Switch(DcRigSnapshot.FromChain(chain), 1)!.Scenes;
        session.Flush();

        chain.Scenes = RigScenes.Rename(chain.Scenes!, 1, "Solo");
        session.Flush();

        Assert.Equal("Solo", NewStore().GetRig(0)!.Scenes!.ToSet().Slots[1].Name);
    }

    [Fact]
    public void ARigWithoutScenes_PersistsExactlyAsBefore()
    {
        var chain = new FakeDspChainService();
        var config = new FakeConfigurationService();
        config.Update(c => c.DefaultTonePresetId = "");
        using var session = new ToneSessionService(chain, config, new DspTonePresetService(chain, config), NewStore());
        session.Start();
        chain.AttachEngine(EngineA);
        chain.AddNode("overdrive");
        session.Flush();

        Assert.Null(NewStore().GetRig(0)!.Scenes);
        Assert.Equal(ToneSessionService.Signature(DcRigSnapshot.Modules(chain.GetNodes())),
            ToneSessionService.Signature(DcRigSnapshot.FromChain(chain)));
    }
}
