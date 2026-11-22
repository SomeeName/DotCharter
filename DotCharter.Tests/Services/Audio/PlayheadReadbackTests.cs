using DotCharter.Tone.Audio.Engine;
using JuceSharp;

namespace DotCharter.Tests.Services.Audio;

/// <summary>The audible-position readback without a native handle or a wall clock.</summary>
public sealed class PlayheadReadbackTests
{
    [Fact]
    public void NoSourceRetainsTheFloorWithoutCompensation()
    {
        var clock = new PlayheadInterpolator(1_000_000);
        var floor = new PlayheadFloor();
        floor.Reset(321, clock.Epoch);

        var position = PlayheadReadback.Read(null, clock, floor, 2, 10_000,
            _ => throw new InvalidOperationException("No source must not read latency."), 2000);

        Assert.Equal(321, position);
    }

    [Fact]
    public void NoEngineReportsNoDeviceLatency() =>
        Assert.Equal(0, PlayheadReadback.EstimateOutputLatency(null, new OutputLatencyCache(), 0));

    [Theory]
    [InlineData(1000, 3000, 920)]
    [InlineData(1000, 800, 800)]
    [InlineData(50, 3000, 0)]
    public void NativeFallbackCompensatesAndClamps(double raw, double length, double expected)
    {
        var source = new Source(raw);
        var floor = new PlayheadFloor();

        var position = PlayheadReadback.Read(source, new PlayheadInterpolator(1_000_000), floor, 2, length,
            static ms => PlayheadReadback.CompensateLatency(ms, 40, 2), 2000);

        Assert.Equal(expected, position);
        Assert.Equal(expected, floor.Ms);
        Assert.Equal(1, source.Reads);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FailedNativeReadRetainsTheFloor(bool disposed)
    {
        var source = new Source(1000)
        {
            Failure = disposed ? new ObjectDisposedException("source") : new InvalidOperationException("device lost")
        };
        var clock = new PlayheadInterpolator(1_000_000);
        var floor = new PlayheadFloor();
        floor.Reset(321, clock.Epoch);

        Assert.Equal(321, PlayheadReadback.Read(source, clock, floor, 2, 3000, static ms => ms, 2000));
    }

    [Fact]
    public void InterpolatedReadUsesSpeedAndSkipsTheNativeRead()
    {
        var source = new Source(9999) { Failure = new InvalidOperationException("The interpolated path must not read native.") };
        var clock = new PlayheadInterpolator(1_000_000);
        var floor = new PlayheadFloor();
        Assert.True(clock.TryTick(1000, 0, 2, clock.Epoch, out _));

        var position = PlayheadReadback.Read(source, clock, floor, 2, 3000,
            static ms => PlayheadReadback.CompensateLatency(ms, 40, 2), 2000);

        Assert.Equal(924, position, 6);
        Assert.Equal(0, source.Reads);
    }

    private sealed class Source(double raw) : IJuceAudioSource
    {
        public int Reads { get; private set; }
        public Exception? Failure { get; init; }
        public TimeSpan AudiblePosition
        {
            get
            {
                Reads++;
                if (Failure != null) throw Failure;
                return TimeSpan.FromMilliseconds(raw);
            }
        }
        public TimeSpan Position => TimeSpan.FromMilliseconds(raw);
        public TimeSpan Length => TimeSpan.FromSeconds(20);
        public float Volume { get; set; }
        public bool Mute { get; set; }
        public JuceLoopRegion? Loop { get; set; }
        public float TempoRatio { get; set; }
        public float PitchSemitones { get; set; }
        public float Pan { get; set; }
        public bool StemGrouped { get; set; }
        public bool IsDisposed { get; private set; }
        public void Play() => throw new NotSupportedException();
        public void Pause() => throw new NotSupportedException();
        public void Stop() => throw new NotSupportedException();
        public void Seek(TimeSpan position) => throw new NotSupportedException();
        public JucePreloadDigest GetPreloadDigest() => throw new NotSupportedException();
        public void Dispose() => IsDisposed = true;
    }
}
