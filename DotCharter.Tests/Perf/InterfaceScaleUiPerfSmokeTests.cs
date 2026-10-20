// The interface-scale feature's two halves, in a REAL window: the global scale
// applied at the shell root, and the Play Mode start screen's hero fill.

using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DotCharter.Localization;
using DotCharter.Services.Gameplay.Leaderboards;
using DotCharter.Services.Gameplay.Profiles;
using DotCharter.Services.Gameplay.Stats;
using DotCharter.Tone.Infrastructure.Storage;
using DotCharter.UI.Controls;
using Avalonia.Controls.Primitives;
using DotCharter.Tone.UI.Controls.Fire;
using DotCharter.UI.Views.Shell;
using DotCharter.UI.Services;
using DotCharter.UI.Views.PlayMode;

namespace DotCharter.Tests.Perf;

/// <summary>
///     <b>The interface-scale work, with a window behind it.</b>
///     <para>
///         <c>AutoUiScaleTests</c> and <c>LaunchHeroScaleTests</c> pin the
///         arithmetic without a window; those are the cheap half and they run in
///         the default gate. What they cannot see is whether the arithmetic is
///         WIRED — whether the transform reaches the shell root, whether the
///         hero's <c>Viewport</c> binding resolves, and whether a natively
///         composed surface asks for the pixels it is composited at. Each of
///         those failures builds clean and renders something.
///     </para>
///     <para>
///         <c>[Collection("HeadlessUI")]</c>, and this class is in the
///         SEPARATE headless invocation: Avalonia's headless dispatcher is
///         process-global (root <c>AGENTS.md</c> § Validation).
///     </para>
/// </summary>
[Collection("HeadlessUI")]
public sealed class InterfaceScaleUiPerfSmokeTests : IAsyncDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dc-uiscale-" + Guid.NewGuid().ToString("N"));

    public InterfaceScaleUiPerfSmokeTests() => Directory.CreateDirectory(_dir);

    public async ValueTask DisposeAsync()
    {
        // Never leave the process-wide scale where a case put it — it is a
        // static, and the next class to build a window would inherit it.
        //
        // INSIDE a dispatch, never from a bare Dispose. A scale change runs
        // every ScaleChanged subscriber, and a MainWindow another class left
        // shown is one (a test cannot Close() it: that terminates the process).
        // Outside a dispatch there is no application, so the layout that window
        // then invalidates creates Avalonia's MediaContext in the ROOT locator,
        // bound to a dispatcher that is already gone — and every later
        // application in the process inherits it and never renders or lays out
        // again: no hit test finds anything, no resize lands, no popup is placed.
        // `HeadlessSessionFixture` fails the next case loudly if that recurs.
        await HeadlessSessionFixture.Dispatch(() =>
        {
            UiScaleController.SetAutoMode(false);
            UiScaleController.Set(1.0);
        }, CancellationToken.None);
        BestEffortDelete.Directory(_dir);
    }

    // ---- the start screen's hero fill ---------------------------------------

    /// <summary>
    ///     <b>1920 x 1080 — the owner's "it's even worse on 1920x1080".</b>
    ///     The hero must occupy at least half the window's height there, and
    ///     1080p is exactly where the GLOBAL scale is 1.0 by policy
    ///     (<c>AutoUiScale</c>), so this can only pass if the hero's own fill is
    ///     doing the work.
    /// </summary>
    [Fact]
    public async Task At1920x1080_TheHeroFillsHalfTheWindowHeight()
    {
        var m = await MeasureHero(1920, 1080);

        Assert.True(m.HeroHeight >= m.RootHeight * 0.5,
            $"the hero measured {m.HeroHeight:F0} px in a {m.RootHeight:F0} px screen " +
            $"({m.Coverage:P1} at {m.Scale:0.00}x) — it is still an island in the middle " +
            "of a 1080p window, which is the report this work answers.");

        // …and it GREW to get there. The height fraction alone is a THIN guard
        // at 1080p and saying so is the point: the hero's natural height is
        // 537 px (measured headlessly 2026-09-12), i.e. 49.7 % of the window, so
        // disabling the fill entirely misses the line above by three pixels.
        // The scale is the claim with room in it. The smallness at 1080p is an
        // AREA problem — 17.6 % of the client area unscaled — which a height
        // fraction barely registers.
        Assert.True(m.Scale > 1.0,
            $"the hero is drawn at {m.Scale:0.00}x in a 1080p window — the fill is not engaging at the " +
            "one resolution where the global interface scale is 1.0 by policy and cannot help.");
    }

    /// <summary>A bigger window gets a bigger hero — the whole point — and the
    /// scale never drops below 1.0 on the way.</summary>
    [Fact]
    public async Task ABiggerWindow_GrowsTheHero_AndNeverShrinksIt()
    {
        var small = await MeasureHero(1920, 1080);
        var large = await MeasureHero(2560, 1440);

        Assert.True(large.Scale >= 1.0, $"1440p scaled the hero to {large.Scale:0.00}x — below the floor.");
        Assert.True(small.Scale >= 1.0, $"1080p scaled the hero to {small.Scale:0.00}x — below the floor.");
        Assert.True(large.HeroHeight > small.HeroHeight,
            $"the hero measured {large.HeroHeight:F0} px in a 1440p window and {small.HeroHeight:F0} px " +
            "in a 1080p one — it is not following the viewport at all.");
    }

    /// <summary>
    ///     The footer is NOT in the fill. The version stamp and the key hints
    ///     stay anchored in their corners at whatever the global scale is;
    ///     folding them into the hero's transform would march them inward as
    ///     the window grew, which is the opposite of a corner anchor.
    /// </summary>
    [Fact]
    public async Task TheFooter_IsOutsideTheHeroScaler()
    {
        await WithStartScreen(2560, 1440, view =>
        {
            var scaler = Find<HeroScaleToFit>(view, "LaunchHeroScaler");
            var footer = Find<Control>(view, "LaunchFooter");
            var version = Find<Control>(view, "LaunchVersionText");
            var hints = Find<Control>(view, "LaunchKeyHints");

            Assert.True(scaler.Scale > 1.0, "the hero is not scaled, so this proves nothing.");
            foreach (var (name, control) in new[]
                         { ("LaunchFooter", footer), ("LaunchVersionText", version), ("LaunchKeyHints", hints) })
            {
                Assert.False(control.GetVisualAncestors().Contains(scaler),
                    $"{name} is inside the hero scaler, so it grows with the hero instead of " +
                    "staying anchored in its corner.");
            }

            // …and it really is pinned to the floor of the screen, at the scale
            // in force. A corner anchor that measures fine but sits in the
            // middle is the failure this catches.
            var root = Find<Control>(view, "StartScreenRoot");
            var bottom = footer.TranslatePoint(new Point(0, footer.Bounds.Height), root);
            Assert.True(bottom.HasValue && Math.Abs(bottom.Value.Y - root.Bounds.Height) <= 1,
                $"the footer ends at y={bottom?.Y:F0} in a {root.Bounds.Height:F0} px screen.");
        });
    }

    // ---- the global scale at the shell root ----------------------------------

    /// <summary>
    ///     <b>ONE transform, at the root, and the content is 1/scale of the
    ///     client size.</b>
    ///     <para>
    ///         Both halves matter. A second scaler anywhere in the tree
    ///         multiplies (the app has three other
    ///         <c>LayoutTransformControl</c>s — the rig's zoom host,
    ///         <c>RigScaleToFit</c> and now the launch hero — and any of them
    ///         appearing above the shell root would square the scale); and
    ///         content that does NOT come back at 1/scale means the transform
    ///         was applied as a RENDER transform, which does not reflow and
    ///         would leave every fixed-pixel layout clipped instead of resized.
    ///     </para>
    /// </summary>
    [Fact]
    public async Task At150Percent_TheShellRootCarriesExactlyOneTransform_AndTheContentReflows()
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            UiScaleController.SetAutoMode(false);
            UiScaleController.Set(1.0);

            // A stand-in for the shell's own RootScaler: the mechanism under
            // test is the LayoutTransformControl + WindowUiScale contract, and
            // building the real MainWindow needs the whole DI graph.
            var content = new Border { Width = double.NaN, Height = double.NaN };
            var scaler = new LayoutTransformControl { Name = WindowUiScaleName, Child = content };
            var window = new Window { Width = 2560, Height = 1440, Content = scaler };
            window.Show();
            try
            {
                var scale = 1.5;
                scaler.LayoutTransform = DotCharter.UI.Views.Shell.WindowUiScale.TransformFor(scale);
                scaler.Measure(new Size(2560, 1440));
                scaler.Arrange(new Rect(0, 0, 2560, 1440));
                Dispatcher.UIThread.RunJobs(DispatcherPriority.Render);

                // Exactly one transform, and it is uniform.
                var transform = Assert.IsType<ScaleTransform>(scaler.LayoutTransform);
                Assert.Equal(1.5, transform.ScaleX);
                Assert.Equal(1.5, transform.ScaleY);
                Assert.Equal(1, CountScalersOnPathTo(content));

                // The content reflowed into 1/1.5 of the space — i.e. layout ran
                // again at the new size rather than the pixels being stretched.
                Assert.Equal(2560 / 1.5, content.Bounds.Width, 0);
                Assert.Equal(1440 / 1.5, content.Bounds.Height, 0);

                // …and the accumulated factor a render surface would read back
                // is the scale, not 1.0. This is the number the two Vulkan
                // surfaces multiply their Bounds by.
                Assert.Equal(1.5, VisualPixelScale.LayoutScaleTo(content, window), 4);
            }
            finally
            {
                window.Close();
            }
        }, CancellationToken.None);
    }

    /// <summary>
    ///     <b>A natively composed surface must ask for the pixels it is
    ///     COMPOSITED at.</b>
    ///     <para>
    ///         <c>Preview3DComposedCanvas</c> and the 2D timeline both size their
    ///         swapchain as <c>Bounds x scaling</c>, and both read that scaling
    ///         from <c>TopLevel.RenderScaling</c> alone — which is right only
    ///         while nothing between them and the window scales. The global UI
    ///         scale is exactly such a thing, so at 150 % they rendered at 1/1.5
    ///         of their composited resolution and the compositor upscaled the
    ///         difference: a soft highway and a soft timeline, with no exception
    ///         and no log line.
    ///     </para>
    ///     <para>
    ///         Asserted through <c>VisualPixelScale</c> because that is the
    ///         seam both call sites now go through, and because
    ///         <c>RenderScaling</c> is 1.0 on the headless backend — the half a
    ///         headless window can honestly speak to is the TRANSFORM half.
    ///     </para>
    /// </summary>
    [Fact]
    public async Task At150Percent_AComposedSurfaceReportsScaledPixels()
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var surface = new Border();
            var scaler = new LayoutTransformControl { Name = WindowUiScaleName, Child = surface };
            var window = new Window { Width = 1920, Height = 1080, Content = scaler };
            window.Show();
            try
            {
                scaler.LayoutTransform = new ScaleTransform(1.5, 1.5);
                scaler.Measure(new Size(1920, 1080));
                scaler.Arrange(new Rect(0, 0, 1920, 1080));
                Dispatcher.UIThread.RunJobs(DispatcherPriority.Render);

                var bounds = surface.Bounds;
                var pixelScale = VisualPixelScale.For(surface);

                // The swapchain size the call sites compute.
                var pixelWidth = (int)Math.Ceiling(bounds.Width * pixelScale);
                var pixelHeight = (int)Math.Ceiling(bounds.Height * pixelScale);

                // Bounds came back in the transform's INNER units…
                Assert.Equal(1920 / 1.5, bounds.Width, 0);
                Assert.Equal(1080 / 1.5, bounds.Height, 0);
                // …and the pixel size is back at the window's real extent, which
                // is the whole point: inner x 1.5 == what the compositor draws.
                Assert.Equal(1920, pixelWidth);
                Assert.Equal(1080, pixelHeight);

                // The pre-fix arithmetic, spelled out so the regression is named
                // rather than implied: RenderScaling alone is 1.0 here, so the
                // surface would have asked for 1280x720 to fill 1920x1080.
                var unscaled = (int)Math.Ceiling(bounds.Width * window.RenderScaling);
                Assert.True(unscaled < pixelWidth,
                    "RenderScaling alone already answers the full width, so this case cannot fail " +
                    "and is not evidence of anything.");
            }
            finally
            {
                window.Close();
            }
        }, CancellationToken.None);
    }

    /// <summary>The two Vulkan call sites go through the shared seam rather
    /// than re-reading <c>TopLevel.RenderScaling</c>. A source lint because the
    /// alternative is a GPU: the fault is a blurry upscale, which renders
    /// perfectly and asserts nothing.</summary>
    [Theory]
    [InlineData(@"Controls\ChartRenderer.cs")]
    [InlineData(@"Controls\Preview3DComposedCanvas.cs")]
    public void TheComposedSurfaces_SizeThemselvesThroughTheSharedSeam(string relativePath)
    {
        var source = UiSourceTree.ReadAllText(UiSourceTree.Resolve(Path.Combine(TestPaths.RepoRoot(), "DotCharter.UI", relativePath)));
        Assert.Contains("VisualPixelScale.For(this)", source, StringComparison.Ordinal);
    }

    // ---- popups ---------------------------------------------------------------

    /// <summary>
    ///     <b>Every popup kind scales with the content it sits beside.</b>
    ///     <para>
    ///         The shell's scale is a <c>LayoutTransformControl</c> and
    ///         Avalonia's overlay popup host is not inside it, so this is the one
    ///         place a 135 % shell could have shipped 100 % menus. Avalonia
    ///         12.1.2 gets a ComboBox dropdown right by itself and hands a
    ///         Flyout, a MenuFlyout and a ToolTip NOTHING — all three measured
    ///         1.0 at a 1.5x shell scale before <c>Views/Shell/PopupUiScale</c>.
    ///         The theory covers all four so the two halves cannot drift apart.
    ///     </para>
    /// </summary>
    [Theory]
    [InlineData("combo")]
    [InlineData("flyout")]
    [InlineData("menuflyout")]
    [InlineData("tooltip")]
    public async Task At150Percent_EveryPopupKindScalesWithTheContent(string kind)
    {
        await WithScaledPopup(kind, 1.5, (window, host, leaf) =>
        {
            Assert.NotNull(host.Transform);
            Assert.Equal(1.5, VisualPixelScale.LayoutScaleTo(leaf, window), 3);
        });
    }

    /// <summary>
    ///     …and it is a LAYOUT scale, not a picture of one: the host's own
    ///     bounds grow by the factor. A host that kept its 100 % size while its
    ///     content painted at 150 % would be clipped, and would flip off the
    ///     wrong screen edge.
    /// </summary>
    [Fact]
    public async Task At150Percent_ThePopupHostItselfGrows()
    {
        double unscaled = 0, scaled = 0;
        await WithScaledPopup("flyout", 1.0, (_, host, _) => unscaled = host.Bounds.Width);
        await WithScaledPopup("flyout", 1.5, (_, host, _) => scaled = host.Bounds.Width);

        Assert.True(unscaled > 1, "the unscaled flyout host has no width, so this proves nothing.");
        Assert.Equal(unscaled * 1.5, scaled, 0);
    }

    /// <summary>
    ///     <b>At the owner's own 1440p factor, a popup still opens AT its
    ///     anchor.</b> Scaling a popup that then lands somewhere else is worse
    ///     than not scaling it, and the failure is invisible to every other
    ///     assertion here — the content measures correctly either way.
    /// </summary>
    [Fact]
    public async Task At135Percent_APopupOpensAtItsAnchor()
    {
        await WithScaledPopup("aligned-flyout", 1.35, (window, host, _) =>
        {
            var anchor = window.GetVisualDescendants().OfType<Button>()
                .First(b => b.Name == "PopupAnchor");
            var anchorBottomLeft = anchor.TranslatePoint(new Point(0, anchor.Bounds.Height), window);
            var hostTopLeft = host.TranslatePoint(new Point(0, 0), window);

            Assert.True(anchorBottomLeft.HasValue && hostTopLeft.HasValue);
            Assert.Equal(anchorBottomLeft!.Value.X, hostTopLeft!.Value.X, 0);
            Assert.Equal(anchorBottomLeft.Value.Y, hostTopLeft.Value.Y, 0);
        });
    }

    /// <summary>
    ///     <b>A popup realized at one scale is correct after the scale
    ///     changes.</b> Auto re-derives on a window RESIZE, so a maximise from a
    ///     small window is exactly this, and <c>MenuFlyoutPrewarmer</c> realizes
    ///     flyout content ahead of time. The mechanism is that
    ///     <c>PopupUiScale</c> hands out ONE instance and writes its factors in
    ///     place — a fresh instance per change would leave an already-assigned
    ///     host on the old factor for ever.
    /// </summary>
    [Fact]
    public async Task AScaleChange_ReachesAPopupThatWasAlreadyRealized()
    {
        var before = PopupUiScale.Transform;
        double reopened = 0;

        await WithScaledPopup("flyout", 1.0, (window, _, leaf) =>
            Assert.Equal(1.0, VisualPixelScale.LayoutScaleTo(leaf, window), 3));

        await WithScaledPopup("flyout", 1.35, (window, _, leaf) =>
            reopened = VisualPixelScale.LayoutScaleTo(leaf, window));

        Assert.Equal(1.35, reopened, 3);
        Assert.Same(before, PopupUiScale.Transform);
        Assert.Equal(1.35, PopupUiScale.Transform.ScaleX, 3);
    }

    /// <summary>The prewarmer must not be what assigns the transform. It
    /// deliberately never OPENS a popup (its build is a CANCELLED open, and the
    /// close-time re-realize of #176 runs on a closed one), so the host does not exist while it runs and the factor
    /// is filled in on the first real open — which is also why a prewarmed
    /// flyout cannot be stuck at the scale it was warmed at.</summary>
    [Fact]
    public void ThePrewarmer_NeverOpensAPopup()
    {
        var source = UiSourceTree.ReadAllText(UiSourceTree.Resolve(Path.Combine(TestPaths.RepoRoot(), "DotCharter.UI",
            "Views", "Shell", "MenuFlyoutPrewarmer.cs")));
        Assert.DoesNotContain("IsSubMenuOpen = true", source, StringComparison.Ordinal);
        Assert.DoesNotContain(".Open()", source, StringComparison.Ordinal);
    }

    private async Task WithScaledPopup(string kind, double scale,
        Action<Window, OverlayPopupHost, Control> body)
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var combo = new ComboBox { Width = 120, ItemsSource = new[] { "aaa", "bbb", "ccc" } };
            var button = new Button { Name = "PopupAnchor", Content = "target", Width = 120, Height = 32 };
            var panel = new StackPanel { Margin = new Thickness(40) };
            panel.Children.Add(combo);
            panel.Children.Add(button);
            var scaler = new LayoutTransformControl { Name = WindowUiScaleName, Child = panel };
            var window = new Window { Width = 1000, Height = 800, Content = scaler };

            UiScaleController.SetManual(scale);
            window.Show();
            try
            {
                scaler.LayoutTransform = DotCharter.UI.Views.Shell.WindowUiScale.TransformFor(scale);
                scaler.Measure(new Size(1000, 800));
                scaler.Arrange(new Rect(0, 0, 1000, 800));
                Dispatcher.UIThread.RunJobs(DispatcherPriority.Render);

                switch (kind)
                {
                    case "combo":
                        combo.IsDropDownOpen = true;
                        break;
                    case "flyout":
                        new Flyout { Content = new Border { Width = 150, Height = 80 } }.ShowAt(button);
                        break;
                    case "aligned-flyout":
                        // An explicit, unambiguous placement: a centred one would
                        // make the assertion about Avalonia's centring arithmetic
                        // rather than about the anchor.
                        new Flyout
                        {
                            Placement = PlacementMode.BottomEdgeAlignedLeft,
                            Content = new Border { Width = 150, Height = 80 }
                        }.ShowAt(button);
                        break;
                    case "menuflyout":
                        var mf = new MenuFlyout();
                        mf.Items.Add(new MenuItem { Header = "One" });
                        mf.Items.Add(new MenuItem { Header = "Two" });
                        mf.ShowAt(button);
                        break;
                    case "tooltip":
                        ToolTip.SetTip(button, "hello");
                        ToolTip.SetIsOpen(button, true);
                        break;
                }

                Dispatcher.UIThread.RunJobs(DispatcherPriority.Render);
                window.UpdateLayout();
                Dispatcher.UIThread.RunJobs(DispatcherPriority.Render);

                var host = window.GetVisualDescendants().OfType<OverlayPopupHost>().FirstOrDefault();
                Assert.True(host != null, $"no OverlayPopupHost realized for '{kind}' — the popup never opened.");
                var leaf = host!.GetVisualDescendants().OfType<Control>()
                    .LastOrDefault(c => c.Bounds.Width > 1 && c.Bounds.Height > 1);
                Assert.True(leaf != null, $"the '{kind}' popup host has no laid-out content.");
                body(window, host, leaf!);
            }
            finally
            {
                window.Close();
            }
        }, CancellationToken.None);
    }

    // ---- helpers -------------------------------------------------------------

    private const string WindowUiScaleName = DotCharter.UI.Views.Shell.WindowUiScale.ScalerName;

    private sealed record HeroMeasurement(double HeroHeight, double RootHeight, double Scale)
    {
        /// <summary>The share of the screen's height the hero covers — the
        /// number the owner asked to see before and after.</summary>
        public double Coverage => RootHeight > 0 ? HeroHeight / RootHeight : 0;
    }

    private async Task<HeroMeasurement> MeasureHero(double width, double height)
    {
        HeroMeasurement? measured = null;
        await WithStartScreen(width, height, view =>
        {
            var scaler = Find<HeroScaleToFit>(view, "LaunchHeroScaler");
            var root = Find<Control>(view, "StartScreenRoot");
            measured = new HeroMeasurement(scaler.Bounds.Height, root.Bounds.Height, scaler.Scale);
        });
        return measured!;
    }

    private async Task WithStartScreen(double width, double height, Action<PlayModeView> body)
    {
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var profiles = new PlayerProfileService(FakeLocalization.English(), logger: null,
                filePath: Path.Combine(_dir, $"profiles-{Guid.NewGuid():N}.json"), defaultProfileName: "Tester");
            var vm = TestRigBuilders.PlayMode(profiles, new GameplayStatsService(
                    new DotCharter.Services.Audio.LivePlay.PracticeProfileService(profiles, logger: null,
                        legacyFilePath: Path.Combine(_dir, $"practice-{Guid.NewGuid():N}.json"))),
                new LeaderboardService(profiles), new ProjectState(),
                toneRig: HeadlessToneRig.FromSession(new ProjectState()), localization: LocalizationService.Instance);

            var view = new PlayModeView { DataContext = vm };
            var window = new Window { Width = width, Height = height, Content = view };
            window.Show();
            try
            {
                // The launch facts land at Background priority; the hero's
                // natural size depends on the chips being filled in.
                Dispatcher.UIThread.RunJobs(DispatcherPriority.Background);
                view.Measure(new Size(width, height));
                view.Arrange(new Rect(0, 0, width, height));
                view.UpdateLayout();
                // A second pass: the hero's scale is decided in MeasureOverride
                // from the ScrollViewer's bounds, which the FIRST pass is what
                // establishes. This is the "one extra layout, not one per frame"
                // the budget allows.
                view.Measure(new Size(width, height));
                view.Arrange(new Rect(0, 0, width, height));
                view.UpdateLayout();

                Assert.True(vm.IsStartScreen, "the view model must boot on the launch screen");
                body(view);
            }
            finally
            {
                window.Close();
            }
        }, CancellationToken.None);
    }

    private static T Find<T>(Visual view, string name) where T : Control
    {
        var control = view.GetVisualDescendants().OfType<T>().FirstOrDefault(c => c.Name == name);
        Assert.True(control != null,
            $"'{name}' is not in the visual tree — the screen never realized (a XAML fault or a rename).");
        return control!;
    }

    private static int CountScalersOnPathTo(Visual leaf) =>
        leaf.GetVisualAncestors().OfType<LayoutTransformControl>()
            .Count(c => c.LayoutTransform is not null);
}
