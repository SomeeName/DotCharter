using DotCharter.Services.Audio.LivePlay;

namespace DotCharter.Tests.UI.ViewModels.PlayMode;

public sealed class LoopClearCompletionReviewTests
{
    [Fact]
    public void ClearingLoopAtItsBoundary_DoesNotFreezeLaterJudgments()
    {
        using var h = new PlayModeReviewHarness(finalNote: true, noteMs: 2600, inputOffsetMs: 100);
        for (var i = 4; i < 10; i++) h.State.ActiveProject!.Beats.Add(new BeatMarker { Time = i * 500, Bpm = 120 });
        h.State.TotalAudioLengthMs = 4000;
        var clock = new PlayModeCompletionMatrixTests.CaptureClock();
        h.Audio.Clock = clock;
        typeof(LivePlayCompletion).GetProperty("Clock")!.SetValue(
            PlayModeReviewHarness.Get<LivePlayCompletion>(h.Live, "_completion"), clock);
        h.Begin();
        var session = PlayModeReviewHarness.Get<LivePlaySessionService>(h.Live, "_session");
        session.SetLoop(0, 2000);
        for (var ms = 10; ms <= 3000; ms += 10)
        {
            clock.Advance(10);
            h.Audio.Feed(new float[PlayModeReviewHarness.SampleRate / 100]);
            h.Audio.PublishPosition(ms);
            h.Ui.Flush();
            h.Tick();
            if (ms == 2010) h.Live.ClearLoopCommand.Execute(null);
        }
        var report = Assert.IsType<LivePlaySessionReport>(PlayModeReviewHarness.Invoke(h.Live, "StopCore"));
        Assert.Equal(1, report.Result.Misses);
        Assert.Equal(1, Assert.Single(report.Sections).Misses);
        Assert.Null(session.Loop);
    }
}
