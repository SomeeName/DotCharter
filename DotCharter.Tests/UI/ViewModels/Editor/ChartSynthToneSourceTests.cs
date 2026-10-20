using DotCharter.UI.ViewModels.Editor;

namespace DotCharter.Tests.UI.ViewModels.Editor;

/// <summary>
///     Mixer ▸ MIDI synth ▸ Synth tone (#164): the two halves of the switch are one
///     saved choice, the chart's tones by default, and every change says so to the
///     bindings of BOTH halves (a RadioButton group reads each).
/// </summary>
public sealed class ChartSynthToneSourceTests
{
    [Fact]
    public void TheChoice_IsSaved_AndBothHalvesFollowIt()
    {
        var config = new FakeConfigurationService();
        var transport = new TransportViewModel(new FakeAudioPlaybackService(), new ProjectState(), config, FakeLocalization.KeysOnly());
        Assert.True(transport.ChartSynthPlaysSongTones);
        Assert.False(transport.ChartSynthPlaysOwnRig);

        var raised = new List<string?>();
        transport.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        transport.ChartSynthPlaysOwnRig = true;
        Assert.True(config.LoadAppConfig().ChartSynthPlaysOwnRig);
        Assert.False(transport.ChartSynthPlaysSongTones);
        Assert.Contains(nameof(TransportViewModel.ChartSynthPlaysOwnRig), raised);
        Assert.Contains(nameof(TransportViewModel.ChartSynthPlaysSongTones), raised);

        // The group unticking the other half writes false to it: that is not a choice.
        transport.ChartSynthPlaysSongTones = false;
        Assert.True(transport.ChartSynthPlaysOwnRig);

        transport.ChartSynthPlaysSongTones = true;
        Assert.False(config.LoadAppConfig().ChartSynthPlaysOwnRig);

        // A fresh transport over the same config reads the saved choice.
        transport.ChartSynthPlaysOwnRig = true;
        Assert.True(new TransportViewModel(new FakeAudioPlaybackService(), new ProjectState(), config, FakeLocalization.KeysOnly()).ChartSynthPlaysOwnRig);
    }
}
