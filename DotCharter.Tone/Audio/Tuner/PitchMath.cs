namespace DotCharter.Tone.Audio.Tuner;

/// <summary>
///     Shared, allocation-free numeric helpers for the managed tuner core
///     (see <see cref="IPitchDetector"/> for the 0.18 native-graduation
///     framing — these formulas are part of the reference contract).
/// </summary>
internal static class PitchMath
{
    /// <summary>
    ///     Copies <paramref name="source"/> into <paramref name="destination"/>
    ///     with the window mean (DC) removed and gates on the AC RMS.
    ///     Returns false when the window is quieter than
    ///     <paramref name="minRms"/> (silence — caller reports no pitch).
    /// </summary>
    public static bool RemoveDcAndGate(ReadOnlySpan<float> source, float[] destination, float minRms)
    {
        double sum = 0;
        for (var i = 0; i < source.Length; i++) sum += source[i];
        var mean = (float)(sum / source.Length);

        double sumSquares = 0;
        for (var i = 0; i < source.Length; i++)
        {
            var v = source[i] - mean;
            destination[i] = v;
            sumSquares += (double)v * v;
        }

        var rms = Math.Sqrt(sumSquares / source.Length);
        return rms >= minRms;
    }

    /// <summary>
    ///     3-point parabolic interpolation of an extremum at integer index
    ///     <paramref name="index"/> of <paramref name="values"/> (which must
    ///     have valid neighbours at ±1). Yields the refined fractional
    ///     position and extremum value. Works for maxima and minima alike.
    /// </summary>
    public static void ParabolicPeak(double[] values, int index, out double position, out double value)
    {
        var a = values[index - 1];
        var b = values[index];
        var c = values[index + 1];

        var denominator = a - 2.0 * b + c;
        if (Math.Abs(denominator) < 1e-12)
        {
            position = index;
            value = b;
            return;
        }

        var delta = 0.5 * (a - c) / denominator;
        // A well-formed extremum interpolates within ±0.5 samples; anything
        // else means the three points don't bracket one — keep the raw bin.
        if (Math.Abs(delta) > 0.5) delta = 0;

        position = index + delta;
        value = b - 0.25 * (a - c) * delta;
    }

    /// <summary>Signed cent distance of <paramref name="frequencyHz"/> from
    /// <paramref name="referenceHz"/>.</summary>
    public static double CentsBetween(double frequencyHz, double referenceHz)
        => 1200.0 * Math.Log2(frequencyHz / referenceHz);

    /// <summary>Frequency shifted by a signed number of cents.</summary>
    public static double ShiftByCents(double frequencyHz, double cents)
        => frequencyHz * Math.Pow(2.0, cents / 1200.0);
}
