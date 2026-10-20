using Avalonia;

namespace DotCharter.Tone.UI.Controls.Fire;

/// <summary>
///     <b>How big the Play Mode start screen's hero column should be drawn.</b>
///
///     <para>
///         The hero (logo, wordmark, tagline, START, the profile panel, the
///         three status pills) is authored at a fixed natural size, centred in
///         whatever window it is given. On the owner's 2560 x 1440 full-screen
///         window it occupied roughly 350 x 300 px of a 3.7-megapixel canvas —
///         "everything else is empty" — and the follow-up was that it is
///         <i>worse</i> at 1920 x 1080, where it is still a small island but
///         the global interface scale (which starts at 1.0 for a 1080p window)
///         cannot help at all.
///     </para>
///
///     <para>
///         So this is deliberately NOT the global UI scale, and it is not a
///         fit-to-available either — a fit would answer 1.0 for content that
///         already fits, which is the whole complaint. It is a FILL: grow the
///         hero until it occupies a target share of the viewport, whichever of
///         the two axes binds first.
///     </para>
///
///     <list type="bullet">
///         <item>
///             <see cref="TargetHeightFraction" /> 0.58 and
///             <see cref="TargetWidthFraction" /> 0.40 are the owner's
///             55–60 % / ~40 % brief. Height usually binds on a 16:9 window;
///             width binds on an ultrawide, which is exactly when a
///             height-only rule would blow the hero off the sides.
///         </item>
///         <item>
///             <see cref="MinScale" /> is 1.0: this may only ever grow the hero.
///             Shrinking it is what a <c>ScrollViewer</c> is for, and the
///             screen already has one.
///         </item>
///         <item>
///             <see cref="MaxScale" /> is 3.0 — enough for a 4K window, and a
///             ceiling because the logo is a 256 x 256 raster
///             (<c>Assets/app-icon.png</c>). Everything else in the hero is
///             vector text and chrome that scales indefinitely; the icon is the
///             one thing with a resolution, and at the ~96 px the hero draws it
///             3x is still inside its native 256.
///         </item>
///         <item>
///             <see cref="Step" /> 0.05 for the same reason
///             <c>AutoUiScale</c> quantises: a drag-resize must not re-lay-out
///             the hero once per delivered size.
///         </item>
///     </list>
///
///     <para>
///         Pure and static so the table is asserted without a window
///         (<c>LaunchHeroScaleTests</c>); the control that applies it is
///         <see cref="HeroScaleToFit" />.
///     </para>
/// </summary>
public static class LaunchHeroScale
{
    /// <summary>The share of the viewport HEIGHT the hero grows to fill.</summary>
    public const double TargetHeightFraction = 0.58;

    /// <summary>The share of the viewport WIDTH the hero grows to fill. Binds
    /// before the height one on a wide window.</summary>
    public const double TargetWidthFraction = 0.40;

    /// <summary>This may only grow the hero — see the type remarks.</summary>
    public const double MinScale = 1.0;

    /// <summary>Bounded by the 256 x 256 raster logo — see the type remarks.</summary>
    public const double MaxScale = 3.0;

    /// <summary>The quantisation grid, so a drag-resize is not a re-layout per
    /// delivered size.</summary>
    public const double Step = 0.05;

    /// <summary>
    ///     The factor to draw a hero of <paramref name="natural" /> size at
    ///     inside a <paramref name="viewport" />.
    ///     <para>
    ///         A non-positive or non-finite input on either axis makes that axis
    ///         stop binding rather than throwing or collapsing: this runs from
    ///         <c>MeasureOverride</c>, where an unconstrained pass legitimately
    ///         offers infinite width, and the first pass of a screen that has
    ///         never been laid out offers zero.
    ///     </para>
    /// </summary>
    public static double For(Size natural, Size viewport)
    {
        var byHeight = AxisScale(natural.Height, viewport.Height, TargetHeightFraction);
        var byWidth = AxisScale(natural.Width, viewport.Width, TargetWidthFraction);

        // Whichever binds FIRST. Both default to +inf when their axis carries no
        // usable number, so Min falls through to the other one — and to MinScale
        // when neither can answer.
        var scale = Math.Min(byHeight, byWidth);
        if (!double.IsFinite(scale)) return MinScale;

        return Quantise(scale);
    }

    /// <summary>Rounds DOWN to the grid, then clamps. Down and not nearest: the
    /// target fractions are a budget, and rounding up spends a hero over it —
    /// at 4K that is the difference between filling 58 % and 61 % of the
    /// window.</summary>
    public static double Quantise(double scale)
    {
        if (!double.IsFinite(scale)) return MinScale;
        var snapped = Math.Floor(scale / Step) * Step;
        return Math.Clamp(Math.Round(snapped, 4), MinScale, MaxScale);
    }

    private static double AxisScale(double natural, double viewport, double fraction)
    {
        if (!double.IsFinite(natural) || natural <= 0) return double.PositiveInfinity;
        if (!double.IsFinite(viewport) || viewport <= 0) return double.PositiveInfinity;
        return viewport * fraction / natural;
    }
}
