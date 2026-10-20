using DotCharter.Tone.Audio.Engine;

namespace DotCharter.Tests.Services.Audio;

/// <summary>
///     #309: the playhead getter's monotonic floor raced a seek. A render read that
///     extrapolated from the PRE-seek sample wrote its value into the floor after
///     <c>SeekTo</c> had set the target, and every read after it was floored to the
///     old position until the next 250 Hz tick. Driven here with a fake clock and
///     the race interleaved by hand: no timer, no engine, no thread.
/// </summary>
public sealed class PlayheadFloorTests
{
    private const long TicksPerSecond = 10_000_000;

    private static long Ticks(double wallMs) => (long)Math.Round(wallMs * TicksPerSecond / 1000.0);

    [Fact]
    public void ARenderReadThatStraddlesASeek_ReportsTheTarget_AndDoesNotHoldTheOldPosition()
    {
        var interp = new PlayheadInterpolator(TicksPerSecond);
        var floor = new PlayheadFloor();

        // Playing at 60 s: a tick publishes, as the position timer does.
        var tickEpoch = interp.Epoch;
        Assert.True(interp.TryTick(60_000, Ticks(0), 1.0, tickEpoch, out var report));
        floor.Publish(report, tickEpoch);

        // The render thread takes the sample 2 ms later...
        Assert.True(interp.TryExtrapolate(Ticks(2), 1.0, out var stale, out var staleEpoch));
        Assert.Equal(60_002.0, stale, 3);

        // ...the UI thread seeks back to 5 s, exactly as SeekTo orders it...
        interp.Reset();
        floor.Reset(5_000, interp.Epoch);

        // ...and only then does the render read reach the floor.
        Assert.Equal(5_000.0, floor.Clamp(stale, staleEpoch));
        Assert.Equal(5_000.0, floor.Ms);

        // The next read finds no sample and reads the native position (5 s plus a
        // millisecond): it must not be floored to the pre-seek 60 s.
        Assert.False(interp.TryExtrapolate(Ticks(3), 1.0, out _, out var rawEpoch));
        Assert.Equal(5_001.0, floor.Publish(5_001, rawEpoch));

        // The first tick of the new burst, and a render read after it, stay at 5 s.
        var nextTick = interp.Epoch;
        Assert.True(interp.TryTick(5_004, Ticks(4), 1.0, nextTick, out var fresh));
        floor.Publish(fresh, nextTick);
        Assert.True(interp.TryExtrapolate(Ticks(5), 1.0, out var read, out var readEpoch));
        Assert.Equal(5_005.0, floor.Clamp(read, readEpoch), 3);
    }

    [Fact]
    public void ATickThatReadTheNativePositionBeforeASeek_IsRefused()
    {
        var interp = new PlayheadInterpolator(TicksPerSecond);
        var epoch = interp.Epoch;   // the tick reads the epoch, then the native position (60 s)...

        interp.Reset();             // ...a seek lands...

        // ...and the pre-seek reading must not become the new burst's start.
        Assert.False(interp.TryTick(60_000, Ticks(0), 1.0, epoch, out _));
        Assert.False(interp.IsRunning);
        Assert.False(interp.TryExtrapolate(Ticks(1), 1.0, out _));
    }

    [Fact]
    public void ATickFromAnOlderEpoch_CannotOverwriteASeek()
    {
        var floor = new PlayheadFloor();
        floor.Publish(60_000, 1);
        floor.Reset(5_000, 2);

        Assert.Equal(5_000.0, floor.Publish(60_004, 1));
        Assert.Equal(5_000.0, floor.Ms);
    }

    [Fact]
    public void WithinOneEpoch_TheFloorStillHoldsAgainstALatencyShift()
    {
        // The case the floor exists for: latency compensation grows mid-playback
        // (a mirror device opens) and the compensated reading steps back.
        var floor = new PlayheadFloor();
        floor.Publish(10_000, 3);

        Assert.Equal(10_000.0, floor.Clamp(9_990, 3));
        Assert.Equal(10_020.0, floor.Clamp(10_020, 3));
        // The raw path is not floored: it is what the floor is corrected toward.
        Assert.Equal(10_010.0, floor.Publish(10_010, 3));
    }

    [Fact]
    public void EveryReset_MovesTheEpoch()
    {
        var interp = new PlayheadInterpolator(TicksPerSecond);
        var before = interp.Epoch;
        interp.Tick(1_000, Ticks(0), 1.0);
        Assert.Equal(before, interp.Epoch);

        interp.Reset();
        Assert.Equal(before + 1, interp.Epoch);
        Assert.True(interp.TryTick(2_000, Ticks(1), 1.0, interp.Epoch, out var report));
        Assert.Equal(2_000.0, report);
    }

    /// <summary>
    ///     The service wires the two in the order the race needs: SeekTo resets the
    ///     interpolator BEFORE the floor (so the floor's epoch is the new one), the
    ///     tick reads the epoch BEFORE the native position, and nothing writes the
    ///     position past the floor any more.
    /// </summary>
    [Fact]
    public void TheService_ResetsTheInterpolatorBeforeTheFloor_AndTicksThroughTheEpoch()
    {
        static string Read(string file) => SourceText.StripCommentLines(File.ReadAllText(
            Path.Combine(TestPaths.RepoRoot(), "DotCharter.Tone", "Audio", file)));

        var transport = Read("JuceAudioPlaybackService.Transport.cs");
        var seek = transport[transport.IndexOf("public void SeekTo(double timeMs)", StringComparison.Ordinal)..];
        var resetInterp = seek.IndexOf("_playhead.Reset();", StringComparison.Ordinal);
        var resetFloor = seek.IndexOf("_lastKnownPosition.Reset(Math.Max(0.0, timeMs), _playhead.Epoch);",
            StringComparison.Ordinal);
        Assert.True(resetInterp >= 0 && resetFloor > resetInterp,
            "SeekTo must reset the interpolator, then the floor with the new epoch.");

        var service = Read("JuceAudioPlaybackService.cs");
        var epochRead = service.IndexOf("var epoch = _playhead.Epoch;", StringComparison.Ordinal);
        var nativeRead = service.IndexOf("nativeMs = src.AudiblePosition.TotalMilliseconds;", StringComparison.Ordinal);
        Assert.True(epochRead >= 0 && nativeRead > epochRead, "The tick must read the epoch before the native position.");
        Assert.Contains("_playhead.TryTick(nativeMs, nowTicks, speed, epoch, out var reportMs)", service);

        var getter = Read("JuceAudioPlaybackService.Playhead.cs");
        Assert.Contains("_lastKnownPosition.Clamp(lat, epoch)", getter);

        foreach (var file in PlaybackServiceSourceFiles.All())
        {
            Assert.DoesNotContain("_lastKnownPositionMs", File.ReadAllText(file));
        }
    }
}
