using JuceSharp.Dsp;

namespace DotCharter.Services.Ai.Match.Similarity;

/// <summary>
///     The raw-audio loss the search lane refines against: multi-scale spectral L1,
///     linear plus log magnitude, at four window sizes.
///     <para>
///         <b>Only valid between two renders of the SAME dry take.</b> It compares
///         spectrogram frames at the same time index, so it hears a different RIFF as
///         a colossal difference and would drive an optimiser straight into a wrong
///         rig. Use it to ask "does candidate rig A put the reference DI closer to
///         candidate rig B's render than rig C does" — never to ask "does this rig
///         sound like the record". <see cref="ToneSimilarity" /> is the metric for
///         the second question, and it is the one every reported number uses.
///     </para>
///     <para>
///         Why both terms: the LINEAR term is dominated by whichever bands are loud
///         and tracks gross spectral balance; the LOG term weights every band alike
///         and is what hears a reverb tail, a noise floor or the quiet upper-order
///         products of a drive. The pair is the standard multi-resolution STFT loss
///         from the neural-audio literature, and it is effect-dependent by nature —
///         excellent for filter- and drive-like chains, weak for modulation, which is
///         exactly why it is the REFINEMENT loss and not the ranking one.
///     </para>
///     <para>
///         Both clips are RMS-normalised first, so this measures shape, not level.
///         Lengths may differ; the shorter one bounds the comparison.
///     </para>
/// </summary>
public sealed class MultiScaleSpectralDistance
{
    /// <summary>Window sizes, in samples at the working rate. The 256 window hears
    /// the pick attack; the 4096 hears the body of a chord.</summary>
    public static readonly int[] Scales = [256, 1024, 2048, 4096];

    /// <summary>
    ///     Distance between two clips, 0 for identical audio. Resamples both to
    ///     <see cref="ToneFeatureExtractor.WorkingRate" /> and RMS-normalises them.
    /// </summary>
    public double Distance(MonoAudio a, MonoAudio b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);

        var left = LevelMatch.ToAnalysisLevel(MonoResampler.To(a, ToneFeatureExtractor.WorkingRate));
        var right = LevelMatch.ToAnalysisLevel(MonoResampler.To(b, ToneFeatureExtractor.WorkingRate));

        var frames = Math.Min(left.Samples.Length, right.Samples.Length);
        if (frames == 0) return 0;

        var x = left.Samples.AsSpan(0, frames);
        var y = right.Samples.AsSpan(0, frames);

        double total = 0;
        var used = 0;
        foreach (var scale in Scales)
        {
            if (frames < scale) continue;
            total += ScaleDistance(x, y, scale);
            used++;
        }

        return used == 0 ? 0 : total / used;
    }

    private static double ScaleDistance(ReadOnlySpan<float> a, ReadOnlySpan<float> b, int scale)
    {
        var builder = new JuceSpectrogramBuilder(scale, scale / 4);
        var sa = builder.Build(a, ToneFeatureExtractor.WorkingRate);
        var sb = new JuceSpectrogramBuilder(scale, scale / 4).Build(b, ToneFeatureExtractor.WorkingRate);

        var frames = Math.Min(sa.FrameCount, sb.FrameCount);
        if (frames == 0) return 0;

        double linearNum = 0, linearDen = 0, logSum = 0;
        var bins = 0;
        for (var f = 0; f < frames; f++)
        {
            var fa = sa.Frames[f];
            var fb = sb.Frames[f];
            for (var i = 0; i < fa.Length; i++)
            {
                linearNum += Math.Abs(fa[i] - (double)fb[i]);
                linearDen += fa[i];
                logSum += Math.Abs(Math.Log(Math.Max(fa[i], 1e-7)) - Math.Log(Math.Max(fb[i], 1e-7)));
                bins++;
            }
        }

        var linear = linearDen > 0 ? linearNum / linearDen : 0;
        var log = bins > 0 ? logSum / bins : 0;
        return linear + log;
    }
}
