using System.Reflection;
using DotCharter.Services.Audio.LivePlay;

namespace DotCharter.Tests.UI.ViewModels.PlayMode;

/// <summary>Late classic-detector reports delivered explicitly after pausing, independent of DSP latency.</summary>
public sealed class PauseCaptureReviewTests
{
    [Theory]
    [InlineData(840, false, LivePlayVerdict.Hit)]
    [InlineData(1000, false, LivePlayVerdict.Miss)]
    [InlineData(840, true, LivePlayVerdict.Miss)]
    public void PausedReports_OnlyCreditAnAttackCapturedBeforeThePause(int capturedMs, bool seek, LivePlayVerdict expected)
    {
        using var h = new PlayModeReviewHarness(finalNote: true, noteMs: 740, inputOffsetMs: 100);
        var clock = new PlayModeCompletionMatrixTests.CaptureClock();
        h.Audio.Clock = clock;
        typeof(LivePlayCompletion).GetProperty("Clock")!.SetValue(
            PlayModeReviewHarness.Get<LivePlayCompletion>(h.Live, "_completion"), clock);
        h.Begin();
        var session = PlayModeReviewHarness.Get<LivePlaySessionService>(h.Live, "_session");
        var sound = Assert.Single(session.Matcher!.Expected);
        const int block = PlayModeReviewHarness.SampleRate / 100;
        for (var wallMs = 10; wallMs <= 1050; wallMs += 10)
        {
            clock.Advance(10);
            h.Audio.Feed(new float[block]);
            h.Audio.IsPlaying = wallMs <= 900;
            h.Audio.PublishPosition(Math.Min(wallMs, 900));
            if (seek && wallMs == 950) h.Audio.SeekTo(900);
            h.Tick();
        }

        Assert.Null(sound.Verdict);
        Assert.False(h.Audio.IsPlaying);
        var ev = new LiveDetectionEvent(false, (ulong)(capturedMs * PlayModeReviewHarness.SampleRate / 1000),
            capturedMs / 1000d, [new LivePitch(45, 0, 1)], 110, [], 0, 1, 1, 0, 1, LiveTechniqueFlags.None);
        var uiType = h.Live.GetType().GetNestedType("TickUiState", BindingFlags.NonPublic)!;
        var ui = Activator.CreateInstance(uiType, nonPublic: true)!;
        PlayModeReviewHarness.Invoke(h.Live, "JudgeDetectionEvent", ev, 900d, false, ui);
        Assert.Equal(expected == LivePlayVerdict.Hit ? LivePlayVerdict.Hit : null, sound.Verdict);
        h.FinishForResults();
        Assert.Equal(expected, sound.Verdict);
        var saved = Assert.Single(Assert.Single(h.Store.GetAll().Values).Sessions);
        Assert.Equal(expected == LivePlayVerdict.Hit ? 1 : 0, saved.Hits);
        Assert.Equal(expected == LivePlayVerdict.Miss ? 1 : 0, saved.Misses);
    }

    [Theory]
    // The transport ran to 890 ms, but the last read made while it still reported playing
    // ended at 880 ms: an attack captured at 885 ms was played, one at 895 ms was not.
    [InlineData(885, LivePlayVerdict.Hit)]
    [InlineData(895, LivePlayVerdict.Miss)]
    public void AttackBetweenTheLastPlayingReadAndThePause_IsStillCredited(int capturedMs, LivePlayVerdict expected)
    {
        using var h = new PlayModeReviewHarness(finalNote: true, noteMs: 785, inputOffsetMs: 100);
        var (sound, clock) = Start(h);
        Run(h, clock, 10, 1050, wallMs =>
        {
            h.Audio.IsPlaying = wallMs <= 880;
            h.Audio.PublishPosition(Math.Min(wallMs, 890));
        });

        Judge(h, capturedMs);
        Assert.Equal(expected == LivePlayVerdict.Hit ? LivePlayVerdict.Hit : null, sound.Verdict);
        h.FinishForResults();
        Assert.Equal(expected, sound.Verdict);
    }

    [Fact]
    public void SampleCounterRestartedDuringThePause_ScoresNothingPlayedWhilePaused()
    {
        using var h = new PlayModeReviewHarness(finalNote: true, noteMs: 740, inputOffsetMs: 100);
        var (sound, clock) = Start(h);
        Run(h, clock, 10, 1800, wallMs =>
        {
            h.Audio.IsPlaying = wallMs <= 900;
            h.Audio.PublishPosition(Math.Min(wallMs, 900));
        }, afterTick: wallMs =>
        {
            // An engine rebuild while paused: the capture counter starts again from zero.
            if (wallMs == 950) h.Audio.RebuildCapture();
        });

        // Played ~840 ms after the restart: on the NEW counter that sample lies before the old
        // pause point, so the old mapping would place it on the note at 740 ms.
        Judge(h, 840);
        Assert.Null(sound.Verdict);
        h.FinishForResults();
        Assert.Equal(LivePlayVerdict.Miss, sound.Verdict);
    }

    private static (ExpectedSound Sound, PlayModeCompletionMatrixTests.CaptureClock Clock) Start(PlayModeReviewHarness h)
    {
        var clock = new PlayModeCompletionMatrixTests.CaptureClock();
        h.Audio.Clock = clock;
        typeof(LivePlayCompletion).GetProperty("Clock")!.SetValue(
            PlayModeReviewHarness.Get<LivePlayCompletion>(h.Live, "_completion"), clock);
        h.Begin();
        var session = PlayModeReviewHarness.Get<LivePlaySessionService>(h.Live, "_session");
        return (Assert.Single(session.Matcher!.Expected), clock);
    }

    private static void Run(PlayModeReviewHarness h, PlayModeCompletionMatrixTests.CaptureClock clock,
        int fromMs, int toMs, Action<int> transport, Action<int>? afterTick = null)
    {
        const int block = PlayModeReviewHarness.SampleRate / 100;
        for (var wallMs = fromMs; wallMs <= toMs; wallMs += 10)
        {
            clock.Advance(10);
            h.Audio.Feed(new float[block]);
            transport(wallMs);
            h.Tick();
            afterTick?.Invoke(wallMs);
        }
    }

    private static void Judge(PlayModeReviewHarness h, int capturedMs)
    {
        Assert.False(h.Audio.IsPlaying);
        var ev = new LiveDetectionEvent(false, (ulong)(capturedMs * PlayModeReviewHarness.SampleRate / 1000),
            capturedMs / 1000d, [new LivePitch(45, 0, 1)], 110, [], 0, 1, 1, 0, 1, LiveTechniqueFlags.None);
        var uiType = h.Live.GetType().GetNestedType("TickUiState", BindingFlags.NonPublic)!;
        var ui = Activator.CreateInstance(uiType, nonPublic: true)!;
        PlayModeReviewHarness.Invoke(h.Live, "JudgeDetectionEvent", ev, h.Audio.CurrentTimeMs, false, ui);
    }
}
