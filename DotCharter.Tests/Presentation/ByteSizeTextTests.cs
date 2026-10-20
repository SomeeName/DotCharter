using DotCharter.Presentation.Shared;

namespace DotCharter.Tests.Presentation;

public sealed class ByteSizeTextTests
{
    [Theory]
    [InlineData(0L, "0 KB")]
    [InlineData(1536L, "2 KB")]
    [InlineData(1024L * 1024 - 1, "1024 KB")]
    [InlineData(1024L * 1024, "1 MB")]
    [InlineData(700L * 1024 * 1024, "700 MB")]
    public void BelowAGigabyte_PrintsWholeUnits(long bytes, string expected) => Assert.Equal(expected, ByteSizeText.Format(bytes));

    [Fact]
    public void FromAGigabyte_PrintsOneDecimalInGigabytes()
    {
        var text = ByteSizeText.Format(1536L * 1024 * 1024);

        Assert.EndsWith(" GB", text, StringComparison.Ordinal);
        Assert.Equal(6, text.Length);
        Assert.StartsWith("1", text, StringComparison.Ordinal);
        Assert.Equal('5', text[2]);
    }
}
