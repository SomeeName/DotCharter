using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using DotCharter.Services.Infrastructure;
using DotCharter.Tone.UI.Controls;
using DotCharter.UI.ViewModels.Panels;
using SkiaSharp;

namespace DotCharter.UI.Controls.Hero;

/// <summary>
///     The Home hero's image, ported 1:1 from the owner's "DotCharter — Start screen" mockup
///     ("only the image part" of its second screen). Back to front: the aurora (the
///     cover's palette as drifting blurred light), the LED wall (the cover as halftone dots, a scan
///     band sweeping it every 7 s and a spotlight under the pointer), three swaying spotlight beams,
///     the two shading gradients the text sits on, then the record and the cover card, which tilts
///     toward the pointer. The mockup's 100 BPM beat pulse (the wall flashing; the beams, the aurora
///     and the card's glow breathing) is left out on purpose: the owner found it "hella annoying".
///     The dots are finer than the mockup's (8 px, not 12), which the owner asked for too.
///     <para>
///         It asks for NO size (Home scrolls vertically; an image that measured itself pushed the
///         header off screen) and arranges its own two children. One 20 fps timer drives it (the motion is slow: 7–14 s sweeps) and
///         STOPS whenever the stage is detached or not effectively visible, like every animated
///         backdrop here. The pixel work is <see cref="HeroRaster" />; the colours <see cref="HeroColor" />.
///     </para>
/// </summary>
public sealed class HeroStage : Control
{
    public static readonly StyledProperty<string?> CoverPathProperty =
        AvaloniaProperty.Register<HeroStage, string?>(nameof(CoverPath));

    /// <summary>
    ///     Off (the default, Settings ▸ Graphics): the same image, drawn once at <see cref="StillMs" />
    ///     without the band, the spotlight or the tilt, and no clock at all.
    /// </summary>
    public static readonly StyledProperty<bool> IsAnimatedProperty =
        AvaloniaProperty.Register<HeroStage, bool>(nameof(IsAnimated));

    /// <summary>The moment the still image is taken at: the beams part, the aurora is settled.</summary>
    private const double StillMs = 5200;

    private static readonly IArtworkSource Artwork = new ArtworkSource();
    private static readonly (double Left, double Duration, double Delay)[] Beams =
        [(0.08, 12, 0), (0.38, 9, -4), (0.64, 14, -7)];

    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly DispatcherTimer _timer;
    private readonly Border _vinylShadow;
    private readonly VinylDisc _vinyl;
    private readonly Border _card;
    private readonly Image _cardImage;
    private readonly Rotate3DTransform _tilt = new() { Depth = 900 };
    private readonly TranslateTransform _vinylSlide = new();

    private bool _attached;
    private bool _running;
    private double _lastMs;
    private Point? _pointer;
    private double _tiltX, _tiltY, _slide = -0.24;

    private HeroPalette _palette = HeroPalette.Default;
    private byte[]? _coverRgba;
    private int _coverW, _coverH;
    private int _loadGeneration;

    private readonly WriteableBitmap _aurora = new(new PixelSize(HeroRaster.AuroraWidth, HeroRaster.AuroraHeight),
        new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
    private readonly byte[] _auroraPixels = new byte[HeroRaster.AuroraWidth * HeroRaster.AuroraHeight * 4];
    private Bitmap? _ledBase, _ledLit;
    private PixelSize _ledSize;
    private Bitmap?[] _beamBitmaps = new Bitmap?[3];

    public HeroStage()
    {
        ClipToBounds = true;
        _ = new EffectiveVisibilityWatcher(this, _ => SyncRunning());
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(50) };
        _timer.Tick += OnTick;

        _vinyl = new VinylDisc();
        _vinylShadow = new Border
        {
            Child = _vinyl,
            RenderTransform = _vinylSlide,
            IsVisible = false
        };
        _cardImage = new Image { Stretch = Stretch.UniformToFill };
        _card = new Border
        {
            CornerRadius = new CornerRadius(4),
            RenderTransform = _tilt,
            IsVisible = false,
            Child = new Border
            {
                CornerRadius = new CornerRadius(4),
                ClipToBounds = true,
                Child = new Panel
                {
                    Children =
                    {
                        _cardImage,
                        // The mockup's glare: a 115° band on a 250 %-wide background, of which the
                        // card shows the first 40 %, so only its edge crosses the lower right.
                        new Border
                        {
                            Background = new LinearGradientBrush
                            {
                                StartPoint = new RelativePoint(0.0323, -0.0685, RelativeUnit.Relative),
                                EndPoint = new RelativePoint(2.4677, 1.0685, RelativeUnit.Relative),
                                GradientStops =
                                {
                                    new GradientStop(Colors.Transparent, 0.35),
                                    new GradientStop(Color.FromArgb(41, 255, 255, 255), 0.48),
                                    new GradientStop(Colors.Transparent, 0.60)
                                }
                            }
                        }
                    }
                }
            }
        };
        // A Control, not a Panel: Panel seals Render, and the stage draws under its children.
        foreach (var child in new Control[] { _vinylShadow, _card })
        {
            LogicalChildren.Add(child);
            VisualChildren.Add(child);
        }
    }

    /// <summary>The cover on disk, or empty for none (the aurora and the beams in the accent, no wall).</summary>
    public string? CoverPath
    {
        get => GetValue(CoverPathProperty);
        set => SetValue(CoverPathProperty, value);
    }

    public bool IsAnimated
    {
        get => GetValue(IsAnimatedProperty);
        set => SetValue(IsAnimatedProperty, value);
    }

    internal bool IsStaging => _timer.IsEnabled;

    internal HeroPalette Palette => _palette;

    internal bool HasLedWall => _ledBase is not null;

    internal bool HasCover => _coverRgba is not null;

    /// <summary>The cover decode in flight, for a test to wait on.</summary>
    internal Task? PendingLoad { get; private set; }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == CoverPathProperty) LoadCover(change.GetNewValue<string?>());
        if (change.Property == IsAnimatedProperty)
        {
            _auroraAt = -1;
            SyncRunning();
            InvalidateVisual();
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _attached = true;
        if (Parent is InputElement host)
        {
            host.PointerMoved += OnHostPointerMoved;
            host.PointerExited += OnHostPointerExited;
        }

        SyncRunning();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (Parent is InputElement host)
        {
            host.PointerMoved -= OnHostPointerMoved;
            host.PointerExited -= OnHostPointerExited;
        }

        _attached = false;
        SyncRunning();
        base.OnDetachedFromVisualTree(e);
    }

    private void OnHostPointerMoved(object? sender, PointerEventArgs e) => _pointer = e.GetPosition(this);

    private void OnHostPointerExited(object? sender, PointerEventArgs e) => _pointer = null;

    private void SyncRunning()
    {
        var live = _attached && IsEffectivelyVisible && IsAnimated;
        if (live == _running) return;
        _running = live;
        if (live)
        {
            _lastMs = _clock.Elapsed.TotalMilliseconds;
            _timer.Start();
            InvalidateVisual();
        }
        else
        {
            _timer.Stop();
        }
    }

    // ── cover ────────────────────────────────────────────────────────────

    private void LoadCover(string? path)
    {
        var generation = ++_loadGeneration;
        if (string.IsNullOrEmpty(path))
        {
            ApplyCover(null, null, 0, 0, HeroPalette.Default);
            return;
        }

        PendingLoad = Task.Run(() =>
        {
            var (rgba, w, h) = DecodeRgba(path);
            var palette = rgba is null ? HeroPalette.Default : HeroColor.Extract(rgba, w, h);
            Dispatcher.UIThread.Post(() =>
            {
                if (generation != _loadGeneration) return;
                // The card's bitmap on the UI thread: it is one small decode, and a platform
                // bitmap made on a pool thread is not guaranteed on every backend.
                ApplyCover(SongCoverImage.Load(Artwork, ArtworkKey.File(path)), rgba, w, h, palette);
            });
        });
    }

    /// <summary>The cover as straight RGBA, capped at 512 px across (a wall cell is ~12 px).</summary>
    internal static (byte[]? Rgba, int W, int H) DecodeRgba(string path)
    {
        try
        {
            using var stream = Artwork.Open(ArtworkKey.File(path));
            if (stream is null) return (null, 0, 0);
            // The header is checked before Skia is asked to allocate: the buffer
            // below is the codec's full declared size, whatever the file weighs.
            if (!ImageHeaderProbe.IsDecodable(stream)) return (null, 0, 0);
            using var codec = SKCodec.Create(stream);
            if (codec is null) return (null, 0, 0);
            var info = new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
            using var bitmap = new SKBitmap(info);
            var result = codec.GetPixels(info, bitmap.GetPixels());
            if (result is not SKCodecResult.Success and not SKCodecResult.IncompleteInput) return (null, 0, 0);
            var bytes = bitmap.Bytes;
            if (info.Width <= 512) return (bytes, info.Width, info.Height);
            var h = Math.Max(1, info.Height * 512 / info.Width);
            return (HeroRaster.Resample(bytes, info.Width, info.Height, 0, 0, info.Width, info.Height, 512, h), 512, h);
        }
        catch
        {
            return (null, 0, 0);
        }
    }

    private void ApplyCover(Bitmap? bitmap, byte[]? rgba, int w, int h, HeroPalette palette)
    {
        _coverRgba = rgba;
        _coverW = w;
        _coverH = h;
        _palette = palette;
        _cardImage.Source = bitmap;
        _vinyl.Label = bitmap;
        _card.IsVisible = _vinylShadow.IsVisible = bitmap is not null;
        _ledBase = _ledLit = null;
        _ledSize = default;
        _beamBitmaps = new Bitmap?[3];
        _shadowSide = 0;
        _auroraAt = -1;
        InvalidateArrange();
        InvalidateVisual();
    }

    // ── layout ───────────────────────────────────────────────────────────

    protected override Size MeasureOverride(Size availableSize)
    {
        var side = CoverSide(availableSize.Width);
        _vinylShadow.Measure(new Size(side * 0.92, side * 0.92));
        _card.Measure(new Size(side, side));
        return default;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var side = CoverSide(finalSize.Width);
        var left = finalSize.Width - 32 - side;
        _vinylShadow.CornerRadius = new CornerRadius(side * 0.46);
        _vinylShadow.Arrange(new Rect(left + side * 0.04, 32 + side * 0.04, side * 0.92, side * 0.92));
        _card.Arrange(new Rect(left, 32, side, side));
        _vinylSlide.X = _slide * side * 0.92;
        return finalSize;
    }

    /// <summary>The mockup's <c>clamp(160px, 21vw, 300px)</c>, with the hero standing in for the viewport.</summary>
    private static double CoverSide(double heroWidth) =>
        double.IsFinite(heroWidth) ? Math.Clamp(heroWidth * 0.27, 160, 300) : 300;

    // ── clock ────────────────────────────────────────────────────────────

    private void OnTick(object? sender, EventArgs e)
    {
        if (!_running) return;
        var now = _clock.Elapsed.TotalMilliseconds;
        var dt = Math.Min((now - _lastMs) / 16.667, 3);
        _lastMs = now;

        // The card tilts toward the pointer (CSS transition 0.25 s ease-out); the record slides
        // further out while the pointer is over the hero (0.6 s).
        double targetX = 0, targetY = 0;
        if (_pointer is { } p && Bounds.Width > 0)
        {
            targetY = (p.X / Bounds.Width - 0.5) * 16;
            targetX = -(p.Y / Bounds.Height - 0.5) * 12;
        }

        var tiltStep = 1 - Math.Pow(1 - 0.18, dt);
        _tiltX += (targetX - _tiltX) * tiltStep;
        _tiltY += (targetY - _tiltY) * tiltStep;
        _tilt.AngleX = _tiltX;
        _tilt.AngleY = _tiltY;

        var hover = Parent is InputElement { IsPointerOver: true };
        _slide += ((hover ? -0.42 : -0.24) - _slide) * (1 - Math.Pow(1 - 0.09, dt));
        _vinylSlide.X = _slide * _vinylShadow.Bounds.Width;
        _vinyl.SpinDegrees = now / 3200 * 360 % 360;

        InvalidateVisual();
    }

    // ── drawing ──────────────────────────────────────────────────────────

    public override void Render(DrawingContext context)
    {
        var w = Bounds.Width;
        var h = Bounds.Height;
        if (w <= 0 || h <= 0) return;
        var now = _clock.Elapsed.TotalMilliseconds;
        var box = new Rect(0, 0, w, h);
        if (!IsAnimated) now = StillMs;

        DrawAurora(context, now, box);
        EnsureLedWall(w, h);
        if (_ledBase is not null && _ledLit is not null) DrawLedWall(context, now, box, _ledBase, _ledLit);
        DrawBeams(context, now, w, h);
        DrawShade(context, box);
        if (_card.IsVisible) DrawShadows(context);
    }

    private void DrawAurora(DrawingContext context, double now, Rect box)
    {
        // It drifts on 30-second sine clocks, so 10 updates a second are plenty.
        if (now - _auroraAt >= 100 || _auroraAt < 0)
        {
            _auroraAt = now;
            HeroRaster.RenderAurora(_auroraPixels, now, 0, _palette, 38 * HeroRaster.AuroraWidth / box.Width);
            // Its 0.85 opacity over the hero's background, done here so it draws as an opaque image.
            for (var i = 0; i < _auroraPixels.Length; i += 4)
            {
                _auroraPixels[i] = (byte)(_auroraPixels[i] * 0.85 + 23 * 0.15 + 0.5);
                _auroraPixels[i + 1] = (byte)(_auroraPixels[i + 1] * 0.85 + 19 * 0.15 + 0.5);
                _auroraPixels[i + 2] = (byte)(_auroraPixels[i + 2] * 0.85 + 17 * 0.15 + 0.5);
            }
            using var buffer = _aurora.Lock();
            System.Runtime.InteropServices.Marshal.Copy(_auroraPixels, 0, buffer.Address, _auroraPixels.Length);
        }

        // blur(38px) saturate(1.2) scale(1.15), opacity 0.85, over the hero's own background.
        using (context.PushRenderOptions(Bilinear))
        {
            context.DrawImage(_aurora, new Rect(0, 0, HeroRaster.AuroraWidth, HeroRaster.AuroraHeight),
                new Rect(-box.Width * 0.075, -box.Height * 0.075, box.Width * 1.15, box.Height * 1.15));
        }
    }

    private void EnsureLedWall(double w, double h)
    {
        if (_coverRgba is null) return;
        var scale = VisualPixelScale.For(this);
        var size = new PixelSize(Math.Max(1, (int)Math.Round(w * scale)), Math.Max(1, (int)Math.Round(h * scale)));
        if (_ledBase is not null && size == _ledSize) return;

        var pitch = (w < 700 ? 6 : 8) * scale;
        var (baseLayer, litLayer) = HeroRaster.BuildLedWall(_coverRgba, _coverW, _coverH, size.Width, size.Height,
            pitch, _palette);
        _ledBase = ToBitmap(baseLayer, size, 0.55);
        _ledLit = ToBitmap(litLayer, size);
        _ledSize = size;
    }

    /// <summary>
    ///     An IMMUTABLE bitmap over premultiplied BGRA, optionally at <paramref name="opacity" />
    ///     baked into its pixels: every layer here is drawn at one constant opacity, and baking it
    ///     lets the layer draw as one plain image, where a brush or an opacity layer cost a
    ///     full-size intermediate every frame.
    /// </summary>
    private static Bitmap ToBitmap(byte[] bgra, PixelSize size, double opacity = 1)
    {
        if (opacity < 1)
            for (var i = 0; i < bgra.Length; i++) bgra[i] = (byte)(bgra[i] * opacity + 0.5);
        var handle = System.Runtime.InteropServices.GCHandle.Alloc(bgra, System.Runtime.InteropServices.GCHandleType.Pinned);
        try
        {
            return new Bitmap(PixelFormat.Bgra8888, AlphaFormat.Premul, handle.AddrOfPinnedObject(), size,
                new Vector(96, 96), size.Width * 4);
        }
        finally
        {
            handle.Free();
        }
    }

    private static void Draw(DrawingContext context, Bitmap bitmap, Rect rect) =>
        context.DrawImage(bitmap, new Rect(bitmap.PixelSize.ToSize(1)), rect);

    private void DrawLedWall(DrawingContext context, double now, Rect box, Bitmap ledBase, Bitmap ledLit)
    {
        var src = new Rect(ledBase.PixelSize.ToSize(1));
        Draw(context, ledBase, box);

        if (!IsAnimated) return;

        // A diagonal scan band sweeping the wall, off screen at both ends of its loop.
        var sweepX = HeroRaster.BandSweepX(now, box.Width, box.Height);
        var band = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(sweepX - 200, 0, RelativeUnit.Absolute),
            EndPoint = new RelativePoint(sweepX + 200, box.Height * 0.4, RelativeUnit.Absolute),
            GradientStops =
            {
                new GradientStop(Colors.Transparent, 0),
                new GradientStop(Color.FromArgb(204, 0, 0, 0), 0.5),
                new GradientStop(Colors.Transparent, 1)
            }
        };
        using (context.PushOpacityMask(band, box))
            Draw(context, ledLit, box);


        // A spotlight that follows the pointer.
        if (_pointer is { } p)
        {
            var spot = new RadialGradientBrush
            {
                Center = new RelativePoint(p.X, p.Y, RelativeUnit.Absolute),
                GradientOrigin = new RelativePoint(p.X, p.Y, RelativeUnit.Absolute),
                RadiusX = new RelativeScalar(170, RelativeUnit.Absolute),
                RadiusY = new RelativeScalar(170, RelativeUnit.Absolute),
                GradientStops = { new GradientStop(Colors.Black, 0), new GradientStop(Colors.Transparent, 1) }
            };
            // Its layer is bounded to the spot, not the hero.
            var around = new Rect(p.X - 170, p.Y - 170, 340, 340).Intersect(box);
            using (context.PushClip(around))
            using (context.PushOpacityMask(spot, around))
                context.DrawImage(ledLit, src, box);
        }
    }

    private void DrawBeams(DrawingContext context, double now, double w, double h)
    {
        Rgb[] colors = [_palette.C1, HeroColor.Accent, _palette.C2];
        var seconds = now / 1000;
        for (var i = 0; i < Beams.Length; i++)
        {
            var bitmap = _beamBitmaps[i] ??= ToBitmap(HeroRaster.RenderBeam(128, 384, colors[i], 3),
                new PixelSize(128, 384), 0.22);
            var beam = Beams[i];
            var angle = HeroRaster.BeamAngle(seconds, beam.Duration, beam.Delay) * Math.PI / 180;
            var pivot = Matrix.CreateRotation(angle) * Matrix.CreateTranslation(w * (beam.Left + 0.17), -h * 0.12);
            using (context.PushTransform(pivot))
            using (context.PushRenderOptions(Bilinear))
                Draw(context, bitmap, new Rect(-w * 0.17, 0, w * 0.34, h * 1.35));
        }
    }

    private static readonly RenderOptions Bilinear = new() { BitmapInterpolationMode = BitmapInterpolationMode.LowQuality };
    private double _auroraAt = -1;

    // The card's and the record's shadows, baked (HeroRaster.RenderShadow) at half resolution: a
    // live BoxShadow re-blurred every frame the stage repainted under it.
    private const double ShadowScale = 0.5;
    private double _shadowSide;
    private Bitmap? _cardDark, _cardGlow, _vinylShade;

    private void DrawShadows(DrawingContext context)
    {
        var side = _card.Bounds.Width;
        if (side <= 0) return;
        if (_shadowSide != side)
        {
            _shadowSide = side;
            _cardDark = Shadow(side, side, 25, false, new Rgb(0, 0, 0), 0.55);
            _cardGlow = Shadow(side, side, 15, false, _palette.C1, 0.35);
            var disc = side * 0.92;
            _vinylShade = Shadow(disc, disc, 20, true, new Rgb(0, 0, 0), 0.6);
        }

        var card = _card.Bounds;
        Draw(context, _cardGlow!, Padded(card, 15, 0));
        Draw(context, _cardDark!, Padded(card, 25, 24));
        var vinyl = _vinylShadow.Bounds.Translate(new Vector(_vinylSlide.X, 0));
        Draw(context, _vinylShade!, Padded(vinyl, 20, 10));
    }

    /// <summary>A shadow's box: the shape grown by three sigmas, moved down by its offset.</summary>
    private static Rect Padded(Rect shape, double sigma, double offsetY) =>
        shape.Inflate(sigma * 3).Translate(new Vector(0, offsetY));

    private static Bitmap Shadow(double w, double h, double sigma, bool ellipse, Rgb color, double alpha)
    {
        var pad = sigma * 3;
        var pw = Math.Max(1, (int)Math.Ceiling((w + pad * 2) * ShadowScale));
        var ph = Math.Max(1, (int)Math.Ceiling((h + pad * 2) * ShadowScale));
        return ToBitmap(HeroRaster.RenderShadow(pw, ph, pad * ShadowScale, pad * ShadowScale,
            w * ShadowScale, h * ShadowScale, ellipse, sigma * ShadowScale, color, alpha), new PixelSize(pw, ph));
    }

    private static readonly IBrush ShadeUp = new LinearGradientBrush
    {
        StartPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
        EndPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
        GradientStops =
        {
            new GradientStop(Color.FromArgb(245, 17, 19, 23), 0),
            new GradientStop(Color.FromArgb(153, 17, 19, 23), 0.28),
            new GradientStop(Color.FromArgb(0, 17, 19, 23), 0.60)
        }
    }.ToImmutable();

    private static readonly IBrush ShadeRight = new LinearGradientBrush
    {
        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
        EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative),
        GradientStops =
        {
            new GradientStop(Color.FromArgb(191, 17, 19, 23), 0),
            new GradientStop(Color.FromArgb(0, 17, 19, 23), 0.55)
        }
    }.ToImmutable();

    private static void DrawShade(DrawingContext context, Rect box)
    {
        context.FillRectangle(ShadeUp, box);
        context.FillRectangle(ShadeRight, box);
    }
}
