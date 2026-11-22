using DotCharter.Services.Audio.LivePlay;
using DotCharter.UI.ViewModels.PlayMode;

namespace DotCharter.Tests.UI.ViewModels.PlayMode;

/// <summary>Every playback entry restores the completion boundary for the current transport.</summary>
public sealed class ResumeCompletionReviewTests
{
    internal static readonly string[] Routes =
        ["menu", "transport", "space", "riff-space", "riff-section", "riff-back", "restart", "loop-pass", "results-retry", "count-in", "device", "punch", "audition", "failed-retry"];

    public static TheoryData<string, bool> ResumeRoutes
    {
        get
        {
            var rows = new TheoryData<string, bool>();
            foreach (var route in Routes)
            {
                rows.Add(route, false);
                rows.Add(route, true);
            }
            return rows;
        }
    }

    [Fact]
    public void EditorLivePlay_DoesNotAcquireAPlayModeSongEndArm()
    {
        using var h = new PlayModeReviewHarness(finalNote: true);
        h.Live.IsActive = true;
        PlayModeReviewHarness.Get<LivePlayDetectionPump>(h.Live, "_pump").Stop();
        h.Transport.PlayCommand.Execute(null);
        Assert.True(h.Audio.IsPlaying);
        Assert.Equal(0, PlayModeReviewHarness.Get<double>(h.Live, "_songEndMs"));
    }

    [Fact]
    public void PauseRiffRepeaterSpaceWithoutLoop_ReachesResultsAndSavesOnce()
    {
        using var run = new CapturedTickTestRun(loop: false, noInput: true);
        Resume(run.H, "riff-space", false);
        run.H.Audio.PublishPosition(4000);
        run.H.EndWatch();
        run.H.EndWatch();
        Assert.False(run.H.Live.IsActive);
        Assert.Single(run.H.Store.GetAll().Values.SelectMany(p => p.Sessions));
    }

    [Theory]
    [MemberData(nameof(ResumeRoutes))]
    public void EveryResumeRoute_ArmsTheCurrentBoundary(string route, bool loop)
        => VerifyResume(route, loop);

    internal static void VerifyResume(string route, bool loop, Arrangement? chart = null)
    {
        using var run = new CapturedTickTestRun(loop: false, noInput: true, chart);
        Resume(run.H, route, loop);
        Assert.True(run.H.Audio.IsPlaying);
        run.H.Tick();
        var currentLoop = run.Session.Loop;
        Assert.Equal(currentLoop?.EndMs ?? 4000, PlayModeReviewHarness.Get<double>(run.H.Live, "_songEndMs"));
        Assert.Equal(loop && route is not ("restart" or "results-retry" or "failed-retry"), currentLoop != null);
        if (currentLoop is { } activeLoop)
        {
            run.H.Audio.PublishPosition(activeLoop.EndMs);
            run.H.Tick();
            Assert.Equal(0, run.H.Audio.CurrentTimeMs);
            Assert.True(run.H.Live.IsActive);
            run.H.Tick();
            run.H.Live.ClearLoopCommand.Execute(null);
        }
        run.H.Audio.PublishPosition(4000);
        run.H.EndWatch();
        run.H.EndWatch();
        Assert.False(run.H.Live.IsActive);
        Assert.Single(run.H.Store.GetAll().Values.SelectMany(p => p.Sessions));
    }

    internal static void Resume(PlayModeReviewHarness h, string route, bool loop)
    {
        h.Vm.PauseGameplay();
        h.Config.AppConfig.GameplayResumeRewindMs = 0;
        var session = PlayModeReviewHarness.Get<LivePlaySessionService>(h.Live, "_session");
        if (loop) session.SetLoop(0, 2000);
        switch (route)
        {
            case "menu": h.Vm.ResumeGameplayCommand.Execute(null); break;
            case "transport":
                h.Vm.OpenRiffRepeaterCommand.Execute(null);
                h.Transport.PlayCommand.Execute(null);
                break;
            case "space":
                h.Transport.TogglePlayPauseCommand.Execute(null);
                break;
            case "riff-space":
                h.Vm.OpenRiffRepeaterCommand.Execute(null);
                h.Transport.TogglePlayPauseCommand.Execute(null);
                break;
            case "riff-section":
                h.Vm.OpenRiffRepeaterCommand.Execute(null);
                h.Live.LoopSectionAt(0);
                if (!loop) h.Live.ClearLoopCommand.Execute(null);
                break;
            case "punch":
                h.Vm.OpenRiffRepeaterCommand.Execute(null);
                h.State.CurrentTimeMs = 0;
                h.Live.SetLoopStartCommand.Execute(null);
                h.State.CurrentTimeMs = 2000;
                h.Live.SetLoopEndCommand.Execute(null);
                if (!loop) h.Live.ClearLoopCommand.Execute(null);
                break;
            case "audition":
                h.Vm.OpenRiffRepeaterCommand.Execute(null);
                Assert.True(h.Transport.AuditionSpan(0, 1000));
                Assert.Equal(session.Loop?.EndMs ?? 4000, PlayModeReviewHarness.Get<double>(h.Live, "_songEndMs"));
                // An audition normally stops before EOF; finish it before continuing the song.
                h.Transport.StopCommand.Execute(null);
                h.Transport.PlayCommand.Execute(null);
                break;
            case "riff-back":
                h.Vm.OpenRiffRepeaterCommand.Execute(null);
                h.Vm.PauseGameplay();
                Assert.True(h.Vm.NavigateBack());
                break;
            case "restart":
            case "results-retry":
            case "failed-retry":
                // Countdown's final step enters the same BeginGameplay used by initial start.
                if (route is "results-retry" or "failed-retry")
                {
                    h.Vm.EndRunFromPauseCommand.Execute(null);
                    PlayModeReviewHarness.Set(h.Vm, "_flowSong",
                        PlaySongItem.FromProject(h.State.CurrentProjectPath!, true, Infrastructure.FakeLocalization.English()));
                    if (route == "results-retry") h.Vm.RetryCommand.Execute(null);
                    else h.Vm.RetryFromFailedSectionCommand.Execute(null);
                }
                else
                {
                    h.Vm.RestartRunCommand.Execute(null);
                }
                Assert.Equal(PlayModeScreen.Countdown, h.Vm.CurrentScreen);
                h.Audio.InputSampleRate = PlayModeReviewHarness.SampleRate;
                for (var i = 0; i < 3; i++) PlayModeReviewHarness.Invoke(h.Vm, "CountdownTick");
                PlayModeReviewHarness.Get<LivePlayDetectionPump>(h.Live, "_pump").Stop();
                h.Audio.InputSampleRate = 0;
                break;
            case "loop-pass":
                h.Vm.ResumeGameplayCommand.Execute(null);
                if (loop)
                {
                    h.Audio.PublishPosition(0);
                    h.Tick();
                    h.Audio.PublishPosition(2000);
                    h.Tick();
                }
                break;
            case "count-in":
                h.Vm.CurrentScreen = PlayModeScreen.Playing;
                PlayModeReviewHarness.Invoke(h.Vm, "StartGameplayTransport");
                break;
            case "device":
                h.Vm.OpenRiffRepeaterCommand.Execute(null);
                h.Audio.RebuildCapture();
                h.Transport.PlayCommand.Execute(null);
                break;
            default: throw new ArgumentOutOfRangeException(nameof(route));
        }
    }
}
