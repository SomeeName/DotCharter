using Avalonia;
using DotCharter.Tone.UI.Converters;

namespace DotCharter.Tests.UI.Converters;

/// <summary>
///     The primary action's CHAMFER — the 45° cut off its top-right and
///     bottom-left corners.
///     <para>
///         It is a brand signature and the spec says so outright ("the angled
///         corner is a brand signature — keep it"). The first port approximated
///         it with <c>CornerRadius="0,16,0,16"</c>, which rounds the same two
///         corners instead and reads as a lozenge. This is the guard that the
///         silhouette is a POLYGON, with the cut on the right two corners and at
///         a constant size — the thing a screenshot review would catch, asserted
///         where it costs nothing.
///     </para>
///     <para>
///         It asserts the CORNER POINTS rather than the geometry, and that split
///         is not cosmetic: building a <c>StreamGeometry</c> resolves
///         <c>IPlatformRenderInterface</c> off the locator and throws without a
///         platform, so a geometry-level test could only ever run outside the
///         default gate. The maths lives in <c>ChamferPoints</c> and runs
///         anywhere; the geometry reaching <c>Clip</c> on a laid-out button is
///         asserted in <c>Perf/FireComponentUiPerfSmokeTests</c>.
///     </para>
/// </summary>
public sealed class FireChamferClipConverterTests
{
    private const double W = 200;
    private const double H = 52;
    private const double Cut = 16;

    /// <summary>
    ///     The whole silhouette, corner by corner: square top-left and
    ///     bottom-right, a 45° cut across the other two.
    /// </summary>
    [Fact]
    public void TheSilhouette_IsSquareOnOneDiagonalAndCutOnTheOther()
    {
        var points = FireChamferClipConverter.ChamferPoints(W, H, Cut);

        Assert.Equal(
            [
                new Point(0, 0),          // top-left, SQUARE
                new Point(W - Cut, 0),    // …top edge stops short
                new Point(W, Cut),        // …and cuts down: top-right is GONE
                new Point(W, H),          // bottom-right, SQUARE
                new Point(Cut, H),        // …bottom edge stops short
                new Point(0, H - Cut)     // …and cuts up: bottom-left is GONE
            ],
            points);
    }

    /// <summary>
    ///     The cut is a 45° chamfer, not an arbitrary bevel: it must retreat the
    ///     same distance along both edges it meets. This is what separates the
    ///     mark from "a corner that is a bit clipped".
    /// </summary>
    [Theory]
    [InlineData(12)]
    [InlineData(16)]
    [InlineData(18)]
    public void EachCut_RetreatsEquallyAlongBothEdgesItMeets(double cut)
    {
        var p = FireChamferClipConverter.ChamferPoints(W, H, cut);

        // Top-right: back along the top edge, then down the right edge.
        Assert.Equal(cut, W - p[1].X, 6);
        Assert.Equal(cut, p[2].Y - 0, 6);

        // Bottom-left: back along the bottom edge, then up the left edge.
        Assert.Equal(cut, p[4].X, 6);
        Assert.Equal(cut, H - p[5].Y, 6);
    }

    /// <summary>
    ///     THE reason this is a converter rather than a static geometry: the cut
    ///     must be the same number of pixels on a 120 px button and on a 900 px
    ///     one. A <c>Path</c> with <c>Stretch="Fill"</c> would scale it, and the
    ///     corner would stop being 45°.
    /// </summary>
    [Fact]
    public void TheCutStaysAConstantSize_WhateverTheButtonWidth()
    {
        foreach (var width in new double[] { 120, 200, 480, 900 })
        {
            var p = FireChamferClipConverter.ChamferPoints(width, H, Cut);

            Assert.Equal(Cut, width - p[1].X, 6);
            Assert.Equal(Cut, p[2].Y, 6);
            Assert.Equal(width, p[2].X, 6);
        }
    }

    /// <summary>
    ///     A control that has not been laid out yet has zero bounds, and one
    ///     shorter than twice the cut would produce a crossed-over polygon — a
    ///     bow tie. Both degrade to a plain rectangle instead.
    /// </summary>
    [Theory]
    [InlineData(0, 0)]
    [InlineData(0, 40)]
    [InlineData(double.NaN, 40)]
    [InlineData(40, double.NaN)]
    public void ADegenerateBox_FallsBackToAPlainRectangle(double width, double height)
    {
        var p = FireChamferClipConverter.ChamferPoints(width, height, Cut);

        Assert.Equal(4, p.Count);
        Assert.All(p, pt => Assert.False(double.IsNaN(pt.X) || double.IsNaN(pt.Y),
            "a NaN corner would make the whole clip geometry invalid"));
    }

    /// <summary>
    ///     A box shorter than twice the cut must not cross over: the two cut
    ///     points on each edge stay in order. At 20×10 with a 16 px cut, a naive
    ///     polygon would put the top edge's stop at x = 4 and the bottom edge's
    ///     at x = 16, i.e. the wrong way round — a bow tie.
    /// </summary>
    [Fact]
    public void ABoxShorterThanTwiceTheCut_ClampsInsteadOfCrossingOver()
    {
        const double w = 20, h = 10;
        var p = FireChamferClipConverter.ChamferPoints(w, h, Cut);

        Assert.Equal(6, p.Count);
        Assert.True(p[4].X <= p[1].X,
            $"the bottom edge's cut ({p[4].X}) is further right than the top edge's ({p[1].X}) — " +
            "the polygon has crossed over and would render as a bow tie");
        Assert.All(p, pt =>
        {
            Assert.InRange(pt.X, 0, w);
            Assert.InRange(pt.Y, 0, h);
        });
    }

    /// <summary>The converter is what the ControlTheme actually calls; anything
    /// that is not a <see cref="Rect" /> must map to null so a mis-typed binding
    /// fails visibly rather than throwing during layout.</summary>
    [Fact]
    public void TheConverter_MapsAnythingThatIsNotARectToNull()
    {
        var converter = FireChamferClipConverter.Instance;

        Assert.Null(converter.Convert("not a rect", typeof(object), null, null!));
        // (object?)null, not a bare null: the converter also implements
        // IMultiValueConverter (the theme-driven cut), so a bare null is
        // ambiguous between the two Convert overloads.
        Assert.Null(converter.Convert((object?)null, typeof(object), null, null!));
        Assert.Null(converter.Convert(42, typeof(object), null, null!));
    }

    /// <summary>
    ///     <c>Clip</c> is evaluated in the clipped element's OWN coordinate
    ///     space, but <c>Bounds</c> is relative to its parent. The maths
    ///     therefore reads only width and height — a chamfered element that is
    ///     not at its parent's origin still clips correctly.
    /// </summary>
    [Fact]
    public void TheSilhouette_IgnoresWhereTheElementSitsInItsParent()
    {
        var atOrigin = new Rect(0, 0, W, H);
        var offset = new Rect(37, 91, W, H);

        Assert.Equal(
            FireChamferClipConverter.ChamferPoints(atOrigin.Width, atOrigin.Height, Cut),
            FireChamferClipConverter.ChamferPoints(offset.Width, offset.Height, Cut));
    }

    /// <summary>The 12–18 px band is the spec's; the default sits in it.</summary>
    [Fact]
    public void TheDefaultCut_IsInsideTheSpecBand() => Assert.InRange(FireChamferClipConverter.DefaultCut, 12, 18);

    /// <summary>
    ///     <b>A THEME can change the signature cut.</b> The control theme puts
    ///     <c>FireCutSize</c> on the control as <c>FireShape.CutSize</c> and the
    ///     clip binds bounds AND cut, because a converter PARAMETER is a plain
    ///     value that <c>{DynamicResource}</c> cannot reach — which is why the
    ///     token existed and was read by nothing until 2026-09-20.
    /// </summary>
    [Fact]
    public void TheMultiBinding_TakesItsCutFromTheSecondValue()
    {
        Assert.Equal(24, FireChamferClipConverter.CutFrom([new Rect(0, 0, W, H), 24d]));

        // An unset, missing or nonsensical token must not lose the silhouette:
        // it falls back to the spec's default rather than to no cut at all.
        Assert.Equal(FireChamferClipConverter.DefaultCut,
            FireChamferClipConverter.CutFrom([new Rect(0, 0, W, H), AvaloniaProperty.UnsetValue]));
        Assert.Equal(FireChamferClipConverter.DefaultCut,
            FireChamferClipConverter.CutFrom([new Rect(0, 0, W, H)]));
        Assert.Equal(FireChamferClipConverter.DefaultCut,
            FireChamferClipConverter.CutFrom([new Rect(0, 0, W, H), -4d]));

        // Zero is a theme asking for a SQUARE action (Graphite's FireCutSize),
        // and it must arrive as zero: treating it as unset put the 16 px
        // chamfer back on every lit action of a theme that had removed it.
        Assert.Equal(0, FireChamferClipConverter.CutFrom([new Rect(0, 0, W, H), 0d]));
        Assert.Equal(4, FireChamferClipConverter.ChamferPoints(W, H, 0).Count);

        // …and the shape follows the cut it is handed.
        Assert.NotEqual(
            FireChamferClipConverter.ChamferPoints(W, H),
            FireChamferClipConverter.ChamferPoints(W, H, 24));
    }
}
