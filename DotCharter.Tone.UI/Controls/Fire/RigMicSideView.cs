using System.Globalization;
using Avalonia;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Media;
using DotCharter.Tone.UI.Controls.Dsp;

namespace DotCharter.Tone.UI.Controls.Fire;

/// <summary>
///     The mic placement's SIDE view: the speaker's profile on the left, a ruler in
///     centimetres, and the microphone lying on it pointing at the grille. Drag the
///     mic left or right to set its <see cref="DistanceCm" /> (0 on the cloth … 30
///     cm back) — close in is the proximity effect's bass, further back the floor
///     bounce and a thinner, more open sound.
/// </summary>
public sealed class RigMicSideView : FireDrawnControl
{
    public static readonly StyledProperty<double> DistanceCmProperty =
        AvaloniaProperty.Register<RigMicSideView, double>(nameof(DistanceCm), 5, defaultBindingMode: BindingMode.TwoWay);

    public static readonly StyledProperty<int> MicTypeProperty =
        AvaloniaProperty.Register<RigMicSideView, int>(nameof(MicType));

    private bool _dragging;
    private double _grabOffset;

    static RigMicSideView()
    {
        AffectsRender<RigMicSideView>(DistanceCmProperty, MicTypeProperty);
        FocusableProperty.OverrideDefaultValue<RigMicSideView>(true);
    }

    public RigMicSideView()
    {
        Cursor = new Cursor(StandardCursorType.SizeWestEast);
        ClipToBounds = true;
    }

    public double DistanceCm { get => GetValue(DistanceCmProperty); set => SetValue(DistanceCmProperty, value); }
    public int MicType { get => GetValue(MicTypeProperty); set => SetValue(MicTypeProperty, value); }

    /// <summary>The grille's x and the ruler's length (test seam).</summary>
    internal (double GrilleX, double Length) Ruler()
    {
        // The ruler spans the strip; a mic far back runs off its right edge, the
        // way a real side view crops the stand (the capsule is what is measured).
        var h = Bounds.Height;
        var grille = h * 0.42;
        return (grille, Math.Max(20, Bounds.Width - grille - h * 0.35));
    }

    /// <summary>Where the capsule's front is drawn (test seam).</summary>
    internal double MicFrontX()
    {
        var (grille, length) = Ruler();
        return grille + MicPlacementMath.FractionForDistance(DistanceCm) * length;
    }

    protected override Size MeasureOverride(Size availableSize)
        => new(double.IsInfinity(availableSize.Width) ? 320 : availableSize.Width, 96);

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var p = new MicArt.Palette(
            Paint.Color(this, "FireHardwareShadow"), Paint.Color(this, "FireHardwareEdge"),
            Paint.Color(this, "FireHardwareHighlight"), Paint.Color(this, "FireHardwareSwitchRing"),
            Paint.Color(this, "FireHardwareGrille"), Paint.Color(this, "FireWarning"),
            Paint.Color(this, "FireTextSecondary"));
        // the whole strip takes the pointer, not just the ink (a press between two
        // ticks fell through to the panel behind)
        context.DrawRectangle(Brushes.Transparent, null, new Rect(Bounds.Size));
        var h = Bounds.Height;
        var (grille, length) = Ruler();
        var axis = h * 0.44;

        MicArt.DrawSpeakerSide(context, grille, h * 0.06, h * 0.82, p);

        // The ruler: a tick every 1 cm, a longer one and a number every 5.
        var tick = new Pen(new SolidColorBrush(MicArt.Alpha(p.Ink, 0.45)), 1);
        var baseY = h * 0.86;
        context.DrawLine(tick, new Point(grille, baseY), new Point(grille + length, baseY));
        var typeface = new Typeface(FontFamily.Default);
        var ink = new SolidColorBrush(p.Ink);
        for (var cm = 0; cm <= (int)MicPlacementMath.MaxDistanceCm; cm++)
        {
            var x = grille + cm / MicPlacementMath.MaxDistanceCm * length;
            var major = cm % 5 == 0;
            context.DrawLine(tick, new Point(x, baseY), new Point(x, baseY - (major ? h * 0.08 : h * 0.04)));
            if (!major) continue;
            var label = new FormattedText(cm.ToString(CultureInfo.CurrentCulture), CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight, typeface, Math.Max(8, h * 0.11), ink);
            context.DrawText(label, new Point(x - label.Width / 2, baseY + 1));
        }

        // A dashed guide from the grille to the capsule: the distance it measures.
        var front = MicFrontX();
        var guide = new Pen(new SolidColorBrush(MicArt.Alpha(p.Accent, 0.6)), 1) { DashStyle = new DashStyle([3, 3], 0) };
        context.DrawLine(guide, new Point(grille, axis), new Point(front, axis));

        MicArt.DrawMicSide(context, MicArt.MicFor(MicType), new Point(front, axis), h * 0.34, p, lit: _dragging);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        _dragging = true;
        e.Pointer.Capture(this);
        var at = e.GetPosition(this);
        var x = at.X;
        // grabbing the mic keeps the grab point under the pointer; a press on the
        // ruler (or anywhere off the mic) jumps the capsule there
        var front = MicFrontX();
        var onMic = at.Y < Bounds.Height * 0.72 && x >= front && x <= front + Bounds.Height * 1.6;
        _grabOffset = onMic ? x - front : 0;
        DragTo(x);
        Focus();
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_dragging) DragTo(e.GetPosition(this).X);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (!_dragging) return;
        _dragging = false;
        e.Pointer.Capture(null);
        InvalidateVisual();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        var step = (e.KeyModifiers & KeyModifiers.Shift) != 0 ? 0.2 : 1.0;
        if (e.Key is Key.Left or Key.Down) { DistanceCm = Math.Max(0, DistanceCm - step); e.Handled = true; }
        else if (e.Key is Key.Right or Key.Up) { DistanceCm = Math.Min(MicPlacementMath.MaxDistanceCm, DistanceCm + step); e.Handled = true; }
    }

    private void DragTo(double x)
    {
        var (grille, length) = Ruler();
        DistanceCm = Math.Round(MicPlacementMath.DistanceForFraction((x - _grabOffset - grille) / length), 1);
    }
}
