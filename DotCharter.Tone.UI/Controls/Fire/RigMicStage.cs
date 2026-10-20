using Avalonia;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Media;
using DotCharter.Tone.UI.Controls.Dsp;

namespace DotCharter.Tone.UI.Controls.Fire;

/// <summary>
///     The mic placement's FRONT view, the way amp sims show it: the speaker, and the
///     microphone on it. Drag (or click) the mic anywhere on the cone to place the
///     capsule — its distance from the centre is the native model's
///     <see cref="Position" /> (0 dust cap … 1 cone edge); the angle around the cone
///     follows the pointer but a round speaker cannot hear it. The wheel moves the
///     mic toward or away from the grille (<see cref="DistanceCm" />, Shift for fine),
///     which the drawing shows as the mic shrinking and its shadow falling further
///     off. Arrow keys nudge along the radius. The second, ribbon, mic of the model
///     is drawn where it stands whenever it is blended in.
/// </summary>
public sealed class RigMicStage : FireDrawnControl
{
    /// <summary>Where the mic sits with no mic unit bound: between the cone's centre and its
    /// edge. A const so markup can name it with <c>{x:Static}</c> — a decimal literal
    /// <c>FallbackValue</c> parses with the UI culture and fails in de-DE.</summary>
    public const double DefaultPosition = 0.3;

    public static readonly StyledProperty<double> PositionProperty =
        AvaloniaProperty.Register<RigMicStage, double>(nameof(Position), DefaultPosition, defaultBindingMode: BindingMode.TwoWay);

    public static readonly StyledProperty<double> DistanceCmProperty =
        AvaloniaProperty.Register<RigMicStage, double>(nameof(DistanceCm), 5, defaultBindingMode: BindingMode.TwoWay);

    public static readonly StyledProperty<int> MicTypeProperty =
        AvaloniaProperty.Register<RigMicStage, int>(nameof(MicType));

    public static readonly StyledProperty<double> SecondMicBlendProperty =
        AvaloniaProperty.Register<RigMicStage, double>(nameof(SecondMicBlend));

    /// <summary>The angle around the cone the mic is drawn at (degrees, clockwise
    /// from 12 o'clock). Placement state only: the model has no bearing.</summary>
    public static readonly StyledProperty<double> BearingDegreesProperty =
        AvaloniaProperty.Register<RigMicStage, double>(nameof(BearingDegrees), MicPlacementMath.DefaultBearingDegrees,
            defaultBindingMode: BindingMode.TwoWay);

    /// <summary>False: the speaker alone (a cabinet with no mic yet).</summary>
    public static readonly StyledProperty<bool> ShowsMicProperty =
        AvaloniaProperty.Register<RigMicStage, bool>(nameof(ShowsMic), true);

    private bool _dragging;
    private bool _hover;

    static RigMicStage()
    {
        AffectsRender<RigMicStage>(PositionProperty, DistanceCmProperty, MicTypeProperty, SecondMicBlendProperty, BearingDegreesProperty, ShowsMicProperty);
        FocusableProperty.OverrideDefaultValue<RigMicStage>(true);
    }

    public RigMicStage()
    {
        Cursor = new Cursor(StandardCursorType.Hand);
        ClipToBounds = true;
    }

    public double Position { get => GetValue(PositionProperty); set => SetValue(PositionProperty, value); }
    public double DistanceCm { get => GetValue(DistanceCmProperty); set => SetValue(DistanceCmProperty, value); }
    public int MicType { get => GetValue(MicTypeProperty); set => SetValue(MicTypeProperty, value); }
    public double SecondMicBlend { get => GetValue(SecondMicBlendProperty); set => SetValue(SecondMicBlendProperty, value); }
    public double BearingDegrees { get => GetValue(BearingDegreesProperty); set => SetValue(BearingDegreesProperty, value); }
    public bool ShowsMic { get => GetValue(ShowsMicProperty); set => SetValue(ShowsMicProperty, value); }

    /// <summary>The speaker's centre and the cone's radius in this control (test seam).</summary>
    internal (Point Centre, double ConeRadius) Geometry()
    {
        var size = Math.Min(Bounds.Width, Bounds.Height);
        var basket = size * 0.46;
        return (new Point(Bounds.Width / 2, Bounds.Height / 2), basket * 0.80);
    }

    /// <summary>Where the capsule is drawn (test seam).</summary>
    internal Point MicPoint()
    {
        var (c, cone) = Geometry();
        var (x, y) = MicPlacementMath.Point(Position, BearingDegrees);
        return new Point(c.X + x * cone, c.Y + y * cone);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        // Square, as big as it is given, and something sensible when it is not.
        var side = Math.Min(double.IsInfinity(availableSize.Width) ? 320 : availableSize.Width,
            double.IsInfinity(availableSize.Height) ? 320 : availableSize.Height);
        return new Size(side, side);
    }

    private MicArt.Palette Palette() => new(
        Paint.Color(this, "FireHardwareShadow"), Paint.Color(this, "FireHardwareEdge"),
        Paint.Color(this, "FireHardwareHighlight"), Paint.Color(this, "FireHardwareSwitchRing"),
        Paint.Color(this, "FireHardwareGrille"), Paint.Color(this, "FireWarning"),
        Paint.Color(this, "FireTextSecondary"));

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        context.DrawRectangle(Brushes.Transparent, null, new Rect(Bounds.Size)); // the whole square takes the pointer
        var p = Palette();
        var (c, _) = Geometry();
        var basket = Math.Min(Bounds.Width, Bounds.Height) * 0.46;
        var cone = MicArt.DrawSpeakerFront(context, c, basket, p);
        if (!ShowsMic) return;

        // The zone the capsule is in, as a faint ring, while it is being placed.
        if (_dragging || _hover)
        {
            var zone = MicPlacementMath.Zone(Position);
            var (inner, outer) = zone switch { 0 => (0.0, 0.18), 1 => (0.18, 0.40), 2 => (0.40, 0.72), _ => (0.72, 1.0) };
            var band = new Pen(new SolidColorBrush(MicArt.Alpha(p.Accent, 0.28)), Math.Max(2, (outer - inner) * cone));
            context.DrawEllipse(null, band, c, (inner + outer) / 2 * cone, (inner + outer) / 2 * cone);
        }

        var micSize = cone * 0.36 * MicPlacementMath.PerspectiveScale(DistanceCm);

        // The second mic: the model's ribbon at 10 cm, blended in by SecondMicBlend.
        if (SecondMicBlend > 0.001)
        {
            var (sx, sy) = MicPlacementMath.Point(0.55, 300);
            using (context.PushOpacity(0.35 + 0.65 * Math.Clamp(SecondMicBlend, 0, 1)))
            {
                MicArt.DrawMicFront(context, MicArt.Mic.Ribbon121, new Point(c.X + sx * cone, c.Y + sy * cone),
                    cone * 0.36 * MicPlacementMath.PerspectiveScale(10), 300, MicPlacementMath.ShadowOffset(10), p, lit: false);
            }
        }

        MicArt.DrawMicFront(context, MicArt.MicFor(MicType), MicPoint(), micSize, BearingDegrees,
            MicPlacementMath.ShadowOffset(DistanceCm), p, lit: _dragging || IsFocused && IsKeyboardFocusWithin);
    }

    // ── input ─────────────────────────────────────────────────────────────────

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        _dragging = true;
        e.Pointer.Capture(this);
        PlaceAt(e.GetPosition(this));
        Focus();
        e.Handled = true; // a placement is not a board drag
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_dragging) PlaceAt(e.GetPosition(this));
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (!_dragging) return;
        _dragging = false;
        e.Pointer.Capture(null);
        InvalidateVisual();
    }

    protected override void OnPointerEntered(PointerEventArgs e)
    {
        base.OnPointerEntered(e);
        _hover = true;
        InvalidateVisual();
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        _hover = false;
        InvalidateVisual();
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        var step = (e.KeyModifiers & KeyModifiers.Shift) != 0 ? 0.2 : 1.0;
        // wheel up brings the mic IN, toward the grille
        DistanceCm = Math.Clamp(DistanceCm - Math.Sign(e.Delta.Y) * step, 0, MicPlacementMath.MaxDistanceCm);
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        var fine = (e.KeyModifiers & KeyModifiers.Shift) != 0 ? 0.01 : 0.05;
        switch (e.Key)
        {
            case Key.Right or Key.Down:
                Position = MicPlacementMath.Nudge(Position, fine);
                e.Handled = true;
                break;
            case Key.Left or Key.Up:
                Position = MicPlacementMath.Nudge(Position, -fine);
                e.Handled = true;
                break;
            case Key.PageUp:
                DistanceCm = Math.Max(0, DistanceCm - 1);
                e.Handled = true;
                break;
            case Key.PageDown:
                DistanceCm = Math.Min(MicPlacementMath.MaxDistanceCm, DistanceCm + 1);
                e.Handled = true;
                break;
        }
    }

    /// <summary>Places the capsule under <paramref name="point" /> (control coordinates).</summary>
    internal void PlaceAt(Point point)
    {
        var (c, cone) = Geometry();
        if (cone <= 0) return;
        var (position, bearing) = MicPlacementMath.Place((point.X - c.X) / cone, (point.Y - c.Y) / cone);
        BearingDegrees = bearing;
        Position = position;
    }
}
