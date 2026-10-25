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

    /// <summary>
    ///     The guitar's jacks reach the engine as an input channel mask on WASAPI
    ///     exactly as on ASIO: JUCE's WASAPI device opens the endpoint and maps the
    ///     masked channels, so a guitar on input 2 of a many-input interface opens
    ///     in shared mode without ASIO's exclusive lock.
    /// </summary>
    [Theory]
    [InlineData("ASIO")]
    [InlineData("WASAPI")]
    public void TheInputMask_NamesThePickedJacks_OnEveryDriver(string driver)
    {
        var mono = new AppConfig { EnableInput = true, AudioDriver = driver, AsioInputMono = true, AsioInputChannelLeft = 1 };
        Assert.Equal(0b10u, GuitarInputChannels.InputChannelMask(mono));

        var pair = new AppConfig
        {
            EnableInput = true, AudioDriver = driver, AsioInputMono = false, AsioInputChannelLeft = 4, AsioInputChannelRight = 19,
        };
        Assert.Equal((1u << 4) | (1u << 19), GuitarInputChannels.InputChannelMask(pair));
    }

    /// <summary>A jack chosen on a bigger interface and carried to a smaller device would
    /// lose its mask bit in JUCE and open silent; the mask falls back to 0 (first channels).</summary>
    [Fact]
    public void TheInputMask_FallsBackToTheFirstChannels_WhenAJackIsPastTheDevice()
    {
        var pair = new AppConfig
        {
            EnableInput = true, AudioDriver = "WASAPI", AsioInputMono = false, AsioInputChannelLeft = 1, AsioInputChannelRight = 19,
        };
        Assert.Equal(0u, GuitarInputChannels.InputChannelMask(pair, 2));
        Assert.True(GuitarInputChannels.JackPastDevice(pair, 2));

        // In range, or a count the engine could not report: the picked jacks stay.
        Assert.Equal((1u << 1) | (1u << 19), GuitarInputChannels.InputChannelMask(pair, 20));
        Assert.Equal((1u << 1) | (1u << 19), GuitarInputChannels.InputChannelMask(pair, 0));

        var mono = new AppConfig { EnableInput = true, AudioDriver = "WASAPI", AsioInputMono = true, AsioInputChannelLeft = 1, AsioInputChannelRight = 19 };
        Assert.Equal(0b10u, GuitarInputChannels.InputChannelMask(mono, 2));
    }

    /// <summary>Input off opens no mask; a jack past the 32-bit mask is dropped rather than wrapped.</summary>
    [Fact]
    public void TheInputMask_IsEmptyWithInputOff_AndIgnoresJacksPastTheMask()
    {
        Assert.Equal(0u, GuitarInputChannels.InputChannelMask(
            new AppConfig { EnableInput = false, AudioDriver = "WASAPI", AsioInputChannelLeft = 1 }));
        Assert.Equal(0u, GuitarInputChannels.InputChannelMask(
            new AppConfig { EnableInput = true, AudioDriver = "WASAPI", AsioInputMono = true, AsioInputChannelLeft = 32 }));
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
        var rows = GuitarInputChannels.JackOptions(FakeLocalization.English(), 2, null, 5);
        Assert.Equal([0, 1, 5], rows.Select(r => r.Jack).ToArray());
        Assert.Equal(GuitarInputChannels.JackLabel(FakeLocalization.English(), 5), rows[^1].Label);
        Assert.Contains("6", rows[^1].Label); // 1-based for the user

        var unknown = GuitarInputChannels.JackOptions(FakeLocalization.English(), 0, null);
        Assert.Equal(GuitarInputChannels.FallbackJackCount, unknown.Count);

        // A 64-input device offers what the 32-bit engine mask can name.
        var wide = GuitarInputChannels.JackOptions(FakeLocalization.English(), 64, null);
        Assert.Equal(GuitarInputChannels.MaxJacks, wide.Count);
        Assert.Equal(31, wide[^1].Jack);
    }

    [Fact]
    public void JackForOpenChannel_FollowsTheCompactedMask_OnEitherDriver()
    {
        // The user typed the HIGHER jack in the left box: JUCE compacts the mask
        // ascending, so open channel 0 is jack 1 and open channel 1 is jack 3.
        var c = Asio(mono: false, left: 3, right: 1);
        Assert.Equal(1, GuitarInputChannels.JackForOpenChannel(c, 0));
        Assert.Equal(3, GuitarInputChannels.JackForOpenChannel(c, 1));
        Assert.Equal(4, GuitarInputChannels.JackForOpenChannel(Asio(mono: true, left: 4), 0));
        // WASAPI masks the same jacks, so the same compaction holds.
        var wasapi = Asio(mono: false, left: 3, right: 1);
        wasapi.AudioDriver = "WASAPI";
        Assert.Equal(1, GuitarInputChannels.JackForOpenChannel(wasapi, 0));
        Assert.Equal(3, GuitarInputChannels.JackForOpenChannel(wasapi, 1));
        // Input off: nothing is masked, the open index IS the jack.
        Assert.Equal(1, GuitarInputChannels.JackForOpenChannel(new AppConfig { EnableInput = false, AsioInputChannelLeft = 5 }, 1));
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
