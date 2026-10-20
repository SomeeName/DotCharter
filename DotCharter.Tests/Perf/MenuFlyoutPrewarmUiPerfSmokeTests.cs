using Avalonia;
using Avalonia.Controls;
using DotCharter.UI.Views.Shell;

namespace DotCharter.Tests.Perf;

/// <summary>
///     What <see cref="MenuFlyoutPrewarmer" /> actually DOES to a menu, against
///     synthetic items so each rule can be isolated. The real markup's 126 rows
///     are covered by <see cref="ShellConstructionUiPerfSmokeTests" />.
/// </summary>
/// <remarks>
///     <para>
///         Here rather than in the default gate because giving a <c>MenuItem</c>
///         children calls <c>Dispatcher.VerifyAccess()</c> deep inside
///         <c>ItemCollection.Add</c> — see the remarks on
///         <see cref="UI.MenuFlyoutPrewarmerTests" />. Everything in this file
///         therefore runs INSIDE <c>HeadlessSessionFixture.Dispatch</c>, on the
///         thread that owns the dispatcher.
///     </para>
///     <para>
///         No popup is ever opened, here or in the helper: opening one to warm it
///         is the flicker this exists to remove, and it deadlocks
///         <c>Avalonia.Headless</c>.
///     </para>
/// </remarks>
[Collection("HeadlessUI")]
public sealed class MenuFlyoutPrewarmUiPerfSmokeTests
{
    private static readonly Size Slot = new(600, 900);

    /// <summary>Counts its own measure passes — the only direct evidence that a
    /// row was realized rather than skipped.</summary>
    private sealed class CountingMenuItem : MenuItem
    {
        public int Measures { get; private set; }

        protected override Size MeasureOverride(Size availableSize)
        {
            Measures++;
            return base.MeasureOverride(availableSize);
        }
    }

    private sealed class ExplodingMenuItem : MenuItem
    {
        protected override Size MeasureOverride(Size availableSize) =>
            throw new InvalidOperationException("measure is not possible here");
    }

    /// <summary>A header with two rows, the second of which is a SUBMENU with a
    /// row of its own — the shape File / Edit / Tempo Map / View / Tools all take
    /// since 0.52.</summary>
    private static (MenuItem Top, CountingMenuItem Row, CountingMenuItem Nested) Grouped()
    {
        var nested = new CountingMenuItem();
        var group = new MenuItem();
        group.Items.Add(nested);

        var row = new CountingMenuItem();
        var top = new MenuItem();
        top.Items.Add(row);
        top.Items.Add(group);
        return (top, row, nested);
    }

    /// <summary>
    ///     The premise the whole helper rests on: the rows of a menu that has
    ///     never been opened have never been laid out, so the first open pays for
    ///     all of them at once. If this stops holding the prewarm is pointless
    ///     rather than wrong — and we want to know.
    /// </summary>
    [Fact]
    public Task A_closed_flyouts_rows_are_not_measured_by_Avalonia() =>
        OnDispatcher(() =>
        {
            var (top, row, nested) = Grouped();

            top.Measure(Slot);

            Assert.Equal(0, row.Measures);
            Assert.Equal(0, nested.Measures);
        });

    [Fact]
    public Task Realize_measures_every_row_including_the_ones_in_submenus() =>
        OnDispatcher(() =>
        {
            var (top, row, nested) = Grouped();

            var rows = MenuFlyoutPrewarmer.Realize(top, Slot);

            Assert.True(row.Measures > 0, "the prewarm did not reach the flyout's rows");
            Assert.True(nested.Measures > 0,
                "the prewarm stopped at the first level — a grouped verb would still stall on hover");
            Assert.Equal(3, rows);
        });

    [Fact]
    public Task Realize_leaves_every_submenu_closed() =>
        OnDispatcher(() =>
        {
            var (top, _, _) = Grouped();

            MenuFlyoutPrewarmer.Realize(top, Slot);

            Assert.False(top.IsSubMenuOpen);
            Assert.All(top.Items.OfType<MenuItem>(), i => Assert.False(i.IsSubMenuOpen));
        });

    [Fact]
    public Task Realize_refuses_an_unusable_constraint() =>
        OnDispatcher(() =>
        {
            var (top, row, _) = Grouped();

            Assert.Equal(0, MenuFlyoutPrewarmer.Realize(top, new Size(0, 900)));
            Assert.Equal(0, MenuFlyoutPrewarmer.Realize(top, new Size(600, double.PositiveInfinity)));
            Assert.Equal(0, row.Measures);
        });

    /// <summary>A menu that cannot measure in isolation must cost the prewarm its
    /// own turn — never the window.</summary>
    [Fact]
    public Task Realize_swallows_a_measure_failure() =>
        OnDispatcher(() =>
        {
            var top = new MenuItem();
            top.Items.Add(new ExplodingMenuItem());

            Assert.Equal(0, MenuFlyoutPrewarmer.Realize(top, Slot));
            Assert.False(top.IsSubMenuOpen);
        });

    [Fact]
    public Task Realize_ignores_separators_and_empty_menus() =>
        OnDispatcher(() =>
        {
            var top = new MenuItem();
            top.Items.Add(new Separator());

            Assert.Equal(0, MenuFlyoutPrewarmer.Realize(top, Slot));
            Assert.Equal(0, MenuFlyoutPrewarmer.Realize(new MenuItem(), Slot));
        });

    private static Task OnDispatcher(Action body) =>
        HeadlessSessionFixture.Dispatch(body, CancellationToken.None);
}
