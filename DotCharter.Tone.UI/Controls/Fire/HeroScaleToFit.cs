using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace DotCharter.Tone.UI.Controls.Fire;

/// <summary>
///     Applies <see cref="LaunchHeroScale" /> to its child: measures the child
///     at its NATURAL size and then scales the whole block once so it fills a
///     share of the viewport.
///
///     <para>
///         A <see cref="LayoutTransformControl" /> rather than a
///         <c>Viewbox</c>, and the reason is hit-testing and text. A Viewbox
///         applies a RENDER transform: the content is laid out at one size and
///         painted at another, so every glyph is rasterized small and scaled up
///         (soft at 2x — the hero's whole problem restated) and pointer
///         coordinates go through an inverse transform that Avalonia handles
///         but that nothing else in this screen expects. A layout transform
///         re-runs layout at the new size, so text is laid out and rasterized
///         AT the final size and the START button's chamfer, the profile rows
///         and the status pills are hit-tested exactly. It is also the
///         mechanism this app already uses for the same job three times over
///         (<c>RootScaler</c>, <c>RigScaleToFit</c>,
///         <c>RigBoardZoomHost</c>).
///     </para>
///
///     <para>
///         <b><see cref="Viewport" /> must be bound, and to the SCROLLVIEWER's
///         bounds, not the window's.</b> The hero lives inside
///         <c>LaunchBodyScroll</c>, and a <c>ScrollViewer</c> offers its content
///         infinite height — so <c>availableSize</c> here carries no usable
///         height at all and a fill computed from it would never engage
///         vertically. <c>RigScaleToFit</c> carries the same note for the same
///         reason.
///     </para>
///
///     <para>
///         Binding the scroll viewer's bounds also makes this compose correctly
///         with the global interface scale instead of multiplying against it:
///         that viewport is measured INSIDE <c>MainWindow</c>'s
///         <c>RootScaler</c>, so at 150 % global scale it reports 1/1.5 of the
///         window's height and this control grows by 1/1.5 less. The hero ends
///         up covering the same share of the SCREEN either way, which is what
///         "a share of the window" has to mean.
///     </para>
/// </summary>
public sealed class HeroScaleToFit : LayoutTransformControl
{
    /// <summary>The space to fill a share of. Bind the hosting scroll viewer's
    /// <c>Bounds</c> — see the type remarks for why it cannot be inferred.</summary>
    public static readonly StyledProperty<Rect> ViewportProperty =
        AvaloniaProperty.Register<HeroScaleToFit, Rect>(nameof(Viewport));

    /// <summary>The space to fill a share of.</summary>
    public Rect Viewport
    {
        get => GetValue(ViewportProperty);
        set => SetValue(ViewportProperty, value);
    }

    /// <summary>The factor in force after the last measure — the test seam, and
    /// the only way to assert this without reading a matrix back off the
    /// transform.</summary>
    public double Scale { get; private set; } = LaunchHeroScale.MinScale;

    static HeroScaleToFit() => AffectsMeasure<HeroScaleToFit>(ViewportProperty);

    /// <inheritdoc />
    protected override Size MeasureOverride(Size availableSize)
    {
        if (Child is null) return base.MeasureOverride(availableSize);

        // The child's natural size, unconstrained — this is what the fixed
        // pixel authoring actually asks for.
        Child.Measure(Size.Infinity);
        var natural = Child.DesiredSize;

        // Viewport first; availableSize is the fallback for a host that has not
        // bound one (and its Height is +inf under a ScrollViewer, which
        // LaunchHeroScale.For treats as "this axis does not bind").
        var viewport = Viewport.Width > 0 || Viewport.Height > 0
            ? Viewport.Size
            : availableSize;

        var scale = LaunchHeroScale.For(natural, viewport);
        if (Math.Abs(scale - Scale) > 0.001)
        {
            Scale = scale;
            LayoutTransform = Math.Abs(scale - 1) > 0.001 ? new ScaleTransform(scale, scale) : null;
        }

        return base.MeasureOverride(availableSize);
    }
}
