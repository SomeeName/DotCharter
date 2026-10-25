using DotCharter.Tone.Audio.Abstractions;

namespace DotCharter.Tests.Services.Audio;

/// <summary>
///     The one shared reading of "which output pair carries the master mix" —
///     the helpers Settings ▸ Audio and the onboarding wizard's output step go
///     through, and the engine config's mask, so the pickers and the engine
///     cannot disagree.
/// </summary>
public sealed class OutputChannelPairsTests
{
    private static AppConfig Pair(string driver, int left, int right) => new()
    {
        AudioDriver = driver,
        AsioOutputChannelLeft = left,
        AsioOutputChannelRight = right
    };

    /// <summary>
    ///     The pair reaches the engine as an output channel mask on WASAPI exactly
    ///     as on ASIO: JUCE's WASAPI device maps the enabled bits to the endpoint's
    ///     mix-format channels, so a headphone out on outputs 3 and 4 plays in
    ///     shared mode too.
    /// </summary>
    [Theory]
    [InlineData("ASIO")]
    [InlineData("WASAPI")]
    public void TheOutputMask_NamesThePickedPair_OnEveryDriver(string driver)
    {
        Assert.Equal(0b1100u, OutputChannelPairs.ChannelMask(Pair(driver, 2, 3)));
        Assert.Equal((1u << 4) | (1u << 19), OutputChannelPairs.ChannelMask(Pair(driver, 4, 19)));
    }

    /// <summary>The default pair is outputs 1 and 2, the same two the engine opens with no mask.</summary>
    [Fact]
    public void TheDefaultConfig_MasksTheFirstPair() =>
        Assert.Equal(0b11u, OutputChannelPairs.ChannelMask(new AppConfig()));

    /// <summary>A mono row names ONE channel, so it opens one output.</summary>
    [Fact]
    public void AMonoPair_MasksOneChannel() =>
        Assert.Equal(0b100u, OutputChannelPairs.ChannelMask(Pair("WASAPI", 2, 2)));

    /// <summary>A pair chosen on a bigger interface and carried to a smaller device
    /// would lose its mask bits in JUCE; the mask falls back to 0 (the first pair opens).</summary>
    [Theory]
    [InlineData("ASIO")]
    [InlineData("WASAPI")]
    public void TheOutputMask_FallsBackToTheFirstPair_WhenAChannelIsPastTheDevice(string driver)
    {
        var pair = Pair(driver, 2, 3);
        Assert.Equal(0u, OutputChannelPairs.ChannelMask(pair, 2));
        Assert.True(OutputChannelPairs.PairPastDevice(pair, 2));

        // One channel in range, one past: still a fallback, not a half-open pair.
        Assert.Equal(0u, OutputChannelPairs.ChannelMask(pair, 3));
        Assert.True(OutputChannelPairs.PairPastDevice(pair, 3));

        // In range, or a count the engine could not report: the picked pair stays.
        Assert.Equal(0b1100u, OutputChannelPairs.ChannelMask(pair, 4));
        Assert.False(OutputChannelPairs.PairPastDevice(pair, 4));
        Assert.Equal(0b1100u, OutputChannelPairs.ChannelMask(pair, 0));
        Assert.False(OutputChannelPairs.PairPastDevice(pair, 0));
    }

    /// <summary>
    ///     A one-output endpoint with the untouched default pair (outputs 1 and 2) is no
    ///     fallback: the engine config WARNs only behind <c>PairPastDevice</c>, so a
    ///     true here was one WARN per apply for a pair nobody chose. The mask opens the
    ///     endpoint's one output. A pair the user DID pick past that endpoint still warns.
    /// </summary>
    [Theory]
    [InlineData("ASIO")]
    [InlineData("WASAPI")]
    public void TheDefaultPair_OnAOneOutputDevice_IsNoFallback_AndOpensThatOutput(string driver)
    {
        var untouched = new AppConfig { AudioDriver = driver };
        Assert.True(OutputChannelPairs.IsDefaultPair(untouched));
        Assert.False(OutputChannelPairs.PairPastDevice(untouched, 1));
        Assert.Equal(0b1u, OutputChannelPairs.ChannelMask(untouched, 1));
        // A bigger device still gets the pair as it is.
        Assert.Equal(0b11u, OutputChannelPairs.ChannelMask(untouched, 2));

        var picked = Pair(driver, 2, 3);
        Assert.False(OutputChannelPairs.IsDefaultPair(picked));
        Assert.True(OutputChannelPairs.PairPastDevice(picked, 1));
        Assert.Equal(0u, OutputChannelPairs.ChannelMask(picked, 1));
    }

    [Fact]
    public void ThePastDeviceWarning_NamesThePairAndTheDevice()
    {
        var text = OutputChannelPairs.PastDeviceMessage(Pair("WASAPI", 2, 3), "Speakers", 2);
        Assert.Contains("3/4", text, StringComparison.Ordinal);
        Assert.Contains("2 outputs of 'Speakers'", text, StringComparison.Ordinal);
    }

    /// <summary>An N-output device lists N/2 pairs, each labelled by its 1-based channels.</summary>
    [Theory]
    [InlineData(2, 1)]
    [InlineData(4, 2)]
    [InlineData(8, 4)]
    [InlineData(18, 9)]
    public void AnNOutputDevice_ListsNOverTwoPairs(int outputs, int pairs)
    {
        var rows = OutputChannelPairs.PairOptions(FakeLocalization.English(), outputs, 0, 1);

        Assert.Equal(pairs, rows.Count);
        Assert.Equal("Out 1 + 2", rows[0].Label);
        Assert.Equal((outputs - 2, outputs - 1), (rows[^1].Left, rows[^1].Right));
        Assert.Equal($"Out {outputs - 1} + {outputs}", rows[^1].Label);
        Assert.All(rows, r => Assert.False(r.IsMono));
    }

    /// <summary>A last odd output, or a one-output device, is a mono row.</summary>
    [Fact]
    public void AnOddOutput_IsAMonoRow()
    {
        var odd = OutputChannelPairs.PairOptions(FakeLocalization.English(), 5, 0, 1);
        Assert.Equal(["Out 1 + 2", "Out 3 + 4", "Out 5 (mono)"], odd.Select(r => r.Label).ToArray());
        Assert.True(odd[^1].IsMono);
        Assert.Equal((4, 4), (odd[^1].Left, odd[^1].Right));

        var single = OutputChannelPairs.PairOptions(FakeLocalization.English(), 1, 0, 0);
        Assert.Equal("Out 1 (mono)", Assert.Single(single).Label);
    }

    /// <summary>An unknown count (no engine, a failed enumeration) offers the generic list, not nothing.</summary>
    [Fact]
    public void AnUnknownCount_OffersTheGenericList()
    {
        var rows = OutputChannelPairs.PairOptions(FakeLocalization.English(), 0, 0, 1);
        Assert.Equal(OutputChannelPairs.FallbackOutputCount / 2, rows.Count);
    }

    /// <summary>A device past the mask's 32 bits offers the first 32 outputs.</summary>
    [Fact]
    public void ABigDevice_StopsAtTheMasksWidth()
    {
        var rows = OutputChannelPairs.PairOptions(FakeLocalization.English(), 64, 0, 1);
        Assert.Equal(OutputChannelPairs.MaxOutputs / 2, rows.Count);
    }

    /// <summary>A saved pair that is not one of the device's rows (typed before the picker
    /// existed, or past the device) stays in the list: it must never read as "none".</summary>
    [Fact]
    public void ASavedPair_NotAmongTheRows_IsKeptInTheList()
    {
        var rows = OutputChannelPairs.PairOptions(FakeLocalization.English(), 4, 1, 2);
        Assert.Equal(["Out 1 + 2", "Out 2 + 3", "Out 3 + 4"], rows.Select(r => r.Label).ToArray());

        var past = OutputChannelPairs.PairOptions(FakeLocalization.English(), 2, 6, 7);
        Assert.Contains(past, r => r is { Left: 6, Right: 7 });
    }

    [Fact]
    public void SelectPair_WritesBothChannels_AndReportsAChange()
    {
        var config = Pair("WASAPI", 0, 1);
        var third = new OutputPairOption(4, 5, "Out 5 + 6");

        Assert.True(OutputChannelPairs.SelectPair(config, third));
        Assert.Equal((4, 5), (config.AsioOutputChannelLeft, config.AsioOutputChannelRight));
        Assert.False(OutputChannelPairs.SelectPair(config, third));
    }
}
