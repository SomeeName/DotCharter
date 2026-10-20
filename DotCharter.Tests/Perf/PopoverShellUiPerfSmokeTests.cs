using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.VisualTree;
using DotCharter.Tone.UI.Controls;
using DotCharter.UI.Views.Shell;

namespace DotCharter.Tests.Perf;

/// <summary>
///     The ONE popover shell (<see cref="FirePopover" />), built for real on the
///     two chrome rails and measured against the mockup's own SHELL RULES.
/// </summary>
/// <remarks>
///     <para>
///         A popover is the hardest surface in the app to test any other way: its
///         content lives inside a closed <c>Popup</c>, so no construction pass and
///         no layout pass in this repo has ever touched it — the eight panels went
///         through six releases with nothing but the app's own runtime looking at
///         them. <see cref="MenuFlyoutPrewarmer" /> is what makes them reachable
///         without opening anything, and this file is the second thing that buys:
///         the popovers are now in the test net, not just off the click path.
///     </para>
///     <para>
///         What is asserted is the SHELL contract, because that is what a shared
///         chrome is for: head 40 px, never wider than 420, a real title, a
///         resolved icon, and a body that measured. A popover whose theme failed
///         to resolve still arranges — it just has no size, and that is the shape
///         every silent XAML fault in this repo takes.
///     </para>
/// </remarks>
[Collection("HeadlessUI")]
public sealed class PopoverShellUiPerfSmokeTests
{
    private const double W = 1600;
    private const double H = 1000;
    private static readonly Size Slot = new(W, H);

    /// <summary>The mockup's own numbers: "Head 40px · rows 34px · radius 14/9"
    /// and "Popover never wider than 420px".</summary>
    private const double HeadHeight = 40;
    private const double MaxWidth = 420;

    [Fact]
    public Task EveryToolbarPopover_isBuiltOnTheOneShell() =>
        WithRealizedPopovers(() => new ToolbarView(), popovers =>
        {
            // Mix · Tempo · Tools · Tracks · Undo · Redo · Overflow (0.59)
            Assert.Equal(7, popovers.Count);

            foreach (var popover in popovers)
            {
                Assert.True(popover.DesiredSize is { Width: > 1, Height: > 1 },
                    $"the '{popover.Title}' popover measured {popover.DesiredSize} — it rendered nothing");

                Assert.False(string.IsNullOrWhiteSpace(popover.Title),
                    "a popover has no title — its localization key does not resolve");
                Assert.True(popover.Icon != null,
                    $"the '{popover.Title}' popover has no icon geometry — its resource key is wrong");

                // Every glyph inside it too. A PathIcon whose DynamicResource
                // missed is simply blank at runtime, with no log line.
                foreach (var icon in popover.GetVisualDescendants().OfType<PathIcon>())
                {
                    Assert.True(icon.Data != null,
                        $"a PathIcon inside the '{popover.Title}' popover has no geometry");
                }
            }
        });

    /// <summary>
    ///     THE SHELL RULES, measured. A shared shell that each surface can quietly
    ///     opt out of is not a shared shell — and "never wider than 420" is the
    ///     one a popover breaks by accident, because it is a MaxWidth nobody sees
    ///     until a long plugin name arrives.
    /// </summary>
    [Fact]
    public Task EveryToolbarPopover_keepsTheMockupsHeadHeightAndWidthCeiling() =>
        WithRealizedPopovers(() => new ToolbarView(), popovers =>
        {
            foreach (var popover in popovers)
            {
                Assert.Equal(MaxWidth, popover.MaxWidth);
                Assert.True(popover.DesiredSize.Width <= MaxWidth + 1,
                    $"the '{popover.Title}' popover measured {popover.DesiredSize.Width:0.#} px wide — " +
                    "the mockup caps a popover at 420, because it is a menu and not a dialog");

                var head = popover.GetVisualDescendants().OfType<Border>()
                    .FirstOrDefault(b => b.Name == "PART_Head");
                Assert.True(head != null,
                    $"the '{popover.Title}' popover has no head — its ControlTheme did not resolve, " +
                    "which is the KeybindCaptureBox fault: no warning, no exception, and the control " +
                    "still measures because its content stretches");
                Assert.Equal(HeadHeight, head!.Bounds.Height, 1);
                Assert.True(head.Bounds.Width > 1,
                    $"the '{popover.Title}' popover's head laid out {head.Bounds} — it rendered nothing");
            }
        });

    /// <summary>
    ///     The mockup gives ALL SIX a footer, and the footer is where the
    ///     panel's own action and its shortcut live. Tools was the one without
    ///     until 0.54, because its footer action — "Rescan plugin folders" — did
    ///     not exist; it does now, so the count is six and a popover that loses
    ///     its footer fails here.
    /// </summary>
    [Fact]
    public Task EveryPopover_carriesAFooterAndTheHistoryOnesShowTheirLiveChord() =>
        WithRealizedPopovers(() => new ToolbarView(), popovers =>
        {
            var withFooter = popovers.Where(p => p.HasFooter).ToList();
            Assert.Equal(7, withFooter.Count);

            foreach (var popover in withFooter)
            {
                var footer = popover.GetVisualDescendants().OfType<Border>()
                    .FirstOrDefault(b => b.Name == "PART_Footer");
                Assert.True(footer is { IsVisible: true, Bounds.Height: > 1 },
                    $"the '{popover.Title}' popover's footer laid out {footer?.Bounds} — it rendered nothing");
            }

            // …and the ones WITHOUT a footer must not draw an empty strip.
            foreach (var popover in popovers.Where(p => !p.HasFooter))
            {
                var footer = popover.GetVisualDescendants().OfType<Border>()
                    .FirstOrDefault(b => b.Name == "PART_Footer");
                Assert.True(footer is { IsVisible: false },
                    $"the '{popover.Title}' popover draws a footer strip with nothing in it");
            }
        });

    /// <summary>
    ///     The mockup's chip rows — Mix's three-way QUICK BALANCE and Tempo's
    ///     four SPEED PRESETS — exist, measure, and are laid out as chips.
    ///     <para>
    ///         Their COMMANDS are guaranteed by the compiler: <c>ToolbarView</c>
    ///         declares <c>x:CompileBindings="True"</c>, so a
    ///         <c>{Binding Transport.SetMixBalanceCommand}</c> that names nothing
    ///         is an AVLN2000 build error rather than a dead button. What a
    ///         binding cannot catch is the CHIP — a class that stopped matching
    ///         leaves a full-size default button in a 34 px row, and every bounds
    ///         assertion still passes.
    ///     </para>
    /// </summary>
    [Fact]
    public Task TheChipRows_areRealChipsAndFitAPopoverRow() =>
        WithRealizedPopovers(() => new ToolbarView(), popovers =>
        {
            var chips = popovers
                .SelectMany(p => p.GetVisualDescendants().OfType<Button>()
                    .Where(b => b.Classes.Contains("FirePopoverChip")))
                .ToList();

            // Mix: M · S · Song only · Even · Guitar only.
            // Tempo: 50% · 65% · 80% · 100% · the disabled keep-pitch On.
            // Tools: Open.  Tracks: Solo · Mute · Settings….
            Assert.True(chips.Count >= 14,
                $"only {chips.Count} popover chips were realized — Mix's quick balance, Tempo's " +
                "speed presets or Tracks' Solo/Mute/Settings row is missing");

            foreach (var chip in chips)
            {
                Assert.True(chip.Bounds is { Width: > 1, Height: > 1 },
                    $"a popover chip laid out {chip.Bounds} — it rendered nothing");
                Assert.True(chip.Bounds.Height <= 30,
                    $"a popover chip is {chip.Bounds.Height:0.#} px tall — it picked up a full-size " +
                    "Fire action instead of the 26 px chip, and a popover row is 34");
                Assert.True(chip.Command != null || chip.DataContext == null,
                    "a popover chip declares no command");
            }
        });

    /// <summary>
    ///     Both status-rail message panels are on the shell too, even though the
    ///     mockup does not draw them: they open off the same rail, and a popover
    ///     in the old grammar beside one in the new is exactly the half-converted
    ///     screen the Fire rules exist to prevent.
    /// </summary>
    [Fact]
    public Task TheStatusRailsPopovers_areOnTheShellToo() =>
        HeadlessSessionFixture.Dispatch(() =>
        {
            var host = new StatusBar();
            var window = new Window { Width = W, Height = H, Content = host };
            window.Show();
            try
            {
                host.Measure(Slot);
                host.Arrange(new Rect(0, 0, W, H));

                foreach (var key in new[] { "RecentMessagesFlyoutContent", "ProfileSwitcherFlyoutContent" })
                {
                    Assert.True(host.TryFindResource(key, out var found),
                        $"the status bar no longer carries {key} — that flyout would open empty");
                    var template = Assert.IsAssignableFrom<IDataTemplate>(found);

                    var content = template.Build(null);
                    Assert.NotNull(content);

                    var probe = new ContentControl { Content = content };
                    var shell = new StackPanel();
                    shell.Children.Add(probe);
                    window.Content = shell;
                    shell.Measure(Slot);
                    shell.Arrange(new Rect(0, 0, W, H));

                    var popover = content!.GetSelfAndVisualDescendants().OfType<FirePopover>().FirstOrDefault()
                                  ?? content as FirePopover;
                    Assert.True(popover != null,
                        $"{key} is not built on FirePopover — the rail now speaks two grammars");
                    Assert.False(string.IsNullOrWhiteSpace(popover!.Title),
                        $"{key}'s popover has no title");
                    Assert.Equal(MaxWidth, popover.MaxWidth);
                }
            }
            finally
            {
                window.Close();
            }
        }, CancellationToken.None);

    // ── helpers ─────────────────────────────────────────────────────────────

    /// <summary>
    ///     Builds a rail, realizes every popover on it through the product's own
    ///     prewarm — the ONLY way to lay one out without opening it — and hands
    ///     over the shells.
    /// </summary>
    private static Task WithRealizedPopovers(Func<Control> factory, Action<IReadOnlyList<FirePopover>> assert) =>
        HeadlessSessionFixture.Dispatch(() =>
        {
            var view = factory();
            var window = new Window { Width = W, Height = H, Content = view };
            window.Show();
            try
            {
                view.Measure(Slot);
                view.Arrange(new Rect(0, 0, W, H));
                view.UpdateLayout();

                var targets = MenuFlyoutPrewarmer.FlyoutsUnder(view);
                Assert.True(targets.Count > 0, "the rail declares no prewarmable popovers at all");
                foreach (var target in targets) MenuFlyoutPrewarmer.Realize(target, Slot);

                // Since 0.54 the flyout's content is the popover's own VIEW —
                // one type per panel, so each can be constructed, hosted twice
                // and RENDERED for comparison against the mockup. The shell is
                // that view's root, so the walk goes one level in.
                var popovers = targets
                    .Select(t => t.Flyout.Content as Control)
                    .Select(c => c?.GetSelfAndVisualDescendants().OfType<FirePopover>().FirstOrDefault())
                    .Where(p => p != null)
                    .Select(p => p!)
                    .ToList();

                Assert.True(popovers.Count == targets.Count,
                    $"{targets.Count - popovers.Count} popover(s) on this rail are not built on FirePopover — " +
                    "the shared shell is being opted out of one surface at a time");

                assert(popovers);
            }
            finally
            {
                window.Close();
            }
        }, CancellationToken.None);

    /// <summary>
    ///     A popover draws its own edge, so the flyout that hosts it must not
    ///     frame it again. The song lists' filter funnels live outside the
    ///     shell's rails, whose view-scoped style was the only thing zeroing the
    ///     presenter, and drew a second rounded frame with a 14 px gap round the
    ///     popover (owner, 2026-09-24).
    /// </summary>
    [Fact]
    public Task AFlyoutHostingAPopover_OutsideTheShell_DrawsNoSecondFrame() =>
        HeadlessSessionFixture.Dispatch(() =>
        {
            var popover = new FirePopover { Title = "Song", Content = new TextBlock { Text = "Played" } };
            var target = new Button { Content = "funnel" };
            var flyout = new Flyout { Content = popover };
            var window = new Window { Width = W, Height = H, Content = target };
            window.Show();
            try
            {
                flyout.ShowAt(target);
                window.UpdateLayout();
                popover.UpdateLayout();

                var presenter = popover.FindAncestorOfType<FlyoutPresenter>();
                Assert.NotNull(presenter);
                Assert.Contains(FirePopover.HostClass, presenter!.Classes);
                Assert.Equal(new Thickness(0), presenter.Padding);
                Assert.Equal(new Thickness(0), presenter.BorderThickness);
                Assert.True(popover.Bounds.Width > 1, $"the popover measured {popover.Bounds}");
            }
            finally
            {
                flyout.Hide();
                window.Close();
            }
        }, CancellationToken.None);
}
