using DotCharter.Tone.Audio.Dsp;

namespace DotCharter.Tests.UI;

/// <summary>
///     The tone rig follows the PROFILE (<c>ProfileToneRigCoordinator</c>
///     reloads every seat's chain on <c>ActiveProfileChanged</c>), but the
///     BOARD on screen listened to nothing — so a profile switch from the
///     shell's popover left the old player's pedals drawn over the new
///     player's chain, the same "applied, but the board did not move" class
///     the onboarding wizard hit in 0.49. The board rebuilds from the chain
///     after every switch, POSTED so it runs after the coordinator's reload
///     whatever the subscription order.
/// </summary>
public sealed class PedalboardProfileResyncTests
{
    [Fact]
    public void AProfileSwitch_RebuildsTheBoardFromTheChain()
    {
        var catalog = new[] { new DspModuleType("amp", "amp", new[] { new DspParamInfo("p", "p", 0f, 1f, 0f) }) };
        var chain = new FakeDspChainService(catalog);
        var profiles = new OnboardingWizardRig.StubProfiles();
        var board = TestRigBuilders.Pedalboard(chain, new FakeDspTonePresetService(), FakeLocalization.English(),
            subscribeProfileSwitch: resync => profiles.ActiveProfileChanged += _ => resync());
        Assert.Empty(board.Nodes);

        // What the coordinator does behind the board's back on a switch.
        chain.AddNode("amp");
        Assert.Empty(board.Nodes);

        var other = profiles.Create("Ana");
        Assert.True(profiles.SwitchTo(other.Id));

        Assert.Equal(["amp"], board.Nodes.Select(n => n.ModuleTypeId));
    }

    [Fact]
    public void WithoutAProfileService_TheBoardStillConstructs()
    {
        var catalog = new[] { new DspModuleType("amp", "amp", new[] { new DspParamInfo("p", "p", 0f, 1f, 0f) }) };
        var board = TestRigBuilders.Pedalboard(new FakeDspChainService(catalog), new FakeDspTonePresetService(), FakeLocalization.English());
        Assert.Empty(board.Nodes);
    }
}
