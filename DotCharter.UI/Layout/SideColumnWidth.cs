namespace DotCharter.UI.Layout;

/// <summary>
///     A side column's width as the user chose it, kept apart from what the window lets it show.
///     A temporary shrink (a half-screen snap, a UI-scale change, a smaller monitor) narrows the
///     column only: the preferred width comes back when the window grows, and it is what is saved.
///     Pure, so the rules are tested without a window.
/// </summary>
public sealed class SideColumnWidth
{
    private double? _shownPx;

    /// <summary>The width the user restored or dragged; null when none is known.</summary>
    public double? PreferredPx { get; private set; }

    /// <summary>
    ///     True while a splitter drag is active. Every delta
    ///     resizes the grids and re-runs the clamp; while this is set neither the clamp nor
    ///     <see cref="Observe" /> touches the column. <see cref="EndDrag" /> commits a choice;
    ///     <see cref="CancelDrag" /> discards an abandoned drag.
    /// </summary>
    public bool IsDragging { get; private set; }

    private double? _dragStartPx;

    /// <summary>A splitter drag starts on a column <paramref name="columnPx" /> wide (null: not a pixel width).</summary>
    public void BeginDrag(double? columnPx)
    {
        IsDragging = true;
        _dragStartPx = columnPx;
    }

    /// <summary>
    ///     A drag ends on <paramref name="columnPx" />: a width that differs from the start is the
    ///     user's choice; a click without a move, an Escape or a drag back to the start is not.
    /// </summary>
    public void EndDrag(double? columnPx)
    {
        if (!IsDragging) return;
        var start = _dragStartPx;
        IsDragging = false;
        _dragStartPx = null;
        if (start is { } from && columnPx is { } px && double.IsFinite(px) && px > 0 && Math.Abs(px - from) >= 0.01)
            Prefer(px);
    }

    /// <summary>Discards an unfinished drag; its current width is observed without becoming a preference.</summary>
    public void CancelDrag(double? columnPx)
    {
        if (!IsDragging) return;
        IsDragging = false;
        _dragStartPx = null;
        _shownPx = columnPx;
    }

    /// <summary>A width the user chose: a restore or the end of a splitter drag.</summary>
    public void Prefer(double px)
    {
        PreferredPx = px;
        _shownPx = px;
    }

    /// <summary>
    ///     The column's current width: when it is not the width last applied, something else
    ///     (the splitter) wrote it, and it becomes the preferred width.
    /// </summary>
    public void Observe(double columnPx)
    {
        if (IsDragging || !double.IsFinite(columnPx) || columnPx <= 0) return;
        if (_shownPx is { } shown && Math.Abs(shown - columnPx) < 0.01) return;
        Prefer(columnPx);
    }

    /// <summary>
    ///     What the column shows: the preferred width as narrowed by <paramref name="clamp" />,
    ///     or null when none is preferred. The preferred width itself is never narrowed.
    /// </summary>
    public double? Shown(Func<double, double> clamp)
    {
        if (PreferredPx is not { } preferred) return null;
        var px = Math.Min(preferred, clamp(preferred));
        _shownPx = px;
        return px;
    }
}
