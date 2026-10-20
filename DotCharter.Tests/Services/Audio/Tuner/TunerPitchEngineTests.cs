using DotCharter.Tone.Audio.Tuner;

namespace DotCharter.Tests.Services.Audio.Tuner;

public sealed class TunerPitchEngineTests
{
    private const int SampleRate = 48000;
    private const int Window = 4096;

    [Theory]
    [InlineData(61.74)]
    [InlineData(110.00)]
    [InlineData(329.63)]
    public void Detect_CleanSignal_UsesMpmAndStaysWithinOneCent(double frequency)
    {
        var engine = new TunerPitchEngine(SampleRate, Window);
        var reading = engine.Detect(TunerTestSignals.Sine(frequency, SampleRate, Window));

        Assert.True(reading.HasPitch);
        Assert.InRange(Math.Abs(TunerTestSignals.CentsBetween(reading.FrequencyHz, frequency)), 0, 1.0);
    }

    [Fact]
    public void Detect_MpmGatedOut_FallsBackToYin()
    {
        // An impossible MPM clarity gate forces every window down the fallback.
        var engine = new TunerPitchEngine(
            new MpmPitchDetector(SampleRate, Window) { ClarityThreshold = 2.0 },
            new YinPitchDetector(SampleRate, Window));

        var reading = engine.Detect(TunerTestSignals.Sine(110.0, SampleRate, Window));

        Assert.True(reading.HasPitch);
        Assert.InRange(Math.Abs(TunerTestSignals.CentsBetween(reading.FrequencyHz, 110.0)), 0, 1.0);
    }

    [Fact]
    public void Detect_NoiseOnly_BothEnginesGate()
    {
        var engine = new TunerPitchEngine(SampleRate, Window);
        Assert.False(engine.Detect(TunerTestSignals.Noise(Window)).HasPitch);
    }

    [Fact]
    public void Ctor_MismatchedDetectors_Throws()
    {
        Assert.Throws<ArgumentException>(() => new TunerPitchEngine(
            new MpmPitchDetector(44100, Window),
            new YinPitchDetector(48000, Window)));
    }

    [Fact]
    public void Detect_IsAllocationFree_AfterWarmup()
    {
        var engine = new TunerPitchEngine(SampleRate, Window);
        var signal = TunerTestSignals.Sine(110.0, SampleRate, Window);
        var noise = TunerTestSignals.Noise(Window);

        AllocationProbe.AssertAllocationFree("TunerPitchEngine.Detect", () =>
        {
            for (var i = 0; i < 10; i++)
            {
                engine.Detect(signal);
                engine.Detect(noise);
            }
        });
    }
}
