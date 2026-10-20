using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace DotCharter.Tone.UI.Controls.Fire;

/// <summary>
/// The insertion marker: a 6 px glowing violet bar showing the EXACT slot a
/// dragged item will land in. Custom-drawn rather than a styled
/// <see cref="Border"/> because the glow is a layered pass — a
/// <c>DrawingContext</c> has no blur, and a <c>BoxShadow</c> on a Border inside
/// an adorner clips against the adorner's own bounds.
///
/// <para>Hit-test invisible: it is a hint painted over the drop target, and it
/// must never eat the pointer events the drag itself is made of.</para>
/// </summary>
public sealed class FireDropMarker : Control
{
    /// <summary>Which way the bar runs. Horizontal lane ⇒ a VERTICAL bar
    /// between two items; vertical lane ⇒ a horizontal one.</summary>
    public static readonly StyledProperty<Orientation> OrientationProperty =
        AvaloniaProperty.Register<FireDropMarker, Orientation>(
            nameof(Orientation), Orientation.Horizontal);

    static FireDropMarker() => AffectsRender<FireDropMarker>(OrientationProperty);

    public FireDropMarker() => IsHitTestVisible = false;

    public Orientation Orientation
    {
        get => GetValue(OrientationProperty);
        set => SetValue(OrientationProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        var bounds = new Rect(Bounds.Size);
        if (bounds.Width <= 0 || bounds.Height <= 0) return;

        var accent = FireUnitState.ColorFrom(this, "FireAccent");
        var thickness = Orientation == Orientation.Horizontal ? bounds.Width : bounds.Height;
        var radius = Math.Max(1.0, thickness / 2.0);

        // The glow first — a wider, much fainter pass under the bar.
        context.DrawRectangle(
            new SolidColorBrush(FireUnitState.At(accent, 0.28)), null,
            bounds.Inflate(FireSlotHitTest.MarkerThickness / 2.0),
            radius + FireSlotHitTest.MarkerThickness / 2.0,
            radius + FireSlotHitTest.MarkerThickness / 2.0);

        context.DrawRectangle(new SolidColorBrush(accent), null, bounds, radius, radius);
    }
}
