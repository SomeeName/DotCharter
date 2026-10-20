using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DotCharter.Presentation.Contracts.PlayMode;
using DotCharter.Presentation.Fakes.PlayMode;
using DotCharter.UI.Views.PlayMode.Redesign;

namespace DotCharter.Tests.Perf;

/// <summary>
///     The seven redesigned Play Mode FLOW screens — Start, Get Ready, Countdown, Playing, Paused, Failed and Results — built for
///     real against every fake variant, attached to a 1600 × 900 window and laid
///     out.
///     <para>
///         A view whose styles failed still ARRANGES — it just has no size — so
///         every assertion here is "this named part measured non-zero", which is
///         the shape every silent XAML fault in this repo takes: an unresolvable
///         <c>StaticResource</c>, a <c>Padding="{DynamicResource SpaceMd}"</c>
///         (<c>Space*</c> is an <c>x:Double</c> and <c>Padding</c> a
///         <c>Thickness</c>, so the cast throws on attach), a Fire class that
///         matches no selector, or a view that fails to construct and renders as
///         a "Not Found: …" placeholder.
///     </para>
///     <para>
///         <see cref="EveryStressVariant_KeepsItsTextInsideItsContainers" /> is
///         the one that exists because of a REPORTED bug rather than a class of
///         them: a horizontal panel arranges its children at their own desired
///         size and clips nothing, so it reports a correct rectangle and paints
///         into the next Grid cell. An empty fixture cannot show that, which is
///         why every fake here carries a stress variant with German and Japanese
///         strings in it.
///     </para>
///     <para>
///         With <c>DOTCHARTER_UI_SWEEP_DUMP=1</c> and
///         <c>DOTCHARTER_RIG_CAPTURE_DIR</c> set, each case is also written to
///         <c>playmode-redesign-&lt;screen&gt;-&lt;variant&gt;.png</c>, the same
///         way <see cref="SongSelectRedesignUiPerfSmokeTests" /> captures the
///         Song Select redesign.
///     </para>
/// </summary>
[Collection("HeadlessUI")]
public sealed class PlayModeFlowRedesignUiPerfSmokeTests
{
    private const int WindowWidth = 1600;
    private const int WindowHeight = 900;

    /// <summary>
    ///     Every screen, its variants, the parts that must measure on ALL of
    ///     them, and the named controls that must carry a bound command.
    /// </summary>
    private static readonly Dictionary<string, ScreenSpec> Screens = new(StringComparer.Ordinal)
    {
        ["start"] = new(["Ready", "FirstRun", "LongText", "Full"], "LongText",
            ["StartRedesignRoot", "StartRedesignLockup", "StartRedesignStatusFacts",
                "StartRedesignProfileRail", "StartRedesignStartButton", "StartRedesignFooter"],
            ["StartRedesignStartButton", "StartRedesignAddProfile", "StartRedesignGuestToggle"]),

        ["tuningcheck"] = new(["Ready", "NeedsTuning", "NoInput", "LongText", "Empty", "Full", "InSetlist", "SetlistSkipped"], "LongText",
            ["TuningCheckRedesignRoot", "TuningCheckRedesignHeader", "TuningCheckRedesignPathRail",
                "TuningCheckRedesignSettingsRail", "TuningCheckRedesignInputPanel",
                "TuningCheckRedesignFooter", "TuningCheckRedesignStart"],
            ["TuningCheckRedesignStart", "TuningCheckRedesignOpenRig"]),

        ["countdown"] = new(["Three", "Go", "LongText", "Empty", "Full"], "LongText",
            ["CountdownRedesignRoot", "CountdownRedesignCard", "CountdownRedesignFigure",
                "CountdownRedesignPips", "CountdownRedesignLockup", "CountdownRedesignCancel"],
            ["CountdownRedesignCancel"]),

        ["playing"] = new(["Solo", "Struggling", "Extreme", "Empty"], "Extreme",
            ["PlayingRedesignRoot", "PlayingRedesignScoreBar", "PlayingRedesignHighwayPlaceholder",
                "PlayingRedesignEnergyPanel", "PlayingRedesignSongProgress", "PlayingRedesignFooter",
                "PlayingRedesignPauseButton"],
            ["PlayingRedesignPauseButton"]),

        ["paused"] = new(["MidRun", "NotStarted", "LongText", "Full", "InSetlist"], "LongText",
            ["PausedRedesignRoot", "PausedRedesignCard", "PausedRedesignRunPanel",
                "PausedRedesignToolGrid", "PausedRedesignLeaveRail", "PausedRedesignResume",
                "PausedRedesignRestart"],
            ["PausedRedesignResume", "PausedRedesignRestart"]),

        ["failed"] = new(["WithSection", "NoSection", "LongText"], "LongText",
            ["FailedRedesignRoot", "FailedRedesignCard", "FailedRedesignProgressPanel",
                "FailedRedesignRestart", "FailedRedesignQuit"],
            ["FailedRedesignRestart", "FailedRedesignQuit"]),

        ["results"] = new(["Strong", "Weak", "FirstRun", "LongText", "Full", "SetlistNext", "SetlistDone",
                "TechniqueDowngrades"], "LongText",
            ["ResultsRedesignRoot", "ResultsRedesignHero", "ResultsRedesignGradeBadge",
                "ResultsRedesignStatsGrid", "ResultsRedesignBreakdown", "ResultsRedesignTimingPanel",
                "ResultsRedesignLeaderboardPanel", "ResultsRedesignRetry"],
            ["ResultsRedesignRetry", "ResultsRedesignSongSelect"])
    };

    /// <summary>
    ///     Parts a variant has nothing to put in: a count-in with no beats draws no pips,
    ///     and while a setlist has a next song "Next" takes Retry's slot (#646).
    ///     Everything else must still measure.
    /// </summary>
    private static readonly HashSet<(string Screen, string Variant, string Part)> EmptyByDesign =
    [
        ("countdown", "Empty", "CountdownRedesignPips"),
        ("results", "LongText", "ResultsRedesignRetry"),
        ("results", "SetlistNext", "ResultsRedesignRetry")
    ];

    public static IEnumerable<object[]> Cases() =>
        Screens.SelectMany(s => s.Value.Variants.Select(v => new object[] { s.Key, v }));

    public static IEnumerable<object[]> StressCases() =>
        Screens.Select(s => new object[] { s.Key, s.Value.StressVariant });

    // ── the whole batch ───────────────────────────────────────────────────

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task EveryScreenAndVariant_BuildsAttachesMeasures_AndIsCaptured(
        string screen, string variant)
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            WithScreen(screen, variant, (view, window) =>
            {
                Assert.True(view.Bounds is { Width: > 1000, Height: > 500 },
                    $"{screen}/{variant}: the screen laid out as {view.Bounds} — it rendered nothing");

                var spec = Screens[screen];
                foreach (var part in spec.Parts.Where(p => !EmptyByDesign.Contains((screen, variant, p))))
                    AssertMeasured(view, part, screen, variant);

                foreach (var name in spec.CommandParts)
                {
                    var button = Assert.IsAssignableFrom<Button>(Named(view, name));
                    Assert.True(button.Command != null,
                        $"{screen}/{variant}: {name} has no bound command — it is a button that "
                        + "cannot be pressed, and that compiles perfectly cleanly");
                }

                Capture(window, screen, variant);
            });
        }, TestContext.Current.CancellationToken);
    }

    /// <summary>
    ///     No text inside a row, card or panel draws outside the box that
    ///     contains it.
    ///     <para>
    ///         Bounds are relative to the parent, so the test is
    ///         <c>child.Bounds.Right &lt;= parent.Bounds.Width</c>. A horizontal
    ///         panel that overflows still reports a correct rectangle for itself,
    ///         which is exactly why a bounds assertion on the CARD passes while
    ///         a German label paints over the figure beside it.
    ///     </para>
    /// </summary>
    [Theory]
    [MemberData(nameof(StressCases))]
    public async Task EveryStressVariant_KeepsItsTextInsideItsContainers(string screen, string variant)
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            WithScreen(screen, variant, (view, _) =>
            {
                var containers = view.GetVisualDescendants().OfType<Border>()
                    .Where(b => ContainerClasses.Any(c => b.Classes.Contains(c)))
                    .ToList();

                Assert.True(containers.Count > 0,
                    $"{screen}/{variant}: no row, card or panel realized — this case proves nothing");

                var overflows = new List<string>();
                foreach (var container in containers)
                {
                    foreach (var text in container.GetVisualDescendants().OfType<TextBlock>())
                    {
                        if (text.GetVisualParent() is not Visual parent) continue;
                        if (text.Bounds.Width <= 0) continue;
                        if (text.Bounds.Right > parent.Bounds.Width + 1)
                        {
                            overflows.Add(
                            $"'{Trim(text.Text)}' arranged to x={text.Bounds.Right:0.#} in a "
                            + $"{parent.GetType().Name} {parent.Bounds.Width:0.#} px wide");
                        }
                    }
                }

                Assert.True(overflows.Count == 0,
                    $"{screen}/{variant}: text drew outside its container (the clipped-text / "
                    + "cell-overlap class):" + Environment.NewLine
                    + string.Join(Environment.NewLine, overflows.Distinct().Take(12)));
            });
        }, TestContext.Current.CancellationToken);
    }

    // ── per-screen facts ──────────────────────────────────────────────────

    [Fact]
    public async Task Start_SwapsTheProfileListForTheGuestPanel_OnFirstRun()
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            WithScreen("start", "Ready", (view, _) =>
            {
                Assert.True(Named(view, "StartRedesignProfileScroll").IsVisible);
                Assert.False(Named(view, "StartRedesignGuestPanel").IsVisible);
                Assert.True(Named(view, "StartRedesignStartButton").IsEffectivelyEnabled,
                    "START is dead with a profile picked");
            });

            WithScreen("start", "FirstRun", (view, _) =>
            {
                Assert.True(Named(view, "StartRedesignGuestPanel").IsVisible);
                Assert.True(Named(view, "StartRedesignNoProfilesPanel").IsVisible);
                Assert.False(Named(view, "StartRedesignStartButton").IsEffectivelyEnabled,
                    "START is live with no profile at all");
            });
        }, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task TuningCheck_ShowsTheStringRows_OnlyWhenThereIsSomethingToCheck()
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            WithScreen("tuningcheck", "NeedsTuning", (view, _) =>
            {
                Assert.True(Named(view, "TuningCheckRedesignTunerPanel").IsVisible);
                Assert.False(Named(view, "TuningCheckRedesignNoCheckPanel").IsVisible);
                Assert.Equal(6, RealizedRows(view).Count);
            });

            WithScreen("tuningcheck", "NoInput", (view, _) =>
            {
                var empty = Named(view, "TuningCheckRedesignNoCheckPanel");
                Assert.True(empty.IsVisible, "the tuner reads as live with no input open");
                Assert.True(empty.Bounds is { Width: > 100, Height: > 30 },
                    $"the no-check panel laid out as {empty.Bounds}");
                Assert.False(Named(view, "TuningCheckRedesignTunerPanel").IsVisible);
                Assert.Empty(RealizedRows(view));
            });

            WithScreen("tuningcheck", "Empty", (view, _) =>
            {
                Assert.True(Named(view, "TuningCheckRedesignNoCheckPanel").IsVisible, "the tuner reads as live with no path to check");
                Assert.False(Named(view, "TuningCheckRedesignStart").IsEffectivelyEnabled, "Start is live with no path picked");
            });

            WithScreen("tuningcheck", "Full", (view, _) =>
            {
                Assert.True(Named(view, "TuningCheckRedesignTunerPanel").IsVisible);
                Assert.Equal(7, RealizedRows(view).Count);
            });
        }, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Countdown_DrawsOnePipPerBeat_AndTheLockupUnderTheFigure()
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            WithScreen("countdown", "Three", (view, window) =>
            {
                var pips = Named(view, "CountdownRedesignPips");
                var dots = pips.GetVisualDescendants().OfType<Border>()
                    .Count(b => b.Classes.Contains("FireDot") && b.IsVisible);
                Assert.Equal(4, dots);

                var figure = Named(view, "CountdownRedesignFigure");
                var lockup = Named(view, "CountdownRedesignLockup");
                Assert.True(Top(figure, window) + figure.Bounds.Height <= Top(lockup, window) + 0.5,
                    "the count-in figure is painted over the song lockup");
            });

            WithScreen("countdown", "Full", (view, _) =>
                Assert.Equal(8, Named(view, "CountdownRedesignPips").GetVisualDescendants().OfType<Border>()
                    .Count(b => b.Classes.Contains("FireDot") && b.IsVisible)));
        }, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Playing_NeverDrawsTheHudOverTheHighway()
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            WithScreen("playing", "Solo", (view, window) =>
            {
                Assert.True(Named(view, "PlayingRedesignSectionBlock").IsVisible);

                // The middle band is the one region the player looks at: the HUD
                // chrome sits above and below it, never over it.
                var highway = Named(view, "PlayingRedesignHighwayPlaceholder");
                var bar = Named(view, "PlayingRedesignScoreBar");
                var footer = Named(view, "PlayingRedesignFooter");
                Assert.True(Top(bar, window) + bar.Bounds.Height <= Top(highway, window) + 0.5,
                    "the score bar is painted over the highway");
                Assert.True(Top(highway, window) + highway.Bounds.Height <= Top(footer, window) + 0.5,
                    "the footer strip is painted over the highway");
            });

            WithScreen("playing", "Struggling", (view, _) =>
                Assert.False(Named(view, "PlayingRedesignSectionBlock").IsVisible,
                    "a chart with no sections still draws an empty section bar"));
        }, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Paused_HidesTheFiguresBeforeTheFirstJudgedNote_AndDrawsEveryTool()
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            WithScreen("paused", "MidRun", (view, _) =>
            {
                Assert.True(Named(view, "PausedRedesignFigures").IsVisible);
                Assert.False(Named(view, "PausedRedesignNoRunNote").IsVisible);
                Assert.True(Named(view, "PausedRedesignSectionRow").IsVisible);
                var tiles = ToolTiles(view);
                Assert.Equal(((DotCharter.Presentation.Contracts.PlayMode.IPausedScreen)view.DataContext!).Tools.Count, tiles.Count);
                // Two columns, and the tiles of a row share one height.
                foreach (var row in tiles.GroupBy(t => Math.Round(t.Bounds.Y)))
                    Assert.Single(row.Select(t => Math.Round(t.Bounds.Height)).Distinct());
            });

            WithScreen("paused", "NotStarted", (view, _) =>
            {
                Assert.False(Named(view, "PausedRedesignFigures").IsVisible,
                    "four zeroes are printed before the first judged note");
                var note = Named(view, "PausedRedesignNoRunNote");
                Assert.True(note.IsVisible);
                Assert.True(note.Bounds is { Width: > 100, Height: > 5 },
                    $"the no-run note laid out as {note.Bounds}");
                Assert.False(Named(view, "PausedRedesignSectionRow").IsVisible);
            });
        }, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Failed_DropsTheRetryFromSectionVerb_OnAChartWithoutSections()
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            WithScreen("failed", "WithSection", (view, _) =>
            {
                Assert.True(Named(view, "FailedRedesignRetrySection").IsVisible);
                Assert.True(Named(view, "FailedRedesignSectionRow").IsVisible);
            });

            WithScreen("failed", "NoSection", (view, _) =>
            {
                Assert.False(Named(view, "FailedRedesignRetrySection").IsVisible,
                    "a verb that cannot run is still offered");
                Assert.False(Named(view, "FailedRedesignSectionRow").IsVisible);
                Assert.True(Named(view, "FailedRedesignRestart").IsVisible);
                // …and with Retry gone it is the lit action, so a player who
                // just failed still has one obvious thing to press.
                Assert.True(Named(view, "FailedRedesignRestart").Classes.Contains("FireCta"),
                    "without a section to retry, the failed screen offers no primary action");
            });

            WithScreen("failed", "WithSection", (view, _) =>
                Assert.False(Named(view, "FailedRedesignRestart").Classes.Contains("FireCta"),
                    "Restart competes with Retry as a second lit action"));
        }, TestContext.Current.CancellationToken);
    }

    /// <summary>
    ///     The technique line (#645) draws, with its words, on a run that hit notes without their
    ///     technique — the long German one included — and is not on screen on a clean run.
    /// </summary>
    [Theory]
    [InlineData("TechniqueDowngrades", true)]
    [InlineData("LongText", true)]
    [InlineData("Strong", false)]
    public async Task Results_TechniqueLine_DrawsOnlyWhenTechniquesWereMissed(string variant, bool shown)
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            WithScreen("results", variant, (view, _) =>
            {
                var line = (TextBlock)Named(view, "ResultsRedesignTechniqueLine");
                if (!shown)
                {
                    Assert.False(line.IsEffectivelyVisible, $"results/{variant}: the technique line is on screen");
                    return;
                }

                AssertMeasured(view, "ResultsRedesignTechniqueLine", "results", variant);
                Assert.Equal(((IResultsScreen)view.DataContext!).TechniqueLabel, line.Text);
            });
        }, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Results_KeepsAllThreePanels_WhenEveryOneOfThemIsEmpty()
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            WithScreen("results", "Strong", (view, _) =>
            {
                Assert.False(Named(view, "ResultsRedesignNoSectionsPanel").IsVisible);
                Assert.False(Named(view, "ResultsRedesignNoTimingPanel").IsVisible);
                Assert.False(Named(view, "ResultsRedesignNoLeaderboardPanel").IsVisible);
                Assert.True(Named(view, "ResultsRedesignHistogram").Bounds.Width > 40,
                    "the timing histogram measured nothing");
                Assert.True(RealizedRows(view).Count >= 9, "the section rows never realized");
            });

            WithScreen("results", "FirstRun", (view, _) =>
            {
                foreach (var name in new[]
                         {
                             "ResultsRedesignNoSectionsPanel", "ResultsRedesignNoTimingPanel",
                             "ResultsRedesignNoLeaderboardPanel"
                         })
                {
                    var panel = Named(view, name);
                    Assert.True(panel.IsVisible, $"{name} is hidden instead of saying why it is empty");
                    Assert.True(panel.Bounds is { Width: > 100, Height: > 10 },
                        $"{name} laid out as {panel.Bounds}");
                }

                foreach (var name in new[]
                         {
                             "ResultsRedesignBreakdown", "ResultsRedesignTimingPanel",
                             "ResultsRedesignLeaderboardPanel"
                         })
                {
                    var panel = Named(view, name);
                    Assert.True(panel.Bounds is { Width: > 150, Height: > 60 },
                        $"{name} laid out as {panel.Bounds} — the panel gave up its slot");
                }

                Assert.False(Named(view, "ResultsRedesignPractice").IsEffectivelyEnabled,
                    "Practice is live with no weak section to practise");
            });
        }, TestContext.Current.CancellationToken);
    }

    /// <summary>
    ///     A running setlist (#646) draws its parts, inside the window, and replaces what it
    ///     says it replaces: Next takes Retry's place on Results, Skip / End take Song
    ///     select's on Pause.
    /// </summary>
    [Theory]
    [InlineData("results", "SetlistNext", "ResultsRedesignSetlistNext,ResultsRedesignSetlistProgress,ResultsRedesignSetlistEnd", "ResultsRedesignRetry")]
    [InlineData("results", "SetlistDone", "ResultsRedesignSetlistDone,ResultsRedesignRetry", "ResultsRedesignSetlistNext")]
    [InlineData("results", "LongText", "ResultsRedesignSetlistNext,ResultsRedesignSetlistProgress", "ResultsRedesignRetry")]
    [InlineData("paused", "InSetlist", "PausedRedesignSetlistProgress,PausedRedesignSetlistRow,PausedRedesignSetlistSkip,PausedRedesignSetlistEnd", "")]
    [InlineData("paused", "LongText", "PausedRedesignSetlistProgress,PausedRedesignSetlistRow,PausedRedesignSetlistSkip,PausedRedesignSetlistEnd", "")]
    [InlineData("paused", "MidRun", "", "PausedRedesignSetlistRow")]
    [InlineData("tuningcheck", "SetlistSkipped", "TuningCheckRedesignSetlistProgress,TuningCheckRedesignSetlistSkipped", "")]
    [InlineData("tuningcheck", "Ready", "", "TuningCheckRedesignSetlistProgress")]
    public async Task ARunningSetlist_DrawsItsParts_InsideTheWindow(string screen, string variant, string shown, string hidden)
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            WithScreen(screen, variant, (view, window) =>
            {
                foreach (var name in shown.Split(',', StringSplitOptions.RemoveEmptyEntries))
                {
                    AssertMeasured(view, name, screen, variant);
                    var part = Named(view, name);
                    var at = part.TranslatePoint(default, window)!.Value;
                    Assert.True(at.Y >= 0 && at.Y + part.Bounds.Height <= window.Bounds.Height + 0.5,
                        $"{screen}/{variant}: {name} spans y={at.Y:0.#}..{at.Y + part.Bounds.Height:0.#}, outside the {window.Bounds.Height:0.#} px window");
                }

                foreach (var name in hidden.Split(',', StringSplitOptions.RemoveEmptyEntries))
                    Assert.False(Named(view, name).IsEffectivelyVisible, $"{screen}/{variant}: {name} is on screen");
            });
        }, TestContext.Current.CancellationToken);
    }

    // ── plumbing ──────────────────────────────────────────────────────────

    private static readonly string[] ContainerClasses =
        ["FireListRow", "FirePanel", "FireCard", "FireHeroPanel", "FireStatusPill", "FireTag"];

    private sealed record ScreenSpec(
        string[] Variants,
        string StressVariant,
        string[] Parts,
        string[] CommandParts);

    private static (UserControl View, object Fake) Build(string screen, string variant) =>
        (screen, variant) switch
        {
            ("start", "Ready") => (new StartScreenView(), FakeStartScreen.Ready()),
            ("start", "FirstRun") => (new StartScreenView(), FakeStartScreen.FirstRun()),
            ("start", "LongText") => (new StartScreenView(), FakeStartScreen.LongText()),
            ("start", "Full") => (new StartScreenView(), FakeStartScreen.Full()),

            ("tuningcheck", "Ready") => (new TuningCheckScreenView(), FakeTuningCheckScreen.Ready()),
            ("tuningcheck", "NeedsTuning") => (new TuningCheckScreenView(), FakeTuningCheckScreen.NeedsTuning()),
            ("tuningcheck", "NoInput") => (new TuningCheckScreenView(), FakeTuningCheckScreen.NoInput()),
            ("tuningcheck", "LongText") => (new TuningCheckScreenView(), FakeTuningCheckScreen.LongText()),
            ("tuningcheck", "Empty") => (new TuningCheckScreenView(), FakeTuningCheckScreen.Empty()),
            ("tuningcheck", "Full") => (new TuningCheckScreenView(), FakeTuningCheckScreen.Full()),
            ("tuningcheck", "InSetlist") => (new TuningCheckScreenView(), FakeTuningCheckScreen.InSetlist()),
            ("tuningcheck", "SetlistSkipped") => (new TuningCheckScreenView(), FakeTuningCheckScreen.SetlistSkipped()),

            ("countdown", "Three") => (new CountdownScreenView(), FakeCountdownScreen.ThreeToGo()),
            ("countdown", "Go") => (new CountdownScreenView(), FakeCountdownScreen.Go()),
            ("countdown", "LongText") => (new CountdownScreenView(), FakeCountdownScreen.LongText()),
            ("countdown", "Empty") => (new CountdownScreenView(), FakeCountdownScreen.Empty()),
            ("countdown", "Full") => (new CountdownScreenView(), FakeCountdownScreen.Full()),

            ("playing", "Solo") => (new PlayingScreenView(), FakePlayingScreen.Solo()),
            ("playing", "Struggling") => (new PlayingScreenView(), FakePlayingScreen.Struggling()),
            ("playing", "Extreme") => (new PlayingScreenView(), FakePlayingScreen.Extreme()),
            ("playing", "Empty") => (new PlayingScreenView(), FakePlayingScreen.Empty()),

            ("paused", "MidRun") => (new PausedScreenView(), FakePausedScreen.MidRun()),
            ("paused", "NotStarted") => (new PausedScreenView(), FakePausedScreen.NotStarted()),
            ("paused", "LongText") => (new PausedScreenView(), FakePausedScreen.LongText()),
            ("paused", "Full") => (new PausedScreenView(), FakePausedScreen.Full()),
            ("paused", "InSetlist") => (new PausedScreenView(), FakePausedScreen.InSetlist()),

            ("failed", "WithSection") => (new FailedScreenView(), FakeFailedScreen.WithSection()),
            ("failed", "NoSection") => (new FailedScreenView(), FakeFailedScreen.NoSection()),
            ("failed", "LongText") => (new FailedScreenView(), FakeFailedScreen.LongText()),

            ("results", "Strong") => (new ResultsScreenView(), FakeResultsScreen.Strong()),
            ("results", "Weak") => (new ResultsScreenView(), FakeResultsScreen.Weak()),
            ("results", "FirstRun") => (new ResultsScreenView(), FakeResultsScreen.FirstRun()),
            ("results", "LongText") => (new ResultsScreenView(), FakeResultsScreen.LongText()),
            ("results", "Full") => (new ResultsScreenView(), FakeResultsScreen.Full()),
            ("results", "SetlistNext") => (new ResultsScreenView(), FakeResultsScreen.SetlistNext()),
            ("results", "SetlistDone") => (new ResultsScreenView(), FakeResultsScreen.SetlistDone()),
            ("results", "TechniqueDowngrades") => (new ResultsScreenView(), FakeResultsScreen.TechniqueDowngrades()),

            _ => throw new ArgumentOutOfRangeException(nameof(variant),
                $"{screen}/{variant}", "unknown screen or fake variant")
        };

    private static void WithScreen(string screen, string variant, Action<UserControl, Window> body)
    {
        var (view, fake) = Build(screen, variant);
        view.DataContext = fake;
        var window = new Window { Width = WindowWidth, Height = WindowHeight, Content = view };

        // Show() roots the tree and IS the initial layout pass; the Background
        // drain is what realizes deferred template bodies.
        window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs(DispatcherPriority.Background);
            for (var pass = 0; pass < 3; pass++) window.UpdateLayout();
            body(view, window);
        }
        finally
        {
            window.Close();
        }
    }

    private static double Top(Visual control, Window window) =>
        control.TranslatePoint(default, window)?.Y
        ?? throw new Xunit.Sdk.XunitException($"{control} is not connected to the window");

    private static Control Named(UserControl view, string name) =>
        view.GetVisualDescendants().OfType<Control>().FirstOrDefault(c => c.Name == name)
        ?? throw new Xunit.Sdk.XunitException($"the view has no part named '{name}'");

    private static void AssertMeasured(UserControl view, string name, string screen, string variant)
    {
        var part = Named(view, name);
        Assert.True(part.Bounds is { Width: > 1, Height: > 1 },
            $"{screen}/{variant}: {name} laid out as {part.Bounds} — it rendered nothing");
    }

    /// <summary>
    ///     Every realized row, whatever element wears the class: a row that
    ///     represents an ACTION is a <c>Button.FireListRow</c> and a row that is
    ///     information is a <c>Border.FireListRow</c>, and a helper that knows
    ///     only about one of them counts zero on half these screens.
    /// </summary>
    private static List<Control> RealizedRows(UserControl view) =>
        view.GetVisualDescendants().OfType<Control>()
            .Where(c => (c is Border or Button) && c.Classes.Contains("FireListRow"))
            .ToList();

    private static List<Button> ToolTiles(UserControl view) =>
        view.GetVisualDescendants().OfType<Button>()
            .Where(b => b.Classes.Contains("FirePresetCard"))
            .ToList();

    private static string Trim(string? text) =>
        text is { Length: > 40 } ? text[..40] + "…" : text ?? "";

    private static void Capture(Window window, string screen, string variant)
    {
        var dir = Environment.GetEnvironmentVariable("DOTCHARTER_RIG_CAPTURE_DIR");
        if (!UiSweepRunner.CaptureEnabled || string.IsNullOrWhiteSpace(dir)) return;

        Directory.CreateDirectory(dir);
        using var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        frame.Save(Path.Combine(dir, $"playmode-redesign-{screen}-{variant.ToLowerInvariant()}.png"),
            new PngBitmapEncoderOptions());
    }
}
