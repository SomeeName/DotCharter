using DotCharter.Localization;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Layout;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DotCharter.Services.Audio.LivePlay;
using DotCharter.Services.Gameplay.Leaderboards;
using DotCharter.Services.Gameplay.Profiles;
using DotCharter.Services.Gameplay.Stats;
using DotCharter.Tone.Infrastructure.Storage;
using DotCharter.Tone.UI.Controls;
using DotCharter.Tone.UI.ViewModels.PlayMode;
using DotCharter.Tone.UI.Views.PlayMode;
using DotCharter.UI;
using DotCharter.Tone.UI.Controls.Dsp;
using DotCharter.Tone.UI.Controls.Fire;
using DotCharter.UI.Services;
using DotCharter.UI.ViewModels.PlayMode;
using DotCharter.Presentation.Contracts.Settings;
using DotCharter.UI.Views.PlayMode;
using DotCharter.UI.Views.Settings;
using DotCharter.UI.Views.Shell;

// `Avalonia.Controls.Shapes` cannot be imported here: its `Path` collides with
// `System.IO.Path`, which this file uses on every artifact write.
using Shape = Avalonia.Controls.Shapes.Shape;

namespace DotCharter.Tests.Perf;

/// <summary>
///     The RESOLUTION audit (owner, 2026-09-12: "generally the ui needs to be
///     overall optimized for all screens 1080p to 4k"). Every Play Mode screen
///     and every standalone view is laid out at 1920x1080, 2560x1440 and
///     3840x2160 and MEASURED — how much of the client area the screen's real
///     content actually covers, which controls are pinned to a fixed size well
///     under the window, which text falls below the legibility floor, and what
///     arranges outside the window.
///     <para>
///         This is a MEASUREMENT harness, not a gate: it asserts only that the
///         run produced a report, and it is OPT-IN behind
///         <c>DOTCHARTER_UI_AUDIT=1</c> so neither the default gate nor the
///         headless CI job pays for it. The class name keeps the
///         <c>UiPerfSmokeTests</c> suffix on purpose — that suffix is what the
///         default gate filters OUT, and this class needs the process-global
///         headless dispatcher exactly like its neighbours.
///     </para>
///     <para>
///         Coverage is reported as the INK BOX: the union of the arranged
///         bounds of every visible content leaf (text, glyphs, images, shapes
///         and the interactive primitives), in window coordinates. A backdrop
///         wash or a full-bleed <c>Border</c> is deliberately NOT content — it
///         always covers 100% and would report every screen as perfect. The
///         largest visible panel that is not full-bleed is reported alongside
///         it as the primary-panel candidate.
///     </para>
///     <para>
///         Artifacts land in <c>&lt;repo&gt;/artifacts/ui-audit/&lt;stamp&gt;/</c>
///         as <c>audit.json</c> + <c>audit.md</c>. With
///         <c>DOTCHARTER_UI_SWEEP_DUMP=1</c> the session boots with real Skia
///         drawing and a PNG per screen per resolution is written beside them
///         (<c>UiSweepRunner.CaptureEnabled</c> is the one switch that decides
///         whether <c>CaptureRenderedFrame</c> can return anything).
///     </para>
/// </summary>
[Collection("HeadlessUI")]
public sealed class UiResolutionAuditUiPerfSmokeTests : IDisposable
{
    /// <summary>Opt-in switch. Absent ⇒ every case skips.</summary>
    private const string GateEnvVar = "DOTCHARTER_UI_AUDIT";

    /// <summary>A control narrower/shorter than this fraction of the window in
    /// an axis, yet pinned there by a literal <c>Width</c>/<c>Height</c>/
    /// <c>MaxWidth</c>/<c>MaxHeight</c>, cannot use a bigger screen.</summary>
    private const double FixedSizeFraction = 0.40;

    /// <summary>Below this the owner reports text as unreadable at 1:1.</summary>
    private const double MinFontSize = 12.0;

    private static readonly (string Name, int Width, int Height)[] Resolutions =
    [
        ("1920x1080", 1920, 1080),
        ("2560x1440", 2560, 1440),
        ("3840x2160", 3840, 2160)
    ];

    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "dc-ui-audit-" + Guid.NewGuid().ToString("N"));

    private static readonly List<ScreenMeasurement> Measurements = [];

    // ── the two cases ─────────────────────────────────────────────────────

    /// <summary>Every <see cref="PlayModeScreen" /> at every resolution, over a
    /// populated profile/practice store — the screens the owner's complaint is
    /// about.</summary>
    [Fact]
    public async Task PlayModeScreens_AtEveryResolution_AreMeasured()
    {
        RequireGate();

        foreach (var (label, width, height) in Resolutions)
        {
            // ONE dispatch per resolution: past ~60 dispatches the headless
            // session refuses to hand out another isolated application.
            await HeadlessSessionFixture.Dispatch(() =>
            {
                Directory.CreateDirectory(_dir);
                var profiles = new PlayerProfileService(FakeLocalization.English(), logger: null,
                    filePath: Path.Combine(_dir, $"profiles-{label}.json"),
                    defaultProfileName: "Tester");
                var store = new PracticeProfileService(profiles, logger: null,
                    legacyFilePath: Path.Combine(_dir, $"practice-{label}.json"));
                var vm = TestRigBuilders.PlayMode(profiles, new GameplayStatsService(store),
                    new LeaderboardService(profiles), new ProjectState(), toneRig: HeadlessToneRig.FromSession(new ProjectState()), localization: LocalizationService.Instance);

                var view = new PlayModeView { DataContext = vm };
                var window = new Window { Width = width, Height = height, Content = view };
                window.Show();
                try
                {
                    Dispatcher.UIThread.RunJobs(DispatcherPriority.Background);
                    window.UpdateLayout();

                    foreach (var screen in Enum.GetValues<PlayModeScreen>())
                    {
                        vm.CurrentScreen = screen;
                        Dispatcher.UIThread.RunJobs(DispatcherPriority.Background);
                        for (var pass = 0; pass < 3; pass++) window.UpdateLayout();

                        var measurement = MeasureSurface(
                            view, "PlayMode/" + screen, label, width, height);
                        Measurements.Add(measurement);
                        Capture(window, $"playmode-{screen}-{label}");
                    }
                }
                finally { window.Close(); }
            }, TestContext.Current.CancellationToken);
        }

        Assert.NotEmpty(Measurements);
        WriteReport();
    }

    /// <summary>Every standalone view — shell chrome, song browser, settings
    /// tabs, editor panes, dialogs, the rig — at every resolution, reusing the
    /// UI sweep's own catalogue and its realistic data contexts so an empty
    /// <c>ItemsControl</c> does not report as a screen that uses no space.
    /// </summary>
    [Fact]
    public async Task EveryStandaloneView_AtEveryResolution_IsMeasured()
    {
        RequireGate();

        var types = UiSweepCatalog.ViewTypes()
            .Where(t => !typeof(Window).IsAssignableFrom(t))
            .ToList();

        foreach (var (label, width, height) in Resolutions)
        {
            await HeadlessSessionFixture.Dispatch(() =>
            {
                var services = App.Services
                                ?? throw new InvalidOperationException(
                                    "App.Services is null — the headless app session did not boot.");
                var seeder = UiSweepDataContexts.Build(services);

                var window = new Window { Width = width, Height = height };
                window.Show();
                try
                {
                    foreach (var type in types)
                    {
                        Control? view = null;
                        try
                        {
                            view = (Control)Activator.CreateInstance(type)!;
                            var axaml = UiSweepCatalog.AxamlFor(type);
                            var declared = axaml is null
                                ? null
                                : UiSweepCatalog.DeclaredDataType(axaml);
                            var (dataContext, _) = seeder.For(declared);
                            if (dataContext != null)
                            {
                                // 39 surfaces are a scrim bound to IsOpen and
                                // measure NOTHING while closed.
                                OpenIfDialog(dataContext);
                                view.DataContext = dataContext;
                            }
                            else if (declared != null)
                            {
                                view.SetValue(StyledElement.DataContextProperty, null);
                            }

                            window.Content = view;
                            Dispatcher.UIThread.RunJobs(DispatcherPriority.Background);
                            for (var pass = 0; pass < 3; pass++) window.UpdateLayout();

                            Measurements.Add(MeasureSurface(
                                view, GroupOf(type) + "/" + type.Name, label, width, height));
                            Capture(window, $"view-{type.Name}-{label}");
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException)
                        {
                            Measurements.Add(ScreenMeasurement.Failed(
                                GroupOf(type) + "/" + type.Name, label, width, height,
                                ex.GetType().Name + ": " + ex.Message));
                        }
                        finally
                        {
                            window.Content = null;
                            if (view is IDisposable disposable) disposable.Dispose();
                        }
                    }
                }
                finally { window.Close(); }
            }, TestContext.Current.CancellationToken);
        }

        Assert.NotEmpty(Measurements);
        WriteReport();
    }

    // ── the fixes this audit produced, asserted ───────────────────────────

    /// <summary>
    ///     <b>The worked example for the breakpoint mechanism, as a GATE rather
    ///     than a measurement.</b>
    ///     <para>
    ///         Settings was the audit's P0.4: a centred strip capped at a
    ///         literal <c>MaxWidth="1040"</c>, <c>ratio</c> 0.25 — pixel-for-
    ///         pixel identical at 1080p and at 4K — on a page
    ///         <c>docs/agents/ui/settings.md</c> had already written off as one
    ///         where "nothing in it is ever rescued by a wider window". The cap
    ///         is now <c>FireColumnMaxWidthLg</c>, which
    ///         <c>Styles/FireTokens/Ladders.Wide.axaml</c> moves to 1400 above
    ///         the wide breakpoint.
    ///     </para>
    ///     <para>
    ///         <b>The assertion is the CAP, not the ink, and that is a measured
    ///         decision rather than a weaker test.</b> The style is
    ///         <c>MaxWidth</c> + <c>HorizontalAlignment="Center"</c>, so a tab
    ///         is arranged at its DESIRED width and the cap binds only when the
    ///         content is wider. Measured here at 3840x2160 over all nine tabs:
    ///         the widest arranges at <b>570 px</b>, and identically at 1040 and
    ///         at 1400, because every tab is a <c>ScrollViewer</c> over an
    ///         auto-sized <c>StackPanel</c> that never asks for more. So the
    ///         settings page had a SECOND fault the cap could not fix — its
    ///         content did not fill the column it was given — and that one was
    ///         fixed separately (2026-09-13): see
    ///         <see cref="TheSettingsTabs_FillTheColumn_AndFlowIntoTwoColumnsWhenWide" />.
    ///     </para>
    ///     <para>
    ///         The view is the REAL settings surface (<see cref="SettingsSurface" />
    ///         over the app's service graph), i.e. the embedded shell — there is
    ///         no floating card any more. The ink it reads is logged, never
    ///         asserted; the assertion is the cap.
    ///     </para>
    ///     <para>
    ///         What IS this lane's, and is asserted: the reading column's
    ///         ceiling at 3840x2160 goes from 27.1% of the window to
    ///         <b>36.5%</b>, and it gets there through the token and the ladder
    ///         swap with no per-view code. Break either and this fails.
    ///     </para>
    ///     <para>
    ///         <b>This case deliberately does NOT take
    ///         <see cref="RequireGate" />.</b> Everything else in this class is
    ///         an opt-in measurement harness that asserts only that a report was
    ///         produced; this is an assertion about the product, and a gate that
    ///         skips unless an env var is set is a gate nobody runs. It costs one
    ///         window and belongs to the headless-UI job like the rest of the
    ///         <c>*UiPerfSmokeTests</c> suffix.
    ///     </para>
    /// </summary>
    [Fact]
    public Task TheSettingsColumn_WidensAtTheWideBreakpoint() =>
        HeadlessSessionFixture.Dispatch(() =>
        {
            const int width = 3840;
            const int height = 2160;

            var view = BuildSettingsView();
            var window = new Window { Width = width, Height = height, Content = view };
            window.Show();
            try
            {
                var (baseCap, baseInk, _) = SettingsStrip(window, view, UiBreakpoint.Default);
                var (wideCap, wideInk, _) = SettingsStrip(window, view, UiBreakpoint.Wide);

                Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"[breakpoint] settings column at {width}x{height}: cap {baseCap:0} px "
                    + $"({baseCap / width:P1}) -> {wideCap:0} px ({wideCap / width:P1}); "
                    + $"widest tab arranges {baseInk:0} -> {wideInk:0} px"));

                Assert.True(baseInk > 0,
                    "no settings tab arranged at all — this assertion is vacuous as written");
                Assert.Equal(1040, baseCap);
                Assert.True(wideCap / width >= 0.35,
                    $"the settings reading column is capped at {wideCap:0} px, {wideCap / width:P1} of a "
                    + $"{width} px window, at the wide breakpoint. It was 1040 px / 27.1% before the "
                    + "breakpoint ladder, and the audit's P0.4 exists because that is the same pixel "
                    + "width as at 1080p.");
            }
            finally
            {
                UiBreakpointController.Apply(window, UiBreakpoint.Default);
                window.Close();
                (view as IDisposable)?.Dispose();
            }
        }, TestContext.Current.CancellationToken);

    // ── lane C: the game moments (audit §4 P0.2 / P0.3) ───────────────────

    /// <summary>
    ///     The three FIGURE screens, with the coverage floor each one now has to
    ///     clear at 3840x2160 — and the design box it grew from, measured in the
    ///     same pass so the before/after is one number against another rather
    ///     than a number against a report.
    /// </summary>
    /// <remarks>
    ///     The floors are stated per screen because the screens are not
    ///     comparable: the pause menu is a six-tile card the player reads, the
    ///     fail card is three choices, and the count-in is a numeral. Each is
    ///     the measured value with roughly a fifth of headroom, so an unrelated
    ///     content change does not fail this and a REGRESSION of the mechanism
    ///     (the Viewbox removed, its ceiling turned back into a literal, the
    ///     stretch direction flipped) falls far below it.
    /// </remarks>
    public static TheoryData<string, string, string, double> FigureScreens => new()
    {
        // screen · the Viewbox that carries it · its design box · floor @4K
        { nameof(PlayModeScreen.Countdown), "CountdownScreenFrame", "CountdownScreenRoot", 0.06 },
        { nameof(PlayModeScreen.Paused), "PauseScreenFrame", "PauseScreenRoot", 0.20 },
        { nameof(PlayModeScreen.Failed), "FailedScreenFrame", "FailedScreenRoot", 0.10 }
    };

    /// <summary>
    ///     <b>Audit §4 P0.3, asserted.</b> Countdown, Paused and Failed drew the
    ///     same postage stamp on every panel — 1.7 % / 16.9 % / 5.3 % of a 1080p
    ///     window and 0.4 % / 4.2 % / 1.3 % of a 4K one, all at <c>ratio</c>
    ///     0.25, which is arithmetic for "pixel-for-pixel identical at every
    ///     resolution".
    ///     <para>
    ///         Each screen's figure now sits in a <c>Viewbox</c>
    ///         (<c>Stretch="Uniform"</c>, <c>StretchDirection="UpOnly"</c>)
    ///         whose <c>MaxWidth</c>/<c>MaxHeight</c> is a FRACTION of the live
    ///         surface rather than a literal
    ///         (<c>Converters/ViewportFractionConverter</c>). That is what makes
    ///         the coverage a share instead of a pixel count, so this case
    ///         asserts the SAME floor at 1920x1080 and at 3840x2160 and prints
    ///         the ratio between them — a mechanism that regressed to a literal
    ///         ceiling would still pass at 1080p and fail at 4K.
    ///     </para>
    ///     <para>
    ///         <b>Why the Viewbox and not the screen root is measured.</b> A
    ///         <c>Viewbox</c> layout-scales its child through a transform: the
    ///         child keeps its DESIGN bounds and only the Viewbox's own arranged
    ///         box is the on-screen size. Reading the child would report the
    ///         pre-fix number for ever — which is precisely why the design box
    ///         is logged here as the "before".
    ///     </para>
    ///     <para>
    ///         Not gated behind <see cref="GateEnvVar" />, for the reason
    ///         <see cref="TheSettingsColumn_WidensAtTheWideBreakpoint" /> gives:
    ///         this is an assertion about the product, and a gate nobody runs is
    ///         not a gate.
    ///     </para>
    /// </summary>
    [Theory]
    [MemberData(nameof(FigureScreens))]
    public Task TheFigureScreens_GrowWithTheWindow(
        string screen, string frameName, string designName, double floorAt4K) =>
        HeadlessSessionFixture.Dispatch(() =>
        {
            var target = Enum.Parse<PlayModeScreen>(screen);
            var measured = new List<(int Width, int Height, double Coverage, Rect Frame, Rect Design)>();

            foreach (var (_, width, height) in Resolutions.Where(r => r.Width != 2560))
            {
                WithPlayMode(width, height, (view, vm) =>
                {
                    vm.CurrentScreen = target;
                    Dispatcher.UIThread.RunJobs(DispatcherPriority.Background);
                    for (var pass = 0; pass < 3; pass++) view.UpdateLayout();

                    var frame = Descendant(view, frameName);
                    var design = Descendant(view, designName);

                    var box = TransformedBounds(frame, view);
                    measured.Add((width, height, box.Width * box.Height / ((double)width * height),
                        box, design.Bounds));

                    // Nothing this screen draws may leave the window. The
                    // Viewbox grows UpOnly, so an over-generous ceiling shows up
                    // HERE first and on the smallest window first.
                    foreach (var control in frame.GetVisualDescendants().OfType<Control>())
                    {
                        if (!control.IsVisible || !control.IsEffectivelyVisible) continue;
                        if (control.Bounds is { Width: <= 1 } or { Height: <= 1 }) continue;
                        var rect = TransformedBounds(control, view);
                        Assert.True(
                            rect.Left >= -1 && rect.Top >= -1
                            && rect.Right <= width + 1 && rect.Bottom <= height + 1,
                            $"{screen}: '{Describe(control)}' arranged {Round(rect)} — outside the "
                            + $"{width}x{height} window. The figure's growth ceiling is too generous "
                            + "for this aspect; lower the fraction on the Viewbox, do not clip it.");
                    }
                });
            }

            var small = measured[0];
            var large = measured[^1];
            var ratio = small.Coverage > 0 ? large.Coverage / small.Coverage : 0;

            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"[P0.3] {screen}: design box {Round(small.Design)} -> on screen "
                + $"{Round(small.Frame)} @1080p / {Round(large.Frame)} @4K; coverage "
                + $"{small.Design.Width * small.Design.Height / (1920.0 * 1080):P1} -> {small.Coverage:P1} "
                + $"@1080p and {large.Design.Width * large.Design.Height / (3840.0 * 2160):P1} -> "
                + $"{large.Coverage:P1} @4K; ratio(4K/1080p) {ratio:0.00}"));

            Assert.True(large.Coverage >= floorAt4K,
                $"{screen} covers {large.Coverage:P1} of a 3840x2160 window; the floor is "
                + $"{floorAt4K:P1}. Its design box is {Round(large.Design)} and it is on screen at "
                + $"{Round(large.Frame)} — if those two are the same, the Viewbox is not scaling it.");
            Assert.True(small.Coverage >= floorAt4K,
                $"{screen} covers {small.Coverage:P1} of a 1920x1080 window, below the same "
                + $"{floorAt4K:P1} floor. The point of a fractional ceiling is that the floor does not "
                + "depend on the resolution.");
            Assert.True(ratio >= 0.9,
                $"{screen} covers {large.Coverage:P1} at 4K against {small.Coverage:P1} at 1080p "
                + $"(ratio {ratio:0.00}). A ratio near 0.25 is the audit's signature for a screen "
                + "frozen at one pixel size; a fractional ceiling produces 1.00.");
        }, TestContext.Current.CancellationToken);

    /// <summary>
    ///     <b>The pause menu's six tool tiles survive being scaled: they are
    ///     placed at their DRAWN positions and they still take keyboard
    ///     focus.</b>
    ///     <para>
    ///         <c>docs/agents/ui/play-mode/chrome-and-screens.md</c> § "The
    ///         START screen's hero FILLS its viewport" rejects <c>Viewbox</c>
    ///         for the launch hero partly because a CTA "would be hit-tested
    ///         where it was laid out, not where it is drawn". <b>That claim
    ///         cannot be settled from a headless session and this case does not
    ///         pretend to settle it</b>: measured 2026-09-13,
    ///         <c>GetVisualsAt</c> returns ONLY the root <c>Panel</c> for every
    ///         button in this view, inside a Viewbox and outside one alike —
    ///         there is no rendered scene to hit-test against, so a hit-test
    ///         assertion here would fail for both and prove nothing about
    ///         either.
    ///     </para>
    ///     <para>
    ///         What IS measurable, and is: the scale is real and large (~2.6x at
    ///         4K), every tile's position in the view's coordinates FOLLOWS it
    ///         (<c>TranslatePoint</c> composes the same matrix the input system
    ///         walks), and focus still lands on a tile inside the scaled
    ///         subtree. The launch hero's own reason for a
    ///         <c>LayoutTransformControl</c> stands independently of this: it
    ///         carries a 256x256 RASTER app icon, and these three screens carry
    ///         no raster asset at all.
    ///     </para>
    /// </summary>
    [Fact]
    public Task AScaledPauseMenu_PlacesAndFocusesItsToolsAtTheDrawnPositions() =>
        HeadlessSessionFixture.Dispatch(() =>
        {
            const int width = 3840;
            const int height = 2160;

            WithPlayMode(width, height, (view, vm) =>
            {
                vm.CurrentScreen = PlayModeScreen.Paused;
                Dispatcher.UIThread.RunJobs(DispatcherPriority.Background);
                for (var pass = 0; pass < 3; pass++) view.UpdateLayout();

                var frame = Descendant(view, "PauseScreenFrame");
                var card = Descendant(view, "PauseScreenRoot");
                var scale = TransformedBounds(frame, view).Width / card.Bounds.Width;

                var tools = Descendant(view, "PauseToolGrid")
                    .GetVisualDescendants().OfType<Button>().ToList();
                Assert.Equal(6, tools.Count);
                Assert.True(scale > 2,
                    $"the pause card is only scaled {scale:0.00}x on a {width}x{height} window, so every "
                    + "assertion below would hold even if the transform did nothing.");

                var focusable = 0;
                foreach (var tool in tools)
                {
                    var drawn = TransformedBounds(tool, view);
                    var laidOut = tool.Bounds;

                    Assert.True(Math.Abs(drawn.Width - (laidOut.Width * scale)) <= 1,
                        $"a pause tool is laid out {laidOut.Width:0} px wide and placed {drawn.Width:0} px "
                        + $"wide at {scale:0.00}x — the subtree's transform does not reach it, so it would "
                        + "draw and be hit where the LAYOUT put it while the card around it moved.");
                    Assert.True(drawn.Right <= width + 1 && drawn.Bottom <= height + 1,
                        $"a scaled pause tool is placed at {Round(drawn)}, outside the window.");

                    // Only the tools that are enabled AND have a command.
                    // One of the six (the tuner tile) reaches the SHELL view
                    // model through `$parent[Window]`, which this harness does
                    // not have, so its Command binds to null; measured
                    // 2026-09-13, Avalonia declines focus for that button —
                    // and it does so with or without the Viewbox. Asserting on
                    // it would report a missing view model as a transform fault.
                    if (!tool.IsEnabled || tool.Command is null) continue;
                    focusable++;
                    tool.Focus();
                    Assert.True(tool.IsFocused,
                        $"pause tool #{focusable} (enabled={tool.IsEnabled}, focusable={tool.Focusable}, "
                        + $"visible={tool.IsEffectivelyVisible}, bounds={Round(tool.Bounds)}) inside the "
                        + "scaled Viewbox cannot take keyboard focus, so the menu is unusable from a "
                        + "controller or the keyboard.");
                }

                Assert.True(focusable > 0,
                    "every pause tool was disabled, so the focus half of this case asserted nothing.");

                Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"[P0.3] pause menu at {width}x{height}: card {card.Bounds.Width:0}x{card.Bounds.Height:0} drawn at {scale:0.00}x; all 6 tools are placed at the scaled positions and take focus"));
            });
        }, TestContext.Current.CancellationToken);

    /// <summary>
    ///     <b>Audit §4 P0.2, asserted.</b> The results screen was a single column
    ///     under a literal <c>MaxWidth="1120"</c> — 28.6 % of a 4K window,
    ///     <c>ratio</c> 0.25. Two mechanisms, both asserted here because either
    ///     alone is half a fix:
    ///     <list type="number">
    ///         <item>the cap is the <c>FireColumnMaxWidthLg</c> token, so the
    ///         wide ladder moves it 1040 → 1400 with no per-view edit;</item>
    ///         <item>the per-section breakdown moves BESIDE the figures at
    ///         <c>Window.Wide</c> instead of a screen-height scroll below them —
    ///         which is what makes the content want the wider column. The
    ///         breakpoint guide's own warning is that "raising a cap is half a
    ///         fix; the content has to want the room", and the settings page is
    ///         the worked example of the half that does not.</item>
    ///     </list>
    ///     The screen's reading order at the default shape is unchanged: both
    ///     columns span the grid and stack, exactly as the single column did.
    /// </summary>
    [Fact]
    public Task TheResultsScreen_UsesTheWideColumnAndTwoColumns() =>
        HeadlessSessionFixture.Dispatch(() =>
        {
            const int width = 3840;
            const int height = 2160;

            WithPlayMode(width, height, (view, vm) =>
            {
                vm.CurrentScreen = PlayModeScreen.Results;

                var (defaultCap, defaultPrimary, defaultDetail) =
                    ResultsLayout(view, UiBreakpoint.Default);
                var (wideCap, widePrimary, wideDetail) =
                    ResultsLayout(view, UiBreakpoint.Wide);

                Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"[P0.2] results column cap {defaultCap:0} px ({defaultCap / width:P1}) -> "
                    + $"{wideCap:0} px ({wideCap / width:P1}); body stacked "
                    + $"{Round(defaultPrimary)} over {Round(defaultDetail)} -> side by side "
                    + $"{Round(widePrimary)} | {Round(wideDetail)}"));

                Assert.Equal(1040, defaultCap);
                Assert.True(wideCap / width >= 0.35,
                    $"the results column is capped at {wideCap:0} px, {wideCap / width:P1} of a {width} px "
                    + "window, at the wide breakpoint. It was a literal 1120 px — the same width as on a "
                    + "laptop — before the audit's P0.2.");

                // Default shape: the detail half is BELOW the figures, and the
                // reading order is the one the single column had.
                Assert.True(defaultDetail.Top >= defaultPrimary.Bottom - 1,
                    $"at the default shape the results detail half arranged at {Round(defaultDetail)}, "
                    + $"not below the figures at {Round(defaultPrimary)} — the unqualified markup is "
                    + "supposed to BE the old single column.");

                // Wide: beside, not below. Vertical overlap is the assertion —
                // two columns of different height still share a top edge.
                Assert.True(wideDetail.Left >= widePrimary.Right - 1,
                    $"at Window.Wide the detail half arranged at {Round(wideDetail)}, which is not to the "
                    + $"right of the figures at {Round(widePrimary)}.");
                Assert.True(wideDetail.Top < widePrimary.Bottom,
                    $"at Window.Wide the detail half starts at y={wideDetail.Y:0}, below the figures which "
                    + $"end at y={widePrimary.Bottom:0} — it moved column but not ROW, so the screen is "
                    + "still as tall as the two halves stacked.");
                Assert.True(wideDetail.Height > 1 && widePrimary.Height > 1,
                    "one of the two results columns arranged to nothing.");

                // ...and the screen's two figures step up with the shape. They
                // are what the screen is ABOUT, and at 1400 px of column they
                // were still the 44/28 px they are on a laptop.
                var stamp = Descendant(view, "GradeStamp")
                    .GetVisualDescendants().OfType<TextBlock>().First();
                var score = (TextBlock)Descendant(view, "ResultScoreText");
                Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"[P0.2] at Wide: grade stamp {stamp.FontSize:0} px, score figure "
                    + $"{score.FontSize:0}/{score.LineHeight:0} px"));

                Assert.True(stamp.FontSize > 44,
                    $"the grade stamp is still {stamp.FontSize:0} px at Window.Wide - the FontSizeStamp "
                    + "default. Its Wide selector is not matching, and the usual cause is a LOCAL "
                    + "FontSize back on the TextBlock: a local value outranks every style setter, which "
                    + "is exactly how this shipped broken the first time.");
                Assert.True(score.FontSize > 28,
                    $"the score figure is still {score.FontSize:0} px at Window.Wide.");
                Assert.True(score.LineHeight > score.FontSize,
                    $"the score figure is {score.FontSize:0} px inside a {score.LineHeight:0} px line box "
                    + "- TextBlock.StatFigure pins a line box, and a figure raised inside a frozen one "
                    + "renders as a clipped middle band.");
            });
        }, TestContext.Current.CancellationToken);

    /// <summary>
    ///     <b>A <c>Window</c>-rooted breakpoint selector really does reach the
    ///     shell's root, which is a SUBCLASS.</b>
    ///     <para>
    ///         The selector half of the breakpoint mechanism
    ///         (<c>docs/agents/ui/fire-design-system.md</c> §Breakpoints,
    ///         <c>Selector="Window.Wide StackPanel#LaunchColumn"</c>) rests on a
    ///         type selector naming <c>Window</c> while the app's root is
    ///         <c>Views.MainWindow : Window</c> — and every headless test in the
    ///         repo, this file included, puts its content in a PLAIN
    ///         <c>Window</c>, where the question cannot come up. Avalonia's own
    ///         documentation says a type selector matches the exact type and
    ///         <c>:is()</c> is the form that matches derived ones, which would
    ///         make the guide's example green in every test and dead in the
    ///         shipped shell.
    ///     </para>
    ///     <para>
    ///         <b>Measured here, and it is NOT what that reading predicts: both
    ///         forms match a subclass.</b> So the guide's example is correct as
    ///         written. PlayModeView's own breakpoint styles still say
    ///         <c>:is(Window)</c> — belt and braces for a rule whose failure
    ///         mode is silent and ships — and this case pins both halves, so the
    ///         day an Avalonia upgrade tightens the exact-type rule it names
    ///         which form went dead instead of a screen quietly losing its wide
    ///         layout.
    ///     </para>
    /// </summary>
    [Fact]
    public Task ABreakpointSelector_ReachesAWindowSubclass() =>
        HeadlessSessionFixture.Dispatch(() =>
        {
            var probe = new Border { Name = "BreakpointProbe" };
            var derived = new BreakpointProbeWindow { Width = 800, Height = 600, Content = probe };
            derived.Styles.Add(new Style(x => x.OfType<Window>().Class("Wide")
                    .Descendant().OfType<Border>().Name("BreakpointProbe"))
            { Setters = { new Setter(Layoutable.WidthProperty, 111d) } });
            derived.Styles.Add(new Style(x => x.Is<Window>().Class("Wide")
                    .Descendant().OfType<Border>().Name("BreakpointProbe"))
            { Setters = { new Setter(Layoutable.HeightProperty, 222d) } });

            derived.Show();
            try
            {
                Dispatcher.UIThread.RunJobs(DispatcherPriority.Background);
                derived.UpdateLayout();
                Assert.True(double.IsNaN(probe.Width) && double.IsNaN(probe.Height),
                    "the probe was styled with NO breakpoint class on the root — this case would then "
                    + "pass whatever the selectors did.");

                derived.Classes.Add("Wide");
                Dispatcher.UIThread.RunJobs(DispatcherPriority.Background);
                derived.UpdateLayout();

                Console.WriteLine(
                    $"[selector] on a {derived.GetType().Name}: `Window.Wide` -> Width={probe.Width}, "
                    + $"`:is(Window).Wide` -> Height={probe.Height}");

                Assert.Equal(222d, probe.Height);
                Assert.Equal(111d, probe.Width);
            }
            finally { derived.Close(); }
        }, TestContext.Current.CancellationToken);

    /// <summary>A <see cref="Window" /> SUBCLASS, which is what the shell's root
    /// is. The case above is meaningless against a plain one.</summary>
    private sealed class BreakpointProbeWindow : Window;

    /// <summary>
    ///     <b>Get Ready, the fourth screen of audit §4 P0.3 — and the one that
    ///     does NOT get a Viewbox.</b>
    ///     <para>
    ///         It is grouped with Countdown/Paused/Failed in the brief because
    ///         they are the four pre-run moments, but it is not the same shape:
    ///         the other three are a figure centred in an empty window, while
    ///         Get Ready was rebuilt in 0.48 as a FULL-BLEED four-region layout
    ///         (header / 404 px rail / <c>*</c> centre / footer) precisely
    ///         because it is the densest screen in the app. Scaling a screen
    ///         that already fills the window is not a fix, it is a zoom — the
    ///         tuner's six string rows and the two sound cards would get bigger
    ///         while the layout kept its 1080p proportions, and the meters would
    ///         leave the viewport.
    ///     </para>
    ///     <para>
    ///         This case is the MEASUREMENT that says so, and it is an
    ///         assertion rather than a note in a guide, because "screen X is
    ///         already fine" is exactly the kind of claim that rots. It pins the
    ///         centre column filling its row and the whole screen covering the
    ///         window at BOTH resolutions — if Get Ready ever regresses to a
    ///         centred capped column (which is what it was until 0.48), this
    ///         fails and the Viewbox question is reopened with a number behind
    ///         it.
    ///     </para>
    /// </summary>
    [Fact]
    public Task GetReady_AlreadyFillsTheWindow_AndNeedsNoViewbox() =>
        HeadlessSessionFixture.Dispatch(() =>
        {
            var measured = new List<(int Width, int Height, double Coverage, double CentreShare)>();

            foreach (var (_, width, height) in Resolutions.Where(r => r.Width != 2560))
            {
                WithPlayMode(width, height, (view, vm) =>
                {
                    vm.CurrentScreen = PlayModeScreen.TuningCheck;
                    Dispatcher.UIThread.RunJobs(DispatcherPriority.Background);
                    for (var pass = 0; pass < 3; pass++) view.UpdateLayout();

                    var root = TransformedBounds(Descendant(view, "TuningCheckScreenRoot"), view);
                    var centre = TransformedBounds(Descendant(view, "GetReadyCentre"), view);
                    measured.Add((width, height,
                        root.Width * root.Height / ((double)width * height),
                        centre.Width / width));

                });
            }

            var small = measured[0];
            var large = measured[^1];
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"[P0.3] GetReady (no Viewbox, full-bleed by design): coverage {small.Coverage:P1} "
                + $"@1080p / {large.Coverage:P1} @4K; the flex centre takes {small.CentreShare:P1} "
                + $"/ {large.CentreShare:P1} of the width"));

            foreach (var m in measured)
            {
                Assert.True(m.Coverage >= 0.90,
                    $"Get Ready covers {m.Coverage:P1} of a {m.Width}x{m.Height} window. It is a "
                    + "FULL-BLEED four-region screen; anything near the audit's centred-column numbers "
                    + "means it regressed to one, and then it needs the P0.3 treatment after all.");
                Assert.True(m.CentreShare >= 0.40,
                    $"the Get Ready centre column takes {m.CentreShare:P1} of a {m.Width} px window. "
                    + "The rails are fixed and the centre is the `*` — a shrinking share is the fixed "
                    + "rails eating the screen, which IS the audit's complaint in another form.");
            }
        }, TestContext.Current.CancellationToken);

    /// <summary>
    ///     The results column's cap and the arranged boxes of its two halves at a
    ///     given breakpoint.
    /// </summary>
    private static (double Cap, Rect Primary, Rect Detail) ResultsLayout(
        PlayModeView view, UiBreakpoint breakpoint)
    {
        UiBreakpointController.Apply((Window)TopLevel.GetTopLevel(view)!, breakpoint);
        Dispatcher.UIThread.RunJobs(DispatcherPriority.Background);
        for (var pass = 0; pass < 3; pass++) view.UpdateLayout();

        var column = Descendant(view, "ResultsColumn");
        return (column.MaxWidth,
            TransformedBounds(Descendant(view, "ResultsPrimaryColumn"), view),
            TransformedBounds(Descendant(view, "ResultsDetailColumn"), view));
    }

    /// <summary>
    ///     Builds the real <see cref="PlayModeView" /> over a real profile and
    ///     practice store at one window size, runs <paramref name="body" /> and
    ///     closes it. Mirrors <c>PlayModeScreensUiPerfSmokeTests.WithView</c>;
    ///     the one difference is that the window size is the subject here.
    /// </summary>
    private void WithPlayMode(int width, int height, Action<PlayModeView, PlayModeViewModel> body)
    {
        Directory.CreateDirectory(_dir);
        var stamp = Guid.NewGuid().ToString("N");
        var profiles = new PlayerProfileService(FakeLocalization.English(), logger: null,
            filePath: Path.Combine(_dir, $"profiles-{stamp}.json"), defaultProfileName: "Tester");
        var store = new PracticeProfileService(profiles, logger: null,
            legacyFilePath: Path.Combine(_dir, $"practice-{stamp}.json"));
        var vm = TestRigBuilders.PlayMode(profiles, new GameplayStatsService(store),
            new LeaderboardService(profiles), new ProjectState(), toneRig: HeadlessToneRig.FromSession(new ProjectState()), localization: LocalizationService.Instance);

        var view = new PlayModeView { DataContext = vm };
        var window = new Window { Width = width, Height = height, Content = view };
        window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs(DispatcherPriority.Background);
            window.UpdateLayout();
            body(view, vm);
        }
        finally
        {
            UiBreakpointController.Apply(window, UiBreakpoint.Default);
            // Results starts a staged reveal; leaving the screen cancels it
            // rather than letting the chain outlive the window.
            vm.CurrentScreen = PlayModeScreen.SongSelect;
            window.Close();
        }
    }

    /// <summary>
    ///     A named control anywhere under the view, INCLUDING inside a deferred
    ///     screen's <c>ContentTemplate</c> — <c>FindControl</c> cannot cross that
    ///     namescope, which is the trap
    ///     <c>docs/agents/ui/play-mode.md</c> records for these screens.
    /// </summary>
    private static Control Descendant(Control view, string name)
    {
        var found = view.GetVisualDescendants().OfType<Control>()
            .FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.Ordinal));
        Assert.True(found is not null,
            $"'{name}' is not in the laid-out tree — it was renamed, removed, or its screen never "
            + "became visible (an invisible Avalonia element runs no MeasureCore at all).");
        return found!;
    }

    /// <summary>
    ///     Arranged bounds in <paramref name="root" />'s coordinates, THROUGH any
    ///     render transform on the way up.
    ///     <para>
    ///         <c>Bounds.Size</c> is the layout size and a <c>Viewbox</c> scales
    ///         its child with a transform, so composing the origin with an
    ///         untransformed size — which is what this file did before lane C —
    ///         reports a scaled figure at its design size and the whole P0.3 fix
    ///         as a no-op.
    ///     </para>
    /// </summary>
    private static Rect TransformedBounds(Control control, Control root)
    {
        var topLeft = control.TranslatePoint(new Point(0, 0), root);
        var bottomRight = control.TranslatePoint(
            new Point(control.Bounds.Width, control.Bounds.Height), root);
        return topLeft is { } tl && bottomRight is { } br ? new Rect(tl, br) : default;
    }

    /// <summary>
    ///     <b>Audit P0.4, the OTHER half — lane D.</b> Lane B raised the
    ///     ceiling; this asserts the page USES it.
    ///     <para>
    ///         The style on the shell's tab host is
    ///         <c>Stretch</c> rather than <c>Center</c>, so a tab is arranged at
    ///         <c>min(slot, MaxWidth)</c> instead of at the ~570 px its
    ///         auto-sized <c>StackPanel</c> desires, and five of the nine tabs
    ///         replace that panel with a <c>MasonryPanel</c> that goes to two
    ///         columns above the wide breakpoint. Both halves are asserted here
    ///         at 3840x2160, and both numbers are logged.
    ///     </para>
    ///     <para>
    ///         Like <see cref="TheSettingsColumn_WidensAtTheWideBreakpoint" />
    ///         this does NOT take <see cref="RequireGate" />: it is an assertion
    ///         about the product, not a measurement run.
    ///     </para>
    /// </summary>
    [Fact]
    public Task TheSettingsTabs_FillTheColumn_AndFlowIntoTwoColumnsWhenWide() =>
        HeadlessSessionFixture.Dispatch(() =>
        {
            const int width = 3840;
            const int height = 2160;

            var view = BuildSettingsView();
            var window = new Window { Width = width, Height = height, Content = view };
            window.Show();
            try
            {
                var (cap, arranged, narrowest) = SettingsStrip(window, view, UiBreakpoint.Wide);
                var measurement = MeasureSurface(view, "Settings/SettingsShellView", "3840x2160", width, height);

                var columns = SettingsFlowColumns(window, view);
                var flow = string.Join(", ", columns.Select(c => $"{c.Key}={c.Value}"));
                var slowest = SlowestTabSwitchMs(window, view);

                Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"[P0.4] settings at {width}x{height} (Wide): cap {cap:0} px, widest tab arranges "
                    + $"{arranged:0} px, narrowest {narrowest:0} px ({narrowest / cap:P1} of the "
                    + $"column); primary panel "
                    + $"{measurement.PrimaryPanel} {measurement.PrimaryPanelBox} = "
                    + $"{measurement.PrimaryPanelCoverage:P1} of the window; ink "
                    + $"{measurement.InkCoverage:P1}; flow columns: {flow}; slowest steady-state tab "
                    + $"switch {slowest:0.0} ms (budget 150; LOGGED, never asserted)"));

                Assert.True(narrowest >= cap * 0.9, string.Create(CultureInfo.InvariantCulture,
                    $"the NARROWEST settings tab arranges at {narrowest:0} px inside a {cap:0} px column. "
                    + $"`MaxWidth` + `HorizontalAlignment=Center` is a CEILING, not a fill — the tab "
                    + $"roots must be Stretch (SettingsShellView.axaml's tab-host style)."));

                Assert.True(measurement.PrimaryPanelCoverage >= 0.45, string.Create(CultureInfo.InvariantCulture,
                    $"the settings page's primary panel covers {measurement.PrimaryPanelCoverage:P1} of a "
                    + $"{width}x{height} window. The audit measured the pre-fix page at `ratio` 0.25."));

                // The five opt-in tabs flow; the four wide-control tabs do not.
                // Asserting only the first half would pass on a change that made
                // the keybind table four chord columns of 230 px.
                Assert.Equal(2, columns["GeneralSettingsView"]);
                Assert.Equal(2, columns["EditorSettingsView"]);
                Assert.Equal(2, columns["FilesSettingsView"]);
                // Play is one group: two columns of it is a column and an empty gutter.
                Assert.DoesNotContain("PlaySettingsView", columns.Keys);
                Assert.Equal(2, columns["GraphicsSettingsView"]);
                Assert.Equal(2, columns["AudioSettingsView"]);
                // Stem models is ONE column since the Graphite review: its model list leads
                // and reads as a table, which two masonry columns would split.
                Assert.DoesNotContain("StemModelsSettingsView", columns.Keys);
                Assert.Equal(2, columns["UpdatesSettingsView"]);
                Assert.Equal(2, columns["AiSettingsView"]);
                Assert.DoesNotContain("KeybindsSettingsView", columns.Keys);
                Assert.DoesNotContain("AppearanceSettingsView", columns.Keys);
                // One card: two columns of it is a column and an empty gutter.
                Assert.DoesNotContain("Tone3000SettingsView", columns.Keys);
            }
            finally
            {
                UiBreakpointController.Apply(window, UiBreakpoint.Default);
                window.Close();
                (view as IDisposable)?.Dispose();
            }
        }, TestContext.Current.CancellationToken);

    /// <summary>
    ///     The NEGATIVE half of P0.4: at 1920x1080 the page is the shape it has
    ///     always been. The breakpoint is Default there, so every
    ///     <c>MasonryPanel</c> is a one-column vertical stack, and nothing is
    ///     arranged outside the window in any tab.
    /// </summary>
    [Fact]
    public Task At1080p_TheSettingsPage_KeepsItsSingleColumnAndDoesNotOverflow() =>
        HeadlessSessionFixture.Dispatch(() =>
        {
            const int width = 1920;
            const int height = 1080;

            var view = BuildSettingsView();
            var window = new Window { Width = width, Height = height, Content = view };
            window.Show();
            try
            {
                var (cap, arranged, narrowest) = SettingsStrip(window, view, UiBreakpoint.Default);
                var measurement = MeasureSurface(view, "Settings/SettingsShellView", "1920x1080", width, height);
                var columns = SettingsFlowColumns(window, view);
                var flow = string.Join(", ", columns.Select(c => $"{c.Key}={c.Value}"));
                // Content leaves only: `measurement.Overflow` also counts the
                // shell's decorative full-bleed `Path`, which is meant to bleed.
                var spilled = view.GetVisualDescendants().OfType<Control>()
                    .Where(c => c.IsVisible && c.IsEffectivelyVisible && IsOverflowSubject(c)
                                && c.Bounds.Width > 1 && c.Bounds.Height > 1)
                    .Select(c => (c, p: c.TranslatePoint(new Point(0, 0), view)))
                    .Where(t => t.p is { } o
                                && (o.X < -1 || o.X + t.c.Bounds.Width > width + 1))
                    .Select(t => Describe(t.c))
                    .Distinct(StringComparer.Ordinal)
                    .ToList();
                var spills = string.Join("; ", spilled.Take(6));

                Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"[P0.4] settings at {width}x{height} (Default): cap {cap:0} px, widest tab arranges "
                    + $"{arranged:0} px, narrowest {narrowest:0} px; primary panel "
                    + $"{measurement.PrimaryPanelCoverage:P1}; "
                    + $"{spilled.Count} content overflow; flow columns: {flow}"));

                Assert.NotEmpty(columns);
                Assert.All(columns, c => Assert.Equal(1, c.Value));
                Assert.True(spilled.Count == 0, string.Create(CultureInfo.InvariantCulture,
                    $"{spilled.Count} settings controls arranged outside a {width}x{height} window: "
                    + $"{spills}"));
            }
            finally
            {
                UiBreakpointController.Apply(window, UiBreakpoint.Default);
                window.Close();
                (view as IDisposable)?.Dispose();
            }
        }, TestContext.Current.CancellationToken);

    /// <summary>
    ///     The Settings tab exactly as the shell hosts it: the root
    ///     <see cref="ScreenHost" /> showing the settings shell screen and every
    ///     tab in its tab host, over the real service graph (<see cref="SettingsSurface" />).
    ///     Dispose it with the window: the host owns a scope.
    /// </summary>
    private static Control BuildSettingsView()
    {
        var services = App.Services
                       ?? throw new InvalidOperationException(
                           "App.Services is null — the headless app session did not boot.");
        return SettingsSurface.Create(services);
    }

    /// <summary>The shell's rail rows, in order — selecting one is the tab switch.</summary>
    private static IReadOnlyList<ISettingsRailItem> RailItems(Control view)
    {
        var shell = ((ScreenHost)view).ViewOf(typeof(ISettingsShellScreen))?.DataContext as ISettingsShellScreen;
        Assert.True(shell is not null, "the settings ScreenHost shows no settings shell — this measures nothing");
        return [.. shell!.Sections.SelectMany(s => s.Items)];
    }

    /// <summary>Every tab body under the tab host.</summary>
    private static IEnumerable<UserControl> TabBodies(Control view) =>
        view.GetVisualDescendants().OfType<ScreenTabHost>()
            .SelectMany(host => host.Children.OfType<UserControl>());

    private static void Settle(Window window)
    {
        Dispatcher.UIThread.RunJobs(DispatcherPriority.Background);
        for (var pass = 0; pass < 3; pass++) window.UpdateLayout();
    }

    /// <summary>The resolved <c>ColumnCount</c> of every tab that opted into the
    /// two-column flow, keyed by the tab type's name. A tab that did not opt in
    /// is ABSENT rather than 1 — which is what lets the caller assert the
    /// opt-OUTs by their absence.</summary>
    private static Dictionary<string, int> SettingsFlowColumns(Window window, Control view)
    {
        var found = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var item in RailItems(view))
        {
            item.SelectCommand.Execute(null);
            Settle(window);

            foreach (var panel in view.GetVisualDescendants().OfType<MasonryPanel>())
            {
                if (!panel.Classes.Contains("SettingsFlow")) continue;
                var owner = panel.GetVisualAncestors().OfType<UserControl>().FirstOrDefault();
                if (owner is null) continue;
                found[owner.GetType().Name] = panel.ColumnCount;
            }
        }

        return found;
    }

    /// <summary>
    ///     The worst per-tab switch cost once every tab has been realized once —
    ///     the number the UI responsiveness budget (150 ms, and it should be
    ///     under 20) is about. Settings is a PREWARMED surface, so the first
    ///     measure of each hidden tab is paid by the shell's prewarm drain and
    ///     this is the cost the user sees. LOGGED, never asserted: <c>AGENTS.md</c>
    ///     bans a wall-clock assertion in a suite that runs beside other work.
    /// </summary>
    private static double SlowestTabSwitchMs(Window window, Control view)
    {
        var items = RailItems(view);
        foreach (var item in items)
        {
            item.SelectCommand.Execute(null);
            Settle(window);
        }

        var worst = 0.0;
        foreach (var item in items)
        {
            var started = Stopwatch.GetTimestamp();
            item.SelectCommand.Execute(null);
            Settle(window);
            worst = Math.Max(worst, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }

        return worst;
    }

    private static Control? ByName(Control root, string name) =>
        root.Name == name
            ? root
            : root.GetVisualDescendants().OfType<Control>().FirstOrDefault(c => c.Name == name);

    /// <summary>
    ///     The cap applied to the settings tab panels, and the widest one
    ///     actually arranged, at a given breakpoint.
    ///     <para>
    ///         All nine tabs are swept, and that is not thoroughness — measuring
    ///         the DEFAULT tab alone would report one panel's number as the
    ///         page's.
    ///     </para>
    /// </summary>
    private static (double Cap, double Arranged, double Narrowest) SettingsStrip(
        Window window, Control view, UiBreakpoint breakpoint)
    {
        UiBreakpointController.Apply(window, breakpoint);

        var cap = 0.0;
        var arranged = 0.0;
        // The NARROWEST visible tab is what proves the tab roots are Stretch.
        // The widest cannot: the tabs that flow their groups through a
        // `MasonryPanel` DESIRE the whole slot (the panel returns the available
        // width from MeasureOverride), so they fill under `Center` too — and a
        // max over nine tabs then reports the page as fixed when four of them
        // are still 570 px islands in a 1400 px column.
        var narrowest = double.MaxValue;
        var items = RailItems(view);
        Assert.True(items.Count >= 9, $"the settings rail lists {items.Count} tabs — the sweep would miss some");

        foreach (var item in items)
        {
            item.SelectCommand.Execute(null);
            Settle(window);

            foreach (var tab in TabBodies(view))
            {
                if (double.IsFinite(tab.MaxWidth)) cap = Math.Max(cap, tab.MaxWidth);
                if (!tab.IsVisible || !tab.IsEffectivelyVisible) continue;
                arranged = Math.Max(arranged, tab.Bounds.Width);
                narrowest = Math.Min(narrowest, tab.Bounds.Width);
            }
        }

        return (cap, arranged, double.IsFinite(narrowest) && narrowest < double.MaxValue ? narrowest : 0);
    }

    // ── measurement ───────────────────────────────────────────────────────

    /// <summary>Walks the arranged tree ONCE and derives every number the audit
    /// reports. The root is the view, not the window: a headless
    /// <c>Window</c> measured directly reports an invalid size.</summary>
    private static ScreenMeasurement MeasureSurface(
        Control root, string surface, string label, int width, int height)
    {
        var clientArea = (double)width * height;
        var inkLeft = double.MaxValue;
        var inkTop = double.MaxValue;
        var inkRight = double.MinValue;
        var inkBottom = double.MinValue;

        Control? primaryPanel = null;
        var primaryArea = 0.0;
        var primaryBounds = default(Rect);

        var fixedOffenders = new List<Offender>();
        var smallText = new List<Offender>();
        var overflow = new List<Offender>();
        var visibleControls = 0;

        foreach (var visual in root.GetVisualDescendants())
        {
            if (visual is not Control control) continue;
            if (!control.IsVisible || !control.IsEffectivelyVisible) continue;

            var bounds = control.Bounds;
            if (bounds.Width <= 1 || bounds.Height <= 1) continue;

            // THROUGH the transforms, not just translated by them: a Viewbox
            // scales its child, so composing a transformed origin with the
            // child's LAYOUT size reports a figure screen at its design size
            // and every P0.3 fix as a no-op.
            var windowRect = TransformedBounds(control, root);
            if (windowRect == default) continue;
            visibleControls++;

            if (IsContentLeaf(control))
            {
                inkLeft = Math.Min(inkLeft, windowRect.Left);
                inkTop = Math.Min(inkTop, windowRect.Top);
                inkRight = Math.Max(inkRight, windowRect.Right);
                inkBottom = Math.Max(inkBottom, windowRect.Bottom);
            }

            // The largest visible panel that is NOT full-bleed: a backdrop
            // covers the whole client area and is never "the content".
            // `Viewbox` joins the candidates because it is now what carries a
            // figure screen, and it is the only element whose arranged box IS
            // that screen's on-screen size.
            if (control is Border or Panel or Viewbox)
            {
                var area = windowRect.Width * windowRect.Height;
                if (area > primaryArea && area <= clientArea * 0.95)
                {
                    primaryArea = area;
                    primaryPanel = control;
                    primaryBounds = windowRect;
                }
            }

            CollectFixedSize(control, windowRect, width, height, fixedOffenders);

            if (control is TextBlock text && text.FontSize < MinFontSize)
            {
                smallText.Add(new Offender(
                    Describe(control), "FontSize",
                    text.FontSize.ToString("0.##", CultureInfo.InvariantCulture),
                    Round(windowRect)));
            }

            if (windowRect.Right > width + 1 || windowRect.Bottom > height + 1
                || windowRect.Left < -1 || windowRect.Top < -1)
            {
                overflow.Add(new Offender(
                    Describe(control), "ArrangedOutsideWindow",
                    $"{width}x{height}", Round(windowRect)));
            }
        }

        var ink = inkRight > inkLeft && inkBottom > inkTop
            ? new Rect(inkLeft, inkTop, inkRight - inkLeft, inkBottom - inkTop)
            : default;

        return new ScreenMeasurement
        {
            Surface = surface,
            Resolution = label,
            WindowWidth = width,
            WindowHeight = height,
            VisibleControls = visibleControls,
            InkBox = Round(ink),
            InkCoverage = clientArea > 0 ? ink.Width * ink.Height / clientArea : 0,
            InkWidthFraction = width > 0 ? ink.Width / width : 0,
            InkHeightFraction = height > 0 ? ink.Height / height : 0,
            PrimaryPanel = primaryPanel is null ? null : Describe(primaryPanel),
            PrimaryPanelBox = Round(primaryBounds),
            PrimaryPanelCoverage = clientArea > 0 ? primaryArea / clientArea : 0,
            FixedSizeOffenders = Dedupe(fixedOffenders),
            SmallText = Dedupe(smallText),
            Overflow = Dedupe(overflow)
        };
    }

    /// <summary>A literal size on a control that is far smaller than the window
    /// in that axis is the shape the audit is hunting: it cannot grow, so the
    /// screen keeps the 1080p layout on a 4K panel.</summary>
    private static void CollectFixedSize(
        Control control, Rect bounds, int width, int height, List<Offender> into)
    {
        Check(control.Width, "Width", bounds.Width, width);
        Check(control.MaxWidth, "MaxWidth", bounds.Width, width);
        Check(control.Height, "Height", bounds.Height, height);
        Check(control.MaxHeight, "MaxHeight", bounds.Height, height);

        void Check(double declared, string property, double arranged, int windowExtent)
        {
            if (double.IsNaN(declared) || double.IsInfinity(declared)) return;
            if (arranged >= windowExtent * FixedSizeFraction) return;
            into.Add(new Offender(
                Describe(control), property,
                declared.ToString("0.##", CultureInfo.InvariantCulture),
                Round(bounds)));
        }
    }

    /// <summary>
    ///     What "nothing overflows" is a claim about: text and the interactive
    ///     primitives. A <see cref="Shape" /> is deliberately excluded — the
    ///     Fire screens draw their vignette as an oversized <c>Path</c> inside a
    ///     <c>ViewboxContainer</c> (measured 2010 px wide on a 1920 px window),
    ///     and a backdrop that bleeds is the design, not a fault.
    /// </summary>
    private static bool IsOverflowSubject(Control control) =>
        IsContentLeaf(control) && control is not Shape;

    /// <summary>Text, glyphs, pictures, shapes and the interactive primitives —
    /// the things a user actually looks at. A container is excluded on purpose:
    /// the ink box must not be inflated by a stretched wrapper.</summary>
    private static bool IsContentLeaf(Control control) =>
        control is TextBlock or TextBox or Image or PathIcon or Shape
            or Button or ToggleButton or CheckBox or RadioButton
            or ComboBox or Slider or ProgressBar or NumericUpDown;

    // ── reporting ─────────────────────────────────────────────────────────

    /// <summary>One stamped directory per PROCESS, so the captures the first
    /// case writes and the report the last case writes land together.</summary>
    private static readonly Lazy<string> ArtifactDir = new(() =>
    {
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var dir = Path.Combine(TestPaths.RepoRoot(), "artifacts", "ui-audit", stamp);
        Directory.CreateDirectory(dir);
        return dir;
    });

    private static void WriteReport()
    {
        var dir = ArtifactDir.Value;

        var ordered = Measurements
            .OrderBy(m => m.Surface, StringComparer.Ordinal)
            .ThenBy(m => m.WindowWidth)
            .ToList();

        File.WriteAllText(Path.Combine(dir, "audit.json"),
            JsonSerializer.Serialize(ordered, new JsonSerializerOptions { WriteIndented = true }));
        File.WriteAllText(Path.Combine(dir, "audit.md"), BuildMarkdown(ordered), Encoding.UTF8);
    }

    private static string BuildMarkdown(IReadOnlyList<ScreenMeasurement> ordered)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# UI resolution audit");
        sb.AppendLine();
        sb.AppendLine(CultureInfo.InvariantCulture,
            $"Generated {DateTime.Now:yyyy-MM-dd HH:mm}, {ordered.Count} measurements, "
            + $"{ordered.Select(m => m.Surface).Distinct().Count()} surfaces.");
        sb.AppendLine();
        sb.AppendLine("Coverage is the INK BOX — the union of every visible content leaf's");
        sb.AppendLine("arranged bounds — over the client area. Backdrops are not content.");
        sb.AppendLine();
        sb.AppendLine("## Coverage: screen x resolution");
        sb.AppendLine();
        sb.AppendLine("| Surface | 1920x1080 | 2560x1440 | 3840x2160 | ink w-frac @4K | ink h-frac @4K |");
        sb.AppendLine("|---|---|---|---|---|---|");

        foreach (var group in ordered.GroupBy(m => m.Surface).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var byRes = group.ToDictionary(m => m.Resolution, StringComparer.Ordinal);
            var uhd = byRes.GetValueOrDefault("3840x2160");
            sb.AppendLine(CultureInfo.InvariantCulture,
                $"| {group.Key} | {Pct(byRes, "1920x1080")} | {Pct(byRes, "2560x1440")} "
                + $"| {Pct(byRes, "3840x2160")} | {uhd?.InkWidthFraction:P0} | {uhd?.InkHeightFraction:P0} |");
        }

        sb.AppendLine();
        sb.AppendLine("## Offenders at 3840x2160");
        sb.AppendLine();
        foreach (var m in ordered.Where(m => m.Resolution == "3840x2160")
                     .OrderBy(m => m.InkCoverage))
        {
            if (m.FixedSizeOffenders.Count == 0 && m.SmallText.Count == 0
                && m.Overflow.Count == 0 && m.Error is null)
            {
                continue;
            }

            sb.AppendLine(CultureInfo.InvariantCulture,
                $"### {m.Surface} — ink coverage {m.InkCoverage:P1}");
            if (m.Error is not null) sb.AppendLine(CultureInfo.InvariantCulture, $"- FAILED: {m.Error}");
            Emit(sb, "fixed size under 40% of the window", m.FixedSizeOffenders);
            Emit(sb, "text below 12 px", m.SmallText);
            Emit(sb, "arranged outside the window", m.Overflow);
            sb.AppendLine();
        }

        return sb.ToString();

        static string Pct(Dictionary<string, ScreenMeasurement> byRes, string key) =>
            byRes.TryGetValue(key, out var m)
                ? m.Error is null ? m.InkCoverage.ToString("P1", CultureInfo.InvariantCulture) : "ERR"
                : "-";
    }

    private static void Emit(StringBuilder sb, string what, IReadOnlyList<Offender> offenders)
    {
        if (offenders.Count == 0) return;
        sb.AppendLine(CultureInfo.InvariantCulture, $"- {offenders.Count} {what}:");
        foreach (var o in offenders.Take(12))
        {
            sb.AppendLine(CultureInfo.InvariantCulture,
                $"  - `{o.Control}` {o.Property}={o.Value} arranged {o.Bounds}");
        }
    }

    // ── capture ───────────────────────────────────────────────────────────

    /// <summary>A PNG per surface per resolution, but only when the session
    /// booted with real drawing — headless drawing returns no frame.</summary>
    private static void Capture(Window window, string name)
    {
        if (!UiSweepRunner.CaptureEnabled) return;
        var dir = Path.Combine(ArtifactDir.Value, "captures");
        Directory.CreateDirectory(dir);
        using var frame = window.CaptureRenderedFrame();
        frame?.Save(Path.Combine(dir, name + ".png"), new PngBitmapEncoderOptions());
    }

    // ── plumbing ──────────────────────────────────────────────────────────

    private static void RequireGate() =>
        Assert.SkipWhen(Environment.GetEnvironmentVariable(GateEnvVar) != "1",
            $"{GateEnvVar}!=1 — the resolution audit is an opt-in measurement harness.");

    private static void OpenIfDialog(object viewModel) => SetBool(viewModel, "IsOpen", true);

    /// <summary>Sets a writable public bool, if the view model has one. A view
    /// model that does not is left alone — several surfaces are always open.</summary>
    private static void SetBool(object viewModel, string name, bool value)
    {
        var property = viewModel.GetType().GetProperty(name,
            BindingFlags.Public | BindingFlags.Instance);
        if (property?.PropertyType != typeof(bool) || !property.CanWrite) return;
        try { property.SetValue(viewModel, value); }
        catch (TargetInvocationException) { /* an opener that needs state we do not have */ }
    }

    private static string GroupOf(Type viewType) =>
        UiSourceTree.UiNamespace(viewType.Namespace)?.Replace("DotCharter.UI.Views", "", StringComparison.Ordinal)
            .TrimStart('.') is { Length: > 0 } group
            ? group
            : "Shell";

    private static string Describe(Control control) =>
        control.Name is { Length: > 0 } name
            ? $"{control.GetType().Name}#{name}"
            : control.GetType().Name;

    private static string Round(Rect r) =>
        string.Create(CultureInfo.InvariantCulture,
            $"{r.X:0},{r.Y:0} {r.Width:0}x{r.Height:0}");

    private static IReadOnlyList<Offender> Dedupe(List<Offender> offenders) =>
        [.. offenders.DistinctBy(o => (o.Control, o.Property, o.Value))];

    public void Dispose() => BestEffortDelete.Directory(_dir);

    // ── records ───────────────────────────────────────────────────────────

    /// <summary>One control the audit is reporting, with the arranged rectangle
    /// that made it a finding.</summary>
    private sealed record Offender(string Control, string Property, string Value, string Bounds);

    /// <summary>One surface at one resolution.</summary>
    [SuppressMessage("Performance", "CA1812", Justification = "Serialized report record.")]
    private sealed class ScreenMeasurement
    {
        public required string Surface { get; init; }
        public required string Resolution { get; init; }
        public required int WindowWidth { get; init; }
        public required int WindowHeight { get; init; }
        public int VisibleControls { get; init; }
        public string InkBox { get; init; } = "";
        public double InkCoverage { get; init; }
        public double InkWidthFraction { get; init; }
        public double InkHeightFraction { get; init; }
        public string? PrimaryPanel { get; init; }
        public string PrimaryPanelBox { get; init; } = "";
        public double PrimaryPanelCoverage { get; init; }
        public IReadOnlyList<Offender> FixedSizeOffenders { get; init; } = [];
        public IReadOnlyList<Offender> SmallText { get; init; } = [];
        public IReadOnlyList<Offender> Overflow { get; init; } = [];
        public string? Error { get; init; }

        public static ScreenMeasurement Failed(
            string surface, string label, int width, int height, string error) =>
            new()
            {
                Surface = surface,
                Resolution = label,
                WindowWidth = width,
                WindowHeight = height,
                Error = error
            };
    }

    // ══ lane F ═══════════════════════════════════════════════════════════════
    //
    //  THE SHELL CHROME, THE LAN SURFACES AND THE COMMAND PALETTE.
    //
    //  These three groups are NOT the audit's usual finding. Lanes B/C/D were
    //  all the same shape — a centred column under a literal `MaxWidth`, ratio
    //  0.25, pixel-for-pixel identical at 1080p and 4K. The editor's rails are
    //  pinned to the window's EDGES and already span it; the LAN board is a `*`
    //  column beside a rail; the HUD strips are corner-anchored over the
    //  highway. What was wrong with all of them is DENSITY: 10.5-13 px type and
    //  14 px glyphs, correct at 1920 and a row of specks at 3840.
    //
    //  So this lane's two mechanisms are deliberately the ones lane B's guide
    //  lists, in the order it lists them, and the choice between them is forced
    //  rather than stylistic:
    //
    //    • A TOKEN for every number that was written ON THE ELEMENT. A
    //      `Width="680"` / `MaxWidth="560"` in markup is a LOCAL value and
    //      outranks every style setter, so a `Window.Wide` SELECTOR on one of
    //      them is silently dead — the trap lane C paid for. Such numbers
    //      moved onto `FirePaletteWidth` / `FirePaletteMaxHeight` /
    //      `FireCardMaxWidth` (the LAN board's two went with the LAN
    //      scoreboard), each with its wide rung in `Styles/FireTokens/Ladders.Wide.axaml`.
    //      `DynamicResource` re-resolves on the merge: no selector, no code.
    //
    //    • A SELECTOR for every TYPE rung, reaching an EXISTING `FireSize*` /
    //      `FontSize*` step. A font ladder may never live in the wide
    //      dictionary — it would compound with `MainWindow`'s `RootScaler`.
    //
    //  Every case below measures the SAME surface at both breakpoints in one
    //  pass, so the before/after is one number against another rather than a
    //  number against a report, and each asserts the DEFAULT value exactly:
    //  1080p must be byte-for-byte what it was, and a case that only checked
    //  "wide is bigger" would pass on a change that broke the laptop.

    /// <summary>
    ///     <b>The chart editor's three rails step their CONTENT up at the wide
    ///     breakpoint and keep their design HEIGHT.</b>
    ///     <para>
    ///         The tool rail's 24 glyphs each spelled <c>Width="14"
    ///         Height="14"</c> on the element until this lane. That is a LOCAL
    ///         value, so the breakpoint rule would have been dead on all 24
    ///         while reading as applied; they are declared once in
    ///         <c>Views/Shell/ShellChromeStyles.axaml</c> now. This case pins
    ///         both halves — the exact default (14 px, square, in a 44 px rail)
    ///         and the step — because "wide is bigger" alone would pass on a
    ///         change that moved 1080p too.
    ///     </para>
    ///     <para>
    ///         The rails' own heights are asserted UNCHANGED, and that is the
    ///         load-bearing half: the editor trades every pixel of vertical
    ///         space for the timeline (38 / 44 / 30 px, the two editor mockups'
    ///         verbatim numbers), so a chrome pass that grew the bars would take
    ///         the space from the one surface that is the product.
    ///     </para>
    /// </summary>
    [Theory]
    [InlineData(1920, 1080)]
    [InlineData(3840, 2160)]
    public Task TheShellChrome_StepsUpItsContentButNotItsRails(int width, int height) =>
        HeadlessSessionFixture.Dispatch(() =>
        {
            WithSweptView(typeof(DotCharter.UI.Views.Shell.ToolbarView), width, height, (window, view) =>
            {
                var rail = ByClass<Border>(view, "FireToolbar");

                AtBreakpoint(window, view, UiBreakpoint.Default);
                var baseRail = rail.Bounds.Height;
                var baseIcon = RailIcon(rail).Bounds;
                var baseCaret = RailCaret(rail).Bounds;
                var baseButton = RailButton(rail).Bounds.Height;
                var baseInk = MeasureSurface(view, "Shell/ToolbarView", $"{width}x{height}", width, height);

                AtBreakpoint(window, view, UiBreakpoint.Wide);
                var wideRail = rail.Bounds.Height;
                var wideIcon = RailIcon(rail).Bounds;
                var wideCaret = RailCaret(rail).Bounds;
                var wideButton = RailButton(rail).Bounds.Height;
                var wideInk = MeasureSurface(view, "Shell/ToolbarView", $"{width}x{height}", width, height);

                Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"[lane F] tool rail at {width}x{height}: glyph {baseIcon.Width:0}x{baseIcon.Height:0} -> "
                    + $"{wideIcon.Width:0}x{wideIcon.Height:0}, caret {baseCaret.Width:0} -> {wideCaret.Width:0}, "
                    + $"button {baseButton:0} -> {wideButton:0} px, rail {baseRail:0} -> {wideRail:0} px; "
                    + $"ink {baseInk.InkCoverage:P1} -> {wideInk.InkCoverage:P1} "
                    + $"(w {baseInk.InkWidthFraction:P1} -> {wideInk.InkWidthFraction:P1})"));

                // The DEFAULT, exactly. These four numbers are what 1080p has
                // always drawn, and the 24 glyphs only reach them through the
                // style that replaced their local values.
                Assert.Equal(14, baseIcon.Width);
                Assert.Equal(14, baseIcon.Height);
                Assert.Equal(10, baseCaret.Width);
                Assert.Equal(28, baseButton);

                Assert.True(wideIcon.Width > baseIcon.Width,
                    $"the tool rail's glyphs are still {wideIcon.Width:0} px at Window.Wide. The usual cause "
                    + "is a LOCAL Width/Height back on the PathIcon: a local value outranks every style "
                    + "setter, which is exactly how these 24 icons could not be moved at all before.");
                Assert.Equal(wideIcon.Width, wideIcon.Height);
                Assert.True(wideCaret.Width > baseCaret.Width && wideCaret.Width < wideIcon.Width,
                    $"the disclosure caret is {wideCaret.Width:0} px against a {wideIcon.Width:0} px glyph — it "
                    + "is the subordinate mark and must stay smaller than the icon it hangs off.");
                Assert.True(wideButton > baseButton,
                    $"the rail's hit targets are still {wideButton:0} px tall at Window.Wide.");
                Assert.True(wideButton <= wideRail,
                    $"a {wideButton:0} px control does not fit the {wideRail:0} px rail.");
                Assert.Equal(baseRail, wideRail);
            });

            WithSweptView(typeof(DotCharter.UI.Views.Shell.StatusBar), width, height, (window, view) =>
            {
                var rail = ByClass<Border>(view, "FireStatusBar");

                AtBreakpoint(window, view, UiBreakpoint.Default);
                var baseSize = RailMono(rail, faint: false).FontSize;
                var baseFaint = RailMono(rail, faint: true).FontSize;
                var baseHeight = rail.Bounds.Height;

                AtBreakpoint(window, view, UiBreakpoint.Wide);
                var wideSize = RailMono(rail, faint: false).FontSize;
                var wideFaint = RailMono(rail, faint: true).FontSize;
                var wideHeight = rail.Bounds.Height;

                Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"[lane F] status rail at {width}x{height}: mono read-out {baseSize:0.#} -> {wideSize:0.#} px, "
                    + $"its .Faint half {baseFaint:0.#} -> {wideFaint:0.#} px, inside a {baseHeight:0} -> "
                    + $"{wideHeight:0} px rail"));

                Assert.Equal(FireTypeLadder.Size("FireSizeMono"), baseSize);
                Assert.Equal(FireTypeLadder.Size("FireSizeEyebrow"), baseFaint);
                Assert.True(wideSize > baseSize,
                    $"every read-out on the status rail is still {wideSize:0.#} px at Window.Wide — the ladder's "
                    + "quietest step, on a 4K panel.");
                Assert.True(wideFaint > baseFaint,
                    $"the rail's quiet half is still {wideFaint:0.#} px at Window.Wide.");
                // The rail has TWO mono rungs and they must stay two. A rule
                // that named only `TextBlock.FireMono` raises both to the same
                // size and deletes a hierarchy that exists at 1080p — which is
                // what THIS assertion, and not the two above, catches.
                Assert.True(wideFaint < wideSize,
                    $"at Window.Wide the rail's .Faint read-outs are {wideFaint:0.#} px against {wideSize:0.#} "
                    + $"for the plain ones — the two mono rungs have been flattened into one.");
                Assert.True(wideSize * 1.4 <= wideHeight,
                    $"{wideSize:0.#} px of type does not clear a {wideHeight:0} px rail with its leading.");
                Assert.Equal(baseHeight, wideHeight);
            });

            WithSweptView(typeof(DotCharter.UI.Views.Shell.MainMenuView), width, height, (window, view) =>
            {
                AtBreakpoint(window, view, UiBreakpoint.Default);
                var baseSize = MenuHeader(view).FontSize;

                AtBreakpoint(window, view, UiBreakpoint.Wide);
                var wideSize = MenuHeader(view).FontSize;

                Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"[lane F] menu bar at {width}x{height}: top-level header {baseSize:0.#} -> {wideSize:0.#} px "
                    + $"(the dropdown ROWS under it are a PopupRoot and deliberately do not move)"));

                Assert.Equal(FireTypeLadder.Size("FireSizeCaption"), baseSize);
                Assert.True(wideSize > baseSize,
                    $"the menu bar's top-level headers are still {wideSize:0.#} px at Window.Wide.");
            });
        }, TestContext.Current.CancellationToken);

    /// <summary>
    ///     <b>The command palette grows instead of being a 1080p box in more
    ///     scrim.</b>
    ///     <para>
    ///         Its own header calls it "a 680 px centred overlay". Both of its
    ///         numbers are tokens now, and the HEIGHT is the one that matters:
    ///         the row height is a fixed 38 px, so the card's ceiling IS the
    ///         number of commands on screen. This case derives that capacity
    ///         from the measured head and footer rather than asserting the
    ///         token twice.
    ///     </para>
    ///     <para>
    ///         The search and the keyboard route are asserted UNCHANGED across
    ///         the switch — a breakpoint that re-templated the field or dropped
    ///         the selection would be a regression the size numbers could not
    ///         see.
    ///     </para>
    /// </summary>
    [Theory]
    [InlineData(1920, 1080)]
    [InlineData(3840, 2160)]
    public Task TheCommandPalette_GrowsAtTheWideBreakpoint(int width, int height) =>
        HeadlessSessionFixture.Dispatch(() =>
        {
            WithSweptView(typeof(DotCharter.UI.Views.CommandPalette.CommandPaletteView), width, height,
                (window, view) =>
                {
                    var card = (Border)Descendant(view, "PaletteCard");
                    var list = (ListBox)Descendant(view, "ResultList");
                    var search = (TextBox)Descendant(view, "SearchBox");

                    AtBreakpoint(window, view, UiBreakpoint.Default);
                    var baseCard = (card.Width, card.MaxHeight);
                    var baseRows = RowCapacity(card, list);
                    var baseSearch = search.FontSize;
                    var baseInk = MeasureSurface(view, "CommandPalette/CommandPaletteView",
                        $"{width}x{height}", width, height);
                    search.Text = "tempo";
                    search.Focus();
                    var baseFocused = search.IsFocused;
                    var baseSelection = list.SelectedIndex;

                    AtBreakpoint(window, view, UiBreakpoint.Wide);
                    var wideCard = (card.Width, card.MaxHeight);
                    var wideRows = RowCapacity(card, list);
                    var wideSearch = search.FontSize;
                    var wideInk = MeasureSurface(view, "CommandPalette/CommandPaletteView",
                        $"{width}x{height}", width, height);

                    Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                        $"[lane F] command palette at {width}x{height}: card {baseCard.Width:0}x"
                        + $"{baseCard.MaxHeight:0} ({baseCard.Width / width:P1} of the width) -> "
                        + $"{wideCard.Width:0}x{wideCard.MaxHeight:0} ({wideCard.Width / width:P1}); "
                        + $"~{baseRows} -> ~{wideRows} visible rows; query field {baseSearch:0.#} -> "
                        + $"{wideSearch:0.#} px; ink {baseInk.InkCoverage:P1} -> {wideInk.InkCoverage:P1}"));

                    Assert.Equal(680d, baseCard.Width);
                    Assert.Equal(500d, baseCard.MaxHeight);
                    Assert.True(wideCard.Width > baseCard.Width && wideCard.MaxHeight > baseCard.MaxHeight,
                        $"the palette is still {wideCard.Width:0}x{wideCard.MaxHeight:0} at Window.Wide — the same "
                        + "box as on a laptop, with twice as much scrim around it.");
                    Assert.True(wideRows > baseRows,
                        $"the palette still shows ~{wideRows} rows at Window.Wide against ~{baseRows} at the "
                        + "default shape. The row height is fixed, so a taller card IS more commands — if these "
                        + "are equal the ceiling did not move.");
                    Assert.True(wideSearch > baseSearch,
                        $"the query field is still {wideSearch:0.#} px in a {wideCard.Width:0} px card.");

                    // The keyboard route is the palette's only route.
                    Assert.True(baseFocused, "the query field could not take focus even at the default shape — "
                                             + "the assertion below would then prove nothing.");
                    Assert.True(search.IsFocused,
                        "the query field lost keyboard focus across the breakpoint switch. A shape change "
                        + "re-styles the whole tree; it must not re-template the one control the user is typing "
                        + "into.");
                    Assert.Equal("tempo", search.Text);
                    Assert.Equal(baseSelection, list.SelectedIndex);
                });
        }, TestContext.Current.CancellationToken);

    // ── lane F plumbing ───────────────────────────────────────────────────

    /// <summary>One standalone view over the UI sweep's own data context, in a
    /// window of the given size. Mirrors the body of
    /// <see cref="EveryStandaloneView_AtEveryResolution_IsMeasured" /> — the
    /// difference is that the BREAKPOINT is the subject here, so the caller
    /// drives it rather than the resolution.</summary>
    private static void WithSweptView(Type viewType, int width, int height, Action<Window, Control> body)
    {
        var services = App.Services
                        ?? throw new InvalidOperationException(
                            "App.Services is null — the headless app session did not boot.");

        var view = (Control)Activator.CreateInstance(viewType)!;
        var axaml = UiSweepCatalog.AxamlFor(viewType);
        var declared = axaml is null ? null : UiSweepCatalog.DeclaredDataType(axaml);
        var (dataContext, _) = UiSweepDataContexts.Build(services).For(declared);
        if (dataContext != null)
        {
            // 39 surfaces are a scrim bound to IsOpen and measure NOTHING while
            // closed — the palette is one of them.
            OpenIfDialog(dataContext);
            SetBool(dataContext, "IsActive", true);
            SetBool(dataContext, "IsHudActive", true);
            view.DataContext = dataContext;
        }

        var window = new Window { Width = width, Height = height, Content = view };
        window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs(DispatcherPriority.Background);
            window.UpdateLayout();
            body(window, view);
        }
        finally
        {
            UiBreakpointController.Apply(window, UiBreakpoint.Default);
            window.Content = null;
            window.Close();
        }
    }

    /// <summary>Applies a breakpoint and lays the tree out. Three passes: a
    /// shape change re-styles the whole tree and re-resolves every
    /// <c>DynamicResource</c> in it, and the sizes that fall out of that are a
    /// measure behind.</summary>
    private static void AtBreakpoint(Window window, Control view, UiBreakpoint breakpoint)
    {
        UiBreakpointController.Apply(window, breakpoint);
        Dispatcher.UIThread.RunJobs(DispatcherPriority.Background);
        for (var pass = 0; pass < 3; pass++) window.UpdateLayout();
        _ = view;
    }

    private static T ByClass<T>(Control root, string styleClass) where T : Control
    {
        var found = root.GetVisualDescendants().OfType<T>()
            .FirstOrDefault(c => c.Classes.Contains(styleClass));
        Assert.True(found is not null,
            $"no {typeof(T).Name} wearing '{styleClass}' is in the laid-out tree — it was renamed or its "
            + "surface never became visible.");
        return found!;
    }

    /// <summary>The rail's first ORDINARY glyph — never a <c>.TbCaret</c>, whose
    /// step is deliberately smaller.</summary>
    private static PathIcon RailIcon(Control rail) =>
        rail.GetVisualDescendants().OfType<PathIcon>().First(p => !p.Classes.Contains("TbCaret"));

    private static PathIcon RailCaret(Control rail) =>
        rail.GetVisualDescendants().OfType<PathIcon>().First(p => p.Classes.Contains("TbCaret"));

    /// <summary>A hit target sized by <c>StackPanel.TbGroup &gt; Button</c> —
    /// i.e. a DIRECT child of a group, which is what that selector reaches.</summary>
    private static Button RailButton(Control rail) =>
        rail.GetVisualDescendants().OfType<Button>()
            .First(b => b.Parent is StackPanel panel && panel.Classes.Contains("TbGroup"));

    /// <summary>A mono read-out off the rail — the plain rung or its quieter
    /// <c>.Faint</c> one. Both, because they are two steps at the default shape
    /// and a breakpoint rule that names only the base class flattens them.</summary>
    private static TextBlock RailMono(Control rail, bool faint) =>
        rail.GetVisualDescendants().OfType<TextBlock>()
            .First(t => t.Classes.Contains("FireMono") && t.Classes.Contains("Faint") == faint);

    private static MenuItem MenuHeader(Control view) =>
        view.GetVisualDescendants().OfType<MenuItem>().First(m => m.Classes.Contains("FireMenuTop"));

    private static TextBlock AnyText(Control view, Func<TextBlock, bool> predicate)
    {
        var found = view.GetVisualDescendants().OfType<TextBlock>().FirstOrDefault(predicate);
        Assert.True(found is not null, "no TextBlock matching the probe is in the laid-out tree.");
        return found!;
    }

    /// <summary>How many result rows the palette's ceiling has room for, derived
    /// from the card's own head + footer rather than from the token — the token
    /// is what this is checking.</summary>
    private static int RowCapacity(Border card, ListBox list)
    {
        var rowHeight = list.GetVisualDescendants().OfType<ListBoxItem>()
            .Select(i => i.Bounds.Height).FirstOrDefault(h => h > 1);
        if (rowHeight <= 1) rowHeight = 38; // the style's MinHeight; an empty list realizes no row
        var chrome = card.Bounds.Height - list.Bounds.Height;
        if (chrome <= 0 || !double.IsFinite(chrome)) chrome = 100;
        return (int)Math.Max(0, Math.Floor((card.MaxHeight - chrome) / rowHeight));
    }

    // ── lane E: the Amp & FX rig (audit §4 P1.4) ──────────────────────────

    /// <summary>
    ///     One row per editor FAMILY: a module id that opens it, the view type
    ///     the selector builds, the <c>RigScaleToFit</c> in it, and whether that
    ///     scaler covers the WHOLE scroller.
    ///     <para>
    ///         <c>RigPedalEditorView</c> is the one that does not: its scaler
    ///         wraps the enclosure alone and a 320 px "beside the pedal" column
    ///         stands outside it, so "the face fits the row" is asserted for all
    ///         nine and "the scroller needs no scrollbar" only for the eight
    ///         where those two are the same claim.
    ///     </para>
    /// </summary>
    /// <remarks>
    ///     The floor is stated PER FAMILY because the faces are not comparable:
    ///     the cabinet console is two racks and a mic stage, the treadle is one
    ///     short strip, and the pedal enclosure is a stompbox PICTURE that is
    ///     supposed to look like one object next to a 320 px column of things
    ///     about it. Each is the measured 3840x2160 value with roughly a fifth
    ///     of headroom, so an unrelated content change does not fail this while
    ///     a regression of the MECHANISM (the scaler removed, MaxScale back to a
    ///     literal, the wide ladder not merged) falls far below it.
    /// </remarks>
    public static TheoryData<string, string, string, bool, double> RigEditorFamilies => new()
    {
        // module · family view · its scaler · scaler covers the whole scroller · floor @4K
        { "amp", "RigAmpFaceView", "RigFaceFit", true, 0.44 },
        { "cab", "RigCabinetEditorView", "RigFaceFit", true, 0.56 },
        { "splitter", "RigRoutingConsoleView", "RigFaceFit", true, 0.63 },
        { "graphiceq", "RigGraphicEqView", "RigFaceFit", true, 0.47 },
        { "eq", "RigParametricEqView", "RigFaceFit", true, 0.31 },
        { "wah", "RigExpressionView", "RigFaceFit", true, 0.31 },
        { "miku", "RigVocalView", "RigFaceFit", true, 0.40 },
        { "compressor", "RigDynamicsView", "RigFaceFit", true, 0.40 },
        // 0.08: the drawn enclosure is the fallback for a module with no faceplate,
        // and the wide dock row is 520 since the board became the editor (was 860);
        // measured 9.3 % there
        { "overdrive", "RigPedalEditorView", "RigEnclosureFit", false, 0.08 }
    };

    /// <summary>
    ///     <b>Audit §4 P1.4 — the module-editor faces.</b> Eight of the nine
    ///     families were authored at a fixed design size and dropped into a dock
    ///     row that is neither: at 1920x1080 the face was TALLER than the row
    ///     and the row scrolled (the owner's "the editor is drawn under the
    ///     footer", fixed in b2c666ca2 by making the scrollbar rest visible —
    ///     which made the cut legible rather than making it go away), and at
    ///     3840x2160 the same face sat at its design size in a corner of a row
    ///     with ~5x the area.
    ///     <para>
    ///         Every family now scales through <c>Controls/Fire/RigScaleToFit</c>
    ///         the way the pedal enclosure already did, against a ceiling that is
    ///         the breakpointed <c>FireRigFaceMaxScale</c> token (1.35 / 2.0).
    ///         This case measures the face at BOTH resolutions in one pass, so
    ///         the design box and the on-screen box are one number against
    ///         another rather than a number against a report.
    ///     </para>
    ///     <para>
    ///         <b>Why the scaler and not the face is measured.</b> A
    ///         <c>LayoutTransformControl</c> lays its child out at the design
    ///         size and scales the result, so the CHILD's <c>Bounds</c> is the
    ///         design box for ever — which is exactly why it is logged here as
    ///         the "before" — and only the transformed box is what is on screen.
    ///     </para>
    ///     <para>
    ///         Not gated behind <see cref="GateEnvVar" />, for the reason
    ///         <see cref="TheSettingsColumn_WidensAtTheWideBreakpoint" /> gives.
    ///     </para>
    /// </summary>
    [Theory]
    [MemberData(nameof(RigEditorFamilies))]
    public Task TheRigEditorFaces_FitTheDockRow(
        string moduleId, string familyView, string scalerName, bool wholeFaceScaled,
        double floorAt4K) =>
        HeadlessSessionFixture.Dispatch(() =>
        {
            var measured = new List<RigFaceMeasurement>();

            // These are the FAMILY views, which a module without a faceplate opens;
            // one with a face is edited on the board (RigDockUnit.EditorFamilyFor).
            RigFaceplates.SuppressedForTests = true;
            try
            {
                foreach (var (width, height, wide) in new[] { (1920, 1080, false), (3840, 2160, true) })
                {
                    WithRigScreen(moduleId, width, height, wide, (view, _) =>
                    {
                        var dock = TransformedBounds(Descendant(view, "RigDockBody"), view);
                        var family = Assert.Single(view.GetVisualDescendants().OfType<Control>(),
                            c => string.Equals(c.GetType().Name, familyView, StringComparison.Ordinal));
                        var scroll = Assert.IsType<ScrollViewer>(ByName(family, "RigEditorScroll"));
                        var fit = Assert.IsType<RigScaleToFit>(ByName(family, scalerName));
                        var design = fit.Child is Control child ? child.Bounds : default;
                        var onScreen = TransformedBounds(fit, view);

                        measured.Add(new RigFaceMeasurement(width, height, wide, fit.Scale, design,
                            onScreen, dock, scroll.Extent, scroll.Viewport));
                    });
                }
            }
            finally
            {
                RigFaceplates.SuppressedForTests = false;
            }

            var small = measured[0];
            var large = measured[1];
            var ratio = small.Coverage > 0 ? large.Coverage / small.Coverage : 0;
            var layoutRatio = small.LayoutCoverage > 0
                ? large.LayoutCoverage / small.LayoutCoverage : 0;

            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"[P1.4] {familyView} ({moduleId}): design box {Round(small.Design)} -> on screen "
                + $"{Round(small.OnScreen)} @1080p (scale {small.Scale:0.00}) / {Round(large.OnScreen)} "
                + $"@4K Wide (scale {large.Scale:0.00}); dock row {Round(small.Dock)} / "
                + $"{Round(large.Dock)}; coverage of the row {small.LayoutCoverage:P1} laid out -> "
                + $"{small.Coverage:P1} drawn @1080p and {large.LayoutCoverage:P1} -> {large.Coverage:P1} "
                + $"@4K; ratio(4K/1080p) {ratio:0.00} (laid out {layoutRatio:0.00}); face/row height "
                + $"{small.HeightFill:P0} and {large.HeightFill:P0}; scroller extent/viewport "
                + $"{small.Extent.Height:0}/{small.Viewport.Height:0} @1080p and "
                + $"{large.Extent.Height:0}/{large.Viewport.Height:0} @4K; the design box "
                + $"{(small.Design.Height > small.Viewport.Height ? "WAS" : "was not")} taller than the "
                + $"1080p row"));

            foreach (var m in measured)
            {
                Assert.True(m.Design.Width > 1 && m.Design.Height > 1,
                    $"{familyView}: the scaler's child measured {Round(m.Design)} at {m.Width}x{m.Height} "
                    + $"— nothing was laid out and every assertion below is vacuous.");

                // THE CUT. The face is scaled to the row, so it is inside it.
                Assert.True(m.OnScreen.Height <= m.Viewport.Height + 1, string.Create(
                    CultureInfo.InvariantCulture,
                    $"{familyView} is {m.OnScreen.Height:0} px tall on a {m.Viewport.Height:0} px dock "
                    + $"row at {m.Width}x{m.Height} (scale {m.Scale:0.00}) — it is CUT. Either the "
                    + $"TargetHeight binding is not reaching RigEditorScroll's viewport or the face "
                    + $"asks for more than RigArtwork.MinEditorScale can fold into the row."));
                Assert.True(m.OnScreen.Width <= m.Viewport.Width + 1, string.Create(
                    CultureInfo.InvariantCulture,
                    $"{familyView} is {m.OnScreen.Width:0} px wide in a {m.Viewport.Width:0} px dock "
                    + $"row at {m.Width}x{m.Height}."));

                if (!wholeFaceScaled) continue;
                Assert.True(m.Extent.Height <= m.Viewport.Height + 1, string.Create(
                    CultureInfo.InvariantCulture,
                    $"{familyView}'s scroller wants {m.Extent.Height:0} px of a {m.Viewport.Height:0} px "
                    + $"viewport at {m.Width}x{m.Height}: the default module still needs a SCROLLBAR, "
                    + $"which is the cut this lane exists to remove."));
            }

            // …and it GROWS. 1.35 was the old ceiling, so a face still at or
            // under it at 4K is one the wide token never reached.
            Assert.True(large.Scale >= small.Scale, string.Create(CultureInfo.InvariantCulture,
                $"{familyView} is scaled {large.Scale:0.00} on a 3840x2160 dock row against "
                + $"{small.Scale:0.00} on a 1920x1080 one — the face does not grow with the window."));
            // A face that already fills the row's HEIGHT is not held back by the
            // ceiling at all (the generated faceplates are tall: 2026-09-27), so the
            // proof that the wide token reached it is that it fills the row; a face
            // bound by width must be past the old ceiling.
            var heightBound = large.OnScreen.Height >= large.Viewport.Height * 0.9;
            Assert.True(heightBound || large.Scale > RigArtwork.MaxEditorScale, string.Create(
                CultureInfo.InvariantCulture,
                $"{familyView} is scaled {large.Scale:0.00} at 3840x2160, which is the pre-lane-E "
                + $"ceiling of {RigArtwork.MaxEditorScale:0.00}: MaxScale is not resolving "
                + $"FireRigFaceMaxScale, or the wide ladder was not merged for this window."));
            Assert.True(large.Coverage >= floorAt4K, string.Create(CultureInfo.InvariantCulture,
                $"{familyView} covers {large.Coverage:P1} of the dock row at 3840x2160 against a floor "
                + $"of {floorAt4K:P1} (it is laid out over {large.LayoutCoverage:P1} of it). Its design box "
                + $"is {Round(large.Design)} and it is on screen at {Round(large.OnScreen)} — if those "
                + $"two are the same, RigScaleToFit is not scaling it at all."));
        }, TestContext.Current.CancellationToken);

    /// <summary>
    ///     <b>Audit §4 P1.5 — the rig SCREEN's panels.</b> The gear palette was
    ///     a literal <c>Width="320"</c>: 16.7 % of a 1920 window and 8.3 % of a
    ///     3840 one, the same halving the front door's two rails had. It is the
    ///     <c>FireRigGearPanelWidth</c> token now (320, 420 above the wide
    ///     breakpoint) and everything else on the screen — the board well, the
    ///     module dock and the footer — is asserted to take the rest of the
    ///     window rather than a design width of its own.
    /// </summary>
    [Theory]
    [InlineData(1920, 1080, false, 320)]
    [InlineData(3840, 2160, true, 420)]
    public Task TheRigScreenPanels_UseTheWindow(int width, int height, bool wide, double railWidth) =>
        HeadlessSessionFixture.Dispatch(() =>
            WithRigScreen("overdrive", width, height, wide, (view, _) =>
            {
                var rail = Descendant(view, "RigGearPanel");
                // the routing MAP's console: its routing rail and the board, side by side
                var board = Descendant(view, "RigSignalStrip");
                var dock = Descendant(view, "RigDockBody");
                var footer = Descendant(view, "RigFooter");
                var measurement = MeasureSurface(view, "PlayMode/AmpFxRig",
                    $"{width}x{height}", width, height);

                // The screen sits in an 18 px FireScreen margin, so the body
                // column is everything except the rail and those two margins.
                var body = width - rail.Bounds.Width - 36;

                Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"[P1.5] rig screen at {width}x{height} ({(wide ? "Wide" : "Default")}): gear rail "
                    + $"{rail.Bounds.Width:0} px ({rail.Bounds.Width / width:P1} of the window), body "
                    + $"column {body:0} px; board {board.Bounds.Width:0}x{board.Bounds.Height:0}, dock "
                    + $"{dock.Bounds.Width:0}x{dock.Bounds.Height:0}, footer {footer.Bounds.Width:0} px; "
                    + $"primary panel {measurement.PrimaryPanel} {measurement.PrimaryPanelBox} = "
                    + $"{measurement.PrimaryPanelCoverage:P1}; ink {measurement.InkCoverage:P1} "
                    + $"(w {measurement.InkWidthFraction:P1})"));

                Assert.Equal(railWidth, rail.Bounds.Width, 1);
                foreach (var (name, control) in new[]
                             { ("RigSignalStrip", board), ("RigDockBody", dock), ("RigFooter", footer) })
                {
                    Assert.True(control.Bounds.Width >= body - 24, string.Create(
                        CultureInfo.InvariantCulture,
                        $"{name} arranges {control.Bounds.Width:0} px wide in a {body:0} px body column "
                        + $"of a {width}x{height} window — it is pinned to a design width instead of "
                        + $"taking the room the gear rail leaves."));
                }

                // Nothing the screen DRAWS may leave the window: the rail is
                // 100 px wider at Wide and it takes that out of the body.
                var spills = view.GetVisualDescendants().OfType<Control>()
                    .Where(c => c.IsVisible && c.IsEffectivelyVisible
                                && c.Bounds.Width > 1 && c.Bounds.Height > 1
                                && IsOverflowSubject(c))
                    .Select(c => (c, r: TransformedBounds(c, view)))
                    .Where(t => t.r != default && (t.r.Left < -1 || t.r.Right > width + 1))
                    .Select(t => Describe(t.c))
                    .Distinct(StringComparer.Ordinal)
                    .ToList();
                Assert.True(spills.Count == 0, string.Create(CultureInfo.InvariantCulture,
                    $"{spills.Count} rig controls arranged outside a {width} px window: "
                    + $"{string.Join(", ", spills.Take(6))}"));
            }), TestContext.Current.CancellationToken);

    /// <summary>One face, at one resolution. <see cref="Coverage" /> is the
    /// share of the DOCK ROW the face is drawn over — the row is what it has to
    /// use, and the row itself grows with the window.</summary>
    private sealed record RigFaceMeasurement(
        int Width, int Height, bool Wide, double Scale,
        Rect Design, Rect OnScreen, Rect Dock, Size Extent, Size Viewport)
    {
        public double Coverage => Share(OnScreen.Width, OnScreen.Height);

        /// <summary>What the face covers in LOGICAL pixels — the box the
        /// scaler laid it out at, before the transform. Not a "before": under
        /// `Stretch` that box is already the slot the transform left, so it
        /// tracks the scale. The pair (laid out, drawn) is what says whether
        /// the face is being magnified or merely stretched.</summary>
        public double LayoutCoverage => Share(Design.Width, Design.Height);

        /// <summary>How much of the row's HEIGHT the face is drawn over. This
        /// is the axis that binds: at 1080p the row is the constraint, at 4K it
        /// is `FireRigFaceMaxScale`.</summary>
        public double HeightFill => Dock.Height > 0 ? OnScreen.Height / Dock.Height : 0;

        private double Share(double w, double h) => Dock.Width > 0 && Dock.Height > 0
            ? w * h / (Dock.Width * Dock.Height)
            : 0;
    }

    /// <summary>
    ///     The Amp &amp; FX rig screen over a fake chain carrying one module of
    ///     the wanted type, selected, at a window size and a breakpoint.
    ///     <para>
    ///         The breakpoint is APPLIED rather than inferred: nothing attaches
    ///         <c>UiBreakpointController</c> to a bare test window, and a case
    ///         that measured 3840 px while the base ladder was still merged
    ///         would report the wide mechanism as doing nothing.
    ///     </para>
    /// </summary>
    private static void WithRigScreen(
        string moduleId, int width, int height, bool wide,
        Action<AmpFxRigView, AmpFxRigViewModel> body)
    {
        var metadata = new DotCharter.Tone.Audio.Dsp.JuceDspChainService();
        metadata.LoadCatalog();
        var catalogue = metadata.Catalog;
        Assert.SkipWhen(catalogue.Count == 0, "native DSP ABI (juce_sharp.dll) not loadable here");
        var type = catalogue.SingleOrDefault(m => m.Id == moduleId);
        Assert.SkipWhen(type is null, $"the native catalogue has no '{moduleId}'");

        var chain = new DotCharter.Tests.Infrastructure.FakeDspChainService(catalogue);
        using var rig = TestRigBuilders.Pedalboard(
            chain, new DotCharter.Tests.Infrastructure.FakeDspTonePresetService(), LocalizationService.Instance);
        rig.AddGear(type!);
        if (moduleId == "splitter") rig.AddGear(catalogue.Single(m => m.Id == "merger"));
        rig.SelectNodeAt(0);

        var vm = new AmpFxRigViewModel(rig);
        var view = new AmpFxRigView { DataContext = vm };
        var window = new Window { Width = width, Height = height, Content = view };
        window.Show();
        try
        {
            UiBreakpointController.Apply(window, wide ? UiBreakpoint.Wide : UiBreakpoint.Default);
            Dispatcher.UIThread.RunJobs(DispatcherPriority.Background);
            // Four passes: the dock row's height is capped from code on arrange
            // (AmpFxRigView.CapDockBody) and the faces then fit to the viewport
            // that cap produces, so the fit is settled one pass AFTER the row is.
            for (var pass = 0; pass < 4; pass++) window.UpdateLayout();
            body(view, vm);
        }
        finally
        {
            UiBreakpointController.Apply(window, UiBreakpoint.Default);
            window.Close();
        }
    }

    // ── tone panel ────────────────────────────────────────────────────────

    /// <summary>
    ///     THE TONE SURFACE SCROLLS INSIDE ITS MODE AND NEVER REACHES THE
    ///     FOOTER.
    ///     <para>
    ///         The owner reported the drawer version of this on 2026-09-14: the
    ///         "Design a tone" panel with "Match by section" and a result open
    ///         was simply TALLER than the drawer and was arranged straight out
    ///         of the bottom of it, over the footer's tuner strip, with no
    ///         scrollbar. W4 replaced the drawer with the TONES MODE, so the
    ///         cap (<c>AmpFxRigView.CapToneDesign</c>) is gone and the
    ///         containment is the mode's own <c>*</c> row — but the FAULT is
    ///         unchanged and so is this case: <c>RigToneBrowser</c> clips, and
    ///         everything inside it stays above <c>RigFooter</c>.
    ///     </para>
    ///     <para>
    ///         Since 2026-09-27 the designer is a COLUMN of the Tones view with its
    ///         own thread scroller (<c>ToneDesignScroll</c>) and its composer
    ///         pinned under it; the card grid scrolls in <c>RigTonesScroll</c>
    ///         beside it. So the overflow is asked of the THREAD's scroller, and
    ///         the grid is checked for the same containment separately.
    ///     </para>
    ///     <para>
    ///         <b>The panel is NOT scaled</b>, which is where it parts company
    ///         with lane E above: a family view is a PICTURE of one piece of gear
    ///         and shrinking it costs nothing, while this is a FORM — two text
    ///         boxes, a combo, three lists — and a scaled form has scaled hit
    ///         targets and scaled type.
    ///     </para>
    ///     <para>
    ///         1896x1000 is the owner's own window: a 2560x1440 monitor at the
    ///         Auto interface scale of 1.35.
    ///     </para>
    /// </summary>
    [Theory]
    [InlineData(1920, 1080)]
    [InlineData(1896, 1000)]
    public Task TheToneDesignPanel_ScrollsInsideTheDrawer_AndNeverReachesTheFooter(
        int width, int height) =>
        HeadlessSessionFixture.Dispatch(() => WithRigDrawer(width, height, view =>
        {
            var drawer = Descendant(view, "RigToneBrowser");
            var scroll = Assert.IsType<ScrollViewer>(Descendant(view, "ToneDesignScroll"));
            var panel = Descendant(view, "RigToneDesignPanel");
            var footer = Descendant(view, "RigFooter");
            var cards = Descendant(view, "RigBrowserCardHost");

            var drawerBox = TransformedBounds(drawer, view);
            var scrollBox = TransformedBounds(scroll, view);
            var footerBox = TransformedBounds(footer, view);
            var painted = PaintedBox(panel, view);
            var cardBox = PaintedBox(cards, view);

            // NON-VACUITY, first: everything below is about a panel that is too
            // TALL, so a panel that realized nothing would pass all of it. The
            // section block and the result block are the two that made the
            // owner's panel overflow, and both have to be on screen.
            var sections = Descendant(view, "ToneSectionsRoot");
            var result = Descendant(view, "ToneDesignResult");
            foreach (var (name, control) in new[]
                         { ("ToneSectionsRoot", sections), ("ToneDesignResult", result) })
            {
                Assert.True(control.IsEffectivelyVisible
                            && control.Bounds is { Width: > 1, Height: > 1 },
                    $"'{name}' laid out as {control.Bounds} inside the Tones mode — the panel under "
                    + "test is not the expanded one, so every containment assertion below is vacuous.");
            }

            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"[tone panel] {width}x{height}: mode {Round(drawerBox)}, scroller "
                + $"{Round(scrollBox)}, panel painted {Round(painted)}, footer {Round(footerBox)}; "
                + $"panel content {panel.Bounds.Height:0} px in a {scroll.Viewport.Height:0} px "
                + $"viewport (extent {scroll.Extent.Height:0}); card grid painted {Round(cardBox)}"));

            // (a) it scrolls rather than growing: the content is taller than the
            //     room it is given, and the room is inside the mode.
            Assert.True(scroll.Extent.Height > scroll.Viewport.Height + 1, string.Create(
                CultureInfo.InvariantCulture,
                $"the tone surface's scroller wants {scroll.Extent.Height:0} px of a "
                + $"{scroll.Viewport.Height:0} px viewport at {width}x{height} — with the section "
                + $"list and a result open it must have MORE content than viewport, or this case is "
                + $"not exercising the overflow the owner reported."));
            Assert.True(scrollBox.Bottom <= drawerBox.Bottom + 1, string.Create(
                CultureInfo.InvariantCulture,
                $"the designer's thread scroller ends at y={scrollBox.Bottom:0} in a mode that ends at "
                + $"y={drawerBox.Bottom:0} ({width}x{height}) — the thread is not the column's `*` "
                + $"row any more, so its content is taking the whole column and more."));
            Assert.True(painted.Bottom <= drawerBox.Bottom + 1, string.Create(
                CultureInfo.InvariantCulture,
                $"the tone panel PAINTS down to y={painted.Bottom:0} past a mode that ends at "
                + $"y={drawerBox.Bottom:0} at {width}x{height}."));
            Assert.True(painted.Bottom <= footerBox.Top + 1, string.Create(
                CultureInfo.InvariantCulture,
                $"the tone panel paints down to y={painted.Bottom:0} and the footer's tuner strip "
                + $"starts at y={footerBox.Top:0} at {width}x{height} — this is the owner's "
                + $"screenshot exactly."));

            // (b) …and containment is STRUCTURAL, not a side effect of the
            //     panel happening to have a scroller at its root.
            Assert.True(drawer.ClipToBounds,
                "RigToneBrowser lost ClipToBounds. A Border does not clip, so any content "
                + "without a scroller of its own is drawn straight into the footer's row again.");

            // (c) the scrollbar RESTS VISIBLE. `Auto` is what hid the module
            //     editor's cut, and it does not inherit — it is per scroller.
            Assert.Equal(ScrollBarVisibility.Visible, scroll.VerticalScrollBarVisibility);
            Assert.False(scroll.AllowAutoHide,
                "ToneDesignScroll auto-hides its scrollbar: the thread is cut at the column's edge "
                + "with nothing on screen saying it continues.");
            // The scroller's OWN bar — `TemplatedParent == sv` — and not the
            // first vertical ScrollBar the walk meets: depth-first, that is a
            // collapsed `Auto` bar inside one of the panel's lists, which laid
            // out as 0,0,0,0 and failed this case against a product that was
            // right.
            var bar = scroll.GetVisualDescendants().OfType<ScrollBar>()
                .FirstOrDefault(b => b.Orientation == Orientation.Vertical
                                     && ReferenceEquals(b.TemplatedParent, scroll));
            Assert.True(bar is { IsEffectivelyVisible: true } && bar.Bounds.Width > 1,
                $"ToneDesignScroll has no visible vertical scrollbar (it laid out as "
                + $"{bar?.Bounds.ToString() ?? "absent"}).");

            // (d) …and the mode's OTHER content — the preset card grid — is held
            //     the same way: a WrapPanel over the whole catalogue in its own
            //     scroller (RigTonesScroll) beside the designer, never inside it,
            //     and the clip is what keeps it inside the mode.
            Assert.True(cardBox.Bottom <= drawerBox.Bottom + 1, string.Create(
                CultureInfo.InvariantCulture,
                $"the preset card grid paints down to y={cardBox.Bottom:0} in a mode that ends at "
                + $"y={drawerBox.Bottom:0} at {width}x{height}."));
            Assert.True(cardBox.Height > 1, string.Create(CultureInfo.InvariantCulture,
                $"the preset card grid was squeezed to {cardBox.Height:0} px at {width}x{height} — "
                + $"the designer column has left the browser no room to be a browser."));
        }), TestContext.Current.CancellationToken);

    /// <summary>The region of <paramref name="v" /> that actually reaches the
    /// screen: its own box intersected with every clipping ancestor's. A
    /// ScrollViewer arranges its content at FULL height and offsets it, so the
    /// raw transformed box of scrolled content always overflows and says
    /// nothing.</summary>
    private static Rect PaintedBox(Visual v, Visual root)
    {
        Rect InRoot(Visual x, Rect local) => new(x.TranslatePoint(local.TopLeft, root)!.Value, local.Size);

        var rect = InRoot(v, new Rect(v.Bounds.Size));
        foreach (var a in v.GetVisualAncestors())
        {
            if (a.ClipToBounds) rect = rect.Intersect(InRoot(a, new Rect(a.Bounds.Size)));
            if (a.Clip is { } g) rect = rect.Intersect(InRoot(a, g.Bounds));
            if (ReferenceEquals(a, root)) break;
        }

        return rect;
    }

    /// <summary>
    ///     The rig screen with the TONE DRAWER open and its AI panel expanded
    ///     over a seeded section list and a finished result.
    ///     <para>
    ///         The panel's view model is pushed onto the real
    ///         <c>RigToneDesignPanel</c> rather than reached through
    ///         <c>Rig.Browser.ToneDesign</c>: the drawer's own instance resolves
    ///         <c>IToneDesignService</c> out of <c>App.Services</c>, which a test
    ///         window does not have, so it would render the "not configured"
    ///         banner and nothing else. The VIEW under test is the shipping one
    ///         in its shipping slot; only the data is a stub.
    ///     </para>
    /// </summary>
    private static void WithRigDrawer(int width, int height, Action<AmpFxRigView> body)
    {
        var metadata = new DotCharter.Tone.Audio.Dsp.JuceDspChainService();
        metadata.LoadCatalog();
        var catalogue = metadata.Catalog;
        Assert.SkipWhen(catalogue.Count == 0, "native DSP ABI (juce_sharp.dll) not loadable here");

        var chain = new DotCharter.Tests.Infrastructure.FakeDspChainService(catalogue);
        using var rig = TestRigBuilders.Pedalboard(
            chain, new DotCharter.Tests.Infrastructure.FakeDspTonePresetService(), LocalizationService.Instance,
            // The Tones mode shows its design column only when the host supplies
            // a panel (RigLab supplies none); this case is about that column.
            toneDesignPanel: ToneDesignPanels.Real());
        rig.AddGear(catalogue.Single(m => m.Id == "overdrive"));
        rig.SelectNodeAt(0);

        var vm = new AmpFxRigViewModel(rig);
        var view = new AmpFxRigView { DataContext = vm };
        var window = new Window { Width = width, Height = height, Content = view };
        window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs(DispatcherPriority.Background);
            for (var pass = 0; pass < 4; pass++) window.UpdateLayout();

            rig.IsBrowserOpen = true;
            for (var pass = 0; pass < 4; pass++) window.UpdateLayout();

            // The Tones mode hosts the panel in a ContentControl (the rig assembly
            // cannot name it); App.axaml's DataTemplate renders the real view. The
            // column is shown while the browser's own designer is OPEN, so that
            // one is opened; the content is then swapped for the fat stub.
            ToneDesignPanels.Of(rig.Browser).IsExpanded = true;
            for (var pass = 0; pass < 2; pass++) window.UpdateLayout();
            Assert.IsType<ContentControl>(Descendant(view, "RigToneDesignPanel")).Content = FatToneDesignVm();
            // Six: the drawer's row is capped from code on SizeChanged and the
            // panel's scroller then measures against the cap, so the layout is
            // settled one pass AFTER the cap is.
            for (var pass = 0; pass < 6; pass++) window.UpdateLayout();

            body(view);
        }
        finally
        {
            window.Close();
        }
    }

    private sealed class DrawerDesignService : DotCharter.Tone.Ai.IToneDesignService
    {
        /// <summary>The copy &amp; paste route is not exercised by this fake.</summary>
        public Task<DotCharter.Tone.Ai.ToneDesignManualPrompt> BuildManualPromptAsync(
            DotCharter.Tone.Ai.ToneDesignRequest request, CancellationToken ct) =>
            throw new NotSupportedException("this fake never builds a copy & paste prompt");

        /// <summary>The copy &amp; paste route is not exercised by this fake.</summary>
        public Task<DotCharter.Tone.Ai.ToneDesignResult> ImportReplyAsync(
            DotCharter.Tone.Ai.ToneDesignRequest request, string reply, IProgress<string>? status,
            CancellationToken ct) =>
            throw new NotSupportedException("this fake never imports a pasted answer");

        public bool IsConfigured => true;
        public event EventHandler? ConfiguredChanged { add { } remove { } }
        public void NotifyConfigurationChanged() { }

        public Task<DotCharter.Tone.Ai.ToneDesignResult> DesignAsync(
            DotCharter.Tone.Ai.ToneDesignRequest request, IProgress<string>? status,
            CancellationToken ct) =>
            throw new NotSupportedException("this case never runs a design");
    }

    private sealed class DrawerSetMatcher : DotCharter.Services.Ai.Match.IToneSetMatcher
    {
        public Task<DotCharter.Services.Ai.Match.ToneSetMatchResult> MatchAsync(
            DotCharter.Services.Ai.Match.ToneSetMatchRequest request, IProgress<string>? status,
            CancellationToken ct) =>
            throw new NotSupportedException("this case never runs a match");
    }

    private sealed class DrawerProposer : DotCharter.Services.Ai.Match.IToneSectionProposer
    {
        public Task<IReadOnlyList<DotCharter.Services.Ai.Match.ToneSectionProposal>> ProposeAsync(
            string songAudioPath, string stem,
            IReadOnlyList<DotCharter.Services.Ai.Match.ToneSection> chartSpans,
            IProgress<string>? status, CancellationToken ct) =>
            throw new NotSupportedException("this case never proposes");
    }

    private static DotCharter.Tone.Audio.Dsp.RsToneConversion.DcRigPreset ToneRig(
        params string[] modules) =>
        new(modules
            .Select(m => new DotCharter.Tone.Audio.Dsp.RsToneConversion.DcRigModule(
                m, new Dictionary<string, float>()))
            .ToList());

    /// <summary>An expanded panel carrying THREE matched sections and a finished
    /// result — the owner's screenshot, which is what makes it taller than the
    /// drawer.</summary>
    private static ToneDesignViewModel FatToneDesignVm()
    {
        var vm = new ToneDesignViewModel(
            static () => new DrawerDesignService(),
            static _ => { },
            static () => new DotCharter.Tone.Audio.Dsp.Presets.DspTonePreset(
                "live", "Live", DotCharter.Tone.Audio.Dsp.Presets.DspToneCategory.Clean, "",
                ToneRig("amp"), DotCharter.Tone.Audio.Dsp.Presets.DspToneOrigin.User),
            static (name, category, _, rig) =>
                new DotCharter.Tone.Audio.Dsp.Presets.DspTonePreset(
                    "s", name, category, "", rig,
                    DotCharter.Tone.Audio.Dsp.Presets.DspToneOrigin.User), LocalizationService.Instance,
            static id => id);

        vm.ConfigureMatch(static () => null, static () => null);
        vm.ConfigureSections(static () => new DrawerProposer(), static () => new DrawerSetMatcher());
        vm.IsExpanded = true;

        foreach (var (label, from, to) in new[]
                     { ("Clean intro", 10, 30), ("Main riff", 30, 62), ("Solo", 62, 96) })
        {
            var row = vm.Sections.AddForTests(label,
                DotCharter.Services.Ai.Match.ToneStems.Guitar,
                TimeSpan.FromSeconds(from), TimeSpan.FromSeconds(to));
            row.PublishCandidates(
                new DotCharter.Services.Ai.Match.ToneMatchResult(
                    [
                        new DotCharter.Services.Ai.Match.ToneMatchCandidate(
                            ToneRig("comp", "amp", "cab"), 0.21, "retrieval"),
                        new DotCharter.Services.Ai.Match.ToneMatchCandidate(
                            ToneRig("ts9", "amp"), 0.44, "refined")
                    ],
                    0.2, TimeSpan.FromSeconds(4), ["rendered 400 rigs"]),
                vm);
        }

        vm.SetResultForTests(
            new DotCharter.Tone.Ai.ToneDesignResult(
                new DotCharter.Tone.Audio.Dsp.Presets.DspTonePreset(
                    "ai", "B Standard Crusher",
                    DotCharter.Tone.Audio.Dsp.Presets.DspToneCategory.Lead, "",
                    ToneRig("rat", "amp", "cab"),
                    DotCharter.Tone.Audio.Dsp.Presets.DspToneOrigin.User),
                "A Rat into a clean power amp keeps the mids forward.",
                0.84, ["gain clamped to 10"], "{}",
                new DotCharter.Tone.Ai.AiUsage("m", 1, 1)),
            "Rat → Power Amp → Cab");
        return vm;
    }
}
