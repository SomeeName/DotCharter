using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using DotCharter.Tone.UI.Controls.Dsp;

namespace DotCharter.Tone.UI.Controls.Fire;

/// <summary>
///     Lays its child out at the child's NATURAL size, then scales the whole
///     thing once to fit the space it was given — the way a plugin scales
///     its own window (tone-rig design §3.3). Grows up to
///     <see cref="MaxScale" /> (a pedal fills a tall dock), never below
///     <see cref="RigArtwork.MinEditorScale" />: past that floor the content
///     keeps its size and the host scrolls, because shrinking labels past
///     reading size is worse than a scrollbar.
///
///     <para>
///         A <see cref="LayoutTransformControl" /> rather than a render
///         transform so the layout really reflows and hit-testing stays exact.
///         The height limit comes from <see cref="TargetHeight" /> (bind the
///         host's viewport height) because a ScrollViewer hands its content
///         infinite height and the fit would otherwise never engage.
///     </para>
/// </summary>
public sealed class RigScaleToFit : LayoutTransformControl
{
    public static readonly StyledProperty<double> TargetHeightProperty =
        AvaloniaProperty.Register<RigScaleToFit, double>(nameof(TargetHeight), double.PositiveInfinity);

    /// <summary>The height to fit into; infinity (the default) fits the width only.</summary>
    public double TargetHeight { get => GetValue(TargetHeightProperty); set => SetValue(TargetHeightProperty, value); }

    public static readonly StyledProperty<double> MaxScaleProperty =
        AvaloniaProperty.Register<RigScaleToFit, double>(nameof(MaxScale), RigArtwork.MaxEditorScale);

    /// <summary>
    ///     The growth ceiling. Defaults to <see cref="RigArtwork.MaxEditorScale" />,
    ///     so a host that sets nothing behaves exactly as this control did
    ///     before the property existed. The rig's module-editor faces bind it to
    ///     the breakpointed <c>FireRigFaceMaxScale</c> token, which is the only
    ///     way the ceiling can know it is on a 4K panel: the dock row grows ~5x
    ///     in AREA between 1920x1080 and 3840x2160 and a fixed ceiling leaves the
    ///     face in a corner of it.
    /// </summary>
    public double MaxScale { get => GetValue(MaxScaleProperty); set => SetValue(MaxScaleProperty, value); }

    public static readonly StyledProperty<double> MinScaleProperty =
        AvaloniaProperty.Register<RigScaleToFit, double>(nameof(MinScale), RigArtwork.MinEditorScale);

    /// <summary>
    ///     The shrink floor. Defaults to <see cref="RigArtwork.MinEditorScale" />, below
    ///     which drawn labels stop shrinking and the host scrolls. The generated
    ///     faceplate passes a lower one: its labels are part of the picture, and on
    ///     the AmpliTube layout's stage a face cut off at the bottom is worse than a
    ///     smaller one (see <see cref="RigArtwork.FitScale(double,double,double,double)" />).
    /// </summary>
    public double MinScale { get => GetValue(MinScaleProperty); set => SetValue(MinScaleProperty, value); }

    /// <summary>The scale in force after the last measure — the test seam.</summary>
    public double Scale { get; private set; } = 1.0;

    static RigScaleToFit() => AffectsMeasure<RigScaleToFit>(TargetHeightProperty, MaxScaleProperty, MinScaleProperty);

    protected override Size MeasureOverride(Size availableSize)
    {
        if (Child is null) return base.MeasureOverride(availableSize);

        // The child's natural size, unconstrained.
        Child.Measure(Size.Infinity);
        var natural = Child.DesiredSize;

        var height = double.IsFinite(TargetHeight) && TargetHeight > 0
            ? TargetHeight
            : availableSize.Height;
        var byWidth = RigArtwork.FitScale(natural.Width, availableSize.Width, MaxScale, MinScale);
        var byHeight = RigArtwork.FitScale(natural.Height, height, MaxScale, MinScale);
        var scale = Math.Min(byWidth, byHeight);

        if (Math.Abs(scale - Scale) > 0.001)
        {
            Scale = scale;
            LayoutTransform = Math.Abs(scale - 1) > 0.001 ? new ScaleTransform(scale, scale) : null;
        }

        return base.MeasureOverride(availableSize);
    }
}
