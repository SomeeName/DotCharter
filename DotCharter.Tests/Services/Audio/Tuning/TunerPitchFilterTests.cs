using DotCharter.Services.Audio.Tuning;

namespace DotCharter.Tests.Services.Audio.Tuning;

public sealed class TunerPitchFilterTests
{
    private static TunerPitchFilter NewFilter() => new()
    {
        MinRms = 0.005f,
        AttackJumpRatio = 2.5f,
        AttackHoldMs = 50.0,
        MedianWindow = 5,
        MinReadings = 2
    };

    [Fact]
    public void Silence_ReturnsNull()
    {
        var filter = NewFilter();
        Assert.Null(filter.Process(110.0, rms: 0.001f, deltaMs: 66));
        Assert.Null(filter.Process(110.0, rms: 0.0f, deltaMs: 66));
    }

    [Fact]
    public void SteadyTone_ReportsAfterMinReadings()
    {
        var filter = NewFilter();
        // Steady level (no attack jump): first reading withheld, second reported.
        Assert.Null(filter.Process(110.0, rms: 0.05f, deltaMs: 66));
        Assert.Equal(110.0, filter.Process(110.0, rms: 0.05f, deltaMs: 66));
    }

    [Fact]
    public void Attack_HoldsOffReadingsForTheConfiguredTime()
    {
        var filter = NewFilter();
        filter.Process(null, rms: 0.001f, deltaMs: 66);          // silence baseline
        Assert.Null(filter.Process(111.5, rms: 0.2f, deltaMs: 66)); // pluck: transient, held
        // Hold-off expired after 66 ms > 50 ms; string has settled.
        Assert.Null(filter.Process(110.0, rms: 0.15f, deltaMs: 66));  // first reading
        Assert.Equal(110.0, filter.Process(110.0, rms: 0.12f, deltaMs: 66));
    }

    [Fact]
    public void Attack_ClearsHistoryFromThePreviousPluck()
    {
        var filter = NewFilter();
        filter.Process(220.0, rms: 0.05f, deltaMs: 66);
        filter.Process(220.0, rms: 0.04f, deltaMs: 66);
        Assert.Equal(220.0, filter.Process(220.0, rms: 0.03f, deltaMs: 66));

        // Repluck (level jump over the decayed tail) after retuning the peg:
        // old 220 Hz readings must not median into the new 110 Hz pitch.
        Assert.Null(filter.Process(112.0, rms: 0.2f, deltaMs: 66)); // transient
        Assert.Null(filter.Process(110.0, rms: 0.15f, deltaMs: 66));
        Assert.Equal(110.0, filter.Process(110.0, rms: 0.12f, deltaMs: 66));
    }

    [Fact]
    public void MedianRejectsASingleOctaveGlitch()
    {
        var filter = NewFilter();
        filter.Process(110.0, rms: 0.05f, deltaMs: 66);
        filter.Process(110.0, rms: 0.05f, deltaMs: 66);
        filter.Process(110.2, rms: 0.05f, deltaMs: 66);
        // YIN octave error on one frame: the median must not follow it.
        var smoothed = filter.Process(220.0, rms: 0.05f, deltaMs: 66);
        Assert.NotNull(smoothed);
        Assert.InRange(smoothed!.Value, 109.0, 111.0);
    }

    [Fact]
    public void AperiodicFrames_KeepHistoryButReportNothing()
    {
        var filter = NewFilter();
        filter.Process(110.0, rms: 0.05f, deltaMs: 66);
        filter.Process(110.0, rms: 0.05f, deltaMs: 66);
        Assert.Null(filter.Process(null, rms: 0.04f, deltaMs: 66)); // detector dropout
        Assert.Equal(110.0, filter.Process(110.0, rms: 0.04f, deltaMs: 66));
    }

    [Fact]
    public void SilenceClearsHistory()
    {
        var filter = NewFilter();
        filter.Process(220.0, rms: 0.05f, deltaMs: 66);
        filter.Process(220.0, rms: 0.05f, deltaMs: 66);
        filter.Process(null, rms: 0.0f, deltaMs: 500); // string fully decayed

        // Next pluck at a steady level (no jump vs prior silence tick is a
        // jump — but from rms 0 anything is): held, then fresh readings only.
        Assert.Null(filter.Process(110.0, rms: 0.05f, deltaMs: 66));
        Assert.Null(filter.Process(110.0, rms: 0.05f, deltaMs: 66));
        Assert.Equal(110.0, filter.Process(110.0, rms: 0.05f, deltaMs: 66));
    }
}
