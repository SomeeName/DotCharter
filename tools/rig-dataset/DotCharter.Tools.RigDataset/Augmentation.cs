using System.Globalization;

namespace DotCharter.Tools.RigDataset;

/// <summary>
///     Everything that happens to a rendered clip AFTER the rig and BEFORE the
///     manifest. Every stage is drawn from the clip's own <see cref="Random" /> and
///     records what it did on <c>RigDatasetRow.Augmentations</c>, because an
///     augmentation nobody can see in the manifest is an unexplained distribution
///     shift when the model underperforms on one slice.
///     <para>
///         <b>Every tag is formatted INVARIANT.</b> The tool sets
///         <c>InvariantGlobalization</c>, but this type is also called from the test
///         host and from anything else that links it, and on a German machine
///         <c>$"{db:F1}"</c> writes <c>-1,2</c>. A manifest whose tags depend on the
///         renderer's locale is a manifest two machines cannot compare, and
///         <c>RigDatasetRenderTests.NormalisationHitsItsTarget_…</c> is what found it.
///     </para>
/// </summary>
public static class Augmentation
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>The DI pool's and the corpus's reference level. −18 dBFS RMS is the
    /// study's figure and the one the DI pool is normalised to, so a rig's own output
    /// level is measured against a fixed input.</summary>
    public const float TargetRmsDbfs = -18f;

    public static float Rms(ReadOnlySpan<float> x)
    {
        if (x.Length == 0) return 0f;
        double acc = 0;
        foreach (var v in x) acc += (double)v * v;
        return (float)Math.Sqrt(acc / x.Length);
    }

    public static float Peak(ReadOnlySpan<float> x)
    {
        var p = 0f;
        foreach (var v in x) p = MathF.Max(p, MathF.Abs(v));
        return p;
    }

    public static float Db(float linear) => linear <= 1e-12f ? -240f : 20f * MathF.Log10(linear);

    public static float FromDb(float db) => MathF.Pow(10f, db / 20f);

    /// <summary>Scales to a target RMS. The jitter is the point: a corpus normalised
    /// to exactly one level teaches the model that level, and at inference the guitar
    /// stem arrives at whatever the separator felt like.</summary>
    public static string Normalise(float[] x, Random rng, float targetDbfs, float jitterDb)
    {
        var jitter = (float)(rng.NextDouble() * 2d - 1d) * jitterDb;
        var target = targetDbfs + jitter;
        var rms = Rms(x);
        if (rms <= 1e-9f) return "norm:silent";
        var gain = FromDb(target) / rms;
        for (var i = 0; i < x.Length; i++) x[i] *= gain;
        return string.Create(Inv, $"norm:{target:F1}dBFS");
    }

    /// <summary>
    ///     A first-order tilt: a low shelf and a high shelf that move in opposite
    ///     directions about a 1 kHz pivot, bounded at ±<paramref name="maxDb" />. It
    ///     stands in for everything between a mix bus and a listener — a different
    ///     guitar, a different cabinet mic, a different mastering chain — without ever
    ///     changing WHICH modules are in the rig, which is what makes it a safe
    ///     augmentation for this label.
    /// </summary>
    public static string Tilt(float[] x, int sampleRate, Random rng, float maxDb)
    {
        var db = (float)(rng.NextDouble() * 2d - 1d) * maxDb;
        if (MathF.Abs(db) < 0.05f) return "tilt:0.0dB";

        // One-pole split at the pivot: low = lp, high = x - lp. Recombine with
        // complementary gains, so the tilt is exactly ±db across the band and unity
        // at the pivot.
        const float pivotHz = 1000f;
        var a = MathF.Exp(-2f * MathF.PI * pivotHz / sampleRate);
        var lowGain = FromDb(db * 0.5f);
        var highGain = FromDb(-db * 0.5f);

        var lp = 0f;
        for (var i = 0; i < x.Length; i++)
        {
            lp = (1f - a) * x[i] + a * lp;
            x[i] = lp * lowGain + (x[i] - lp) * highGain;
        }

        return string.Create(Inv, $"tilt:{db:+0.0;-0.0}dB");
    }

    /// <summary>Mixes a bed under the clip at a bounded, random level. This is the
    /// cheap half of "the model will hear other instruments"; the expensive half is
    /// the separator.</summary>
    public static string Bleed(float[] x, float[] bed, Random rng, float minDb, float maxDb)
    {
        var rel = minDb + (float)rng.NextDouble() * (maxDb - minDb);
        var bedRms = Rms(bed);
        var clipRms = Rms(x);
        if (bedRms <= 1e-9f || clipRms <= 1e-9f) return "bleed:skipped-silent";

        var gain = clipRms * FromDb(rel) / bedRms;
        var n = Math.Min(x.Length, bed.Length);
        for (var i = 0; i < n; i++) x[i] += bed[i] * gain;
        return string.Create(Inv, $"bleed:{rel:F0}dB");
    }

    // ------------------------------------------------------------- DI stage
    // The study's §3.1 answer to "there are only ~5 h of licence-clean DI": augment
    // the DI itself, BEFORE the rig, so the rig's label still describes the audio
    // exactly. All three are recorded, because a DI augmentation is invisible in the
    // wet clip and would otherwise be an unexplained slice.

    /// <summary>
    ///     Resamples <paramref name="src" /> into <paramref name="dst" /> by
    ///     <c>2^(semitones/12)</c>, with linear interpolation, zero-padding when the
    ///     source runs out.
    ///     <para>
    ///         <b>This shifts pitch by changing PLAYBACK RATE, so it shifts the
    ///         formants and the tempo with it, and that is deliberate.</b> The
    ///         alternative on a 5 s window is a phase vocoder, which smears exactly
    ///         the transients a drive keys off — and a resample is what physically
    ///         happens when the same riff is played on a guitar tuned a tone down,
    ///         which is the variation being modelled. The tempo change is harmless
    ///         here: the corpus is 5 s of playing with no grid.
    ///     </para>
    /// </summary>
    /// <param name="src">The raw DI window; needs <c>dst.Length × ratio</c> samples.</param>
    /// <param name="dst">The output window.</param>
    /// <param name="semitones">−2…+2, typically.</param>
    /// <returns>The tag.</returns>
    public static string PitchShiftInto(float[] src, float[] dst, double semitones)
    {
        var ratio = Math.Pow(2d, semitones / 12d);
        for (var i = 0; i < dst.Length; i++)
        {
            var pos = i * ratio;
            var i0 = (int)pos;
            var frac = (float)(pos - i0);
            var a = i0 < src.Length ? src[i0] : 0f;
            var b = i0 + 1 < src.Length ? src[i0 + 1] : 0f;
            dst[i] = a + (b - a) * frac;
        }

        return string.Create(Inv, $"di-pitch:{semitones:+0.00;-0.00}st");
    }

    /// <summary>A bounded level offset on the DI, AFTER its normalisation. This is
    /// not cosmetic: how hard the DI hits a drive is the difference between the same
    /// rig sounding like an edge-of-breakup and a fuzz, and a corpus whose DI always
    /// arrives at exactly −18 dBFS never shows the model that.</summary>
    /// <param name="x">The DI. Scaled in place.</param>
    /// <param name="rng">The clip's generator.</param>
    /// <param name="maxDb">Half-width of the window, e.g. 6.</param>
    public static string DiLevel(float[] x, Random rng, float maxDb)
    {
        var db = (float)(rng.NextDouble() * 2d - 1d) * maxDb;
        var g = FromDb(db);
        for (var i = 0; i < x.Length; i++) x[i] *= g;
        return string.Create(Inv, $"di-level:{db:+0.0;-0.0}dB");
    }

    /// <summary>
    ///     A feed-forward comb standing in for moving the pickup along the string —
    ///     bridge/middle/neck and the positions between them.
    ///     <para>
    ///         <b>It is an approximation and this comment is the place that says so.</b>
    ///         A real pickup samples the string at a fixed fraction of its length and
    ///         therefore nulls every harmonic whose node lands on it, which for a
    ///         given open string IS a comb — but the comb's spacing follows the
    ///         STRING, not the recording, and we do not know which string was played.
    ///         A fixed-delay comb in the 0.4–2.6 ms range produces the same family of
    ///         nulls across the register a guitar covers, which is the timbral
    ///         variation wanted; it is not a physical model and must not be described
    ///         as one.
    ///     </para>
    /// </summary>
    /// <param name="x">The DI. Filtered in place.</param>
    /// <param name="sampleRate">48 000.</param>
    /// <param name="rng">The clip's generator.</param>
    public static string PickupComb(float[] x, int sampleRate, Random rng)
    {
        var ms = 0.4 + rng.NextDouble() * 2.2;
        var depth = 0.30f + (float)rng.NextDouble() * 0.40f;
        var d = Math.Max(1, (int)Math.Round(ms * 1e-3 * sampleRate));
        if (d >= x.Length) return "pickup-comb:skipped-short";

        // Backwards, so the delayed sample read is always the ORIGINAL one.
        for (var i = x.Length - 1; i >= d; i--) x[i] -= depth * x[i - d];
        return string.Create(Inv, $"pickup-comb:{ms:F2}ms/{depth:F2}");
    }

    /// <summary>Every stage-B tag starts with this. Resuming the separate pass is
    /// exactly "does this row already carry one?", so the prefix is a CONTRACT and
    /// not a formatting detail — <c>separated:failed</c> and
    /// <c>separated:unavailable</c> have to match it too, or a row that cannot be
    /// separated is retried on every resume for ever.</summary>
    public const string SeparatedPrefix = "separated:";

    /// <summary>True when stage B has already had its go at this row.</summary>
    /// <param name="tags">The row's augmentation tags.</param>
    public static bool IsSeparated(IEnumerable<string> tags)
        => tags.Any(t => t.StartsWith(SeparatedPrefix, StringComparison.Ordinal));

    /// <summary>
    ///     Mixes the clip INTO a bed at a realistic guitar-to-mix level — the
    ///     opposite direction from <see cref="Bleed" />, and the difference is the
    ///     whole of stage B. <see cref="Bleed" /> puts a bed 25–40 dB UNDER the
    ///     guitar, so the guitar is still plainly the subject; this puts the guitar
    ///     between 12 dB under the bed and 3 dB over it, which is where a guitar
    ///     actually sits in a mix and the only input on which htdemucs behaves the
    ///     way it will at inference.
    /// </summary>
    /// <param name="x">The clip. Mixed in place.</param>
    /// <param name="bed">The bed window; may be shorter, and is then zero-extended.</param>
    /// <param name="rng">The row's own generator.</param>
    /// <param name="minDb">Quietest the guitar may sit relative to the bed.</param>
    /// <param name="maxDb">Loudest.</param>
    /// <returns>The tag recording the level that was drawn.</returns>
    public static string MixIntoBed(float[] x, float[] bed, Random rng, float minDb, float maxDb)
    {
        var rel = minDb + (float)rng.NextDouble() * (maxDb - minDb);
        var bedRms = Rms(bed);
        var clipRms = Rms(x);
        if (bedRms <= 1e-9f || clipRms <= 1e-9f) return "mix:skipped-silent";

        // `rel` is the GUITAR's level relative to the bed, so the BED is scaled by
        // −rel: at rel = +3 dB the guitar ends up 3 dB over the bed.
        var gain = clipRms * FromDb(-rel) / bedRms;
        var n = Math.Min(x.Length, bed.Length);
        for (var i = 0; i < n; i++) x[i] += bed[i] * gain;
        return string.Create(Inv, $"mix:{rel:+0.0;-0.0}dB");
    }

    /// <summary>Trims a clip back under full scale. A corpus whose wet clips clip is a
    /// corpus that teaches the model to hear a 16-bit wrap as distortion.</summary>
    public static string? TrimPeak(float[] x, float ceiling = 0.97f)
    {
        var peak = Peak(x);
        if (peak <= ceiling) return null;
        var g = ceiling / peak;
        for (var i = 0; i < x.Length; i++) x[i] *= g;
        return string.Create(Inv, $"peak-trim:{Db(g):F1}dB");
    }

    /// <summary>Fraction of samples within 0.1 dB of full scale — the study's reject
    /// rule for a render the rig drove into the ceiling.</summary>
    public static double ClippedFraction(ReadOnlySpan<float> x)
    {
        if (x.Length == 0) return 1d;
        var n = 0;
        foreach (var v in x)
        {
            if (MathF.Abs(v) >= 0.988f)
                n++;
        }

        return n / (double)x.Length;
    }

    /// <summary>
    ///     True when the rig did essentially nothing — every module bypassed, or a
    ///     chain of unity gains. Compared after level normalisation, so it is a
    ///     SHAPE test rather than a level one: a correlation of ~1 against the DI
    ///     means the clip carries no information about the label at all, and a corpus
    ///     full of those is a corpus that teaches the model to guess.
    /// </summary>
    public static bool IsIndistinguishableFrom(ReadOnlySpan<float> wet, ReadOnlySpan<float> dry)
    {
        var n = Math.Min(wet.Length, dry.Length);
        if (n == 0) return true;
        double sw = 0, sd = 0, swd = 0;
        for (var i = 0; i < n; i++)
        {
            sw += (double)wet[i] * wet[i];
            sd += (double)dry[i] * dry[i];
            swd += (double)wet[i] * dry[i];
        }

        if (sw <= 1e-18 || sd <= 1e-18) return true;
        var corr = swd / Math.Sqrt(sw * sd);
        return corr > 0.9995d;
    }
}
