using DotCharter.UI.ViewModels.Editor;

namespace DotCharter.Tests.UI;

/// <summary>
///     The SONG MAP caption under the minimap — <c>4:12 · 84 BARS</c>.
///     <para>
///         The states worth pinning are the incomplete ones: a project opened
///         before its audio finished decoding, and a chart with no beat map yet.
///         A caption that answers "0:00 · 0 BARS" there is worse than one that
///         says nothing, because it reads as a measurement rather than as the
///         absence of one.
///     </para>
/// </summary>
public class SongMapCaptionTests
{
    /// <summary>The timeline's caption cell stacks the halves: one line never fit its 118 px.</summary>
    [Fact]
    public void TheTimelinesCaption_PutsTheBarsOnTheirOwnLine()
    {
        Assert.Equal("3:03\n156 bars", SongMapCaption.Describe(183_000, 156, "156 bars", separator: "\n"));
        Assert.Equal("156 bars", SongMapCaption.Describe(0, 156, "156 bars", separator: "\n"));
    }

    [Fact]
    public void BothHalvesPresent_ReadsAsTheDesignDraws() =>
        // 4 min 12 s.
        Assert.Equal("4:12 · 84 BARS", SongMapCaption.Describe(252_000, 84, "84 BARS"));

    [Fact]
    public void NoAudioYet_PrintsOnlyTheBars() => Assert.Equal("84 BARS", SongMapCaption.Describe(0, 84, "84 BARS"));

    [Fact]
    public void NoBeatMapYet_PrintsOnlyTheDuration() => Assert.Equal("4:12", SongMapCaption.Describe(252_000, 0, "0 BARS"));

    [Fact]
    public void NeitherHalf_PrintsNothingRatherThanZeroes() => Assert.Equal(string.Empty, SongMapCaption.Describe(0, 0, "0 BARS"));

    /// <summary>A duration, not a timestamp — the design prints <c>4:12</c>, so
    /// the minutes are not zero-padded while the seconds are.</summary>
    [Theory]
    [InlineData(252_000, "4:12")]
    [InlineData(9_000, "0:09")]
    [InlineData(600_000, "10:00")]
    [InlineData(3_723_000, "1:02:03")]
    public void Duration_IsMinutesSeconds_GrowingToHoursOnlyWhenItMust(double ms, string expected) => Assert.Equal(expected, SongMapCaption.Duration(ms));

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    public void Duration_WithoutAudio_IsNull(double ms) => Assert.Null(SongMapCaption.Duration(ms));
}
