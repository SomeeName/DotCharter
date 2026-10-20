using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.VisualTree;
using DotCharter.Localization;
using DotCharter.Services.Gameplay.Leaderboards;
using DotCharter.Services.Gameplay.Profiles;
using DotCharter.Services.Gameplay.Stats;
using DotCharter.Services.Audio.LivePlay;
using DotCharter.Tone.Infrastructure.Storage;
using DotCharter.UI.ViewModels.PlayMode;
using DotCharter.UI.Views.PlayMode.Screens;

namespace DotCharter.Tests.Perf;

/// <summary>
///     The two views #645 touched, constructed, attached and laid out over a real
///     <see cref="PlayModeViewModel" />: the shipped results screen draws its technique line
///     only when a run hit notes without their technique, and the Play Mode settings' Gameplay
///     pane draws the "Judge bends, slides &amp; vibrato" row (a row whose styles failed still
///     arranges — it just has no size, which is what is asserted).
/// </summary>
[Collection("HeadlessUI")]
public sealed class TechniqueContourViewsUiPerfSmokeTests : IDisposable
{
    private const double WindowWidth = 1600;
    private const double WindowHeight = 1000;

    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "DotCharterTests", Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Results_TechniqueLine_DrawsOnlyWhenTechniquesWereMissed(bool missed)
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var vm = ViewModel();
            vm.RunResults.ResultTechniqueLine = missed
                ? ResultsTechniqueLine.For([new TechniqueResult(PlayedTechnique.Bend, 9, 3, 0)], LocalizationService.Instance)
                : "";
            WithView(new ResultsView(), vm, $"results-technique-{(missed ? "missed" : "clean")}", view =>
            {
                var line = Named(view, "ResultsTechniqueLine");
                if (!missed)
                {
                    Assert.False(line.IsVisible, "the technique line shows on a clean run");
                    return;
                }

                Assert.True(line.Bounds is { Width: > 1, Height: > 1 },
                    $"ResultsTechniqueLine laid out as {line.Bounds} — it rendered nothing");
                Assert.Equal(vm.RunResults.ResultTechniqueLine, ((TextBlock)line).Text);
            });
        }, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task GameplaySettings_PerTechniqueRows_LayOut()
    {
        // #730: one switch per technique (bends, slides, vibrato).
        string[] rows = ["GameSettingsJudgeBendsRow", "GameSettingsJudgeSlidesRow", "GameSettingsJudgeVibratoRow"];
        await HeadlessSessionFixture.Dispatch(() =>
        {
            WithView(new GameSettingsGameplayView(), ViewModel(), "settings-gameplay", view =>
            {
                foreach (var name in rows)
                {
                    var row = Named(view, name);
                    Assert.True(row.Bounds is { Width: > 1, Height: > 1 }, $"{name} laid out as {row.Bounds} — it rendered nothing");
                    var toggle = row.GetVisualDescendants().OfType<ToggleSwitch>().Single();
                    Assert.True(toggle.Bounds is { Width: > 1, Height: > 1 }, $"{name}'s switch laid out as {toggle.Bounds}");
                    var texts = ((Grid)row).Children.OfType<StackPanel>().Single().Children.OfType<TextBlock>().ToList();
                    Assert.Equal(2, texts.Count);
                    Assert.All(texts, t => Assert.False(string.IsNullOrWhiteSpace(t.Text), $"{name} has an empty text"));
                }
            });
        }, TestContext.Current.CancellationToken);
    }

    private PlayModeViewModel ViewModel()
    {
        Directory.CreateDirectory(_dir);
        var profiles = new PlayerProfileService(FakeLocalization.English(), logger: null,
            filePath: Path.Combine(_dir, "player-profiles.json"), defaultProfileName: "Tester");
        var store = new PracticeProfileService(profiles, logger: null,
            legacyFilePath: Path.Combine(_dir, "practice-profiles.json"));
        var state = new ProjectState();
        return TestRigBuilders.PlayMode(profiles, new GameplayStatsService(store),
            new LeaderboardService(profiles), state, toneRig: HeadlessToneRig.FromSession(state), localization: LocalizationService.Instance);
    }

    // DOTCHARTER_UI_SWEEP_DUMP=1 + DOTCHARTER_RIG_CAPTURE_DIR=<dir outside the repo> save the frame.
    private static void WithView(Control view, PlayModeViewModel vm, string captureName, Action<Control> body)
    {
        view.DataContext = vm;
        var window = new Window { Width = WindowWidth, Height = WindowHeight, Content = view };
        foreach (var grammar in PlayModeViewGrammar)
            window.Styles.Add(new StyleInclude(new Uri("avares://DotCharter/")) { Source = new Uri(grammar) });
        window.Show();
        try
        {
            view.Measure(new Size(WindowWidth, WindowHeight));
            view.Arrange(new Rect(0, 0, WindowWidth, WindowHeight));
            body(view);
            var dir = Environment.GetEnvironmentVariable("DOTCHARTER_RIG_CAPTURE_DIR");
            if (UiSweepRunner.CaptureEnabled && !string.IsNullOrWhiteSpace(dir))
            {
                Directory.CreateDirectory(dir);
                using var frame = window.CaptureRenderedFrame();
                frame?.Save(Path.Combine(dir, captureName + ".png"), new PngBitmapEncoderOptions());
            }
        }
        finally
        {
            window.Close();
        }
    }

    private static Control Named(Control view, string name) =>
        view.GetVisualDescendants().OfType<Control>().FirstOrDefault(c => c.Name == name)
        ?? throw new Xunit.Sdk.XunitException($"the view has no part named '{name}'");

    /// <summary>What <c>PlayModeView.axaml</c>'s own <c>UserControl.Styles</c> includes.</summary>
    private static readonly string[] PlayModeViewGrammar =
    [
        "avares://DotCharter/Styles/SongListHeaderStyles.axaml",
        "avares://DotCharter/Views/PlayMode/PlayModeStyles.axaml"
    ];

    public void Dispose() => BestEffortDelete.Directory(_dir);
}
