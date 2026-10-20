using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using DotCharter.Services.Export.Sheet;
using SkiaSharp;

// SheetMusicView's paint layers: the chunk canvas, its three overlays (cursor, selection,
// hover) on their one SheetOverlayLayer base, and the Skia draw op. Each is its own Control
// so a cursor, selection or hover change repaints only its overlay and never re-blits the
// cached AlphaSkia chunks.
namespace DotCharter.UI.Controls.Sheet;

/// <summary>One hovered cell in source space: the beat column center X, its
/// vertical extent (a single string cell in tab mode, else the full staff
/// height) and the highlight band width.</summary>
internal readonly record struct HoverBand(double CenterX, double Top, double Height, double BandWidth);

/// <summary>One selection-highlight band in source space: beat column center
/// X and its vertical extent (full staff in the strip; the containing
/// system's Y span in page mode).</summary>
internal readonly record struct HighlightBand(double CenterX, double Top, double Height);

/// <summary>
/// Inner control sized to the rendered sheet that custom-paints the
/// AlphaSkia chunks + cursor onto Avalonia's active SKCanvas. Holds the
/// only strong reference to the SKImage chunks so ownership is clear.
/// Auto-fits the rendered content to the parent strip height so nothing
/// gets clipped at the top regardless of how tall the produced score is.
/// </summary>
internal sealed class SheetCanvas : Control
{
    private SheetMusicRenderer.RenderResult? _result;

    public double ContentWidth { get; private set; }
    public double RenderScale { get; private set; } = 1.0;

    /// <summary>User zoom: the canvas targets viewport×zoom instead of
    /// fitting the viewport exactly, and the ScrollViewer takes over
    /// vertically. 1.0 keeps the historical fit-to-strip behavior.</summary>
    public double ZoomFactor { get; set; } = 1.0;

    /// <summary>Strip height in DIPs — needed because a vertically
    /// scrollable parent measures with infinite height.</summary>
    public double ViewportHeightHint { get; set; }

    private bool _pageMode;
    private double _pageScale;

    /// <summary>Page layout: display at a FIXED source→DIP scale (content
    /// fills the viewport width and scrolls vertically) instead of fitting
    /// the score to the strip height.</summary>
    public void SetPageMode(bool pageMode, double pageScale)
    {
        _pageMode = pageMode;
        _pageScale = pageScale;
        InvalidateMeasure();
        InvalidateVisual();
    }

    public void SetResult(SheetMusicRenderer.RenderResult? result)
    {
        var old = _result;
        _result = result;
        old?.Release();
        InvalidateMeasure();
        InvalidateVisual();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        if (_pageMode && _result is { Height: > 0 })
        {
            var s = _pageScale > 0 ? _pageScale : 1.0;
            RenderScale = s;
            ContentWidth = _result.Width * s;
            return new Size(ContentWidth, _result.Height * s);
        }

        var parentHeight = availableSize.Height;
        if (double.IsInfinity(parentHeight) || parentHeight <= 0)
            parentHeight = ViewportHeightHint > 0 ? ViewportHeightHint : _result?.Height ?? 0;

        var targetHeight = parentHeight * Math.Max(ZoomFactor, 0.1);

        if (_result == null || _result.Height <= 0)
            return new Size(0, targetHeight);

        var scale = targetHeight / _result.Height;
        if (scale <= 0 || double.IsNaN(scale)) scale = 1.0;
        RenderScale = scale;
        ContentWidth = _result.Width * scale;
        return new Size(ContentWidth, targetHeight);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (_result == null || _result.Chunks.Count == 0) return;

        var height = Bounds.Height;
        var width = Bounds.Width;
        if (height <= 0 || width <= 0) return;

        context.Custom(new SheetDrawOp(
            new Rect(0, 0, width, height),
            _result.Retain(),
            RenderScale));
    }
}

/// <summary>
/// The base of the three transparent overlays (cursor, selection, hover): each is
/// sized identically to the <see cref="SheetCanvas"/> it covers and maps source pixels
/// to DIPs through <see cref="Scale"/>, so the page-mode state, the setters the view
/// calls and the one measure live here. A derived overlay keeps only its own state and
/// its <c>Render</c>.
/// </summary>
internal abstract class SheetOverlayLayer : Control
{
    private double _sourceHeight;
    private bool _pageMode;
    private double _pageScale;

    public double SourceWidth { get; private set; }

    /// <summary>Source px → DIP, as of the last measure that had metrics (a measure
    /// without them keeps the previous value).</summary>
    internal double Scale { get; private set; } = 1.0;

    /// <summary>Mirrors <see cref="SheetCanvas.ZoomFactor"/> so the
    /// overlay sizes identically to the canvas it covers.</summary>
    public double ZoomFactor { get; set; } = 1.0;

    public double ViewportHeightHint { get; set; }

    public void SetPageMode(bool pageMode, double pageScale)
    {
        _pageMode = pageMode;
        _pageScale = pageScale;
        InvalidateMeasure();
        InvalidateVisual();
    }

    public void SetMetrics(double sourceWidth, double sourceHeight)
    {
        SourceWidth = sourceWidth;
        _sourceHeight = sourceHeight;
        InvalidateMeasure();
        InvalidateVisual();
    }

    protected sealed override Size MeasureOverride(Size availableSize)
    {
        if (_pageMode && SourceWidth > 0 && _sourceHeight > 0)
        {
            Scale = _pageScale > 0 ? _pageScale : 1.0;
            return new Size(SourceWidth * Scale, _sourceHeight * Scale);
        }

        var parentHeight = availableSize.Height;
        if (double.IsInfinity(parentHeight) || parentHeight <= 0)
            parentHeight = ViewportHeightHint > 0 ? ViewportHeightHint : _sourceHeight;
        var targetHeight = parentHeight * Math.Max(ZoomFactor, 0.1);
        if (SourceWidth <= 0 || _sourceHeight <= 0)
            return new Size(0, targetHeight);
        var scale = targetHeight / _sourceHeight;
        if (scale <= 0 || double.IsNaN(scale)) scale = 1.0;
        Scale = scale;
        return new Size(SourceWidth * scale, targetHeight);
    }
}

/// <summary>
/// Transparent overlay sized identically to the sheet canvas. Owns the
/// playback cursor so cursor invalidation never re-blits the (cached)
/// AlphaSkia chunks beneath it. The overlay is parented next to the
/// canvas inside a Grid so it shares the same scroll offset.
/// </summary>
internal sealed class CursorOverlay : SheetOverlayLayer
{
    private static readonly IImmutableSolidColorBrush CursorBrush =
        new ImmutableSolidColorBrush(Color.FromArgb(200, 220, 50, 50));

    private double _cursorSourceX;
    private double _cursorTop;
    private double _cursorHeight;

    /// <summary>Cursor segment in source space: X and the Y band (strip:
    /// top 0, full height; page: the active system's band).</summary>
    public void SetCursor(double srcX, double srcTop, double srcHeight)
    {
        if (Math.Abs(srcX - _cursorSourceX) < 0.25 &&
            Math.Abs(srcTop - _cursorTop) < 0.25 &&
            Math.Abs(srcHeight - _cursorHeight) < 0.25)
        {
            return;
        }

        _cursorSourceX = srcX;
        _cursorTop = srcTop;
        _cursorHeight = srcHeight;
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var h = Bounds.Height;
        if (h <= 0 || SourceWidth <= 0) return;
        var x = _cursorSourceX * Scale;
        var y = _cursorTop * Scale;
        var segHeight = _cursorHeight > 0 ? _cursorHeight * Scale : h;
        context.FillRectangle(CursorBrush, new Rect(x, y, 2, segHeight));
    }
}

/// <summary>
/// Transparent overlay, sized identically to the sheet canvas, that paints a
/// translucent band behind every selected note/chord. Kept separate from the
/// canvas (like <see cref="CursorOverlay"/>) so a selection change repaints
/// only this thin overlay and never re-blits the cached AlphaSkia chunks.
/// The highlight colour is pulled from the app's accent token so it tracks
/// the theme instead of being hard-coded.
/// </summary>
internal sealed class SelectionOverlay : SheetOverlayLayer
{
    private static readonly IImmutableSolidColorBrush FallbackBrush =
        new ImmutableSolidColorBrush(Color.FromArgb(70, 139, 92, 246)); // AccentColorValue @ ~27% alpha

    private HighlightBand[] _bands = [];
    private double _bandWidthSrc;
    private IImmutableSolidColorBrush? _brush;

    public void SetHighlights(IReadOnlyList<HighlightBand> bands, double bandWidthSrc)
    {
        // Skip the repaint when nothing actually moved — keeps InvalidateVisual
        // honest (rule: only invalidate when rendered state changed).
        if (_bands.Length == bands.Count && Math.Abs(_bandWidthSrc - bandWidthSrc) < 0.01)
        {
            var identical = true;
            for (var i = 0; i < _bands.Length; i++)
            {
                if (Math.Abs(_bands[i].CenterX - bands[i].CenterX) >= 0.25 ||
                    Math.Abs(_bands[i].Top - bands[i].Top) >= 0.25 ||
                    Math.Abs(_bands[i].Height - bands[i].Height) >= 0.25)
                {
                    identical = false;
                    break;
                }
            }

            if (identical) return;
        }

        _bands = bands.Count == 0 ? [] : bands.ToArray();
        _bandWidthSrc = bandWidthSrc;
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var h = Bounds.Height;
        if (h <= 0 || SourceWidth <= 0 || _bands.Length == 0 || _bandWidthSrc <= 0) return;

        var brush = ResolveBrush();
        var halfWidth = _bandWidthSrc * Scale * 0.5;
        foreach (var b in _bands)
        {
            var cx = b.CenterX * Scale;
            var y = b.Top * Scale;
            var bandHeight = b.Height > 0 ? b.Height * Scale : h;
            context.FillRectangle(brush, new Rect(cx - halfWidth, y, halfWidth * 2, bandHeight));
        }
    }

    private IImmutableSolidColorBrush ResolveBrush()
    {
        if (_brush != null) return _brush;
        // Theme token → translucent highlight. Resolved once (selection edits
        // are rare and the accent token doesn't change mid-session in practice).
        if (this.TryFindResource("AccentColorValue", out var res) && res is Color accent)
            _brush = new ImmutableSolidColorBrush(Color.FromArgb(70, accent.R, accent.G, accent.B));
        else
            _brush = FallbackBrush;
        return _brush;
    }
}

/// <summary>
/// Transparent overlay that outlines the note head / tab digit under the
/// cursor — the sheet's hover affordance. Deliberately view-LOCAL (unlike
/// selection, which is the shared <see cref="ISelectionService"/> model):
/// hover changes at pointer rates and is never fed to the other views. Kept
/// separate from the canvas so a hover move never re-blits the cached chunks.
/// The colour comes from the accent token at a subtler alpha than selection.
/// </summary>
internal sealed class HoverOverlay : SheetOverlayLayer
{
    private static readonly IImmutableSolidColorBrush FallbackFill =
        new ImmutableSolidColorBrush(Color.FromArgb(40, 139, 92, 246));

    private HoverBand? _band;
    private IImmutableSolidColorBrush? _fill;
    private ImmutablePen? _pen;

    public void SetHover(HoverBand? band)
    {
        // Skip the repaint when nothing meaningfully moved (rule: only
        // invalidate when the rendered state changed).
        if (_band is { } a && band is { } b)
        {
            if (Math.Abs(a.CenterX - b.CenterX) < 0.25 &&
                Math.Abs(a.Top - b.Top) < 0.25 &&
                Math.Abs(a.Height - b.Height) < 0.25 &&
                Math.Abs(a.BandWidth - b.BandWidth) < 0.25)
            {
                return;
            }
        }
        else if (_band is null && band is null)
        {
            return;
        }

        _band = band;
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (SourceWidth <= 0 || _band is not { } band) return;

        var (fill, pen) = ResolveBrushes();
        var halfWidth = band.BandWidth * Scale * 0.5;
        var x = band.CenterX * Scale - halfWidth;
        var y = band.Top * Scale;
        var w = halfWidth * 2;
        var h = band.Height * Scale;
        if (w <= 0 || h <= 0) return;
        context.DrawRectangle(fill, pen, new Rect(x, y, w, h), 3, 3);
    }

    private (IImmutableSolidColorBrush Fill, ImmutablePen Pen) ResolveBrushes()
    {
        if (_fill != null && _pen != null) return (_fill, _pen);
        if (this.TryFindResource("AccentColorValue", out var res) && res is Color accent)
        {
            _fill = new ImmutableSolidColorBrush(Color.FromArgb(40, accent.R, accent.G, accent.B));
            _pen = new ImmutablePen(
                new ImmutableSolidColorBrush(Color.FromArgb(150, accent.R, accent.G, accent.B)), 1);
        }
        else
        {
            _fill = FallbackFill;
            _pen = new ImmutablePen(new ImmutableSolidColorBrush(Color.FromArgb(150, 139, 92, 246)), 1);
        }

        return (_fill, _pen);
    }
}

internal sealed class SheetDrawOp : ICustomDrawOperation
{
    private readonly SheetMusicRenderer.RenderResult _result;
    private readonly double _scale;

    public SheetDrawOp(Rect bounds, SheetMusicRenderer.RenderResult result, double scale)
    {
        Bounds = bounds;
        _result = result;
        _scale = scale;
    }

    public Rect Bounds { get; }

    public bool Equals(ICustomDrawOperation? other) => false;

    public bool HitTest(Point p) => false;

    public void Dispose() => _result.Release();

    public void Render(ImmediateDrawingContext context)
    {
        var feature = context.TryGetFeature<ISkiaSharpApiLeaseFeature>();
        if (feature == null) return;

        using var lease = feature.Lease();
        var canvas = lease.SkCanvas;
        var w = (float)Bounds.Width;
        var h = (float)Bounds.Height;

        using var bg = new SKPaint();
        bg.Color = new SKColor(28, 28, 32);
        bg.IsAntialias = false;
        canvas.DrawRect(new SKRect(0, 0, w, h), bg);

        var sampling = new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None);
        var save = canvas.Save();
        canvas.Scale((float)_scale);
        var chunks = _result.Chunks;
        for (var i = 0; i < chunks.Count; i++)
        {
            var c = chunks[i];
            var dst = new SKRect(
                (float)c.X,
                (float)c.Y,
                (float)(c.X + c.Width),
                (float)(c.Y + c.Height));
            canvas.DrawImage(c.Image, dst, sampling);
        }
        canvas.RestoreToCount(save);
    }
}
