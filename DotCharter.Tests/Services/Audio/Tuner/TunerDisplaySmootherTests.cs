using DotCharter.Tone.Audio.Tuner;

namespace DotCharter.Tests.Services.Audio.Tuner;

public sealed class TunerDisplaySmootherTests
{
    private const double TickMs = 100;

    /// <summary>Feeds <paramref name="values"/> at a fixed tick, starting at
    /// <paramref name="startMs"/>; returns the last reading.</summary>
    private static SmoothedTunerReading Feed(TunerDisplaySmoother smoother,
        IReadOnlyList<double> values, double startMs = 0)
    {
        var reading = default(SmoothedTunerReading);
        for (var i = 0; i < values.Count; i++)
            reading = smoother.Process(values[i], startMs + i * TickMs);
        return reading;
    }

    [Fact]
    public void SteadyReadings_ConvergeOnTheValue()
    {
        var smoother = new TunerDisplaySmoother();
        var reading = Feed(smoother, [10, 10, 10, 10, 10, 10, 10, 10]);

        Assert.True(reading.HasValue);
        Assert.InRange(reading.Cents, 9.0, 10.0);
        Assert.False(reading.WithinBand);
    }

    [Fact]
    public void MedianRejectsASingleOctaveGlitch()
    {
        var smoother = new TunerDisplaySmoother();
        // A lone +1200¢ glitch inside steady 5¢ readings must not move the needle.
        var reading = Feed(smoother, [5, 5, 1200, 5, 5, 5, 5]);

        Assert.InRange(reading.Cents, 4.0, 6.0);
    }

    [Fact]
    public void ExponentialSmoothing_ApproachesAStepGradually()
    {
        var smoother = new TunerDisplaySmoother(medianWindow: 1) { SmoothingAlpha = 0.35 };

        var first = smoother.Process(0, 0);
        Assert.Equal(0, first.Cents, 3);

        // Step to 20¢: the very next frame must move toward it but not jump.
        var second = smoother.Process(20, TickMs);
        Assert.InRange(second.Cents, 5.0, 9.0); // 0.35 · 20 = 7

        var third = smoother.Process(20, 2 * TickMs);
        Assert.True(third.Cents > second.Cents);
        Assert.True(third.Cents < 20.0);
    }

    [Fact]
    public void InTune_RequiresTheStabilityHold()
    {
        var smoother = new TunerDisplaySmoother(medianWindow: 1) { StabilityHoldMs = 750 };

        // Within band immediately, but not yet "in tune".
        var early = smoother.Process(2, 0);
        Assert.True(early.WithinBand);
        Assert.False(early.InTune);

        var at700 = Feed(smoother, [2, 2, 2, 2, 2, 2, 2], startMs: TickMs); // through 700 ms
        Assert.False(at700.InTune);

        var at800 = smoother.Process(2, 800);
        Assert.True(at800.InTune);
    }

    [Fact]
    public void LeavingTheBand_RestartsTheHold()
    {
        var smoother = new TunerDisplaySmoother(medianWindow: 1)
        {
            StabilityHoldMs = 500,
            SmoothingAlpha = 1.0 // isolate the hold logic
        };

        Feed(smoother, [2, 2, 2, 2, 2]); // 0..400 ms within band
        var excursion = smoother.Process(15, 500);
        Assert.False(excursion.WithinBand);

        // Back in band: the hold starts over from 600 ms.
        var back = smoother.Process(2, 600);
        Assert.True(back.WithinBand);
        Assert.False(back.InTune);

        Assert.False(smoother.Process(2, 1000).InTune);
        Assert.True(smoother.Process(2, 1100).InTune);
    }

    [Fact]
    public void Silence_ClearsHistoryAndHold()
    {
        var smoother = new TunerDisplaySmoother(medianWindow: 1)
        {
            StabilityHoldMs = 500,
            SmoothingAlpha = 1.0
        };

        Feed(smoother, [2, 2, 2, 2, 2]); // 0..400 ms within band
        var silent = smoother.ProcessSilence(450);
        Assert.False(silent.HasValue);

        // Fresh pluck: no residue from before the silence — hold restarts,
        // and the smoothed value is exactly the new reading (no stale EMA).
        var fresh = smoother.Process(30, 500);
        Assert.Equal(30, fresh.Cents, 3);
        Assert.False(fresh.InTune);

        // Back in band at 900 ms — the hold runs 900→1400 ms.
        Assert.False(smoother.Process(2, 900).InTune);
        Assert.False(smoother.Process(2, 1300).InTune);
        Assert.True(smoother.Process(2, 1400).InTune);
    }

    /// <summary>The ENTRY threshold is inclusive at ±<c>InTuneCents</c>. Leaving
    /// takes <c>ReleaseCents</c> instead — the band is a Schmitt trigger, see
    /// <see cref="TunerDisplayHysteresisTests"/> for why.</summary>
    [Fact]
    public void BandBoundary_CountsAsWithin()
    {
        var smoother = new TunerDisplaySmoother(medianWindow: 1) { SmoothingAlpha = 1.0 };
        Assert.True(smoother.Process(5.0, 0).WithinBand);
        Assert.True(smoother.Process(-5.0, 100).WithinBand);
        Assert.False(smoother.Process(9.0, 200).WithinBand);

        // …and entering still takes the tight threshold: 5.1 does not get in.
        var fresh = new TunerDisplaySmoother(medianWindow: 1) { SmoothingAlpha = 1.0 };
        Assert.False(fresh.Process(5.1, 0).WithinBand);
    }

    [Fact]
    public void Reset_ClearsEverything()
    {
        var smoother = new TunerDisplaySmoother();
        Feed(smoother, [3, 3, 3, 3, 3, 3, 3, 3, 3, 3]);

        smoother.Reset();
        var fresh = smoother.Process(40, 5000);
        Assert.Equal(40, fresh.Cents, 3);
        Assert.False(fresh.InTune);
    }

    [Fact]
    public void Ctor_InvalidWindow_Throws() => Assert.Throws<ArgumentOutOfRangeException>(() => new TunerDisplaySmoother(0));

    [Fact]
    public void StrobeIntoSmoother_EndToEnd_InTuneVerdict()
    {
        // Synthesized end-to-end slice: strobe readings of an on-target
        // string feed the smoother until the stability hold confirms.
        const int sampleRate = 48000;
        const int block = 2048;
        const double target = 110.0;

        var strobe = new VirtualStrobe(sampleRate, target, block);
        var smoother = new TunerDisplaySmoother(medianWindow: 3) { StabilityHoldMs = 400 };

        const int blocks = 24; // ~1 s
        var signal = TunerTestSignals.Sine(target, sampleRate, block * blocks);
        var inTune = false;
        for (var b = 0; b < blocks; b++)
        {
            var nowMs = b * block * 1000.0 / sampleRate;
            var strobeReading = strobe.Process(signal.AsSpan(b * block, block));
            var smoothed = strobeReading.IsTracking
                ? smoother.Process(strobeReading.CentsOffset, nowMs)
                : smoother.ProcessSilence(nowMs);
            inTune = smoothed.InTune;
        }

        Assert.True(inTune);
    }

    [Fact]
    public void Process_IsAllocationFree_AfterWarmup()
    {
        var smoother = new TunerDisplaySmoother();
        smoother.Process(1, 0);
        smoother.ProcessSilence(100);
        smoother.Process(2, 200);

        // The clock keeps advancing across batches — the smoother's state is
        // time-driven, so replaying the same timestamps would measure a
        // different path than the one that runs live.
        var nowMs = 300.0;
        AllocationProbe.AssertAllocationFree("TunerDisplaySmoother.Process", () =>
        {
            for (var i = 0; i < 50; i++)
            {
                smoother.Process(i % 7, nowMs);
                if (i % 9 == 0) smoother.ProcessSilence(nowMs + 25);
                nowMs += 50;
            }
        });
    }
}
