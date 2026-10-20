using DotCharter.UI.Controls.Layers;

namespace DotCharter.Tests.UI.Rendering;

/// <summary>
///     The playhead's TIME chip — the red badge in the ruler strip that the
///     timeline design (`Chart Editor.dc.html`) draws centred on the playhead.
///     <para>
///         Only its geometry is pinned here, and deliberately: the layer needs a
///         <c>TimelineBatcher</c> with a live font atlas to measure its own text,
///         so the placement rule is a pure static and this is the default-gate
///         half of it.
///     </para>
/// </summary>
public class PlayheadTimeChipTests
{
    /// <summary>The chip sat a fixed 20 px below the lanes — inside the FHP band once the SHAPES
    /// and FHP bands grew above the ruler — and covered FHP fret chips (review, 2026-09-23).</summary>
    [Theory]
    [InlineData(300.0)]
    [InlineData(520.0)]
    public void TheChip_SitsWhollyInsideTheTimeRuler(double viewportHeight)
    {
        const float badgeH = 18f;
        var top = DotCharter.UI.Controls.Layers.PlayheadLayer.BadgeY(viewportHeight, badgeH);

        Assert.True(DotCharter.UI.Input.TimelineZones.IsTimeRuler(top, viewportHeight), $"the chip's top {top} is above the ruler");
        Assert.True(DotCharter.UI.Input.TimelineZones.IsTimeRuler(top + badgeH, viewportHeight), "the chip runs past the viewport");
    }

    [Fact]
    public void MidViewport_TheChipIsCentredOnThePlayhead() =>
        // 64 px chip, playhead at 500 of a 1000 px viewport.
        Assert.Equal(500f - 32f, PlayheadLayer.BadgeX(500f, 64f, 1000));

    /// <summary>Through 0.51 the chip hung off the playhead's RIGHT side, which
    /// reads as the time a moment ahead of the line. Centring it is the change;
    /// this is what fails if it comes back.</summary>
    [Fact]
    public void TheChip_DoesNotHangOffOneSideOfThePlayhead()
    {
        const float playhead = 400f;
        const float w = 70f;
        var x = PlayheadLayer.BadgeX(playhead, w, 1000);
        Assert.True(x < playhead && x + w > playhead,
            $"the chip [{x}, {x + w}] no longer straddles the playhead at {playhead}");
    }

    [Fact]
    public void AtTheLeftEdge_TheChipStaysWhole() => Assert.Equal(PlayheadLayer.BadgeEdgeInset, PlayheadLayer.BadgeX(0f, 64f, 1000));

    [Fact]
    public void AtTheRightEdge_TheChipStaysWhole()
    {
        var x = PlayheadLayer.BadgeX(1000f, 64f, 1000);
        Assert.Equal(1000f - 64f - PlayheadLayer.BadgeEdgeInset, x);
        Assert.True(x + 64f <= 1000f, "the chip runs past the right edge of the viewport");
    }

    /// <summary>A viewport narrower than the chip is degenerate (a collapsed
    /// pane mid-drag) and must not produce a negative x — the clamp's own
    /// bounds would be inverted, which throws.</summary>
    [Fact]
    public void AViewportNarrowerThanTheChip_DoesNotThrowOrGoNegative() => Assert.Equal(PlayheadLayer.BadgeEdgeInset, PlayheadLayer.BadgeX(10f, 200f, 40));
}
