using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
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
///     Every Play Mode SCREEN as a picture (owner, 2026-09-08: "a screenshot of
///     every single page in all possible configurations … so I can let it be
///     analyzed by a designer"). One case per <see cref="PlayModeScreen" />
///     value over a fresh profile store and no song: the view is navigated to
///     the screen, the Background queue drained (deferred bodies build there),
///     laid out, and — with the capture env vars set — written to
///     <c>playmode-&lt;screen&gt;.png</c>. A screen that needs live state (a
///     loaded song, a session) still captures whatever it shows at rest, which
///     is exactly the empty state a designer should also see.
///     <para>The assertion is the smoke-test minimum — the screen root the
///     view model reports for that screen is visible and measured — so the
///     case doubles as a construction + layout guard for screens the sweep only
///     reaches as hidden sub-surfaces. Own process (headless suite).</para>
/// </summary>
[Collection("HeadlessUI")]
public sealed class PlayModeScreensCaptureUiPerfSmokeTests : IDisposable
{
    private const int WindowWidth = 1600;
    private const int WindowHeight = 900;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dc-screens-" + Guid.NewGuid().ToString("N"));

    public static IEnumerable<object[]> Screens() =>
        Enum.GetValues<PlayModeScreen>().Select(s => new object[] { s.ToString() });

    [Theory]
    [MemberData(nameof(Screens))]
    public async Task EveryPlayModeScreen_LaysOut_AndIsCaptured(string screenName)
    {
        var screen = Enum.Parse<PlayModeScreen>(screenName);
        await HeadlessSessionFixture.Dispatch(() =>
        {
            Directory.CreateDirectory(_dir);
            var profiles = new PlayerProfileService(FakeLocalization.English(), logger: null,
                filePath: Path.Combine(_dir, "player-profiles.json"), defaultProfileName: "Spieler 1");
            var store = new PracticeProfileService(profiles, logger: null,
                legacyFilePath: Path.Combine(_dir, "practice-profiles.json"));
            var vm = TestRigBuilders.PlayMode(profiles, new GameplayStatsService(store),
                new LeaderboardService(profiles), new ProjectState(), toneRig: HeadlessToneRig.FromSession(new ProjectState()), localization: LocalizationService.Instance);

            var view = new PlayModeView { DataContext = vm };
            var window = new Window { Width = WindowWidth, Height = WindowHeight, Content = view };
            window.Show();
            try
            {
                Dispatcher.UIThread.RunJobs(DispatcherPriority.Background);
                window.UpdateLayout();
                vm.CurrentScreen = screen;
                Dispatcher.UIThread.RunJobs(DispatcherPriority.Background);
                for (var pass = 0; pass < 3; pass++) window.UpdateLayout();

                Assert.Equal(screen, vm.CurrentScreen);
                Assert.True(view.Bounds.Width > 1000 && view.Bounds.Height > 500, $"{screen}: the view measured {view.Bounds}");

                var dir = Environment.GetEnvironmentVariable("DOTCHARTER_RIG_CAPTURE_DIR");
                if (UiSweepRunner.CaptureEnabled && !string.IsNullOrWhiteSpace(dir))
                {
                    Directory.CreateDirectory(dir);
                    using var frame = window.CaptureRenderedFrame();
                    Assert.NotNull(frame);
                    frame.Save(Path.Combine(dir, $"playmode-{screen}.png"), new PngBitmapEncoderOptions());
                }
            }
            finally { window.Close(); }
        }, TestContext.Current.CancellationToken);
    }

    public void Dispose() => BestEffortDelete.Directory(_dir);
}
