using Avalonia;
using Avalonia.Input;
using DotCharter.Tone.UI.Controls.Dsp;

namespace DotCharter.Tone.UI.Controls.Fire;

/// <summary>
/// Pure geometry for <see cref="FireKnob"/> — every number in
/// <c>Amp and FX Rig.dc.html</c>'s <c>knob()</c>, in one testable place and
/// free of Avalonia rendering types so the mapping is pinned in the default
/// gate (<c>FireKnobGeometryTests</c>).
///
/// <para>The value ↔ angle mapping is NOT re-derived here: it is
/// <see cref="KnobMath"/>'s, which already sweeps 270° from −135°, exactly what
/// the mockup does (<c>angle = -135 + pct * 270</c>). Two copies of that formula
/// is how one knob ends up pointing somewhere else.</para>
///
/// <para><b>The single detail most likely to be got wrong:</b> the drag is
/// VERTICAL and its sensitivity is a CONSTANT — 170 px of pointer travel spans
/// the whole range whatever size the dial is drawn at. It is not a rotary
/// gesture and it does not scale with the control. Hence
/// <see cref="PixelsForFullRange"/> is a const, not a styled property: a knob
/// whose sensitivity a caller can change is a rig where two knobs feel
/// different.</para>
/// </summary>
public static class FireKnobGeometry
{
    /// <summary>Pointer travel, in pixels, for a full min→max sweep. The
    /// mockup: <c>cur + (y0 - ev.clientY) / 170</c>.</summary>
    public const double PixelsForFullRange = 170.0;

    /// <summary>Shift-drag divides the range covered by the same travel by
    /// this, for a fine pass.</summary>
    public const double FineDragMultiplier = 10.0;

    /// <summary>The dial diameter. 46 px, or 38 in a compact (collapsed-card)
    /// row.</summary>
    public const double DialSize = 46.0;

    /// <inheritdoc cref="DialSize"/>
    public const double CompactDialSize = 38.0;

    /// <summary>The cell a knob occupies. Wider than the dial because the
    /// caption below is allowed to be.</summary>
    public const double CellWidth = 58.0;

    /// <inheritdoc cref="CellWidth"/>
    public const double CompactCellWidth = 52.0;

    /// <summary>How far the value arc reaches OUTSIDE the dial (the mockup's
    /// <c>inset: -4px</c>).</summary>
    public const double ArcOutset = 4.0;

    /// <summary>How far the value arc reaches INSIDE the dial's edge (the
    /// mockup masks the ring from <c>size/2 - 2</c>).</summary>
    public const double ArcInset = 2.0;

    /// <summary>Vertical gap between the dial, the caption and the readout.</summary>
    public const double RowGap = 5.0;

    /// <summary>Caption type: the spec's "Mono / micro", 8 px, .08em tracked,
    /// UPPERCASE, up to two lines.</summary>
    public const double LabelFontSize = 8.0;

    /// <inheritdoc cref="LabelFontSize"/>
    public const double LabelLetterSpacing = 0.65; // .08em at 8 px

    /// <inheritdoc cref="LabelFontSize"/>
    public const double LabelLineHeight = 10.0; // line-height 1.25

    /// <inheritdoc cref="LabelFontSize"/>
    public const int MaxLabelLines = 2;

    /// <summary>Readout type: the spec's "Mono / value", 10 px, white.</summary>
    public const double ValueFontSize = 10.0;

    /// <inheritdoc cref="ValueFontSize"/>
    public const double ValueLineHeight = 13.0;

    /// <summary>Indicator length as a fraction of the dial diameter, and its
    /// stroke width (the mockup: <c>height: size * 0.37</c>, 2 px flat).</summary>
    public const double IndicatorLengthRatio = 0.37;

    /// <inheritdoc cref="IndicatorLengthRatio"/>
    public const double IndicatorWidth = 2.0;

    /// <summary>Focus ring: 2 px, offset 3 px — the spec's
    /// "2px solid #cfe2ff, offset 3–4px".</summary>
    public const double FocusRingWidth = 2.0;

    /// <inheritdoc cref="FocusRingWidth"/>
    public const double FocusRingOffset = 3.0;

    /// <summary>Sweep of the value arc, and the angle it starts at (clockwise
    /// from 12 o'clock). Re-exported from <see cref="KnobMath"/> so a reader of
    /// this file does not have to guess whether the Fire knob differs.</summary>
    public const double SweepDegrees = KnobMath.SweepDegrees;

    /// <inheritdoc cref="SweepDegrees"/>
    public const double StartAngleDegrees = KnobMath.MinAngleDegrees;

    /// <summary>The dial diameter for a knob in this mode.</summary>
    public static double DialDiameter(bool compact) => compact ? CompactDialSize : DialSize;

    /// <summary>The cell width for a knob in this mode.</summary>
    public static double CellWidthFor(bool compact) => compact ? CompactCellWidth : CellWidth;

    /// <summary>Centre-line radius of the value arc: the ring runs from
    /// <c>dial/2 - ArcInset</c> to <c>dial/2 + ArcOutset</c>.</summary>
    public static double ArcRadius(double dialDiameter)
        => dialDiameter / 2.0 + (ArcOutset - ArcInset) / 2.0;

    /// <summary>Width of the value arc's pen — the whole ring band.</summary>
    public const double ArcStrokeWidth = ArcOutset + ArcInset;

    /// <summary>Height the dial block reserves: the dial plus the arc's reach
    /// on both sides, so a knob never paints onto its neighbour.</summary>
    public static double DialBlockHeight(double dialDiameter) => dialDiameter + 2 * ArcOutset;

    /// <summary>
    /// The whole cell: dial block, caption (two lines, always reserved so a
    /// row of knobs whose captions wrap differently still lines up) and
    /// readout. Deliberately independent of the text engine — this is what
    /// <see cref="FireKnob.MeasureOverride"/> returns, so it must resolve with
    /// no Avalonia platform.
    /// </summary>
    public static Size DesiredSize(bool compact)
        => new(CellWidthFor(compact),
            DialBlockHeight(DialDiameter(compact))
            + RowGap + MaxLabelLines * LabelLineHeight
            + RowGap + ValueLineHeight);

    /// <summary>Indicator angle (degrees clockwise from 12 o'clock) for a 0..1
    /// sweep fraction: <c>−135° + pct × 270°</c>.</summary>
    public static double AngleDegreesFor(double fraction) => KnobMath.AngleDegreesForFraction(fraction);

    /// <summary>0..1 position of a value within its range.</summary>
    public static double FractionFromValue(double value, double min, double max)
        => KnobMath.FractionFromValue(value, min, max);

    /// <inheritdoc cref="FractionFromValue(double,double,double)"/>
    /// <remarks>The dial's travel mapping (<see cref="KnobTaper"/>).</remarks>
    public static double FractionFromValue(double value, double min, double max, KnobTaper taper)
        => KnobMath.FractionFromValue(value, min, max, taper);

    /// <summary>
    /// Applies a vertical pointer drag. <paramref name="deltaY"/> is the
    /// pointer's movement since the anchor (positive = DOWN, so dragging up
    /// raises the value), and <see cref="PixelsForFullRange"/> of travel spans
    /// min→max — never the control's height.
    /// </summary>
    public static double ValueForDrag(
        double anchorValue, double deltaY, double min, double max, bool fine = false)
        => ValueForDrag(anchorValue, deltaY, min, max, KnobTaper.Linear, fine);

    /// <inheritdoc cref="ValueForDrag(double,double,double,double,bool)"/>
    /// <remarks>The dial's travel mapping. The drag is applied in TRAVEL space, so a pixel of pointer movement is a pixel of dial movement under either taper.</remarks>
    public static double ValueForDrag(
        double anchorValue, double deltaY, double min, double max, KnobTaper taper, bool fine = false)
        => KnobMath.ApplyVerticalDrag(anchorValue, deltaY, min, max,
            PixelsForFullRange * (fine ? FineDragMultiplier : 1.0), taper);

    /// <summary>A point on the dial at a clockwise-from-12-o'clock angle.</summary>
    public static Point PointOnDial(Point center, double radius, double angleDegrees)
    {
        var (dx, dy) = KnobMath.IndicatorVector(angleDegrees);
        return new Point(center.X + dx * radius, center.Y + dy * radius);
    }

    // ── The keyboard ─────────────────────────────────────────────────────────

    /// <summary>How many arrow steps one Page press is worth.</summary>
    public const double PageStepMultiplier = 10.0;

    /// <summary>
    /// One arrow press. A parameter that declares a <c>Step</c> moves by exactly
    /// that step — anything else would land between two grid points and be
    /// snapped straight back, so the key would look dead; a continuous parameter
    /// gets 1 % of its range.
    /// </summary>
    public static double ArrowStep(double min, double max, double step)
        => step > 0 ? step : (max - min) / 100.0;

    /// <summary>
    /// One Page press: always <see cref="PageStepMultiplier"/> arrow steps.
    /// **A stepped parameter must not get ONE step here**, which would make PageUp and
    /// Up indistinguishable on every knob the catalogue declares a step for —
    /// i.e. on most of the rig.
    /// </summary>
    public static double PageStep(double min, double max, double step)
        => ArrowStep(min, max, step) * PageStepMultiplier;

    /// <summary>
    /// The value a key press lands on, or <c>null</c> when the key is not one of
    /// the knob's. Clamped here so the mapping is complete on its own; the
    /// control still runs it through the same snap every other gesture uses.
    ///
    /// <para>Up/Right and Down/Left step, Page steps ten of those, Home/End go
    /// to the ends, and Delete/Backspace revert to
    /// <paramref name="defaultValue"/> — the keyboard's half of the
    /// double-click reset.</para>
    /// </summary>
    public static double? ValueForKey(
        Key key, double value, double min, double max, double step, double defaultValue = 0.0)
    {
        if (max <= min) return null;

        var arrow = ArrowStep(min, max, step);

        double? target = key switch
        {
            Key.Up or Key.Right => value + arrow,
            Key.Down or Key.Left => value - arrow,
            Key.PageUp => value + arrow * PageStepMultiplier,
            Key.PageDown => value - arrow * PageStepMultiplier,
            Key.Home => min,
            Key.End => max,
            Key.Delete or Key.Back => defaultValue,
            _ => null
        };

        return target is { } t ? Math.Clamp(t, min, max) : null;
    }
}
