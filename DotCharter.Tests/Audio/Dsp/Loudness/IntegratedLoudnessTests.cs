using DotCharter.Tone.Audio.Dsp.Loudness;

namespace DotCharter.Tests.Audio.Dsp.Loudness;

/// <summary>
///     The loudness meter the Rocksmith-tone levelling stands on, held to closed-form
///     signals: BS.1770's own calibration point, both gates, the K-weighting's tilt
///     and sample-rate independence.
/// </summary>
public sealed class IntegratedLoudnessTests
{
    private static float[] Sine(double hz, double seconds, double amplitude, int rate = 48000)
    {
        var x = new float[(int)(seconds * rate)];
        for (var i = 0; i < x.Length; i++) x[i] = (float)(amplitude * Math.Sin(2 * Math.PI * hz * i / rate));
        return x;
    }

    private static float[] Concat(params float[][] parts) => parts.SelectMany(p => p).ToArray();

    [Fact]
    public void AFullScale1kHzSine_ReadsMinus3Lufs()
        // BS.1770-4's calibration: a 0 dBFS 1 kHz sine in one channel is -3.01 LKFS.
        => Assert.Equal(-3.01, IntegratedLoudness.Measure(Sine(1000, 10, 1.0), 48000), 1);

    [Fact]
    public void Silence_IsGatedOut_AbsoluteGate()
    {
        var tone = Sine(1000, 5, 0.1);
        var withSilence = Concat(tone, new float[5 * 48000]);
        // Only the few 400 ms blocks that straddle the edge count, a fraction of a LU.
        Assert.InRange(IntegratedLoudness.Measure(tone, 48000) - IntegratedLoudness.Measure(withSilence, 48000), 0.0, 0.25);
    }

    [Fact]
    public void AMuchQuieterPassage_IsGatedOut_RelativeGate_ButANearOneCounts()
    {
        var loud = Sine(1000, 5, 0.5);
        var loudAlone = IntegratedLoudness.Measure(loud, 48000);

        // 20 dB under: below the -10 LU relative gate, so it does not pull the reading down.
        var farUnder = IntegratedLoudness.Measure(Concat(loud, Sine(1000, 5, 0.05)), 48000);
        Assert.InRange(loudAlone - farUnder, 0.0, 0.25); // only the straddling blocks

        // 6 dB under: above the gate, so the reading drops (energy mean of the two).
        var nearUnder = IntegratedLoudness.Measure(Concat(loud, Sine(1000, 5, 0.25)), 48000);
        Assert.InRange(loudAlone - nearUnder, 1.5, 2.5);
    }

    [Fact]
    public void KWeighting_HearsThePresenceBand_OverTheFundamental()
    {
        var at1k = IntegratedLoudness.Measure(Sine(1000, 5, 0.25), 48000);
        var at4k = IntegratedLoudness.Measure(Sine(4000, 5, 0.25), 48000);
        var at30 = IntegratedLoudness.Measure(Sine(30, 5, 0.25), 48000);
        Assert.InRange(at4k - at1k, 2.8, 4.0);   // the +4 dB shelf, mostly in by 4 kHz
        Assert.True(at30 < at1k - 1.5, $"30 Hz {at30:F2} vs 1 kHz {at1k:F2}: the 38 Hz high-pass must cut it");
    }

    [Fact]
    public void TheSameSignal_ReadsTheSame_At44k1And48k()
        => Assert.Equal(
            IntegratedLoudness.Measure(Sine(2000, 5, 0.3, 48000), 48000),
            IntegratedLoudness.Measure(Sine(2000, 5, 0.3, 44100), 44100), 1);

    [Fact]
    public void ShorterThanOneBlock_OrSilent_IsSilence()
    {
        Assert.Equal(IntegratedLoudness.Silence, IntegratedLoudness.Measure(Sine(1000, 0.3, 1.0), 48000));
        Assert.Equal(IntegratedLoudness.Silence, IntegratedLoudness.Measure(new float[48000], 48000));
    }

    [Fact]
    public void ThePhrase_IsDeterministic_AtTheReferencePeak()
    {
        var phrase = CalibrationPhrase.Samples.ToArray();
        Assert.Equal(CalibrationPhrase.TotalSamples, phrase.Length);
        Assert.Equal(-18.0, IntegratedLoudness.PeakDbfs(phrase), 2);
        // A fixed seed: two reads are the same buffer content, and the meter agrees.
        Assert.Equal(phrase, CalibrationPhrase.Samples.ToArray());
        Assert.True(double.IsFinite(IntegratedLoudness.Measure(phrase, CalibrationPhrase.SampleRate)));
    }
}
