namespace DotCharter.Services.Ai.Match.Similarity;

/// <summary>
///     Rate conversion for the MATCH lane's analysis path only — a windowed-sinc
///     interpolator with the anti-alias cutoff following whichever Nyquist is lower.
///     <para>
///         <b>This is not the engine's load resampler and must never be used for
///         playback.</b> <c>JuceSharp.Native</c>'s <c>src/LoadResampler.h</c> owns the
///         file→device conversion (rational polyphase, zero group delay by
///         construction, ×59 faster than the sinc it replaced); this one runs offline
///         on a few seconds of audio in a feature extractor where 20 lines of
///         obviously-correct code beat a fast path nobody will profile.
///     </para>
///     <para>
///         Deterministic: same input, same rate pair, bit-identical output. The
///         extractor's id (<c>tonefx-v1</c>) covers this kernel — changing the taps
///         changes every stored feature vector, so bump the id if you touch it.
///     </para>
/// </summary>
public static class MonoResampler
{
    private const int HalfTaps = 24;

    /// <summary>
    ///     Returns <paramref name="audio" /> at <paramref name="targetRate" />, or the
    ///     same instance when it is already there (the common case — the eval's stems
    ///     and renders are both 48 kHz).
    /// </summary>
    public static MonoAudio To(MonoAudio audio, int targetRate)
    {
        if (targetRate <= 0) throw new ArgumentOutOfRangeException(nameof(targetRate));
        if (audio.SampleRate == targetRate) return audio;
        if (audio.Samples.Length == 0) return new MonoAudio([], targetRate);

        var ratio = targetRate / (double)audio.SampleRate;
        var outLength = Math.Max(1, (int)Math.Round(audio.Samples.Length * ratio));
        var dest = new float[outLength];

        // Downsampling moves the anti-alias corner to the OUTPUT Nyquist; upsampling
        // leaves it at the input's. Getting this backwards aliases the whole top
        // octave into the mel bands the distortion proxy reads.
        var cutoff = Math.Min(1.0, ratio) * 0.95;
        var src = audio.Samples;
        var step = 1.0 / ratio;

        for (var i = 0; i < outLength; i++)
        {
            var centre = i * step;
            var first = (int)Math.Floor(centre) - HalfTaps + 1;
            double acc = 0, norm = 0;
            for (var t = 0; t < HalfTaps * 2; t++)
            {
                var n = first + t;
                if (n < 0 || n >= src.Length) continue;
                var x = n - centre;
                var w = Blackman(x);
                if (w <= 0) continue;
                var h = Sinc(cutoff * x) * cutoff * w;
                acc += src[n] * h;
                norm += h;
            }

            dest[i] = (float)(norm > 1e-9 ? acc / norm : acc);
        }

        return new MonoAudio(dest, targetRate);
    }

    private static double Sinc(double x) =>
        Math.Abs(x) < 1e-9 ? 1.0 : Math.Sin(Math.PI * x) / (Math.PI * x);

    private static double Blackman(double x)
    {
        var a = Math.Abs(x);
        if (a >= HalfTaps) return 0;
        var p = Math.PI * (x + HalfTaps) / HalfTaps;
        return 0.42 - 0.5 * Math.Cos(p) + 0.08 * Math.Cos(2 * p);
    }
}
