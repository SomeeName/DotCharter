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

    /// <summary>
    ///     Design D's "Reset routing" asks first (nothing moves until Confirm), then puts the song tones back and the
    ///     offset to 0 ms; "Reset instrument" asks the same way on its card.
    /// </summary>
    [Fact]
    public void ResetRouting_AsksFirst_ThenPutsTheToneSourceAndOffsetBack()
    {
        var config = new FakeConfigurationService();
        var transport = new TransportViewModel(new FakeAudioPlaybackService(), new ProjectState(), config, FakeLocalization.KeysOnly())
        {
            ChartSynthPlaysOwnRig = true,
            MidiOffsetMs = 40
        };

        transport.ResetRoutingCommand.Execute(null);
        Assert.True(transport.ResetRoutingConfirm.IsPending);
        Assert.Contains("Mixer_Midi_ResetRoutingAsk", transport.ResetRoutingConfirm.Sentence);
        Assert.True(transport.ChartSynthPlaysOwnRig);
        Assert.Equal(40, transport.MidiOffsetMs);

        transport.ResetRoutingConfirm.ConfirmCommand.Execute(null);
        Assert.True(transport.ChartSynthPlaysSongTones);
        Assert.False(config.LoadAppConfig().ChartSynthPlaysOwnRig);
        Assert.Equal(0, transport.MidiOffsetMs);
        Assert.False(transport.ResetRoutingConfirm.IsPending);

        transport.ChartSynthInstrument.ResetInstrumentCommand.Execute(null);
        Assert.True(transport.ChartSynthInstrument.ResetConfirm.IsPending);
        Assert.Contains("Mixer_Midi_ResetInstrumentAsk", transport.ChartSynthInstrument.ResetConfirm.Sentence);
        transport.ChartSynthInstrument.ResetConfirm.CancelCommand.Execute(null);
        Assert.False(transport.ChartSynthInstrument.ResetConfirm.IsPending);
    }
}
