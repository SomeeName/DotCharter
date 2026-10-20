using System.Numerics;

namespace DotCharter.Tone.Audio.Equalizer;

/// <summary>
///     One normalized 2nd-order section: <c>b0 + b1 z^-1 + b2 z^-2</c> over
///     <c>1 + a1 z^-1 + a2 z^-2</c>.
/// </summary>
/// <remarks>
///     Deliberately the SAME five numbers the native side publishes to its audio
///     thread (<c>AudioEngine::BiquadCoeffs</c>), computed from the same RBJ
///     cookbook formulae, so the curve this draws is the curve the engine runs
///     rather than a decorative approximation of it.
/// </remarks>
public readonly record struct EqBiquad(double B0, double B1, double B2, double A1, double A2)
{
    /// <summary>The pass-through section — flat, 0 dB everywhere.</summary>
    public static EqBiquad Identity => new(1.0, 0.0, 0.0, 0.0, 0.0);

    /// <summary>
    ///     This section's magnitude at <paramref name="frequencyHz" />, in dB.
    ///     <para>
    ///         Evaluated on the unit circle: <c>H(e^jw)</c> with
    ///         <c>w = 2*pi*f/fs</c>. Above Nyquist the response is not meaningful
    ///         and the digital filter mirrors, so callers must not ask — the plot
    ///         stops at 20 kHz and every supported rate is at least 44.1 kHz.
    ///     </para>
    /// </summary>
    public double MagnitudeDbAt(double frequencyHz, double sampleRate)
    {
        var w = 2.0 * Math.PI * frequencyHz / sampleRate;
        var z1 = Complex.FromPolarCoordinates(1.0, -w);
        var z2 = z1 * z1;

        var numerator = B0 + B1 * z1 + B2 * z2;
        var denominator = 1.0 + A1 * z1 + A2 * z2;

        var magnitude = numerator.Magnitude / Math.Max(denominator.Magnitude, 1e-12);
        return 20.0 * Math.Log10(Math.Max(magnitude, 1e-7));
    }
}

/// <summary>
///     The EQ's maths: RBJ biquad coefficients per band shape, their summed
///     magnitude response, and the two axis mappings the plot and its draggable
///     handles share.
///     <para>
///         PURE and static on purpose. It is the one thing in this feature that
///         can be tested exhaustively against known filter responses (a bell at
///         its centre frequency yields exactly its gain; a 12 dB/oct high pass is
///         −12 dB one octave below cutoff), and burying it inside a custom
///         control would have made every one of those assertions need a UI
///         thread. See <c>DotCharter.Tests/Services/Audio/EqCurveTests.cs</c>.
///     </para>
/// </summary>
public static class EqCurve
{
    /// <summary>
    ///     The sample rate the CURVE is drawn at.
    ///     <para>
    ///         Deliberately fixed rather than read from the open device. The plot
    ///         is a picture of the filter's shape, and re-drawing it at a
    ///         different rate every time the user changes their interface would
    ///         make the same band render differently for no reason the user can
    ///         see. The engine of course runs at the device rate; the divergence
    ///         between the two is below a tenth of a dB anywhere under 20 kHz for
    ///         every supported rate, which is far under a pixel on this plot.
    ///     </para>
    /// </summary>
    public const double DisplaySampleRate = 48000.0;

    /// <summary>How many octaves-of-decade the log frequency axis spans:
    /// 20 Hz to 20 kHz is exactly three decades, so <c>f = 20 * 1000^t</c>.</summary>
    private const double FrequencyDecades = 1000.0;

    // ── axis mappings ────────────────────────────────────────────────────

    /// <summary>Frequency to a 0..1 position along the log frequency axis.
    /// 20 Hz is 0, 20 kHz is 1, and 632 Hz — the geometric middle — is 0.5.</summary>
    public static double FrequencyToNormalized(double frequencyHz)
    {
        var clamped = Math.Clamp(frequencyHz, EqLimits.MinFrequencyHz, EqLimits.MaxFrequencyHz);
        return Math.Log(clamped / EqLimits.MinFrequencyHz) / Math.Log(FrequencyDecades);
    }

    /// <summary>The inverse of <see cref="FrequencyToNormalized" />, clamped to the
    /// axis at both ends so a drag that leaves the plot pins rather than wraps.</summary>
    public static double NormalizedToFrequency(double normalized) =>
        EqLimits.MinFrequencyHz * Math.Pow(FrequencyDecades, Math.Clamp(normalized, 0.0, 1.0));

    /// <summary>Gain in dB to a 0..1 position DOWN the plot: +18 dB is 0 (the
    /// top), 0 dB is 0.5, −18 dB is 1. Y grows downward, which is what a
    /// pointer position gives.</summary>
    public static double GainDbToNormalized(double gainDb) =>
        0.5 - Math.Clamp(gainDb, -EqLimits.MaxGainDb, EqLimits.MaxGainDb) / (2.0 * EqLimits.MaxGainDb);

    /// <summary>The inverse of <see cref="GainDbToNormalized" />.</summary>
    public static double NormalizedToGainDb(double normalized) =>
        (0.5 - Math.Clamp(normalized, 0.0, 1.0)) * 2.0 * EqLimits.MaxGainDb;

    /// <summary>Q to a 0..1 slider position, log-spaced over
    /// [<see cref="EqLimits.MinQ" />, <see cref="EqLimits.MaxQ" />] so the useful
    /// low end is not crushed into the first tenth of the track.</summary>
    public static double QToNormalized(double q)
    {
        var clamped = Math.Clamp(q, EqLimits.MinQ, EqLimits.MaxQ);
        return Math.Log(clamped / EqLimits.MinQ) / Math.Log(EqLimits.MaxQ / EqLimits.MinQ);
    }

    /// <summary>The inverse of <see cref="QToNormalized" />.</summary>
    public static double NormalizedToQ(double normalized) =>
        EqLimits.MinQ * Math.Pow(EqLimits.MaxQ / EqLimits.MinQ, Math.Clamp(normalized, 0.0, 1.0));

    // ── coefficients ─────────────────────────────────────────────────────

    /// <summary>
    ///     The RBJ Audio EQ Cookbook coefficients for one band, normalized by a0.
    ///     <para>
    ///         Character-for-character the formulae the native engine hands to
    ///         <c>juce::dsp::IIR::Coefficients</c>, which uses the same cookbook.
    ///         A band tuned above Nyquist would produce a degenerate section, so
    ///         the frequency is capped just under it.
    ///     </para>
    /// </summary>
    public static EqBiquad Coefficients(EqBandType type, double frequencyHz, double q, double gainDb,
        double sampleRate = DisplaySampleRate)
    {
        var f0 = Math.Clamp(frequencyHz, 1.0, sampleRate * 0.49);
        var qq = Math.Max(q, 1e-3);

        var w0 = 2.0 * Math.PI * f0 / sampleRate;
        var cosW = Math.Cos(w0);
        var sinW = Math.Sin(w0);
        var alpha = sinW / (2.0 * qq);

        // A is the SQUARE ROOT of the linear gain for shelves and bells — that is
        // the cookbook's convention (gain/40, not gain/20), and getting it wrong
        // produces a curve that is silently double the requested boost.
        var a = Math.Pow(10.0, gainDb / 40.0);
        var twoSqrtAAlpha = 2.0 * Math.Sqrt(a) * alpha;

        double b0, b1, b2, a0, a1, a2;
        switch (type)
        {
            case EqBandType.HighPass:
                b0 = (1.0 + cosW) / 2.0;
                b1 = -(1.0 + cosW);
                b2 = (1.0 + cosW) / 2.0;
                a0 = 1.0 + alpha;
                a1 = -2.0 * cosW;
                a2 = 1.0 - alpha;
                break;

            case EqBandType.LowPass:
                b0 = (1.0 - cosW) / 2.0;
                b1 = 1.0 - cosW;
                b2 = (1.0 - cosW) / 2.0;
                a0 = 1.0 + alpha;
                a1 = -2.0 * cosW;
                a2 = 1.0 - alpha;
                break;

            case EqBandType.Notch:
                b0 = 1.0;
                b1 = -2.0 * cosW;
                b2 = 1.0;
                a0 = 1.0 + alpha;
                a1 = -2.0 * cosW;
                a2 = 1.0 - alpha;
                break;

            case EqBandType.Bell:
                b0 = 1.0 + alpha * a;
                b1 = -2.0 * cosW;
                b2 = 1.0 - alpha * a;
                a0 = 1.0 + alpha / a;
                a1 = -2.0 * cosW;
                a2 = 1.0 - alpha / a;
                break;

            case EqBandType.LowShelf:
                b0 = a * (a + 1.0 - (a - 1.0) * cosW + twoSqrtAAlpha);
                b1 = 2.0 * a * (a - 1.0 - (a + 1.0) * cosW);
                b2 = a * (a + 1.0 - (a - 1.0) * cosW - twoSqrtAAlpha);
                a0 = a + 1.0 + (a - 1.0) * cosW + twoSqrtAAlpha;
                a1 = -2.0 * (a - 1.0 + (a + 1.0) * cosW);
                a2 = a + 1.0 + (a - 1.0) * cosW - twoSqrtAAlpha;
                break;

            case EqBandType.HighShelf:
            default:
                b0 = a * (a + 1.0 + (a - 1.0) * cosW + twoSqrtAAlpha);
                b1 = -2.0 * a * (a - 1.0 + (a + 1.0) * cosW);
                b2 = a * (a + 1.0 + (a - 1.0) * cosW - twoSqrtAAlpha);
                a0 = a + 1.0 - (a - 1.0) * cosW + twoSqrtAAlpha;
                a1 = 2.0 * (a - 1.0 - (a + 1.0) * cosW);
                a2 = a + 1.0 - (a - 1.0) * cosW - twoSqrtAAlpha;
                break;
        }

        if (Math.Abs(a0) < 1e-12) return EqBiquad.Identity;
        return new EqBiquad(b0 / a0, b1 / a0, b2 / a0, a1 / a0, a2 / a0);
    }

    /// <summary>This band's own contribution, in dB, at one frequency.</summary>
    public static double BandResponseDbAt(EqBand band, double frequencyHz,
        double sampleRate = DisplaySampleRate) =>
        Coefficients(band.Type, band.FrequencyHz, band.Q, band.HasGain ? band.GainDb : 0.0, sampleRate)
            .MagnitudeDbAt(frequencyHz, sampleRate);

    /// <summary>
    ///     The summed response of a set of bands at one frequency, in dB.
    ///     <para>
    ///         Cascaded sections MULTIPLY in magnitude, which is a SUM in dB —
    ///         and that is exactly what the audio path does, because each band is
    ///         a separate biquad run in series over the same signal.
    ///     </para>
    /// </summary>
    public static double ResponseDbAt(IReadOnlyList<EqBand> bands, double frequencyHz,
        double sampleRate = DisplaySampleRate)
    {
        var sum = 0.0;
        for (var i = 0; i < bands.Count; i++)
            sum += BandResponseDbAt(bands[i], frequencyHz, sampleRate);
        return sum;
    }

    /// <summary>
    ///     The summed response sampled at <paramref name="pointCount" /> points
    ///     evenly spaced along the LOG frequency axis — i.e. one value per column
    ///     of the plot.
    /// </summary>
    /// <param name="bands">Only the bands that are actually engaged; this method
    ///     applies no enable/solo policy of its own (see
    ///     <see cref="EqSection.AudibleBands" />, which is where that lives).</param>
    /// <param name="pointCount">How many samples across the axis. At least 2.</param>
    /// <param name="sampleRate">The rate the response is evaluated at.</param>
    public static double[] Response(IReadOnlyList<EqBand> bands, int pointCount,
        double sampleRate = DisplaySampleRate)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(pointCount, 2);

        var result = new double[pointCount];
        Response(bands, result, sampleRate);
        return result;
    }

    /// <summary>
    ///     <see cref="Response(IReadOnlyList{EqBand}, int, double)" /> into a
    ///     caller-owned buffer — one value per element of <paramref name="into" />,
    ///     whose length is the point count. The plot repaints at ~15 Hz for as
    ///     long as the EQ module is attached and would allocate two fresh
    ///     <c>double[160]</c> per frame through the array overload.
    /// </summary>
    public static void Response(IReadOnlyList<EqBand> bands, Span<double> into,
        double sampleRate = DisplaySampleRate)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(into.Length, 2);

        var last = into.Length - 1;
        for (var i = 0; i < into.Length; i++)
        {
            var f = NormalizedToFrequency(i / (double)last);
            into[i] = ResponseDbAt(bands, f, sampleRate);
        }
    }

    // ── the drag verb ────────────────────────────────────────────────────

    /// <summary>
    ///     Where a band lands when its handle is dragged to a normalized point on
    ///     the plot: sideways is frequency, up is gain — or, for a shape that has
    ///     no gain, up is <b>Q</b>.
    ///     <para>
    ///         The whole verb, as a pure function, because "the handle exists" is
    ///         not the thing that can regress — the MAPPING is. Both axes clamp
    ///         rather than wrap, so a pointer dragged off the plot pins the band
    ///         at 20 Hz or 20 kHz instead of teleporting it.
    ///     </para>
    ///     <para>
    ///         <b>A gainless band's vertical axis is Q, and that is not a
    ///         flourish.</b> Bands 1 and 6 of every preset are a high pass and a
    ///         low pass; neither has gain, so half of their handle's travel used
    ///         to do nothing at all — the owner reported it as "6 and 1 can't be
    ///         moved in height". A dead axis on a draggable handle is dead UI, and
    ///         Q is the one remaining thing a filter of that shape has: raising it
    ///         visibly lifts the resonant peak at the cutoff, so the drag has a
    ///         result the user can see on the very curve they are holding. The
    ///         handle stays drawn on the 0 dB line, exactly as the mockup draws
    ///         it.
    ///     </para>
    ///     <para>
    ///         <b>The Q travel is RELATIVE to the grab, which is why
    ///         <paramref name="band" /> must be the band as it was when the drag
    ///         STARTED.</b> An absolute mapping would snap Q off 0.71 to the
    ///         axis midpoint the instant the handle was touched, because the
    ///         handle does not sit at the y its Q would map to. Passing the LIVE
    ///         band instead of the grabbed one compounds the multiplier once per
    ///         pointer move and sends Q to a limit in a few pixels.
    ///     </para>
    ///     <para>
    ///         <b>It also switches the band ON</b>, which the mockup does too
    ///         (<c>onMove</c> writes <c>on: true</c>). A drag that leaves the band
    ///         bypassed is a gesture with an authored value and a flat curve —
    ///         the state the owner's screenshot showed, band 5 reading −10.9 dB
    ///         over a curve with no bend in it and the badge honestly saying 0/6.
    ///         Aiming a band before you hear it is still available: the chip's dot
    ///         toggles it back off, and it keeps whatever the drag left.
    ///     </para>
    /// </summary>
    /// <param name="band">The band as it was when the drag BEGAN. Not the live
    ///     one — see above.</param>
    /// <param name="normalizedX">0 at the left edge of the plot, 1 at the right.</param>
    /// <param name="normalizedY">0 at the TOP of the plot, 1 at the bottom.</param>
    /// <param name="grabNormalizedY">Where on that axis the drag started. Equal to
    ///     <paramref name="normalizedY" /> means no vertical travel yet.</param>
    /// <returns>The band as the drag leaves it.</returns>
    public static EqBand Drag(EqBand band, double normalizedX, double normalizedY,
        double grabNormalizedY)
    {
        var frequency = NormalizedToFrequency(normalizedX);

        if (band.HasGain)
        {
            return band with
            {
                FrequencyHz = frequency,
                GainDb = NormalizedToGainDb(normalizedY),
                Enabled = true
            };
        }

        return band with { FrequencyHz = frequency, GainDb = 0.0, Enabled = true, Q = DragQ(band.Q, normalizedY, grabNormalizedY) };
    }

    /// <summary>
    ///     Q after a vertical drag of <c>grabY - y</c> of the plot's height, up
    ///     being positive.
    ///     <para>
    ///         Multiplicative over the whole Q range, so ONE full-height drag
    ///         covers <see cref="EqLimits.MinQ" />..<see cref="EqLimits.MaxQ" />
    ///         end to end and the feel is the same at 0.3 as at 18 — the same
    ///         reason the wheel gesture and the Q slider are both logarithmic.
    ///     </para>
    /// </summary>
    public static double DragQ(double grabbedQ, double normalizedY, double grabNormalizedY)
    {
        var travel = Math.Clamp(grabNormalizedY, 0.0, 1.0) - Math.Clamp(normalizedY, 0.0, 1.0);
        var scaled = Math.Clamp(grabbedQ, EqLimits.MinQ, EqLimits.MaxQ)
                     * Math.Pow(EqLimits.MaxQ / EqLimits.MinQ, travel);
        return Math.Clamp(scaled, EqLimits.MinQ, EqLimits.MaxQ);
    }

    /// <summary>
    ///     Which band's handle a pointer at a normalized plot position is
    ///     grabbing — the nearest one, measured in PLOT space so a band 3 dB away
    ///     vertically is not judged closer than one an octave away horizontally.
    /// </summary>
    /// <returns>The band index, or −1 when there are no bands.</returns>
    public static int NearestHandle(IReadOnlyList<EqBand> bands, double normalizedX, double normalizedY)
    {
        var best = -1;
        var bestDistance = double.MaxValue;
        for (var i = 0; i < bands.Count; i++)
        {
            var dx = FrequencyToNormalized(bands[i].FrequencyHz) - normalizedX;
            var dy = GainDbToNormalized(bands[i].HasGain ? bands[i].GainDb : 0.0) - normalizedY;
            var distance = dx * dx + dy * dy;
            if (distance >= bestDistance) continue;
            bestDistance = distance;
            best = i;
        }

        return best;
    }
}
