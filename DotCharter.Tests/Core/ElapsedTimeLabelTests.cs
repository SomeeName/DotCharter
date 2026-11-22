using System.Globalization;

namespace DotCharter.Tests.Core;

/// <summary>Elapsed song clocks keep total hours and truncate to the requested precision.</summary>
public sealed class ElapsedTimeLabelTests
{
    [Theory]
    [InlineData(0, 0, false, "0:00")]
    [InlineData(-1, 3, false, "0:00.000")]
    [InlineData(999.9, 2, true, "00:00.99")]
    [InlineData(62_345.9, 0, false, "1:02")]
    [InlineData(62_345.9, 1, false, "1:02.3")]
    [InlineData(62_345.9, 2, true, "01:02.34")]
    [InlineData(62_345.9, 3, false, "1:02.345")]
    [InlineData(3_599_999.9, 3, false, "59:59.999")]
    [InlineData(3_600_000, 0, false, "1:00:00")]
    [InlineData(3_600_000, 2, true, "1:00:00.00")]
    [InlineData(3_723_456, 1, false, "1:02:03.4")]
    [InlineData(90_061_456, 3, false, "25:01:01.456")]
    public void Formats_elapsed_time(double ms, int fractionalDigits, bool padMinutes, string expected) =>
        Assert.Equal(expected, ElapsedTimeLabel.Format(ms, fractionalDigits, padMinutes));

    [Theory]
    [InlineData("de-DE")]
    [InlineData("ja-JP")]
    public void Clock_punctuation_is_invariant(string culture)
    {
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);

            Assert.Equal("1:02:03.456", ElapsedTimeLabel.Format(3_723_456, 3));
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Theory]
    [InlineData(3_599_999, "59:59.999")]
    [InlineData(3_600_000, "1:00:00.000")]
    [InlineData(90_061_456, "25:01:01.456")]
    public void Annotation_times_preserve_elapsed_hours(double ms, string expected) =>
        Assert.Equal(expected, new ChartAnnotation { TimeMs = ms }.TimeLabel);
}
