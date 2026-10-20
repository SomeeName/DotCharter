using Avalonia;
using Avalonia.Controls;

namespace DotCharter.UI.Controls;

/// <summary>
///     <b>The DIP→pixel factor for a visual that may sit under a layout
///     transform — which, in this app, every visual does.</b>
///
///     <para>
///         The two natively-composed surfaces size their swapchain as
///         <c>Bounds x TopLevel.RenderScaling</c>. That is right only while
///         nothing between the control and the window scales, and the global
///         UI-scale setting is exactly such a thing:
///         <c>MainWindow</c>'s <c>RootScaler</c>
///         (<see cref="Views.Shell.WindowUiScale.ScalerName" />) is a
///         <c>LayoutTransformControl</c> wrapping the WHOLE shell, so a
///         descendant's <c>Bounds</c> is in the transform's INNER coordinate
///         space while the pixels it finally occupies are
///         <c>Bounds x uiScale x RenderScaling</c>.
///     </para>
///
///     <para>
///         <b>So the 3D highway and the 2D timeline rendered at 1/uiScale of
///         the resolution they were composited at and the compositor upscaled
///         the difference.</b> That shipped with the UI-scale setting and was
///         invisible because the setting defaulted to 1.0 and was a slider
///         nobody moved; with <b>Auto</b> as the default it puts every
///         1440p user on it. It is the same class of fault
///         <c>Preview3DComposedCanvas</c> already documents for a DPI change —
///         "went on rendering at the old pixel size and the compositor rescaled
///         it, i.e. a blurry highway" — with the layout transform as the second
///         input nobody was reading.
///     </para>
///
///     <para>
///         <b>This factor alone does not make a surface sharp, and saying so
///         was the gap that left the chart editor blurry through v0.55.1.</b>
///         It answers how many pixels to RENDER; where those pixels LAND is
///         <see cref="ComposedSurfaceGeometry" />'s job. A fractional shell
///         scale also breaks the whole-pixel size and origin that Avalonia's
///         layout rounding guarantees, and a surface composited onto a
///         fractional rectangle is bilinearly resampled however many pixels it
///         carries.
///     </para>
///
///     <para>
///         The accumulated matrix is read rather than
///         <c>UiScaleController.Scale</c> multiplied in, deliberately: it is
///         correct for ANY intervening transform (the rig's
///         <c>RigBoardZoomHost</c>, <c>RigScaleToFit</c>, the launch hero's
///         scaler) without each of them having to publish itself, and it cannot
///         drift from what the compositor actually does.
///     </para>
/// </summary>
public static class VisualPixelScale
{
    /// <summary>
    ///     Physical pixels per <paramref name="visual" />-local unit: the
    ///     window's <c>RenderScaling</c> folded with every layout/render
    ///     transform between the visual and its top level.
    ///     <para>
    ///         Answers 1.0 for a detached visual and for a headless top level
    ///         with no usable transform — the callers multiply a size by this,
    ///         so a zero or a NaN would be a zero-sized swapchain rather than a
    ///         wrong-sized one.
    ///     </para>
    /// </summary>
    public static double For(Visual? visual)
    {
        if (visual is null) return 1.0;
        var topLevel = TopLevel.GetTopLevel(visual);
        if (topLevel is null) return 1.0;

        var renderScaling = topLevel.RenderScaling;
        if (!double.IsFinite(renderScaling) || renderScaling <= 0) renderScaling = 1.0;

        return renderScaling * LayoutScaleTo(visual, topLevel);
    }

    /// <summary>
    ///     The visual's top-left corner in PHYSICAL PIXELS of the top level —
    ///     every transform between the two folded in, then multiplied by
    ///     <c>RenderScaling</c>.
    ///     <para>
    ///         Null for a detached visual or when the transform cannot be
    ///         resolved, so a caller can tell "the origin is 0" from "there is
    ///         no origin yet" and leave its surface where it is rather than
    ///         nudging it against a bogus number.
    ///     </para>
    ///     <para>
    ///         Exists for <see cref="ComposedSurfaceGeometry.SnapOffset" />: a
    ///         composed surface at a fractional pixel origin is resampled, and
    ///         under a fractional shell scale a layout-rounded INNER offset is
    ///         exactly that.
    ///     </para>
    /// </summary>
    public static Point? DeviceOrigin(Visual? visual)
    {
        if (visual is null) return null;
        var topLevel = TopLevel.GetTopLevel(visual);
        if (topLevel is null) return null;
        if (visual.TransformToVisual(topLevel) is not { } matrix) return null;
        if (!double.IsFinite(matrix.M31) || !double.IsFinite(matrix.M32)) return null;

        var renderScaling = topLevel.RenderScaling;
        if (!double.IsFinite(renderScaling) || renderScaling <= 0) renderScaling = 1.0;

        return new Point(matrix.M31 * renderScaling, matrix.M32 * renderScaling);
    }

    /// <summary>
    ///     The transform-only half — the factor contributed by the visual tree
    ///     between <paramref name="visual" /> and <paramref name="ancestor" />,
    ///     with <c>RenderScaling</c> left out. Exposed because it is the half a
    ///     headless test can assert: <c>RenderScaling</c> is the platform's
    ///     answer and is 1.0 on the headless backend either way.
    /// </summary>
    public static double LayoutScaleTo(Visual? visual, Visual? ancestor)
    {
        if (visual is null || ancestor is null || ReferenceEquals(visual, ancestor)) return 1.0;
        if (visual.TransformToVisual(ancestor) is not { } matrix) return 1.0;

        // The magnitude of the transformed unit-X vector. A rotation would make
        // X and Y disagree; nothing in this app rotates a render surface's
        // ancestry, and taking X alone keeps a square swapchain square.
        var scale = Math.Sqrt(matrix.M11 * matrix.M11 + matrix.M12 * matrix.M12);
        return double.IsFinite(scale) && scale > 0.01 ? scale : 1.0;
    }
}
