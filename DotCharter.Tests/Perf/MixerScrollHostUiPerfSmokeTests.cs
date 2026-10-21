using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DotCharter.Localization;
using DotCharter.UI;
using DotCharter.UI.Controls;
using DotCharter.UI.ViewModels.Editor;
using DotCharter.UI.ViewModels.Editor.Inspector;
using DotCharter.UI.Views.Editor.Inspector;
using Microsoft.Extensions.DependencyInjection;

namespace DotCharter.Tests.Perf;

/// <summary>
///     The editor's Inspector ▸ Mixer tab, scrolled for real: the real
///     <see cref="MixerInspectorView" /> on the app's own
///     <see cref="InspectorViewModel" />, driven with headless wheel and click
///     input so hit testing, the tunnel and the bring-into-view route are the
///     product's.
///     <para>
///         <b>#47 — scrolling sometimes moved the EQ's Q.</b> It was not the Q
///         SLIDER: <c>SliderWheelPolicy</c> stopped every notch over it. It was
///         the EQ PLOT, 100 px tall in the middle of the tab, which turned any
///         notch into a Q edit of the selected band (Band 1, the high pass)
///         — and the Q slider below it showed the change. A few notches into a
///         scroll the plot slid under the pointer and took every notch after it,
///         so the panel stopped dead and Q walked. "Sometimes" was where the
///         pointer happened to be.
///     </para>
///     <para>
///         <b>#48 — opening the dropdown scrolled the panel up.</b> The
///         dropdown list brings its selected item into view as it opens; the
///         request bubbles out of the popup through the <c>Popup</c> (its logical
///         parent) into the inspector's scroller, which — popups being in-window
///         — maps the item, still at its unplaced position, into its content and
///         scrolls to it. Measured here before the fix: 586 → 526 and 446 → 385.
///     </para>
/// </summary>
[Collection("HeadlessUI")]
public sealed class MixerScrollHostUiPerfSmokeTests
{
    private const double MixerWidth = 340;

    /// <summary>
    ///     Since #601 the Mixer shows ONE section at a time behind its rail, and the EQ section
    ///     alone fits a tall pane. The harness opens it at the height a 1280x720 window gives the
    ///     inspector's section body (~160 px under the tab strip and the pinned row), where the EQ
    ///     scrolls — the shape the owner's laptop has, and the one these cases are about.
    /// </summary>
    private const double MixerHeight = 160;

    /// <summary>
    ///     Wheel the whole tab down and back up with the pointer parked at each
    ///     of a column of points — so every wheel-adjustable control in the EQ
    ///     passes under it at some scroll position — and the EQ must never
    ///     change, and the panel must never stop short.
    /// </summary>
    [Fact]
    public Task ScrollingTheMixer_neverEditsTheEq_whateverPassesUnderThePointer() =>
        HeadlessSessionFixture.Dispatch(() =>
        {
            var mixer = Open();
            try
            {
                var (window, host, eq) = (mixer.Window, mixer.Host, mixer.Eq);
                var before = Snapshot(eq);
                var max = host.Extent.Height - host.Viewport.Height;
                Assert.True(max > 100, $"the Mixer tab must scroll for this to mean anything (max offset {max}).");

                var crossedQ = false;
                var crossedPlot = false;
                var x = mixer.QSlider.TranslatePoint(new Point(mixer.QSlider.Bounds.Width / 2, 0), window)!.Value.X;
                var viewportTop = host.TranslatePoint(default, window)!.Value.Y;

                for (var y = viewportTop + 20; y < viewportTop + host.Viewport.Height - 10; y += 40)
                {
                    host.Offset = default;
                    Settle(window);
                    var point = new Point(x, y);

                    foreach (var direction in new[] { -1, 1 })
                    {
                        for (var notch = 0; notch < 60; notch++)
                        {
                            var hit = window.InputHitTest(point) as Visual;
                            crossedQ |= hit != null && (hit == mixer.QSlider || mixer.QSlider.IsVisualAncestorOf(hit));
                            crossedPlot |= hit == mixer.Plot;

                            Wheel(window, point, direction);
                            Assert.True(Snapshot(eq).SequenceEqual(before),
                                $"a wheel notch at {point}, scroll offset {host.Offset.Y:0}, over " +
                                $"{hit?.GetType().Name ?? "nothing"} edited the EQ (Q now {eq.SelectedQText}) — " +
                                "a bare notch over the Mixer tab must scroll it, never edit a value.");
                        }

                        var end = direction < 0 ? max : 0;
                        Assert.True(Math.Abs(host.Offset.Y - end) < 0.5,
                            $"with the pointer at {point} the Mixer tab stopped at {host.Offset.Y:0} instead of " +
                            $"{end:0} — something under the pointer is taking the notches.");
                    }
                }

                Assert.True(crossedQ, "the sweep never put the pointer over the EQ's Q slider — it proves nothing about it.");
                Assert.True(crossedPlot, "the sweep never put the pointer over the EQ plot — it proves nothing about it.");
            }
            finally
            {
                mixer.Window.Close();
            }
        }, CancellationToken.None);

    /// <summary>
    ///     The two controls the issue names, each parked under the pointer at
    ///     several scroll positions: a bare notch scrolls the HOST (the offset
    ///     moves) and leaves Q exactly where it was.
    /// </summary>
    [Theory]
    [InlineData("q-slider", 100)]
    [InlineData("q-slider", 135)]
    [InlineData("q-slider", 120)]
    [InlineData("plot", 20)]
    [InlineData("plot", 60)]
    [InlineData("plot", 100)]
    public Task ABareNotchOverTheQControls_scrollsTheHost_andLeavesQAlone(string target, double atViewportY) =>
        HeadlessSessionFixture.Dispatch(() =>
        {
            var mixer = Open();
            try
            {
                var (window, host, eq) = (mixer.Window, mixer.Host, mixer.Eq);
                Control control = target == "plot" ? mixer.Plot : mixer.QSlider;
                var point = ParkUnderViewportY(mixer, control, atViewportY);
                var q = eq.Section.Bands[eq.SelectedIndex].Q;

                foreach (var direction in new[] { -1, 1 })
                {
                    var offset = host.Offset.Y;
                    Wheel(window, point, direction);

                    Assert.Equal(q, eq.Section.Bands[eq.SelectedIndex].Q);
                    Assert.True(Math.Abs(host.Offset.Y - offset) > 1,
                        $"a notch over the {target} at offset {offset:0} did not scroll the Mixer tab.");
                }
            }
            finally
            {
                mixer.Window.Close();
            }
        }, CancellationToken.None);

    /// <summary>
    ///     The plot keeps its Q gesture behind a modifier nobody presses by
    ///     accident while scrolling: Ctrl + wheel still widens / narrows Q, and
    ///     the host does not move. Without this the policy could pass by simply
    ///     killing the plot's wheel.
    /// </summary>
    [Fact]
    public Task CtrlWheelOverThePlot_stillAdjustsQ_andDoesNotScroll() =>
        HeadlessSessionFixture.Dispatch(() =>
        {
            var mixer = Open();
            try
            {
                var point = ParkUnderViewportY(mixer, mixer.Plot, 60);
                var offset = mixer.Host.Offset.Y;
                var q = mixer.Eq.Section.Bands[mixer.Eq.SelectedIndex].Q;

                Wheel(mixer.Window, point, -1, RawInputModifiers.Control);

                Assert.NotEqual(q, mixer.Eq.Section.Bands[mixer.Eq.SelectedIndex].Q);
                Assert.Equal(offset, mixer.Host.Offset.Y);
            }
            finally
            {
                mixer.Window.Close();
            }
        }, CancellationToken.None);

    /// <summary>
    ///     #48: open a Mixer dropdown (the EQ preset picker) at several scrolled
    ///     positions, the way a user does (a click on its arrow), and the
    ///     inspector must not move — same offset, ComboBox under the same point.
    ///     <para>
    ///         The Mixer is docked BELOW other content, the editor's real shape
    ///         (the inspector is a pane, not the window): that is what puts the
    ///         list's not-yet-placed position outside the inspector's viewport,
    ///         which is when the stray request scrolled it.
    ///     </para>
    /// </summary>
    [Theory]
    [InlineData(20)]
    [InlineData(70)]
    [InlineData(120)]
    public Task OpeningADropdown_doesNotScrollTheMixer(double atViewportY) =>
        HeadlessSessionFixture.Dispatch(() =>
        {
            var mixer = Open(above: 400, section: "MixerMidiBody");
            try
            {
                var (window, host) = (mixer.Window, mixer.Host);
                // The MIDI synth section's last dropdown (its playing style): far
                // enough down the section to park at every viewport row. (The EQ
                // preset picker this was written on sits at the top of its own
                // section since #601, so it can never be scrolled.)
                var combo = mixer.View.GetVisualDescendants().OfType<ComboBox>().Last(c => c.IsEffectivelyVisible && c.Bounds.Height > 0);
                ParkUnderViewportY(mixer, combo, atViewportY);

                var offset = host.Offset.Y;
                Assert.True(offset > 10, $"the Mixer must be scrolled for this to mean anything (offset {offset}).");
                var comboAt = combo.TranslatePoint(default, window)!.Value;
                var arrow = combo.TranslatePoint(new Point(combo.Bounds.Width - 8, combo.Bounds.Height / 2), window)!.Value;

                window.MouseDown(arrow, MouseButton.Left);
                window.MouseUp(arrow, MouseButton.Left);
                Settle(window);

                Assert.True(combo.IsDropDownOpen, "the click did not open the dropdown — nothing was tested.");
                Assert.True(Math.Abs(host.Offset.Y - offset) < 0.5,
                    $"opening the dropdown scrolled the Mixer from {offset:0} to {host.Offset.Y:0}.");
                Assert.Equal(comboAt, combo.TranslatePoint(default, window)!.Value);

                combo.IsDropDownOpen = false;
                Settle(window);
                Assert.True(Math.Abs(host.Offset.Y - offset) < 0.5,
                    $"closing the dropdown scrolled the Mixer from {offset:0} to {host.Offset.Y:0}.");
            }
            finally
            {
                mixer.Window.Close();
            }
        }, CancellationToken.None);

    /// <summary>
    ///     …and real bring-into-view still works: Tab focus landing on a control
    ///     below the fold scrolls the Mixer to it. The popup policy must stop
    ///     requests from INSIDE a popup, not every request.
    /// </summary>
    [Fact]
    public Task KeyboardFocusOnAnOffscreenControl_stillScrollsItIntoView() =>
        HeadlessSessionFixture.Dispatch(() =>
        {
            var mixer = Open(above: 400);
            try
            {
                var (window, host) = (mixer.Window, mixer.Host);
                host.Offset = default;
                Settle(window);

                // The LAST control a Tab can land on: below the fold at offset 0
                // by construction, whatever the tab's content is this week.
                var target = mixer.View.GetVisualDescendants().OfType<Control>()
                    .Last(c => c.Focusable && c.IsEffectivelyEnabled && c.IsEffectivelyVisible && c.Bounds.Height > 0);
                Assert.False(IsInViewport(host, target), $"{target.GetType().Name} must start below the fold.");

                Assert.True(target.Focus(NavigationMethod.Tab), $"{target.GetType().Name} did not take Tab focus.");
                Settle(window);

                Assert.True(host.Offset.Y > 0, "Tab focus on an off-screen control no longer scrolls the Mixer to it.");
                Assert.True(IsInViewport(host, target), "Tab focus scrolled the Mixer, but not far enough to show the control.");
            }
            finally
            {
                mixer.Window.Close();
            }
        }, CancellationToken.None);

    // ── harness ────────────────────────────────────────────────────────────

    private sealed record Mixer(
        Window Window, MixerInspectorView View, ScrollViewer Host, EqModuleViewModel Eq, Slider QSlider, EqPlotControl Plot);

    private static Mixer Open(double above = 0, string section = "MixerEqBody")
    {
        var services = App.Services
            ?? throw new InvalidOperationException("the headless app session did not boot.");
        var inspector = services.GetRequiredService<InspectorViewModel>();

        var view = new MixerInspectorView { DataContext = inspector };
        // #601: the EQ is a section of the Mixer's rail; open it (or the one a case asks
        // for), as a click on its entry does. The live EQ below is put in its place either way.
        view.Rail.Choose("MixerEqBody");
        var root = new Grid { RowDefinitions = new RowDefinitions($"{above},{MixerHeight}") };
        Grid.SetRow(view, 1);
        root.Children.Add(view);
        var window = new Window { Width = MixerWidth, Height = MixerHeight + above, Content = root };
        window.Show();
        Settle(window);

        // The headless session opens no audio device, so the app's own EQ is the
        // DISABLED module, whose sliders and plot take no input at all — a test on
        // it would pass whatever the policy did. A live module over a fake engine
        // is put in its place, on the real view inside the real host.
        var eqView = view.GetVisualDescendants().OfType<EqModuleView>().Single();
        var eq = new EqModuleViewModel(new FakeAudioPlaybackService { SupportsParametricEq = true }, LocalizationService.Instance);
        eqView.DataContext = eq;
        Settle(window);
        Assert.True(eq.IsLive, "the EQ module under test is not live — every control in it is disabled.");

        // The Q slider is the one bound to SelectedQNormalized: give it a value
        // no other slider holds and find it by that, rather than by row order.
        eq.SelectedQNormalized = 0.4375;
        Settle(window);
        var qSlider = eqView.GetVisualDescendants().OfType<Slider>().Single(s => Math.Abs(s.Value - eq.SelectedQNormalized) < 1e-9);
        var plot = eqView.GetVisualDescendants().OfType<EqPlotControl>().Single();

        var host = view.GetVisualDescendants().OfType<ScrollViewer>()
            .First(s => !s.GetVisualAncestors().OfType<ScrollViewer>().Any());
        if (section != "MixerEqBody")
        {
            view.Rail.Choose(section);
            Settle(window);
        }

        return new Mixer(window, view, host, eq, qSlider, plot);
    }

    /// <summary>Scrolls the host so <paramref name="control" />'s centre sits
    /// <paramref name="atViewportY" /> px below the viewport's top (clamped to
    /// the scroll range), and returns that centre in window coordinates.</summary>
    private static Point ParkUnderViewportY(Mixer mixer, Control control, double atViewportY)
    {
        var host = mixer.Host;
        var centreInContent = control.TranslatePoint(new Point(0, control.Bounds.Height / 2), host)!.Value.Y + host.Offset.Y;
        var max = host.Extent.Height - host.Viewport.Height;
        host.Offset = new Vector(0, Math.Clamp(centreInContent - atViewportY, 0, max));
        Settle(mixer.Window);

        var centre = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), mixer.Window)!.Value;
        // Hit testing reads the composed scene, which can trail the layout after a programmatic
        // scroll; tick until it catches up (bounded, #55), then fail with what it hit instead.
        Visual? Hit() => mixer.Window.InputHitTest(centre) as Visual;
        bool OnControl() => Hit() is { } h && (h == control || control.IsVisualAncestorOf(h));
        RenderQuiescence.WaitUntil(OnControl, () => Settle(mixer.Window), () =>
        {
            var hit = Hit();
            return $"{control.GetType().Name} is not under the pointer at {centre} (hit {hit?.GetType().Name ?? "nothing"} " +
                   $"{(hit as Control)?.Name} in {string.Join("/", hit?.GetVisualAncestors().Take(6).Select(a => a.GetType().Name) ?? [])}; " +
                   $"offset {host.Offset.Y:0} of {max:0}).";
        });
        return centre;
    }

    private static void Wheel(Window window, Point point, int direction, RawInputModifiers modifiers = RawInputModifiers.None)
    {
        // The notch is routed by the composed scene's hit test, which can trail the previous notch's
        // scroll under load (#55): wait until the scene under the pointer stops changing first.
        SettleAt(window, point);
        window.MouseWheel(point, new Vector(0, direction), modifiers);
        Settle(window);
    }

    /// <summary>Settles, then ticks until hit testing at <paramref name="point" /> returns the same
    /// visual on two consecutive frames — layout is idle, so the scene has caught up there.</summary>
    private static void SettleAt(Window window, Point point)
    {
        Settle(window);
        Visual? last = null;
        var first = true;
        RenderQuiescence.WaitUntil(
            () =>
            {
                var hit = window.InputHitTest(point) as Visual;
                var same = !first && hit == last;
                (first, last) = (false, hit);
                return same;
            },
            () => SettleOnce(window),
            () => $"hit testing at {point} kept changing ({last?.GetType().Name ?? "nothing"} on the last frame).");
    }

    /// <summary>
    ///     Runs layout, forces a render, then waits (bounded, #55) until the composed scene agrees with
    ///     the layout: hit testing a visible control at its layout centre must find that control.
    ///     <c>ForceRenderTimerTick</c> only prompts a render, so under load the scene used for hit
    ///     testing can trail layout, and a wheel at a stale position reads as a product fault.
    /// </summary>
    private static void Settle(Window window)
    {
        SettleOnce(window);
        var probe = FindProbe(window);
        if (probe is null) return;

        var (control, centre) = probe.Value;
        Visual? Hit() => window.InputHitTest(centre) as Visual;
        RenderQuiescence.WaitUntil(
            () => Hit() is { } h && (h == control || control.IsVisualAncestorOf(h)),
            () => SettleOnce(window),
            () => $"{control.GetType().Name} {control.Name} at layout position {centre} but hit testing found " +
                  $"{Hit()?.GetType().Name ?? "nothing"}.");
    }

    private static void SettleOnce(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        // Hit testing reads the last RENDERED scene, not the layout just run:
        // without a frame the pointer "hits" where the content used to be.
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>A visible, hit-testable control whose centre is inside every scroll viewport above it.
    /// None while a dropdown is open (its popup may legitimately cover the control).</summary>
    private static (Control Control, Point Centre)? FindProbe(Window window)
    {
        var all = window.GetVisualDescendants().ToList();
        if (all.OfType<ComboBox>().Any(c => c.IsDropDownOpen)) return null;

        foreach (var control in all.OfType<Control>())
        {
            if (!control.Focusable || !control.IsHitTestVisible || !control.IsEffectivelyVisible || control.Bounds.Height < 4) continue;

            var inView = true;
            foreach (var scroller in control.GetVisualAncestors().OfType<ScrollViewer>())
            {
                var local = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), scroller);
                inView &= local is { } l && l.X >= 0 && l.Y >= 0 && l.X < scroller.Viewport.Width && l.Y < scroller.Viewport.Height;
            }

            if (!inView) continue;
            var centre = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window);
            if (centre is { } c && c.X >= 0 && c.Y >= 0 && c.X < window.Bounds.Width && c.Y < window.Bounds.Height)
                return (control, c);
        }

        return null;
    }

    private static bool IsInViewport(ScrollViewer host, Control control)
    {
        var top = control.TranslatePoint(default, host)!.Value.Y;
        return top >= 0 && top + control.Bounds.Height <= host.Viewport.Height;
    }

    private static List<object> Snapshot(EqModuleViewModel eq) =>
        [.. eq.Section.Bands.Cast<object>(), eq.Section.TrimDb, eq.SelectedIndex];
}
