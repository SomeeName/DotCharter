using Avalonia;
using Avalonia.Media;

namespace DotCharter.Tone.UI.Controls.Fire;

/// <summary>
/// The Fire design system's per-string tuner meter: a sunken track with the
/// in-tune target band marked out at its centre and a single lit needle showing
/// how far off the string is, flat to the left and sharp to the right.
///
/// <para><b>Why this is a new primitive and not a variant of something.</b>
/// <see cref="FireLevelMeter" /> is a MAGNITUDE meter — it fills up from a floor
/// and colours each bar by the dB that bar represents; it has no centre, no
/// signed direction and no "nothing has arrived yet" that differs from zero.
/// <c>ProgressBar.FireMeter</c> is a 0..1 RATIO that fills from an edge, which
/// is the one shape a tuner must not have: the needle is anchored to the middle
/// and the thing being shown is a deviation either side of it, with a tolerance
/// band drawn on the track. Both would have to be lied to about their meaning to
/// draw this, and the lie is exactly the kind that survives review because the
/// picture still looks plausible.</para>
///
/// <para>Everything it draws comes from the pure <see cref="FireTunerScale" />,
/// whose scale is derived from the REAL tuner's in-tune tolerance
/// (<see cref="InTuneCents" />, bound from <c>GuidedTuningCheck.InTuneCents</c>)
/// rather than declared here.</para>
///
/// <para><b>This is fed from audio.</b> <see cref="Cents" /> changes far faster
/// than the picture does, so the control repaints only when the needle really
/// moves — gated on the rendered needle POSITION quantized to a pixel, plus the
/// band it is in. Every invalidation goes through one counted path so the gate
/// is testable.</para>
///
/// <para>No literal colour: every brush is resolved from the Fire dictionary by
/// key, so a token correction moves this meter with everything else.</para>
/// </summary>
public sealed class FireTunerMeter : FireDrawnControl
{
    /// <summary>Signed deviation from the target, in cents. Negative is flat.
    /// Ignored entirely while <see cref="HasReading" /> is false.</summary>
    public static readonly StyledProperty<double> CentsProperty =
        AvaloniaProperty.Register<FireTunerMeter, double>(nameof(Cents));

    /// <summary>Has this string been heard at all yet? False draws the track and
    /// the band but NO needle — "pluck it", which is a different statement from
    /// "it is zero cents off".</summary>
    public static readonly StyledProperty<bool> HasReadingProperty =
        AvaloniaProperty.Register<FireTunerMeter, bool>(nameof(HasReading));

    /// <summary>The judge's own in-tune tolerance. Sets the band's width and,
    /// through <see cref="FireTunerScale.ScaleToBandRatio" />, the whole
    /// scale.</summary>
    public static readonly StyledProperty<double> InTuneCentsProperty =
        AvaloniaProperty.Register<FireTunerMeter, double>(
            nameof(InTuneCents), FireTunerScale.DefaultInTuneCents);

    public static readonly StyledProperty<double> TrackHeightProperty =
        AvaloniaProperty.Register<FireTunerMeter, double>(nameof(TrackHeight), DefaultTrackHeight);

    /// <summary>The mockup's row: a 22 px well with a 4 px needle inset 3 px.</summary>
    public const double DefaultTrackHeight = 22.0;

    public const double NeedleWidth = 4.0;
    public const double NeedleInset = 3.0;
    public const double TrackCornerRadius = 6.0;
    public const double NeedleCornerRadius = 2.0;

    /// <summary>How wide the glow pass around the needle is. A
    /// <c>DrawingContext</c> has no blur, so "lit" is layered: a wider, fainter
    /// pass under the solid one — the same trick <see cref="FireKnob" />'s arc
    /// uses.</summary>
    public const double NeedleGlowSpread = 3.0;

    public const double NeedleGlowAlpha = 0.30;

    private int _needlePixel = -1;
    private FireTunerBand _band = FireTunerBand.NoReading;

    public FireTunerMeter()
        : base(followsThemeVariant: true)
    {
    }

    public double Cents { get => GetValue(CentsProperty); set => SetValue(CentsProperty, value); }
    public bool HasReading { get => GetValue(HasReadingProperty); set => SetValue(HasReadingProperty, value); }

    public double InTuneCents
    {
        get => GetValue(InTuneCentsProperty);
        set => SetValue(InTuneCentsProperty, value);
    }

    public double TrackHeight
    {
        get => GetValue(TrackHeightProperty);
        set => SetValue(TrackHeightProperty, value);
    }

    /// <summary>What the meter is currently SAYING, so a caller (the row's
    /// right-hand state text) can say the same thing without re-deriving the
    /// tolerance.</summary>
    public FireTunerBand Band => FireTunerScale.BandFor(HasReading, Cents, InTuneCents);

    protected override Size MeasureOverride(Size availableSize)
    {
        // Width is whatever the row gives it — the meter is the flexible column
        // between the fixed note-name and state columns. An infinite available
        // width (an unconstrained StackPanel) must not become an infinite
        // desired width, so it falls back to its own height.
        var width = double.IsFinite(availableSize.Width) ? availableSize.Width : TrackHeight;
        return new Size(width, TrackHeight);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        var width = Bounds.Width;
        var height = Math.Min(Bounds.Height, TrackHeight);
        if (width <= 0 || height <= 0) return;

        var opacity = FireUnitState.OpacityFor(this);

        // The well.
        var track = new Rect(0, 0, width, height);
        context.DrawRectangle(
            Brush("FireSurfaceSunken", opacity),
            Paint.Pen(Colour("FireBorderSeam", opacity), 1),
            track, TrackCornerRadius, TrackCornerRadius);

        // The target band — where the string is ACCEPTED. Always drawn, even
        // with no reading: it is the thing the player is aiming at.
        var bandX = FireTunerScale.BandStartFraction * width;
        var bandW = FireTunerScale.BandWidthFraction * width;
        var band = new Rect(bandX, 1, bandW, Math.Max(0, height - 2));
        context.DrawRectangle(Brush("FireSuccessTint", opacity), null, band);
        var edge = Paint.Pen(Colour("FireSuccessBorder", opacity), 1);
        context.DrawLine(edge, new Point(bandX, 1), new Point(bandX, height - 1));
        context.DrawLine(edge, new Point(bandX + bandW, 1), new Point(bandX + bandW, height - 1));

        if (!HasReading) return;

        // The needle.
        var band2 = FireTunerScale.BandFor(true, Cents, InTuneCents);
        var color = Paint.Color(this, FireTunerScale.NeedleTokenKeyFor(band2));
        var fraction = FireTunerScale.NeedleFraction(Cents, InTuneCents);
        var centre = Math.Clamp(fraction * width, NeedleWidth / 2, width - NeedleWidth / 2);
        var top = NeedleInset;
        var bottom = Math.Max(top, height - NeedleInset);

        var glow = new Rect(
            centre - NeedleWidth / 2 - NeedleGlowSpread, top - NeedleGlowSpread / 2,
            NeedleWidth + 2 * NeedleGlowSpread, Math.Max(0, bottom - top + NeedleGlowSpread));
        context.DrawRectangle(
            Paint.Brush(FireUnitState.At(color, NeedleGlowAlpha * opacity)), null,
            glow, TrackCornerRadius, TrackCornerRadius);

        var needle = new Rect(centre - NeedleWidth / 2, top, NeedleWidth, Math.Max(0, bottom - top));
        context.DrawRectangle(
            Paint.Brush(FireUnitState.At(color, opacity)), null,
            needle, NeedleCornerRadius, NeedleCornerRadius);
    }

    // Colours by token and brushes/pens by colour, remembered across paints —
    // this control would resolve every token TWICE per brush (the second walk
    // only to read the alpha) and allocate the brush, on six live instances.
    // See FirePaintCache.
    /// <summary>The token's colour at <paramref name="opacity" /> times its own
    /// translucency — one resolve per token, not two.</summary>
    private Color Colour(string key, double opacity)
    {
        var color = Paint.Color(this, key);
        return FireUnitState.At(color, opacity * (color.A / 255.0));
    }

    private IBrush Brush(string key, double opacity) => Paint.Brush(Colour(key, opacity));

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        var p = change.Property;

        if (p == TrackHeightProperty)
        {
            InvalidateMeasure();
            Repaint();
            return;
        }

        if (p == BoundsProperty)
        {
            // A resize moves the needle's PIXEL without moving its value, so the
            // cached position has to be re-derived rather than compared.
            Restate(force: true);
            return;
        }

        if (p == CentsProperty || p == HasReadingProperty || p == InTuneCentsProperty)
        {
            Restate();
            return;
        }

        if (p == FireUnitState.IsBypassedProperty || p == IsEffectivelyEnabledProperty)
            Repaint();
    }

    /// <summary>Re-derives what is actually drawn and repaints only if it moved.
    /// The needle is compared as a whole PIXEL: cents arrive continuously off a
    /// pitch detector, and a sub-pixel change is not a change to anything the
    /// player can see.</summary>
    private void Restate(bool force = false)
    {
        var band = FireTunerScale.BandFor(HasReading, Cents, InTuneCents);
        var pixel = band == FireTunerBand.NoReading
            ? -1
            : (int)Math.Round(FireTunerScale.NeedleFraction(Cents, InTuneCents) * Bounds.Width);

        if (!force && band == _band && pixel == _needlePixel) return;
        _band = band;
        _needlePixel = pixel;
        Repaint();
    }
}
