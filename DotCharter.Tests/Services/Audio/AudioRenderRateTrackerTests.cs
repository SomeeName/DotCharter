using DotCharter.Tone.Audio.Diagnostics;

namespace DotCharter.Tests.Services.Audio;

/// <summary>
///     The health monitor's sustained-under-run signal. Pure, so the arithmetic
///     is tested without a device — the same split
///     <see cref="AudioDropoutDetector" /> already uses for the per-poll half.
///     <para>
///         The bug it was extracted for: a heartbeat whose render-clock read
///         FAILED stored the window's frame count as <c>0</c>, so the next
///         heartbeat measured the engine's entire lifetime of frames across one
///         30 s window and printed a WARNING that the audio thread was not
///         keeping up. Nothing had happened.
///     </para>
/// </summary>
public sealed class AudioRenderRateTrackerTests
{
    private const int Sr = 48_000;
    private const double WindowMs = 30_000;

    private static long FramesFor(double seconds, double rate = Sr) => (long)(seconds * rate);

    [Fact]
    public void AHealthyDeviceRendersAtItsOwnRate_AndIsNotAnUnderRun()
    {
        var tracker = new AudioRenderRateTracker();

        // First window has no predecessor to span.
        Assert.False(tracker.Observe(FramesFor(600), WindowMs).HaveRate);

        var reading = tracker.Observe(FramesFor(630), WindowMs);

        Assert.True(reading.HaveRate);
        Assert.Equal(Sr, reading.EffectiveSampleRate, 0);
        Assert.False(AudioRenderRateTracker.IsSustainedUnderRun(reading, Sr));
    }

    [Fact]
    public void ADeviceFallingBehindRealTimeIsReported()
    {
        var tracker = new AudioRenderRateTracker();
        tracker.Observe(FramesFor(600), WindowMs);

        // 30 s of wall clock, only 27 s of audio produced.
        var reading = tracker.Observe(FramesFor(627), WindowMs);

        Assert.True(reading.HaveRate);
        Assert.True(AudioRenderRateTracker.IsSustainedUnderRun(reading, Sr));
    }

    /// <summary>THE REGRESSION. An unread clock must leave the tracker with no
    /// reference at all — storing it as frame 0 makes the next window measure the
    /// whole engine lifetime and fabricate an under-run.</summary>
    [Fact]
    public void AWindowWhoseClockCouldNotBeRead_DoesNotMakeTheNextOneLie()
    {
        var tracker = new AudioRenderRateTracker();
        tracker.Observe(FramesFor(600), WindowMs);

        // The engine went away between the poll and the heartbeat.
        Assert.False(tracker.Observe(null, WindowMs).HaveRate);

        // Ten minutes of frames on the clock, one 30 s window: the old code
        // reported ~960 kHz here and warned about a "sustained under-run".
        var next = tracker.Observe(FramesFor(630), WindowMs);

        Assert.False(next.HaveRate);
        Assert.False(AudioRenderRateTracker.IsSustainedUnderRun(next, Sr));

        // …and the window AFTER that measures cleanly again.
        var recovered = tracker.Observe(FramesFor(660), WindowMs);
        Assert.True(recovered.HaveRate);
        Assert.Equal(Sr, recovered.EffectiveSampleRate, 0);
    }

    /// <summary>A fresh engine's render clock starts near zero. That is a
    /// REBUILD, not an under-run, and it must yield no rate rather than a
    /// negative-delta one.</summary>
    [Fact]
    public void AnEngineRebuildIsNotAnUnderRun()
    {
        var tracker = new AudioRenderRateTracker();
        tracker.Observe(FramesFor(600), WindowMs);

        var acrossTheRebuild = tracker.Observe(FramesFor(2), WindowMs);
        Assert.False(acrossTheRebuild.HaveRate);

        // The new engine is then measured on its own terms.
        var next = tracker.Observe(FramesFor(32), WindowMs);
        Assert.True(next.HaveRate);
        Assert.Equal(Sr, next.EffectiveSampleRate, 0);
    }

    /// <summary>`Forget` is what the monitor calls when the device is gone or the
    /// log category is off; a window spanning that gap always measures LOW, i.e.
    /// always reads as an under-run.</summary>
    [Fact]
    public void ForgettingDropsTheReference_SoNoWindowSpansTheGap()
    {
        var tracker = new AudioRenderRateTracker();
        tracker.Observe(FramesFor(600), WindowMs);

        tracker.Forget();

        Assert.False(tracker.Observe(FramesFor(630), WindowMs).HaveRate);
        Assert.True(tracker.Observe(FramesFor(660), WindowMs).HaveRate);
    }

    /// <summary>Refusing to judge is the honest answer, and returning true with
    /// nothing to compare against is exactly how the fabricated warning escaped.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void WithNoDeviceRateToJudgeAgainst_NothingIsReported(int deviceSampleRate)
    {
        var tracker = new AudioRenderRateTracker();
        tracker.Observe(FramesFor(600), WindowMs);
        var reading = tracker.Observe(FramesFor(600), WindowMs); // zero frames produced

        Assert.True(reading.HaveRate);
        Assert.False(AudioRenderRateTracker.IsSustainedUnderRun(reading, deviceSampleRate));
    }

    [Fact]
    public void AZeroLengthWindowMeasuresNothing_RatherThanDividingByIt()
    {
        var tracker = new AudioRenderRateTracker();
        tracker.Observe(FramesFor(600), WindowMs);

        Assert.False(tracker.Observe(FramesFor(630), 0).HaveRate);
    }
}
