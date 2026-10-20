namespace DotCharter.UI.Controls;

/// <summary>
///     The pixel size a natively-composed surface must allocate, together with
///     the DIP size its <c>CompositionSurfaceVisual</c> must declare so the two
///     describe the SAME rectangle.
/// </summary>
/// <param name="PixelWidth">Swapchain width in physical pixels.</param>
/// <param name="PixelHeight">Swapchain height in physical pixels.</param>
/// <param name="DipWidth">
///     <see cref="PixelWidth" /> expressed back in the visual's own units, i.e.
///     <c>PixelWidth / pixelScale</c> EXACTLY — not the control's raw
///     <c>Bounds.Width</c>.
/// </param>
/// <param name="DipHeight">The same for the height.</param>
public readonly record struct ComposedSurfaceSize(
    int PixelWidth,
    int PixelHeight,
    double DipWidth,
    double DipHeight);

/// <summary>
///     <b>Pixel-grid alignment for the two natively-composed surfaces (the 2D
///     timeline and the 3D highway).</b>
///
///     <para>
///         A composed surface is a bitmap of <c>PixelWidth x PixelHeight</c>
///         texels that the compositor draws into the rectangle
///         <c>visual.Size x (every transform above it) x RenderScaling</c>. It
///         is sampled 1:1 — i.e. it is SHARP — only while that destination
///         rectangle is exactly <c>PixelWidth x PixelHeight</c> device pixels
///         AND sits at a whole-pixel origin. Any other destination is a
///         bilinear resample of the whole surface, which softens text far more
///         visibly than it softens geometry.
///     </para>
///
///     <para>
///         <b>Sizing a surface as
///         <c>ceil(Bounds x scale)</c> while declaring <c>visual.Size =
///         Bounds</c> is only correct by accident.</b> With
///         <c>scale == TopLevel.RenderScaling</c>, Avalonia's layout rounding
///         (<c>UseLayoutRounding</c>, on by default, rounding against
///         <c>TopLevel.LayoutScaling</c>) guarantees that <c>Bounds x
///         RenderScaling</c> is already a whole number of pixels at a whole-pixel
///         origin — so the <c>ceil</c> is a no-op and the destination matches.
///         A global UI scale (<b>Auto</b> is the default) puts a
///         fractional <c>ScaleTransform</c> on <c>MainWindow</c>'s
///         <c>RootScaler</c> between those controls and the window, and layout
///         rounding knows nothing about it: the inner DIPs are rounded against
///         <c>RenderScaling</c> alone, so <c>Bounds x uiScale x RenderScaling</c>
///         is fractional in both size and origin again. <c>ceil</c> then
///         allocates up to a pixel MORE than the destination and the compositor
///         resamples the entire timeline — a soft, banded chart editor.
///     </para>
///
///     <para>
///         <see cref="VisualPixelScale" /> fixed the RESOLUTION half of this
///         (rendering at 1/uiScale and being upscaled); this type fixes the
///         ALIGNMENT half. They are independent: a surface can carry the right
///         number of pixels and still be resampled.
///     </para>
///
///     <para>Pure and static so the arithmetic is unit-testable without a GPU —
///     <c>ComposedSurfaceGeometryTests</c>. The fault renders perfectly and
///     throws nothing, so arithmetic is the only thing that can assert it.</para>
/// </summary>
public static class ComposedSurfaceGeometry
{
    /// <summary>
    ///     The surface size for a control of <paramref name="dipWidth" /> x
    ///     <paramref name="dipHeight" /> local units composited at
    ///     <paramref name="pixelScale" /> physical pixels per local unit (i.e.
    ///     <see cref="VisualPixelScale.For" />).
    ///     <para>
    ///         Rounds to the NEAREST pixel rather than up: the returned
    ///         <see cref="ComposedSurfaceSize.DipWidth" /> is what the visual
    ///         declares, so rounding up would make the surface claim up to a
    ///         whole pixel of the control's neighbour, while rounding to nearest
    ///         keeps the worst case at half a pixel — the same error budget
    ///         layout rounding already spends everywhere else.
    ///     </para>
    ///     <para>
    ///         A non-finite or non-positive scale is treated as 1.0 and a
    ///         degenerate size as one pixel: every caller feeds this to a
    ///         swapchain, where a zero or a NaN is a hard failure and a wrong
    ///         size is a blur.
    ///     </para>
    /// </summary>
    public static ComposedSurfaceSize Measure(double dipWidth, double dipHeight, double pixelScale)
    {
        if (!double.IsFinite(pixelScale) || pixelScale <= 0) pixelScale = 1.0;

        var pixelWidth = ToPixels(dipWidth, pixelScale);
        var pixelHeight = ToPixels(dipHeight, pixelScale);

        // The DIP size is DERIVED from the pixel size, never the other way
        // round. That is the whole invariant: DipWidth * pixelScale ==
        // PixelWidth to the last bit, so the compositor's destination rectangle
        // is exactly as many pixels as the surface has texels.
        return new ComposedSurfaceSize(
            pixelWidth,
            pixelHeight,
            pixelWidth / pixelScale,
            pixelHeight / pixelScale);
    }

    /// <summary>
    ///     The translation, in the surface visual's OWN units, that moves it
    ///     onto the physical pixel grid — at most half a pixel, and 0 when the
    ///     origin is already whole.
    ///     <para>
    ///         <paramref name="deviceOrigin" /> is one axis of the control's
    ///         origin in physical pixels
    ///         (<see cref="VisualPixelScale.DeviceOrigin" />);
    ///         <paramref name="pixelScale" /> converts back into the visual's
    ///         units, which is what <c>CompositionVisual.Offset</c> is expressed
    ///         in.
    ///     </para>
    /// </summary>
    public static double SnapOffset(double deviceOrigin, double pixelScale)
    {
        if (!double.IsFinite(deviceOrigin)) return 0;
        if (!double.IsFinite(pixelScale) || pixelScale <= 0) return 0;

        var fraction = deviceOrigin - Math.Round(deviceOrigin, MidpointRounding.AwayFromZero);
        var snap = -fraction / pixelScale;
        return double.IsFinite(snap) ? snap : 0;
    }

    private static int ToPixels(double dip, double pixelScale)
    {
        if (!double.IsFinite(dip) || dip <= 0) return 1;
        var pixels = Math.Round(dip * pixelScale, MidpointRounding.AwayFromZero);
        if (!double.IsFinite(pixels)) return 1;
        return (int)Math.Clamp(pixels, 1, 1 << 16);
    }
}
