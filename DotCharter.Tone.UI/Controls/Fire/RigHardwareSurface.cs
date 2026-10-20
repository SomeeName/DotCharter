using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using DotCharter.Tone.UI.Controls.Dsp;

namespace DotCharter.Tone.UI.Controls.Fire;

/// <summary>
/// Static vector hardware decoration. Place behind content, or reserve a separate
/// face region with DetailOnly. No hit targets, DSP controls, clocks or bitmap assets.
/// </summary>
public sealed class RigHardwareSurface : Control
{
    // One decode per application, shared across chain tiles, gear and editors.
    private static readonly Lazy<Bitmap> AmpArt = new(() => LoadArt("amp-chassis.png"));
    private static readonly Lazy<Bitmap> CabArt = new(() => LoadArt("speaker-cabinet.png"));
    private static Bitmap LoadArt(string name)
    {
        using var stream = AssetLoader.Open(new Uri($"avares://DotCharter.Tone.UI/Assets/Rig/{name}"));
        return new Bitmap(stream);
    }

    public static readonly StyledProperty<PedalSkin> SkinProperty =
        AvaloniaProperty.Register<RigHardwareSurface, PedalSkin>(nameof(Skin));
    public static readonly StyledProperty<bool> IsThumbnailProperty =
        AvaloniaProperty.Register<RigHardwareSurface, bool>(nameof(IsThumbnail));
    public bool IsThumbnail { get => GetValue(IsThumbnailProperty); set => SetValue(IsThumbnailProperty, value); }

    public static readonly StyledProperty<bool> DetailOnlyProperty =
        AvaloniaProperty.Register<RigHardwareSurface, bool>(nameof(DetailOnly));
    public static readonly StyledProperty<IBrush?> ChassisBrushProperty =
        AvaloniaProperty.Register<RigHardwareSurface, IBrush?>(nameof(ChassisBrush));
    public static readonly StyledProperty<IBrush?> EdgeBrushProperty =
        AvaloniaProperty.Register<RigHardwareSurface, IBrush?>(nameof(EdgeBrush));
    public static readonly StyledProperty<IBrush?> HighlightBrushProperty =
        AvaloniaProperty.Register<RigHardwareSurface, IBrush?>(nameof(HighlightBrush));
    public static readonly StyledProperty<IBrush?> ShadowBrushProperty =
        AvaloniaProperty.Register<RigHardwareSurface, IBrush?>(nameof(ShadowBrush));
    public static readonly StyledProperty<IBrush?> GrilleBrushProperty =
        AvaloniaProperty.Register<RigHardwareSurface, IBrush?>(nameof(GrilleBrush));

    public PedalSkin Skin { get => GetValue(SkinProperty); set => SetValue(SkinProperty, value); }
    public bool DetailOnly { get => GetValue(DetailOnlyProperty); set => SetValue(DetailOnlyProperty, value); }
    public IBrush? ChassisBrush { get => GetValue(ChassisBrushProperty); set => SetValue(ChassisBrushProperty, value); }
    public IBrush? EdgeBrush { get => GetValue(EdgeBrushProperty); set => SetValue(EdgeBrushProperty, value); }
    public IBrush? HighlightBrush { get => GetValue(HighlightBrushProperty); set => SetValue(HighlightBrushProperty, value); }
    public IBrush? ShadowBrush { get => GetValue(ShadowBrushProperty); set => SetValue(ShadowBrushProperty, value); }
    public IBrush? GrilleBrush { get => GetValue(GrilleBrushProperty); set => SetValue(GrilleBrushProperty, value); }

    static RigHardwareSurface()
    {
        IsHitTestVisibleProperty.OverrideDefaultValue<RigHardwareSurface>(false);
        AffectsRender<RigHardwareSurface>(SkinProperty, DetailOnlyProperty, IsThumbnailProperty, ChassisBrushProperty,
            EdgeBrushProperty, HighlightBrushProperty, ShadowBrushProperty, GrilleBrushProperty,
            FireUnitState.CategoryProperty, FireUnitState.IsBypassedProperty, IsEffectivelyEnabledProperty);
    }

    public RigHardwareSurface()
    {
        Bind(ChassisBrushProperty, new DynamicResourceExtension("FireHardwareChassis"));
        Bind(EdgeBrushProperty, new DynamicResourceExtension("FireHardwareEdge"));
        Bind(HighlightBrushProperty, new DynamicResourceExtension("FireHardwareHighlight"));
        Bind(ShadowBrushProperty, new DynamicResourceExtension("FireHardwareShadow"));
        Bind(GrilleBrushProperty, new DynamicResourceExtension("FireHardwareGrille"));
    }

    private static void DrawAmpEnclosure(DrawingContext context, Rect dest)
    {
        // Nine-slice the enclosure so corner guards remain round on wide faces.
        double[] sx = [0, 110, 1490, 1600], sy = [140, 235, 565, 660];
        double[] dx = [dest.X, dest.X + 44, dest.Right - 44, dest.Right];
        double[] dy = [dest.Y, dest.Y + 36, dest.Bottom - 36, dest.Bottom];
        for (var y = 0; y < 3; y++)
        {
            for (var x = 0; x < 3; x++)
            {
                context.DrawImage(AmpArt.Value,
                    new Rect(sx[x], sy[y], sx[x + 1] - sx[x], sy[y + 1] - sy[y]),
                    new Rect(dx[x], dy[y], dx[x + 1] - dx[x], dy[y + 1] - dy[y]));
            }
        }
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (Bounds.Width < 20 || Bounds.Height < 20) return;
        var bounds = new Rect(Bounds.Size).Deflate(2);
        var color = FireUnitState.AccentOf(this);
        var accent = new SolidColorBrush(color);
        var edge = new Pen(EdgeBrush, 1);
        if (Skin is PedalSkin.AmpHead or PedalSkin.NeuralAmp)
        {
            var dest = bounds;
            if (IsThumbnail)
            {
                var height = Math.Min(bounds.Height, bounds.Width / 2.1);
                dest = new Rect(bounds.X, bounds.Center.Y - height / 2, bounds.Width, height);
            }
            if (IsThumbnail) context.DrawImage(AmpArt.Value, new Rect(0, 140, 1600, 520), dest);
            else DrawAmpEnclosure(context, dest);
            if (IsThumbnail)
            {
                for (var i = 0; i < 6; i++)
                {
                    var center = new Point(dest.X + dest.Width * (.26 + i * .105), dest.Y + dest.Height * .72);
                    context.DrawEllipse(ShadowBrush, new Pen(accent, .7), center, 2, 2);
                }
            }

            return;
        }
        if (Skin == PedalSkin.Cabinet)
        {
            var side = Math.Min(bounds.Width, bounds.Height);
            var dest = new Rect((Bounds.Width - side) / 2, (Bounds.Height - side) / 2, side, side);
            context.DrawImage(CabArt.Value, new Rect(CabArt.Value.Size), dest);
            return;
        }
        var face = bounds;
        if (IsThumbnail)
        {
            var width = Math.Min(bounds.Width, bounds.Height * .68);
            face = new Rect(bounds.Center.X - width / 2, bounds.Y, width, bounds.Height);
        }
        var enamel = new SolidColorBrush(Color.FromRgb((byte)(color.R * .55), (byte)(color.G * .55), (byte)(color.B * .55)));
        context.DrawRectangle(ShadowBrush, null, face.Translate(new Vector(2, 3)), 7, 7);
        context.DrawRectangle(enamel, new Pen(accent, 1.5), face, 7, 7);
        context.DrawLine(new Pen(HighlightBrush, 1), new Point(face.X + 6, face.Y + 3), new Point(face.Right - 6, face.Y + 3));
        if (!IsThumbnail) return;
        var radius = Math.Max(3, face.Width * .13);
        foreach (var point in new[] { new Point(face.X+face.Width*.27, face.Y+face.Height*.24),
                     new Point(face.X+face.Width*.73, face.Y+face.Height*.24),
                     new Point(face.Center.X, face.Y+face.Height*.52) })
        {
            context.DrawEllipse(ShadowBrush, edge, point, radius, radius);
            context.DrawLine(new Pen(HighlightBrush, 1), point, new Point(point.X, point.Y - radius + 1));
        }
        context.DrawEllipse(HighlightBrush, edge, new Point(face.Center.X, face.Y + face.Height * .80), radius * .65, radius * .65);
    }
}
