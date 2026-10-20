using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Media;
using DotCharter.UI.Controls.Themes;
using DotCharter.UI.ViewModels.Panels;
using Path = Avalonia.Controls.Shapes.Path;

namespace DotCharter.UI.Views.Panels;

// Guitar-Pro-style bend editor surface. All drawing and pointer routing
// happens here; the canvas is rebuilt from scratch on every change, which
// is fine since there are at most ~13 shapes on screen and the control is
// in the inspector (not in a hot render path).
//
// Interaction model:
//   * Left-click on empty space       → create + start dragging a point
//   * Left-click on an existing point → start dragging it (stays selected)
//   * Drag                            → follows cursor freely; one undo entry per gesture
//   * Right-click on point            → delete it
//   * Hover                           → crosshair guides + ghost preview of
//                                       where a click would drop a new point,
//                                       value callout near the cursor
//   * Keyboard (canvas focused)       → arrows nudge selected point,
//                                       Shift+arrows jump by 3 slots,
//                                       Ctrl nudges in eighth-step increments,
//                                       Delete/Backspace removes it,
//                                       Esc clears the selection
//   * Shift held while dragging       → disables quarter-step quantization so
//                                       the point slides smoothly along Y
//
// Coordinate system:
//   * X axis : BendEditorViewModel.TimeSlotCount equally-spaced time slots
//              (indices 0..TimeSlotCount).
//   * Y axis : 0..MaxStep semitones, quarter-step quantized on commit.
//   * Padding on all sides gives room for the labels that sit
//     OUTSIDE the plot area.
public partial class BendEditorControl : UserControl
{
    // Padding, hit radii, and Y-tick table all live in BendEditorGeometry —
    // see that class for the values. Aliases keep the call-sites in this file
    // succinct and avoid a hard rename touching every line.
    private const double PaddingLeft = BendEditorGeometry.PaddingLeft;
    private const double PaddingRight = BendEditorGeometry.PaddingRight;
    private const double PaddingTop = BendEditorGeometry.PaddingTop;
    private const double PaddingBottom = BendEditorGeometry.PaddingBottom;
    private const double PointDrawRadius = BendEditorGeometry.PointDrawRadius;
    private const double SelectedDrawR = BendEditorGeometry.SelectedDrawR;
    private const double HoverGhostR = BendEditorGeometry.HoverGhostR;

    private static (double Step, string Label)[] YTicks => BendEditorGeometry.YTicks;

    private Border? _border;

    private Canvas? _canvas;
    private bool _dragActive;
    private BendEditorViewModel.ControlPoint? _dragPoint;

    // Track the currently-hooked collection separately from _vm.Points so we
    // can always detach from the exact instance we originally attached to,
    // even after the view model replaces Points wholesale.
    private ObservableCollection<BendEditorViewModel.ControlPoint>? _hookedPoints;
    private BendEditorViewModel.ControlPoint? _hoverHit;
    private Point? _hoverPoint;
    private NotifyCollectionChangedEventHandler? _pointsCollectionHandler;
    private BendEditorViewModel? _vm;

    public BendEditorControl()
    {
        InitializeComponent();

        Focusable = true;

        AttachedToVisualTree += OnAttached;
        DetachedFromVisualTree += OnDetached;
    }

    /// <summary>The <see cref="Path" /> that draws the bend curve (the headless smoke test finds it by name).</summary>
    public const string CurvePathName = "BendCurvePath";

    private static Color ColorCurve => ChartTheme.SkyBlueColor;
    private static Color ColorPoint => ChartTheme.SkyBlueColor;
    private static Color ColorSelected => ChartTheme.GoldColor;

    private static Color ColorGhost =>
        Color.FromArgb(0xB0, ChartTheme.GrayColor.R, ChartTheme.GrayColor.G, ChartTheme.GrayColor.B);

    private static Color ColorGuide => Color.FromArgb(0x70, ChartTheme.SkyBlueColor.R, ChartTheme.SkyBlueColor.G,
        ChartTheme.SkyBlueColor.B);

    private static Color ColorHold => ChartTheme.WhiteSmokeColor;
    private static Color ColorGridMajor => ChartTheme.MutedNoteColor;
    private static Color ColorGridMinor => ChartTheme.BorderColor;
    private static Color ColorLabel => ChartTheme.MeasureColor;
    private static Color ColorCallout => ChartTheme.TopMarginBgColor;
    private static Color ColorCalloutText => ChartTheme.WhiteSmokeColor;

    private void OnAttached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        _canvas = this.FindControl<Canvas>("GraphCanvas");
        _border = this.FindControl<Border>("GraphBorder");

        if (_canvas != null)
        {
            _canvas.Focusable = true;
            _canvas.PointerPressed += OnCanvasPointerPressed;
            _canvas.PointerMoved += OnCanvasPointerMoved;
            _canvas.PointerReleased += OnCanvasPointerReleased;
            _canvas.PointerExited += OnCanvasPointerExited;
            _canvas.KeyDown += OnCanvasKeyDown;
        }

        _border?.SizeChanged += OnBorderSizeChanged;

        BindToDataContext();
        ChartTheme.ThemeChanged += OnThemeChanged;
        _needsGridRebuild = true;
        RebuildGraph();
    }

    private void OnThemeChanged()
    {
        _needsGridRebuild = true;
        RebuildGraph();
    }

    private void OnDetached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        DetachFromDataContext();
        ChartTheme.ThemeChanged -= OnThemeChanged;
        if (_canvas != null)
        {
            _canvas.PointerPressed -= OnCanvasPointerPressed;
            _canvas.PointerMoved -= OnCanvasPointerMoved;
            _canvas.PointerReleased -= OnCanvasPointerReleased;
            _canvas.PointerExited -= OnCanvasPointerExited;
            _canvas.KeyDown -= OnCanvasKeyDown;
        }

        _border?.SizeChanged -= OnBorderSizeChanged;
    }

    private void OnBorderSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        _needsGridRebuild = true;
        RebuildGraph();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        BindToDataContext();
        // Data change = new curve; the grid depends only on size + theme.
        // The inspector swaps in a fresh BendEditorViewModel on EVERY
        // selection change, and rebuilding the grid's dozens of Line/label
        // CONTROLS each time was a measured top contributor to editing GC
        // churn (~31 ms + hundreds of KB per committed edit, v0.32.5).
        _needsCurveRebuild = true;
        RebuildGraph();
    }

    private void BindToDataContext()
    {
        if (ReferenceEquals(_vm, DataContext)) return;

        DetachFromDataContext();
        _vm = DataContext as BendEditorViewModel;
        if (_vm != null)
        {
            _vm.PropertyChanged += OnVmPropertyChanged;
            _vm.GraphChanged += RebuildGraph;
            HookPointsCollection(_vm.Points);
        }
    }

    private void DetachFromDataContext()
    {
        if (_vm != null)
        {
            _vm.PropertyChanged -= OnVmPropertyChanged;
            _vm.GraphChanged -= RebuildGraph;
        }

        UnhookPointsCollection();
        _vm = null;
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(BendEditorViewModel.Points))
        {
            if (_vm != null) HookPointsCollection(_vm.Points);
            _needsCurveRebuild = true;
            RebuildGraph();
        }
        else if (e.PropertyName == nameof(BendEditorViewModel.SelectedPoint))
        {
            // Selection only recolors dots/labels — all in the dynamic layer.
            RebuildGraph();
        }
    }

    private void HookPointsCollection(ObservableCollection<BendEditorViewModel.ControlPoint> pts)
    {
        if (ReferenceEquals(_hookedPoints, pts)) return;

        UnhookPointsCollection();

        _pointsCollectionHandler = OnPointsCollectionChanged;
        pts.CollectionChanged += _pointsCollectionHandler;
        foreach (var p in pts) p.PropertyChanged += OnPointChanged;

        _hookedPoints = pts;
    }

    private void UnhookPointsCollection()
    {
        if (_hookedPoints == null) return;

        if (_pointsCollectionHandler != null)
        {
            _hookedPoints.CollectionChanged -= _pointsCollectionHandler;
            _pointsCollectionHandler = null;
        }

        foreach (var p in _hookedPoints) p.PropertyChanged -= OnPointChanged;

        _hookedPoints = null;
    }

    private void OnPointsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems != null)
        {
            foreach (BendEditorViewModel.ControlPoint p in e.OldItems)
                p.PropertyChanged -= OnPointChanged;
        }

        if (e.NewItems != null)
        {
            foreach (BendEditorViewModel.ControlPoint p in e.NewItems)
                p.PropertyChanged += OnPointChanged;
        }

        _needsCurveRebuild = true;
        RebuildGraph();
    }

    private void OnPointChanged(object? sender, PropertyChangedEventArgs e)
    {
        _needsCurveRebuild = true;
        RebuildGraph();
    }

    // --- Drawing ---

    // Three canvas layers, cheapest-invalidation-first (v0.32.5 GC work —
    // same z-order and pixels as the historical full rebuild):
    //   grid    [0.._gridChildCount)      — depends on SIZE + THEME only
    //   curve   [grid.._staticChildCount) — depends on the Points data
    //   dynamic [static..end)             — hover ghost, dots, callout
    private bool _needsGridRebuild = true;
    private bool _needsCurveRebuild = true;
    private int _gridChildCount;
    private int _staticChildCount;

    private void RebuildGraph()
    {
        if (_canvas == null || _vm == null) return;

        var plotW = Math.Max(1.0, _canvas.Bounds.Width - PaddingLeft - PaddingRight);
        var plotH = Math.Max(1.0, _canvas.Bounds.Height - PaddingTop - PaddingBottom);
        if (plotW <= 1 || plotH <= 1) return;

        if (_needsGridRebuild)
        {
            _canvas.Children.Clear();
            DrawGrid(plotW, plotH);
            _gridChildCount = _canvas.Children.Count;
            _needsGridRebuild = false;
            _needsCurveRebuild = true;
        }

        if (_needsCurveRebuild)
        {
            while (_canvas.Children.Count > _gridChildCount)
                _canvas.Children.RemoveAt(_canvas.Children.Count - 1);
            DrawCurve(plotW, plotH);
            _staticChildCount = _canvas.Children.Count;
            _needsCurveRebuild = false;
        }
        else
        {
            // Light rebuild: strip the dynamic layer (hover ghost, point
            // dots, callout, background) and redraw only those. The grid
            // and curve are static — they don't change on hover/exit.
            while (_canvas.Children.Count > _staticChildCount)
                _canvas.Children.RemoveAt(_canvas.Children.Count - 1);
        }

        DrawHoverIndicator(plotW, plotH);
        DrawPoints(plotW, plotH);
        DrawCallout(plotW, plotH);
    }

    private void DrawGrid(double plotW, double plotH)
    {
        if (_canvas == null) return;

        // Horizontal gridlines + Y labels on both sides
        foreach (var (step, label) in YTicks)
        {
            var y = StepToY(step, plotH) + PaddingTop;

            var isMajor = BendEditorGeometry.IsMajorYTick(step);
            _canvas.Children.Add(new Line
            {
                StartPoint = new Point(PaddingLeft, y),
                EndPoint = new Point(PaddingLeft + plotW, y),
                Stroke = new SolidColorBrush(isMajor ? ColorGridMajor : ColorGridMinor),
                StrokeThickness = isMajor ? 1.0 : 0.7,
                StrokeDashArray = isMajor ? null : [2, 3]
            });

            _canvas.Children.Add(MakeLabel(label, 2, y - 7));
            _canvas.Children.Add(MakeLabel(label, PaddingLeft + plotW + 4, y - 7));
        }

        // Vertical slot gridlines
        for (var i = 0; i <= BendEditorViewModel.TimeSlotCount; i++)
        {
            var x = PaddingLeft + plotW * i / BendEditorViewModel.TimeSlotCount;
            _canvas.Children.Add(new Line
            {
                StartPoint = new Point(x, PaddingTop),
                EndPoint = new Point(x, PaddingTop + plotH),
                Stroke = new SolidColorBrush(i % 4 == 0 ? ColorGridMajor : Color.FromRgb(0x20, 0x20, 0x23)),
                StrokeThickness = 0.6
            });
        }
    }

    private void DrawCurve(double plotW, double plotH)
    {
        if (_canvas == null || _vm == null) return;

        // The whole sustain, not just first-to-last point: a rise from 0 on the
        // attack into the first point and a hold after the last — what plays.
        var segments = BendEditorGeometry.BuildSustainSegments(_vm.Points);
        if (segments.Count == 0) return;

        var geom = new StreamGeometry();
        using (var ctx = geom.Open())
        {
            ctx.BeginFigure(PointToCanvas(segments[0].FromSlot, segments[0].FromStep, plotW, plotH), false);
            foreach (var seg in segments)
            {
                var p0 = PointToCanvas(seg.FromSlot, seg.FromStep, plotW, plotH);
                var p1 = PointToCanvas(seg.ToSlot, seg.ToStep, plotW, plotH);
                if (seg.IsHold)
                {
                    ctx.LineTo(p1);
                }
                else
                {
                    var (c0, c1) = BendEditorGeometry.GetCurveBezierControls(p0, p1);
                    ctx.CubicBezierTo(c0, c1, p1);
                }
            }
        }

        _canvas.Children.Add(new Path
        {
            Name = CurvePathName,
            Data = geom,
            Stroke = new SolidColorBrush(ColorCurve),
            StrokeThickness = 2.0,
            StrokeJoin = PenLineJoin.Round
        });

        // Dashed overlay for horizontal ("hold") runs — GP uses a dashed
        // line to show that the pitch is being held without movement.
        foreach (var seg in segments)
        {
            if (!seg.IsHold) continue;

            _canvas.Children.Add(new Line
            {
                StartPoint = PointToCanvas(seg.FromSlot, seg.FromStep, plotW, plotH),
                EndPoint = PointToCanvas(seg.ToSlot, seg.ToStep, plotW, plotH),
                Stroke = new SolidColorBrush(ColorHold),
                StrokeThickness = 1.4,
                StrokeDashArray = [4, 3]
            });
        }
    }

    private void DrawHoverIndicator(double plotW, double plotH)
    {
        if (_canvas == null || _vm == null || _hoverPoint is not { } h) return;
        if (_dragActive) return; // drag state takes over feedback
        if (_hoverHit != null) return; // cursor is on a real point — skip ghost

        var (slot, step) = CanvasToSlotStep(h);
        if (!slot.HasValue) return;

        // Crosshair guides
        var guide = new SolidColorBrush(ColorGuide);
        _canvas.Children.Add(new Line
        {
            StartPoint = new Point(PaddingLeft, h.Y),
            EndPoint = new Point(PaddingLeft + plotW, h.Y),
            Stroke = guide, StrokeThickness = 0.7,
            StrokeDashArray = [3, 3]
        });
        _canvas.Children.Add(new Line
        {
            StartPoint = new Point(h.X, PaddingTop),
            EndPoint = new Point(h.X, PaddingTop + plotH),
            Stroke = guide, StrokeThickness = 0.7,
            StrokeDashArray = [3, 3]
        });

        // Snapped ghost point
        var snapped = new BendEditorViewModel.ControlPoint(slot.Value, SnapStepForDisplay(step));
        var gp = PointToCanvas(snapped, plotW, plotH);
        var ghost = new Ellipse
        {
            Width = HoverGhostR * 2,
            Height = HoverGhostR * 2,
            Fill = new SolidColorBrush(ColorGhost),
            Stroke = new SolidColorBrush(Colors.White),
            StrokeThickness = 1.0,
            StrokeDashArray = [2, 2]
        };
        Canvas.SetLeft(ghost, gp.X - HoverGhostR);
        Canvas.SetTop(ghost, gp.Y - HoverGhostR);
        _canvas.Children.Add(ghost);
    }

    private void DrawPoints(double plotW, double plotH)
    {
        if (_canvas == null || _vm == null) return;

        foreach (var p in _vm.Points)
        {
            var isSelected = ReferenceEquals(_vm.SelectedPoint, p);
            var isHover = ReferenceEquals(_hoverHit, p);
            var r = isSelected ? SelectedDrawR : PointDrawRadius;

            var pt = PointToCanvas(p, plotW, plotH);

            // Selected/hover halo — a larger translucent disc behind the dot.
            if (isSelected || isHover)
            {
                var halo = new Ellipse
                {
                    Width = (r + 4) * 2,
                    Height = (r + 4) * 2,
                    Fill = new SolidColorBrush(isSelected
                        ? Color.FromArgb(0x55, ColorSelected.R, ColorSelected.G, ColorSelected.B)
                        : Color.FromArgb(0x33, ColorCurve.R, ColorCurve.G, ColorCurve.B))
                };
                Canvas.SetLeft(halo, pt.X - (r + 4));
                Canvas.SetTop(halo, pt.Y - (r + 4));
                _canvas.Children.Add(halo);
            }

            var dot = new Ellipse
            {
                Width = r * 2,
                Height = r * 2,
                Fill = new SolidColorBrush(isSelected ? ColorSelected : ColorPoint),
                Stroke = new SolidColorBrush(Colors.White),
                StrokeThickness = isSelected ? 1.8 : 1.2
            };
            Canvas.SetLeft(dot, pt.X - r);
            Canvas.SetTop(dot, pt.Y - r);
            _canvas.Children.Add(dot);

            // Per-point value label, placed above the dot unless it would
            // clip the top of the plot — in that case put it underneath.
            var labelText = BendEditorViewModel.StepLabel(p.Step);
            var aboveY = pt.Y - r - 16;
            var below = aboveY < PaddingTop + 2;
            var tb = new TextBlock
            {
                Text = labelText,
                Foreground = new SolidColorBrush(isSelected ? ColorSelected : ColorLabel),
                FontSize = 10,
                FontWeight = isSelected ? FontWeight.SemiBold : FontWeight.Normal,
                TextAlignment = TextAlignment.Center,
                Width = 40
            };
            Canvas.SetLeft(tb, pt.X - 20);
            Canvas.SetTop(tb, below ? pt.Y + r + 2 : aboveY);
            _canvas.Children.Add(tb);
        }
    }

    private void DrawCallout(double plotW, double plotH)
    {
        if (_canvas == null || _vm == null) return;

        // Two modes: hovering/dragging a real point → show its (slot, step).
        // Hovering empty space → show the would-be snapped value.
        var focus = _dragPoint ?? _hoverHit;
        string text;
        Point anchor;

        if (focus != null)
        {
            text = $"Slot {focus.TimeSlot} · {BendEditorViewModel.StepLabel(focus.Step)}";
            anchor = PointToCanvas(focus, plotW, plotH);
        }
        else if (_hoverPoint is { } h)
        {
            var (slot, step) = CanvasToSlotStep(h);
            if (!slot.HasValue) return;
            text = $"Slot {slot.Value} · {BendEditorViewModel.StepLabel(SnapStepForDisplay(step))}";
            anchor = h;
        }
        else
        {
            return;
        }

        var bg = new Border
        {
            Background = new SolidColorBrush(ColorCallout),
            BorderBrush = new SolidColorBrush(ColorGridMajor),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(3),
            Padding = new Thickness(6, 2, 6, 3),
            Child = new TextBlock
            {
                Text = text,
                Foreground = new SolidColorBrush(ColorCalloutText),
                FontSize = 10,
                FontWeight = FontWeight.SemiBold
            }
        };

        // Rough width estimate: 6.5 px per character + padding/border.
        var estWidth = text.Length * 6.5 + 14;
        var x = anchor.X + 12;
        var y = anchor.Y - 26;
        if (x + estWidth > PaddingLeft + plotW) x = anchor.X - 12 - estWidth;
        if (y < PaddingTop) y = anchor.Y + 14;

        Canvas.SetLeft(bg, x);
        Canvas.SetTop(bg, y);
        _canvas.Children.Add(bg);
    }

    private static TextBlock MakeLabel(string text, double x, double y)
    {
        var tb = new TextBlock
        {
            Text = text,
            Foreground = new SolidColorBrush(ColorLabel),
            FontSize = 10,
            TextAlignment = TextAlignment.Left,
            Width = 24
        };
        Canvas.SetLeft(tb, x);
        Canvas.SetTop(tb, y);
        return tb;
    }

    private static double StepToY(double step, double plotHeight)
        => BendEditorGeometry.StepToY(step, plotHeight);

    private static double SnapStepForDisplay(double step)
        => BendEditorGeometry.SnapStepForDisplay(step);

    private static Point PointToCanvas(BendEditorViewModel.ControlPoint p, double plotW, double plotH)
        => BendEditorGeometry.PointToCanvas(p, plotW, plotH);

    private static Point PointToCanvas(int slot, double step, double plotW, double plotH)
        => BendEditorGeometry.PointToCanvas(slot, step, plotW, plotH);

    // --- Pointer interaction ---

    private void OnCanvasPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_canvas == null || _vm == null) return;

        _canvas.Focus();

        var pt = e.GetPosition(_canvas);
        var props = e.GetCurrentPoint(_canvas).Properties;
        var hit = HitTestPoint(pt);

        if (props.IsRightButtonPressed)
        {
            if (hit != null) _vm.RemovePoint(hit);
            else _vm.ClearSelection();
            e.Handled = true;
            return;
        }

        if (props.IsMiddleButtonPressed && hit != null)
        {
            _vm.RemovePoint(hit);
            e.Handled = true;
            return;
        }

        if (!props.IsLeftButtonPressed) return;

        // Decide what this press grabs BEFORE opening the transaction. A press
        // in the padding — inside the canvas, outside the plot — hits nothing
        // and CanvasToSlotStep returns no slot, so there was nothing to drag and
        // nothing to capture the pointer with. The old order had already called
        // BeginTransaction: releasing outside the un-captured canvas then never
        // reached OnCanvasPointerReleased, _transactionDepth stayed at 1 for the
        // life of the control, and every later edit was swallowed by
        // Commit()'s `_transactionDepth > 0` branch — the bend editor went
        // permanently, silently dead, and each further press/release pair moved
        // the depth 1->2->1 without ever reaching zero.
        var shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        var (slot, step) = CanvasToSlotStep(pt);
        if (hit == null && !slot.HasValue)
        {
            e.Handled = true;
            return;
        }

        // One undo entry per gesture: wrap every mutation in this drag.
        _vm.BeginTransaction();
        _dragActive = true;

        if (hit != null)
        {
            _dragPoint = hit;
            _vm.SelectedPoint = hit;
        }
        else
        {
            _dragPoint = _vm.AddOrMovePoint(slot!.Value, step, shift);
        }

        e.Pointer.Capture(_canvas);
        e.Handled = true;
    }

    private void OnCanvasPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_canvas == null || _vm == null) return;
        var pt = e.GetPosition(_canvas);

        if (_dragActive && _dragPoint != null)
        {
            var (slot, step) = CanvasToSlotStep(pt);
            if (slot.HasValue)
            {
                var freeStep = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
                _vm.MovePoint(_dragPoint, slot.Value, step, freeStep);
            }

            _hoverPoint = pt;
            _hoverHit = _dragPoint;
            RebuildGraph();
            return;
        }

        _hoverPoint = IsInsidePlot(pt) ? pt : null;
        _hoverHit = _hoverPoint.HasValue ? HitTestPoint(pt) : null;
        UpdateCursor();
        RebuildGraph();
    }

    private void OnCanvasPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_dragActive && _vm != null) _vm.EndTransaction();
        _dragActive = false;
        if (_dragPoint != null) e.Pointer.Capture(null);
        _dragPoint = null;
        RebuildGraph();
    }

    private void OnCanvasPointerExited(object? sender, PointerEventArgs e)
    {
        if (_dragActive) return;
        _hoverPoint = null;
        _hoverHit = null;
        UpdateCursor();
        RebuildGraph();
    }

    private void OnCanvasKeyDown(object? sender, KeyEventArgs e)
    {
        if (_vm == null || _vm.SelectedPoint == null)
        {
            // Esc still usable to blur.
            if (e.Key == Key.Escape)
            {
                _vm?.ClearSelection();
                e.Handled = true;
            }

            return;
        }

        var fine = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        var jump = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        var dStep = fine ? BendEditorViewModel.FineStepQuantum : BendEditorViewModel.StepQuantum;
        var dSlot = jump ? 3 : 1;

        _vm.BeginTransaction();
        try
        {
            switch (e.Key)
            {
                case Key.Left:
                    _vm.NudgeSelected(-dSlot, 0, fine);
                    e.Handled = true;
                    break;
                case Key.Right:
                    _vm.NudgeSelected(+dSlot, 0, fine);
                    e.Handled = true;
                    break;
                case Key.Up:
                    _vm.NudgeSelected(0, +dStep, fine);
                    e.Handled = true;
                    break;
                case Key.Down:
                    _vm.NudgeSelected(0, -dStep, fine);
                    e.Handled = true;
                    break;
                case Key.Delete:
                case Key.Back:
                    _vm.DeleteSelected();
                    e.Handled = true;
                    break;
                case Key.Escape:
                    _vm.ClearSelection();
                    e.Handled = true;
                    break;
            }
        }
        finally
        {
            _vm.EndTransaction();
        }
    }

    private void UpdateCursor()
    {
        if (_canvas == null) return;
        if (_dragActive)
        {
            _canvas.Cursor = new Cursor(StandardCursorType.Hand);
            return;
        }

        if (_hoverHit != null)
        {
            _canvas.Cursor = new Cursor(StandardCursorType.Hand);
            return;
        }

        if (_hoverPoint.HasValue)
        {
            _canvas.Cursor = new Cursor(StandardCursorType.Cross);
            return;
        }

        _canvas.Cursor = Cursor.Default;
    }

    private bool IsInsidePlot(Point p)
    {
        if (_canvas == null) return false;
        return BendEditorGeometry.IsInsideCanvas(p, _canvas.Bounds.Width, _canvas.Bounds.Height);
    }

    private BendEditorViewModel.ControlPoint? HitTestPoint(Point pt)
    {
        if (_canvas == null || _vm == null) return null;
        if (!BendEditorGeometry.TryGetPlotSize(_canvas.Bounds.Width, _canvas.Bounds.Height, out var plotW, out var plotH))
            return null;

        return BendEditorGeometry.HitTestPoint(_vm.Points, pt, plotW, plotH);
    }

    private (int? slot, double step) CanvasToSlotStep(Point pt)
    {
        if (_canvas == null) return (null, 0);
        return BendEditorGeometry.CanvasToSlotStep(pt, _canvas.Bounds.Width, _canvas.Bounds.Height);
    }
}
