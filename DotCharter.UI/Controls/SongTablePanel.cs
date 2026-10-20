using Avalonia;
using Avalonia.Controls;

namespace DotCharter.UI.Controls;

/// <summary>
///     One line of a song table, the caption row or a song row, laid out by a shared
/// <see cref="SongTableLayout" />. Each child sits in the column its attached
///     <see cref="ColumnProperty" /> names, at the column's full width and the panel's full height
///     (its own alignment places it inside). The caption row and the rows are measured at the same
///     width, so they agree on which columns are shown and where each starts.
///     <para>
///         <b>The panel owns its children's <c>IsVisible</c>.</b> A child in a column that gave way
///         is hidden; a child whose <see cref="StandInForProperty" /> names a column is shown only
///         while THAT column is hidden (a status mark in the cover column while the Status column
///         is gone). A child that needs its own visibility binding wraps it in an element the panel
///         may switch. The decision is taken in measure from the width alone, so it cannot
///         oscillate; an unbounded width shows every column.
///     </para>
/// </summary>
public sealed class SongTablePanel : Panel
{
    public static readonly StyledProperty<SongTableLayout?> ColumnsProperty =
        AvaloniaProperty.Register<SongTablePanel, SongTableLayout?>(nameof(Columns));

    public static readonly AttachedProperty<int> ColumnProperty =
        AvaloniaProperty.RegisterAttached<SongTablePanel, Control, int>("Column");

    public static readonly AttachedProperty<int> StandInForProperty =
        AvaloniaProperty.RegisterAttached<SongTablePanel, Control, int>("StandInFor", -1);

    private bool[]? _shown;
    private (double Left, double Width)[] _places = [];

    static SongTablePanel()
    {
        AffectsMeasure<SongTablePanel>(ColumnsProperty);
        AffectsParentMeasure<SongTablePanel>(ColumnProperty, StandInForProperty);
    }

    /// <summary>The table's columns; one instance shared by the caption row and the row template.</summary>
    public SongTableLayout? Columns
    {
        get => GetValue(ColumnsProperty);
        set => SetValue(ColumnsProperty, value);
    }

    /// <summary>Which columns the last measure showed (empty before the first).</summary>
    public IReadOnlyList<bool> Shown => _shown ?? [];

    /// <summary>Each column's left edge and width at the last arrange (empty before the first).</summary>
    public IReadOnlyList<(double Left, double Width)> Places => _places;

    public static int GetColumn(Control control) => control.GetValue(ColumnProperty);

    public static void SetColumn(Control control, int value) => control.SetValue(ColumnProperty, value);

    public static int GetStandInFor(Control control) => control.GetValue(StandInForProperty);

    public static void SetStandInFor(Control control, int value) => control.SetValue(StandInForProperty, value);

    protected override Size MeasureOverride(Size availableSize)
    {
        if (Columns is not { } layout) return base.MeasureOverride(availableSize);

        var shown = layout.ShownAt(availableSize.Width);
        _shown = shown;
        var width = double.IsFinite(availableSize.Width) ? availableSize.Width : layout.FullWidth;
        var places = layout.Place(width, shown);
        var height = 0.0;
        foreach (var child in Children)
        {
            var shows = Shows(child, layout, shown);
            if (child.IsVisible != shows) child.SetCurrentValue(IsVisibleProperty, shows);
            if (!shows) continue;

            child.Measure(new Size(places[GetColumn(child)].Width, availableSize.Height));
            height = Math.Max(height, child.DesiredSize.Height);
        }

        return new Size(width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (Columns is not { } layout) return base.ArrangeOverride(finalSize);

        _places = layout.Place(finalSize.Width, _shown ?? layout.ShownAt(finalSize.Width));
        foreach (var child in Children)
        {
            if (!child.IsVisible) continue;
            var (left, width) = _places[GetColumn(child)];
            child.Arrange(new Rect(left, 0, width, finalSize.Height));
        }

        return finalSize;
    }

    private static bool Shows(Control child, SongTableLayout layout, bool[] shown)
    {
        var column = GetColumn(child);
        if (column < 0 || column >= layout.Columns.Count)
            throw new InvalidOperationException($"a song table child names column {column} of {layout.Columns.Count}");

        var standsInFor = GetStandInFor(child);
        return shown[column] && (standsInFor < 0 || !shown[standsInFor]);
    }
}
