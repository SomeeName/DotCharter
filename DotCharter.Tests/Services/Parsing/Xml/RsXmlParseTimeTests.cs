using DotCharter.Services.Parsing.Xml.Song;

namespace DotCharter.Tests.Services.Parsing.Xml;

/// <summary>
/// #319: every RS-XML time (song, vocals, showlights) goes through
/// <c>Fmt.ParseTime</c>, which stripped the dot and read the digits as ms.
/// That is right only for exactly three decimals; the Custom Song Toolkit writes
/// floats ("12.5", "0"), which imported 10× to 1000× off.
/// </summary>
public sealed class RsXmlParseTimeTests
{
    [Theory]
    [InlineData("12.345", 12_345)]
    [InlineData("12.5", 12_500)]
    [InlineData("12.34", 12_340)]
    [InlineData("12", 12_000)]
    [InlineData("12.", 12_000)]
    [InlineData(".5", 500)]
    [InlineData("0", 0)]
    [InlineData("1.2345", 1_235)]
    [InlineData("1.2344", 1_234)]
    [InlineData("0.0005", 1)]
    [InlineData("-0.500", -500)]
    [InlineData("-0.5", -500)]
    [InlineData("+3.25", 3_250)]
    [InlineData(" 7.1 ", 7_100)]
    [InlineData("103.1", 103_100)]
    public void Reads_seconds_with_any_number_of_decimals(string text, int expectedMs) =>
        Assert.Equal(expectedMs, Fmt.ParseTime(text));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("1e3")]
    [InlineData("abc")]
    [InlineData("1.2.3")]
    [InlineData("-")]
    [InlineData(".")]
    public void Garbage_reads_as_zero_and_never_throws(string? text) => Assert.Equal(0, Fmt.ParseTime(text));

    [Fact]
    public void An_overlong_value_clamps_instead_of_overflowing()
    {
        Assert.Equal(int.MaxValue, Fmt.ParseTime("99999999999999999999.5"));
        Assert.Equal(int.MinValue, Fmt.ParseTime("-99999999999999999999"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(12_345)]
    [InlineData(-500)]
    [InlineData(3_599_999)]
    public void Round_trips_what_the_writer_emits(int ms) => Assert.Equal(ms, Fmt.ParseTime(Fmt.Time(ms)));
}
