using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using DotCharter.UI.ViewModels.Panels;
using DotCharter.UI.Views.Panels;

namespace DotCharter.UI.Controls;

/// <summary>
///     Inspector design D's bend curve over a real <see cref="BendEditorViewModel" />: time along the sound's
///     sustain (the editor's 12 slots) left to right, semitones up to <see cref="Range" /> bottom to top, 28 px handles.
///     A press on a handle selects it and drags it (one editor transaction, so one stage per drag); a press elsewhere
///     adds a point there and drags it (Shift: unsnapped); a right press deletes a handle. Tab / Shift+Tab walk the
///     points (and leave the plot past either end); every other key is the old graph's (<see cref="HandleKey" />).
///     A point above the range is drawn on the top edge and keeps its value; the plot names the peak. A curve that stops
///     before the sustain's end holds flat to the right edge (display only). The editor's
///     writer decides what an edit does: on the Note tab it STAGES (the footer's Apply writes).
/// </summary>
public sealed class ColumnFlowBendPlot : Control
{
    public static readonly StyledProperty<BendEditorViewModel?> EditorProperty =
        AvaloniaProperty.Register<ColumnFlowBendPlot, BendEditorViewModel?>(nameof(Editor));

    public static readonly StyledProperty<double> RangeProperty =
        AvaloniaProperty.Register<ColumnFlowBendPlot, double>(nameof(Range), 2);

    /// <summary>A handle's hit radius (px): the design's 28 px target.</summary>
    internal const double HandleRadius = 14;

    internal const double AxisWidth = 31;

    private BendEditorViewModel? _hooked;
    private INotifyCollectionChanged? _points;
    private BendEditorViewModel.ControlPoint? _dragged;

    static ColumnFlowBendPlot() => AffectsRender<ColumnFlowBendPlot>(RangeProperty);

    public ColumnFlowBendPlot()
    {
        Focusable = true;
        MinHeight = 72;
    }

    public BendEditorViewModel? Editor
    {
        get => GetValue(EditorProperty);
        set => SetValue(EditorProperty, value);
    }

    /// <summary>The semitones the plot shows (2, 4 or 6).</summary>
    public double Range
    {
        get => GetValue(RangeProperty);
        set => SetValue(RangeProperty, value);
    }

    private Rect PlotArea => new(AxisWidth + HandleRadius, HandleRadius,
        Math.Max(1, Bounds.Width - AxisWidth - 2 * HandleRadius), Math.Max(1, Bounds.Height - 2 * HandleRadius));

    private double ShownRange => Math.Max(1, Range);

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property != EditorProperty) return;
        Unhook();
        if (VisualRoot != null) Hook();
        InvalidateVisual();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Hook();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        Unhook();
    }

    private void Hook()
    {
        if (_hooked != null || Editor is not { } editor) return;
        _hooked = editor;
        editor.GraphChanged += InvalidateVisual;
        editor.PropertyChanged += OnEditorChanged;
        HookPoints(editor);
    }

    private void HookPoints(BendEditorViewModel editor)
    {
        _points?.CollectionChanged -= OnPointsChanged;
        _points = editor.Points;
        _points.CollectionChanged += OnPointsChanged;
    }

    private void Unhook()
    {
        if (_hooked is not { } editor) return;
        editor.GraphChanged -= InvalidateVisual;
        editor.PropertyChanged -= OnEditorChanged;
        _points?.CollectionChanged -= OnPointsChanged;
        _points = null;
        _hooked = null;
        _dragged = null;
    }

    private void OnEditorChanged(object? sender, PropertyChangedEventArgs e)
    {
        // The editor replaced its list wholesale (a reload): listen to the new one.
        if (e.PropertyName == nameof(BendEditorViewModel.Points) && _hooked is { } editor) HookPoints(editor);
        InvalidateVisual();
    }

    private void OnPointsChanged(object? sender, NotifyCollectionChangedEventArgs e) => InvalidateVisual();

    public override void Render(DrawingContext context)
    {
        context.DrawRectangle(Brushes.Transparent, null, new Rect(Bounds.Size));
        if (!this.TryFindResource("FireAccentBright", out var accent) || accent is not IBrush ink) return;
        DrawGrid(context);
        if (Editor is not { } editor) return;
        var pen = new Pen(ink, 2);
        var points = editor.Points;
        var peak = points.Count == 0 ? 0 : points.Max(p => p.Step);
        if (peak > ShownRange)
        {
            var above = Text(string.Format(CultureInfo.CurrentCulture, "▲ +{0:0.##} st", peak), ink);
            context.DrawText(above, new Point(PlotArea.Right - above.Width, 0));
        }

        for (var i = 1; i < points.Count; i++) context.DrawLine(pen, ToPixels(points[i - 1]), ToPixels(points[i]));
        if (HoldLine(editor) is var (holdFrom, holdTo)) context.DrawLine(pen, holdFrom, holdTo);
        foreach (var point in points) context.DrawEllipse(ink, null, ToPixels(point), 3.5, 3.5);
        if (editor.SelectedPoint is { } selected && points.Contains(selected))
            context.DrawEllipse(null, pen, ToPixels(selected), 7, 7);
    }

    private void DrawGrid(DrawingContext context)
    {
        if (!this.TryFindResource("FireBorder", out var grid) || grid is not IBrush gridInk ||
            !this.TryFindResource("FireTextMuted", out var label) || label is not IBrush labelInk)
        {
            return;
        }

        var area = PlotArea;
        var step = ShownRange > 2 ? 2 : 1;
        var gridPen = new Pen(gridInk);
        for (var pitch = 0; pitch <= ShownRange; pitch += step)
        {
            var y = PitchY(pitch);
            context.DrawLine(gridPen, new Point(area.Left, y), new Point(area.Right, y));
            var caption = Text(pitch == 0 ? "0 st" : string.Format(CultureInfo.CurrentCulture, "+{0} st", pitch), labelInk);
            context.DrawText(caption, new Point(2, y - caption.Height / 2));
        }
    }

    private static FormattedText Text(string text, IBrush ink) =>
        new(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Typeface.Default, 11, ink);

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (Editor is not { } editor) return;
        Focus();
        var at = e.GetPosition(this);
        var (slot, step) = FromPixels(at);
        var kind = e.GetCurrentPoint(this).Properties.PointerUpdateKind;
        _dragged = Press(editor, HitHandle(at), slot, step, kind, e.KeyModifiers.HasFlag(KeyModifiers.Shift));
        if (_dragged != null) e.Pointer.Capture(this);
        InvalidateVisual();
        e.Handled = true;
    }

    /// <summary>
    ///     What a press does, as the old bend graph did: a right press deletes the handle under it (or, on empty
    ///     plot, clears the selection), a middle press deletes the handle under it, and a left press opens one editor
    ///     transaction and grabs the handle under it, or adds a point there and grabs that (Shift: unsnapped step).
    ///     Returns the point a left press drags; the caller ends the transaction on release.
    /// </summary>
    internal static BendEditorViewModel.ControlPoint? Press(BendEditorViewModel editor, BendEditorViewModel.ControlPoint? hit,
        int slot, double step, PointerUpdateKind kind, bool shift)
    {
        switch (kind)
        {
            case PointerUpdateKind.RightButtonPressed:
            {
                if (hit != null) editor.RemovePoint(hit);
                else editor.ClearSelection();
                return null;
            }
            case PointerUpdateKind.MiddleButtonPressed:
            {
                if (hit != null) editor.RemovePoint(hit);
                return null;
            }
            case PointerUpdateKind.LeftButtonPressed:
            {
                editor.BeginTransaction();
                if (hit == null) return editor.AddOrMovePoint(slot, step, shift);
                editor.SelectedPoint = hit;
                return hit;
            }
            default:
                return null;
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_dragged is not { } point || Editor is not { } editor) return;
        var (slot, step) = FromPixels(e.GetPosition(this));
        editor.MovePoint(point, slot, step, e.KeyModifiers.HasFlag(KeyModifiers.Shift));
        InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        EndDrag();
        e.Pointer.Capture(null);
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        EndDrag();
    }

    private void EndDrag()
    {
        if (_dragged == null) return;
        _dragged = null;
        Editor?.EndTransaction();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (Editor is not { } editor || !HandleKey(editor, e.Key, e.KeyModifiers)) return;
        e.Handled = true;
        InvalidateVisual();
    }

    /// <summary>
    ///     The plot's keyboard: Tab / Shift+Tab walk the points and leave the plot past either end (the one key the
    ///     plot owns); every other key is the bend graph's own (<see cref="BendEditorKeys" />: Enter / Insert add, Home /
    ///     End / Page Up / Page Down pick, the arrows move by the quarter-tone grid, Ctrl finer, Shift three slots,
    ///     Delete removes, Esc deselects), one editor transaction per key.
    /// </summary>
    internal static bool HandleKey(BendEditorViewModel editor, Key key, KeyModifiers modifiers) =>
        key == Key.Tab && modifiers is KeyModifiers.None or KeyModifiers.Shift
            ? editor.SelectAdjacent(modifiers == KeyModifiers.Shift ? -1 : 1)
            : BendEditorKeys.Handle(editor, key, modifiers);

    /// <summary>The point whose handle holds <paramref name="at" />, the nearest when two do.</summary>
    internal BendEditorViewModel.ControlPoint? HitHandle(Point at)
    {
        BendEditorViewModel.ControlPoint? hit = null;
        var distance = HandleRadius;
        foreach (var point in Editor?.Points ?? [])
        {
            var candidate = ((Vector)(ToPixels(point) - at)).Length;
            if (candidate > distance) continue;
            distance = candidate;
            hit = point;
        }

        return hit;
    }

    /// <summary>The flat hold from the editor's last point to the sustain's end (the plot's right edge), as Guitar Pro
    /// draws it (<see cref="BendEditorViewModel.HoldFrom" />); null when there is nothing held.</summary>
    internal (Point From, Point To)? HoldLine(BendEditorViewModel editor)
    {
        if (editor.HoldFrom is not { } last) return null;
        var from = ToPixels(last);
        return (from, new Point(PlotArea.Right, from.Y));
    }

    internal double PitchY(double semitones) => PlotArea.Y + (1 - Math.Clamp(semitones / ShownRange, 0, 1)) * PlotArea.Height;

    internal Point ToPixels(BendEditorViewModel.ControlPoint point) =>
        new(PlotArea.X + (double)point.TimeSlot / BendEditorViewModel.TimeSlotCount * PlotArea.Width, PitchY(point.Step));

    /// <summary>The slot and semitones under a pixel (the editor snaps the step to its own grid).</summary>
    internal (int Slot, double Step) FromPixels(Point at)
    {
        var area = PlotArea;
        var x = Math.Clamp((at.X - area.X) / area.Width, 0, 1);
        var y = 1 - Math.Clamp((at.Y - area.Y) / area.Height, 0, 1);
        return ((int)Math.Round(x * BendEditorViewModel.TimeSlotCount), y * ShownRange);
    }
}

/// <summary>A bend preset's curve drawn small inside its button, so the charter sees the shape before staging it.</summary>
public sealed class ColumnFlowBendPreview : Control
{
    public static readonly StyledProperty<BendEditorViewModel.BendPreset> PresetProperty =
        AvaloniaProperty.Register<ColumnFlowBendPreview, BendEditorViewModel.BendPreset>(nameof(Preset));

    static ColumnFlowBendPreview() => AffectsRender<ColumnFlowBendPreview>(PresetProperty);

    public ColumnFlowBendPreview()
    {
        Width = 28;
        Height = 16;
        IsHitTestVisible = false;
    }

    public BendEditorViewModel.BendPreset Preset
    {
        get => GetValue(PresetProperty);
        set => SetValue(PresetProperty, value);
    }

    /// <summary>The preset's curve as (time 0..1, semitones) points; "None" is the flat no-bend line.</summary>
    internal static Point[] Shape(BendEditorViewModel.BendPreset preset) => preset switch
    {
        BendEditorViewModel.BendPreset.Bend => [new(0, 0), new(0.25, 2), new(1, 2)],
        BendEditorViewModel.BendPreset.Release => [new(0, 2), new(0.75, 0), new(1, 0)],
        BendEditorViewModel.BendPreset.BendRelease => [new(0, 0), new(1d / 3, 2), new(2d / 3, 2), new(1, 0)],
        BendEditorViewModel.BendPreset.Hold => [new(0, 2), new(1, 2)],
        BendEditorViewModel.BendPreset.Prebend => [new(0, 2), new(1, 2)],
        BendEditorViewModel.BendPreset.PrebendBend => [new(0, 2), new(5d / 12, 3), new(1, 3)],
        BendEditorViewModel.BendPreset.PrebendRelease => [new(0, 2), new(5d / 6, 0), new(1, 0)],
        _ => [new(0, 0), new(1, 0)]
    };

    public override void Render(DrawingContext context)
    {
        if (!this.TryFindResource("FireAccentBright", out var resource) || resource is not IBrush ink) return;
        var points = Shape(Preset);
        var peak = Math.Max(2, points.Max(point => point.Y));
        var pen = new Pen(ink, 1.5);
        Point Map(Point point) => new(point.X * (Bounds.Width - 2) + 1, (1 - point.Y / peak) * (Bounds.Height - 2) + 1);
        for (var index = 1; index < points.Length; index++) context.DrawLine(pen, Map(points[index - 1]), Map(points[index]));
    }
}
