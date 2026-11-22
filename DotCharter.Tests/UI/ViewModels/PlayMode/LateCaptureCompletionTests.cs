using DotCharter.Services.Audio.LivePlay;

namespace DotCharter.Tests.UI.ViewModels.PlayMode;

/// <summary>The first real capture sync must replace an EOF-only synthetic anchor.</summary>
public sealed class LateCaptureCompletionTests
{
    [Fact]
    public void PausedLoopBoundary_DoesNotRestartOrJudgeThePausedRun()
    {
        using var h = new PlayModeReviewHarness(finalNote: true, noteMs: 1600, inputOffsetMs: 100);
        h.State.TotalAudioLengthMs = 2000;
        var clock = new PlayModeCompletionMatrixTests.CaptureClock();
        h.Audio.Clock = clock;
        typeof(LivePlayCompletion).GetProperty("Clock")!.SetValue(
            PlayModeReviewHarness.Get<LivePlayCompletion>(h.Live, "_completion"), clock);
        h.Begin();
        var session = PlayModeReviewHarness.Get<LivePlaySessionService>(h.Live, "_session");
        session.SetLoop(0, 2000);
        h.Live.ArmSongEnd(0); // the actual Play Mode pause path disarms transport completion
        h.Audio.IsPlaying = false;
        h.Audio.PublishPosition(2000);
        for (var step = 0; step < 100; step++)
        {
            clock.Advance(10);
            h.Audio.Feed(new float[PlayModeReviewHarness.SampleRate / 100]);
            h.Tick();
        }
        Assert.False(h.Audio.IsPlaying);
        Assert.Equal(0, session.PassCount);
        Assert.Null(Assert.Single(session.Matcher!.Expected).Verdict);
    }

    [Fact]
    public void LoopAtEof_SettlesSilentCaptureAndRestartsTransport()
    {
        using var h = new PlayModeReviewHarness(finalNote: true, noteMs: 1600, inputOffsetMs: 100);
        h.State.TotalAudioLengthMs = 2000;
        var clock = new PlayModeCompletionMatrixTests.CaptureClock();
        h.Audio.Clock = clock;
        typeof(LivePlayCompletion).GetProperty("Clock")!.SetValue(
            PlayModeReviewHarness.Get<LivePlayCompletion>(h.Live, "_completion"), clock);
        h.Begin();
        var session = PlayModeReviewHarness.Get<LivePlaySessionService>(h.Live, "_session");
        session.SetLoop(0, 2000);
        const int block = PlayModeReviewHarness.SampleRate / 100;
        for (var ms = 0; ms < 2500 && session.PassCount == 0; ms += 10)
        {
            clock.Advance(10);
            h.Audio.Feed(new float[block]);
            h.Audio.IsPlaying = ms < 1990;
            h.Audio.PublishPosition(Math.Min(ms + 10, 2000));
            h.Ui.Flush();
            h.Tick();
        }
        Assert.Equal(1, session.PassCount);
        Assert.Single(session.LastPassMisses);
        Assert.True(h.Live.IsActive);
        Assert.True(h.Audio.IsPlaying);
        Assert.Equal(0, h.Audio.CurrentTimeMs);
    }

    [Fact]
    public void ManualStop_SettlesSectionCountsBeforeTheReportSnapshot()
    {
        using var h = new PlayModeReviewHarness(finalNote: true, noteMs: 600, inputOffsetMs: 100);
        var clock = new PlayModeCompletionMatrixTests.CaptureClock();
        h.Audio.Clock = clock;
        typeof(LivePlayCompletion).GetProperty("Clock")!.SetValue(
            PlayModeReviewHarness.Get<LivePlayCompletion>(h.Live, "_completion"), clock);
        h.Begin();
        const int block = PlayModeReviewHarness.SampleRate / 100;
        for (var ms = 0; ms < 990; ms += 10)
        {
            clock.Advance(10);
            h.Audio.Feed(new float[block]);
            h.Audio.PublishPosition(ms + 10);
            h.Ui.Flush();
            h.Tick();
        }
        var report = Assert.IsType<LivePlaySessionReport>(PlayModeReviewHarness.Invoke(h.Live, "StopCore"));
        Assert.Equal(1, report.Result.Misses);
        Assert.Equal(1, Assert.Single(report.Sections).Misses);
    }

    [Fact]
    public void CaptureStartsAfterEof_UsesRealSyncNotSyntheticAnchor() => Run(20, 100, 1300, 1400);

    [Theory]
    [InlineData(700, 0)]
    [InlineData(700, 100)]
    [InlineData(700, -100)]
    [InlineData(120, 0)]
    [InlineData(120, 100)]
    [InlineData(120, -100)]
    [InlineData(20, 0)]
    [InlineData(20, 100)]
    [InlineData(20, -100)]
    public void LateCaptureMatrix_RejectsAnAttackPastTheRealLateEdge(int distance, int offset)
        => Run(distance, offset, 1300, 1400);

    [Theory]
    [InlineData(0)]
    [InlineData(100)]
    [InlineData(-100)]
    public void LateCapture_StillAcceptsEvidenceInsideItsCalibratedWindow(int offset)
        => Run(20, offset, 1100, 1200);

    private static void Run(int distance, int offset, int firstCaptureMs, int attackMs)
    {
        using var h = new PlayModeReviewHarness(finalNote: true, noteMs: 1000 - distance, inputOffsetMs: offset);
        var clock = new PlayModeCompletionMatrixTests.CaptureClock();
        h.Audio.Clock = clock;
        typeof(LivePlayCompletion).GetProperty("Clock")!.SetValue(
            PlayModeReviewHarness.Get<LivePlayCompletion>(h.Live, "_completion"), clock);
        h.Begin();
        var sound = Assert.Single(PlayModeReviewHarness.Get<LivePlaySessionService>(h.Live, "_session").Matcher!.Expected);
        var samples = PlayModeCompletionMatrixTests.Pluck(attackMs);
        const int block = PlayModeReviewHarness.SampleRate / 100;
        for (var ms = 0; ms < 2500; ms += 10)
        {
            clock.Advance(10);
            if (h.Live.IsActive && ms >= firstCaptureMs) h.Audio.Feed(samples.AsSpan(ms / 10 * block, block));
            h.Audio.IsPlaying = ms < 990;
            if (ms <= 990) h.Audio.PublishPosition(Math.Min(ms + 10, 1000));
            h.Ui.Flush();
            h.Tick();
            if (ms >= 990) h.EndWatch();
        }
        Assert.False(h.Live.IsActive);
        var shouldHit = attackMs - offset <= 1000 - distance + 250;
        Assert.Equal(shouldHit ? LivePlayVerdict.Hit : LivePlayVerdict.Miss, sound.Verdict);
        var saved = Assert.Single(Assert.Single(h.Store.GetAll().Values).Sessions);
        Assert.Equal(shouldHit ? 1 : 0, saved.Hits);
        Assert.Equal(shouldHit ? 0 : 1, saved.Misses);
    }
}
