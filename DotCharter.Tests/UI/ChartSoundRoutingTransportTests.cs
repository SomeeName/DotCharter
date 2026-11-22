using DotCharter.Tone.Audio.ChartSynth;
using DotCharter.Tone.Audio.Vst;
using DotCharter.UI.ViewModels.Editor;

namespace DotCharter.Tests.UI;

/// <summary>
///     The Mixer's Sound source and Route instrument (#751) on the transport: each
///     choice drives the service and the config it is saved in, comes back at the
///     next start, and a choice the setup cannot honour reads as unavailable with
///     its reason instead of live.
/// </summary>
public sealed class ChartSoundRoutingTransportTests
{
    private static VstPluginInfo Plugin() => new() { Name = "Ample", PluginPath = @"C:\VST3\Ample.vst3" };

    private static TransportViewModel Transport(FakeChartSynthAudio audio, FakeConfigurationService config) =>
        new(audio, new ProjectState(), config, FakeLocalization.KeysOnly());

    [Fact]
    public void RouteInstrument_DrivesTheService_IsSaved_AndComesBackAtTheNextStart()
    {
        var audio = new FakeChartSynthAudio { TwoSeats = true };
        var config = new FakeConfigurationService();
        var transport = Transport(audio, config);
        Assert.Equal(ChartSoundSeatRoute.GuitarOnly, transport.SoundRouting.SeatRoute);
        Assert.True(transport.SoundRouting.CanRouteToSeats);
        transport.SoundRouting.Source = ChartSoundSource.OwnRig;   // under Song tones the route changes nothing (ChartSoundRoutingChoicesTests)
        Assert.Equal("", transport.SoundRouting.SeatRouteUnavailableReason);

        transport.SoundRouting.SeatRoute = ChartSoundSeatRoute.GuitarAndBass;
        Assert.Equal(ChartSoundSeatRoute.GuitarAndBass, audio.ChartSeatRoute);
        Assert.Equal(ChartSoundSeatRoute.GuitarAndBass, config.LoadAppConfig().ChartSoundSeatRoute);

        var nextStart = new FakeChartSynthAudio { TwoSeats = true };
        Transport(nextStart, config);
        Assert.Equal(ChartSoundSeatRoute.GuitarAndBass, nextStart.ChartSeatRoute);
    }

    [Fact]
    public void SongTones_ReachTheService_AndComeBackAtTheNextStart()
    {
        // The engine keeps the chart source on the song-tone seat while Song tones
        // play (#751 review), so the service has to know which source is picked.
        var audio = new FakeChartSynthAudio { TwoSeats = true };
        var config = new FakeConfigurationService();
        var transport = Transport(audio, config);
        Assert.True(audio.ChartSynthPlaysSongTones);

        transport.SoundRouting.Source = ChartSoundSource.OwnRig;
        Assert.False(audio.ChartSynthPlaysSongTones);

        var nextStart = new FakeChartSynthAudio { TwoSeats = true, ChartSynthPlaysSongTones = true };
        Transport(nextStart, config);
        Assert.False(nextStart.ChartSynthPlaysSongTones);

        transport.SoundRouting.Source = ChartSoundSource.SongTones;
        Assert.True(audio.ChartSynthPlaysSongTones);
    }

    [Fact]
    public void RouteInstrument_WithOneInput_IsUnavailable_WithItsReason()
    {
        var transport = Transport(new FakeChartSynthAudio(), new FakeConfigurationService());
        Assert.False(transport.SoundRouting.CanRouteToSeats);
        Assert.Equal("Mixer_ChartSound_SeatRoute_Unavailable", transport.SoundRouting.SeatRouteUnavailableReason);

        var noSynth = new TransportViewModel(new FakeAudioPlaybackService(), new ProjectState(), new FakeConfigurationService(),
            FakeLocalization.KeysOnly());
        Assert.False(noSynth.SoundRouting.CanRouteToSeats);
        Assert.False(noSynth.SoundRouting.CanUseMyInstrument);
    }

    [Fact]
    public void SoundSource_WithoutAnInstrument_IsTheBuiltInVoice_AndMyInstrumentIsRefused()
    {
        var audio = new FakeChartSynthAudio();
        var config = new FakeConfigurationService();
        var transport = Transport(audio, config);
        Assert.Equal(ChartSoundSource.SongTones, transport.SoundRouting.Source);
        Assert.False(transport.SoundRouting.CanUseMyInstrument);
        Assert.Equal("Mixer_ChartSound_MyInstrument_Unavailable", transport.SoundRouting.UseMyInstrumentUnavailableReason);

        transport.SoundRouting.ThroughRig = true;   // the built-in voice through the player's own rig
        Assert.Equal(ChartSoundSource.OwnRig, transport.SoundRouting.Source);
        Assert.True(config.LoadAppConfig().ChartSynthPlaysOwnRig);

        transport.SoundRouting.Source = ChartSoundSource.InstrumentThroughRig;   // no instrument: refused
        Assert.Equal(ChartSoundSource.OwnRig, transport.SoundRouting.Source);
        Assert.Empty(audio.RouteWrites);
    }

    [Fact]
    public void SoundSource_PicksTheInstrumentOrTheVoice_AndRoutesTheInstrumentIntoTheRig()
    {
        var audio = new FakeChartSynthAudio();
        var config = new FakeConfigurationService();
        var transport = Transport(audio, config);
        transport.SelectedInstrumentVst = Plugin();   // the first pick also switches Out on
        Assert.True(transport.SoundRouting.CanUseMyInstrument);
        // A loaded instrument plays, as before #751.
        Assert.Equal(ChartSoundSource.MyInstrument, transport.SoundRouting.Source);
        Assert.True(audio.IsVstInstrumentEnabled);
        Assert.False(audio.IsChartSynthEnabled);

        transport.SoundRouting.Source = ChartSoundSource.InstrumentThroughRig;
        Assert.True(audio.RouteInstrumentToRig);
        Assert.True(config.LoadAppConfig().InstrumentThroughRig);

        // "Use my instrument" off: the built-in voice plays although a plugin is loaded.
        transport.SoundRouting.UsesInstrument = false;
        Assert.Equal(ChartSoundSource.OwnRig, transport.SoundRouting.Source);
        Assert.False(audio.IsVstInstrumentEnabled);
        Assert.True(audio.IsChartSynthEnabled);
        Assert.False(config.LoadAppConfig().ChartSoundUsesInstrument);

        transport.SoundRouting.Source = ChartSoundSource.MyInstrument;
        Assert.True(audio.IsVstInstrumentEnabled);
        Assert.False(audio.RouteInstrumentToRig);
    }

    [Fact]
    public void SavedRouteOn_ThenSandboxOn_ReadsAsMyInstrument_WithRouteDisabled()
    {
        // The engine routes only when it is saved AND possible, so the card must read what plays.
        var audio = new FakeChartSynthAudio { Sandboxed = true, RouteInstrumentToRig = true };
        var transport = Transport(audio, new FakeConfigurationService());
        transport.SelectedInstrumentVst = Plugin();
        Assert.Equal(ChartSoundSource.MyInstrument, transport.SoundRouting.Source);
        Assert.False(transport.SoundRouting.CanGoThroughRig);
    }

    [Fact]
    public void SandboxedInstrumentThroughRigPick_FromTheBuiltInVoice_ChangesNothing()
    {
        var audio = new FakeChartSynthAudio { Sandboxed = true };
        var config = new FakeConfigurationService();
        var transport = Transport(audio, config);
        transport.SelectedInstrumentVst = Plugin();
        transport.SoundRouting.Source = ChartSoundSource.SongTones;
        var before = config.LoadAppConfig();
        Assert.False(before.ChartSoundUsesInstrument);

        transport.SoundRouting.Source = ChartSoundSource.InstrumentThroughRig;

        Assert.False(config.LoadAppConfig().ChartSoundUsesInstrument);
        Assert.Equal(ChartSoundSource.SongTones, transport.SoundRouting.Source);
        Assert.True(audio.IsChartSynthEnabled);
    }

    [Fact]
    public void ThroughRig_ForASandboxedInstrument_IsUnavailable_WithItsReason()
    {
        var audio = new FakeChartSynthAudio { Sandboxed = true };
        var transport = Transport(audio, new FakeConfigurationService());
        Assert.True(transport.SoundRouting.CanGoThroughRig);   // the built-in voice can always use the rig
        transport.SelectedInstrumentVst = Plugin();
        Assert.False(transport.SoundRouting.CanGoThroughRig);
        Assert.Equal("Mixer_InstrumentThroughRig_Sandboxed", transport.SoundRouting.ThroughRigUnavailableReason);
    }
}
