using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Threading;
using DotCharter.Tone.UI.Controls;
using DotCharter.UI.Controls.Themes;

namespace DotCharter.UI.Controls;

/// <summary>
///     Home's hero when the last song has no cover: a guitar neck seen at a slight angle, six
///     strings in the highway's string colours over faint frets, strummed low E to high e every few
///     seconds and left to ring out. It gives the window an animation instead of
///     a blank panel.
///     <para>
///         Same lifecycle as <see cref="MenuBackdropControl" />: one ~30 fps
///         <see cref="DispatcherTimer" /> that STOPS whenever the control is detached or not
///         effectively visible (Home is an <c>IsVisible</c>-flipped screen, so a hidden Home would
///         otherwise tick for the whole session), and between strums, once every string has rung
///         out, the tick repaints nothing. The motion is <see cref="HeroStringsMath" />.
///     </para>
/// </summary>
public sealed class HeroStringsBackdrop : Control
{
    private const int Segments = 44;
    private const int GlowLevels = 8;
    private const int FretCount = 15;

    private readonly Stopwatch _clock = new();
    private readonly DispatcherTimer _timer;
    private bool _attached;
    private bool _running;
    private bool _drewSettled;

    private readonly IPen?[] _stringPens = new IPen?[HeroStringsMath.StringCount];
    private readonly IPen?[,] _glowPens = new IPen?[HeroStringsMath.StringCount, GlowLevels];
    private IPen? _fretPen;
    private IPen? _nutPen;
    private IBrush? _haze;

    /// <summary>Repaints the tick asked for; parked or settled, it stops advancing.</summary>
    internal long InvalidationCount { get; private set; }

    internal bool IsStrumming => _timer.IsEnabled;

    /// <summary>Off (the default, Settings ▸ Graphics): the neck is drawn once, at rest, with no clock.</summary>
    public static readonly StyledProperty<bool> IsAnimatedProperty =
        AvaloniaProperty.Register<HeroStringsBackdrop, bool>(nameof(IsAnimated));

    public bool IsAnimated
    {
        get => GetValue(IsAnimatedProperty);
        set => SetValue(IsAnimatedProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property != IsAnimatedProperty) return;
        SyncRunning();
        Repaint();
    }

    public HeroStringsBackdrop()
    {
        _ = new EffectiveVisibilityWatcher(this, _ => SyncRunning());
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(33) };
        _timer.Tick += OnTick;
        IsHitTestVisible = false;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _attached = true;
        ChartTheme.ThemeChanged += OnThemeChanged;
        ResolvePalette();
        SyncRunning();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        ChartTheme.ThemeChanged -= OnThemeChanged;
        _attached = false;
        SyncRunning();
        base.OnDetachedFromVisualTree(e);
    }

    private void SyncRunning()
    {
        var live = _attached && IsEffectivelyVisible && IsAnimated;
        if (live == _running) return;
        _running = live;
        if (live)
        {
            _clock.Start();
            _timer.Start();
            Repaint();
        }
        else
        {
            _timer.Stop();
            _clock.Stop();
        }
    }

    private void OnThemeChanged()
    {
        ResolvePalette();
        if (_running) Repaint();
    }

    private void OnTick(object? sender, EventArgs e)
    {
        if (!_running) return;
        var settled = HeroStringsMath.IsSettled(_clock.Elapsed.TotalSeconds);
        // A settled neck draws the same frame every tick: paint it once, then wait for the strum.
        if (settled && _drewSettled) return;
        _drewSettled = settled;
        Repaint();
    }

    private void Repaint()
    {
        InvalidationCount++;
        InvalidateVisual();
    }

    private void ResolvePalette()
    {
        var colors = ChartTheme.BaseStringColors;
        for (var i = 0; i < HeroStringsMath.StringCount; i++)
        {
            var c = colors[i % colors.Length];
            // Thicker strings are drawn heavier, like the highway's.
            _stringPens[i] = new ImmutablePen(new ImmutableSolidColorBrush(Color.FromArgb(150, c.R, c.G, c.B)),
                2.2 - i * 0.22, lineCap: PenLineCap.Round);
            for (var level = 0; level < GlowLevels; level++)
            {
                var alpha = (byte)(10 + level * 11);
                _glowPens[i, level] = new ImmutablePen(new ImmutableSolidColorBrush(Color.FromArgb(alpha, c.R, c.G, c.B)),
                    7, lineCap: PenLineCap.Round);
            }
        }

        _fretPen = new ImmutablePen(new ImmutableSolidColorBrush(Color.FromArgb(26, 255, 255, 255)), 1.5);
        _nutPen = new ImmutablePen(new ImmutableSolidColorBrush(Color.FromArgb(46, 255, 255, 255)), 4);
        var accent = this.TryFindResource("FireAccent", out var a) && a is ISolidColorBrush b
            ? b.Color
            : Color.FromRgb(96, 165, 250);
        _haze = new RadialGradientBrush
        {
            Center = new RelativePoint(0.78, 0.28, RelativeUnit.Relative),
            GradientOrigin = new RelativePoint(0.78, 0.28, RelativeUnit.Relative),
            RadiusX = new RelativeScalar(0.6, RelativeUnit.Relative),
            RadiusY = new RelativeScalar(0.75, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(Color.FromArgb(40, accent.R, accent.G, accent.B), 0),
                new GradientStop(Color.FromArgb(0, accent.R, accent.G, accent.B), 1)
            }
        }.ToImmutable();
    }

    public override void Render(DrawingContext context)
    {
        var w = Bounds.Width;
        var h = Bounds.Height;
        if (w <= 0 || h <= 0 || _fretPen is null) return;

        context.DrawRectangle(_haze, null, new Rect(0, 0, w, h));

        // The neck: from a nut a little in from the left to past the right edge, rising slightly,
        // strings spreading a little as they come toward the viewer.
        var nutX = w * 0.12;
        var endX = w * 1.05;
        var nutMid = h * 0.40;
        var endMid = h * 0.30;
        var nutSpread = Math.Min(h * 0.26, 120);
        var endSpread = nutSpread * 1.45;

        double StringY(int i, double u)
        {
            var mid = nutMid + (endMid - nutMid) * u;
            var spread = nutSpread + (endSpread - nutSpread) * u;
            // Low E at the bottom, as a player looking down at the neck sees it.
            return mid + spread * (0.5 - i / (double)(HeroStringsMath.StringCount - 1));
        }

        var span = endX - nutX;
        var scale = span * 1.35;
        context.DrawLine(_nutPen!, new Point(nutX, StringY(HeroStringsMath.StringCount - 1, 0) - 6),
            new Point(nutX, StringY(0, 0) + 6));
        for (var n = 1; n <= FretCount; n++)
        {
            var u = HeroStringsMath.FretPosition(n, scale) / span;
            if (u > 1) break;
            var x = nutX + u * span;
            context.DrawLine(_fretPen, new Point(x, StringY(HeroStringsMath.StringCount - 1, u) - 4),
                new Point(x, StringY(0, u) + 4));
        }

        // Still: a moment between strums, when every string has rung out.
        var seconds = IsAnimated ? _clock.Elapsed.TotalSeconds : HeroStringsMath.StrumPeriod - 0.01;
        for (var i = 0; i < HeroStringsMath.StringCount; i++)
        {
            var envelope = HeroStringsMath.Envelope(i, seconds);
            var glowLevel = (int)Math.Clamp(envelope / HeroStringsMath.PeakAmplitude * GlowLevels, 0, GlowLevels - 1);
            if (envelope < HeroStringsMath.SettledAmplitude)
            {
                context.DrawLine(_stringPens[i]!, new Point(nutX, StringY(i, 0)), new Point(endX, StringY(i, 1)));
                continue;
            }

            // ONE polyline per string: separate translucent segments would double up at every joint.
            var geometry = new StreamGeometry();
            using (var g = geometry.Open())
            {
                g.BeginFigure(new Point(nutX, StringY(i, 0)), false);
                for (var s = 1; s <= Segments; s++)
                {
                    var u = s / (double)Segments;
                    g.LineTo(new Point(nutX + u * span, StringY(i, u) + HeroStringsMath.Displacement(i, u, seconds)));
                }

                g.EndFigure(false);
            }

            context.DrawGeometry(null, _glowPens[i, glowLevel], geometry);
            context.DrawGeometry(null, _stringPens[i], geometry);
        }
    }
}
