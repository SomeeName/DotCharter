using Avalonia;
using Avalonia.Controls;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;
using DotCharter.Localization;
using DotCharter.Services.Audio.LivePlay;
using DotCharter.Services.Gameplay.Leaderboards;
using DotCharter.Services.Gameplay.Profiles;
using DotCharter.Services.Gameplay.Stats;
using DotCharter.Tone.Infrastructure.Storage;
using DotCharter.UI.ViewModels.PlayMode;
using DotCharter.UI.Views.PlayMode;
using DotCharter.UI.Views.Shell;

namespace DotCharter.Tests.Perf;

/// <summary>
///     <b>The behavioural half of <c>UI/PlayModeDeferredScreenTests</c>: the
///     deferred screens really are absent from the boot tree, and really do
///     arrive.</b>
///     <para>
///         The source lints there can prove the MARKUP is shaped for deferral.
///         Only a real Avalonia session can prove that Avalonia honours it — that
///         a hidden <c>ContentControl</c> never runs the layout pass that would
///         build its <c>ContentTemplate</c>, and that the prewarm listing and a
///         plain navigation each build it after all. Every one of those is a
///         framework behaviour an Avalonia upgrade could change silently: the
///         screens would simply be constructed before the first frame again, with
///         every other test green.
///     </para>
///     <para>
///         Counted in ELEMENTS, never milliseconds — the repo rule. The element
///         count IS the cost being moved: it is what a first construction has to
///         allocate, style and template.
///     </para>
/// </summary>
[Collection("HeadlessUI")]
public sealed class PlayModeDeferredScreenUiPerfSmokeTests(ITestOutputHelper output) : IDisposable
{
    private const double WindowWidth = 1600;
    private const double WindowHeight = 1000;

    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "DotCharterTests", Guid.NewGuid().ToString("N"));

    /// <summary>The root of each deferred body, by the <c>x:Name</c> the markup
    /// still carries. Derived from the production list so a host added there is
    /// checked here the day it compiles.</summary>
    private static IEnumerable<string> DeferredBodyNames() =>
        PlayModeView.DeferredScreenHostNames.Select(h => h[..^"Host".Length]);

    [Fact]
    public async Task TheDeferredScreens_AreNotBuiltForTheBootFrame_AndArriveWithThePrewarm()
    {
        await WithView((view, _) =>
        {
            var atBoot = view.GetVisualDescendants().OfType<Control>().ToList();
            var namesAtBoot = atBoot.Select(c => c.Name).ToHashSet(StringComparer.Ordinal);

            foreach (var body in DeferredBodyNames())
            {
                Assert.False(namesAtBoot.Contains(body),
                    $"'{body}' was built for the app's FIRST FRAME. Its host is a ContentControl whose " +
                    "body is a ContentTemplate; a hidden host must never run the layout pass that builds " +
                    "one. Something measured it — a second gate that went missing, a host that turned " +
                    "visible, or an Avalonia change to when ContentPresenter builds its child.");
            }

            // The listing IS the materialization (HiddenSurfaces builds the
            // bodies first), and it is called from the prewarm drain, after the
            // first frame.
            var surfaces = ((IPrewarmableSurfaces)view).HiddenSurfaces();
            var afterPrewarmList = view.GetVisualDescendants().OfType<Control>().ToList();
            var namesAfter = afterPrewarmList.Select(c => c.Name).ToHashSet(StringComparer.Ordinal);

            foreach (var body in DeferredBodyNames())
            {
                Assert.True(namesAfter.Contains(body),
                    $"'{body}' is still missing after the prewarm listed the surfaces — it would then be " +
                    "built on the user's first navigation instead, which is the lag the prewarm exists " +
                    "to remove.");
            }

            // …and the panes INSIDE those bodies reach the list, which is the
            // thing FindControl could not do across a template namescope.
            var listed = surfaces.Select(s => s.Name).ToHashSet(StringComparer.Ordinal);
            // The rig's tone drawer is a hidden root INSIDE the prewarmed rig
            // overlay; the drain stops at the overlay, so the drawer has to be
            // listed by name or its sixty cards lay out on the first click of
            // the tone pill (owner, 2026-09-08: "pressing this causes lag").
            Assert.Contains("RigToneBrowser", PlayModeView.NestedScreenBodyNames);
            Assert.Contains(surfaces, s => s.Name == "RigToneBrowser");
            // …and the Tones VIEW that holds it is listed BEFORE it (2026-09-27):
            // its template is what puts the drawer in the visual tree at all.
            var names = PlayModeView.NestedScreenBodyNames.ToList();
            Assert.True(names.IndexOf("RigTonesMode") is >= 0 and var mode && mode < names.IndexOf("RigToneBrowser"),
                "the Tones view is not listed before its drawer");
            Assert.Contains(surfaces, s => s.Name == "RigTonesMode");

            foreach (var pane in PlayModeView.GameplaySettingsPaneNames)
            {
                Assert.True(listed.Contains(pane),
                    $"'{pane}' lives inside a deferred body and did not reach HiddenSurfaces() — every " +
                    "surface in that list would go cold with it, silently.");
            }

            output.WriteLine(
                $"PlayModeView controls: boot={atBoot.Count}, after prewarm listing={afterPrewarmList.Count} " +
                $"(+{afterPrewarmList.Count - atBoot.Count} deferred off the first frame); " +
                $"{surfaces.Count} surfaces listed.");

            Assert.True(afterPrewarmList.Count > atBoot.Count,
                "the prewarm listing added no controls at all — nothing is being deferred.");
        });
    }

    /// <summary>
    ///     <b>Song Select and Results are deferred too (#648)</b>, and they are
    ///     what the shell's switch budget is about: Song Select is Home's first
    ///     destination and Results ends every run. Neither may be built for the
    ///     first frame, and after the drain's own steps — the construction steps,
    ///     the listing, one <c>Realize</c> + <c>Settle</c> per surface — each must
    ///     be LAID OUT (non-zero bounds) while its host is still hidden, so the
    ///     first switch pays no construction and no first layout.
    /// </summary>
    [Theory]
    [InlineData("SongSelectScreenRoot")]
    [InlineData("ResultsScreenRoot")]
    public async Task SongSelectAndResults_AreNotBuiltForTheBootFrame_AndAreRealizedByTheDrain(string root)
    {
        Assert.Contains(root, DeferredBodyNames());

        await WithView((view, _) =>
        {
            Assert.DoesNotContain(root, view.GetVisualDescendants().OfType<Control>().Select(c => c.Name));
            Assert.DoesNotContain(root, view.GetLogicalDescendants().OfType<Control>().Select(c => c.Name));

            var size = new Size(WindowWidth, WindowHeight);
            foreach (var step in ((IPrewarmConstruction)view).ConstructionSteps())
                step();
            foreach (var surface in ((IPrewarmableSurfaces)view).HiddenSurfaces())
            {
                HiddenSubtreePrewarmer.Realize(surface, size);
                HiddenSubtreePrewarmer.Settle(surface, size);
            }

            var screen = view.GetVisualDescendants().OfType<Control>().FirstOrDefault(c => c.Name == root);
            Assert.True(screen is not null, $"'{root}' is still not built after the prewarm drain's steps.");
            var host = view.FindControl<ContentControl>($"{root}Host")!;
            Assert.False(host.IsVisible, $"the prewarm turned '{root}Host' visible — it must never write a gate.");
            Assert.True(screen!.Bounds is { Width: > 1, Height: > 1 },
                $"'{root}' was built but not laid out by the drain ({screen.Bounds}) — the first switch to it " +
                "would pay its whole first layout.");
            output.WriteLine($"{root}: realized hidden at {screen.Bounds.Width:F0}x{screen.Bounds.Height:F0}, " +
                             $"{screen.GetVisualDescendants().Count()} visuals.");
        });
    }

    /// <summary>
    ///     A user who navigates to a deferred screen before the drain gets there
    ///     needs NO code path of ours: the host turning visible runs its own
    ///     measure, which builds the body. If that ever stopped being true the
    ///     screen would come up EMPTY — an unrecoverable dead end, not a slow one.
    /// </summary>
    [Theory]
    [InlineData(PlayModeScreen.TuningCheck, "TuningCheckScreenRoot")]
    [InlineData(PlayModeScreen.SongSelect, "SongSelectScreenRoot")]
    [InlineData(PlayModeScreen.Results, "ResultsScreenRoot")]
    public async Task NavigatingToADeferredScreen_BuildsAndLaysItOut_WithNoPrewarm(PlayModeScreen target, string root)
    {
        await WithView((view, vm) =>
        {
            Assert.DoesNotContain(root,
                view.GetVisualDescendants().OfType<Control>().Select(c => c.Name));

            vm.CurrentScreen = target;
            view.UpdateLayout();

            var screen = view.GetVisualDescendants().OfType<Control>()
                .FirstOrDefault(c => c.Name == root);
            Assert.True(screen is not null,
                $"navigating to {target} built nothing — the deferred body never arrived.");
            Assert.True(screen!.Bounds is { Width: > 1, Height: > 1 },
                $"{target} laid out as {screen.Bounds} — it rendered nothing.");

            // Entering Results starts the staged reveal (a delay chain); leaving
            // cancels it, so nothing ticks on after the window closes.
            vm.CurrentScreen = PlayModeScreen.Start;
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
                window.Close();
            }
        }, CancellationToken.None);
    }

    public void Dispose() => BestEffortDelete.Directory(_dir);
}
