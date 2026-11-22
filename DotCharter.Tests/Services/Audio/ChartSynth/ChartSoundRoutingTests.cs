using DotCharter.Tone.Audio.ChartSynth;
using JuceSharp;

namespace DotCharter.Tests.Services.Audio.ChartSynth;

/// <summary>
///     The Mixer's chart-sound routing (#751): the pure seat-mask and sound-source
///     rules, and the driver that writes the seat route to the engine (ABI v51) on a
///     live change, on a live-seat change and on a rebuild replay.
/// </summary>
public sealed class ChartSoundRoutingTests
{
    [Theory]
    [InlineData(ChartSoundSeatRoute.GuitarOnly, 2, 0b01u)]
    [InlineData(ChartSoundSeatRoute.BassOnly, 2, 0b10u)]
    [InlineData(ChartSoundSeatRoute.GuitarAndBass, 2, 0b11u)]
    [InlineData(ChartSoundSeatRoute.GuitarAndBass, 4, 0b11u)]
    // One live input: every choice is the guitar seat, the seat that exists.
    [InlineData(ChartSoundSeatRoute.BassOnly, 1, 0b01u)]
    [InlineData(ChartSoundSeatRoute.GuitarAndBass, 1, 0b01u)]
    public void SeatMask_NamesTheChosenSeats_AndOneSeatIsAlwaysTheGuitar(ChartSoundSeatRoute route, int seats, uint mask)
    {
        Assert.Equal(mask, ChartSoundRouting.SeatMask(route, seats));
        Assert.Equal(seats >= 2, ChartSoundRouting.CanRouteToSeats(seats));
    }

    [Fact]
    public void TheDefaultRoute_IsTheGuitarSeat_ThePrePathOf751()
    {
        Assert.Equal(ChartSoundSeatRoute.GuitarOnly, ChartSoundRouting.DefaultSeatRoute);
        Assert.Equal(ChartSoundSeatRoute.GuitarOnly, ChartSoundRouting.Normalize((ChartSoundSeatRoute)42));
    }

    [Theory]
    [InlineData(ChartSoundSource.SongTones, false, false)]
    [InlineData(ChartSoundSource.OwnRig, false, true)]
    [InlineData(ChartSoundSource.MyInstrument, true, false)]
    [InlineData(ChartSoundSource.InstrumentThroughRig, true, true)]
    public void SoundSource_IsTheTwoToggles(ChartSoundSource source, bool instrument, bool rig)
    {
        Assert.Equal(instrument, ChartSoundRouting.UsesInstrument(source));
        Assert.Equal(rig, ChartSoundRouting.ThroughRig(source));
        Assert.Equal(source, ChartSoundRouting.Compose(instrument, rig));
    }

    [Fact]
    public void ASeatRoute_ReachesTheChartSource_AndTheRoutedInstrumentOnly()
    {
        var seats = 2;
        var engine = new RecordingBackend();
        var driver = new ChartSynthEngineDriver(null, () => engine, () => true, () => seats) { PlaysSongTones = false };
        Assert.True(driver.CanRouteToSeats);

        driver.SeatRoute = ChartSoundSeatRoute.BassOnly;
        driver.WaitForPushes();
        Assert.Equal(0b10u, engine.ChartMask);
        Assert.Equal(0u, engine.InstrumentMask);   // not routed to the rig: stays after the master

        driver.RouteInstrumentToRig = true;
        driver.WaitForPushes();
        Assert.Equal(0b10u, engine.InstrumentMask);

        driver.SeatRoute = ChartSoundSeatRoute.GuitarAndBass;
        driver.WaitForPushes();
        Assert.Equal((0b11u, 0b11u), (engine.ChartMask, engine.InstrumentMask));
    }

    [Fact]
    public void ALiveSeatChange_RePushesTheRoute_AndOneSeatPlaysThroughTheGuitar()
    {
        var seats = 2;
        var engine = new RecordingBackend();
        var driver = new ChartSynthEngineDriver(null, () => engine, () => true, () => seats)
        {
            PlaysSongTones = false,
            RouteInstrumentToRig = true,
            SeatRoute = ChartSoundSeatRoute.BassOnly,
        };
        driver.WaitForPushes();
        Assert.Equal((0b10u, 0b10u), (engine.ChartMask, engine.InstrumentMask));

        seats = 1;
        Assert.False(driver.CanRouteToSeats);
        driver.OnLiveSeatsChanged();
        driver.WaitForPushes();
        // The request is kept, but with no bass seat the guitar seat plays it — the
        // instrument must not fall back out of the rig onto the master.
        Assert.Equal(ChartSoundSeatRoute.BassOnly, driver.SeatRoute);
        Assert.Equal((0b01u, 0b01u), (engine.ChartMask, engine.InstrumentMask));
    }

    [Fact]
    public void ARebuild_ReplaysTheSeatRoute_AndADefaultRequestCostsNoWrite()
    {
        IChartSynthBackend? current = null;
        var driver = new ChartSynthEngineDriver(null, () => current, () => true, () => 2);

        var untouched = new RecordingBackend();
        current = untouched;
        driver.ReplayTo(untouched);
        Assert.Equal(0, untouched.MaskWrites);   // a fresh engine already stands on the default

        driver.PlaysSongTones = false;
        driver.SeatRoute = ChartSoundSeatRoute.GuitarAndBass;
        driver.RouteInstrumentToRig = true;
        driver.WaitForPushes();

        var rebuilt = new RecordingBackend();
        current = rebuilt;
        driver.ReplayTo(rebuilt);
        Assert.Equal((0b11u, 0b11u), (rebuilt.ChartMask, rebuilt.InstrumentMask));
    }

    [Theory]
    [InlineData(ChartSoundSeatRoute.GuitarOnly, 0b01u)]
    [InlineData(ChartSoundSeatRoute.BassOnly, 0b01u)]
    [InlineData(ChartSoundSeatRoute.GuitarAndBass, 0b01u)]
    public void SongTones_KeepTheChartSourceOnTheSongToneSeat(ChartSoundSeatRoute route, uint mask)
    {
        Assert.Equal(mask, ChartSoundRouting.ChartSourceSeatMask(route, 2, songTones: true));
        Assert.Equal(ChartSoundRouting.SeatMask(route, 2), ChartSoundRouting.ChartSourceSeatMask(route, 2, songTones: false));
    }

    [Fact]
    public void SongTones_RouteTheSourceAroundTheBassBoard_ButTheInstrumentStillFollowsTheRoute()
    {
        // #751 review: "Song tones" with Bass only / Guitar+Bass sent the chart synth
        // through the bass player's own board. The song's tones live on the guitar
        // seat's rig, so the source stays there; the own-rig choice follows the route.
        var engine = new RecordingBackend();
        var driver = new ChartSynthEngineDriver(null, () => engine, () => true, () => 2)
        {
            RouteInstrumentToRig = true,
            SeatRoute = ChartSoundSeatRoute.GuitarAndBass,
        };
        driver.WaitForPushes();
        Assert.True(driver.PlaysSongTones);   // the default source
        Assert.Equal((0b01u, 0b11u), (engine.ChartMask, engine.InstrumentMask));

        driver.PlaysSongTones = false;
        driver.WaitForPushes();
        Assert.Equal(0b11u, engine.ChartMask);

        driver.PlaysSongTones = true;
        driver.WaitForPushes();
        Assert.Equal(0b01u, engine.ChartMask);
    }

    [Fact]
    public void ARefusedMask_WarnsOnALiveEngine_ButNotOnATornDownOne()
    {
        var logger = new FakeAppLogger();
        var engine = new RecordingBackend { Refuse = true, IsLive = false };
        var driver = new ChartSynthEngineDriver(logger, () => engine, () => true, () => 2)
        {
            SeatRoute = ChartSoundSeatRoute.BassOnly,
        };
        driver.WaitForPushes();
        Assert.DoesNotContain(logger.Entries, e => e.Message.Contains("refused the", StringComparison.Ordinal)
                                                   && e.Message.Contains("seat mask", StringComparison.Ordinal));

        engine.IsLive = true;
        driver.SeatRoute = ChartSoundSeatRoute.GuitarAndBass;
        driver.WaitForPushes();
        Assert.Contains(logger.Entries, e => e.Message.Contains("refused the seat mask", StringComparison.Ordinal));
    }

    private sealed class RecordingBackend : IChartSynthBackend
    {
        public object Identity => this;
        public bool IsLive { get; set; } = true;
        public bool Refuse { get; init; }
        public uint ChartMask { get; private set; } = 0b01u;
        public uint InstrumentMask { get; private set; }
        public int MaskWrites { get; private set; }
        public void SetChartNotes(int player, JuceChartNote[] notes, JuceChartBendPoint[] bends) { }
        public bool TrySetChartVoice(int player, JuceChartVoice voice) => true;
        public bool TrySetChartInstrument(int player, JuceChartInstrument instrument) => true;
        public void SetChartSourceParams(int player, float gain, double offset, JuceChartSourceMode mode) { }
        public void SetChartSourceEnabled(int player, bool enabled) { }

        public bool TrySetInstrumentRouteMask(uint seatMask)
        {
            MaskWrites++;
            InstrumentMask = seatMask;
            return !Refuse;
        }

        public bool TrySetChartRouteMask(int player, uint seatMask)
        {
            Assert.Equal(ChartSoundRouting.GuitarSeat, player);
            MaskWrites++;
            ChartMask = seatMask;
            return !Refuse;
        }

        public bool TryGetChartSourceStatus(int player, out JuceChartSourceStatus status)
        {
            status = default;
            return false;
        }
    }
}
