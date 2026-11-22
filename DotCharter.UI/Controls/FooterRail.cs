using Avalonia;
using Avalonia.Controls;
using Avalonia.LogicalTree;

namespace DotCharter.UI.Controls;

/// <summary>
/// The pinned footer row of an Inspector tab in design D: the first child leads at the left edge, the second
///     trails at the right edge, in ONE row while both fit. When they do not, the room is given up in this order:
///     <list type="number">
///         <item>the children marked with a <see cref="DropOrderProperty" /> are dropped, lowest order first, as few as
///         make the row fit: the leading panel's own children and any descendant of the trailing child (the Note footer
///         drops its place in the queue, then the playhead time, then the ladder's short reach);</item>
///         <item>the rail takes two rows, the trailing child over the full width on top and the leading one under it,
///         dropping only what the leading child needs to fit the full width (the trailing child's droppables come back:
///         it has the row to itself). The trailing child is never squeezed below its natural width in one row: a cut
///         reach or rung is worse than a second row; stacked, it is measured at the full width (a wrap panel wraps).</item>
///     </list>
///     A dropped child is hidden (<c>IsVisible</c>, never a zero opacity); the rail remembers the width it last
///     measured at, so it shows it again as soon as it fits. Nothing is arranged wider than the rail, and the rail
///     clips, so nothing spills at the dock's 320 px floor. A hidden trailing child leaves the leading one the row.
/// </summary>
public sealed class FooterRail : Panel
{
    public static readonly StyledProperty<double> SpacingProperty =
        AvaloniaProperty.Register<FooterRail, double>(nameof(Spacing), 6);

    /// <summary>On a child of the leading panel or a descendant of the trailing child: 1 is dropped first, 2 next; 0
    /// (the default) never.</summary>
    public static readonly AttachedProperty<int> DropOrderProperty =
        AvaloniaProperty.RegisterAttached<FooterRail, Control, int>("DropOrder");

    // What each droppable child takes in its panel (its width plus the panel's spacing), as last measured shown.
    private readonly Dictionary<Control, double> _dropWidths = [];
    private double _leadWidth;
    private double _trailWidth;

    static FooterRail() => AffectsMeasure<FooterRail>(SpacingProperty);

    public FooterRail() => ClipToBounds = true;

    public double Spacing
    {
        get => GetValue(SpacingProperty);
        set => SetValue(SpacingProperty, value);
    }

    /// <summary>Whether the last layout needed two rows — the test hook.</summary>
    public bool IsStacked { get; private set; }

    /// <summary>How many droppable children the last layout hid — the test hook.</summary>
    public int Dropped { get; private set; }

    /// <summary>How many droppable children the rail holds — the test hook.</summary>
    public int MaxDroppable { get; private set; }

    /// <summary>How many droppable children the one-row attempt hid (all of them when the rail stacked) — the test hook:
    /// the rail never takes a second row while an optional part could still go.</summary>
    public int DropsBeforeStacking { get; private set; }

    public static int GetDropOrder(Control control) => control.GetValue(DropOrderProperty);

    public static void SetDropOrder(Control control, int value) => control.SetValue(DropOrderProperty, value);

    private Control? Leading => Children.Count > 0 ? Children[0] : null;

    private Control? Trailing => Children.Count > 1 && Children[1].IsVisible ? Children[1] : null;

    protected override Size MeasureOverride(Size availableSize)
    {
        var unbounded = new Size(double.PositiveInfinity, availableSize.Height);
        var room = availableSize.Width;
        var trailing = Trailing;
        Leading?.Measure(unbounded);
        trailing?.Measure(unbounded);
        var droppable = Droppables(trailing);
        MaxDroppable = droppable.Count;
        var full = Full(Leading, droppable) + (trailing is null ? 0 : Spacing + Full(trailing, droppable));
        IsStacked = false;

        var drop = DropsToFit(droppable, full, room);
        DropsBeforeStacking = drop;
        var hide = droppable.Take(drop).ToHashSet();
        if (trailing is not null && full - Saved(droppable, drop) > room)
        {
            // Everything droppable is gone and the row is still too wide: two rows, each half dropping only what it
            // needs to fit the full width (the trailing child's own droppables come back where they fit).
            IsStacked = true;
            var trailDrops = droppable.Where(trailing.IsLogicalAncestorOf).ToList();
            var leadDrops = droppable.Except(trailDrops).ToList();
            hide = [.. leadDrops.Take(DropsToFit(leadDrops, Full(Leading, leadDrops), room)),
                .. trailDrops.Take(DropsToFit(trailDrops, Full(trailing, trailDrops), room))];
        }

        ApplyDrops(droppable, hide, trailing, unbounded);
        var lead = Leading?.DesiredSize ?? default;
        _leadWidth = Math.Min(lead.Width, room);
        if (trailing is null) return new Size(_leadWidth, lead.Height);

        _trailWidth = IsStacked ? room : trailing.DesiredSize.Width;
        if (IsStacked) trailing.Measure(new Size(room, availableSize.Height));
        if (IsStacked) return new Size(room, lead.Height + Spacing + trailing.DesiredSize.Height);

        var width = double.IsInfinity(room) ? _leadWidth + Spacing + _trailWidth : room;
        return new Size(width, Math.Max(lead.Height, trailing.DesiredSize.Height));
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var lead = Leading?.DesiredSize ?? default;
        var leadWidth = Math.Min(_leadWidth, finalSize.Width);
        if (Trailing is not { } trailing)
        {
            Leading?.Arrange(new Rect(0, 0, leadWidth, finalSize.Height));
            return finalSize;
        }

        var trail = trailing.DesiredSize;
        if (IsStacked)
        {
            trailing.Arrange(new Rect(0, 0, finalSize.Width, trail.Height));
            Leading?.Arrange(new Rect(0, trail.Height + Spacing, leadWidth, lead.Height));
            return finalSize;
        }

        Leading?.Arrange(new Rect(0, 0, leadWidth, finalSize.Height));
        var width = Math.Min(_trailWidth, Math.Max(0, finalSize.Width - leadWidth - Spacing));
        trailing.Arrange(new Rect(finalSize.Width - width, 0, width, finalSize.Height));
        return finalSize;
    }

    /// <summary>The leading panel's droppable children and the trailing child's droppable descendants, first to go
    /// first; the width of each shown one is noted.</summary>
    private List<Control> Droppables(Control? trailing)
    {
        IEnumerable<Control> leading = Leading is Panel panel ? panel.Children : [];
        var trail = trailing?.GetLogicalDescendants().OfType<Control>() ?? [];
        var droppable = leading.Concat(trail).Where(c => GetDropOrder(c) > 0).OrderBy(GetDropOrder).ToList();
        foreach (var child in droppable.Where(c => c.IsVisible)) _dropWidths[child] = child.DesiredSize.Width + Gap(child);
        return droppable;
    }

    private static double Gap(Control child) => child.Parent switch
    {
        StackPanel stack => stack.Spacing,
        Grid grid => grid.ColumnSpacing,
        _ => 0
    };

    /// <summary>What <paramref name="half" /> would take with every one of its droppables shown.</summary>
    private double Full(Control? half, List<Control> droppable) => half is null
        ? 0
        : half.DesiredSize.Width + droppable.Where(d => !d.IsVisible && half.IsLogicalAncestorOf(d))
            .Sum(d => _dropWidths.GetValueOrDefault(d));

    /// <summary>The fewest drops that bring the row within <paramref name="budget" />, or all of them.</summary>
    private int DropsToFit(List<Control> droppable, double full, double budget)
    {
        var drop = 0;
        while (drop < droppable.Count && full - Saved(droppable, drop) > budget) drop++;
        return drop;
    }

    private double Saved(List<Control> droppable, int drop) => droppable.Take(drop).Sum(d => _dropWidths.GetValueOrDefault(d));

    private void ApplyDrops(List<Control> droppable, HashSet<Control> hide, Control? trailing, Size unbounded)
    {
        Dropped = hide.Count;
        var changed = false;
        foreach (var child in droppable)
        {
            var show = !hide.Contains(child);
            if (child.IsVisible == show) continue;
            child.IsVisible = show;
            changed = true;
        }

        if (!changed) return;
        Leading?.Measure(unbounded);
        trailing?.Measure(unbounded);
    }
}
