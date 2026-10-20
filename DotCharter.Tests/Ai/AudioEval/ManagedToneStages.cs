using DotCharter.Services.Ai.Match;
using DotCharter.Services.Ai.Match.Similarity;

namespace DotCharter.Tests.Ai.AudioEval;

/// <summary>
///     Three crude tone stages in pure managed code, for the tests that must prove
///     what the METRIC hears without depending on the native engine.
///     <para>
///         They are not the rig and are not meant to be. The point of the
///         content-invariance tests is that "a clean amp" and "a fuzz" are audibly
///         different and that the distance says so by a wide margin — a one-pole
///         filter and a hard <c>tanh</c> are different by that measure, and they run
///         in the default gate on a machine with no <c>juce_sharp.dll</c> at all. The
///         native renderer gets its own tests.
///     </para>
/// </summary>
internal static class ManagedToneStages
{
    /// <summary>A clean amp: one-pole low-pass at 5 kHz, a touch of low cut, unity
    /// gain, no saturation whatsoever.</summary>
    public static MonoAudio Clean(MonoAudio input) => Filtered(input, 5000, 90, 1.0, 0.0);

    /// <summary>A fuzz: 30 dB in and hard <c>tanh</c> saturation, through the SAME
    /// filter as <see cref="Clean" />. Identical EQ on purpose — the pair then differs
    /// by distortion and nothing else, so a test over it is a test of the distortion
    /// and dynamics blocks rather than of a filter difference the mel block would have
    /// picked up anyway.</summary>
    public static MonoAudio Fuzz(MonoAudio input) => Filtered(input, 5000, 90, 30.0, 1.0);

    /// <summary>A clean boost: the SAME tone, <paramref name="db" /> louder.</summary>
    public static MonoAudio Gain(MonoAudio input, double db)
    {
        var g = Math.Pow(10.0, db / 20.0);
        var samples = new float[input.Samples.Length];
        for (var i = 0; i < samples.Length; i++) samples[i] = (float)(input.Samples[i] * g);
        return new MonoAudio(samples, input.SampleRate);
    }

    /// <summary>
    ///     A feedback delay, level-matched like the others. Used to prove the metric
    ///     can hear a time-domain effect at all — the one thing a long-term average
    ///     spectrum is blind to.
    /// </summary>
    public static MonoAudio Delay(MonoAudio input, TimeSpan time, double feedback)
    {
        var samples = (float[])input.Samples.Clone();
        var lag = (int)(time.TotalSeconds * input.SampleRate);
        if (lag > 0)
        {
            for (var i = lag; i < samples.Length; i++)
                samples[i] += (float)(samples[i - lag] * feedback);
        }

        return LevelMatch.ToAnalysisLevel(new MonoAudio(samples, input.SampleRate));
    }

    private static MonoAudio Filtered(MonoAudio input, double lowPassHz, double highPassHz,
        double drive, double saturation)
    {
        var rate = input.SampleRate;
        var lp = Math.Exp(-2.0 * Math.PI * lowPassHz / rate);
        var hp = Math.Exp(-2.0 * Math.PI * highPassHz / rate);

        var samples = new float[input.Samples.Length];
        double lpState = 0, hpState = 0;
        for (var i = 0; i < samples.Length; i++)
        {
            var x = input.Samples[i] * drive;
            if (saturation > 0) x = Math.Tanh(x) * 0.7;

            lpState = x * (1 - lp) + lpState * lp;
            hpState = lpState * (1 - hp) + hpState * hp;
            samples[i] = (float)(lpState - hpState);
        }

        // Level-matched, exactly as RigRenderer level-matches a real render: without it
        // a fuzz arrives louder than a clean amp and the tests would be leaning on a
        // level difference the product never presents to the metric.
        return LevelMatch.ToAnalysisLevel(new MonoAudio(samples, rate));
    }
}
