namespace DotCharter.Services.Ai.Match.Sections;

/// <summary>What the gain heuristic measured, and what it concluded.</summary>
/// <param name="CrestDb">Peak over RMS, in dB. Distortion compresses a signal, so a
/// driven part has a LOW crest and a clean one a high one.</param>
/// <param name="HighRatio">Energy above <see cref="SectionGainHeuristic.HighBandHz" />
/// over total energy. Distortion manufactures harmonics, so a driven part carries
/// more of its energy up there.</param>
/// <param name="Suffix">The label suffix, or null when the two measurements do not
/// agree well enough to say anything.</param>
public readonly record struct SectionGainReading(double CrestDb, double HighRatio, string? Suffix);

/// <summary>
///     <b>A labelled heuristic, and nothing more.</b> It appends "(clean)" or
///     "(driven)" to a proposed section's name so the list reads like the owner's own
///     description of a song — "0:10–0:30 clean, 0:30–0:50 distortion" — and it is
///     wrong sometimes. It is deliberately ONE class with ONE entry point rather than
///     two lines inside the proposer, because a guess that is spread around is a guess
///     nobody can find, re-tune or delete.
///     <para>
///         <b>It is never allowed to decide anything.</b> Nothing downstream reads the
///         suffix: the matcher measures the audio itself and will happily return a
///         high-gain rig for a section this called clean. The suffix is a word in a
///         label, and the user can retype it.
///     </para>
///     <para>
///         Two measurements, and BOTH must agree before it speaks. Crest factor alone
///         calls a palm-muted clean part driven (muting flattens the envelope exactly
///         as clipping does); high-band energy alone calls a bright clean single-coil
///         driven. Requiring both is what keeps it quiet — returning no suffix at all
///         is a perfectly good answer and the common one for anything in between.
///     </para>
/// </summary>
public static class SectionGainHeuristic
{
    /// <summary>Appended to a label when the part reads as clean.</summary>
    public const string CleanSuffix = " (clean)";

    /// <summary>Appended to a label when the part reads as driven.</summary>
    public const string DrivenSuffix = " (driven)";

    /// <summary>Where the "high band" starts. Above the fundamental range of every
    /// guitar and bass, and squarely in the region a clipper fills in.</summary>
    public const double HighBandHz = 2000.0;

    /// <summary>At or below this crest factor the envelope is flat enough to be
    /// clipped rather than played.</summary>
    public const double DrivenCrestDb = 11.0;

    /// <summary>At or above this crest factor the part has the dynamic range a clean
    /// signal keeps.</summary>
    public const double CleanCrestDb = 13.0;

    /// <summary>
    ///     At or above this share of energy above <see cref="HighBandHz" /> the part
    ///     carries manufactured harmonics.
    ///     <para>
    ///         <b>This and <see cref="CleanHighRatio" /> are the least certain numbers
    ///         in the lane, and they are small for a real reason</b>: a guitar's energy
    ///         lives below 1 kHz and a cabinet rolls off hard above 4 kHz, so even a
    ///         heavily distorted part keeps only a percent or two of its energy above
    ///         2 kHz. Set against the lane's reference signals, measured —
    ///         0.00236 for the clean plucks and 0.00943 for the same playing hard-clipped
    ///         (<c>SectionGainHeuristicTests</c> pins both). RE-MEASURE these against
    ///         real separated stems before trusting the suffix on a corpus song; they
    ///         are the first thing here that should move.
    ///     </para>
    /// </summary>
    public const double DrivenHighRatio = 0.006;

    /// <summary>At or below this share the part has no high-order content at all.
    /// See <see cref="DrivenHighRatio" /> for how it was set.</summary>
    public const double CleanHighRatio = 0.003;

    /// <summary>
    ///     The suffix for a window of audio, or null when it declines to say. Pure
    ///     and deterministic: same samples, same answer, on every machine.
    /// </summary>
    public static string? Suffix(MonoAudio audio) => Measure(audio).Suffix;

    /// <summary>
    ///     The measurement behind <see cref="Suffix" />, for a report or a test that
    ///     wants to know WHY it said what it said.
    /// </summary>
    public static SectionGainReading Measure(MonoAudio audio)
    {
        ArgumentNullException.ThrowIfNull(audio);

        double sq = 0, peak = 0;
        var n = 0;
        foreach (var s in audio.Samples)
        {
            if (!float.IsFinite(s)) continue;
            sq += (double)s * s;
            peak = Math.Max(peak, Math.Abs(s));
            n++;
        }

        // Silence has no timbre. Saying nothing is the honest answer and keeps the
        // caller from labelling a gap between two parts.
        if (n == 0 || sq <= 0 || peak <= 0) return new SectionGainReading(0, 0, null);

        var rms = Math.Sqrt(sq / n);
        var crestDb = 20.0 * Math.Log10(peak / Math.Max(rms, 1e-12));
        var highRatio = HighBandEnergyRatio(audio, sq);

        string? suffix = null;
        if (crestDb <= DrivenCrestDb && highRatio >= DrivenHighRatio) suffix = DrivenSuffix;
        else if (crestDb >= CleanCrestDb || highRatio <= CleanHighRatio) suffix = CleanSuffix;

        return new SectionGainReading(crestDb, highRatio, suffix);
    }

    /// <summary>
    ///     Energy above <see cref="HighBandHz" /> as a share of the total, through one
    ///     Butterworth high-pass biquad.
    ///     <para>
    ///         A biquad rather than an FFT on purpose: this runs once per proposed
    ///         section, needs no windowing decisions, and a 12 dB/octave skirt is
    ///         plenty for a ratio that is being compared against 0.003 and 0.006. An FFT
    ///         here would be a more precise answer to a question the thresholds are
    ///         not precise about.
    ///     </para>
    /// </summary>
    private static double HighBandEnergyRatio(MonoAudio audio, double totalEnergy)
    {
        var rate = audio.SampleRate;
        if (rate is <= 0 or <= (int)(HighBandHz * 2.0)) return 0;

        var w0 = 2.0 * Math.PI * HighBandHz / rate;
        var cos = Math.Cos(w0);
        var alpha = Math.Sin(w0) / (2.0 * (1.0 / Math.Sqrt(2.0)));

        var a0 = 1.0 + alpha;
        var b0 = (1.0 + cos) / 2.0 / a0;
        var b1 = -(1.0 + cos) / a0;
        var b2 = b0;
        var a1 = -2.0 * cos / a0;
        var a2 = (1.0 - alpha) / a0;

        double x1 = 0, x2 = 0, y1 = 0, y2 = 0, high = 0;
        foreach (var sample in audio.Samples)
        {
            var x = float.IsFinite(sample) ? sample : 0.0;
            var y = b0 * x + b1 * x1 + b2 * x2 - a1 * y1 - a2 * y2;
            x2 = x1; x1 = x;
            y2 = y1; y1 = y;
            high += y * y;
        }

        return totalEnergy > 0 ? Math.Clamp(high / totalEnergy, 0.0, 1.0) : 0.0;
    }
}
