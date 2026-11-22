using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using DotCharter.Localization;
using DotCharter.UI.Controls;
using DotCharter.UI.ViewModels.Editor;
using DotCharter.UI.Views.Editor;

namespace DotCharter.Tests.Perf;

/// <summary>
///     #573: the Inspector's tab strip is ALL labelled or ALL icons — the active tab always keeps
///     its caption — and what does not fit even as icons goes into "More ▾", whose rows carry the tab's
///     badges. Since inspector design D (#751) the labelled strip is captions only (the glyphs show only in
///     the icons fallback), packed at the start while nothing overflows. The owner's captures
///     showed the opposite: some tabs labelled, some not, the set shifting as the pane resized, and
///     wide gaps at the end of the row.
///     <para>
///         The real <see cref="InspectorView" /> over the UI sweep's seeded song (a three-digit
///         Issues badge), at the five widths the issue names, with every tab selected in turn. The
///         pure rule is <c>UI/InspectorTabStripLayoutTests</c>. Width-shaped, so this class is in
///         the release's de-DE / ja-JP layout filter (German captions are the longest).
///     </para>
/// </summary>
[Collection("HeadlessUI")]
public sealed class InspectorTabStripUiPerfSmokeTests
{
    [Theory]
    [InlineData(320)]
    [InlineData(420)]
    [InlineData(560)]
    [InlineData(720)]
    [InlineData(1100)]
    public async Task TheStrip_IsAllOrNothing_ClipsNoTab_AndLeavesNoGap(int width) =>
        await HeadlessSessionFixture.Dispatch(() => InspectorNarrowWidthUiPerfSmokeTests.WithPane(width, view =>
        {
            var vm = (InspectorViewModel)view.DataContext!;
            var window = (Window)TopLevel.GetTopLevel(view)!;
            var (strip, row) = Strip(view);
            var tabs = Tabs(row);
            Assert.True(tabs.Count >= 7, $"only {tabs.Count} inspector tabs exist - repoint this test");

            foreach (var tab in tabs)
            {
                vm.SelectedTabIndex = TagOf(tab);
                NarrowWidthLayout.Settle(window);
                AssertStrip(width, strip, row, tabs, tab);
                if (TagOf(tab) == 3) NarrowWidthLayout.Capture(window, $"tabstrip-{width}");
            }
        }), CancellationToken.None);

    /// <summary>
    ///     At the dock's 320 px floor some tabs overflow in English and German; in ja-JP the
    ///     icons all fit there, so the case narrows the pane until something does (the strip's
    ///     rule is the same at any width). The More menu lists
    ///     exactly those, in strip order, under the tab's own name; picking one selects it and
    ///     brings it into the strip — the keyboard reaches it the same way (Tab to More, Enter,
    ///     arrows, Enter).
    /// </summary>
    [Fact]
    public async Task TheMoreMenu_ListsTheOverflowedTabs_AndPickingOneBringsItIntoTheStrip()
    {
        var width = await HeadlessSessionFixture.Dispatch(FirstWidthThatOverflows, CancellationToken.None);
        Assert.True(width > 0, $"nothing overflows even at {MoreMenuWidths[^1]} px - this case tests nothing; repoint it");
        await HeadlessSessionFixture.Dispatch(() => InspectorNarrowWidthUiPerfSmokeTests.WithPane(width, view =>
        {
            var vm = (InspectorViewModel)view.DataContext!;
            var window = (Window)TopLevel.GetTopLevel(view)!;
            var (strip, row) = Strip(view);
            var tabs = Tabs(row);
            vm.SelectedTabIndex = 0;
            NarrowWidthLayout.Settle(window);

            var overflow = row.OverflowTabs.OfType<ToggleButton>().ToList();
            Assert.True(overflow.Count > 0, $"nothing overflows at {width} px ({row.Mode}) - this case tests nothing; repoint it");
            var more = Assert.IsType<ToggleButton>(row.OverflowButton);
            Assert.True(KeyboardNavigation.GetIsTabStop(more) && more.Focusable && more.IsEffectivelyEnabled,
                "the More button cannot take keyboard focus");
            Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetName(more)), "the More button has no accessible name");

            var menu = Assert.IsType<MenuFlyout>(more.Flyout);
            menu.ShowAt(more);
            NarrowWidthLayout.Settle(window);
            try
            {
                var items = menu.Items.OfType<MenuItem>().Where(i => i.IsVisible).ToList();
                Assert.NotEmpty(items);
                var popup = TopLevel.GetTopLevel(items[0]);
                Assert.Equal(overflow.Select(AutomationProperties.GetName), items.Select(i => (i.Header as Panel)?.Children.OfType<TextBlock>().FirstOrDefault()?.Text));
                Assert.Equal(overflow.Select(t => t.Tag), items.Select(i => i.Tag));
                Assert.True(more.IsChecked == true, "the More button does not show that its menu is open");
                Assert.All(items, item => Assert.True(item.Bounds.Width > 1 && item.IsEffectivelyEnabled,
                    $"the More row '{item.Header}' did not lay out ({item.Bounds}; menu open {menu.IsOpen}, top level {popup?.GetType().Name ?? "none"})"));
                CaptureWithPopup(window, items[0], $"tabstrip-{width}-more-open");

                var picked = items[^1];
                picked.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                menu.Hide();
                NarrowWidthLayout.Settle(window);

                var target = overflow[^1];
                Assert.Equal(TagOf(target), vm.SelectedTabIndex);
                Assert.Contains(target, row.ShownTabs);
                Assert.False(more.IsChecked == true, "the More button stayed lit after its menu closed");
                AssertStrip(width, strip, row, tabs, target);
            }
            finally
            {
                menu.Hide();
            }
        }), CancellationToken.None);
    }

    /// <summary>
    ///     #751 review: design D's captions-only strip put the tabs that did not fit into More (two clicks, and the
    ///     rows dropped the Issues count). At the 530 x 370 primary size and the 480 x 370 tiny one, in every
    ///     language, every tab stands in the strip (one click) whichever is active, all labelled or all icons, and
    ///     the Issues count shows; in English at 530 every caption fits.
    /// </summary>
    [Theory]
    [InlineData(530)]
    [InlineData(480)]
    public async Task EveryTab_IsOneClick_AtThePrimaryAndTinySizes_InEveryCulture(int width) =>
        await HeadlessSessionFixture.Dispatch(() => InEveryCulture(culture => InspectorNarrowWidthUiPerfSmokeTests.WithPane(width, view =>
        {
            var vm = (InspectorViewModel)view.DataContext!;
            var window = (Window)TopLevel.GetTopLevel(view)!;
            window.Height = 370;
            NarrowWidthLayout.Settle(window);
            var (strip, row) = Strip(view);
            var tabs = Tabs(row);
            foreach (var tab in tabs)
            {
                vm.SelectedTabIndex = TagOf(tab);
                NarrowWidthLayout.Settle(window);
                AssertStrip(width, strip, row, tabs, tab);
                Assert.True(row.OverflowTabs.Count == 0,
                    $"at {width} px ({culture}, tab {tab.Tag} active, {row.Mode}) tabs are two clicks away in More: " +
                    string.Join(", ", row.OverflowTabs.Select(AutomationProperties.GetName)));
                if (TagOf(tab) == 0) NarrowWidthLayout.Capture(window, $"tabstrip-{width}x370-{culture}");
            }

            if (width >= 530 && culture == "en-US") Assert.Equal(TabStripMode.Labelled, row.Mode);
        })), CancellationToken.None);

    /// <summary>
    ///     A tab in More says what it says in the strip: the Issues row carries the live Issues count (the rows used
    ///     to drop it). The case narrows the pane until Issues itself overflows.
    /// </summary>
    [Fact]
    public async Task AMoreRow_CarriesItsTabsBadge() =>
        await HeadlessSessionFixture.Dispatch(() =>
        {
            var tested = false;
            foreach (var width in MoreMenuWidths)
            {
                InspectorNarrowWidthUiPerfSmokeTests.WithPane(width, view =>
                {
                    var (strip, row) = Strip(view);
                    var issues = Tabs(row).Single(t => TagOf(t) == 4);
                    if (tested || !row.OverflowTabs.Contains(issues)) return;
                    tested = true;
                    var count = strip.GetVisualDescendants().OfType<Border>().Single(b => b.Classes.Contains("TabCountBadge"))
                        .GetVisualDescendants().OfType<TextBlock>().Single().Text;
                    var more = Assert.IsType<ToggleButton>(row.OverflowButton);
                    var menu = Assert.IsType<MenuFlyout>(more.Flyout);
                    menu.ShowAt(more);
                    NarrowWidthLayout.Settle((Window)TopLevel.GetTopLevel(view)!);
                    try
                    {
                        AssertRowCarriesBadge(menu.Items.OfType<MenuItem>().Single(i => Equals(i.Tag, "4")), count, $"at {width} px, the Issues row");
                    }
                    finally
                    {
                        menu.Hide();
                    }
                });
            }

            Assert.True(tested, $"Issues never overflows even at {MoreMenuWidths[^1]} px - this case tests nothing; repoint it");
        }, CancellationToken.None);

    /// <summary>A More row shows a badge whose text is <paramref name="text" />, visible and laid out.</summary>
    internal static void AssertRowCarriesBadge(MenuItem item, string? text, string what)
    {
        Assert.False(string.IsNullOrEmpty(text), $"{what}: the tab's own badge is empty - this case tests nothing");
        var header = Assert.IsAssignableFrom<Panel>(item.Header);
        var texts = header.Children.Skip(1).SelectMany(c => c.GetVisualDescendants().Prepend(c)).OfType<TextBlock>()
            .Where(t => t.IsEffectivelyVisible).ToList();
        Assert.True(texts.Any(t => t.Text == text && t.Bounds.Width > 1),
            $"{what} carries no visible '{text}' badge (it shows: {string.Join(", ", texts.Select(t => $"'{t.Text}' {t.Bounds.Width:0.#} px"))})");
    }

    private static void InEveryCulture(Action<string> body)
    {
        var restore = LocalizationService.Instance.ConfiguredCulture;
        try
        {
            foreach (var culture in new[] { "en-US", "de-DE", "ja-JP" })
            {
                LocalizationService.Instance.SetConfiguredCulture(culture);
                body(culture);
            }
        }
        finally
        {
            LocalizationService.Instance.SetConfiguredCulture(restore);
            System.Globalization.CultureInfo.CurrentUICulture = LocalizationService.Instance.Culture;
        }
    }

    private static readonly int[] MoreMenuWidths = [320, 280, 240, 200, 160];

    /// <summary>The widest pane width at which the strip puts a tab into More, or 0 when none does.</summary>
    private static int FirstWidthThatOverflows()
    {
        foreach (var width in MoreMenuWidths)
        {
            var overflows = false;
            InspectorNarrowWidthUiPerfSmokeTests.WithPane(width, view => overflows = Strip(view).Row.OverflowTabs.OfType<ToggleButton>().Any());
            if (overflows) return width;
        }

        return 0;
    }

    private static void AssertStrip(int width, Border strip, OverflowTabStrip row, List<ToggleButton> tabs, ToggleButton active)
    {
        var shown = row.ShownTabs.OfType<ToggleButton>().ToList();
        var overflow = row.OverflowTabs.OfType<ToggleButton>().ToList();
        var more = row.OverflowButton;
        var at = $"at {width} px with tab {active.Tag} selected ({row.Mode}, {shown.Count} shown, {overflow.Count} in More)";
        NarrowWidthLayout.Report($"[tab-strip] {at}: row {row.Bounds.Width:0.#} px; " +
                                 string.Join(" ", shown.Select(t => $"{t.Tag}:{t.Bounds.Width:0.#}")) +
                                 (overflow.Count > 0 ? $" | more {more?.Bounds.Width:0.#} | " + string.Join(",", overflow.Select(t => t.Tag)) : string.Empty));

        // Every tab is in exactly one place, and the active one is in the strip and checked.
        Assert.Equal(tabs.Count, shown.Count + overflow.Count);
        Assert.Contains(active, shown);
        Assert.Equal(active, Assert.Single(tabs, t => t.IsChecked == true));

        // All or nothing: the active tab is labelled, and every other shown tab is labelled exactly
        // when the strip is.
        Assert.True(CaptionShown(active), $"{at} the active tab shows no caption");
        var labelled = shown.Where(t => t != active).Select(CaptionShown).Distinct().ToList();
        Assert.True(labelled.Count <= 1, $"{at} the strip MIXES labelled and icon-only tabs: " +
                                         string.Join(" ", shown.Select(t => $"{t.Tag}={(CaptionShown(t) ? "label" : "icon")}")));
        if (labelled.Count == 1)
        {
            Assert.True((row.Mode == TabStripMode.Labelled) == labelled[0],
                $"{at} the strip decided {row.Mode} but its other tabs are {(labelled[0] ? "labelled" : "icons")}");
        }

        // One row, in tab order, every shown tab whole and inside the strip, no caption cut.
        var items = shown.Cast<Control>().Concat(overflow.Count > 0 && more is not null ? [more] : []).ToList();
        var tops = items.Select(t => Math.Round(NarrowWidthLayout.TopIn(t, row))).Distinct().Count();
        Assert.True(tops == 1, $"{at} the strip takes {tops} rows");
        Assert.Equal(shown.Select(t => tabs.IndexOf(t)).Order(), shown.Select(t => tabs.IndexOf(t)));
        foreach (var item in items)
        {
            // Against the PANEL, not the strip's frame: the panel clips, so an item past its edge is
            // cut even where the frame's padding would still show it.
            var (left, right) = NarrowWidthLayout.Span(item, row);
            Assert.True(left >= -0.5 && right <= row.Bounds.Width + 0.5,
                $"{at} '{AutomationProperties.GetName(item)}' spans {left:0.##}..{right:0.##} px in a {row.Bounds.Width:0.##} px strip");
            Assert.True(item.Bounds.Width >= item.DesiredSize.Width - 0.5,
                $"{at} '{AutomationProperties.GetName(item)}' is squeezed to {item.Bounds.Width:0.#} of {item.DesiredSize.Width:0.#} px");
            if (Caption(item) is { IsEffectivelyVisible: true } caption)
            {
                var (captionLeft, captionRight) = NarrowWidthLayout.Span(caption, item);
                Assert.True(captionLeft >= -0.5 && captionRight <= item.Bounds.Width + 0.5,
                    $"{at} the caption '{caption.Text}' spans {captionLeft:0.#}..{captionRight:0.#} px in a {item.Bounds.Width:0.#} px tab");
            }
        }

        // No gap wider than one tab: neither between two items nor after the last.
        var narrowest = shown.Min(t => t.Bounds.Width);
        var edges = items.Select(i => NarrowWidthLayout.Span(i, row)).ToList();
        for (var i = 1; i < edges.Count; i++)
        {
            var gap = edges[i].Left - edges[i - 1].Right;
            Assert.True(gap <= narrowest + 0.5, $"{at} a {gap:0.#} px gap opens before item {i}");
        }

        var tail = row.Bounds.Width - edges[^1].Right;
        // Design D (#751) packs the tabs at the start while nothing overflows (the gap at the end is the design's);
        // a strip with tabs in More shares its slack, so it never ends in a gap beside the More button.
        if (row.ShareSlack || overflow.Count > 0)
            Assert.True(tail <= narrowest + 0.5, $"{at} the strip ends in a {tail:0.#} px gap, wider than its narrowest tab ({narrowest:0.#} px)");

        // More is in the strip exactly when something overflows, and an overflowed tab is neither
        // drawn nor reachable by Tab or the pointer — only through More.
        if (more is not null)
        {
            var (moreLeft, _) = NarrowWidthLayout.Span(more, row);
            Assert.Equal(overflow.Count > 0, moreLeft >= -0.5 && more.IsHitTestVisible && KeyboardNavigation.GetIsTabStop(more));
        }

        Assert.All(overflow, tab =>
        {
            var (left, right) = NarrowWidthLayout.Span(tab, row);
            Assert.True(right <= 0 || left >= row.Bounds.Width, $"{at} overflowed tab {tab.Tag} is drawn at {left:0.#}..{right:0.#}");
            Assert.False(tab.IsHitTestVisible || KeyboardNavigation.GetIsTabStop(tab), $"{at} overflowed tab {tab.Tag} still takes input");
        });
        Assert.All(shown, tab => Assert.True(tab.IsHitTestVisible && KeyboardNavigation.GetIsTabStop(tab) && tab.Focusable,
            $"{at} tab {tab.Tag} cannot take keyboard focus"));
        Assert.All(tabs, tab => Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetName(tab)), $"tab {tab.Tag} has no accessible name"));

        // The Issues count stays readable in every mode, unless Issues itself is in More.
        var badge = strip.GetVisualDescendants().OfType<Border>().Single(b => b.Classes.Contains("TabCountBadge"));
        if (shown.Any(t => TagOf(t) == 4))
            Assert.True(badge.IsEffectivelyVisible && badge.Bounds.Width > 1, $"{at} the Issues count badge is hidden");
    }

    private static (Border Strip, OverflowTabStrip Row) Strip(InspectorView view)
    {
        var strip = view.GetVisualDescendants().OfType<Border>().First(b => b.Classes.Contains("TabStrip"));
        return (strip, Assert.IsType<OverflowTabStrip>(strip.Child));
    }

    private static List<ToggleButton> Tabs(OverflowTabStrip row) =>
        [.. row.Children.OfType<ToggleButton>().Where(t => t.IsVisible && !OverflowTabStrip.GetIsOverflowButton(t))];

    private static int TagOf(Control tab) => int.Parse((string)tab.Tag!, System.Globalization.CultureInfo.InvariantCulture);

    private static TextBlock? Caption(Control item) =>
        ((item as ContentControl)?.Content as Panel)?.Children.OfType<TextBlock>().FirstOrDefault();

    private static bool CaptionShown(Control tab) => Caption(tab) is { IsEffectivelyVisible: true, Bounds.Width: > 1 };

    /// <summary>The window, and the menu's own top level beside it when the popup is a separate one
    /// (the headless platform does not overlay popups); the offset goes to the capture log.</summary>
    private static void CaptureWithPopup(Window window, Visual inPopup, string name)
    {
        NarrowWidthLayout.Capture(window, name);
        if (TopLevel.GetTopLevel(inPopup) is not { } popup || ReferenceEquals(popup, window) || !UiSweepRunner.CaptureEnabled) return;

        using var frame = popup.CaptureRenderedFrame();
        if (frame is null) return;
        var offset = popup.PointToScreen(new Point(0, 0)) - window.PointToScreen(new Point(0, 0));
        frame.Save(Path.Combine(NarrowWidthLayout.ArtifactDirectory, $"{name}-popup-{HeadlessSessionFixture.SweptCulture}.png"), new PngBitmapEncoderOptions());
        NarrowWidthLayout.Report($"[tab-strip] {name} popup at {offset.X},{offset.Y}");
    }
}
