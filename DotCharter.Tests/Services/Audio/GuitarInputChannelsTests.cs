using DotCharter.Tone.Audio.Abstractions;

namespace DotCharter.Tests.Services.Audio;

/// <summary>
///     The one shared reading of "which jack is the guitar on" — the helpers
///     every picker (Settings ▸ Audio, the onboarding wizard, the rig footer,
///     the latency wizard) goes through, so they cannot disagree.
/// </summary>
public sealed class GuitarInputChannelsTests
{
    private static AppConfig Asio(bool mono, int left = 0, int right = 1) => new()
    {
        EnableInput = true,
        AudioDriver = "ASIO",
        AsioInputMono = mono,
        AsioInputChannelLeft = left,
        AsioInputChannelRight = right
    };

    [Fact]
    public void JackIndexes_AreUsedOnAsioOnly_AndNeverWithInputOff()
    {
        Assert.True(GuitarInputChannels.JackIndexesAreUsed(Asio(mono: true)));
        Assert.False(GuitarInputChannels.JackIndexesAreUsed(new AppConfig { EnableInput = true, AudioDriver = "WASAPI" }));
        Assert.False(GuitarInputChannels.JackIndexesAreUsed(new AppConfig { EnableInput = false, AudioDriver = "ASIO" }));
    }

    [Fact]
    public void OpenChannelCount_MirrorsTheEngineConfig()
    {
        Assert.Equal(0, GuitarInputChannels.OpenChannelCount(new AppConfig { EnableInput = false }));
        Assert.Equal(1, GuitarInputChannels.OpenChannelCount(Asio(mono: true)));
        Assert.Equal(2, GuitarInputChannels.OpenChannelCount(Asio(mono: false)));
    }

    [Fact]
    public void SelectJack_SeatZero_WritesTheLeftBox_AndSwapsOnCollision()
    {
        var c = Asio(mono: false, left: 0, right: 1);
        Assert.True(GuitarInputChannels.SelectJackForSeat(c, 0, 1));
        Assert.Equal(1, c.AsioInputChannelLeft);
        Assert.Equal(0, c.AsioInputChannelRight); // swapped, not collapsed onto one jack
        Assert.False(GuitarInputChannels.SelectJackForSeat(c, 0, 1)); // no-op reports so
    }

    [Fact]
    public void SelectJack_SeatOne_WritesTheRightBox()
    {
        var c = Asio(mono: false, left: 0, right: 1);
        Assert.True(GuitarInputChannels.SelectJackForSeat(c, 1, 3));
        Assert.Equal(0, c.AsioInputChannelLeft);
        Assert.Equal(3, c.AsioInputChannelRight);
        Assert.Equal(3, GuitarInputChannels.JackForSeat(c, 1));
    }

    [Fact]
    public void SelectJack_Mono_AlwaysWritesTheLeftBox_WhicheverSeatAsked()
    {
        var c = Asio(mono: true, left: 0, right: 1);
        Assert.True(GuitarInputChannels.SelectJackForSeat(c, 1, 2));
        Assert.Equal(2, c.AsioInputChannelLeft);
        Assert.Equal(1, c.AsioInputChannelRight);
    }

    [Fact]
    public void JackOptions_CoverTheDevice_AndAlwaysKeepTheConfiguredJacks()
    {
        var rows = GuitarInputChannels.JackOptions(FakeLocalization.English(), 2, 5);
        Assert.Equal([0, 1, 5], rows.Select(r => r.Jack).ToArray());
        Assert.Equal(GuitarInputChannels.JackLabel(FakeLocalization.English(), 5), rows[^1].Label);
        Assert.Contains("6", rows[^1].Label); // 1-based for the user

        var unknown = GuitarInputChannels.JackOptions(FakeLocalization.English(), 0);
        Assert.Equal(GuitarInputChannels.FallbackJackCount, unknown.Count);
    }

    [Fact]
    public void JackForOpenChannel_FollowsTheCompactedAsioMask()
    {
        // The user typed the HIGHER jack in the left box: JUCE compacts the mask
        // ascending, so open channel 0 is jack 1 and open channel 1 is jack 3.
        var c = Asio(mono: false, left: 3, right: 1);
        Assert.Equal(1, GuitarInputChannels.JackForOpenChannel(c, 0));
        Assert.Equal(3, GuitarInputChannels.JackForOpenChannel(c, 1));
        Assert.Equal(4, GuitarInputChannels.JackForOpenChannel(Asio(mono: true, left: 4), 0));
        // WASAPI: no mask, the open index IS the jack.
        Assert.Equal(1, GuitarInputChannels.JackForOpenChannel(new AppConfig { EnableInput = true, AudioDriver = "WASAPI" }, 1));
    }

    [Fact]
    public void SeatZeroOpenChannel_IsTheSeatLayoutsAnswer()
    {
        Assert.Equal(0, GuitarInputChannels.SeatZeroOpenChannel(Asio(mono: false, left: 0, right: 1)));
        var twoGuitarsSwapped = Asio(mono: false, left: 1, right: 0);
        twoGuitarsSwapped.InputChannelsAreSeparateGuitars = true;
        Assert.Equal(LiveInputSeatLayout.FromInputConfig(twoGuitarsSwapped).Channels[0],
            GuitarInputChannels.SeatZeroOpenChannel(twoGuitarsSwapped));
        Assert.Equal(1, GuitarInputChannels.SeatZeroOpenChannel(twoGuitarsSwapped));
    }

    [Fact]
    public void CalibrationChannelOrder_PreferredFirst_ThenTheRest_Clamped()
    {
        Assert.Equal([1, 0], GuitarInputChannels.CalibrationChannelOrder(1, 2).ToArray());
        Assert.Equal([0, 1], GuitarInputChannels.CalibrationChannelOrder(0, 2).ToArray());
        Assert.Equal([0], GuitarInputChannels.CalibrationChannelOrder(5, 1).ToArray());
        Assert.Equal([0], GuitarInputChannels.CalibrationChannelOrder(0, 0).ToArray());
    }
}
