using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.VisualTree;
using DotCharter.Localization;
using DotCharter.Services.Audio.LivePlay;
using DotCharter.Services.Gameplay.Leaderboards;
using DotCharter.Services.Gameplay.Profiles;
using DotCharter.Services.Gameplay.Stats;
using DotCharter.Tone.Infrastructure.Storage;
using DotCharter.UI.ViewModels.PlayMode;
using DotCharter.UI.Views.PlayMode;
using DotCharter.UI.Views.PlayMode.Screens;

namespace DotCharter.Tests.Perf;

/// <summary>
///     Every Play Mode screen view lifted out of <c>PlayModeView.axaml</c> (#294)
///     constructs, attaches and lays out ON ITS OWN, over a real
///     <see cref="PlayModeViewModel" />.
///     <para>
///         A lifted view loads its XAML outside <c>PlayModeView</c>'s resource
///         scope, so the faults this catches are the ones the move can introduce
///         and the build cannot see: a <c>StaticResource</c> that only resolved
///         through the old host (a theme from <c>PlayModeThemes.axaml</c> the view
///         forgot to merge throws at construction), and a subtree whose styles
///         failed — it still ARRANGES, it just measures to nothing, which is why
///         each case asserts the screen root's measured size.
///     </para>
///     <para>
///         The window is given the grammar <c>PlayModeView</c> includes for its
///         screens (the selector rules reach the lifted views through the style
///         cascade in the app), the same thing the UI sweep's
///         <c>InheritedStyleIncludes</c> derives. The cases that drive each screen
///         INSIDE <c>PlayModeView</c> are <c>PlayModeScreensUiPerfSmokeTests</c>.
///     </para>
/// </summary>
[Collection("HeadlessUI")]
public sealed class PlayModeScreenViewsConstructionUiPerfSmokeTests : IDisposable
{
    private const double WindowWidth = 1600;
    private const double WindowHeight = 1000;

    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "DotCharterTests", Guid.NewGuid().ToString("N"));

    /// <summary>Each lifted view, with the <c>x:Name</c> of the root it must lay out (empty:
    /// the view's own content).</summary>
    private static readonly Dictionary<string, (Func<Control> Create, string Root)> Cases = new(StringComparer.Ordinal)
    {
        [nameof(LaunchScreenView)] = (() => new LaunchScreenView(), "StartScreenRoot"),
        [nameof(GetReadyView)] = (() => new GetReadyView(), "TuningCheckScreenRoot"),
        [nameof(GetReadyCentreView)] = (() => new GetReadyCentreView(), "GetReadyCentre"),
        [nameof(ResultsView)] = (() => new ResultsView(), "ResultsScreenRoot"),
        [nameof(SongSelectView)] = (() => new SongSelectView(), "SongSelectScreenRoot"),
        [nameof(SongSelectDetailRailView)] = (() => new SongSelectDetailRailView(), "SongSelectDetailRail"),
        // The setlist panel (#646) binds PlaySetlistViewModel: hosted as the rail
        // hosts it, its DataContext bound to Play Mode's Setlists.
        [nameof(SetlistPanelView)] = (() => new Border
        {
            Child = new SetlistPanelView
            {
                [!StyledElement.DataContextProperty] = new Avalonia.Data.Binding(nameof(PlayModeViewModel.Setlists))
            }
        }, "SongSelectSetlistPanel"),
        [nameof(CountdownView)] = (() => new CountdownView(), "CountdownScreenRoot"),
        [nameof(PlayingView)] = (() => new PlayingView(), "PlayingScreenRoot"),
        [nameof(PauseMenuView)] = (() => new PauseMenuView(), "PauseScreenRoot"),
        [nameof(FailedView)] = (() => new FailedView(), "FailedScreenRoot"),
        [nameof(GameplaySettingsView)] = (() => new GameplaySettingsView(), "GameplaySettingsOverlayCard"),
        // The six section panes: each root is the pane's ScrollViewer (its host
        // carries the pane's name and gate), so the case asserts the first child.
        [nameof(GameSettingsAudioView)] = (() => new GameSettingsAudioView(), ""),
        [nameof(GameSettingsDevicesView)] = (() => new GameSettingsDevicesView(), ""),
        [nameof(GameSettingsGameplayView)] = (() => new GameSettingsGameplayView(), ""),
        [nameof(GameSettingsGraphicsView)] = (() => new GameSettingsGraphicsView(), ""),
        [nameof(GameSettingsAppearanceView)] = (() => new GameSettingsAppearanceView(), ""),
        [nameof(GameSettingsKeybindsView)] = (() => new GameSettingsKeybindsView(), "")
    };

    public static TheoryData<string> ViewNames() => [.. Cases.Keys];

    /// <summary>A view lifted without a case here would ship untested: the list is
    /// checked against the namespace, both ways.</summary>
    [Fact]
    public void EveryScreenView_HasACase()
    {
        var views = typeof(PlayModeView).Assembly.GetTypes()
            .Where(t => t.Namespace == typeof(GetReadyView).Namespace && typeof(Control).IsAssignableFrom(t))
            .Select(t => t.Name)
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.Equal(views, Cases.Keys.Order(StringComparer.Ordinal).ToList());
    }

    [Theory]
    [MemberData(nameof(ViewNames))]
    public async Task TheView_Constructs_Attaches_AndLaysOut(string viewName)
    {
        var (create, rootName) = Cases[viewName];
        await HeadlessSessionFixture.Dispatch(() =>
        {
            Directory.CreateDirectory(_dir);
            var profiles = new PlayerProfileService(FakeLocalization.English(), logger: null,
                filePath: Path.Combine(_dir, "player-profiles.json"), defaultProfileName: "Tester");
            var store = new PracticeProfileService(profiles, logger: null,
                legacyFilePath: Path.Combine(_dir, "practice-profiles.json"));
            var state = new ProjectState();
            var vm = TestRigBuilders.PlayMode(profiles, new GameplayStatsService(store),
                new LeaderboardService(profiles), state, toneRig: HeadlessToneRig.FromSession(state), localization: LocalizationService.Instance);

            var view = create();
            view.DataContext = vm;
            var window = new Window { Width = WindowWidth, Height = WindowHeight, Content = view };
            foreach (var grammar in PlayModeViewGrammar)
                window.Styles.Add(new StyleInclude(new Uri("avares://DotCharter/")) { Source = new Uri(grammar) });
            window.Show();
            try
            {
                view.Measure(new Size(WindowWidth, WindowHeight));
                view.Arrange(new Rect(0, 0, WindowWidth, WindowHeight));

                var root = rootName.Length == 0
                    ? (view as ContentControl)?.Content as Control
                    : view.GetVisualDescendants().OfType<Control>().FirstOrDefault(c => c.Name == rootName);
                Assert.True(root != null, $"{viewName} realized no '{rootName}' — the view built nothing");
                Assert.True(root!.Bounds is { Width: > 1, Height: > 1 },
                    $"{viewName}: '{rootName}' laid out as {root.Bounds} — a subtree whose styles failed " +
                    "still arranges, it just has no size");
                // A floor, not a census: the detail rail with no song selected is
                // its prompt alone (ten visuals).
                Assert.True(view.GetVisualDescendants().Count() > 5,
                    $"{viewName} realized only {view.GetVisualDescendants().Count()} visuals");
            }
            finally
            {
                window.Close();
            }
        }, TestContext.Current.CancellationToken);
    }

    /// <summary>What <c>PlayModeView.axaml</c>'s own <c>UserControl.Styles</c> includes.</summary>
    private static readonly string[] PlayModeViewGrammar =
    [
        "avares://DotCharter/Styles/SongListHeaderStyles.axaml",
        "avares://DotCharter/Views/PlayMode/PlayModeStyles.axaml"
    ];

    public void Dispose() => BestEffortDelete.Directory(_dir);
}
