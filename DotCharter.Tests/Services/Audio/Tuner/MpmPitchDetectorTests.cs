using DotCharter.Tone.Audio.Tuner;

namespace DotCharter.Tests.Services.Audio.Tuner;

public sealed class MpmPitchDetectorTests
{
    private const int SampleRate = 48000;
    private const int Window = 4096;

    private static MpmPitchDetector Detector(int sampleRate = SampleRate, int window = Window)
        => new(sampleRate, window);

    // Guitar range: 7-string low B (B1) through the high-E string's octave
    // harmonics (E5 = 12th-fret harmonic, E6 = the one above it).
    [Theory]
    [InlineData(61.74)]   // B1, 7-string low B
    [InlineData(82.41)]   // E2
    [InlineData(110.00)]  // A2
    [InlineData(146.83)]  // D3
    [InlineData(196.00)]  // G3
    [InlineData(246.94)]  // B3
    [InlineData(329.63)]  // E4
    [InlineData(659.26)]  // E5 harmonic
    [InlineData(1318.51)] // E6 harmonic
    public void Detect_PureSine_WithinOneCent(double frequency)
    {
        var reading = Detector().Detect(TunerTestSignals.Sine(frequency, SampleRate, Window));

        Assert.True(reading.HasPitch);
        Assert.InRange(Math.Abs(TunerTestSignals.CentsBetween(reading.FrequencyHz, frequency)), 0, 1.0);
        Assert.InRange(reading.Clarity, 0.9, 1.0);
    }

    [Theory]
    [InlineData(44100)]
    [InlineData(48000)]
    [InlineData(96000)]
    public void Detect_SampleRateAgnostic_WithinOneCent(int sampleRate)
    {
        const double frequency = 110.0;
        var reading = Detector(sampleRate)
            .Detect(TunerTestSignals.Sine(frequency, sampleRate, Window));

        Assert.True(reading.HasPitch);
        Assert.InRange(Math.Abs(TunerTestSignals.CentsBetween(reading.FrequencyHz, frequency)), 0, 1.0);
    }

    [Theory]
    [InlineData(82.41)]
    [InlineData(196.00)]
    [InlineData(329.63)]
    public void Detect_HarmonicRichTone_LocksFundamental(double f0)
    {
        var reading = Detector().Detect(TunerTestSignals.Harmonics(f0, SampleRate, Window));

        Assert.True(reading.HasPitch);
        Assert.InRange(Math.Abs(TunerTestSignals.CentsBetween(reading.FrequencyHz, f0)), 0, 1.0);
    }

    [Fact]
    public void Detect_StrongOctaveHarmonic_NoOctaveError()
    {
        const double f0 = 110.0;
        var reading = Detector()
            .Detect(TunerTestSignals.SineWithStrongOctave(f0, SampleRate, Window, octaveRatio: 0.9));

        Assert.True(reading.HasPitch);
        // Within a semitone of the true fundamental — i.e. neither 2·f0 nor f0/2.
        Assert.InRange(Math.Abs(TunerTestSignals.CentsBetween(reading.FrequencyHz, f0)), 0, 100);
    }

    [Theory]
    [InlineData(82.41, 5)]
    [InlineData(82.41, -5)]
    [InlineData(246.94, 10)]
    [InlineData(246.94, -10)]
    [InlineData(329.63, 50)]
    [InlineData(329.63, -50)]
    [InlineData(61.74, -50)]
    public void Detect_DetunedSine_RecoversTheDetuneWithinOneCent(double nominal, double cents)
    {
        var detuned = TunerTestSignals.Detune(nominal, cents);
        var reading = Detector().Detect(TunerTestSignals.Sine(detuned, SampleRate, Window));

        Assert.True(reading.HasPitch);
        var measuredCents = TunerTestSignals.CentsBetween(reading.FrequencyHz, nominal);
        Assert.InRange(Math.Abs(measuredCents - cents), 0, 1.0);
    }

    [Fact]
    public void Detect_NoiseOnly_ReportsNoPitch()
    {
        var detector = Detector();
        for (var seed = 1; seed <= 5; seed++)
        {
            var reading = detector.Detect(TunerTestSignals.Noise(Window, seed));
            Assert.False(reading.HasPitch);
        }
    }

    [Fact]
    public void Detect_Silence_ReportsNoPitch() => Assert.False(Detector().Detect(new float[Window]).HasPitch);

    [Fact]
    public void Detect_BelowAmplitudeGate_ReportsNoPitch()
    {
        var quiet = TunerTestSignals.Sine(110.0, SampleRate, Window, amplitude: 0.002);
        Assert.False(Detector().Detect(quiet).HasPitch);
    }

    [Fact]
    public void Detect_AboveAmplitudeGate_ReportsPitch()
    {
        var audible = TunerTestSignals.Sine(110.0, SampleRate, Window, amplitude: 0.05);
        Assert.True(Detector().Detect(audible).HasPitch);
    }

    [Fact]
    public void Detect_DcOffsetDoesNotDefeatTheGateOrTheEstimate()
    {
        // Pure DC must gate as silence (AC RMS is what matters) …
        var dcOnly = new float[Window];
        Array.Fill(dcOnly, 0.5f);
        Assert.False(Detector().Detect(dcOnly).HasPitch);

        // … and a biased sine must still read within a cent.
        var biased = TunerTestSignals.Sine(110.0, SampleRate, Window);
        for (var i = 0; i < biased.Length; i++) biased[i] += 0.3f;
        var reading = Detector().Detect(biased);
        Assert.True(reading.HasPitch);
        Assert.InRange(Math.Abs(TunerTestSignals.CentsBetween(reading.FrequencyHz, 110.0)), 0, 1.0);
    }

    [Fact]
    public void Detect_WrongWindowLength_Throws() => Assert.Throws<ArgumentException>(() => Detector().Detect(new float[Window - 1]));

    [Fact]
    public void Ctor_InvalidRange_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new MpmPitchDetector(SampleRate, Window, 100, 50));
        Assert.Throws<ArgumentOutOfRangeException>(() => new MpmPitchDetector(0));
    }

    [Fact]
    public void Detect_IsAllocationFree_AfterWarmup()
    {
        var detector = Detector();
        var signal = TunerTestSignals.Sine(110.0, SampleRate, Window);
        var noise = TunerTestSignals.Noise(Window);

        AllocationProbe.AssertAllocationFree("MpmPitchDetector.Detect", () =>
        {
            for (var i = 0; i < 10; i++)
            {
                detector.Detect(signal);
                detector.Detect(noise);
            }
        });
    }
}
