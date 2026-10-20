using Avalonia;
using DotCharter.Tone.UI.Controls.Fire;

namespace DotCharter.Tests.UI;

/// <summary>
///     <b>The Play Mode start screen's hero fills a share of the window, and
///     this is the arithmetic.</b>
///     <para>
///         The screen is authored at a fixed natural size and centred, so it
///         was a small island in the middle of any window bigger than the
///         mockup: the owner reported ~350 x 300 px of a 2560 x 1440 full-screen
///         window, then that it is worse at 1920 x 1080 — where the global
///         interface scale is 1.0 by policy (<c>AutoUiScale</c>) and cannot
///         help at all.
///     </para>
///     <para>
///         Pure, because the interesting cases — an ultrawide, a viewport that
///         has not been laid out yet, a hero with no natural size — are all
///         arithmetic. The headless suite then spends its one real window on
///         proving the control is WIRED
///         (<c>Perf/InterfaceScaleUiPerfSmokeTests</c>).
///     </para>
/// </summary>
public sealed class LaunchHeroScaleTests
{
    /// <summary>
    ///     The hero's REAL natural size, measured in a headless window on
    ///     2026-09-12 (<c>LaunchColumn</c> is <c>MaxWidth="520"</c> plus
    ///     <c>FirePadScreen</c>, with the profiles panel closed).
    ///     <para>
    ///         Measured and not invented: a plausible-looking guess of 520 x 700
    ///         put every expectation in this file one or two steps out and made
    ///         the 1080p case read as "the floor holds", which is a different
    ///         claim from the one that is true.
    ///     </para>
    /// </summary>
    private static readonly Size Natural = new(680, 537);

    /// <summary>
    ///     The three displays that matter. <b>Width binds on all three</b> —
    ///     16:9 is wide enough that 40 % of the width is a tighter budget than
    ///     58 % of the height for a hero this shape — which is exactly why the
    ///     rule is a Min of two and not a height rule with a width guard.
    /// </summary>
    [Theory]
    // 1920x1080: width 1920*0.40 = 768 / 680 = 1.129 => 1.10 (floored to grid).
    // Height would have allowed 1080*0.58 = 626 / 537 = 1.166 => 1.15.
    [InlineData(1920, 1080, 1.10)]
    // 2560x1440: 1024 / 680 = 1.506 => 1.50.
    [InlineData(2560, 1440, 1.50)]
    // 3840x2160: 1536 / 680 = 2.259 => 2.25.
    [InlineData(3840, 2160, 2.25)]
    public void For_GrowsWithTheViewport(double w, double h, double expected) => Assert.Equal(expected, LaunchHeroScale.For(Natural, new Size(w, h)), 4);

    /// <summary>
    ///     …and HEIGHT binds on an ultrawide, which is the case that makes the
    ///     Min live. A 3440 x 1440 window allows 2.02 by width and 1.55 by
    ///     height; a width-only rule would grow the hero half again taller than
    ///     the window has room for, and the screen would open scrolled.
    /// </summary>
    [Fact]
    public void OnAnUltrawide_HeightBinds()
    {
        Assert.Equal(1.55, LaunchHeroScale.For(Natural, new Size(3440, 1440)), 4);
        // 5120x1440 super-ultrawide: width allows 3.01 (over the cap anyway),
        // height still 1.55.
        Assert.Equal(1.55, LaunchHeroScale.For(Natural, new Size(5120, 1440)), 4);
    }

    /// <summary>Never below 1.0 — this may only ever GROW the hero. Shrinking
    /// is what the screen's ScrollViewer is for, and a hero scaled to 0.6 would
    /// be the original complaint with extra steps.</summary>
    [Theory]
    [InlineData(320, 240)]
    [InlineData(800, 600)]
    [InlineData(1280, 720)]
    [InlineData(1366, 768)]
    public void ASmallViewport_NeverShrinksTheHero(double w, double h) => Assert.Equal(1.0, LaunchHeroScale.For(Natural, new Size(w, h)));

    /// <summary>Capped, because the logo is a 256 x 256 raster and everything
    /// past ~3x is a soft upscale of it.</summary>
    [Fact]
    public void AHugeViewport_IsCapped() => Assert.Equal(LaunchHeroScale.MaxScale, LaunchHeroScale.For(Natural, new Size(15360, 8640)));

    /// <summary>
    ///     A measure pass legitimately offers infinity (the hero is inside a
    ///     ScrollViewer) and a screen that has never been laid out offers zero.
    ///     Neither may produce a NaN, an infinity or a zero scale — each of
    ///     those makes the hero measure to nothing, which on screen is
    ///     indistinguishable from a view that failed to construct.
    /// </summary>
    [Theory]
    [InlineData(0, 0)]
    [InlineData(double.PositiveInfinity, double.PositiveInfinity)]
    [InlineData(1920, double.PositiveInfinity)]
    [InlineData(double.NaN, double.NaN)]
    public void ADegenerateViewport_IsIdentity(double w, double h)
    {
        var scale = LaunchHeroScale.For(Natural, new Size(w, h));
        Assert.True(double.IsFinite(scale) && scale >= LaunchHeroScale.MinScale, $"got {scale}");
    }

    /// <summary>A hero that has not measured yet has no natural size, and
    /// dividing by it would be an infinity.</summary>
    [Fact]
    public void AZeroSizedHero_IsIdentity() => Assert.Equal(1.0, LaunchHeroScale.For(default, new Size(2560, 1440)));

    /// <summary>
    ///     Quantised DOWN and always on the grid. Down, because the target
    ///     fractions are a BUDGET — rounding to nearest would spend a hero over
    ///     it — and on the grid because a value like 1.5000000000000002 reaching
    ///     a ScaleTransform defeats every "has it changed?" comparison
    ///     downstream, which is a re-layout per measure pass.
    /// </summary>
    [Fact]
    public void EveryAnswer_IsOnTheGridAndUnderBothBudgets()
    {
        for (var h = 400.0; h <= 4400; h += 7)
        {
            var w = h * 16 / 9;
            var scale = LaunchHeroScale.For(Natural, new Size(w, h));

            var steps = scale / LaunchHeroScale.Step;
            Assert.True(Math.Abs(steps - Math.Round(steps)) < 1e-9, $"{h} gave {scale}, off-grid.");
            Assert.InRange(scale, LaunchHeroScale.MinScale, LaunchHeroScale.MaxScale);

            // Under budget on BOTH axes — except where the floor lifts it, which
            // is the one case where overflowing the budget is the intended
            // answer (the ScrollViewer takes it from there).
            if (scale > LaunchHeroScale.MinScale)
            {
                Assert.True(Natural.Height * scale <= h * LaunchHeroScale.TargetHeightFraction + 1e-6,
                    $"at {w:0}x{h:0} the hero is {Natural.Height * scale:0.#} px tall, over its height budget.");
                Assert.True(Natural.Width * scale <= w * LaunchHeroScale.TargetWidthFraction + 1e-6,
                    $"at {w:0}x{h:0} the hero is {Natural.Width * scale:0.#} px wide, over its width budget.");
            }
        }
    }

    /// <summary>The floor and the cap must bracket something, and the fractions
    /// must be fractions. Asserted on the CONSTANTS so a later edit that makes
    /// the policy incoherent fails here rather than on screen.</summary>
    [Fact]
    public void ThePolicyConstants_AreCoherent()
    {
        Assert.True(LaunchHeroScale.MinScale >= 1.0, "the hero must never be shrunk.");
        Assert.True(LaunchHeroScale.MaxScale > LaunchHeroScale.MinScale);
        Assert.InRange(LaunchHeroScale.TargetHeightFraction, 0.1, 1.0);
        Assert.InRange(LaunchHeroScale.TargetWidthFraction, 0.1, 1.0);
        Assert.True(LaunchHeroScale.Step > 0);
    }
}
