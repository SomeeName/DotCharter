using DotCharter.Tone.Audio.Dsp;

namespace DotCharter.Tests.Services.Audio.Dsp;

/// <summary>
///     SPILLOVER (ABI v37) is the player's setting, not the engine's: the engine
///     starts with it OFF, so the chain service RECORDS the value and pushes it to
///     every engine it is attached to — the first one, and every rebuilt one (a
///     device change, the NAM 48 kHz request and a watchdog recovery each hand the
///     chain a fresh engine). These pin the push, the replay, the startup seed
///     from <c>RigConfig.RigSpillover</c> and the rig switch that writes both.
/// </summary>
public sealed class SpilloverSettingTests
{
    private sealed class SpillInterop : FakeDspNativeInterop, IDspSpilloverNativeInterop
    {
        public List<bool> Pushes { get; } = [];
        public bool SetSpillover(bool enabled) { Pushes.Add(enabled); return true; }
        public bool TryGetSpillover(out bool enabled) { enabled = Pushes.LastOrDefault(); return true; }
    }

    [Fact]
    public void TheChain_DefaultsOn_AndPushesNothingWithoutAnEngine()
    {
        var native = new SpillInterop { EngineHandle = nint.Zero };
        var svc = new JuceDspChainService(native);
        Assert.True(svc.Spillover);
        Assert.False(svc.SupportsSpillover);
        svc.Spillover = false;
        Assert.Empty(native.Pushes);
    }

    [Fact]
    public void TheSetting_IsPushedOnAttach_AndReplayedOnEveryRebuild()
    {
        var native = new SpillInterop { EngineHandle = nint.Zero };
        var svc = new JuceDspChainService(native)
        {
            Spillover = false // before any engine exists
        };

        svc.AttachEngine(111);
        Assert.Equal([false], native.Pushes);
        Assert.True(svc.SupportsSpillover);

        svc.Spillover = true; // live toggle
        Assert.Equal([false, true], native.Pushes);

        // A rebuild: detach, then a DIFFERENT (fresh, spillover-off) engine.
        svc.AttachEngine(0);
        svc.AttachEngine(222);
        Assert.Equal([false, true, true], native.Pushes);
        Assert.True(svc.SupportsSpillover);
    }

    [Fact]
    public void ToneSessionSetCreate_SeedsEverySeatFromTheConfig()
    {
        var config = new FakeConfigurationService();
        config.AppConfig.RigSpillover = false;
        var chains = new PlayerDspChains([new FakeDspChainService(), new FakeDspChainService { Player = 1 }]);
        using var sessions = ToneSessionSet.Create(chains, config, new FakeDspTonePresetService(),
            new ProfileToneRigStore(Path.Combine(Path.GetTempPath(), "dc-spill-" + Guid.NewGuid().ToString("N")),
                legacyConfig: null));
        Assert.All(chains.All, c => Assert.False(c.Spillover));
    }

    [Fact]
    public void TheRigSwitch_WritesTheConfigAndEverySeat()
    {
        var config = new FakeConfigurationService();
        var seat0 = new FakeDspChainService { SupportsSpillover = true };
        var seat1 = new FakeDspChainService { Player = 1, SupportsSpillover = true };
        var chains = new PlayerDspChains([seat0, seat1]);
        using var rig = TestRigBuilders.Pedalboard(seat0, new FakeDspTonePresetService(), FakeLocalization.English(), config, chains: chains);

        Assert.True(rig.Spillover);
        Assert.True(rig.CanToggleSpillover);
        rig.Spillover = false;

        Assert.False(config.AppConfig.RigSpillover);
        Assert.False(seat0.Spillover);
        Assert.False(seat1.Spillover);
    }

    [Fact]
    public void WithNoEngine_TheSwitchIsDisabled_AndSaysWhy()
    {
        using var rig = TestRigBuilders.Pedalboard(new FakeDspChainService(), new FakeDspTonePresetService(), FakeLocalization.English());
        Assert.False(rig.CanToggleSpillover);
        Assert.Equal(FakeLocalization.English()["Rig_Spillover_NoEngine"], rig.SpilloverTip);
    }
}
