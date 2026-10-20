using DotCharter.UI.Controls;

namespace DotCharter.Tests.UI;

/// <summary>
///     <b>The chart editor went soft at v0.55.x because the two composed
///     surfaces described their rectangle twice and the two descriptions stopped
///     agreeing.</b>
///
///     <para>
///         Both sized their swapchain <c>ceil(Bounds x scale)</c> while telling
///         the compositor <c>visual.Size = Bounds</c>. That pair is only
///         equivalent while <c>Bounds x scale</c> is already a whole number of
///         pixels — which Avalonia's layout rounding guaranteed for as long as
///         <c>scale</c> WAS <c>TopLevel.RenderScaling</c>. The interface-scale
///         wave put a fractional <c>ScaleTransform</c> on the shell root and
///         made it the default, and layout rounding does not know about it: the
///         product is fractional again, <c>ceil</c> over-allocates, and the
///         compositor bilinearly resamples the whole timeline. Text is what a
///         resample destroys first.
///     </para>
///
///     <para>
///         The invariant that has to hold, and that these cases assert, is one
///         line: <c>DipSize x pixelScale == PixelSize</c>, exactly, for every
///         combination of OS scaling and shell scale. No GPU can assert it —
///         the fault renders perfectly and throws nothing — so the arithmetic
///         is the test.
///     </para>
/// </summary>
public class ComposedSurfaceGeometryTests
{
    /// <summary>The OS-scaling values the orchestrator checks by eye, times the
    /// shell factors <c>AutoUiScale</c>'s 0.05 grid actually produces on a
    /// 1440p/4K panel. 1.0 x 1.0 is in the set on purpose: the case that was
    /// never broken must stay unbroken.</summary>
    public static TheoryData<double, double> Scales()
    {
        var data = new TheoryData<double, double>();
        foreach (var renderScaling in new[] { 1.0, 1.25, 1.5 })
        {
            foreach (var uiScale in new[] { 1.0, 1.05, 1.15, 1.30, 1.45, 2.0 })
                data.Add(renderScaling, uiScale);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Scales))]
    public void TheDipSizeTimesTheScale_IsExactlyThePixelSize(double renderScaling, double uiScale)
    {
        var pixelScale = renderScaling * uiScale;

        // Deliberately awkward DIP extents: a dock splitter, a star column and
        // a scrollbar leave the timeline on values like these, and they are the
        // ones layout rounding cannot help with once a transform is in the way.
        foreach (var dip in new[] { 1.0, 17.0, 341.0, 720.5, 1279.3, 1920.0 })
        {
            var size = ComposedSurfaceGeometry.Measure(dip, dip + 7.25, pixelScale);

            Assert.Equal(size.PixelWidth, size.DipWidth * pixelScale, 9);
            Assert.Equal(size.PixelHeight, size.DipHeight * pixelScale, 9);
            Assert.True(size.PixelWidth >= 1 && size.PixelHeight >= 1);

            // …and it must still be the size the user asked for, to within the
            // half pixel layout rounding spends anyway.
            Assert.True(Math.Abs(size.DipWidth - dip) <= 0.5 / pixelScale + 1e-9,
                $"{size.DipWidth} is more than half a pixel away from {dip} at scale {pixelScale}.");
        }
    }

    /// <summary>
    ///     The regression, named rather than implied: the shipped arithmetic —
    ///     <c>ceil(bounds x scale)</c> texels drawn into a <c>bounds x scale</c>
    ///     destination — is a resample at a real shell factor, and this case
    ///     would pass vacuously if it were not.
    /// </summary>
    [Fact]
    public void ThePreFixArithmetic_AsksForMorePixelsThanItIsGiven()
    {
        const double bounds = 1279.3;
        const double pixelScale = 1.30;

        var shippedTexels = Math.Ceiling(bounds * pixelScale);
        var shippedDestination = bounds * pixelScale;
        Assert.True(shippedTexels - shippedDestination > 0.25,
            "the pre-fix pair happens to line up for these numbers — pick a case where it does not.");

        var fixedSize = ComposedSurfaceGeometry.Measure(bounds, bounds, pixelScale);
        Assert.Equal(fixedSize.PixelWidth, fixedSize.DipWidth * pixelScale, 9);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-2.0)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void ANonsenseScale_FallsBackToOneRatherThanAZeroSizedSwapchain(double pixelScale)
    {
        var size = ComposedSurfaceGeometry.Measure(640, 480, pixelScale);
        Assert.Equal(640, size.PixelWidth);
        Assert.Equal(480, size.PixelHeight);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-8.0)]
    [InlineData(double.NaN)]
    public void ADegenerateExtent_IsOnePixel_NotZero(double dip)
    {
        var size = ComposedSurfaceGeometry.Measure(dip, dip, 1.25);
        Assert.Equal(1, size.PixelWidth);
        Assert.Equal(1, size.PixelHeight);
    }

    // ---- origin ---------------------------------------------------------------

    /// <summary>A surface whose origin is already whole must not be moved:
    /// the snap is a correction, not a constant bias.</summary>
    [Theory]
    [InlineData(0.0)]
    [InlineData(260.0)]
    [InlineData(-64.0)]
    public void AWholePixelOrigin_IsNotNudged(double deviceOrigin) => Assert.Equal(0.0, ComposedSurfaceGeometry.SnapOffset(deviceOrigin, 1.3), 12);

    /// <summary>The nudge lands the origin ON the grid, stays under half a
    /// pixel, and is expressed in the visual's own units (hence the divide).</summary>
    [Theory]
    [MemberData(nameof(Scales))]
    public void AFractionalOrigin_IsSnappedOntoTheGrid(double renderScaling, double uiScale)
    {
        var pixelScale = renderScaling * uiScale;

        foreach (var origin in new[] { 0.4, 96.2, 124.8, 1023.51, -17.3 })
        {
            var snapLocal = ComposedSurfaceGeometry.SnapOffset(origin, pixelScale);
            var snappedDevice = origin + snapLocal * pixelScale;

            Assert.Equal(Math.Round(snappedDevice), snappedDevice, 9);
            Assert.True(Math.Abs(snapLocal * pixelScale) <= 0.5 + 1e-9,
                $"a {snapLocal * pixelScale} px nudge is a visible move, not a snap.");
        }
    }

    [Theory]
    [InlineData(double.NaN, 1.3)]
    [InlineData(100.5, 0.0)]
    [InlineData(100.5, double.NaN)]
    public void ANonsenseOriginOrScale_LeavesTheSurfaceWhereItIs(double origin, double pixelScale) => Assert.Equal(0.0, ComposedSurfaceGeometry.SnapOffset(origin, pixelScale));
}
