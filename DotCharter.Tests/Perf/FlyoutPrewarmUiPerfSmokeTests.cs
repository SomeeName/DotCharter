using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DotCharter.UI.Views.Shell;

namespace DotCharter.Tests.Perf;

/// <summary>
///     What <see cref="PrewarmableFlyout" /> and
///     <see cref="MenuFlyoutPrewarmer.Realize(MenuFlyoutPrewarmer.FlyoutTarget, Size)" />
///     actually do to the transport bar's and status rail's popovers — against the
///     REAL markup, because the whole point is that a flyout's content has no
///     parent, no style scope and no template until something builds one.
/// </summary>
/// <remarks>
///     <para>
///         <b>Measured here, asserted structurally.</b> Headless, Debug,
///         <c>r2r=no</c> (a dev build JIT-compiles its whole startup path, so these
///         are directional, not product figures). Six toolbar popovers, first
///         <c>ShowAt</c> each:
///     </para>
///     <list type="bullet">
///         <item>
///             cold process — <b>535 ms total</b> (43-141 ms each) →
///             <b>78 ms</b> after the prewarm
///         </item>
///         <item>
///             warm process (JIT already paid, i.e. the per-INSTANCE realization
///             the prewarm can move) — <b>514 ms</b> (38-168 ms each) →
///             <b>35 ms</b> (4.8-7.3 ms each)
///         </item>
///         <item>
///             the status rail's three — <b>68 ms</b> → <b>8 ms</b>
///         </item>
///     </list>
///     <para>
///         The prewarm itself costs 95-527 ms and is drained one popover per
///         <c>Background</c> dispatcher turn AFTER first paint, so it never lands
///         as a freeze.
///     </para>
///     <para>
///         Timings are LOGGED, never asserted — a wall-clock assertion flakes the
///         moment suites run in parallel. What is asserted is what can break
///         silently: that nothing is opened, that the content really was realized,
///         and that the flyout still opens normally afterwards.
///     </para>
/// </remarks>
[Collection("HeadlessUI")]
public sealed class FlyoutPrewarmUiPerfSmokeTests
{
    private const double W = 1600;
    private const double H = 1000;
    private static readonly Size Slot = new(W, H);

    /// <summary>
    ///     The premise: a popover that has never been shown has no popup at all,
    ///     so its content has no parent and has never been styled or measured. If
    ///     this stops holding the prewarm is pointless rather than wrong — and we
    ///     want to know.
    /// </summary>
    [Fact]
    public Task A_flyout_that_was_never_shown_has_no_popup_and_an_unparented_body() =>
        WithRail(() => new ToolbarView(), (_, popovers) =>
        {
            // Count pinned against ToolbarView.axaml's `PrewarmableFlyout`s. It
            // read 6 from 0.43 until c111a6c8 added the overflow `>>` popover
            // (2026-08-11) and nobody saw the red, because this suite is not in
            // the default gate. Re-count the markup when you change it.
            Assert.Equal(7, popovers.Count);

            foreach (var p in popovers)
            {
                Assert.Null(p.Flyout.Popup?.Child);
                Assert.True(p.Flyout.Content is Control { Parent: null },
                    "a closed flyout's content already has a parent — the premise of this prewarm changed");
                Assert.True(p.Flyout.Content is Control { DesiredSize.Height: 0 },
                    "a closed flyout's content has already been measured");
            }
        });

    [Fact]
    public Task Realize_builds_and_measures_every_popover_without_opening_one() =>
        WithRail(() => new ToolbarView(), (_, popovers) =>
        {
            var sw = Stopwatch.StartNew();
            var realized = popovers.Sum(p => MenuFlyoutPrewarmer.Realize(p, Slot));
            var cost = sw.Elapsed.TotalMilliseconds;

            Assert.Equal(popovers.Count, realized);

            foreach (var p in popovers)
            {
                // NOTHING may be open. That is the flicker this removes, and it
                // steals focus from whatever the user is doing.
                Assert.False(p.Flyout.IsOpen, "the prewarm opened a popover");
                Assert.NotNull(p.Flyout.Popup);
                Assert.False(p.Flyout.Popup!.IsOpen, "the prewarm left a popup open");

                // …and the BODY was really realized: parented, templated, sized.
                var body = Assert.IsAssignableFrom<Control>(p.Flyout.Content);
                Assert.NotNull(body.Parent);
                Assert.True(body.DesiredSize is { Width: > 1, Height: > 1 },
                    $"a popover body measured {body.DesiredSize} — it was styled by nothing, which is " +
                    "the dead end a DETACHED realize hits and the reason this one goes through the popup");
                Assert.True(body.GetVisualDescendants().Any(),
                    "a popover body produced no visual tree — its templates never applied");
            }

            Debug.WriteLine($"[flyout-prewarm] realized {realized} toolbar popovers in {cost:0.0} ms");
        });

    /// <summary>
    ///     <b>A CLOSE undoes the prewarm, and the close takes it back (#176).</b>
    ///     Closing un-parents the popup and invalidates the body's layout; the next
    ///     <c>ShowAt</c> then re-styled the whole popover inside the click (25-90 ms
    ///     a repeat open on the real window). <c>PrewarmableFlyout.OnClosed</c>
    ///     re-parents the popup into its target's live style scope at
    ///     <c>Background</c> and only THEN re-measures it.
    /// </summary>
    /// <remarks>
    ///     Structural, not timed. What breaks silently: the close could stop
    ///     scheduling the restore, the restore could measure the body DETACHED
    ///     (the unstyled-dropdown bug — <c>Perf/PopoverRestyleUiPerfSmokeTests</c>
    ///     checks the brushes), and it could OPEN something.
    /// </remarks>
    [Fact]
    public Task Closing_a_popover_re_parents_and_re_realizes_it_in_its_style_scope() =>
        WithRail(() => new ToolbarView(), (_, popovers) =>
        {
            foreach (var p in popovers) MenuFlyoutPrewarmer.Realize(p, Slot);

            foreach (var p in popovers)
            {
                var body = Assert.IsAssignableFrom<Layoutable>(p.Flyout.Content);
                Assert.True(body.IsMeasureValid, "the prewarm did not leave a measured body");

                p.Flyout.ShowAt(p.Target);
                p.Flyout.Hide();

                // The close itself still costs the layout: Avalonia un-parents the
                // popup. Nothing is realized synchronously inside the close.
                Assert.False(body.IsMeasureValid,
                    "a closed popover kept its measure — Avalonia's close changed, so the restore in " +
                    "PrewarmableFlyout.OnClosed may be unnecessary (see its remarks)");
                Assert.Null(((ILogical)p.Flyout.Popup!).LogicalParent);

                Dispatcher.UIThread.RunJobs(DispatcherPriority.Background);

                Assert.Same(p.Target, ((ILogical)p.Flyout.Popup!).LogicalParent);
                Assert.True(body.IsMeasureValid, $"{p.Target.Name}: the close never re-realized the popover");
                Assert.True(((ILogical)body).IsAttachedToLogicalTree,
                    $"{p.Target.Name}: the popover was realized DETACHED — its DynamicResources cached fallbacks");
                Assert.False(p.Flyout.IsOpen);
                Assert.False(p.Flyout.Popup!.IsOpen);
            }
        });

    /// <summary>
    ///     A prewarmed popover must still OPEN. Cancelling an open is a strange
    ///     thing to do to a flyout, and a flyout left in a half-shown state would
    ///     be a far worse bug than the lag this removes.
    /// </summary>
    [Fact]
    public Task A_prewarmed_popover_still_opens_and_closes_normally() =>
        WithRail(() => new ToolbarView(), (_, popovers) =>
        {
            foreach (var p in popovers) MenuFlyoutPrewarmer.Realize(p, Slot);

            foreach (var p in popovers)
            {
                p.Flyout.ShowAt(p.Target);
                Assert.True(p.Flyout.IsOpen, "a prewarmed popover refused to open");
                p.Flyout.Hide();
                Assert.False(p.Flyout.IsOpen, "a prewarmed popover refused to close");
            }
        });

    /// <summary>
    ///     The win, asserted by STRUCTURE and logged in milliseconds. Both rails, first
    ///     open with and without the prewarm, on separate instances so neither pays for
    ///     the other. A cold popover's first open has to build its body's visual tree;
    ///     a prewarmed one opens the body it already built. Comparing the two timings
    ///     instead flipped under shard contention (#246): one sample a side, one GC on
    ///     the warm side, and the "direction" inverted with nothing broken.
    /// </summary>
    [Fact]
    public Task The_first_open_of_every_popover_reuses_the_prewarmed_body()
    {
        var report = new List<string>();

        return HeadlessSessionFixture.Dispatch(() =>
        {
            var windows = new List<Window>();

            void Round(string name, Func<Control> build)
            {
                var cold = build();
                windows.Add(Show(cold));
                var coldTargets = MenuFlyoutPrewarmer.FlyoutsUnder(cold);
                var coldOpens = coldTargets.Select(FirstOpen).ToList();

                var warm = build();
                windows.Add(Show(warm));
                var warmTargets = MenuFlyoutPrewarmer.FlyoutsUnder(warm);
                foreach (var t in warmTargets) MenuFlyoutPrewarmer.Realize(t, Slot);
                var warmOpens = warmTargets.Select(FirstOpen).ToList();

                report.Add($"{name}: cold {coldOpens.Sum(o => o.Ms):0.0} ms -> prewarmed " +
                           $"{warmOpens.Sum(o => o.Ms):0.0} ms over {coldTargets.Count} popovers");

                Assert.Equal(coldTargets.Count, warmTargets.Count);
                for (var i = 0; i < warmOpens.Count; i++)
                {
                    var target = warmTargets[i].Target.Name ?? warmTargets[i].Target.GetType().Name;

                    // The cold side proves the check can fail: an un-prewarmed body
                    // is unmeasured and outside any logical tree until its first open.
                    Assert.False(coldOpens[i].BuiltBefore,
                        $"{name}/{target}: the COLD body was already measured and attached before its " +
                        "first open — this case no longer measures anything");

                    Assert.True(warmOpens[i].BuiltBefore,
                        $"{name}/{target}: the prewarmed body was not measured and attached before its " +
                        "first open — the realize is not sticking");
                    Assert.True(warmOpens[i].SameBody,
                        $"{name}/{target}: the first open showed a different body from the one the prewarm built");
                }
            }

            Round("toolbar", () => new ToolbarView());
            Round("status rail", () => new StatusBar());

            foreach (var w in windows) w.Close();

            foreach (var line in report) Debug.WriteLine($"[flyout-prewarm] {line}");
        }, CancellationToken.None);
    }

    /// <summary>The status rail's two popovers, which no other case here
    /// covers: the session-log tail, plus the profile switcher — the only one
    /// whose body is a <c>DataTemplate</c>.
    /// <para>
    ///     It said THREE ("two message panels sharing one template") until
    ///     <c>c678f479</c> collapsed the warning/error chips into one message
    ///     panel (2026-08-11). The assertion has been red ever since and nobody
    ///     saw it, because this suite is not in the default gate.
    /// </para></summary>
    [Fact]
    public Task The_status_rails_popovers_are_reachable_and_realizable() =>
        WithRail(() => new StatusBar(), (_, popovers) =>
        {
            Assert.Equal(2, popovers.Count);
            Assert.Equal(2, popovers.Sum(p => MenuFlyoutPrewarmer.Realize(p, Slot)));
            Assert.All(popovers, p => Assert.False(p.Flyout.IsOpen));
        });

    // ── helpers ─────────────────────────────────────────────────────────────

    private static Window Show(Control view)
    {
        var window = new Window { Width = W, Height = H, Content = view };
        window.Show();
        view.Measure(Slot);
        view.Arrange(new Rect(0, 0, W, H));
        view.UpdateLayout();
        return window;
    }

    /// <summary>One first open: its time (logged only), whether the body was already
    /// BUILT before it (measured, in a logical tree — the prewarm's two marks), and
    /// whether the popup showed that same body.</summary>
    private static (double Ms, bool BuiltBefore, bool SameBody) FirstOpen(MenuFlyoutPrewarmer.FlyoutTarget t)
    {
        var body = t.Flyout.Content as Layoutable;
        var builtBefore = body is { IsMeasureValid: true } && ((ILogical)body).IsAttachedToLogicalTree;
        var sw = Stopwatch.StartNew();
        t.Flyout.ShowAt(t.Target);
        var ms = sw.Elapsed.TotalMilliseconds;
        var shown = t.Flyout.Popup?.Child;
        var sameBody = body is not null && shown is not null &&
                       (ReferenceEquals(shown, body) || shown.GetVisualDescendants().Contains(body) ||
                        body.GetLogicalAncestors().Contains(shown));
        t.Flyout.Hide();
        return (ms, builtBefore, sameBody);
    }

    private static Task WithRail(
        Func<Control> factory, Action<Control, IReadOnlyList<MenuFlyoutPrewarmer.FlyoutTarget>> assert) =>
        HeadlessSessionFixture.Dispatch(() =>
        {
            var view = factory();
            var window = Show(view);
            try
            {
                assert(view, MenuFlyoutPrewarmer.FlyoutsUnder(view));
            }
            finally
            {
                window.Close();
            }
        }, CancellationToken.None);
}
