using DotCharter.Tone.Audio.Vst;
using DotCharter.UI.ViewModels.Editor;

namespace DotCharter.Tests.UI;

/// <summary>
///     #138: MIDI Out is the ONE switch that plays the chart. Without a VST
///     instrument it plays the built-in guitar / bass voice (the Windows GS
///     Wavetable fallback is gone); with one it plays the plugin. MIDI in is that
///     instrument's level, whichever plays. Asserted against the audio service.
/// </summary>
public sealed class MidiOutRoutingTests
{
    private static VstPluginInfo Plugin() => new() { Name = "Ample", PluginPath = @"C:\VST3\Ample.vst3" };

    [Fact]
    public void MidiOut_WithoutAPlugin_PlaysTheBuiltInSynth()
    {
        var audio = new FakeChartSynthAudio();
        var transport = new TransportViewModel(audio, new ProjectState(), new FakeConfigurationService(), FakeLocalization.KeysOnly())
        {
            MidiEnabled = true,
        };

        Assert.True(audio.IsChartSynthEnabled);
        Assert.False(audio.IsVstInstrumentEnabled);
        Assert.False(transport.PreviewsMidiToExternalDevice);   // no GS Wavetable fallback

        transport.MidiEnabled = false;
        Assert.False(audio.IsChartSynthEnabled);
    }

    [Fact]
    public void MidiOut_WithAPlugin_PlaysThePlugin_AndClearingItHandsBackToTheSynth()
    {
        var audio = new FakeChartSynthAudio();
        var transport = new TransportViewModel(audio, new ProjectState(), new FakeConfigurationService(), FakeLocalization.KeysOnly())
        {
            SelectedInstrumentVst = Plugin(),   // the first pick also switches Out on
        };

        Assert.True(transport.MidiEnabled);
        Assert.True(audio.IsVstInstrumentEnabled);
        Assert.False(audio.IsChartSynthEnabled);

        transport.SelectedInstrumentVst = null;

        Assert.True(transport.MidiEnabled);        // Out stays as the user left it
        Assert.False(audio.IsVstInstrumentEnabled);
        Assert.True(audio.IsChartSynthEnabled);
    }

    [Theory]
    [InlineData(0.5)]
    [InlineData(2.0)]
    public void ChartSound_SendsTheSameFaderToBothInstruments_WithMuteAndSolo(double gain)
    {
        var audio = new FakeChartSynthAudio();
        var transport = new TransportViewModel(audio, new ProjectState(), new FakeConfigurationService(), FakeLocalization.KeysOnly())
        {
            MidiVolume = gain,
        };

        Assert.Equal((float)gain, audio.ChartSynthGain);
        Assert.Equal(gain, audio.VstInstrumentVolume);

        transport.IsMidiMuted = true;
        Assert.Equal(0f, audio.ChartSynthGain);
        Assert.Equal(0.0, audio.VstInstrumentVolume);

        transport.IsMidiMuted = false;
        transport.IsSongSoloed = true;
        Assert.Equal(0f, audio.ChartSynthGain);
        Assert.Equal(0.0, audio.VstInstrumentVolume);
    }

    [Fact]
    public void ALoadedRoutedInstrument_CannotBoostTheRigWithMidiOutOff()
    {
        var audio = new FakeChartSynthAudio();
        var transport = new TransportViewModel(audio, new ProjectState(), new FakeConfigurationService(), FakeLocalization.KeysOnly())
        {
            SelectedInstrumentVst = Plugin(),
            RouteInstrumentToRig = true,
            MidiVolume = 2.0,
        };
        Assert.Equal(2.0, audio.VstInstrumentVolume);

        transport.MidiEnabled = false;
        Assert.Equal(1.0, audio.VstInstrumentVolume);
        Assert.Equal(2.0, transport.MidiVolume);

        transport.MidiEnabled = true;
        Assert.Equal(2.0, audio.VstInstrumentVolume);
    }

    [Fact]
    public void AnExternalMidiDevice_PickedInSettings_StillGetsThePreview()
    {
        var config = new FakeConfigurationService();
        config.AppConfig.MidiOutputDevice = 1;
        config.AppConfig.MidiOutputDeviceName = "Roland UM-ONE";
        var transport = new TransportViewModel(new FakeChartSynthAudio(), new ProjectState(), config, FakeLocalization.KeysOnly())
        {
            MidiEnabled = true,
        };
        Assert.True(transport.PreviewsMidiToExternalDevice);

        config.AppConfig.MidiOutputDeviceName = "Microsoft GS Wavetable Synth";
        transport.MidiEnabled = false;
        transport.MidiEnabled = true;
        Assert.False(transport.PreviewsMidiToExternalDevice);
    }
}
