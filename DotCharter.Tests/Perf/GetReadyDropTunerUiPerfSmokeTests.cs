using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
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
///     Get Ready's per-song drop-tuner row (#635), built for real: <see cref="PlayModeView" />
///     attached to a shown <c>Window</c>, navigated to Get Ready on a real chart and laid out.
///     The row must MEASURE inside the "Choose your path" panel (a style that failed to resolve
///     still arranges a zero-size switch), be live on a path below standard, take "Play in my
///     tuning" down with its reason while on, and be disabled with its own reason on a path the
///     engine cannot honour.
///     <para>
///         Width-shaped (every row is asserted to fit the 404 px rail), so it is on the release's
///         de-DE / ja-JP layout list (<c>$HeadlessLayoutFilter</c> in
///         <c>tools/release/New-Release.ps1</c>). With <c>DOTCHARTER_UI_SWEEP_DUMP=1</c> and
///         <c>DOTCHARTER_RIG_CAPTURE_DIR</c> set, each state is written as a PNG.
///     </para>
/// </summary>
[Collection("HeadlessUI")]
public sealed class GetReadyDropTunerUiPerfSmokeTests : IDisposable
{
    private const double WindowWidth = 1600;
    private const double WindowHeight = 1000;

    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "DotCharterTests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task GetReady_DropTuner_IsLiveBelowStandard_OverrulesPlayInMyTuning_AndIsRefusedWithItsReason() =>
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var (window, view, vm) = Build();
            try
            {
                vm.PreparePrePlay();
                vm.CurrentScreen = PlayModeScreen.TuningCheck;
                view.UpdateLayout();
                var panel = Find(view, "GetReadyPathPanel");
                Find(view, "GetReadyDropTuner").BringIntoView();
                view.UpdateLayout();

                // OFF on the E-flat path: the switch is live and "Play in my tuning" is untouched.
                AssertRowFits(view, panel);
                var drop = Find<ToggleSwitch>(view, "GetReadyDropTunerToggle");
                var match = Find<ToggleSwitch>(view, "GetReadyTuningMatchToggle");
                Assert.True(drop.IsEffectivelyEnabled, "an E-flat path can be played through the drop tuner");
                Assert.False(drop.IsChecked);
                Assert.Contains("-1", Find<TextBlock>(view, "GetReadyDropTunerLabel").Text);
                Assert.True(match.IsEffectivelyEnabled);
                Capture(window, "getready-droptuner-off.png");

                // ON, with "Play in my tuning" opted in: that switch goes off and disabled, and its
                // status line prints the reason.
                vm.TuningMatch.IsOn = true;
                vm.DropTuner.IsOn = true;
                view.UpdateLayout();
                Assert.True(drop.IsChecked);
                Assert.False(match.IsEffectivelyEnabled, "the drop tuner wins over Play in my tuning");
                Assert.False(match.IsChecked);
                AssertLaidOut(view, "GetReadyTuningMatchStatus");
                Assert.Equal(vm.TuningMatch.StatusText, Find<TextBlock>(view, "GetReadyTuningMatchStatus").Text);
                AssertContains(panel, Find(view, "GetReadyTuningMatchStatus"), "Play in my tuning's reason");
                AssertRowFits(view, panel);
                Assert.Equal(vm.DropTuner.StandardTuningName, Find<TextBlock>(view, "GetReadyTuningLockup").Text);
                Capture(window, "getready-droptuner-on.png");

                // A Drop D path: the engine shifts every string together, so the switch is off and
                // disabled, with its own reason printed.
                vm.SelectedFlowArrangement = vm.FlowArrangements[1];
                view.UpdateLayout();
                Assert.False(drop.IsEffectivelyEnabled);
                Assert.False(drop.IsChecked);
                Assert.False(string.IsNullOrWhiteSpace(vm.DropTuner.StatusText));
                Assert.Equal(vm.DropTuner.StatusText, Find<TextBlock>(view, "GetReadyDropTunerStatus").Text);
                AssertRowFits(view, panel);
                Capture(window, "getready-droptuner-refused.png");

                // An E-standard path: nothing to shift, so no row.
                vm.SelectedFlowArrangement = vm.FlowArrangements[2];
                view.UpdateLayout();
                Assert.False(Find(view, "GetReadyDropTuner").IsVisible);
            }
            finally
            {
                window.Close();
            }
        }, CancellationToken.None);

    /// <summary>The row, its switch and its status line measure and sit inside the path panel.</summary>
    private static void AssertRowFits(PlayModeView view, Control panel)
    {
        foreach (var name in new[] { "GetReadyDropTuner", "GetReadyDropTunerLabel", "GetReadyDropTunerToggle", "GetReadyDropTunerStatus" })
        {
            AssertLaidOut(view, name);
            AssertContains(panel, Find(view, name), name);
        }
    }

    private (Window Window, PlayModeView View, PlayModeViewModel Vm) Build()
    {
        var profiles = new PlayerProfileService(FakeLocalization.English(), logger: null,
            filePath: Path.Combine(_dir, "player-profiles.json"), defaultProfileName: "Tester");
        var store = new PracticeProfileService(profiles, logger: null,
            legacyFilePath: Path.Combine(_dir, "practice-profiles.json"));
        var state = new ProjectState();
        Seed(state);
        var vm = TestRigBuilders.PlayMode(profiles, new GameplayStatsService(store),
            new LeaderboardService(profiles), state, toneRig: HeadlessToneRig.FromSession(state), localization: LocalizationService.Instance);

        var view = new PlayModeView { DataContext = vm };
        var window = new Window { Width = WindowWidth, Height = WindowHeight, Content = view };
        // Show() roots the tree; layout is driven through the VIEW (measuring a headless Window
        // directly reports "Invalid size returned for Measure").
        window.Show();
        view.Measure(new Size(WindowWidth, WindowHeight));
        view.Arrange(new Rect(0, 0, WindowWidth, WindowHeight));
        return (window, view, vm);
    }

    /// <summary>An E-flat lead (drop-tunable, and shiftable for Play in my tuning), a Drop D
    /// rhythm (neither) and an E-standard bass (nothing to do).</summary>
    private static void Seed(ProjectState state)
    {
        var project = new ChartProject();
        foreach (var (name, type, steps) in new[]
                 {
                     ("Lead", ArrangementType.Lead, new[] { -1, -1, -1, -1, -1, -1 }),
                     ("Rhythm", ArrangementType.Rhythm, new[] { -2, 0, 0, 0, 0, 0 }),
                     ("Bass", ArrangementType.Bass, new[] { 0, 0, 0, 0 })
                 })
        {
            var arrangement = new Arrangement { Name = name, ArrangementType = type, StringCount = steps.Length };
            foreach (var step in steps) arrangement.TuningPitches.Add(new TuningOffset(step));
            var level = new ChartLevel { DifficultyIndex = 0 };
            for (var i = 0; i < 12; i++)
                level.Notes.Add(new GuitarNote { StringIndex = i % steps.Length, Fret = i % 5 });
            arrangement.Levels.Add(level);
            project.Arrangements.Add(arrangement);
        }

        state.ActiveProject = project;
        state.ActiveArrangement = project.Arrangements[0];
    }

    private static Control Find(PlayModeView view, string name)
    {
        var control = view.GetVisualDescendants().OfType<Control>().FirstOrDefault(c => c.Name == name);
        Assert.True(control != null,
            $"'{name}' is not in the visual tree — the screen never realized (a XAML fault or a renamed element).");
        return control!;
    }

    private static T Find<T>(PlayModeView view, string name) where T : Control =>
        Assert.IsAssignableFrom<T>(Find(view, name));

    private static void AssertLaidOut(PlayModeView view, string name)
    {
        var control = Find(view, name);
        Assert.True(control.IsVisible, $"'{name}' realized but is not visible");
        Assert.True(control.Bounds.Width > 1 && control.Bounds.Height > 1,
            $"'{name}' laid out as {control.Bounds} — it rendered nothing");
    }

    /// <summary>A descendant lies inside its ancestor's box (translated, 1 px tolerance for the
    /// panels' hairline borders).</summary>
    private static void AssertContains(Control ancestor, Control child, string what)
    {
        var topLeft = child.TranslatePoint(default, ancestor);
        var bottomRight = child.TranslatePoint(new Point(child.Bounds.Width, child.Bounds.Height), ancestor);
        Assert.True(topLeft.HasValue && bottomRight.HasValue, $"{what} is not in the same visual tree as its panel");
        Assert.True(topLeft!.Value.X >= -1 && topLeft.Value.Y >= -1, $"{what} starts at {topLeft.Value}, outside its panel");
        Assert.True(bottomRight!.Value.X <= ancestor.Bounds.Width + 1,
            $"{what} reaches x={bottomRight.Value.X:F0} in a {ancestor.Bounds.Width:F0} px panel");
        Assert.True(bottomRight.Value.Y <= ancestor.Bounds.Height + 1,
            $"{what} reaches y={bottomRight.Value.Y:F0} in a {ancestor.Bounds.Height:F0} px panel");
    }

    private static void Capture(Window window, string fileName)
    {
        var dir = Environment.GetEnvironmentVariable("DOTCHARTER_RIG_CAPTURE_DIR");
        if (!UiSweepRunner.CaptureEnabled || string.IsNullOrWhiteSpace(dir)) return;
        Directory.CreateDirectory(dir);
        using var bitmap = new RenderTargetBitmap(new PixelSize((int)WindowWidth, (int)WindowHeight));
        bitmap.Render(window);
        bitmap.Save(Path.Combine(dir, fileName), new PngBitmapEncoderOptions());
    }

    public void Dispose() => BestEffortDelete.Directory(_dir);
}
