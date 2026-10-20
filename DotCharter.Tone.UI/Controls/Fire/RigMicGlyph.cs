using Avalonia;
using Avalonia.Media;

namespace DotCharter.Tone.UI.Controls.Fire;

/// <summary>A microphone drawn from the side (<see cref="MicArt" />), fitted to its
/// bounds — the mic picker's card art.</summary>
public sealed class RigMicGlyph : FireDrawnControl
{
    public static readonly StyledProperty<int> MicTypeProperty =
        AvaloniaProperty.Register<RigMicGlyph, int>(nameof(MicType));

    static RigMicGlyph() => AffectsRender<RigMicGlyph>(MicTypeProperty);

    public int MicType { get => GetValue(MicTypeProperty); set => SetValue(MicTypeProperty, value); }

    protected override Size MeasureOverride(Size availableSize) => new(96, 30);

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var p = new MicArt.Palette(
            Paint.Color(this, "FireHardwareShadow"), Paint.Color(this, "FireHardwareEdge"),
            Paint.Color(this, "FireHardwareHighlight"), Paint.Color(this, "FireHardwareSwitchRing"),
            Paint.Color(this, "FireHardwareGrille"), Paint.Color(this, "FireWarning"),
            Paint.Color(this, "FireTextSecondary"));
        // every mic is drawn ~4.6 heights long: fit the height to the width
        var h = Math.Min(Bounds.Height * 0.9, Bounds.Width / 4.8);
        var length = h * 4.6;
        MicArt.DrawMicSide(context, MicArt.MicFor(MicType),
            new Point((Bounds.Width - length) / 2, Bounds.Height / 2), h, p, lit: false);
    }
}
