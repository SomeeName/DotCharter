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
    ///     Design D shows ONE Mixer section at a time (its strip), its cards paged on a narrow pane, each card
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
                // Design D: the Q slider (Band card) and the plot (Response card) each scroll inside their own card.
                foreach (var target in new Control[] { mixer.QSlider, mixer.Plot })
                    SweepOver(mixer, target);
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
                var (window, eq) = (mixer.Window, mixer.Eq);
                Control control = target == "plot" ? mixer.Plot : mixer.QSlider;
                var host = HostOf(mixer, control);
                var point = ParkUnderViewportY(mixer, host, control, atViewportY);
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
                var host = HostOf(mixer, mixer.Plot);
                var point = ParkUnderViewportY(mixer, host, mixer.Plot, 20);
                var offset = host.Offset.Y;
                var q = mixer.Eq.Section.Bands[mixer.Eq.SelectedIndex].Q;

                Wheel(mixer.Window, point, -1, RawInputModifiers.Control);

                Assert.NotEqual(q, mixer.Eq.Section.Bands[mixer.Eq.SelectedIndex].Q);
                Assert.Equal(offset, host.Offset.Y);
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
                var window = mixer.Window;
                // The MIDI section's last dropdown (its playing style, on its own card): far enough
                // down its card to park at every viewport row. Without the synth (the headless session has none) design D
                // keeps the card disabled with its reason; a disabled dropdown cannot be clicked, so the card is let live.
                mixer.View.GetVisualDescendants().OfType<Panel>().Single(p => p.Name == "MidiPlayStyleRows").IsEnabled = true;
                Settle(window);
                var combo = mixer.View.GetVisualDescendants().OfType<ComboBox>().Last(c => c.IsEffectivelyVisible && c.Bounds.Height > 0);
                var host = HostOf(mixer, combo);
                ParkUnderViewportY(mixer, host, combo, atViewportY);

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
                var window = mixer.Window;
                // The LAST control a Tab can land on in the Band card: below the fold at offset 0
                // by construction, whatever the card's content is this week.
                var card = mixer.QSlider.GetVisualAncestors().OfType<ColumnFlowCard>().First();
                var host = HostOf(mixer, mixer.QSlider);
                host.Offset = default;
                Settle(window);
                var target = card.GetVisualDescendants().OfType<Control>()
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

    /// <summary>Wheels <paramref name="target" />'s card down and back up with the pointer parked at each of a
    /// column of points across it: the EQ must never change and the card must never stop short.</summary>
    private static void SweepOver(Mixer mixer, Control target)
    {
        var (window, eq) = (mixer.Window, mixer.Eq);
        var host = HostOf(mixer, target);
        var before = Snapshot(eq);
        var max = host.Extent.Height - host.Viewport.Height;
        Assert.True(max > 20, $"the {target.GetType().Name}'s card must scroll for this to mean anything (max offset {max}).");

        var crossed = false;
        var x = target.TranslatePoint(new Point(target.Bounds.Width / 2, 0), window)!.Value.X;
        var viewportTop = host.TranslatePoint(default, window)!.Value.Y;

        for (var y = viewportTop + 10; y < viewportTop + host.Viewport.Height - 5; y += 9)
        {
            host.Offset = default;
            Settle(window);
            var point = new Point(x, y);

            foreach (var direction in new[] { -1, 1 })
            {
                for (var notch = 0; notch < 40; notch++)
                {
                    var hit = window.InputHitTest(point) as Visual;
                    crossed |= hit != null && (hit == target || target.IsVisualAncestorOf(hit));

                    Wheel(window, point, direction);
                    Assert.True(Snapshot(eq).SequenceEqual(before),
                        $"a wheel notch at {point}, scroll offset {host.Offset.Y:0}, over " +
                        $"{hit?.GetType().Name ?? "nothing"} edited the EQ (Q now {eq.SelectedQText}): " +
                        "a bare notch over the Mixer tab must scroll it, never edit a value.");
                }

                // the card's viewport can settle a few px after the first pass: read the range now
                var end = direction < 0 ? host.Extent.Height - host.Viewport.Height : 0;
                Assert.True(Math.Abs(host.Offset.Y - end) < 0.5,
                    $"with the pointer at {point} the card stopped at {host.Offset.Y:0} instead of " +
                    $"{end:0}: something under the pointer is taking the notches " +
                    $"({string.Join("/", (window.InputHitTest(point) as Visual)?.GetSelfAndVisualAncestors().Take(8).Select(v => v.GetType().Name + ":" + (v as Control)?.Name) ?? [])}; scrollers " +
                    $"{string.Join(" | ", (window.InputHitTest(point) as Visual)?.GetVisualAncestors().OfType<ScrollViewer>().Select(v => $"{v.Name}:{v.Offset.Y:0}/{v.Extent.Height:0}-{v.Viewport.Height:0}{(v == host ? "*" : "")}") ?? [])}).");
            }
        }

        Assert.True(crossed, $"the sweep never put the pointer over the {target.GetType().Name}; it proves nothing about it.");
    }

    /// <summary>Shows the card that holds <paramref name="control" /> (a narrow pane pages them) and returns
    /// its scroller, the host a bare wheel notch over the control must move.</summary>
    private static ScrollViewer HostOf(Mixer mixer, Control control)
    {
        var card = control.GetVisualAncestors().OfType<ColumnFlowCard>().First();
        var panel = card.GetVisualAncestors().OfType<ColumnFlowPanel>().First();
        panel.ShowPage(card);
        Settle(mixer.Window);
        return card.GetVisualDescendants().OfType<ScrollViewer>().First();
    }

    private sealed record Mixer(
        Window Window, MixerInspectorView View, EqModuleViewModel Eq, Slider QSlider, ColumnFlowEqPlot Plot);

    private static Mixer Open(double above = 0, string section = "MixerEqBody")
    {
        var services = App.Services
            ?? throw new InvalidOperationException("the headless app session did not boot.");
        var inspector = services.GetRequiredService<InspectorViewModel>();

        var view = new MixerInspectorView { DataContext = inspector };
        // Design D: the EQ is a section of the Mixer's strip; open it (or the one a case asks
        // for), as a click on its entry does. The live EQ below is put in its place either way.
        inspector.MixerSections.ChooseCommand.Execute(MixerSection.Eq);
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
        var plot = eqView.GetVisualDescendants().OfType<ColumnFlowEqPlot>().Single();

        if (section != "MixerEqBody")
        {
            inspector.MixerSections.ChooseCommand.Execute(section switch
            {
                "MixerStemsBody" => MixerSection.Stems,
                "MixerMidiBody" => MixerSection.Midi,
                _ => throw new ArgumentOutOfRangeException(nameof(section), section, "no such Mixer section")
            });
            Settle(window);
        }

        return new Mixer(window, view, eq, qSlider, plot);
    }

    /// <summary>Scrolls the host so <paramref name="control" />'s centre sits
    /// <paramref name="atViewportY" /> px below the viewport's top (clamped to
    /// the scroll range), and returns that centre in window coordinates.</summary>
    private static Point ParkUnderViewportY(Mixer mixer, ScrollViewer host, Control control, double atViewportY)
    {
        // a card's viewport is shorter than the old tab's: park inside it, whatever row a case names
        atViewportY = Math.Clamp(atViewportY, control.Bounds.Height / 2 + 2, Math.Max(control.Bounds.Height / 2 + 2, host.Viewport.Height - control.Bounds.Height / 2 - 2));
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
