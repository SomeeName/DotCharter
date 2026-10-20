using Avalonia;
using Avalonia.Media;

namespace DotCharter.Tone.UI.Controls.Fire;

/// <summary>
/// The Fire design system's level meter: a row of 4 px bars 2 px apart, heights
/// ramping 5 → 14 px, lit up to the current level. <b>Green below −6 dB, amber
/// −6 … −2, red above</b>, with the unlit remainder at the shared 10 %-white
/// track token.
///
/// <para>The thresholds live in the pure <see cref="FireLevelScale"/> and each
/// bar's colour is decided from the dB IT represents, not from its index — the
/// difference only shows up when the scale changes, which is exactly when a
/// decorative meter starts lying.</para>
///
/// <para><b>This is a hot path.</b> It is fed from audio, so
/// <see cref="LevelDb"/> changes far faster than the picture does: the control
/// repaints only when the number of LIT BARS actually changes
/// (<see cref="FireLevelScale.LitSegments"/>). Everything else it draws is
/// static.</para>
///
/// <para>Bypass and disabled come from the inherited
/// <see cref="FireUnitState"/> attached properties — the same single switch the
/// unit card sets for its knobs, so a bypassed unit cannot end up with a live
/// meter.</para>
/// </summary>
public sealed class FireLevelMeter : FireDrawnControl
{
    /// <summary>The level to display, in dBFS.
    /// <see cref="double.NegativeInfinity"/> is silence.</summary>
    public static readonly StyledProperty<double> LevelDbProperty =
        AvaloniaProperty.Register<FireLevelMeter, double>(
            nameof(LevelDb), double.NegativeInfinity);

    public static readonly StyledProperty<double> MinimumDbProperty =
        AvaloniaProperty.Register<FireLevelMeter, double>(
            nameof(MinimumDb), FireLevelScale.DefaultMinimumDb);

    public static readonly StyledProperty<double> MaximumDbProperty =
        AvaloniaProperty.Register<FireLevelMeter, double>(
            nameof(MaximumDb), FireLevelScale.DefaultMaximumDb);

    public static readonly StyledProperty<int> SegmentCountProperty =
        AvaloniaProperty.Register<FireLevelMeter, int>(
            nameof(SegmentCount), FireLevelScale.DefaultSegmentCount);

    public static readonly StyledProperty<double> BarWidthProperty =
        AvaloniaProperty.Register<FireLevelMeter, double>(
            nameof(BarWidth), FireLevelScale.DefaultBarWidth);

    public static readonly StyledProperty<double> BarGapProperty =
        AvaloniaProperty.Register<FireLevelMeter, double>(
            nameof(BarGap), FireLevelScale.DefaultBarGap);

    /// <summary>Height of the leftmost bar. Set it equal to
    /// <see cref="MaxBarHeight"/> for the flat footer row.</summary>
    public static readonly StyledProperty<double> MinBarHeightProperty =
        AvaloniaProperty.Register<FireLevelMeter, double>(
            nameof(MinBarHeight), FireLevelScale.DefaultMinBarHeight);

    /// <summary>Height of the rightmost bar.</summary>
    public static readonly StyledProperty<double> MaxBarHeightProperty =
        AvaloniaProperty.Register<FireLevelMeter, double>(
            nameof(MaxBarHeight), FireLevelScale.DefaultMaxBarHeight);

    /// <summary>
    ///     Repeat the bar-height ramp every N bars — a SAWTOOTH rather than one
    ///     wedge. <c>0</c> (the default) is the wedge the amp rig draws;
    ///     <c>3</c> is `Play Menus.dc.html`'s Mix meter, <c>4 + (i % 3) * 2</c>.
    ///     Decoration only: the colour of a bar is still decided from the dB IT
    ///     represents.
    /// </summary>
    public static readonly StyledProperty<int> HeightCycleProperty =
        AvaloniaProperty.Register<FireLevelMeter, int>(nameof(HeightCycle));

    private int _lit;

    public FireLevelMeter()
        : base(followsThemeVariant: true)
    {
    }

    public double LevelDb { get => GetValue(LevelDbProperty); set => SetValue(LevelDbProperty, value); }
    public double MinimumDb { get => GetValue(MinimumDbProperty); set => SetValue(MinimumDbProperty, value); }
    public double MaximumDb { get => GetValue(MaximumDbProperty); set => SetValue(MaximumDbProperty, value); }
    public int SegmentCount { get => GetValue(SegmentCountProperty); set => SetValue(SegmentCountProperty, value); }
    public double BarWidth { get => GetValue(BarWidthProperty); set => SetValue(BarWidthProperty, value); }
    public double BarGap { get => GetValue(BarGapProperty); set => SetValue(BarGapProperty, value); }

    public double MinBarHeight
    {
        get => GetValue(MinBarHeightProperty);
        set => SetValue(MinBarHeightProperty, value);
    }

    public double MaxBarHeight
    {
        get => GetValue(MaxBarHeightProperty);
        set => SetValue(MaxBarHeightProperty, value);
    }

    public int HeightCycle
    {
        get => GetValue(HeightCycleProperty);
        set => SetValue(HeightCycleProperty, value);
    }

    /// <summary>How many bars are currently lit — the only thing a level change
    /// can alter on screen, and therefore the repaint gate.</summary>
    internal int LitSegments => _lit;

    /// <summary>The band the current level falls in — what the meter is
    /// SAYING, exposed so a caller (a status chip, an LED) can say the same
    /// thing without re-deriving the thresholds.</summary>
    public FireLevelBand Band => FireLevelScale.BandFor(LevelDb);

    protected override Size MeasureOverride(Size availableSize)
        => new(FireLevelScale.RowWidth(SegmentCount, BarWidth, BarGap),
            Math.Max(MinBarHeight, MaxBarHeight));

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        var count = SegmentCount;
        if (count <= 0) return;

        var inactive = FireUnitState.IsInactive(this);
        var opacity = FireUnitState.OpacityFor(this);
        var track = Paint.Color(this, "FireSurfaceTrack");
        var rowHeight = Math.Max(MinBarHeight, MaxBarHeight);
        var baseline = Math.Max(rowHeight, Bounds.Height);

        var good = Paint.Color(this, "FireSuccess");
        var hot = Paint.Color(this, "FireWarning");
        var over = Paint.Color(this, "FireDanger");

        for (var i = 0; i < count; i++)
        {
            var height = FireLevelScale.SegmentHeight(i, count, MinBarHeight, MaxBarHeight, HeightCycle);
            var x = i * (BarWidth + BarGap);
            var rect = new Rect(x, baseline - height, BarWidth, height);

            Color color;
            if (i < _lit && !inactive)
            {
                color = FireLevelScale.BandForSegment(i, count, MinimumDb, MaximumDb) switch
                {
                    FireLevelBand.Over => over,
                    FireLevelBand.Hot => hot,
                    _ => good
                };
            }
            else
            {
                // UnlitColor SCALES the track token's own translucency;
                // FireUnitState.At would REPLACE it, which painted every unlit
                // bar solid white — an idle meter that read as a hot signal.
                color = FireLevelScale.UnlitColor(track);
            }

            // At most four distinct colours per paint, so at most four brushes —
            // this must not allocate one per SEGMENT per paint on three meters at
            // 20–30 Hz.
            context.DrawRectangle(
                Paint.Brush(FireUnitState.At(color, opacity * (color.A / 255.0))),
                null, rect, FireLevelScale.BarCornerRadius, FireLevelScale.BarCornerRadius);
        }
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        var p = change.Property;

        // A level change is only visible when it moves the LIT BAR COUNT. This
        // is the whole reason a meter fed at audio rate does not melt the UI
        // thread.
        if (p == LevelDbProperty)
        {
            Relight();
            return;
        }

        if (p == SegmentCountProperty || p == BarWidthProperty || p == BarGapProperty ||
            p == MinBarHeightProperty || p == MaxBarHeightProperty || p == HeightCycleProperty)
        {
            InvalidateMeasure();
        }

        // A scale change re-derives the lit count from the SAME level, so it
        // has to repaint even when that count happens to land the same.
        if (p == MinimumDbProperty || p == MaximumDbProperty || p == SegmentCountProperty)
        {
            Relight(force: true);
        }
        else if (p == BarWidthProperty || p == BarGapProperty || p == MinBarHeightProperty ||
                 p == MaxBarHeightProperty || p == HeightCycleProperty ||
                 p == FireUnitState.IsBypassedProperty || p == IsEffectivelyEnabledProperty)
        {
            Repaint();
        }
    }

    private void Relight(bool force = false)
    {
        var lit = FireLevelScale.LitSegments(LevelDb, SegmentCount, MinimumDb, MaximumDb);
        if (!force && lit == _lit) return;
        _lit = lit;
        Repaint();
    }
}
