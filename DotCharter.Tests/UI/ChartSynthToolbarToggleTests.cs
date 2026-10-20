using System.ComponentModel;
using DotCharter.Tone.Audio.Vst;
using DotCharter.UI.ViewModels.Editor;

namespace DotCharter.Tests.UI;

/// <summary>
///     #574: the editor toolbar's play-the-chart toggle. It is a second door onto
///     the ONE switch (<see cref="TransportViewModel.MidiEnabled" />, the Mixer's
///     "Play the chart" checkbox): the toolbar's command and the Mixer's property
///     flip the same state both ways, asserted against the audio service. While no
///     engine is up nothing could sound, so the command refuses, the switch reads
///     unavailable and the tip says why; an engine (re)build brings it back.
/// </summary>
public sealed class ChartSynthToolbarToggleTests
{
    private static TransportViewModel Transport(FakeChartSynthAudio audio) =>
        new(audio, new ProjectState(), new FakeConfigurationService(), FakeLocalization.KeysOnly(),
            new OnboardingWizardRig.InlineDispatcher());

    [Fact]
    public void TheToolbarCommand_AndTheMixerSwitch_FlipTheSameState()
    {
        var audio = new FakeChartSynthAudio();
        var transport = Transport(audio);
        Assert.True(transport.CanPlayChart);
        Assert.True(transport.ToggleChartSynthCommand.CanExecute(null));

        // The toolbar (and the keybind, and the palette) run the command…
        transport.ToggleChartSynthCommand.Execute(null);
        Assert.True(transport.MidiEnabled);                 // …which the Mixer's checkbox shows
        Assert.True(audio.IsChartSynthEnabled);             // …and the synth plays

        // The Mixer's checkbox writes the property; the toolbar's IsChecked reads it.
        transport.MidiEnabled = false;
        Assert.False(audio.IsChartSynthEnabled);

        transport.ToggleChartSynthCommand.Execute(null);
        Assert.True(transport.MidiEnabled);
        transport.ToggleChartSynthCommand.Execute(null);
        Assert.False(transport.MidiEnabled);
        Assert.Equal([true, false, true, false], audio.EnabledWrites);
    }

    [Fact]
    public void WithAnInstrumentPlugin_TheToggleSwitchesThePlugin_NotTheSynth()
    {
        var audio = new FakeChartSynthAudio();
        var transport = Transport(audio);
        transport.SelectedInstrumentVst = new VstPluginInfo { Name = "Ample", PluginPath = @"C:\VST3\Ample.vst3" };
        Assert.True(transport.MidiEnabled);   // the first pick switches Out on

        transport.ToggleChartSynthCommand.Execute(null);
        Assert.False(transport.MidiEnabled);
        Assert.False(audio.IsVstInstrumentEnabled);
        Assert.False(audio.IsChartSynthEnabled);
    }

    [Fact]
    public void WithNoEngine_TheToggleIsUnavailable_SaysWhy_AndRefuses()
    {
        var audio = new FakeChartSynthAudio();
        var transport = Transport(audio);
        var raised = new List<string?>();
        ((INotifyPropertyChanged)transport).PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        var canExecuteChanged = 0;
        transport.ToggleChartSynthCommand.CanExecuteChanged += (_, _) => canExecuteChanged++;

        audio.SetEngineUp(false);   // a failed rebuild, or before the lazy open

        Assert.False(transport.CanPlayChart);
        Assert.Contains(nameof(TransportViewModel.CanPlayChart), raised);
        Assert.Contains(nameof(TransportViewModel.ChartSynthToggleTip), raised);
        Assert.True(canExecuteChanged > 0, "the toolbar button was never told to re-read CanExecute");
        Assert.False(transport.ToggleChartSynthCommand.CanExecute(null));
        Assert.Equal("Toolbar_ToggleChartSynth_Unavailable", transport.ChartSynthToggleTip);

        transport.ToggleChartSynthCommand.Execute(null);   // the keybind runs Execute directly
        Assert.False(transport.MidiEnabled);
        Assert.Empty(audio.EnabledWrites);

        audio.SetEngineUp(true);
        Assert.True(transport.CanPlayChart);
        Assert.True(transport.ToggleChartSynthCommand.CanExecute(null));
        Assert.Equal("Toolbar_ToggleChartSynth_Hint", transport.ChartSynthToggleTip);
    }

    [Fact]
    public void AServiceWithNoSynth_ShowsTheToggleUnavailable()
    {
        var transport = new TransportViewModel(new FakeAudioPlaybackService(), new ProjectState(), new FakeConfigurationService(), FakeLocalization.KeysOnly(),
            new OnboardingWizardRig.InlineDispatcher());
        Assert.False(transport.CanPlayChart);
        Assert.False(transport.ToggleChartSynthCommand.CanExecute(null));
        Assert.Equal("Toolbar_ToggleChartSynth_Unavailable", transport.ChartSynthToggleTip);
    }

    /// <summary>No voice pack: the built-in voice still SOUNDS (its stand-in), so the
    /// switch stays live — but the tip says what is playing.</summary>
    [Fact]
    public void WithNoVoicePack_TheToggleStaysLive_AndTheTipNamesTheStandIn()
    {
        var audio = new FakeChartSynthAudio { PacksInstalled = false };
        var transport = Transport(audio);
        transport.ChartSynthInstrument.LoadInfoForTest();

        Assert.True(transport.CanPlayChart);
        Assert.True(transport.ChartSynthPlaysStandIn);
        Assert.Equal("Toolbar_ToggleChartSynth_StandIn", transport.ChartSynthToggleTip);

        // A loaded instrument plugin plays instead of the stand-in.
        transport.SelectedInstrumentVst = new VstPluginInfo { Name = "Ample", PluginPath = @"C:\VST3\Ample.vst3" };
        Assert.False(transport.ChartSynthPlaysStandIn);
        Assert.Equal("Toolbar_ToggleChartSynth_Hint", transport.ChartSynthToggleTip);
    }

    [Fact]
    public void WithVoicePacks_TheTipIsThePlainHint()
    {
        var audio = new FakeChartSynthAudio();
        var transport = Transport(audio);
        transport.ChartSynthInstrument.LoadInfoForTest();
        Assert.False(transport.ChartSynthPlaysStandIn);
        Assert.Equal("Toolbar_ToggleChartSynth_Hint", transport.ChartSynthToggleTip);
    }
}
