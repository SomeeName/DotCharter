using DotCharter.Tone.Audio.Abstractions;

namespace DotCharter.Tests.Services.Audio;

/// <summary>
///     The driver's channel names on the output-pair and input-jack rows. JUCE's
///     WASAPI device names every channel "Output channel N" / "Input channel N",
///     which says nothing the number does not, so those rows stay numbered; an
///     ASIO driver's own names ("Headphones L", "Hi-Z") are shown after the number.
/// </summary>
public sealed class ChannelNamesTests
{
    private static readonly string[] SixOutputs =
        ["Main L", "Main R", "Headphones L", "Headphones R", "Analog 5", "Analog 6"];

    [Theory]
    [InlineData("Output channel 3")]
    [InlineData("Input channel 12")]
    [InlineData("Out 3")]
    [InlineData("IN 2")]
    [InlineData("Ch. 5")]
    [InlineData("Channel #4")]
    [InlineData("7")]
    public void ANameThatIsOnlyAChannelNumber_IsGeneric(string name) => Assert.True(ChannelNames.IsGeneric(name));

    [Theory]
    [InlineData("Headphones L")]
    [InlineData("Hi-Z")]
    [InlineData("Analog 5")]
    [InlineData("Mic/Line 1")]
    [InlineData("S/PDIF L")]
    public void ADriversOwnName_IsNotGeneric(string name) => Assert.False(ChannelNames.IsGeneric(name));

    [Fact]
    public void AnEmptyGenericOrMissingName_IsNoName()
    {
        Assert.Null(ChannelNames.Meaningful(null, 0));
        Assert.Null(ChannelNames.Meaningful(["", "Hi-Z"], 0));
        Assert.Null(ChannelNames.Meaningful(["Input channel 1"], 0));
        Assert.Null(ChannelNames.Meaningful(["Hi-Z"], 1));
        Assert.Null(ChannelNames.Meaningful(["Hi-Z"], -1));
        Assert.Equal("Hi-Z", ChannelNames.Meaningful(["  Hi-Z "], 0));
    }

    [Fact]
    public void APairName_DropsTheSides_KeepsAShortTail_OrNamesBoth()
    {
        Assert.Equal("Headphones", ChannelNames.PairName(SixOutputs, 2, 3));
        Assert.Equal("Main", ChannelNames.PairName(SixOutputs, 0, 1));
        Assert.Equal("Analog 5/6", ChannelNames.PairName(SixOutputs, 4, 5));
        Assert.Equal("Analog 13/14", ChannelNames.PairName(["Analog 13", "Analog 14"], 0, 1));
        Assert.Equal("Main R / Headphones L", ChannelNames.PairName(SixOutputs, 1, 2));
        Assert.Equal("Analog 5", ChannelNames.PairName(SixOutputs, 4, 4));
        // One side without a name: the row stays numbered rather than half-named.
        Assert.Null(ChannelNames.PairName(["Headphones L", "Output channel 2"], 0, 1));
    }

    [Fact]
    public void ThePairRows_CarryTheDriversNames()
    {
        var rows = OutputChannelPairs.PairOptions(FakeLocalization.English(), 6, 0, 1, SixOutputs);
        Assert.Equal(["Out 1 + 2 · Main", "Out 3 + 4 · Headphones", "Out 5 + 6 · Analog 5/6"],
            rows.Select(r => r.Label).ToArray());

        var mono = OutputChannelPairs.PairOptions(FakeLocalization.English(), 1, 0, 0, ["Line Out"]);
        Assert.Equal("Out 1 (mono) · Line Out", Assert.Single(mono).Label);
    }

    /// <summary>WASAPI's generic names, an empty list and a list shorter than the
    /// device all fall back to the numbered rows.</summary>
    [Fact]
    public void ThePairRows_FallBackToNumbers_WithoutAMeaningfulName()
    {
        var wasapi = OutputChannelPairs.PairOptions(FakeLocalization.English(), 4, 0, 1,
            ["Output channel 1", "Output channel 2", "Output channel 3", "Output channel 4"]);
        Assert.Equal(["Out 1 + 2", "Out 3 + 4"], wasapi.Select(r => r.Label).ToArray());

        var none = OutputChannelPairs.PairOptions(FakeLocalization.English(), 4, 0, 1, []);
        Assert.Equal(["Out 1 + 2", "Out 3 + 4"], none.Select(r => r.Label).ToArray());

        var shortList = OutputChannelPairs.PairOptions(FakeLocalization.English(), 4, 0, 1, ["Main L", "Main R"]);
        Assert.Equal(["Out 1 + 2 · Main", "Out 3 + 4"], shortList.Select(r => r.Label).ToArray());
    }

    [Fact]
    public void TheJackRows_CarryTheDriversNames_AndFallBackToNumbers()
    {
        var rows = GuitarInputChannels.JackOptions(FakeLocalization.English(), 3, ["Mic 1", "Hi-Z", "Input channel 3"]);
        Assert.Equal(["Input 1 · Mic 1", "Input 2 · Hi-Z", "Input 3"], rows.Select(r => r.Label).ToArray());

        Assert.Equal("Input 2", GuitarInputChannels.JackLabel(FakeLocalization.English(), 1));
        Assert.Equal("Input 2 · Hi-Z", GuitarInputChannels.JackLabel(FakeLocalization.English(), 1, ["Mic 1", "Hi-Z"]));
    }
}
