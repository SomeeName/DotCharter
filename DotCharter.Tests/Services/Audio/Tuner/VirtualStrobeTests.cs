using DotCharter.Tone.Audio.Tuner;

namespace DotCharter.Tests.Services.Audio.Tuner;

public sealed class VirtualStrobeTests
{
    private const int SampleRate = 48000;
    private const int Block = 2048;

    /// <summary>Feeds a contiguous sine at <paramref name="frequency"/>
    /// through <paramref name="blocks"/> blocks and returns the last reading.</summary>
    private static StrobeReading FeedSine(VirtualStrobe strobe, double frequency, int blocks,
        double amplitude = 0.25)
    {
        var signal = TunerTestSignals.Sine(frequency, SampleRate, Block * blocks, amplitude);
        var reading = default(StrobeReading);
        for (var b = 0; b < blocks; b++)
            reading = strobe.Process(signal.AsSpan(b * Block, Block));
        return reading;
    }

    [Theory]
    [InlineData(61.74)]
    [InlineData(110.0)]
    [InlineData(329.63)]
    public void OnTargetSine_ReadsNearZeroCents(double target)
    {
        var strobe = new VirtualStrobe(SampleRate, target, Block);
        var reading = FeedSine(strobe, target, blocks: 4);

        Assert.True(reading.HasSignal);
        Assert.True(reading.IsTracking);
        Assert.InRange(Math.Abs(reading.CentsOffset), 0, 0.1);
        Assert.InRange(Math.Abs(strobe.WheelTurns), 0, 0.01);
    }

    [Theory]
    [InlineData(110.0, 5)]
    [InlineData(110.0, -5)]
    [InlineData(110.0, 10)]
    [InlineData(246.94, -10)]
    [InlineData(329.63, 50)]
    [InlineData(329.63, -50)]
    [InlineData(61.74, -50)]
    public void DetunedSine_ReadsTheDetune(double target, double cents)
    {
        var strobe = new VirtualStrobe(SampleRate, target, Block);
        var detuned = TunerTestSignals.Detune(target, cents);
        var reading = FeedSine(strobe, detuned, blocks: 4);

        Assert.True(reading.IsTracking);
        Assert.InRange(Math.Abs(reading.CentsOffset - cents), 0, 0.5);
    }

    [Fact]
    public void SubCentResolution_NearTarget()
    {
        // The whole point of a strobe: offsets a needle can't show.
        var strobe = new VirtualStrobe(SampleRate, 110.0, Block);
        var reading = FeedSine(strobe, TunerTestSignals.Detune(110.0, 0.5), blocks: 6);

        Assert.True(reading.IsTracking);
        Assert.InRange(reading.CentsOffset, 0.3, 0.7);
    }

    [Fact]
    public void WheelTurns_AccumulateWithTheBeatRate_AndCarrySign()
    {
        const double target = 110.0;
        const double cents = 5.0;
        var detuned = TunerTestSignals.Detune(target, cents);

        // ~1 s sharp: expected turns = Δf · t (t counted between the first
        // and last block midpoints, i.e. (blocks−1)·block/rate).
        const int blocks = 24;
        var sharp = new VirtualStrobe(SampleRate, target, Block);
        FeedSine(sharp, detuned, blocks);
        var expected = (detuned - target) * (blocks - 1) * Block / SampleRate;
        Assert.InRange(sharp.WheelTurns, expected * 0.9, expected * 1.1);
        Assert.True(sharp.WheelTurns > 0);

        var flat = new VirtualStrobe(SampleRate, target, Block);
        FeedSine(flat, TunerTestSignals.Detune(target, -cents), blocks);
        Assert.True(flat.WheelTurns < 0);
    }

    [Fact]
    public void FirstSignalBlock_HasSignalButNoDriftYet()
    {
        var strobe = new VirtualStrobe(SampleRate, 110.0, Block);
        var reading = strobe.Process(TunerTestSignals.Sine(110.0, SampleRate, Block));

        Assert.True(reading.HasSignal);
        Assert.False(reading.IsTracking);
    }

    [Fact]
    public void Silence_ReportsNoSignal_AndBreaksTracking()
    {
        var strobe = new VirtualStrobe(SampleRate, 110.0, Block);
        FeedSine(strobe, 110.0, blocks: 2);

        var silent = strobe.Process(new float[Block]);
        Assert.False(silent.HasSignal);

        // Resuming after a gap must re-acquire, not extrapolate drift over it.
        var resumed = strobe.Process(TunerTestSignals.Sine(110.0, SampleRate, Block));
        Assert.True(resumed.HasSignal);
        Assert.False(resumed.IsTracking);
    }

    [Fact]
    public void OffTargetTone_IsRejectedBySelectivity()
    {
        // A loud tone one octave off is NOT the target string.
        var strobe = new VirtualStrobe(SampleRate, 110.0, Block);
        var reading = FeedSine(strobe, 220.0, blocks: 3);

        Assert.False(reading.HasSignal);
    }

    [Fact]
    public void NoiseOnly_IsRejected()
    {
        var strobe = new VirtualStrobe(SampleRate, 110.0, Block);
        var reading = strobe.Process(TunerTestSignals.Noise(Block));

        Assert.False(reading.HasSignal);
    }

    [Fact]
    public void HarmonicRichTone_StillTracksTheFundamentalBin()
    {
        var strobe = new VirtualStrobe(SampleRate, 110.0, Block);
        var detuned = TunerTestSignals.Detune(110.0, 8.0);
        var signal = TunerTestSignals.Harmonics(detuned, SampleRate, Block * 4);

        var reading = default(StrobeReading);
        for (var b = 0; b < 4; b++)
            reading = strobe.Process(signal.AsSpan(b * Block, Block));

        Assert.True(reading.IsTracking);
        Assert.InRange(Math.Abs(reading.CentsOffset - 8.0), 0, 0.5);
    }

    [Fact]
    public void Retarget_ResetsTrackingAndWheel()
    {
        var strobe = new VirtualStrobe(SampleRate, 110.0, Block);
        FeedSine(strobe, TunerTestSignals.Detune(110.0, 10), blocks: 4);
        Assert.NotEqual(0.0, strobe.WheelTurns);

        strobe.Retarget(146.83);
        Assert.Equal(146.83, strobe.TargetFrequencyHz);
        Assert.Equal(0.0, strobe.WheelTurns);

        var first = strobe.Process(TunerTestSignals.Sine(146.83, SampleRate, Block));
        Assert.True(first.HasSignal);
        Assert.False(first.IsTracking);
    }

    [Fact]
    public void WrongBlockLength_Throws()
    {
        var strobe = new VirtualStrobe(SampleRate, 110.0, Block);
        Assert.Throws<ArgumentException>(() => strobe.Process(new float[Block - 1]));
    }

    [Fact]
    public void Ctor_InvalidTarget_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new VirtualStrobe(SampleRate, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new VirtualStrobe(SampleRate, SampleRate));
    }

    [Fact]
    public void Process_IsAllocationFree_AfterWarmup()
    {
        var strobe = new VirtualStrobe(SampleRate, 110.0, Block);
        var signal = TunerTestSignals.Sine(110.0, SampleRate, Block);
        var silence = new float[Block];

        AllocationProbe.AssertAllocationFree("VirtualStrobe.Process", () =>
        {
            for (var i = 0; i < 20; i++)
            {
                strobe.Process(signal);
                strobe.Process(silence);
            }
        });
    }
}
