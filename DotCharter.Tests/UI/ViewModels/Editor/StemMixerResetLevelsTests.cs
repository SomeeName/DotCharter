using DotCharter.UI.ViewModels.Editor;

namespace DotCharter.Tests.UI.ViewModels.Editor;

/// <summary>
///     Design D's "Reset levels" question under the Levels card is about the project on screen: a project switch
///     withdraws it, and a released mixer no longer listens to the project state.
/// </summary>
public sealed class StemMixerResetLevelsTests
{
    [Fact]
    public void AProjectSwitch_WithdrawsTheResetQuestion_UntilTheMixerIsReleased()
    {
        var state = new ProjectState { ActiveProject = new ChartProject() };
        var vm = new StemMixerViewModel(state, new FakeAudioPlaybackService(), FakeLocalization.KeysOnly(), ui: new GatedInlineDispatcher());

        vm.ResetLevelsCommand.Execute(null);
        Assert.True(vm.ResetLevelsConfirm.IsPending);
        state.ActiveProject = new ChartProject();
        Assert.False(vm.ResetLevelsConfirm.IsPending);

        vm.Dispose();
        vm.ResetLevelsConfirm.Ask("still here", () => { });
        state.ActiveProject = new ChartProject();
        Assert.True(vm.ResetLevelsConfirm.IsPending); // the released mixer's question no longer follows the state
    }
}
