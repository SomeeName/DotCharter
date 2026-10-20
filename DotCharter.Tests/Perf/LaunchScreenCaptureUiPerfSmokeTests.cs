using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DotCharter.Localization;
using DotCharter.Services.Audio.LivePlay;
using DotCharter.Services.Gameplay.Leaderboards;
using DotCharter.Services.Gameplay.Profiles;
using DotCharter.Services.Gameplay.Stats;
using DotCharter.Tone.Infrastructure.Storage;
using DotCharter.Tone.UI.Controls.Fire;
using DotCharter.UI.ViewModels.PlayMode;
using DotCharter.UI.Views.PlayMode;

namespace DotCharter.Tests.Perf;

/// <summary>
///     The LAUNCH screen's layout harness (the same shape as
///     <c>RigEditorCaptureUiPerfSmokeTests</c>): the app's first surface over a
///     fresh profile store, at the size the owner's screenshot had, written to
///     <c>launch.png</c> with the capture env vars set. And two facts the
///     owner's screenshot broke (2026-09-08): every visible line of text in
///     the three status chips is in a readable ink — the state captions were
///     the FAINT mono, the only line in an empty chip, and could not be read —
///     and the START label is centred on its button rather than parked beside
///     the play icon at the left edge. Own process (headless suite).
/// </summary>
[Collection("HeadlessUI")]
public sealed class LaunchScreenCaptureUiPerfSmokeTests : IDisposable
{
    private const int WindowWidth = 900;
    private const int WindowHeight = 820;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dc-launch-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task TheLaunchScreen_ReadsInEveryChipState_AndCentresStart()
    {
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
                // The launch facts land with the Background warm-up the first
                // frame drains for real; until then every chip binds the EMPTY
                // snapshot and shows nothing (the same drain the front-door test does).
                Dispatcher.UIThread.RunJobs(DispatcherPriority.Background);
                for (var pass = 0; pass < 3; pass++) window.UpdateLayout();
                Assert.NotSame(LaunchFacts.Empty, vm.Launch);
                var root = Named<Grid>(view, "StartScreenRoot");
                Assert.True(root.IsVisible && root.Bounds.Height > 400, $"the launch screen measured {root.Bounds}");

                // Every chip has a visible TITLE line, and no visible line wears the faint ink.
                var faint = FireUnitState.ColorFrom(root, "FireTextFaint");
                var chips = Named<Grid>(view, "LaunchStatusChips");
                foreach (var name in new[] { "LaunchLibraryChip", "LaunchInputChip" })
                {
                    var chip = Named<Border>(view, name);
                    var visible = chip.GetVisualDescendants().OfType<TextBlock>()
                        .Where(t => t.IsEffectivelyVisible && !string.IsNullOrWhiteSpace(t.Text)).ToList();
                    Assert.NotEmpty(visible);
                    Assert.Contains(visible, t => t.Classes.Contains("FireRowTitle"));
                    foreach (var line in visible)
                    {
                        var ink = Assert.IsAssignableFrom<ISolidColorBrush>(line.Foreground).Color;
                        Assert.True(ink != faint, $"{name}: '{line.Text}' is in the faint ink nobody can read");
                    }
                }
                Assert.True(chips.Bounds.Width > 400, $"the status chips measured {chips.Bounds}");

                // START: the label's centre sits within a few px of the button's centre.
                // The ▶ START group is a StackPanel inside the button's content
                // Panel; both centres are read in that Panel's own coordinates.
                var start = Named<Button>(view, "LaunchStartButton");
                var label = start.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Classes.Contains("FireCtaLabel"));
                var group = Assert.IsType<StackPanel>(label.Parent);
                var face = Assert.IsAssignableFrom<Panel>(group.Parent);
                Assert.True(face.Bounds.Width > 300, $"the START face measured {face.Bounds}");
                Assert.True(Math.Abs(group.Bounds.Center.X - face.Bounds.Width / 2) < 4,
                    $"▶ START sits at x={group.Bounds.X:0}–{group.Bounds.Right:0} on a {face.Bounds.Width:0} px face — not centred");
                Assert.True(Math.Abs(group.Bounds.Center.Y - face.Bounds.Height / 2) < 4,
                    $"▶ START sits at y={group.Bounds.Y:0}–{group.Bounds.Bottom:0} on a {face.Bounds.Height:0} px face");
                Assert.True(label.Bounds.Width > 40 && label.Bounds.Height > 10, $"the START label measured {label.Bounds}");

                var dir = Environment.GetEnvironmentVariable("DOTCHARTER_RIG_CAPTURE_DIR");
                if (UiSweepRunner.CaptureEnabled && !string.IsNullOrWhiteSpace(dir))
                {
                    Directory.CreateDirectory(dir);
                    using var frame = window.CaptureRenderedFrame();
                    Assert.NotNull(frame);
                    frame.Save(Path.Combine(dir, "launch.png"), new PngBitmapEncoderOptions());
                }
            }
            finally { window.Close(); }
        }, TestContext.Current.CancellationToken);
    }

    /// <summary>A control by <c>x:Name</c> anywhere in the view's visual tree: the
    /// launch screen is its own view since #294, so its names are in THAT view's
    /// namescope and <c>FindControl</c> on <c>PlayModeView</c> no longer sees them.</summary>
    private static T Named<T>(Control view, string name) where T : Control =>
        Assert.IsAssignableFrom<T>(view.GetVisualDescendants().OfType<Control>().FirstOrDefault(c => c.Name == name));

    public void Dispose() => BestEffortDelete.Directory(_dir);
}
