// The in-run screens: countdown, playing, the pause and fail overlays, the results tile board, its detector-captioned leaderboard and its title colours, and the gameplay-settings overlay.

using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.VisualTree;
using DotCharter.Localization;
using DotCharter.Services.Audio.LivePlay;
using DotCharter.Services.Gameplay.Leaderboards;
using DotCharter.UI.ViewModels.PlayMode;
using DotCharter.UI.Views.PlayMode;

namespace DotCharter.Tests.Perf;

public sealed partial class PlayModeScreensUiPerfSmokeTests
{
    [Fact]
    public async Task CountdownAndPlayingScreens_layOut()
    {
        await WithView((view, vm) =>
        {
            vm.CurrentScreen = PlayModeScreen.Countdown;
            view.UpdateLayout();
            AssertLaidOut(view, "CountdownScreenRoot");
            // The beat pips and the fact chips are the two parts of the
            // rebuilt count-in that come from an ItemsControl / a binding
            // rather than from the chrome, so they are the two that can
            // silently render nothing.
            AssertLaidOut(view, "CountdownPipRow");
            AssertLaidOut(view, "CountdownFactChips");
            Assert.Equal(PlayModeViewModel.CountdownSteps,
                Find(view, "CountdownPipRow").GetVisualDescendants()
                    .OfType<Border>().Count(b => b.Bounds.Width > 1 && b.Height > 0));

            vm.CurrentScreen = PlayModeScreen.Playing;
            view.UpdateLayout();
            AssertLaidOut(view, "PlayingScreenRoot");
        });
    }

    /// <summary>The pause and fail surfaces float over a FROZEN highway frame
    /// (airspace), so they are ordinary Avalonia panels and do lay out
    /// headlessly. The pause tool grid is the part worth pinning: six tiles,
    /// each of which must be reachable, not merely drawn.</summary>
    [Fact]
    public async Task PauseAndFailOverlays_layOut()
    {
        await WithView((view, vm) =>
        {
            vm.CurrentScreen = PlayModeScreen.Paused;
            view.UpdateLayout();
            AssertLaidOut(view, "PauseScreenRoot");
            AssertLaidOut(view, "PauseToolGrid");
            var tools = Find(view, "PauseToolGrid").GetVisualDescendants().OfType<Button>().ToList();
            Assert.Equal(6, tools.Count);
            Assert.All(tools, t => Assert.True(t.Bounds.Width > 1 && t.Bounds.Height > 1,
                $"a pause tool tile laid out as {t.Bounds}"));

            // The run figures hide when there is no judged run behind the
            // pause — which is the state this harness pauses in, so the
            // header must lay out WITHOUT them rather than reserving a gap
            // or collapsing the title beside them.
            Assert.False(Find(view, "PauseRunFigures").IsVisible);
            Assert.False(Find(view, "PauseWhereRow").IsVisible);
            AssertLaidOut(view, "PauseEndRun");

            vm.CurrentScreen = PlayModeScreen.Failed;
            view.UpdateLayout();
            AssertLaidOut(view, "FailedScreenRoot");
        });
    }

    [Fact]
    public async Task ResultsScreen_laysOutItsTileBoard()
    {
        await WithView((view, vm) =>
        {
            vm.CurrentScreen = PlayModeScreen.Results;
            view.UpdateLayout();

            AssertLaidOut(view, "ResultsScreenRoot");
            var tiles = Find(view, "ResultsTileBoard");
            Assert.True(tiles.Bounds.Height > 50,
                $"the results tile board laid out as {tiles.Bounds}");
            Assert.Equal(4, tiles.GetVisualDescendants().OfType<Border>()
                .Count(b => b.Classes.Contains("PlayStatTile")));
            // Exactly one hero tile per screen (R4).
            Assert.Equal(1, tiles.GetVisualDescendants().OfType<Border>()
                .Count(b => b.Classes.Contains("Hero")));
            // The section panel keeps its slot with no sections behind it.
            AssertLaidOut(view, "ResultsSectionPanel");

            // Entering Results starts the staged reveal (a delay chain in the
            // code-behind). Leave the screen so CancelReveal runs — a live
            // reveal task outliving the window is not what this case is about.
            vm.CurrentScreen = PlayModeScreen.SongSelect;
            view.UpdateLayout();
        });
    }

    /// <summary>
    ///     The local leaderboard ranks ONE note detector's runs (#329), and the
    ///     caption under its title names which (#642). The board only shows
    ///     once a run has finished, so this case ends a real one: a CQT best on
    ///     the store, then the session-end handler the live session raises.
    /// </summary>
    [Fact]
    public async Task ResultsScreen_captionsItsLeaderboardWithTheRunsDetector()
    {
        await WithView((view, vm) =>
        {
            CompleteRun(view, vm, LiveDetectorMode.Cqt);

            AssertLaidOut(view, "ResultsLeaderboardPanel");
            AssertLaidOut(view, "ResultsLeaderboardCaption");
            Assert.Equal(LocalizationService.Instance["PlayMode_Results_Leaderboard_Cqt"],
                Find<TextBlock>(view, "ResultsLeaderboardCaption").Text);

            LeaveResults(view, vm);
        });
    }

    /// <summary>
    ///     The panel titles and the leaderboard's profile name are drawn in the
    ///     Fire title token (#688). They used to carry no foreground of their
    ///     own and so took the HOST WINDOW's Fluent one: white under
    ///     MainWindow's Dark variant, near-black under any other — which is how
    ///     the #642 capture showed them. Compared by value against the app's
    ///     <c>FireTextPrimary</c>, and no text on the screen may take its colour
    ///     from the window at all. The Countdown title shares the class.
    /// </summary>
    [Fact]
    public async Task ResultsScreen_titlesAndProfileNameUseTheFireTitleToken()
    {
        await WithView((view, vm) =>
        {
            CompleteRun(view, vm, LiveDetectorMode.Cqt);

            var token = FireTitleColor();
            var root = Find(view, "ResultsScreenRoot");
            var titles = root.GetVisualDescendants().OfType<TextBlock>()
                .Where(t => t.IsEffectivelyVisible
                            && (t.Classes.Contains("ScreenTitle") || t.Classes.Contains("SectionTitle")))
                .ToList();
            // "Results", "Section breakdown", "Local leaderboard".
            Assert.True(titles.Count >= 3, $"found {titles.Count} titles on the results screen");
            Assert.Contains(titles, t => t.Text == LocalizationService.Instance["PlayMode_Results_Leaderboard"]);
            Assert.All(titles, t => AssertForeground(token, t));

            var name = Find(view, "ResultsLeaderboardPanel").GetVisualDescendants().OfType<TextBlock>()
                .Single(t => t.DataContext is LeaderboardEntry e && t.Text == e.ProfileName);
            AssertForeground(token, name);

            foreach (var text in root.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible))
                AssertNotFromTheWindow(text);

            LeaveResults(view, vm);
            vm.CurrentScreen = PlayModeScreen.Countdown;
            view.UpdateLayout();
            var countdownTitle = Find(view, "CountdownScreenRoot").GetVisualDescendants().OfType<TextBlock>()
                .First(t => t.Classes.Contains("ScreenTitle"));
            AssertForeground(token, countdownTitle);
        });
    }

    private static Color FireTitleColor()
    {
        Assert.True(Application.Current!.TryGetResource("FireTextPrimary", ThemeVariant.Dark, out var resource),
            "the app has no FireTextPrimary resource");
        return Assert.IsAssignableFrom<ISolidColorBrush>(resource).Color;
    }

    private static void AssertForeground(Color expected, TextBlock text)
    {
        var brush = Assert.IsAssignableFrom<ISolidColorBrush>(text.Foreground);
        Assert.True(brush.Color == expected,
            $"'{text.Text}' is drawn in {brush.Color}, not the Fire title token {expected}");
    }

    /// <summary>Walks up to whoever SETS the inherited foreground; a top level
    /// means the text has no colour of its own and follows the theme variant.</summary>
    private static void AssertNotFromTheWindow(TextBlock text)
    {
        Visual? source = text;
        while (source != null && !source.IsSet(TextElement.ForegroundProperty))
            source = source.GetVisualParent();
        Assert.False(source is null or TopLevel,
            $"'{text.Text}' takes its colour from the window's theme variant, not a Fire token");
    }

    /// <summary>
    ///     Renders the results screen after a CQT run to
    ///     <c>TestResults/results-capture/results-&lt;culture&gt;.png</c> for the
    ///     #642 review. Runs only with <c>DOTCHARTER_UI_SWEEP_DUMP=1</c> (real
    ///     Skia drawing); <c>DOTCHARTER_HEADLESS_CULTURE</c> picks the language.
    ///     The capture is a review aid, never the assertion.
    /// </summary>
    [Fact]
    public async Task LocalOnly_ResultsScreen_capturesTheDetectorCaption()
    {
        Assert.SkipWhen(!UiSweepRunner.CaptureEnabled,
            $"set {UiSweepRunner.CaptureEnvVar}=1 — captures need real Skia drawing");

        await WithView((view, vm) =>
        {
            CompleteRun(view, vm, LiveDetectorMode.Cqt);

            var dir = Path.Combine(AppContext.BaseDirectory, "TestResults", "results-capture");
            Directory.CreateDirectory(dir);
            var size = new PixelSize((int)view.Bounds.Width, (int)view.Bounds.Height);
            using var bitmap = new RenderTargetBitmap(size);
            bitmap.Render(view);
            bitmap.Save(Path.Combine(dir, $"results-{HeadlessSessionFixture.SweptCulture}.png"), new PngBitmapEncoderOptions());

            LeaveResults(view, vm);
        });
    }

    /// <summary>Records a best with <paramref name="detector" /> and ends a run
    /// of the same song through the private session-end handler, which only
    /// answers a run this flow started — so the screen is Playing first.</summary>
    private void CompleteRun(PlayModeView view, PlayModeViewModel vm, LiveDetectorMode detector)
    {
        var store = _store!;
        var result = new LivePlaySessionResult(184_200, 0.92, 92, 8, 100, 41, 180, 100, 1.0,
            Detector: detector);
        store.Record("lib:ResultsSong", "Lead:Lead", result, displayName: "Artist - Results Song");

        vm.CurrentScreen = PlayModeScreen.Playing;
        var bests = new PracticeBests(0, 0, 0, 0);
        var report = new LivePlaySessionReport(result, bests, bests, WasRecorded: true,
            "lib:ResultsSong", "Lead:Lead", []);
        typeof(PlayModeViewModel).GetMethod("OnSessionCompleted", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(vm, [report]);
        Assert.Equal(PlayModeScreen.Results, vm.CurrentScreen);
        view.UpdateLayout();
    }

    /// <summary>Entering Results starts the staged reveal (a delay chain in the
    /// code-behind); leaving runs CancelReveal, so no reveal task outlives the window.</summary>
    private static void LeaveResults(PlayModeView view, PlayModeViewModel vm)
    {
        vm.CurrentScreen = PlayModeScreen.SongSelect;
        view.UpdateLayout();
    }

    /// <summary>The overlays are `IsXOpen` bools rather than screens, so
    /// nothing else in this file reaches them. Each is a scrimmed card that has
    /// to construct AND measure — the gameplay-settings one especially, since
    /// it binds through `$parent[Window]` to a shell view model that is absent
    /// here (an unresolvable path must degrade, never throw).</summary>
    [Fact]
    public async Task Overlays_layOut()
    {
        await WithView((view, vm) =>
        {
            // The online hub (v0.60) and the practice planner (2026-09-19) were
            // removed — the gameplay settings overlay is the one left here.
            vm.Settings.OpenGameplaySettingsCommand.Execute(null);
            view.UpdateLayout();
            AssertLaidOut(view, "GameplaySettingsOverlayCard");
            vm.Settings.CloseGameplaySettingsCommand.Execute(null);
        });
    }
}
