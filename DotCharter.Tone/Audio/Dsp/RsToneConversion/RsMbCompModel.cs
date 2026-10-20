// RsMbCompModel: Bass_Pedal_MBComp as the player's own bank describes it — the fitted model the converter evaluates; the plugin that reads the install decodes the chain and makes the fit. No number of the game's is in this file.
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;

namespace DotCharter.Tone.Audio.Dsp.RsToneConversion;

/// <summary>
///     <c>Bass_Pedal_MBComp</c> as the converter plays it: one broadband <c>compressor</c>
///     whose gain computer is FITTED, per Compress setting, to the static curve of the
///     pedal's own chain read from the player's install (a plugin decodes the chain and makes the fit).
///     The chain's cut depends only on the level its meters see, so the model keeps that
///     cut sampled (<see cref="Cut" />) as a function of input level plus the input gain
///     Compress puts in front of the meters (<see cref="InputDb" />); Compress then moves
///     the window of that curve the playing range sees (the threshold slides) and the
///     make-up (<see cref="MakeupDb" />). Every field comes from the install at run time;
///     without one the converter keeps its generic mapping.
/// </summary>
/// <param name="InputDb">The Compress curves in front of the first meter, dB (summed).</param>
/// <param name="OutputDb">The Compress curves after the last meter, dB (summed).</param>
/// <param name="FixedDb">The chain's low-level gain at the test frequency that no knob moves, dB.</param>
/// <param name="CutLowDb">The level (input plus input gain, dB) of <see cref="Cut" />'s first sample.</param>
/// <param name="Cut">The chain's level-dependent gain, dB, in 1 dB steps from <see cref="CutLowDb" />.</param>
/// <param name="AttackMs">Recovery Speed: the first meter's attack, ms.</param>
/// <param name="ReleaseMs">The first meter's release, ms.</param>
/// <param name="Rms">Whether the first meter reads RMS (else peak).</param>
internal sealed record RsMbCompModel(
    WwiseCurve[] InputDb,
    WwiseCurve[] OutputDb,
    float FixedDb,
    float CutLowDb,
    float[] Cut,
    WwiseCurve AttackMs,
    float ReleaseMs,
    bool Rms)
{
    /// <summary>The <c>compressor</c> node's threshold range, dBFS.</summary>
    internal const double NodeThresholdMinDb = -60.0, NodeThresholdMaxDb = 0.0;

    /// <summary>
    ///     The playing range a fit covers: a sine's RMS level at the rig's input, dBFS (the
    ///     converter's choice: a full strum PEAKS near the rig's -18 dBFS reference). Above
    ///     it the game's cut can run out where a compressor keeps compressing, so a fit over
    ///     every level would trade the playing range for levels nobody plays.
    /// </summary>
    internal const double PlayingLowDb = -60.0, PlayingHighDb = -18.0;

    private static readonly ConditionalWeakTable<RsMbCompModel, ConcurrentDictionary<float, (float, float, float, float)>> Fits = [];

    /// <summary>The input gain Compress puts in front of the meters, dB.</summary>
    public float InputGainDb(float compress) => Sum(InputDb, compress);

    /// <summary>The chain's whole low-level gain at <paramref name="compress" />, dB.</summary>
    public float MakeupDb(float compress) => FixedDb + InputGainDb(compress) + Sum(OutputDb, compress);

    /// <summary>The chain's level-dependent gain at level <paramref name="x" /> (input plus
    /// input gain), dB: <see cref="Cut" /> interpolated, held at its ends.</summary>
    public double CutDb(double x)
    {
        if (Cut.Length < 2) return Cut.Length == 0 ? 0.0 : Cut[0];
        var i = Math.Clamp(x - CutLowDb, 0.0, Cut.Length - 1);
        var k = Math.Min((int)i, Cut.Length - 2);
        return Cut[k] + (Cut[k + 1] - Cut[k]) * (i - k);
    }

    /// <summary>
    ///     The compressor's threshold (rig dBFS), ratio, knee and a make-up trim at
    ///     <paramref name="compress" />: the smallest worst-case miss against <see cref="CutDb" />
    ///     over the playing range as Compress's input gain shifts it, the threshold kept
    ///     inside the node's range. The trim adds to <see cref="MakeupDb" />. Memoized.
    /// </summary>
    public (float ThresholdDb, float Ratio, float KneeDb, float TrimDb) GainComputer(float compress)
        => Fits.GetValue(this, _ => new ConcurrentDictionary<float, (float, float, float, float)>()).GetOrAdd(compress, c =>
        {
            var gain = (double)InputGainDb(c);
            var xs = new List<double>();
            var cut = new List<double>();
            for (var x = PlayingLowDb; x <= PlayingHighDb + 1e-9; x += 1.0)
            {
                xs.Add(x + gain);
                cut.Add(CutDb(x + gain));
            }

            var (t, r, w, trim) = FitGainComputer(xs, cut, NodeThresholdMinDb + gain, NodeThresholdMaxDb + gain);
            return ((float)(t - gain), (float)r, (float)w, (float)trim);
        });

    /// <summary>
    ///     The <c>compressor</c> node at Compress <paramref name="compress" /> and Recovery
    ///     Speed <paramref name="rate" />: the fitted threshold, ratio and knee, the chain's
    ///     make-up plus the fit's trim, the first meter's attack, release and detector, each
    ///     clamped to the node's range. <paramref name="notes" /> gets a line for every
    ///     approximation (the broadband fit, Transition Band unused, a value the node cannot
    ///     reach).
    /// </summary>
    public Dictionary<string, float> CompressorParams(float compress, float rate, ICollection<string> notes)
    {
        notes.Add("MB Comp: one broadband compressor fitted to the game's own level-dependent cut (read from your install); " +
                  "the game cuts a band, this compresses all of it");
        notes.Add("MB Comp: Transition Band drives nothing in the game's bank and is not mapped");
        var p = new Dictionary<string, float>(StringComparer.Ordinal);

        void Set(string id, float value, string what)
        {
            p[id] = DcRigTargetCatalog.Clamp(DcRigTargetCatalog.Compressor, id, value);
            if (MathF.Abs(p[id] - value) > 0.05f) notes.Add($"MB Comp: {what} ({value:0.#}) is outside the compressor's range");
        }

        var (threshold, ratio, knee, trim) = GainComputer(compress);
        if (threshold <= NodeThresholdMinDb + 0.05) notes.Add("MB Comp: the game's cut starts under the compressor's lowest threshold");
        Set("thresholdDb", threshold, "the threshold");
        Set("ratio", ratio, "the ratio");
        Set("kneeDb", knee, "the knee");
        Set("makeupDb", MakeupDb(compress) + trim, "the make-up");
        Set("attackMs", AttackMs.Evaluate(rate), "the meter's attack");
        Set("releaseMs", ReleaseMs, "the meter's release");
        p["detector"] = Rms ? 1f : 0f;
        return p;
    }

    /// <summary>
    ///     The static gain of the <c>compressor</c> node's gain computer
    ///     (JuceSharp <c>dcdsp_compressor.h</c>: Giannoulis/Massberg/Reiss soft knee), dB, for
    ///     a detector level of <paramref name="xDb" />: 0 under the knee, a quadratic through
    ///     it, <c>(1/R - 1)(x - T)</c> above it. Make-up is not included.
    /// </summary>
    public static double CompressorGainDb(double xDb, double thresholdDb, double ratio, double kneeDb)
    {
        var d = xDb - thresholdDb;
        var slope = 1.0 / ratio - 1.0;
        if (2.0 * d < -kneeDb) return 0.0;
        if (kneeDb > 0.0 && 2.0 * Math.Abs(d) <= kneeDb)
        {
            var e = d + kneeDb / 2.0;
            return slope * e * e / (2.0 * kneeDb);
        }

        return slope * d;
    }

    /// <summary>The smallest worst-case miss (minimax) over a coarse grid, then a finer one around its best: the
    /// threshold, ratio and knee whose <see cref="RsMbCompModel.CompressorGainDb" />, shifted by
    /// a constant trim, best matches <paramref name="cut" /> at <paramref name="xs" />, inside
    /// the node's ranges.</summary>
    internal static (double Threshold, double Ratio, double Knee, double Trim) FitGainComputer(
        IReadOnlyList<double> xs, IReadOnlyList<double> cut, double minThreshold, double maxThreshold)
    {
        if (minThreshold > maxThreshold) minThreshold = maxThreshold = (minThreshold + maxThreshold) / 2.0;
        var best = (T: -20.0, R: 1.0, W: 0.0, Trim: 0.0);
        var bestErr = double.MaxValue;

        void Try(double t, double r, double w)
        {
            t = Math.Clamp(t, minThreshold, maxThreshold);
            r = Math.Clamp(r, 1.0, 20.0);
            w = Math.Clamp(w, 0.0, 24.0);
            // The trim centres the misses, so the worst one is half their spread.
            var (lo, hi) = (double.MaxValue, double.MinValue);
            for (var i = 0; i < xs.Count && (hi - lo) / 2.0 < bestErr; i++)
            {
                var e = RsMbCompModel.CompressorGainDb(xs[i], t, r, w) - cut[i];
                (lo, hi) = (Math.Min(lo, e), Math.Max(hi, e));
            }

            var err = (hi - lo) / 2.0;
            if (err < bestErr) (bestErr, best) = (err, (t, r, w, -(hi + lo) / 2.0));
        }

        for (var t = minThreshold; t <= maxThreshold + 1e-9; t += 1.0)
        {
            for (var k = 0; k <= 30; k++)
            {
                for (var w = 0.0; w <= 24.0; w += 2.0) Try(t, Math.Pow(20.0, k / 30.0), w);
            }
        }

        var coarse = best;
        for (var t = coarse.T - 1.0; t <= coarse.T + 1.0; t += 0.1)
        {
            for (var k = -10; k <= 10; k++)
            {
                for (var w = coarse.W - 2.0; w <= coarse.W + 2.0; w += 0.25) Try(t, coarse.R * Math.Pow(1.015, k), w);
            }
        }

        return best;
    }

    private static float Sum(WwiseCurve[] curves, float x)
    {
        var s = 0f;
        foreach (var c in curves) s += c.Evaluate(x);
        return s;
    }
}
