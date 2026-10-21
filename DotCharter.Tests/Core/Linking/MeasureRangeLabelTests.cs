using DotCharter.Core.Linking;

namespace DotCharter.Tests.Core.Linking;

/// <summary>The one spelling of a measure range that the link panel, the manager and the suggestions share.</summary>
public sealed class MeasureRangeLabelTests
{
    [Theory]
    [InlineData(53, 1, "M53")]
    [InlineData(17, 8, "M17–M24")]
    [InlineData(5, 0, "M5")]
    public void Formats_the_timeline_pill_notation(int first, int count, string expected) =>
        Assert.Equal(expected, MeasureRangeLabel.Format(first, count));
}
