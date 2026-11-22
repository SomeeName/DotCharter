using DotCharter.Tone.Audio.ChartSynth;
using DotCharter.Tone.Audio.Vst;
using DotCharter.UI.ViewModels.Editor;

namespace DotCharter.Tests.UI;

/// <summary>
///     The Mixer's "Instrument routing" card (#751, design D) as the view binds it: one
///     segment per Sound source and per Route instrument value, each choice that cannot
///     be honoured disabled with its reason. Route instrument changes nothing under Song
///     tones (the song's tone exists only on the guitar seat's rig) nor under My instrument
///     (an unrouted instrument plays after the mix, through no rig).
/// </summary>
public sealed class ChartSoundRoutingChoicesTests
{
    private static VstPluginInfo Plugin() => new() { Name = "Ample", PluginPath = @"C:\VST3\Ample.vst3" };

    private static TransportViewModel Transport(FakeChartSynthAudio audio) =>
        new(audio, new ProjectState(), new FakeConfigurationService(), FakeLocalization.KeysOnly());

    [Fact]
    public void SourceSegments_ReadAndWriteTheOneSourceValue()
    {
        var transport = Transport(new FakeChartSynthAudio());
        transport.SelectedInstrumentVst = Plugin();
        var routing = transport.SoundRouting;
        Assert.True(routing.IsMyInstrument);

        routing.IsInstrumentThroughRig = true;
        Assert.Equal(ChartSoundSource.InstrumentThroughRig, routing.Source);
        Assert.False(routing.IsMyInstrument);

        routing.IsOwnRig = true;
        Assert.Equal(ChartSoundSource.OwnRig, routing.Source);
        routing.IsOwnRig = false;   // a radio group unchecking the old segment changes nothing
        Assert.Equal(ChartSoundSource.OwnRig, routing.Source);

        routing.IsSongTones = true;
        Assert.Equal(ChartSoundSource.SongTones, routing.Source);
        routing.IsMyInstrument = true;
        Assert.Equal(ChartSoundSource.MyInstrument, routing.Source);
    }

    [Fact]
    public void InstrumentSegments_WithoutAnInstrument_OrSandboxed_AreDisabled_WithTheirReason()
    {
        var none = Transport(new FakeChartSynthAudio()).SoundRouting;
        Assert.False(none.CanUseInstrumentThroughRig);
        Assert.Equal("Mixer_ChartSound_MyInstrument_Unavailable", none.SourceUnavailableReason);
        none.IsInstrumentThroughRig = true;
        Assert.Equal(ChartSoundSource.SongTones, none.Source);

        var sandboxed = Transport(new FakeChartSynthAudio { Sandboxed = true });
        sandboxed.SelectedInstrumentVst = Plugin();
        Assert.True(sandboxed.SoundRouting.CanUseMyInstrument);
        Assert.False(sandboxed.SoundRouting.CanUseInstrumentThroughRig);
        Assert.Equal("Mixer_InstrumentThroughRig_Sandboxed", sandboxed.SoundRouting.SourceUnavailableReason);
        sandboxed.SoundRouting.IsInstrumentThroughRig = true;   // refused, not silently dry
        Assert.Equal(ChartSoundSource.MyInstrument, sandboxed.SoundRouting.Source);

        var live = Transport(new FakeChartSynthAudio());
        live.SelectedInstrumentVst = Plugin();
        Assert.True(live.SoundRouting.CanUseInstrumentThroughRig);
        Assert.Equal("", live.SoundRouting.SourceUnavailableReason);
    }

    [Fact]
    public void RouteInstrument_UnderSongTones_IsDisabled_WithItsReason_AndLiveUnderOwnRig()
    {
        var routing = Transport(new FakeChartSynthAudio { TwoSeats = true }).SoundRouting;
        Assert.Equal(ChartSoundSource.SongTones, routing.Source);
        Assert.True(routing.CanRouteToSeats);
        Assert.False(routing.CanChooseSeatRoute);
        Assert.Equal("Mixer_ChartSound_Route_SongTones", routing.SeatRouteUnavailableReason);

        routing.IsOwnRig = true;
        Assert.True(routing.CanChooseSeatRoute);
        Assert.Equal("", routing.SeatRouteUnavailableReason);
    }

    [Fact]
    public void RouteInstrument_ForTheDryInstrument_IsDisabled_AndLiveThroughTheRig()
    {
        var transport = Transport(new FakeChartSynthAudio { TwoSeats = true });
        transport.SelectedInstrumentVst = Plugin();
        var routing = transport.SoundRouting;
        Assert.Equal(ChartSoundSource.MyInstrument, routing.Source);
        Assert.False(routing.CanChooseSeatRoute);
        Assert.Equal("Mixer_ChartSound_Route_AfterMix", routing.SeatRouteUnavailableReason);

        routing.IsInstrumentThroughRig = true;
        Assert.True(routing.CanChooseSeatRoute);
    }

    [Fact]
    public void RouteInstrument_WithOneInput_SaysSo_WhateverTheSource()
    {
        var routing = Transport(new FakeChartSynthAudio()).SoundRouting;
        routing.IsOwnRig = true;
        Assert.False(routing.CanChooseSeatRoute);
        Assert.Equal("Mixer_ChartSound_SeatRoute_Unavailable", routing.SeatRouteUnavailableReason);
    }

    [Fact]
    public void RouteSegments_ReadAndWriteTheSeatRoute_AndRaiseOnASourceChange()
    {
        var audio = new FakeChartSynthAudio { TwoSeats = true };
        var routing = Transport(audio).SoundRouting;
        Assert.True(routing.IsGuitarOnly);

        routing.IsGuitarAndBass = true;
        Assert.Equal(ChartSoundSeatRoute.GuitarAndBass, audio.ChartSeatRoute);
        routing.IsBassOnly = true;
        Assert.Equal(ChartSoundSeatRoute.BassOnly, routing.SeatRoute);
        Assert.False(routing.IsGuitarAndBass);
        routing.IsBassOnly = false;
        Assert.Equal(ChartSoundSeatRoute.BassOnly, routing.SeatRoute);

        var raised = new List<string?>();
        routing.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        routing.IsOwnRig = true;
        Assert.Contains(nameof(routing.CanChooseSeatRoute), raised);
        Assert.Contains(nameof(routing.IsOwnRig), raised);
        Assert.Contains(nameof(routing.SourceUnavailableReason), raised);
    }
}
