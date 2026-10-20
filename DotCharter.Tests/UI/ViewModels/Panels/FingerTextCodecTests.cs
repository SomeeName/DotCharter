using DotCharter.UI.ViewModels.Panels;

namespace DotCharter.Tests.UI.ViewModels.Panels;

public class FingerTextCodecTests
{
    [Theory]
    [InlineData("T", 0)]
    [InlineData("t", 0)]
    [InlineData("1", 1)]
    [InlineData("2", 2)]
    [InlineData("3", 3)]
    [InlineData("4", 4)]
    public void Parse_ReturnsExpectedFingerId(string input, int expected) => Assert.Equal(expected, FingerTextCodec.Parse(input));

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData(" ")]
    [InlineData("0")]   // explicit '0' is NOT thumb (only "T" is)
    [InlineData("5")]
    [InlineData("x")]
    [InlineData("12")]
    public void Parse_InvalidInputs_ReturnInvalidMarker(string? input) => Assert.Equal(FingerTextCodec.InvalidFinger, FingerTextCodec.Parse(input));

    [Theory]
    [InlineData(0, "T")]
    [InlineData(1, "1")]
    [InlineData(4, "4")]
    public void Format_RendersValidIds(int id, string expected) => Assert.Equal(expected, FingerTextCodec.Format(id));

    [Theory]
    [InlineData(-1)]
    [InlineData(5)]
    [InlineData(99)]
    public void Format_OutOfRange_ReturnsEmpty(int id) => Assert.Equal(string.Empty, FingerTextCodec.Format(id));

    [Theory]
    [InlineData("T")]
    [InlineData("1")]
    [InlineData("3")]
    public void Parse_Then_Format_RoundTrips(string original) => Assert.Equal(original, FingerTextCodec.Format(FingerTextCodec.Parse(original)));
}
