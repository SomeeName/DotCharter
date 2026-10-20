using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using DotCharter.UI.Controls.Themes;

namespace DotCharter.UI.Controls;

/// <summary>
///     Lightweight Avalonia overlay that draws the marquee-selection rubber
///     band published by its paired <see cref="ChartRenderer" />
///     (<see cref="ChartRenderer.MarqueeChanged" />). Kept OUT of the GL scene
///     on purpose: a pointer-move during a marquee drag only invalidates this
///     tiny transparent control, never the chart itself. Place it in the same
///     panel cell as the renderer (`Renderer="{Binding #SomeRenderer}"`), it is
///     hit-test invisible so all input still lands on the renderer.
/// </summary>
public class MarqueeSelectionOverlay : Control
{
    public static readonly StyledProperty<ChartRenderer?> RendererProperty =
        AvaloniaProperty.Register<MarqueeSelectionOverlay, ChartRenderer?>(nameof(Renderer));

    // Same accent family as the minimap viewport box; transient chrome, so the
    // static snapshot of the theme colour is fine (matches MinimapOverviewControl).
    private static readonly IBrush FillBrush = new SolidColorBrush(Color.FromArgb(40,
        ChartTheme.SkyBlueColor.R, ChartTheme.SkyBlueColor.G, ChartTheme.SkyBlueColor.B));

    private static readonly IPen BorderPen = new Pen(new SolidColorBrush(Color.FromArgb(220,
        ChartTheme.SkyBlueColor.R, ChartTheme.SkyBlueColor.G, ChartTheme.SkyBlueColor.B)));

    private ChartRenderer? _subscribed;
    private Rect? _lastRect;

    public MarqueeSelectionOverlay()
    {
        IsHitTestVisible = false;
        ClipToBounds = true;
    }

    public ChartRenderer? Renderer
    {
        get => GetValue(RendererProperty);
        set => SetValue(RendererProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == RendererProperty && VisualRoot != null)
            Resubscribe(Renderer);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Resubscribe(Renderer);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        // The renderer can outlive this overlay across float/redock cycles —
        // drop the subscription so detached overlays are never rooted.
        Resubscribe(null);
        base.OnDetachedFromVisualTree(e);
    }

    private void Resubscribe(ChartRenderer? target)
    {
        if (ReferenceEquals(_subscribed, target)) return;
        _subscribed?.MarqueeChanged -= OnMarqueeChanged;
        _subscribed = target;
        _subscribed?.MarqueeChanged += OnMarqueeChanged;
        OnMarqueeChanged(this, EventArgs.Empty);
    }

    private void OnMarqueeChanged(object? sender, EventArgs e)
    {
        var rect = _subscribed?.MarqueeRect;
        if (rect == _lastRect) return; // only invalidate on real change
        _lastRect = rect;
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        if (_lastRect is not { } r) return;
        if (r.Width < 1 && r.Height < 1) return;
        context.DrawRectangle(FillBrush, BorderPen, r);
    }
}
