using DotCharter.Services.Audio.LivePlay;

namespace DotCharter.Tests.UI.ViewModels.PlayMode;

public sealed class CompletionActionRaceReviewTests
{
    public static TheoryData<string, string, string> InterruptedActions
    {
        get
        {
            var cases = new TheoryData<string, string, string>();
            foreach (var state in new[] { "unanchored", "anchored", "synthetic-drain", "anchored-drain", "reanchored-drain", "evidence-ready", "stalled-ready", "reset" })
            {
                foreach (var action in new[] { "loop", "song-end" })
                {
                    foreach (var interruption in new[] { "pause", "stop", "seek", "run-end" }) cases.Add(state, action, interruption);
                }
            }
            return cases;
        }
    }

    [Fact]
    public async Task PauseBetweenLoopSnapshotAndRestart_StaysPausedAndAddsNoJudgment()
    {
        using var h = NewHarness(note: true);
        var clock = Clock(h);
        h.Begin();
        var session = PlayModeReviewHarness.Get<LivePlaySessionService>(h.Live, "_session");
        session.SetLoop(0, 2000);
        var signal = PlayModeCompletionMatrixTests.Pluck(500);
        const int block = PlayModeReviewHarness.SampleRate / 100;
        for (var ms = 10; ms < 2000; ms += 10)
        {
            clock.Advance(10);
            h.Audio.Feed(signal.AsSpan((ms / 10 - 1) * block, block));
            h.Audio.PublishPosition(ms);
            h.Tick();
        }
        Assert.Equal(1, session.Stats!.Hits);
        using var snapshotted = new ManualResetEventSlim();
        using var paused = new ManualResetEventSlim();
        Task? worker = null;
        h.Audio.PublishPosition(2000);
        h.Audio.BeforeCaptureRead = () =>
        {
            h.Audio.BeforeCaptureRead = null;
            snapshotted.Set();
            paused.Wait(TestContext.Current.CancellationToken);
        };
        h.Audio.BeforePause = () =>
        {
            worker = Task.Run(() => PlayModeReviewHarness.Invoke(h.Live, "WorkerTick"), TestContext.Current.CancellationToken);
            snapshotted.Wait(TestContext.Current.CancellationToken);
        };
        h.Audio.AfterPause = paused.Set;
        h.Vm.PauseGameplay(); // real disarm, then worker snapshot, then real audio pause
        await worker!;
        h.Audio.BeforePause = null;
        h.Audio.AfterPause = null;
        var restarted = h.Audio.IsPlaying;
        for (var ms = 10; ms <= 1000; ms += 10)
        {
            clock.Advance(10);
            h.Audio.Feed(new float[block]);
            if (h.Audio.IsPlaying) h.Audio.PublishPosition(ms);
            h.Tick();
        }
        h.Vm.EndRunFromPauseCommand.Execute(null);
        var saved = Assert.Single(Assert.Single(h.Store.GetAll().Values).Sessions);
        Assert.False(restarted);
        Assert.False(h.Audio.IsPlaying);
        Assert.Equal(0, saved.Misses);
        Assert.Equal(1, saved.Hits);
    }

    [Fact]
    public void UnarmedOrdinaryLoop_SeeksWhileAlreadyPlaying()
    {
        using var h = NewHarness(note: false);
        Clock(h);
        h.Begin();
        PlayModeReviewHarness.Get<LivePlaySessionService>(h.Live, "_session").SetLoop(0, 2000);
        h.Tick();
        h.Live.ArmSongEnd(0); // Editor practice has no gameplay song-end arm.
        h.Audio.PublishPosition(2000);
        h.Tick();
        Assert.Equal(0, h.Audio.CurrentTimeMs);
        Assert.True(h.Audio.IsPlaying);
    }

    [Theory]
    [MemberData(nameof(InterruptedActions))]
    public async Task ActionTable_InterruptionBetweenSnapshotAndGateReentryWins(string state, string action, string interruption)
    {
        using var h = NewHarness(note: false);
        var clock = Clock(h);
        h.Begin();
        if (action == "loop") PlayModeReviewHarness.Get<LivePlaySessionService>(h.Live, "_session").SetLoop(0, 2000);
        var completion = PlayModeReviewHarness.Get<LivePlayCompletion>(h.Live, "_completion");
        PendingSeekCompletionReviewTests.Prepare(completion, clock, state);
        h.Audio.PublishPosition(action == "loop" ? 2000 : 4000);
        if (action == "song-end") h.Audio.IsPlaying = false;
        var snapshot = (Epoch: PlayModeReviewHarness.Get<int>(h.Live, "_uiEpoch"), Seek: h.Audio.SeekGeneration);
        using var ready = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var worker = Task.Run(() =>
        {
            ready.Set();
            release.Wait(TestContext.Current.CancellationToken);
            if (action == "song-end")
            {
                h.Live.CompleteSong(4000);
            }
            else
            {
                var method = h.Live.GetType().GetMethod("CompleteLoopTransport", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
                method.Invoke(h.Live, method.GetParameters().Length == 1 ? [0d] : [0d, snapshot]);
            }
        }, TestContext.Current.CancellationToken);
        ready.Wait(TestContext.Current.CancellationToken);
        Interrupt(h, interruption);
        var position = h.Audio.CurrentTimeMs;
        var playing = h.Audio.IsPlaying;
        var active = h.Live.IsActive;
        release.Set();
        await worker;
        Assert.Equal(position, h.Audio.CurrentTimeMs);
        Assert.Equal(playing, h.Audio.IsPlaying);
        Assert.Equal(active, h.Live.IsActive);
    }

    private static void Interrupt(PlayModeReviewHarness h, string interruption)
    {
        switch (interruption)
        {
            case "pause": h.Vm.PauseGameplay(); break;
            case "stop": h.Live.IsActive = false; h.Audio.Pause(); break;
            case "seek": h.Audio.SeekTo(1000); break;
            case "run-end": h.Vm.PauseGameplay(); h.Vm.EndRunFromPauseCommand.Execute(null); break;
        }
    }

    private static PlayModeReviewHarness NewHarness(bool note)
    {
        var h = new PlayModeReviewHarness(finalNote: note, noteMs: 500);
        for (var i = 4; i < 10; i++) h.State.ActiveProject!.Beats.Add(new BeatMarker { Time = i * 500, Bpm = 120 });
        h.State.TotalAudioLengthMs = 4000;
        return h;
    }

    private static PlayModeCompletionMatrixTests.CaptureClock Clock(PlayModeReviewHarness h)
    {
        var clock = new PlayModeCompletionMatrixTests.CaptureClock();
        h.Audio.Clock = clock;
        typeof(LivePlayCompletion).GetProperty("Clock")!.SetValue(
            PlayModeReviewHarness.Get<LivePlayCompletion>(h.Live, "_completion"), clock);
        return clock;
    }
}
