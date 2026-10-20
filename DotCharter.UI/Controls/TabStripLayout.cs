namespace DotCharter.UI.Controls;

/// <summary>How every tab in an <see cref="OverflowTabStrip" /> is drawn: ALL with their captions,
/// or ALL as icons with only the active tab keeping its caption. Never a mix.</summary>
public enum TabStripMode
{
    Labelled,
    IconsOnly
}

/// <summary>One tab's two widths: as an icon (plus anything that never hides, such as a count
/// badge) and with its caption.</summary>
public readonly record struct TabStripTab(double IconWidth, double LabelledWidth)
{
    /// <summary>What the caption adds — the extra width the tab takes when it is the active one.</summary>
    public double CaptionWidth => Math.Max(0, LabelledWidth - IconWidth);
}

/// <summary>The decision for one width: the mode, which tabs stand in the strip and which go into the
/// "More" overflow, both as indices into the tab list and both in tab order.</summary>
public sealed record TabStripPlan(TabStripMode Mode, IReadOnlyList<int> Shown, IReadOnlyList<int> Overflow)
{
    public static readonly TabStripPlan Empty = new(TabStripMode.Labelled, [], []);

    public bool HasOverflow => Overflow.Count > 0;
}

/// <summary>
/// The Inspector tab strip's one rule, pure so the gate pins it:
///     <list type="number">
///         <item>Every tab fits WITH its caption: all are labelled, nothing overflows.</item>
///         <item>Otherwise every tab is an icon and only the active tab is labelled. The active tab's
///         slot is sized for the WIDEST caption in the strip, so choosing another tab never changes
///         how many tabs fit — the strip does not reshuffle on a click.</item>
///         <item>If even the icons do not all fit, a "More" button takes the end of the strip and the
///         tabs that do not fit go into it: the active tab always stays in the strip, the others
///         fill the remaining slots in tab order, the rest overflow in tab order.</item>
///     </list>
///     The plan says nothing about slack: the strip shares whatever width is left between the shown
///     tabs, so it never leaves a gap at its end.
/// </summary>
public static class TabStripLayout
{
    private const double Epsilon = 0.5;

    /// <param name="available">The width the strip may use.</param>
    /// <param name="spacing">The gap between two neighbouring items (tabs and the More button).</param>
    /// <param name="tabs">The tabs that exist, in strip order (a hidden tab is not in the list).</param>
    /// <param name="active">The index of the active tab in <paramref name="tabs" />, or -1 for none.</param>
    /// <param name="moreWidth">The width of the More button.</param>
    public static TabStripPlan Decide(double available, double spacing, IReadOnlyList<TabStripTab> tabs, int active, double moreWidth)
    {
        var count = tabs.Count;
        if (count == 0) return TabStripPlan.Empty;

        var all = Enumerable.Range(0, count).ToArray();
        var gaps = spacing * (count - 1);
        if (double.IsInfinity(available) || double.IsNaN(available)
            || tabs.Sum(t => t.LabelledWidth) + gaps <= available + Epsilon)
        {
            return new TabStripPlan(TabStripMode.Labelled, all, []);
        }

        var activeSlot = tabs.Max(t => t.CaptionWidth);
        if (tabs.Sum(t => t.IconWidth) + activeSlot + gaps <= available + Epsilon)
            return new TabStripPlan(TabStripMode.IconsOnly, all, []);

        var slots = SlotsBeside(available - moreWidth - spacing, spacing, tabs, activeSlot);
        var shown = new List<int>(slots);
        if (active >= 0 && active < count) shown.Add(active);
        foreach (var index in all)
        {
            if (shown.Count >= slots) break;
            if (!shown.Contains(index)) shown.Add(index);
        }

        shown.Sort();
        return new TabStripPlan(TabStripMode.IconsOnly, shown, [.. all.Where(i => !shown.Contains(i))]);
    }

    /// <summary>How many tabs fit beside the More button, sized for the worst case — the WIDEST
    /// icons and the widest caption — so the count holds whichever tab is active. At least one:
    /// the active tab always stands in the strip.</summary>
    private static int SlotsBeside(double budget, double spacing, IReadOnlyList<TabStripTab> tabs, double activeSlot)
    {
        var widest = tabs.Select(t => t.IconWidth).OrderDescending().ToArray();
        var used = activeSlot;
        var slots = 0;
        for (var i = 0; i < widest.Length - 1; i++)
        {
            used += widest[i] + (i > 0 ? spacing : 0);
            if (used > budget + Epsilon) break;
            slots++;
        }

        return Math.Max(1, slots);
    }
}
