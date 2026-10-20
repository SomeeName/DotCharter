using DotCharter.Rendering.Data;

namespace DotCharter.Tests.Rendering;

/// <summary>
///     The couch co-op split highway's tiling policy. GPU-free and in the
///     DEFAULT gate on purpose: the two failure modes it guards — a degenerate
///     viewport (a Vulkan validation error) and an overlapping pair (one
///     player's highway drawing into the other's half of a shared depth buffer)
///     — are cheap to assert here and expensive to find on a GPU.
/// </summary>
public class SplitHighwayLayoutTests
{
    private static HighwayViewportRect[] Buffer(int n = 4) => new HighwayViewportRect[n];

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(-3)]
    public void OnePlayer_GetsTheWholeFrame(int players)
    {
        var dest = Buffer();
        var count = SplitHighwayLayout.Compute(1920, 1080, players, SplitHighwayOrientation.Auto, dest);

        Assert.Equal(1, count);
        Assert.Equal(new HighwayViewportRect(0, 0, 1920, 1080), dest[0]);
    }

    [Fact]
    public void TwoPlayers_OnALandscapeFrame_Stack()
    {
        var dest = Buffer();
        var count = SplitHighwayLayout.Compute(1920, 1080, 2, SplitHighwayOrientation.Auto, dest);

        Assert.Equal(2, count);
        // Each keeps the FULL width — that is the whole point of stacking: the
        // owner-settled camera framing is aspect-derived and was tuned against
        // wide viewports.
        Assert.Equal(1920, dest[0].Width);
        Assert.Equal(1920, dest[1].Width);
        Assert.Equal(0, dest[0].Y);
        Assert.True(dest[1].Y > dest[0].Bottom, "the second view must start after the first ends");
    }

    [Fact]
    public void TwoPlayers_OnAPortraitFrame_SitSideBySide()
    {
        var dest = Buffer();
        var count = SplitHighwayLayout.Compute(800, 1400, 2, SplitHighwayOrientation.Auto, dest);

        Assert.Equal(2, count);
        Assert.Equal(1400, dest[0].Height);
        Assert.Equal(1400, dest[1].Height);
        Assert.Equal(0, dest[0].X);
        Assert.True(dest[1].X > dest[0].Right);
    }

    [Theory]
    [InlineData(SplitHighwayOrientation.Stacked)]
    [InlineData(SplitHighwayOrientation.SideBySide)]
    public void AnExplicitOrientation_OverridesTheAspectHeuristic(SplitHighwayOrientation orientation)
    {
        var dest = Buffer();
        SplitHighwayLayout.Compute(1920, 1080, 2, orientation, dest);

        if (orientation == SplitHighwayOrientation.Stacked)
        {
            Assert.Equal(1920, dest[0].Width);
            Assert.Equal(1920, dest[1].Width);
        }
        else
        {
            Assert.Equal(1080, dest[0].Height);
            Assert.Equal(1080, dest[1].Height);
        }
    }

    [Theory]
    // Odd sizes are the interesting ones: integer division leaves a remainder
    // that would otherwise show as unpainted rows of raw clear colour.
    [InlineData(1920, 1081, 2)]
    [InlineData(1921, 1080, 2)]
    [InlineData(1366, 768, 2)]
    [InlineData(1000, 1000, 3)]
    [InlineData(777, 999, 4)]
    [InlineData(3840, 2160, 2)]
    public void EveryTiling_IsLegalNonOverlappingAndReachesBothEdges(int w, int h, int players)
    {
        var dest = Buffer();
        var count = SplitHighwayLayout.Compute(w, h, players, SplitHighwayOrientation.Auto, dest);
        Assert.Equal(players, count);

        var stacked = dest[0].Width == w;

        for (var i = 0; i < count; i++)
        {
            var r = dest[i];
            // Legal: positive extents, entirely inside the frame. A zero or
            // negative extent is VUID-VkViewport-width-01770 on the GPU.
            Assert.True(r.Width > 0 && r.Height > 0, $"view {i} is degenerate: {r}");
            Assert.True(r.X >= 0 && r.Y >= 0, $"view {i} starts outside the frame: {r}");
            Assert.True(r.Right <= w && r.Bottom <= h, $"view {i} exceeds the frame: {r}");
        }

        for (var i = 1; i < count; i++)
        {
            var prev = dest[i - 1];
            var cur = dest[i];
            // Exactly one divider band between neighbours — never an overlap
            // (shared depth buffer) and never a wider gap (reads as a bug).
            var gap = stacked ? cur.Y - prev.Bottom : cur.X - prev.Right;
            Assert.Equal(SplitHighwayLayout.DividerPx, gap);
        }

        // The tiling reaches the far edge exactly: the last view absorbs the
        // integer-division remainder.
        var last = dest[count - 1];
        Assert.Equal(stacked ? h : w, stacked ? last.Bottom : last.Right);
        Assert.Equal(0, stacked ? dest[0].Y : dest[0].X);
    }

    [Fact]
    public void AFrameTooSmallToSplit_DegradesToOneFullViewport()
    {
        var dest = Buffer();
        // 40 px of height cannot give two players MinViewportExtent each.
        var count = SplitHighwayLayout.Compute(1920, 40, 2, SplitHighwayOrientation.Stacked, dest);

        Assert.Equal(1, count);
        Assert.Equal(new HighwayViewportRect(0, 0, 1920, 40), dest[0]);
    }

    [Fact]
    public void ThePlayerCount_IsClampedToTheDestinationAndTheCap()
    {
        var dest = Buffer(2);
        var count = SplitHighwayLayout.Compute(1920, 1080, 8, SplitHighwayOrientation.Stacked, dest);
        Assert.Equal(2, count);

        var big = Buffer(8);
        var capped = SplitHighwayLayout.Compute(1920, 2160, 8, SplitHighwayOrientation.Stacked, big);
        Assert.Equal(SplitHighwayLayout.MaxPlayers, capped);
    }

    [Fact]
    public void AZeroSizedFrame_NeverProducesAZeroSizedViewport()
    {
        var dest = Buffer();
        var count = SplitHighwayLayout.Compute(0, 0, 2, SplitHighwayOrientation.Auto, dest);

        Assert.Equal(1, count);
        Assert.True(dest[0].Width > 0 && dest[0].Height > 0);
    }

    [Fact]
    public void AnEmptyDestination_IsARejectedArgument()
    {
        Assert.Throws<ArgumentException>(() =>
            SplitHighwayLayout.Compute(1920, 1080, 2, SplitHighwayOrientation.Auto, []));
    }
}
