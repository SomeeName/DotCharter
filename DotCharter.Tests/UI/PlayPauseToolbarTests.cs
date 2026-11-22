using System.ComponentModel;
using DotCharter.UI.ViewModels.Editor;

namespace DotCharter.Tests.UI;

/// <summary>
///     #492: the toolbar's Play and Pause are ONE button. The view model owns what it
///     says: its accessible name and tooltip format follow <c>IsPlaying</c> (so a screen
///     reader announces the verb the press will perform), and both are announced as
///     changed so the bound button re-reads them.
/// </summary>
public sealed class PlayPauseToolbarTests
{
    private static TransportViewModel Transport() =>
        new(new FakeAudioPlaybackService(), new ProjectState(), new FakeConfigurationService(), FakeLocalization.KeysOnly(),
            new OnboardingWizardRig.InlineDispatcher());

    [Fact]
    public void WhilePaused_TheButtonOffersPlay()
    {
        var transport = Transport();
        Assert.False(transport.IsPlaying);
        Assert.Equal("Toolbar_Play", transport.PlayPauseLabel);
        Assert.Equal("Toolbar_Play_Hint", transport.PlayPauseTip);
    }

    [Fact]
    public void WhilePlaying_TheButtonOffersPause_AndTellsTheBindingToReadAgain()
    {
        var transport = Transport();
        var raised = new List<string?>();
        ((INotifyPropertyChanged)transport).PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        transport.IsPlaying = true;

        Assert.Equal("Toolbar_Pause", transport.PlayPauseLabel);
        Assert.Equal("Toolbar_Pause_Hint", transport.PlayPauseTip);
        Assert.Contains(nameof(TransportViewModel.PlayPauseLabel), raised);
        Assert.Contains(nameof(TransportViewModel.PlayPauseTip), raised);

        transport.IsPlaying = false;
        Assert.Equal("Toolbar_Play", transport.PlayPauseLabel);
    }
}
