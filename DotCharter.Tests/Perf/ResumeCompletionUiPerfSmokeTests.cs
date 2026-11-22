using DotCharter.Tests.UI.ViewModels.PlayMode;
using DotCharter.UI.ViewModels.PlayMode;

namespace DotCharter.Tests.Perf;

/// <summary>The real completion notification reaches Results on the UI dispatcher.</summary>
[Collection("HeadlessUI")]
public sealed class ResumeCompletionUiPerfSmokeTests
{
    [Fact]
    public Task PauseRiffRepeaterSpaceWithoutLoop_ReachesResultsAndSavesOnce()
        => HeadlessSessionFixture.Dispatch(() =>
        {
            using var run = new CapturedTickTestRun(loop: false, noInput: true);
            ResumeCompletionReviewTests.Resume(run.H, "riff-space", false);
            run.H.Audio.PublishPosition(4000);
            run.H.EndWatch();
            run.H.EndWatch();
            Assert.Equal(PlayModeScreen.Results, run.H.Vm.CurrentScreen);
            Assert.False(run.H.Live.IsActive);
            Assert.Single(run.H.Store.GetAll().Values.SelectMany(p => p.Sessions));
        }, TestContext.Current.CancellationToken);
}
