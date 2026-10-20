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
///     The Play Mode main-menu backdrop (0.16 5.2): ONE custom-drawn control
///     replacing the previous ~29 style-animated elements (3 neon glows,
///     6 string lines, 20 EQ bars). Everything renders in a single pass on a
///     throttled ~30 fps timer, and the timer STOPS entirely whenever the
///     control is detached or not effectively visible — the same gating
///     semantics the old <c>backdropLive</c> style class enforced, now owned
///     by the control itself (style animations kept ticking on hidden
///     controls; a parked timer simply doesn't).
///     <para>
///     Layers (back to front): a vertical wash derived from the app
///     background token, a field of accent-hued radial glows with slow
///     parallax drift + opacity breathing, a few faint gently-tilted
///     guitar-string lines with a traveling shimmer, a sparse pooled particle
///     field, and the bottom-edge EQ bars.
///     All colours resolve from theme tokens (<c>Accent*</c>,
///     <c>AppBackgroundColor</c>) + <see cref="ChartTheme.BaseStringColors" />
///     at attach and again on <see cref="ChartTheme.ThemeChanged" /> — no
///     hard-coded palette. All animation math lives in the pure
///     <see cref="MenuBackdropMath" />.
///     </para>
///     <para>
///     Since the 0.16 visual pass ONE instance renders behind every Play
///     Mode screen; the <see cref="Intensity" /> styled property picks the
///     per-screen profile (Full front door vs. dimmed Ambient behind
///     content screens) via <see cref="MenuBackdropMath.ProfileFor" />.
///     </para>
/// </summary>
public sealed class MenuBackdropControl : Control
{
    /// <summary>Per-screen intensity (0.16 visual pass): the SAME single
    /// backdrop instance renders behind every Play Mode screen; screens set
    /// this styled property (via binding) to pick the front-door Full scene
    /// or the dimmed Ambient treatment. All tuning lives in the pure
    /// <see cref="MenuBackdropMath.ProfileFor" />.</summary>
    public static readonly StyledProperty<MenuBackdropIntensity> IntensityProperty =
        AvaloniaProperty.Register<MenuBackdropControl, MenuBackdropIntensity>(
            nameof(Intensity), MenuBackdropIntensity.Full);

    public MenuBackdropIntensity Intensity
    {
        get => GetValue(IntensityProperty);
        set => SetValue(IntensityProperty, value);
    }

    private MenuBackdropProfile _profile = MenuBackdropMath.ProfileFor(MenuBackdropIntensity.Full);

    private const int BarCount = 20;
    private const double BarWidth = 7;
    private const double BarGap = 6;
    private const int ParticleCount = MenuBackdropMath.MaxParticleCount;
    private const int ParticleAlphaLevels = 8;
    // Wave V4: fewer, more spread-out neck lines (was 6 tightly-stacked) so the
    // strings read as art, not scanlines.
    private const int StringCount = 4;
    private const double StringSpacing = 40;

    // One shared clock: parking freezes the scene, resuming continues it.
    private readonly Stopwatch _clock = new();
    private readonly DispatcherTimer _timer;
    private bool _attached;
    private bool _running;
    private double _lastTickSeconds;

    /// <summary>Total <c>InvalidateVisual</c> calls issued by the animation
    /// tick — the perf counter for comparing against the old 29-clock
    /// backdrop (which re-rendered at compositor rate and re-LAID-OUT the EQ
    /// StackPanel every frame). Parked ⇒ this stops advancing.</summary>
    internal long InvalidationCount { get; private set; }

    /// <summary>Whether the animation timer is currently running (parked =
    /// false). Exposed for verification.</summary>
    internal bool IsBackdropAnimating => _timer.IsEnabled;

    /// <summary>Audio-reactive EQ source (0.16 5.3): returns the latest raw
    /// input-capture peak. Wired by PlayModeView to
    /// <c>PlayModeViewModel.ReadBackdropInputPeak</c> — the SAME
    /// non-destructive windowed tap the pre-play signal meter uses, polled
    /// only at this control's own throttled tick (so a parked backdrop never
    /// touches the audio engine). Null / silence ⇒ the idle bar pattern.</summary>
    public Func<float>? InputPeakProvider { get; set; }

    // Peak-follower state: envelope decays smoothly, blend crossfades the
    // bars between the idle pattern and the audio-driven targets.
    private double _audioEnvelope;
    private double _audioBlend;

    // ---- palette (resolved at attach + on theme change) --------------------

    private IBrush? _washBrush;
    private IBrush? _dimBrush; // Ambient readability dim (background-coloured)
    // Wave V4: a richer 5-glow field (was 3) at Full intensity — more colour
    // depth across the front door; the Ambient profile still scales it down.
    private readonly IBrush?[] _glowBrushes = new IBrush?[5];
    private readonly IBrush?[] _stringBrushes = new IBrush?[StringCount];
    private readonly IPen?[] _stringPens = new IPen?[StringCount];
    private readonly IBrush?[] _shimmerBrushes = new IBrush?[StringCount];
    private readonly IBrush?[] _barBrushes = new IBrush?[BarCount];
    private readonly IBrush?[] _particleBrushes = new IBrush?[ParticleAlphaLevels];

    // ---- animated state ----------------------------------------------------

    private readonly double[] _barHeights = new double[BarCount];

    private struct Particle
    {
        public double BaseX;   // normalized 0..1
        public double Y;       // normalized 0..1
        public double Speed;   // normalized units/s upward
        public double SwayPhase;
        public double TwinklePhase;
        public double Radius;  // px
    }

    private readonly Particle[] _particles = new Particle[ParticleCount];

    /// <summary>Glow anchor positions (normalized) + radius factor (of the
    /// larger bounds side). Wave V4 grows the field from three to five so the
    /// front door glows fill the frame with depth instead of leaving a
    /// near-empty backdrop; the Ambient profile scales opacity + drift down.</summary>
    private static readonly (double Ax, double Ay, double R)[] GlowAnchors =
    [
        (0.14, 0.18, 0.46), // top-left
        (0.87, 0.84, 0.51), // bottom-right
        (0.50, 0.50, 0.44), // center
        (0.80, 0.20, 0.40), // top-right
        (0.20, 0.80, 0.42)  // bottom-left
    ];

    public MenuBackdropControl()
    {
        // Effective visibility (own IsVisible binding AND any hidden ancestor —
        // the prewarmed Play Mode surface under the editor) — through the
        // watcher, because the old OnPropertyChanged branch keyed on a name
        // Avalonia 12 never raises (see EffectiveVisibilityWatcher).
        _ = new EffectiveVisibilityWatcher(this, _ => SyncRunning());

        _timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(33) // ~30 fps cap
        };
        _timer.Tick += OnTick;

        // Deterministic seed: the field looks the same every launch and the
        // pool is allocated exactly once — no per-frame allocation.
        var rng = new Random(1337);
        for (var i = 0; i < ParticleCount; i++)
        {
            _particles[i] = new Particle
            {
                BaseX = rng.NextDouble(),
                Y = rng.NextDouble(),
                Speed = 0.008 + rng.NextDouble() * 0.020,
                SwayPhase = rng.NextDouble() * Math.PI * 2,
                TwinklePhase = rng.NextDouble() * Math.PI * 2,
                Radius = 1.0 + rng.NextDouble() * 1.6
            };
        }

        for (var i = 0; i < BarCount; i++)
            _barHeights[i] = MenuBackdropMath.BarMinHeight;
    }

    // ---- lifecycle / gating (Wave-1 semantics: park when not shown) --------

    protected override void OnAttachedToVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _attached = true;
        ChartTheme.ThemeChanged += OnThemeChanged;
        ResolvePalette();
        SyncRunning();
    }

    protected override void OnDetachedFromVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        // ChartTheme.ThemeChanged is a static event — unsubscribing here is
        // what keeps a detached backdrop collectable (leak-guard contract).
        ChartTheme.ThemeChanged -= OnThemeChanged;
        _attached = false;
        SyncRunning();
        base.OnDetachedFromVisualTree(e);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        // Per-screen intensity: resolve the profile ONCE per change (never
        // per frame). Parked ⇒ the next wake redraws with it anyway.
        if (change.Property == IntensityProperty)
        {
            _profile = MenuBackdropMath.ProfileFor(change.GetNewValue<MenuBackdropIntensity>());
            if (_running)
            {
                InvalidationCount++;
                InvalidateVisual();
            }
        }
    }

    /// <summary>Starts/stops the animation timer off attach state + effective
    /// visibility. Parked = timer stopped = zero invalidations, zero render
    /// passes, zero layout — the backdrop costs nothing while hidden.</summary>
    private void SyncRunning()
    {
        var live = _attached && IsEffectivelyVisible;
        if (live == _running) return;
        _running = live;
        if (live)
        {
            _clock.Start();
            _lastTickSeconds = _clock.Elapsed.TotalSeconds;
            _timer.Start();
            InvalidationCount++;
            InvalidateVisual(); // first frame immediately, not a tick later
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
        // Parked ⇒ no repaint needed now; the next wake redraws with the new
        // palette anyway (InvalidateVisual only on real, visible change).
        if (_running)
        {
            InvalidationCount++;
            InvalidateVisual();
        }
    }

    // ---- animation tick ----------------------------------------------------

    private void OnTick(object? sender, EventArgs e)
    {
        if (!_running) return;
        var t = _clock.Elapsed.TotalSeconds;
        var dt = Math.Clamp(t - _lastTickSeconds, 0.0, 0.1);
        _lastTickSeconds = t;

        // Audio: poll the input peak on THIS throttled tick (no extra timer,
        // no polling while parked), follow it with a smooth-decay envelope
        // and crossfade the bars between idle and audio-driven targets.
        // The Ambient profile has no EQ bars, so it skips the whole block —
        // including the native input-capture read — behind content screens.
        if (_profile.ShowEqBars)
        {
            var mapped = MenuBackdropMath.MapPeak(InputPeakProvider?.Invoke() ?? 0f);
            _audioEnvelope = MenuBackdropMath.AudioEnvelope(_audioEnvelope, mapped);
            _audioBlend = MenuBackdropMath.NextAudioBlend(
                _audioBlend, MenuBackdropMath.IsAudioLive(_audioEnvelope));

            for (var i = 0; i < BarCount; i++)
            {
                var idle = MenuBackdropMath.IdleBarHeight(i, t);
                var target = _audioBlend <= 0
                    ? idle
                    : MenuBackdropMath.BlendBarTarget(idle,
                        MenuBackdropMath.AudioBarTarget(i, BarCount, _audioEnvelope, t), _audioBlend);
                _barHeights[i] = MenuBackdropMath.SmoothBarHeight(_barHeights[i], target);
            }
        }

        // Particles: advance the pooled structs in place (no allocation).
        // Only the profile's visible slice moves — hidden particles freeze.
        var speedFactor = _profile.ParticleSpeedFactor;
        var visibleParticles = Math.Min(_profile.ParticleCount, ParticleCount);
        for (var i = 0; i < visibleParticles; i++)
        {
            _particles[i].Y = MenuBackdropMath.AdvanceParticleY(
                _particles[i].Y, _particles[i].Speed * speedFactor, dt);
        }

        // Every tick advances the scene, so this is a real visual change —
        // and it is the ONLY InvalidateVisual source while animating (≤30 Hz).
        InvalidationCount++;
        InvalidateVisual();
    }

    // ---- palette -----------------------------------------------------------

    private Color FindColor(string key, Color fallback)
        => this.TryFindResource(key, out var value) && value is Color c ? c : fallback;

    /// <summary>Resolves every brush from the CURRENT theme tokens. Called at
    /// attach and on <see cref="ChartTheme.ThemeChanged" /> — never per frame.</summary>
    private void ResolvePalette()
    {
        var accent = FindColor("AccentColorValue", Color.Parse("#5E9FF2"));
        var accentEnd = FindColor("AccentGradientEndColor", Color.Parse("#3C7BD1"));
        var accentHover = FindColor("AccentHoverColor", Color.Parse("#7AB6FF"));
        var background = FindColor("AppBackgroundColor", Color.Parse("#0E0F14"));

        // Vertical wash: graded darkness derived from the app background so
        // every theme keeps the "lit from above" depth of the old fixed wash.
        _washBrush = new ImmutableLinearGradientBrush(
            new[]
            {
                new ImmutableGradientStop(0.00, Scale(background, 1.25)),
                new ImmutableGradientStop(0.55, Scale(background, 0.55)),
                new ImmutableGradientStop(1.00, Scale(background, 0.40))
            },
            startPoint: new RelativePoint(0, 0, RelativeUnit.Relative),
            endPoint: new RelativePoint(0, 1, RelativeUnit.Relative));

        // Ambient readability dim: a background-coloured veil drawn over the
        // whole scene at the profile's SceneDimOpacity (0 = not drawn).
        _dimBrush = new ImmutableSolidColorBrush(Scale(background, 0.45));

        // Five related glow hues across the accent ramp — a richer field than
        // the old three (Wave V4), still built only from theme accent tokens.
        _glowBrushes[0] = MakeGlow(accent);
        _glowBrushes[1] = MakeGlow(accentEnd);
        _glowBrushes[2] = MakeGlow(Mix(accent, accentEnd, 0.5));
        _glowBrushes[3] = MakeGlow(accentHover);
        _glowBrushes[4] = MakeGlow(Mix(accentEnd, accentHover, 0.5));

        // Strings use the SAME theme-driven string colours as the highway
        // (includes the colour-blind-safe presets). Base line is faint (drawn
        // as a gently-tilted line, Wave V4); the shimmer is a softer gradient
        // highlight traveling along it.
        for (var i = 0; i < StringCount; i++)
        {
            var c = ChartTheme.BaseStringColors[i % ChartTheme.BaseStringColors.Length];
            var lineBrush = new ImmutableSolidColorBrush(c);
            _stringBrushes[i] = lineBrush;
            _stringPens[i] = new ImmutablePen(lineBrush, 1.5);
            _shimmerBrushes[i] = new ImmutableLinearGradientBrush(
                new[]
                {
                    new ImmutableGradientStop(0.0, Color.FromArgb(0x00, c.R, c.G, c.B)),
                    new ImmutableGradientStop(0.5, Color.FromArgb(0x4D, c.R, c.G, c.B)),
                    new ImmutableGradientStop(1.0, Color.FromArgb(0x00, c.R, c.G, c.B))
                },
                startPoint: new RelativePoint(0, 0.5, RelativeUnit.Relative),
                endPoint: new RelativePoint(1, 0.5, RelativeUnit.Relative));
        }

        // EQ bars: a smooth accent → gradient-end ramp across the row
        // (alpha baked in — the old row was 0.55 × 0.5 opacity).
        for (var i = 0; i < BarCount; i++)
        {
            var c = Mix(accent, accentEnd, i / (double)(BarCount - 1));
            _barBrushes[i] = new ImmutableSolidColorBrush(Color.FromArgb(0x55, c.R, c.G, c.B));
        }

        // Particles: quantized twinkle-alpha levels so the field needs no
        // per-particle PushOpacity (and no per-frame brush allocation).
        for (var i = 0; i < ParticleAlphaLevels; i++)
        {
            var alpha = (byte)(0x0A + i * (0x52 - 0x0A) / (ParticleAlphaLevels - 1));
            _particleBrushes[i] = new ImmutableSolidColorBrush(
                Color.FromArgb(alpha, accentHover.R, accentHover.G, accentHover.B));
        }
    }

    private static Color Scale(Color c, double factor)
        => Color.FromRgb(
            (byte)Math.Clamp(c.R * factor, 0, 255),
            (byte)Math.Clamp(c.G * factor, 0, 255),
            (byte)Math.Clamp(c.B * factor, 0, 255));

    private static Color Mix(Color a, Color b, double t)
        => Color.FromRgb(
            (byte)Math.Clamp(a.R + (b.R - a.R) * t, 0, 255),
            (byte)Math.Clamp(a.G + (b.G - a.G) * t, 0, 255),
            (byte)Math.Clamp(a.B + (b.B - a.B) * t, 0, 255));

    private static IBrush MakeGlow(Color c) => new ImmutableRadialGradientBrush(
        new[]
        {
            new ImmutableGradientStop(0.0, Color.FromArgb(0x33, c.R, c.G, c.B)),
            new ImmutableGradientStop(1.0, Color.FromArgb(0x00, c.R, c.G, c.B))
        },
        radiusX: new RelativeScalar(0.5, RelativeUnit.Relative),
        radiusY: new RelativeScalar(0.5, RelativeUnit.Relative));

    // ---- render (the single pass) -------------------------------------------

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var w = Bounds.Width;
        var h = Bounds.Height;
        if (w <= 0 || h <= 0 || _washBrush == null) return;
        var t = _clock.Elapsed.TotalSeconds;
        var bounds = new Rect(0, 0, w, h);

        using var clip = context.PushClip(bounds); // oversized glows stay inside

        // 1) Wash.
        context.DrawRectangle(_washBrush, null, bounds);

        // 2) Glows: anchored like the old three ellipses (top-left /
        //    bottom-right / center), drifting slowly for parallax. The
        //    profile scales opacity + drift (Ambient = softer, calmer).
        var glowScale = Math.Max(w, h);
        for (var i = 0; i < GlowAnchors.Length; i++)
        {
            if (_glowBrushes[i] is not { } brush) continue;
            var (dx, dy) = MenuBackdropMath.GlowDrift(i, t, _profile.GlowDriftAmplitude);
            var center = new Point((GlowAnchors[i].Ax + dx) * w, (GlowAnchors[i].Ay + dy) * h);
            var radius = GlowAnchors[i].R * glowScale;
            using (context.PushOpacity(MenuBackdropMath.GlowOpacity(i, t) * _profile.GlowOpacityFactor))
                context.DrawEllipse(brush, null, center, radius, radius);
        }

        // 3) Strings: faint theme-coloured lines, gently TILTED so they fan in a
        //    subtle perspective (Wave V4 — no more horizontal scanlines) + a
        //    softer shimmer that travels along each tilted line. The profile
        //    fades both together (0 = skipped entirely).
        if (_profile.StringOpacityFactor > 0)
        {
            var stringsMid = h * 0.5 + 60;
            var stringsTop = stringsMid - (StringCount - 1) * StringSpacing / 2;
            for (var i = 0; i < StringCount; i++)
            {
                var slope = MenuBackdropMath.StringSlope(i, StringCount);
                // Bias the left endpoint up so the tilted line stays centred on
                // its row (crosses the row height at mid-width).
                var yLeft = stringsTop + i * StringSpacing - slope * w * 0.5;
                var yRight = yLeft + slope * w;

                if (_stringPens[i] is { } pen)
                {
                    using (context.PushOpacity(
                               MenuBackdropMath.StringOpacity(i, t) * _profile.StringOpacityFactor))
                    {
                        context.DrawLine(pen, new Point(0, yLeft), new Point(w, yRight));
                    }
                }

                if (_shimmerBrushes[i] is { } shimmer)
                {
                    var cx = MenuBackdropMath.ShimmerCenterX(i, t, w);
                    var cy = yLeft + slope * cx;
                    var half = MenuBackdropMath.ShimmerHalfWidth;
                    using (context.PushOpacity(_profile.StringOpacityFactor))
                    using (context.PushTransform(
                               Matrix.CreateRotation(Math.Atan(slope)) * Matrix.CreateTranslation(cx, cy)))
                    {
                        context.DrawRectangle(shimmer, null, new Rect(-half, -1.5, half * 2, 3));
                    }
                }
            }
        }

        // 4) Particles (pooled; quantized alpha brushes — no per-frame
        //    allocs). Only the profile's slice of the pool is drawn.
        var visibleParticles = Math.Min(_profile.ParticleCount, ParticleCount);
        for (var i = 0; i < visibleParticles; i++)
        {
            ref var p = ref _particles[i];
            var alpha = MenuBackdropMath.ParticleAlpha(p.TwinklePhase, t);
            var level = Math.Clamp((int)(alpha * (ParticleAlphaLevels - 1)), 0, ParticleAlphaLevels - 1);
            if (_particleBrushes[level] is not { } brush) continue;
            var x = MenuBackdropMath.ParticleX(p.BaseX, p.SwayPhase, t) * w;
            context.DrawEllipse(brush, null, new Point(x, p.Y * h), p.Radius, p.Radius);
        }

        // 5) EQ bars along the bottom edge (centered like the old row) —
        //    front-door (Full) profile only.
        if (_profile.ShowEqBars)
        {
            var rowWidth = BarCount * BarWidth + (BarCount - 1) * BarGap;
            var x0 = (w - rowWidth) / 2;
            for (var i = 0; i < BarCount; i++)
            {
                if (_barBrushes[i] is not { } brush) continue;
                var barH = _barHeights[i];
                context.DrawRectangle(brush, null,
                    new Rect(x0 + i * (BarWidth + BarGap), h - barH, BarWidth, barH), 2, 2);
            }
        }

        // 6) Ambient readability dim: one background-coloured veil over the
        //    whole scene so list content on top stays readable.
        if (_profile.SceneDimOpacity > 0 && _dimBrush is { } dim)
        {
            using (context.PushOpacity(_profile.SceneDimOpacity))
                context.DrawRectangle(dim, null, bounds);
        }
    }
}
