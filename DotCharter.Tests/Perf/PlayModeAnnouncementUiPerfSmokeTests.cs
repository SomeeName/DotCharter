using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Threading;
using DotCharter.Localization;
using DotCharter.Services.Audio.LivePlay;
using DotCharter.Services.Gameplay.Leaderboards;
using DotCharter.Services.Gameplay.Profiles;
using DotCharter.Services.Gameplay.Stats;
using DotCharter.Tone.Infrastructure.Storage;
using DotCharter.UI.ViewModels.PlayMode;
using DotCharter.UI.Views.PlayMode;

namespace DotCharter.Tests.Perf;

/// <summary>
///     Play Mode announces the run to a screen reader (#859): a polite live
///     region follows the play state, and the Pause menu takes focus when it opens.
/// </summary>
[Collection("HeadlessUI")]
public sealed class PlayModeAnnouncementUiPerfSmokeTests : IDisposable
{
    private const double WindowWidth = 1600;
    private const double WindowHeight = 1000;

    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "DotCharterTests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task TheLiveRegion_IsPolite_AndFollowsThePlayState()
    {
        await WithView((view, vm) =>
        {
            var region = view.FindControl<TextBlock>("PlayAnnouncement");
            Assert.NotNull(region);
            Assert.Equal(AutomationLiveSetting.Polite, AutomationProperties.GetLiveSetting(region));

            string Say(PlayModeScreen screen)
            {
                vm.CurrentScreen = screen;
                return region.Text ?? "";
            }

            vm.FlowSongTitle = "Sweet Child";
            Assert.Equal("", region.Text ?? "");
            Assert.Contains("Get ready", Say(PlayModeScreen.Countdown));
            Assert.Equal("Now playing: Sweet Child", Say(PlayModeScreen.Playing));
            Assert.Equal("Paused", Say(PlayModeScreen.Paused));
            Assert.Equal("Resumed", Say(PlayModeScreen.Playing));
            Assert.Equal("Song failed", Say(PlayModeScreen.Failed));
            Assert.Contains("Results", Say(PlayModeScreen.Results));
            Assert.Equal("", Say(PlayModeScreen.Start));
        });
    }

    [Fact]
    public async Task ThePauseMenu_TakesFocus_EveryTimeItOpens()
    {
        await WithView((view, vm) =>
        {
            for (var i = 0; i < 2; i++)
            {
                vm.CurrentScreen = PlayModeScreen.Paused;
                view.Measure(new Size(WindowWidth, WindowHeight));
                view.Arrange(new Rect(0, 0, WindowWidth, WindowHeight));
                Dispatcher.UIThread.RunJobs();

                var focused = TopLevel.GetTopLevel(view)?.FocusManager?.GetFocusedElement() as Control;
                Assert.NotNull(focused);
                Assert.Equal("PauseResume", focused.Name);

                vm.CurrentScreen = PlayModeScreen.Playing;
                view.Measure(new Size(WindowWidth, WindowHeight));
                Dispatcher.UIThread.RunJobs();
            }
        });
    }

    private async Task WithView(Action<PlayModeView, PlayModeViewModel> body)
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var profiles = new PlayerProfileService(FakeLocalization.English(), logger: null,
                filePath: Path.Combine(_dir, "player-profiles.json"), defaultProfileName: "Tester");
            var store = new PracticeProfileService(profiles, logger: null,
                legacyFilePath: Path.Combine(_dir, "practice-profiles.json"));

            var vm = TestRigBuilders.PlayMode(profiles, new GameplayStatsService(store),
                new LeaderboardService(profiles), new ProjectState(), toneRig: HeadlessToneRig.FromSession(new ProjectState()), localization: LocalizationService.Instance);

            var view = new PlayModeView { DataContext = vm };
            var window = new Window { Width = WindowWidth, Height = WindowHeight, Content = view };
            window.Show();
            try
            {
                view.Measure(new Size(WindowWidth, WindowHeight));
                view.Arrange(new Rect(0, 0, WindowWidth, WindowHeight));
                body(view, vm);
            }
            finally
            {
                vm.CurrentScreen = PlayModeScreen.Start;
                window.Close();
            }
        }, CancellationToken.None);
    }

    public void Dispose() => BestEffortDelete.Directory(_dir);
}
