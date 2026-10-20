using Avalonia;
using Avalonia.Media;

namespace DotCharter.Tone.UI.Controls.Fire;

/// <summary>What a progress bar MEANS. Colour is semantic on this layer:
/// violet is progress, amber needs attention, green is good, red is failure.</summary>
public enum FireProgressTone
{
    /// <summary>The default — this is progress.</summary>
    Accent,

    /// <summary>Good / complete.</summary>
    Success,

    /// <summary>Needs attention.</summary>
    Warning,

    /// <summary>Failing.</summary>
    Danger
}

/// <summary>
/// The Fire design system's progress bar, in both of the spec's forms:
/// <b>continuous</b> (a 6 px track at the shared 10 %-white token under a
/// violet gradient fill with a glow) and <b>segmented</b>
/// (<see cref="Segments"/> children, 3 px tall, 5 px apart, filled ones solid
/// accent + glow).
///
/// <para><b>This EXTENDS <c>ProgressBar.FireMeter</c>; it does not replace
/// it.</b> That class skin stays exactly as it is, and stays the right answer
/// for the ratios already bound to it in list rows (technique clean rate,
/// weak-spot miss rate, goal progress, mastery): it repaints Fluent's
/// <c>PART_Indicator</c> with <c>Foreground</c> and therefore keeps the PART
/// contract, indeterminate mode and the accessibility peer for free. What a
/// class skin CANNOT do is the two things this control exists for — a GRADIENT
/// fill and a GLOW both need to reach the indicator's own Border, which means a
/// template override, and the segmented variant is not a ProgressBar at all.
/// Rule of thumb: a row ratio is a <c>ProgressBar.FireMeter</c>; a lit,
/// emphasised or stepped bar is a <see cref="FireProgress"/>.</para>
///
/// <para>Both forms gate their own repaints: the continuous one on the ratio,
/// the segmented one on the FILLED COUNT, so a session bar bound to a live
/// figure only invalidates when a step actually lights.</para>
/// </summary>
public sealed class FireProgress : FireDrawnControl
{
    /// <summary>The ratio, 0..1.</summary>
    public static readonly StyledProperty<double> ValueProperty =
        AvaloniaProperty.Register<FireProgress, double>(nameof(Value));

    /// <summary>0 (the default) is the continuous bar; N ≥ 1 is the stepped
    /// variant with N children.</summary>
    public static readonly StyledProperty<int> SegmentsProperty =
        AvaloniaProperty.Register<FireProgress, int>(nameof(Segments));

    /// <summary>Track height of the continuous bar.</summary>
    public static readonly StyledProperty<double> TrackHeightProperty =
        AvaloniaProperty.Register<FireProgress, double>(
            nameof(TrackHeight), FireProgressGeometry.TrackHeight);

    /// <summary>Gap between segments of the stepped variant.</summary>
    public static readonly StyledProperty<double> SegmentGapProperty =
        AvaloniaProperty.Register<FireProgress, double>(
            nameof(SegmentGap), FireProgressGeometry.SegmentGap);

    public static readonly StyledProperty<FireProgressTone> ToneProperty =
        AvaloniaProperty.Register<FireProgress, FireProgressTone>(nameof(Tone));

    private int _filled;

    public double Value { get => GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public int Segments { get => GetValue(SegmentsProperty); set => SetValue(SegmentsProperty, value); }

    public double TrackHeight
    {
        get => GetValue(TrackHeightProperty);
        set => SetValue(TrackHeightProperty, value);
    }

    public double SegmentGap
    {
        get => GetValue(SegmentGapProperty);
        set => SetValue(SegmentGapProperty, value);
    }

    public FireProgressTone Tone { get => GetValue(ToneProperty); set => SetValue(ToneProperty, value); }

    /// <summary>How many segments are filled — the stepped variant's repaint
    /// gate, and what a caller reads back for "2 of 5".</summary>
    internal int FilledSegments => _filled;

    private bool IsSegmented => Segments > 0;

    private double BarHeight => IsSegmented ? FireProgressGeometry.SegmentHeight : TrackHeight;

    protected override Size MeasureOverride(Size availableSize)
        => new(double.IsInfinity(availableSize.Width) || double.IsNaN(availableSize.Width)
                ? FireProgressGeometry.MinimumTrackWidth
                : availableSize.Width,
            BarHeight);

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        var width = Bounds.Width;
        if (width <= 0) return;

        var height = Math.Min(BarHeight, Bounds.Height);
        if (height <= 0) return;

        var radius = height / 2.0;
        var opacity = FireUnitState.OpacityFor(this);
        var track = FireUnitState.ColorFrom(this, "FireSurfaceTrack");
        var accentColor = FireUnitState.ColorFrom(this, ToneColorKey());
        var top = (Bounds.Height - height) / 2.0;

        if (IsSegmented)
        {
            RenderSegments(context, width, height, radius, top, track, accentColor, opacity);
            return;
        }

        // Continuous: the track, then the glow, then the violet gradient fill.
        context.DrawRectangle(Solid(track, opacity), null,
            new Rect(0, top, width, height), radius, radius);

        var fillWidth = FireProgressGeometry.FillWidth(Value, width);
        if (fillWidth <= 0) return;

        var fillRect = new Rect(0, top, fillWidth, height);
        context.DrawRectangle(Solid(FireUnitState.At(accentColor, 0.32 * opacity), 1.0), null,
            fillRect.Inflate(FireProgressGeometry.GlowSpread / 2.0),
            radius + FireProgressGeometry.GlowSpread / 2.0,
            radius + FireProgressGeometry.GlowSpread / 2.0);

        if (FillBrush() is { } gradient)
        {
            using (context.PushOpacity(opacity))
                context.DrawRectangle(gradient, null, fillRect, radius, radius);
        }
        else
        {
            context.DrawRectangle(Solid(accentColor, opacity), null, fillRect, radius, radius);
        }
    }

    /// <summary>
    /// The continuous fill's gradient, or null when the bar paints a flat
    /// signal colour instead. The default accent tone is the spec's "fill
    /// gradient violet" and comes from the dictionary; a SEMANTIC tone is flat,
    /// because an amber bar that also graded to violet would say two things.
    /// <para>A method rather than an inline lookup so a test can ask the CONTROL
    /// what it will actually paint with — asking the dictionary directly only
    /// proves a gradient token exists, not that the bar uses it.</para>
    /// </summary>
    internal IBrush? FillBrush()
        => Tone == FireProgressTone.Accent ? FireUnitState.BrushFrom(this, "FireAccentFill") : null;

    private void RenderSegments(
        DrawingContext context, double width, double height, double radius, double top,
        Color track, Color accent, double opacity)
    {
        var count = Segments;
        for (var i = 0; i < count; i++)
        {
            var bounds = FireProgressGeometry
                .SegmentBounds(i, count, width, height, SegmentGap)
                .Translate(new Vector(0, top));
            if (bounds.Width <= 0) continue;

            if (i < _filled)
            {
                context.DrawRectangle(Solid(FireUnitState.At(accent, 0.35 * opacity), 1.0), null,
                    bounds.Inflate(FireProgressGeometry.GlowSpread / 2.0),
                    radius + FireProgressGeometry.GlowSpread / 2.0,
                    radius + FireProgressGeometry.GlowSpread / 2.0);
                context.DrawRectangle(Solid(accent, opacity), null, bounds, radius, radius);
            }
            else
            {
                context.DrawRectangle(Solid(track, opacity), null, bounds, radius, radius);
            }
        }
    }

    private string ToneColorKey() => Tone switch
    {
        FireProgressTone.Success => "FireSuccess",
        FireProgressTone.Warning => "FireWarning",
        FireProgressTone.Danger => "FireDanger",
        _ => "FireAccent"
    };

    private static SolidColorBrush Solid(Color color, double opacity)
        => new(FireUnitState.At(color, opacity * (color.A / 255.0)));

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        var p = change.Property;

        if (p == SegmentsProperty || p == TrackHeightProperty) InvalidateMeasure();

        if (p == ValueProperty)
        {
            Refill();
            return;
        }

        if (p == SegmentsProperty)
        {
            Refill(force: true);
        }
        else if (p == TrackHeightProperty || p == SegmentGapProperty || p == ToneProperty ||
                 p == FireUnitState.IsBypassedProperty || p == IsEffectivelyEnabledProperty)
        {
            Repaint();
        }
    }

    /// <summary>
    /// The repaint gate. The segmented variant only changes on screen when a
    /// STEP lights, so a value that moves inside one step must not invalidate;
    /// the continuous bar moves by pixels, so it always does.
    /// </summary>
    private void Refill(bool force = false)
    {
        var filled = FireProgressGeometry.FilledSegments(Value, Segments);
        var changed = filled != _filled;
        _filled = filled;

        if (force || !IsSegmented || changed) Repaint();
    }
}
