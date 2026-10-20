using DotCharter.Services.Ai.Match.Model;
using DotCharter.Tone.Audio.Dsp.Presets;
using DotCharter.Tone.Audio.Dsp.RsToneConversion;
using DotCharter.UI.ViewModels.PlayMode;

namespace DotCharter.Tests.UI;

/// <summary>
///     The tone panel's one-click "Match model" offer: the half of the
///     download state machine that makes a missing model FIXABLE from where the
///     user noticed it. The settings block over the same downloader is
///     <c>Presentation.Settings.MatchModelViewModelTests</c>.
/// </summary>
public sealed class ToneDesignMatchOfferTests
{
    private static ToneDesignViewModel PanelVm(IRigEstimatorModelDownloadService? downloads)
    {
        var live = new DspTonePreset(
            "live", "Live", DspToneCategory.Clean, "",
            new DcRigPreset([new DcRigModule("amp", new Dictionary<string, float>())]),
            DspToneOrigin.User);

        var vm = new ToneDesignViewModel(
            () => null,
            _ => { },
            () => live,
            (name, category, _, rig) => live, FakeLocalization.English(),
            id => id);
        vm.ConfigureMatch(() => null, () => null, modelDownloads: () => downloads);
        return vm;
    }

    [Fact]
    public void ThePanelOffersTheDownloadOnlyWhenTheModelIsMissing()
    {
        Assert.True(PanelVm(new FakeRigEstimatorModelDownloadService()).ShowMatchModelOffer);
        Assert.False(PanelVm(
            new FakeRigEstimatorModelDownloadService { IsInstalled = true }).ShowMatchModelOffer);
        Assert.False(PanelVm(null).ShowMatchModelOffer);
    }

    /// <summary>The offer names the size, mirroring how lyric auto-sync tells
    /// the user what a press is about to cost.</summary>
    [Fact]
    public void ThePanelOfferNamesTheSize()
    {
        var vm = PanelVm(new FakeRigEstimatorModelDownloadService { ApproximateMegabytes = 187 });

        Assert.Contains("187", vm.MatchModelOffer, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ThePanelDownloadInstallsAndWithdrawsTheOffer()
    {
        var downloads = new FakeRigEstimatorModelDownloadService();
        var vm = PanelVm(downloads);

        await vm.DownloadMatchModelCommand.ExecuteAsync(null);

        Assert.Equal(1, downloads.Calls);
        Assert.False(vm.MatchModelMissing);
        Assert.False(vm.ShowMatchModelOffer);
        Assert.Equal(string.Empty, vm.MatchModelOffer);
    }

    [Fact]
    public async Task ThePanelReportsAFailedDownloadOnItsOwnLine()
    {
        var vm = PanelVm(new FakeRigEstimatorModelDownloadService { FailWith = "no network" });

        await vm.DownloadMatchModelCommand.ExecuteAsync(null);

        Assert.True(vm.ShowMatchModelOffer, "a failed download must leave the offer up");
        Assert.Contains("no network", vm.MatchModelOffer, StringComparison.Ordinal);
    }

    /// <summary>
    ///     The offer is hidden while a MATCH runs: the model cannot join a
    ///     search already in flight, and a button that silently would not apply
    ///     is worse than no button.
    /// </summary>
    [Fact]
    public void TheOfferIsHiddenWhileAMatchIsRunning()
    {
        var vm = PanelVm(new FakeRigEstimatorModelDownloadService());

        Assert.True(vm.ShowMatchModelOffer);

        vm.IsMatching = true;

        Assert.False(vm.ShowMatchModelOffer);
    }
}
