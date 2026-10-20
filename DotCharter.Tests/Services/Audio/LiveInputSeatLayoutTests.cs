using DotCharter.Tone.Audio.Abstractions;
using DotCharter.Tone.Audio.Dsp;

namespace DotCharter.Tests.Services.Audio;

/// <summary>
///     <b>Two guitars must not come out one per ear.</b> The native callback has
///     had the right behaviour since ABI v28 — at more than one live seat every
///     seat is MONO from its own channel and duplicated L/R, i.e. centred — but
///     nothing outside an armed co-op run ever asked it for more than one seat.
///     <see cref="LiveInputSeatLayout.FromInputConfig" /> is the answer to "how
///     many seats does this INPUT CONFIGURATION want", and it is the only
///     producer of the resting layout.
///     <para>
///         Pure by construction: no engine, no device, no config file. That is
///         deliberate — the alternative seam is
///         <c>ApplyAudioConfiguration</c>, which opens a real audio device, and a
///         test host that opens one holds it exclusively.
///     </para>
/// </summary>
public sealed class LiveInputSeatLayoutTests
{
    private static AppConfig TwoChannels(bool separateGuitars, string driver = "ASIO",
        int left = 0, int right = 1) => new()
        {
            EnableInput = true,
            AudioDriver = driver,
            AsioInputMono = false,
            AsioInputChannelLeft = left,
            AsioInputChannelRight = right,
            InputChannelsAreSeparateGuitars = separateGuitars
        };

    // ------------------------------------------------------------- the feature

    /// <summary>THE POINT. Two open channels declared as two guitars run TWO live
    /// seats, one per channel — which is what puts each player through their own
    /// rig, centred, instead of filling one chain's L and R with both of
    /// them.</summary>
    [Fact]
    public void TwoGuitarsOnTwoChannels_RunTwoSeats_OnePerChannel()
    {
        var layout = LiveInputSeatLayout.FromInputConfig(TwoChannels(separateGuitars: true));

        Assert.Equal(2, layout.PlayerCount);
        Assert.Equal(0, layout.Channels[0]);
        Assert.Equal(1, layout.Channels[1]);
    }

    /// <summary>…and it is NOT inferred from the second channel being open. That
    /// is what unticking Mono has always meant for a genuine stereo pickup, whose
    /// two channels are one instrument — inferring would silently collapse their
    /// stereo image to a centred mono, and nothing in the signal tells the two
    /// cases apart.</summary>
    [Fact]
    public void TwoOpenChannelsAlone_StayOneSeat_SoAStereoPickupIsUnchanged()
    {
        var layout = LiveInputSeatLayout.FromInputConfig(TwoChannels(separateGuitars: false));

        Assert.Equal(1, layout.PlayerCount);
    }

    /// <summary>Mono opens ONE channel, so a second seat would be listening to a
    /// channel the device was never asked for. It stays one seat whatever the
    /// two-guitars flag says.</summary>
    [Fact]
    public void MonoInput_StaysOneSeat_EvenWithTheTwoGuitarFlagSet()
    {
        var config = TwoChannels(separateGuitars: true);
        config.AsioInputMono = true;

        Assert.Equal(1, LiveInputSeatLayout.FromInputConfig(config).PlayerCount);
    }

    /// <summary>No live input at all ⇒ nothing is open to split.</summary>
    [Fact]
    public void WithLiveInputOff_ItIsOneSeat()
    {
        var config = TwoChannels(separateGuitars: true);
        config.EnableInput = false;

        Assert.Equal(1, LiveInputSeatLayout.FromInputConfig(config).PlayerCount);
    }

    // --------------------------------------------------- which channel is which

    /// <summary>
    ///     The seat channels index the OPEN set, not the interface's jacks. ASIO
    ///     builds a channel MASK and JUCE hands the callback the enabled bits
    ///     compacted in ascending hardware order, so a rig on inputs 3 and 4 still
    ///     reads 0 and 1 — and a user who typed the HIGHER jack in the "left" box
    ///     gets the seats the other way round.
    /// </summary>
    [Theory]
    [InlineData(0, 1, 0, 1)]
    [InlineData(2, 3, 0, 1)]
    [InlineData(3, 2, 1, 0)]
    public void TheSeatChannelsIndexTheOpenSet_InAscendingHardwareOrder(
        int left, int right, int expectedSeat0, int expectedSeat1)
    {
        var layout = LiveInputSeatLayout.FromInputConfig(
            TwoChannels(separateGuitars: true, left: left, right: right));

        Assert.Equal(expectedSeat0, layout.Channels[0]);
        Assert.Equal(expectedSeat1, layout.Channels[1]);
    }

    /// <summary>WASAPI has no mask — the device simply opens channels 0..N-1 — so
    /// the numbers in the ASIO channel boxes must NOT reorder the seats there.
    /// They are a control that does nothing on that driver, which is why the
    /// settings row hides them.</summary>
    [Fact]
    public void OnWasapi_TheAsioChannelNumbersDoNotReorderTheSeats()
    {
        var layout = LiveInputSeatLayout.FromInputConfig(
            TwoChannels(separateGuitars: true, driver: "WASAPI", left: 3, right: 2));

        Assert.Equal(0, layout.Channels[0]);
        Assert.Equal(1, layout.Channels[1]);
    }

    // ------------------------------------------------------------- the value type

    /// <summary>A layout always carries EVERY seat, so switching to a smaller one
    /// can never leave a stale channel on a seat that comes back.</summary>
    [Fact]
    public void ALayoutCarriesEverySeat_WithDefaultsBeyondTheLiveOnes()
    {
        var layout = new LiveInputSeatLayout(2, [1, 0]);

        Assert.Equal(DspPlayers.MaxPlayers, layout.Channels.Count);
        Assert.Equal(DspPlayers.MaxPlayers, layout.MonitorGains.Count);
        for (var p = 2; p < DspPlayers.MaxPlayers; p++)
            Assert.Equal(PlayerInputRoutingState.DefaultChannelFor(p), layout.Channels[p]);
        Assert.All(layout.MonitorGains, g => Assert.Equal(PlayerInputRoutingState.DefaultMonitorGain, g));
    }

    /// <summary>A count past the native ceiling asks for rigs that do not exist,
    /// and a non-finite or negative trim is a broken cable rather than a setting.
    /// Both are held here as well as in the roster, because this type is what the
    /// engine is written from.</summary>
    [Fact]
    public void AnImpossibleCountOrTrim_IsHeldToWhatTheEngineCanDo()
    {
        var layout = new LiveInputSeatLayout(99, [0, 1], [float.NaN, -3f]);

        Assert.Equal(DspPlayers.MaxPlayers, layout.PlayerCount);
        Assert.Equal(PlayerInputRoutingState.DefaultMonitorGain, layout.MonitorGains[0]);
        Assert.Equal(PlayerInputRoutingState.DefaultMonitorGain, layout.MonitorGains[1]);
        Assert.Equal(1, new LiveInputSeatLayout(0).PlayerCount);
    }

    /// <summary>
    ///     How many hardware input channels are OPEN — the one expression the
    ///     engine config and the co-op setup screen must share.
    ///     <para>
    ///         The co-op screen derived it from <c>AsioInputMono</c> alone and so
    ///         answered ONE with live input switched OFF, which is the shipped
    ///         default (<c>AppConfig.EnableInput</c> has no initialiser). The
    ///         seat picker then offered a "Channel 1" the device never opens and
    ///         the rig notice said "you share one guitar" where the truth is "no
    ///         input is configured" — a capability the app cannot honour, looking
    ///         live, on every machine that has never plugged a guitar in.
    ///     </para>
    /// </summary>
    [Theory]
    [InlineData(false, true, 0)]
    [InlineData(false, false, 0)]
    [InlineData(true, true, 1)]
    [InlineData(true, false, 2)]
    public void OpenInputChannels_IsZeroWheneverLiveInputIsOff(bool enableInput, bool mono, int expected)
    {
        var config = new AppConfig { EnableInput = enableInput, AsioInputMono = mono };

        Assert.Equal(expected, LiveInputSeatLayout.OpenInputChannels(config));
    }
}
