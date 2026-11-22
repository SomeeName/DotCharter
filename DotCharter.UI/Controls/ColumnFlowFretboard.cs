using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using DotCharter.UI.Controls.Themes;

namespace DotCharter.UI.Controls;

/// <summary>
///     The Shape page's fretboard (inspector design D): a chord box whose string lanes are exactly
///     <see cref="LaneHeight" /> tall, so each string lines up with its row of the grip grid beside it, under
///     a fret scale <see cref="ScaleHeight" /> tall. It shows the grip's frets in a window that fits the
///     width (a stretch of up to eight frets at once), pages that window lower or higher
///     (<see cref="PageFrets" />, <see cref="JumpToFret" />), and marks notes outside it at the right edge.
///     Keyboard: arrows move a cursor, Enter or Space places or clears the fret under it, Delete clears the
///     string, Escape drops the cursor.
/// </summary>
public sealed class ColumnFlowFretboard : ChordBoxControl
{
    public const int LaneHeight = 28;
    public const int ScaleHeight = 18;
    private const int Rail = 56;
    private const int EndInset = 4;
    private const int EdgeWidth = 30;
    private const int CompactSpan = 8;
    private const int MinimumFretWidth = 16;

    /// <summary>A compact board's string pitch (the Library's read-only grip): the board is then one scale row, half a lane
    /// above the first and below the last string, and this much between strings, so it grows with the string count
    /// instead of squeezing 8 strings into a fixed height. NaN (the default): full <see cref="LaneHeight" /> lanes.</summary>
    public static readonly StyledProperty<double> LanePitchProperty =
        AvaloniaProperty.Register<ColumnFlowFretboard, double>(nameof(LanePitch), double.NaN);

    public static readonly DirectProperty<ColumnFlowFretboard, string> WindowLabelProperty =
        AvaloniaProperty.RegisterDirect<ColumnFlowFretboard, string>(nameof(WindowLabel), board => board.WindowLabel);

    /// <summary>Strings whose fingers disagree across the targets, with the dot's caption ("1/2"): the dot gets a dashed
    /// warning ring and that caption instead of the shape's finger. Bind a FRESH dictionary to re-render.</summary>
    public static readonly StyledProperty<IReadOnlyDictionary<int, string>?> MixedFingersProperty =
        AvaloniaProperty.Register<ColumnFlowFretboard, IReadOnlyDictionary<int, string>?>(nameof(MixedFingers));

    private (int String, int Fret)? _hover;
    private int? _requestedFret;
    private string _windowLabel = "";

    /// <summary>The frets on screen, "4–8" (or "12" for one), for the page header.</summary>
    public string WindowLabel
    {
        get => _windowLabel;
        private set => SetAndRaise(WindowLabelProperty, ref _windowLabel, value);
    }

    static ColumnFlowFretboard()
    {
        AffectsMeasure<ColumnFlowFretboard>(StringCountProperty, LanePitchProperty);
        AffectsRender<ColumnFlowFretboard>(MixedFingersProperty);
    }

    public IReadOnlyDictionary<int, string>? MixedFingers
    {
        get => GetValue(MixedFingersProperty);
        set => SetValue(MixedFingersProperty, value);
    }

    public double LanePitch
    {
        get => GetValue(LanePitchProperty);
        set => SetValue(LanePitchProperty, value);
    }

    public ChordBoxGeometry Lanes => Geometry;

    public bool ShowsNut => Lanes.MinFret == Capo + 1;

    internal (int String, int Fret)? KeyCursor { get; private set; }

    protected override ChordBoxGeometry Geometry
    {
        get
        {
            var frets = Chord?.ChordNotes.Select(note => note.Fret).Where(fret => fret > Capo).ToArray() ?? [];
            var window = ChooseWindow(Bounds.Width, frets);
            var edges = frets.Any(fret => fret < window.First || fret > window.Last);
            return base.Geometry with
            {
                MinFret = window.First,
                MaxFret = window.Last,
                MaskWidth = LaneHeight,
                OpenWidth = Rail - LaneHeight,
                FretRightInset = EndInset + (edges ? EdgeWidth : 0),
                StringTopInset = LaneHeight / 2.0 + ScaleHeight,
                StringBottomInset = LaneHeight / 2.0
            };
        }
    }

    // As tall as the grip grid beside it: the scale row plus one lane per string; a compact board the scale row, half a
    // lane at each end and LanePitch between strings.
    protected override Size MeasureOverride(Size availableSize) => double.IsNaN(LanePitch)
        ? new Size(0, ScaleHeight + Math.Max(1, StringCount) * LaneHeight)
        : new Size(0, ScaleHeight + LaneHeight + Math.Max(0, StringCount - 1) * LanePitch);

    public void JumpToFret(int fret)
    {
        _requestedFret = Math.Clamp(fret, 0, FretLimits.MaxFret);
        _hover = null;
        Refresh();
    }

    /// <summary>Pages the window one width lower (-1) or higher (+1): the pointer path to frets outside it.</summary>
    public void PageFrets(int direction)
    {
        var lanes = Lanes;
        var span = lanes.MaxFret - lanes.MinFret + 1;
        JumpToFret((lanes.MinFret + lanes.MaxFret) / 2 + direction * span);
    }

    /// <summary>Back to the window that frames the grip.</summary>
    public void ShowShape()
    {
        _requestedFret = null;
        Refresh();
    }

    private void Refresh()
    {
        InvalidateVisual();
        UpdateWindowLabel();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == BoundsProperty || change.Property == ChordProperty || change.Property == CapoProperty)
            UpdateWindowLabel();
    }

    private void UpdateWindowLabel()
    {
        var lanes = Lanes;
        WindowLabel = lanes.MinFret == lanes.MaxFret
            ? lanes.MinFret.ToString(CultureInfo.InvariantCulture)
            : string.Create(CultureInfo.InvariantCulture, $"{lanes.MinFret}–{lanes.MaxFret}");
    }

    internal (int First, int Last) ChooseWindow(double width, int[] frets)
    {
        var (first, last) = ChordBoxGeometry.DisplayFrets(frets, Capo);
        last = Math.Min(FretLimits.MaxFret, last);
        var floor = Math.Min(FretLimits.MaxFret, Capo + 1);
        if (frets.Length == 0 && _requestedFret is null) return (first, last);
        var start = frets.Length == 0 ? floor : frets.Min();
        var end = frets.Length == 0 ? floor : frets.Max();
        var span = end - start + 1;
        var room = width - Rail - EndInset;
        if ((_requestedFret is null || _requestedFret >= start && _requestedFret <= end) && span <= CompactSpan)
            return span >= 6 || room / (last - first + 1) < 15 ? (start, end) : (first, last);

        var capacity = Math.Max(CompactSpan, (int)Math.Floor(room / MinimumFretWidth));
        if (_requestedFret is null && span <= capacity) return (start, end);
        capacity = Math.Clamp((int)Math.Floor((room - EdgeWidth) / MinimumFretWidth), 1, FretLimits.MaxFret - floor + 1);
        var target = Math.Clamp(_requestedFret ?? start, floor, FretLimits.MaxFret);
        first = Math.Clamp(target - (_requestedFret is null ? 0 : capacity / 2), floor, 25 - capacity);
        return (first, first + capacity - 1);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var point = e.GetPosition(this);
        var lanes = Lanes;
        (int, int)? hover = Role != ChordBoxRole.ReadOnly && !lanes.IsInMaskZone(point.X) &&
            lanes.TryHit(point.X, point.Y, Capo, out var index, out var fret) ? (index, fret) : null;
        if (hover == _hover) return;
        _hover = hover;
        InvalidateVisual();
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        if (_hover == null) return;
        _hover = null;
        InvalidateVisual();
    }

    protected override void OnGotFocus(FocusChangedEventArgs e)
    {
        base.OnGotFocus(e);
        if (Role == ChordBoxRole.ReadOnly) return;
        KeyCursor ??= (0, Math.Max(Lanes.MinFret, Capo + 1));
        InvalidateVisual();
    }

    protected override void OnLostFocus(FocusChangedEventArgs e)
    {
        base.OnLostFocus(e);
        KeyCursor = null;
        InvalidateVisual();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled || Role == ChordBoxRole.ReadOnly || KeyCursor is not { } cursor) return;
        if (!HandleCursorKey(e.Key, cursor)) return;
        e.Handled = true;
        InvalidateVisual();
    }

    private bool HandleCursorKey(Key key, (int String, int Fret) cursor)
    {
        var down = StringCount > 1 && Lanes.StringY(1) > Lanes.StringY(0) ? 1 : -1;
        (int String, int Fret)? next = key switch
        {
            Key.Up => (Math.Clamp(cursor.String - down, 0, StringCount - 1), cursor.Fret),
            Key.Down => (Math.Clamp(cursor.String + down, 0, StringCount - 1), cursor.Fret),
            Key.Left => (cursor.String, Math.Max(0, cursor.Fret - 1)),
            Key.Right => (cursor.String, Math.Min(FretLimits.MaxFret, cursor.Fret + 1)),
            _ => null
        };
        if (next is { } moved)
        {
            KeyCursor = moved;
            if (moved.Fret > Capo && (moved.Fret < Lanes.MinFret || moved.Fret > Lanes.MaxFret)) JumpToFret(moved.Fret);
            return true;
        }

        switch (key)
        {
            case Key.Enter or Key.Space:
                Run(ToggleNoteCommand, (cursor.String, cursor.Fret));
                return true;
            case Key.Delete or Key.Back:
                Run(ClearStringCommand, cursor.String);
                return true;
            case Key.Escape:
                KeyCursor = null;
                return true;
            default:
                return false;
        }
    }

    private static void Run(System.Windows.Input.ICommand? command, object parameter)
    {
        if (command?.CanExecute(parameter) == true) command.Execute(parameter);
    }

    public override void Render(DrawingContext context)
    {
        var bounds = new Rect(Bounds.Size);
        if (bounds.Width <= 0 || bounds.Height <= 0) return;
        var lanes = Lanes;
        var surface = FindBrush("FireSurfaceSunken", ChartTheme.BottomMarginBgBrush);
        var labelInk = FindBrush("FireTextBright", Brushes.White);
        context.FillRectangle(surface, bounds);
        DrawFrets(context, lanes, labelInk);
        DrawScale(context, lanes);
        DrawStrings(context, lanes);
        if (_hover is { } hover && MarkerX(lanes, hover.Fret) is { } hoverX)
            context.DrawEllipse(ChartTheme.HoverBrush, null, new Point(hoverX, lanes.StringY(hover.String)), 8, 8);
        DrawNotes(context, lanes, surface);
        DrawEdgeNotes(context, lanes);
        if (KeyCursor is { } cursor && MarkerX(lanes, cursor.Fret) is { } cursorX)
        {
            var focus = new Pen(FindBrush("FireAccentBright", labelInk), 2);
            context.DrawRectangle(null, focus, new Rect(cursorX - 11, lanes.StringY(cursor.String) - 11, 22, 22), 3, 3);
        }
        DrawMutes(context, lanes, labelInk);
        // The selection's validation pips at the strings' far end, as the chord box draws them.
        DrawIssuePips(context, lanes, bounds.Width - 5);
    }

    private void DrawFrets(DrawingContext context, ChordBoxGeometry lanes, IBrush labelInk)
    {
        var wire = new Pen(FindBrush("FireBorderStrong", Brushes.Gray));
        var top = lanes.StringTopInset;
        var bottom = lanes.Height - lanes.StringBottomInset;
        for (var fret = 0; fret <= lanes.FretCount; fret++)
        {
            var x = lanes.NutX + fret * lanes.FretSpacing;
            context.DrawLine(fret == 0 && ShowsNut ? new Pen(labelInk, 4) : wire, new Point(x, top), new Point(x, bottom));
        }
    }

    private void DrawStrings(DrawingContext context, ChordBoxGeometry lanes)
    {
        for (var index = 0; index < StringCount; index++)
        {
            var y = lanes.StringY(index);
            var color = ChartTheme.StringBrushes[ChartTheme.StringSlot(index, StringCount)];
            if (Mask.IsExplicit(index) || _hover?.String == index)
                context.FillRectangle(ChartTheme.HoverBrush, new Rect(0, y - LaneHeight / 2d, Bounds.Width, LaneHeight));
            context.DrawLine(new Pen(color, 1.5), new Point(lanes.NutX, y), new Point(lanes.NutX + lanes.FretboardWidth, y));
            DrawMaskPip(context, lanes, y, Mask.IsExplicit(index));
        }
    }

    private void DrawMutes(DrawingContext context, ChordBoxGeometry lanes, IBrush labelInk)
    {
        if (MutedStrings is not { } muted) return;
        var pen = new Pen(labelInk, 1.5);
        foreach (var index in muted.Where(index => index >= 0 && index < StringCount))
        {
            var center = new Point(lanes.OpenMarkerX, lanes.StringY(index));
            context.DrawLine(pen, center - new Vector(4, 4), center + new Vector(4, 4));
            context.DrawLine(pen, center - new Vector(4, -4), center + new Vector(4, -4));
        }
    }

    private double? MarkerX(ChordBoxGeometry lanes, int fret) => fret <= Capo ? lanes.OpenMarkerX
        : fret >= lanes.MinFret && fret <= lanes.MaxFret ? lanes.FretCenterX(fret) : null;

    private void DrawNotes(DrawingContext context, ChordBoxGeometry lanes, IBrush surface)
    {
        foreach (var note in Chord?.ChordNotes ?? [])
        {
            var index = note.StringIndex;
            if (index < 0 || index >= StringCount) continue;
            var y = lanes.StringY(index);
            var color = ChartTheme.StringBrushes[ChartTheme.StringSlot(index, StringCount)];
            var ghost = Template?.IsGhost(index) == true;
            if (note.Fret <= Capo)
            {
                // A ghosted open string is drawn dashed so it never reads as a struck one.
                var open = ghost ? new Pen(color, 2, new DashStyle([1.5, 1.5], 0)) : new Pen(color, 2);
                context.DrawEllipse(null, open, new Point(lanes.OpenMarkerX, y), 6, 6);
                continue;
            }
            if (note.Fret < lanes.MinFret || note.Fret > lanes.MaxFret) continue;
            var center = new Point(lanes.FretCenterX(note.Fret), y);
            var radius = Math.Clamp(lanes.FretSpacing / 2 - 0.5, 2, 8.5);
            context.DrawEllipse(ghost ? surface : color, ghost ? new Pen(color, 2) : null, center, radius, radius);
            var fill = ghost && surface is ISolidColorBrush ghostFill ? ghostFill.Color : color.Color;
            var ink = FingerInk(fill) == Colors.Black ? Brushes.Black : Brushes.White;
            if (MixedFingers?.TryGetValue(index, out var mixed) == true)
            {
                context.DrawEllipse(null, new Pen(FindBrush("FireWarning", Brushes.Orange), 1.5, new DashStyle([2, 1.5], 0)),
                    center, radius + 2.5, radius + 2.5);
                if (radius < 5) continue;
                var label = Format(mixed, ink, mixed.Length > 3 ? 7 : 8);
                context.DrawText(label, new Point(center.X - label.Width / 2, center.Y - label.Height / 2));
                continue;
            }

            if (radius >= 5 && Template?.Fingers is { } fingers && fingers.TryGetValue(index, out var finger))
            {
                var caption = Format(finger == 0 ? "T" : finger.ToString(CultureInfo.InvariantCulture), ink, radius < 8 ? 10 : 11);
                context.DrawText(caption, new Point(center.X - caption.Width / 2, center.Y - caption.Height / 2));
            }
        }
    }

    // A fretted note outside the window: its fret at the right edge, with an arrow toward it.
    private void DrawEdgeNotes(DrawingContext context, ChordBoxGeometry lanes)
    {
        var right = lanes.NutX + lanes.FretboardWidth;
        foreach (var note in Chord?.ChordNotes ?? [])
        {
            if (note.StringIndex < 0 || note.StringIndex >= StringCount || note.Fret <= Capo ||
                note.Fret >= lanes.MinFret && note.Fret <= lanes.MaxFret)
            {
                continue;
            }

            var color = ChartTheme.StringBrushes[ChartTheme.StringSlot(note.StringIndex, StringCount)];
            var fret = note.Fret.ToString(CultureInfo.InvariantCulture);
            var text = Format(note.Fret < lanes.MinFret ? $"‹{fret}" : $"{fret}›", color, 10);
            context.DrawText(text, new Point(right + 3, lanes.StringY(note.StringIndex) - text.Height / 2));
        }
    }

    internal static Color FingerInk(Color fill) => Luminance(fill) >= Math.Sqrt(1.05 * 0.05) - 0.05 ? Colors.Black : Colors.White;

    internal static double Luminance(Color color)
    {
        static double Linear(byte value)
        {
            var channel = value / 255d;
            return channel <= 0.04045 ? channel / 12.92 : Math.Pow((channel + 0.055) / 1.055, 2.4);
        }

        return 0.2126 * Linear(color.R) + 0.7152 * Linear(color.G) + 0.0722 * Linear(color.B);
    }

    private void DrawScale(DrawingContext context, ChordBoxGeometry geometry)
    {
        var ink = FindBrush("FireTextMuted", Brushes.White);
        var stagger = geometry.FretSpacing < 13;
        var size = stagger ? 10 : 11;
        if (ShowsNut)
        {
            var open = Format(Capo.ToString(CultureInfo.InvariantCulture), ink, size);
            context.DrawText(open, new Point(geometry.OpenMarkerX - open.Width / 2, 0));
        }

        for (var fret = geometry.MinFret; fret <= geometry.MaxFret; fret++)
        {
            var text = Format(fret.ToString(CultureInfo.InvariantCulture), ink, size);
            var y = stagger && (fret - geometry.MinFret) % 2 == 1 ? 6 : 0;
            context.DrawText(text, new Point(geometry.FretCenterX(fret) - text.Width / 2, y));
        }
    }

    private IBrush FindBrush(string key, IBrush fallback) =>
        this.TryFindResource(key, out var resource) && resource is IBrush brush ? brush : fallback;

    private static FormattedText Format(string value, IBrush ink, double size) =>
        new(value, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface(FontFamily.Default, FontStyle.Normal, FontWeight.SemiBold), size, ink);
}
