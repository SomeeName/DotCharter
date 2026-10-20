using System.Globalization;
using DotCharter.Tone.Audio.Dsp;

namespace DotCharter.Tone.UI.Controls.Dsp;

/// <summary>
/// How a dial's TRAVEL maps onto a parameter's range. It is a presentation
/// choice and nothing else: the value pushed to the DSP is in the param's own
/// plain units (Hz stays Hz) under either taper — only where the pointer has to
/// be to produce it moves.
/// </summary>
public enum KnobTaper
{
    /// <summary>Equal travel = equal value. Every param shipped this way until
    /// the 0.9 gear wave, and everything that is not a frequency or a
    /// multi-decade sweep keeps it.</summary>
    Linear,

    /// <summary>Equal travel = equal RATIO. A 20–20,000 Hz dial puts 1 kHz at
    /// 57 % of travel instead of 4.9 %, which is what a filter knob has to do to
    /// be usable — the whole guitar range is otherwise crushed into the first
    /// twentieth of the sweep.</summary>
    Logarithmic
}

/// <summary>
/// Pure geometry for a rotary control (the Fire knob): value ↔ sweep-fraction ↔
/// pointer-angle, and vertical-drag application. Kept free of Avalonia types so
/// the rotation logic is unit-tested without a UI instance
/// (<c>KnobMathTests</c>). The knob sweeps a 270° arc: the indicator points to
/// the lower-left at the minimum and lower-right at the maximum, straight up at
/// mid-scale.
/// </summary>
public static class KnobMath
{
    /// <summary>Total sweep of the knob, in degrees (7-o'clock → 5-o'clock).</summary>
    public const double SweepDegrees = 270.0;

    /// <summary>Indicator angle at the minimum value, degrees clockwise from
    /// straight-up (12 o'clock).</summary>
    public const double MinAngleDegrees = -135.0;

    // ── Dial geometry: the dial must fit INSIDE the control's own cell ──────────
    //
    // Knobs are laid out edge-to-edge (WrapPanel ItemWidth=72 / a 2 px-spaced
    // StackPanel), so ANYTHING a knob paints outside its bounds lands on its
    // NEIGHBOUR. The dial is not the outermost thing it paints: the value arc
    // rides at radius + ArcOffset and its soft glow pen is GlowPenWidth wide, so
    // the real outer reach is radius + ArcClearance. Size the dial off THAT, not
    // off the raw bounds.

    /// <summary>Radius offset the value arc rides at, outside the dial.</summary>
    public const double ArcOffset = 6.0;

    /// <summary>Width of the widest (soft outer glow) arc pen.</summary>
    public const double GlowPenWidth = 10.0;

    /// <summary>How far past the dial's radius the knob actually paints — the arc
    /// plus half its widest pen. Everything the dial sizing must budget for.</summary>
    public const double ArcClearance = ArcOffset + GlowPenWidth / 2.0;

    /// <summary>Vertical space the caption + numeric readout lines need under the
    /// dial block (two theme-token text lines and their gaps).</summary>
    public const double TextReserve = 32.0;

    /// <summary>The biggest dial the design draws. Real gear knobs dominate the
    /// face (AmpliTube parity) — this caps how big a dial gets before it stops
    /// growing with the cell.</summary>
    public const double MaxDial = 68.0;

    /// <summary>
    /// The dial diameter for a knob cell of <paramref name="width"/> ×
    /// <paramref name="height"/>: the biggest dial whose value arc + glow still
    /// fit INSIDE the cell horizontally, and that still leaves
    /// <see cref="TextReserve"/> for the caption/readout below. Floors at 12 px so
    /// a degenerate cell still draws something.
    /// </summary>
    public static double DialDiameter(double width, double height)
    {
        var byWidth = width - 2 * ArcClearance;
        var byHeight = height - 2 * ArcClearance - TextReserve;
        return Math.Max(12.0, Math.Min(MaxDial, Math.Min(byWidth, byHeight)));
    }

    /// <summary>Vertical centre of the dial: one arc-clearance below the top edge,
    /// so the arc's topmost pixel lands exactly on y = 0.</summary>
    public static double DialCenterY(double dialDiameter) => ArcClearance + dialDiameter / 2.0;

    /// <summary>Baseline the caption starts at — directly under the value arc's
    /// outermost reach, so text and arc never overlap.</summary>
    public static double TextTop(double dialDiameter) => dialDiameter + 2 * ArcClearance;

    /// <summary>0..1 position of <paramref name="value"/> within the range.
    /// Degenerate (max ≤ min) ranges collapse to 0.</summary>
    public static double FractionFromValue(double value, double min, double max)
        => FractionFromValue(value, min, max, KnobTaper.Linear);

    /// <summary>Maps a 0..1 fraction back to a value in [min, max].</summary>
    public static double ValueFromFraction(double fraction, double min, double max)
        => ValueFromFraction(fraction, min, max, KnobTaper.Linear);

    /// <inheritdoc cref="FractionFromValue(double,double,double)"/>
    /// <remarks>How dial TRAVEL maps onto the range — see <see cref="KnobTaper"/>. The parameter VALUE is unchanged either way.</remarks>
    public static double FractionFromValue(double value, double min, double max, KnobTaper taper)
    {
        if (max <= min) return 0.0;
        if (taper == KnobTaper.Logarithmic && IsLoggable(min, max))
        {
            var v = Math.Clamp(value, min, max);
            return Math.Clamp(Math.Log(v / min) / Math.Log(max / min), 0.0, 1.0);
        }

        var t = (value - min) / (max - min);
        return Math.Clamp(t, 0.0, 1.0);
    }

    /// <inheritdoc cref="ValueFromFraction(double,double,double)"/>
    /// <remarks>How dial TRAVEL maps onto the range — see <see cref="KnobTaper"/>.</remarks>
    public static double ValueFromFraction(double fraction, double min, double max, KnobTaper taper)
    {
        var t = Math.Clamp(fraction, 0.0, 1.0);
        if (taper == KnobTaper.Logarithmic && IsLoggable(min, max))
            return Math.Clamp(min * Math.Pow(max / min, t), min, max);
        return min + t * (max - min);
    }

    /// <summary>
    /// Whether a log taper is even DEFINABLE on this range: both ends strictly
    /// positive and ordered. A bipolar param (<c>freqshift.shiftHz</c> runs
    /// −1,200…+1,200) and an "0 = off" param (<c>midside.monoBelowHz</c>) both
    /// fail it, and both silently fall back to linear rather than producing a
    /// NaN dial — <c>log(0)</c> is −∞ and every arithmetic after it is lost.
    /// </summary>
    public static bool IsLoggable(double min, double max) => min > 0.0 && max > min;

    /// <summary>How many decades a range spans (<c>log10(max/min)</c>), or 0
    /// when a log span is undefined on it.</summary>
    public static double DecadesOf(double min, double max)
        => IsLoggable(min, max) ? Math.Log10(max / min) : 0.0;

    /// <summary>Ranges at or past this many decades get a log taper on travel
    /// alone. Two decades is where a linear dial stops being usable: at 100:1
    /// the bottom tenth of the range owns the first 1 % of the sweep.</summary>
    public const double LogTaperDecades = 2.0;

    /// <summary>
    /// The taper a catalogue param gets, decided from its METADATA — never from
    /// a per-module table (the pedalboard's standing rule: a module added to the
    /// native catalogue tomorrow must land on the right control with nothing
    /// here to update).
    /// <para>
    /// Two triggers, either one enough. A param whose id ends in <c>Hz</c> or
    /// <c>KHz</c> is a FREQUENCY, and frequency is heard in ratios — the native
    /// catalogue asks for this by name (<c>FilterGear.cpp</c>'s <c>svf.cutoffHz</c>
    /// 20–20,000 and <c>comb.freqHz</c> 20–5,000). And any range spanning
    /// <see cref="LogTaperDecades"/> decades or more gets it whatever it is
    /// called, because the arithmetic is the same whether the units are hertz or
    /// milliseconds.
    /// </para>
    /// <para>
    /// Two exclusions, both load-bearing. An ORDINAL param (<paramref name="step"/>
    /// &gt; 0) stays LINEAR: its values are a COUNT the core lrounds, and bunching
    /// a count logarithmically makes the low steps unreachably narrow while the
    /// high ones each own a third of the dial. And a range a log taper is not
    /// defined on — a bipolar <c>shiftHz</c>, an "0 = off" <c>monoBelowHz</c> —
    /// stays linear via <see cref="IsLoggable"/>.
    /// </para>
    /// </summary>
    /// <param name="paramId">The catalogue param id (<c>"cutoffHz"</c>). A unit
    /// string (<c>"Hz"</c>) also matches, so a control that only knows the unit
    /// reaches the same answer.</param>
    /// <param name="min">The param's inclusive minimum.</param>
    /// <param name="max">The param's inclusive maximum.</param>
    /// <param name="step">The ordinal quantization step; 0 for continuous.</param>
    public static KnobTaper TaperFor(string? paramId, double min, double max, double step)
    {
        if (step > 0.0 || !IsLoggable(min, max)) return KnobTaper.Linear;
        if (DspParamInfo.IsFrequencyId(paramId) || DecadesOf(min, max) >= LogTaperDecades) return KnobTaper.Logarithmic;
        return KnobTaper.Linear;
    }

    /// <summary>Indicator angle (degrees, clockwise from 12 o'clock) for a
    /// 0..1 sweep fraction.</summary>
    public static double AngleDegreesForFraction(double fraction)
        => MinAngleDegrees + Math.Clamp(fraction, 0.0, 1.0) * SweepDegrees;

    /// <summary>
    /// The indicator angle for a STEPPED control whose positions are printed at
    /// <paramref name="angles"/> (one per step from <paramref name="min"/>, degrees
    /// clockwise from 12 o'clock): a faceplate selector's labels sit wherever the
    /// art put them — all four sides, a whole circle — not on the even sweep.
    /// </summary>
    public static double AngleForStep(double value, double min, IReadOnlyList<double> angles)
    {
        if (angles.Count == 0) return 0;
        var index = (int)Math.Round(value - min);
        return angles[Math.Clamp(index, 0, angles.Count - 1)];
    }

    /// <summary>Indicator angle for a raw value within [min, max].</summary>
    public static double AngleDegreesForValue(double value, double min, double max)
        => AngleDegreesForFraction(FractionFromValue(value, min, max));

    /// <inheritdoc cref="AngleDegreesForValue(double,double,double)"/>
    /// <remarks>How dial TRAVEL maps onto the range.</remarks>
    public static double AngleDegreesForValue(double value, double min, double max, KnobTaper taper)
        => AngleDegreesForFraction(FractionFromValue(value, min, max, taper));

    /// <summary>
    /// Applies a vertical drag to a value. <paramref name="deltaY"/> is the
    /// pointer's screen-space movement since the drag anchor (positive = moved
    /// DOWN); dragging up therefore raises the value. A full
    /// <paramref name="pixelsForFullRange"/> upward drag spans min→max.
    /// </summary>
    public static double ApplyVerticalDrag(
        double anchorValue, double deltaY, double min, double max, double pixelsForFullRange)
        => ApplyVerticalDrag(anchorValue, deltaY, min, max, pixelsForFullRange, KnobTaper.Linear);

    /// <inheritdoc cref="ApplyVerticalDrag(double,double,double,double,double)"/>
    /// <remarks>How dial TRAVEL maps onto the range. The drag is applied in TRAVEL space, which is the whole point: one pixel of pointer movement must always be one pixel of dial movement, whatever the value under it is doing.</remarks>
    public static double ApplyVerticalDrag(
        double anchorValue, double deltaY, double min, double max, double pixelsForFullRange,
        KnobTaper taper)
    {
        if (pixelsForFullRange <= 0) return anchorValue;
        var t = FractionFromValue(anchorValue, min, max, taper);
        t -= deltaY / pixelsForFullRange; // up (negative deltaY) increases
        return ValueFromFraction(t, min, max, taper);
    }

    /// <summary>Unit indicator vector in screen space (y grows downward) for a
    /// clockwise-from-up angle. Returns (dx, dy) on the unit circle.</summary>
    public static (double Dx, double Dy) IndicatorVector(double angleDegrees)
    {
        var rad = angleDegrees * Math.PI / 180.0;
        return (Math.Sin(rad), -Math.Cos(rad));
    }

    /// <summary>
    /// Quantizes a value onto a param's discrete grid: the nearest
    /// <c>min + k*step</c>, clamped to [min, max]. A <paramref name="step"/> of 0
    /// (or less) means the param is continuous and the value passes through
    /// untouched — NOT clamped, so this stays a pure snap and callers keep owning
    /// the clamp.
    /// <para>
    /// This is what stops a knob showing "6.5 stages" or "2.4 voices": those
    /// params are ints in the native core (it lrounds them), so any value between
    /// two steps is a readout that lies about what the DSP is running. The step
    /// comes from the native catalogue (ABI v27), and it is NOT always 1 —
    /// phaser <c>stages</c> is 2, because the core forces the allpass count even.
    /// </para>
    /// </summary>
    public static double Snap(double value, double min, double max, double step)
    {
        if (step <= 0.0 || max <= min) return value;
        var k = Math.Round((value - min) / step, MidpointRounding.AwayFromZero);
        return Math.Clamp(min + k * step, min, max);
    }

    /// <summary>
    /// Display-space value for the knob's numeric readout. When
    /// <paramref name="tenScale"/> is requested AND the parameter range is
    /// exactly 0..1 (a normalized DSP param), the readout shows hardware-dial
    /// 0–10 units instead of 0.00–1.00. Display only — the underlying value is
    /// never rescaled. Any other range passes through unchanged.
    /// </summary>
    public static double DisplayValue(double value, double min, double max, bool tenScale)
        => tenScale && min == 0.0 && max == 1.0 ? value * 10.0 : value;

    /// <summary>Inverse of <see cref="DisplayValue"/>: maps a value the user
    /// typed into the readout's display space back to the real parameter value.
    /// A 0–10 ten-scale entry on a unit-range param divides by 10; every other
    /// range is already in real units. The result is NOT clamped — callers pair
    /// this with <see cref="Math.Clamp(double,double,double)"/>.</summary>
    public static double ValueFromDisplay(double display, double min, double max, bool tenScale)
        => tenScale && min == 0.0 && max == 1.0 ? display / 10.0 : display;

    /// <summary>
    /// Parses a numeric string the user typed into the inline value editor and
    /// converts it to the real (clamped) parameter value. The entry is in the
    /// readout's DISPLAY space (so a 0–10 ten-scale param accepts "7"); it is
    /// mapped back through <see cref="ValueFromDisplay"/> then clamped to
    /// [min, max]. Accepts both the invariant and current-culture decimal
    /// separators. Returns false (and leaves <paramref name="value"/> at 0) when
    /// the text is null/blank or not a number.
    /// </summary>
    public static bool TryParseEntry(
        string? text, double min, double max, bool tenScale, out double value)
    {
        value = 0.0;
        if (string.IsNullOrWhiteSpace(text)) return false;

        var trimmed = text.Trim();
        if (!double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out var display) &&
            !double.TryParse(trimmed, NumberStyles.Float, CultureInfo.CurrentCulture, out display))
        {
            return false;
        }

        var real = ValueFromDisplay(display, min, max, tenScale);
        value = Math.Clamp(real, Math.Min(min, max), Math.Max(min, max));
        return true;
    }
}
