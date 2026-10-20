using DotCharter.Services.Parsing.GuitarPro;

namespace DotCharter.Tests.Services.Parsing.GuitarPro;

public class GpVersionTests
{
    [Theory]
    [InlineData("3", 3, 0)]
    [InlineData("4", 4, 0)]
    [InlineData("4.06", 4, 6)]
    [InlineData("5.00", 5, 0)]
    [InlineData("5.10", 5, 10)]
    public void TryParse_Succeeds_ForValidStrings(string s, int major, int minor)
    {
        Assert.True(GpVersion.TryParse(s, out var v));
        Assert.Equal(major, v.Major);
        Assert.Equal(minor, v.Minor);
    }

    [Theory]
    [InlineData("")]
    [InlineData("garbage")]
    [InlineData("5.x")]
    [InlineData("x.5")]
    [InlineData("..")]
    public void TryParse_Fails_ForInvalidStrings(string s) => Assert.False(GpVersion.TryParse(s, out _));

    [Theory]
    [InlineData(3, 0, false)]
    [InlineData(4, 6, false)]
    [InlineData(5, 0, false)]
    [InlineData(5, 9, false)]
    [InlineData(5, 10, true)]
    [InlineData(5, 11, true)]
    [InlineData(6, 0, true)]
    public void IsAfter5_0_TreatsMinor10AsThreshold(int major, int minor, bool expected) => Assert.Equal(expected, new GpVersion(major, minor).IsAfter50);

    [Theory]
    [InlineData(3, 0, false)]
    [InlineData(4, 0, false)]
    [InlineData(5, 0, true)]
    [InlineData(5, 10, true)]
    [InlineData(6, 0, true)]
    public void IsVersion5_TrueFromMajor5(int major, int minor, bool expected) => Assert.Equal(expected, new GpVersion(major, minor).IsVersion5);

    [Theory]
    [InlineData(3, 0, false)]
    [InlineData(4, 0, true)]
    [InlineData(4, 6, true)]
    [InlineData(5, 0, true)]
    public void IsVersion4Plus_TrueFromMajor4(int major, int minor, bool expected) => Assert.Equal(expected, new GpVersion(major, minor).IsVersion4Plus);
}
