using DotCharter.UI.Constants;
using DotCharter.UI.Controls.Layers.Helpers;

namespace DotCharter.Tests.UI.Controls;

/// <summary>
///     One lyric syllable's pill in the 2D timeline. The live app (2026-09-23) drew short syllables'
///     pills over each other — each widened to fit its text, none stopped at the next syllable — so
///     the next pill covered the text of the one before ("shi ma u" read "s ma u").
/// </summary>
public sealed class LyricPillLayoutTests
{
    private const float Text = 20f;

    [Fact]
    public void ALongSyllable_IsAsLongAsItIsSung_WithItsTextAtTheNormalInset()
    {
        var pill = LyricPillLayout.Layout(100f, 300f, 400f, Text);
        Assert.Equal(200f, pill.Width);
        Assert.Equal(100f + LyricPillLayout.NormalPadPx, pill.TextX);
        Assert.True(pill.ShowText);
    }

    [Fact]
    public void AShortSyllableWithRoom_GrowsToFitItsText()
    {
        var pill = LyricPillLayout.Layout(100f, 105f, 400f, Text);
        Assert.Equal(Text + LyricPillLayout.NormalPadPx * 2f, pill.Width);
    }

    /// <summary>The fault itself, across every gap to the next syllable that leaves room for the
    /// narrowest pill plus its gap: a pill never reaches the next syllable.</summary>
    [Fact]
    public void APill_NeverRunsIntoTheNextSyllable()
    {
        var firstRoomy = 100f + LyricPillLayout.MinDrawnWidthPx + LyricPillLayout.GapPx;
        for (var next = firstRoomy; next <= 200f; next += 0.5f)
        {
            var pill = LyricPillLayout.Layout(100f, 105f, next, Text);
            Assert.True(100f + pill.Width <= next - LyricPillLayout.GapPx + 0.001f,
                $"next at {next}: the pill ends at {100f + pill.Width}");
        }
    }

    /// <summary>
    ///     The deliberate exception, pinned rather than excused: closer than
    ///     <see cref="LyricPillLayout.MinDrawnWidthPx" /> + <see cref="LyricPillLayout.GapPx" /> — down
    ///     to two syllables at the same instant, where the room is negative — a pill stays
    ///     <see cref="LyricPillLayout.MinDrawnWidthPx" /> wide and textless. A visible mark of where a
    ///     syllable is beats strict separation; the next pill, drawn later, sits over its tail, and
    ///     zooming in separates them. (Until 2026-09-25 the test above waved this case through with an
    ///     <c>|| width &lt;= MinDrawnWidthPx</c> escape its loop never reached.)
    /// </summary>
    [Theory]
    [InlineData(105.5f)] // just short of min + gap
    [InlineData(103f)]   // less than the minimum itself
    [InlineData(100f)]   // the same instant
    [InlineData(97f)]    // negative room: the next syllable starts first
    public void ACrowdedSyllable_KeepsTheMinimumPill_WithoutText(float next)
    {
        var pill = LyricPillLayout.Layout(100f, 105f, next, Text);

        Assert.Equal(LyricPillLayout.MinDrawnWidthPx, pill.Width);
        Assert.False(pill.ShowText);
    }

    [Fact]
    public void ACrampedPill_CentresItsTextWithATighterInset()
    {
        // Room for 26 px: the text (20) fits with 3 px a side, not with 9.
        var pill = LyricPillLayout.Layout(100f, 105f, 128f, Text);
        Assert.Equal(26f, pill.Width);
        Assert.True(pill.ShowText);
        Assert.Equal(103f, pill.TextX);
    }

    /// <summary>Too narrow even for the tight inset: the text is left out, never printed under the
    /// next syllable's pill.</summary>
    [Fact]
    public void APillTooNarrowForItsText_LeavesTheTextOut()
    {
        var pill = LyricPillLayout.Layout(100f, 105f, 115f, Text);
        Assert.False(pill.ShowText);
        Assert.True(pill.Width < Text);
    }

    [Fact]
    public void TheLastSyllable_KeepsTheMinimumWidth()
    {
        var pill = LyricPillLayout.Layout(100f, 101f, float.PositiveInfinity, 0f);
        Assert.Equal(LyricPillMetrics.MinPillWidthPx, pill.Width);
    }

    [Fact]
    public void ASyllableCutOffAtTheLeftEdge_KeepsItsTextOnTheVisiblePart()
    {
        var pill = LyricPillLayout.Layout(-60f, 40f, 200f, Text);
        Assert.Equal(LyricPillLayout.NormalPadPx, pill.TextX);
    }

    /// <summary>With room, a region is at least 2 × <see cref="LyricPillLayout.RegionPadPx" /> wide.</summary>
    [Fact]
    public void ARegionWithRoom_KeepsItsMinimumWidth()
    {
        var (start, end) = LyricPillLayout.PhraseRegion(100f, 100f, 100f, float.PositiveInfinity);

        Assert.Equal(96f, start);
        Assert.Equal(start + LyricPillLayout.RegionPadPx * 2f, end);
    }

    /// <summary>
    ///     However close the next phrase, a region stops <see cref="LyricPillLayout.GapPx" /> short of
    ///     the next phrase's region — the minimum width gives way, down to an empty region. The
    ///     review's example: first syllable at 100 gives start 96, the next phrase also at 100; the
    ///     8 px minimum used to end the band at 104, across the next band's start at 96.
    /// </summary>
    [Theory]
    [InlineData(100f)] // the review's example: the next phrase at the same x
    [InlineData(108f)]
    [InlineData(112f)] // just short of min width + pad + gap
    [InlineData(120f)] // room enough: the minimum applies again
    public void ARegion_NeverRunsIntoTheNextPhrasesRegion(float nextPhraseFirstX)
    {
        var (start, end) = LyricPillLayout.PhraseRegion(100f, 100f, 100f, nextPhraseFirstX);
        var cap = nextPhraseFirstX - LyricPillLayout.RegionPadPx - LyricPillLayout.GapPx;

        Assert.True(end >= start, $"inverted region {start}..{end}");
        // Either it stops at the cap, or — when the cap is before its own start — it is empty.
        Assert.True(end <= Math.Max(start, cap) + 0.001f,
            $"region {start}..{end} runs past {cap}, the gap before the next phrase's region");
    }
}
