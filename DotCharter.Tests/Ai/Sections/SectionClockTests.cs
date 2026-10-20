using DotCharter.Services.Ai.Match;

namespace DotCharter.Tests.Ai.Sections;

public sealed class SectionClockTests
{
    [Theory]
    [InlineData(0, "0:00")]
    [InlineData(9.9, "0:09")]
    [InlineData(70, "1:10")]
    [InlineData(3725, "62:05")]
    [InlineData(-4, "0:00")]
    public void ReadsAsMinutesAndSeconds(double seconds, string expected) => Assert.Equal(expected, SectionClock.Format(TimeSpan.FromSeconds(seconds)));
}
