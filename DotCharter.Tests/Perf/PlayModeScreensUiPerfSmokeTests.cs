// PlayModeScreensUiPerfSmokeTests root: the class contract, the shared window/temp-dir fields, the WithView harness with its practice + trend seeding, and the Find / AssertLaidOut / AssertContains / AssertLetsTheBackdropThrough helpers every partial uses.

using Avalonia;
using Avalonia.Controls;
using Avalonia.Styling;
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
///     Builds <see cref="PlayModeView" /> for real, ATTACHES it to a window and
///     lays out every non-gameplay screen with data behind it.
///
///     <para>
///         Both halves are load-bearing. Construction alone catches a XAML fault
///         (an unresolvable <c>StaticResource</c>, a bad <c>StringFormat</c>) —
///         the app's <c>ViewLocator</c> swallows one into a "Not Found: …"
///         placeholder, so a broken view is indistinguishable from a missing one.
///         But ATTACHING is what raises <c>OnAttachedToLogicalTree</c>, and that
///         is where a <c>Button</c> calls <c>Command.CanExecute</c> — a
///         <c>RelayCommand&lt;T&gt;</c> fed a literal <c>CommandParameter</c>
///         throws there and takes the app down during layout.
///     </para>
///     <para>
///         And a screen only realizes what its bindings can SEE: every Play Mode
///         screen is an <c>IsVisible</c>-gated panel, and Avalonia skips
///         <c>MeasureCore</c> (hence <c>ApplyTemplate</c>) for an invisible
///         element, so a screen the view model never navigated to has zero
///         visual descendants and proves nothing. Each case therefore drives the
///         REAL navigation command, then asserts the screen's key subtrees
///         measure to a non-zero size — the shape a silently-failed panel takes.
///     </para>
///     <para>
///         Named <c>*UiPerfSmokeTests</c> because it needs the real Avalonia app
///         session, which cannot share a testhost with the gate and is excluded from the
///         default gate. Run it by hand after touching any Play Mode screen.
///     </para>
///     <para>
///         <c>[Collection("HeadlessUI")]</c> is deliberate: these cases
///         <c>Show()</c> their own <c>Window</c> on the SHARED headless app
///         session, and the collection's <c>DisableParallelization</c> is what
///         keeps that second window from racing a test that depends on the
///         MainWindow holding keyboard focus (<c>EscapeKeyRoutingUiPerfSmokeTests</c>
///         drives a real Esc key press through it).
///     </para>
/// </summary>
[Collection("HeadlessUI")]
public sealed partial class PlayModeScreensUiPerfSmokeTests : IDisposable
{
    private const double WindowWidth = 1600;
    private const double WindowHeight = 1000;

    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "DotCharterTests", Guid.NewGuid().ToString("N"));

    /// <summary>Asserts a descendant lies inside its ancestor's box. Bounds are
    /// parent-relative, so this translates rather than comparing across
    /// levels — the silent unit error that makes an overflow assertion pass.
    /// A tolerance of 1 px absorbs the hairline borders these panels draw.
    /// </summary>
    private static void AssertContains(Control ancestor, Control child, string what)
    {
        var topLeft = child.TranslatePoint(default, ancestor);
        var bottomRight = child.TranslatePoint(new Point(child.Bounds.Width, child.Bounds.Height), ancestor);
        Assert.True(topLeft.HasValue && bottomRight.HasValue,
            $"{what} is not in the same visual tree as its region");

        Assert.True(topLeft!.Value.X >= -1 && topLeft.Value.Y >= -1,
            $"{what} starts at {topLeft.Value} — above/left of its region");
        Assert.True(bottomRight!.Value.X <= ancestor.Bounds.Width + 1,
            $"{what} reaches x={bottomRight.Value.X:F0} in a {ancestor.Bounds.Width:F0} px region");
        Assert.True(bottomRight.Value.Y <= ancestor.Bounds.Height + 1,
            $"{what} reaches y={bottomRight.Value.Y:F0} in a {ancestor.Bounds.Height:F0} px region — " +
            "it runs off the bottom of the screen");
    }

    private static T Find<T>(PlayModeView view, string name) where T : Control =>
        Assert.IsAssignableFrom<T>(Find(view, name));

    private static void AssertLetsTheBackdropThrough(PlayModeView view, string rootName)
    {
        var root = Find(view, rootName);
        Assert.True(root is not Panel { Background: not null } and not Border { Background: not null },
            $"'{rootName}' paints its own ground over MenuBackdropControl");

        var washes = root.GetVisualDescendants().OfType<Border>()
            .Where(b => b.Classes.Contains("FireWash")).ToList();
        Assert.NotEmpty(washes);
        Assert.All(washes, w => Assert.False(w.IsHitTestVisible,
            "a Fire wash is hit-testable and will swallow clicks meant for the screen"));
    }

    /// <summary>The <c>ProjectState</c> the last <see cref="WithView" /> built.
    /// The Get Ready cases need a real loaded chart, which lives there.</summary>
    private ProjectState? _state;

    /// <summary>The practice store the last <see cref="WithView" /> built, for a
    /// case that records a run behind the screen it lays out.</summary>
    private PracticeProfileService? _store;

    /// <summary><paramref name="seedProject"/> is the Get Ready hook — it runs
    /// against the same <c>ProjectState</c> the view model is handed, so a case
    /// can load a real chart before the view is built.</summary>
    private async Task WithView(
        Action<PlayModeView, PlayModeViewModel> body,
        bool seedPractice = true,
        Action<ProjectState>? seedProject = null,
        double width = WindowWidth,
        double height = WindowHeight)
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var profiles = new PlayerProfileService(FakeLocalization.English(), logger: null,
                filePath: Path.Combine(_dir, "player-profiles.json"), defaultProfileName: "Tester");
            var store = new PracticeProfileService(profiles, logger: null,
                legacyFilePath: Path.Combine(_dir, "practice-profiles.json"));
            if (seedPractice) SeedPractice(store);
            _store = store;

            var state = new ProjectState();
            _state = state;
            seedProject?.Invoke(state);

            var vm = TestRigBuilders.PlayMode(profiles, new GameplayStatsService(store),
                new LeaderboardService(profiles), state, toneRig: HeadlessToneRig.FromSession(state), localization: LocalizationService.Instance);

            var view = new PlayModeView { DataContext = vm };
            // Dark, as MainWindow — PlayModeView's only host — asks for. The app
            // itself is "Default", which a headless platform resolves to Light,
            // so any text that inherits the window's Fluent foreground would
            // render BLACK here and white in the product (#688).
            var window = new Window
            {
                Width = width, Height = height, Content = view, RequestedThemeVariant = ThemeVariant.Dark
            };
            // Show() is what roots the tree and raises OnAttachedToLogicalTree.
            // Layout is driven through the VIEW, not the window: measuring a
            // headless Window directly reports "Invalid size returned for Measure".
            window.Show();
            try
            {
                view.Measure(new Size(width, height));
                view.Arrange(new Rect(0, 0, width, height));
                body(view, vm);
            }
            finally
            {
                window.Close();
            }
        }, CancellationToken.None);
    }

    /// <summary>Three recorded runs on different days of the CURRENT month with
    /// different durations, so the front door's last-session strip and the
    /// song rows' played state have real data. The store stamps every record
    /// "now", so the day is moved afterwards on the cached objects
    /// <c>GetAll()</c> hands back.</summary>
    private static void SeedPractice(PracticeProfileService store)
    {
        var today = DateTime.UtcNow;
        // Day offsets that stay inside this month whatever day it is today.
        var offsets = new[] { 0, Math.Min(2, today.Day - 1), Math.Min(9, today.Day - 1) };
        var durations = new[] { 900.0, 240.0, 3000.0 };

        for (var i = 0; i < offsets.Length; i++)
        {
            var profile = store.Record($"song{i}", "lead",
                new LivePlaySessionResult(1000 + i * 250, 0.8, 8, 2, 20, 5, durations[i], 100, 0.5),
                displayName: $"Artist {i} - Song {i}",
                // Per-SECTION and per-TECHNIQUE tallies, as a real run
                // persists them.
                sections: new[]
                {
                    new SectionResult("Intro", 0, 20_000, 10, 9, 1),
                    new SectionResult("Chorus", 20_000, 45_000, 10, 4, 6)
                },
                techniques: new[]
                {
                    new TechniqueResult(PlayedTechnique.PalmMute, 8, 1, 2),
                    new TechniqueResult(PlayedTechnique.Slide, 4, 2, 5)
                });
            var session = profile.Sessions[^1];
            session.EndedUtc = today.AddDays(-offsets[i]);
            session.DurationSeconds = durations[i];
        }
    }

    /// <summary>The realized `Border.FireListRow` rows of a Fire list. An
    /// ItemsControl/ListBox with no realized containers still MEASURES — it
    /// just has no rows — which is the shape a broken ItemsSource or a row
    /// template that failed to build both take.</summary>
    private static List<Border> FireRows(ItemsControl list) =>
        list.GetVisualDescendants().OfType<Border>()
            .Where(b => b.Classes.Contains("FireListRow"))
            .ToList();

    private static Control Find(PlayModeView view, string name)
    {
        var control = view.GetVisualDescendants().OfType<Control>()
            .FirstOrDefault(c => c.Name == name);
        Assert.True(control != null,
            $"'{name}' is not in the visual tree — the screen never realized (a XAML fault " +
            "or a renamed element).");
        return control!;
    }

    private static void AssertLaidOut(PlayModeView view, string name)
    {
        var control = Find(view, name);
        Assert.True(control.IsVisible, $"'{name}' realized but is not visible");
        Assert.True(control.Bounds.Width > 1 && control.Bounds.Height > 1,
            $"'{name}' laid out as {control.Bounds} — it rendered nothing");
    }

    public void Dispose() => BestEffortDelete.Directory(_dir);
}
