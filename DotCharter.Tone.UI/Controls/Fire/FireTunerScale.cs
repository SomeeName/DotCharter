namespace DotCharter.Tone.UI.Controls.Fire;

/// <summary>What one string's tuner row is SAYING right now.</summary>
public enum FireTunerBand
{
    /// <summary>Nothing has been heard on this string yet. It is NOT "zero cents
    /// off" — a string nobody has plucked and a string that is perfectly in tune
    /// are the two states this screen most needs to keep apart, and a single
    /// <c>double</c> cannot spell the difference.</summary>
    NoReading,

    /// <summary>Within ±<c>InTuneCents</c> of the target — green.</summary>
    InTune,

    /// <summary>Outside it — amber. "You have to do something to the guitar
    /// before you can play", which is what amber means everywhere on this
    /// screen.</summary>
    OutOfTune
}

/// <summary>
/// The pure half of <see cref="FireTunerMeter" />: where a signed cents
/// deviation sits on a centre-anchored scale, and how wide the in-tune band is
/// on it.
///
/// <para><b>The scale is DERIVED from the tolerance, not declared beside it.</b>
/// The mockup (<c>Get Ready.dc.html</c>) draws the needle at
/// <c>50% + cents × 7%</c> with the target band spanning 43%–57%, i.e. the band
/// is exactly one seventh of the half-scale. That ratio
/// (<see cref="ScaleToBandRatio" />) is the rule; the tolerance itself comes
/// from the REAL tuner (<c>GuidedTuningCheck.InTuneCents</c>), so a change there
/// moves the band and the scale together instead of leaving the picture
/// claiming a precision the judge does not use.</para>
///
/// <para>Deliberately separate from <see cref="FireLevelScale" />: that one maps
/// a MAGNITUDE up from a floor and colours by the dB a bar represents. This one
/// maps a SIGNED deviation either side of a centre, has a band rather than a
/// threshold ladder, and has a third state that is not a value at all.</para>
/// </summary>
public static class FireTunerScale
{
    /// <summary>Mirrors <c>GuidedTuningCheck.InTuneCents</c>'s default. It is a
    /// FALLBACK for a control nobody has bound, never the source of truth — the
    /// screen binds the live check's own value.</summary>
    public const double DefaultInTuneCents = 5.0;

    /// <summary>Half the meter's width, in multiples of the in-tune tolerance.
    /// From the mockup: a ±1-cent band on a ±7.1-cent scale.</summary>
    public const double ScaleToBandRatio = 7.0;

    /// <summary>Smallest tolerance the scale will divide by. A zero or negative
    /// tolerance would make every reading land at an edge (or NaN); clamping
    /// keeps a mis-set control merely wrong rather than crashing at render.</summary>
    public const double MinimumInTuneCents = 0.1;

    public static double ClampTolerance(double inTuneCents) =>
        double.IsFinite(inTuneCents) && inTuneCents > MinimumInTuneCents
            ? inTuneCents
            : MinimumInTuneCents;

    /// <summary>Cents at the meter's right edge (and, negated, its left edge).</summary>
    public static double HalfScaleCents(double inTuneCents) =>
        ClampTolerance(inTuneCents) * ScaleToBandRatio;

    /// <summary>Where the needle sits, 0 = fully flat edge, 0.5 = dead on,
    /// 1 = fully sharp edge. CLAMPED: a string 40 cents out pins at the edge and
    /// reads as "a long way off", which is true and is all the player needs.</summary>
    public static double NeedleFraction(double cents, double inTuneCents)
    {
        if (!double.IsFinite(cents)) return 0.5;
        var half = HalfScaleCents(inTuneCents);
        return Math.Clamp(0.5 + 0.5 * cents / half, 0.0, 1.0);
    }

    /// <summary>Half the target band's width as a fraction of the whole meter.
    /// Falls out of <see cref="ScaleToBandRatio" /> alone — it does not depend on
    /// the tolerance, which is exactly why the picture is stable while the
    /// tolerance is tuned.</summary>
    public static double BandHalfFraction => 0.5 / ScaleToBandRatio;

    /// <summary>Left edge of the target band, as a fraction of the meter.</summary>
    public static double BandStartFraction => 0.5 - BandHalfFraction;

    /// <summary>Width of the target band, as a fraction of the meter.</summary>
    public static double BandWidthFraction => 2.0 * BandHalfFraction;

    /// <summary>The band a reading falls in. <paramref name="hasReading" /> is
    /// the caller's "has this string been heard yet" — the meter cannot infer it
    /// from the cents value, and guessing is how a silent string ends up
    /// claiming it is perfectly in tune.</summary>
    public static FireTunerBand BandFor(bool hasReading, double cents, double inTuneCents)
    {
        if (!hasReading || !double.IsFinite(cents)) return FireTunerBand.NoReading;
        return Math.Abs(cents) <= ClampTolerance(inTuneCents)
            ? FireTunerBand.InTune
            : FireTunerBand.OutOfTune;
    }

    /// <summary>Token key for the needle in a given band. No hex, and no
    /// per-band fallback colour — a missing token renders visibly wrong rather
    /// than plausibly right, the same call <see cref="FireUnitState" /> makes.</summary>
    public static string NeedleTokenKeyFor(FireTunerBand band) => band switch
    {
        FireTunerBand.InTune => "FireSuccess",
        FireTunerBand.OutOfTune => "FireWarning",
        _ => "FireTextFaint"
    };
}
