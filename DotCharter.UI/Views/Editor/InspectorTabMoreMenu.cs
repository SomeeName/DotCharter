using System.Globalization;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using DotCharter.UI.Controls;

namespace DotCharter.UI.Views.Editor;

/// <summary>
/// The Inspector tab strip's "More ▾" menu: one row per tab, in strip order, each with
///     the tab's own glyph and name (both BOUND to the tab, so a language switch reaches the menu).
///     Opening the menu shows only the rows of the tabs <see cref="OverflowTabStrip" /> put in the
///     overflow; picking one selects that tab, and because the active tab always stands in the
///     strip, it comes out of the menu and into the row. The More button stays lit while its menu
///     is open.
/// </summary>
/// <remarks>
///     The rows are built ONCE, before the first open, and only their visibility changes after.
///     Rows added in <c>Opening</c> never reached the presenter: the menu opened empty
///     (<c>Perf/InspectorTabStripUiPerfSmokeTests</c> caught it).
/// </remarks>
public static class InspectorTabMoreMenu
{
    public static void Attach(OverflowTabStrip strip, ToggleButton more, Action<int> select)
    {
        if (more.Flyout is not MenuFlyout menu) return;

        var rows = new List<(ToggleButton Tab, MenuItem Row)>();
        foreach (var tab in strip.Children.OfType<ToggleButton>().Where(t => !OverflowTabStrip.GetIsOverflowButton(t)))
        {
            var row = new MenuItem
            {
                [!HeaderedSelectingItemsControl.HeaderProperty] = tab[!AutomationProperties.NameProperty],
                [!AutomationProperties.NameProperty] = tab[!AutomationProperties.NameProperty],
                Tag = tab.Tag,
                Icon = Glyph(tab),
                IsVisible = false
            };
            if (int.TryParse(tab.Tag as string, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index))
                row.Click += (_, _) => select(index);
            rows.Add((tab, row));
            menu.Items.Add(row);
        }

        menu.Opening += (_, _) =>
        {
            var overflow = strip.OverflowTabs;
            foreach (var (tab, row) in rows) row.IsVisible = overflow.Contains(tab);
            more.IsChecked = true;
        };
        menu.Closed += (_, _) => more.IsChecked = false;
    }

    private static PathIcon? Glyph(ToggleButton tab) =>
        (tab.Content as Panel)?.Children.OfType<PathIcon>().FirstOrDefault() is { } glyph
            ? new PathIcon { [!PathIcon.DataProperty] = glyph[!PathIcon.DataProperty] }
            : null;
}
