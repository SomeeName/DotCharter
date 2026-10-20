namespace DotCharter.UI.Controls;

/// <summary>
///     Per-screen backdrop intensity (0.16 visual pass): the backdrop renders
///     behind EVERY Play Mode screen, but list-heavy screens (Song Select,
///     Stats, boards, History) run a dimmed "ambient" profile so content
///     stays readable, while the Start/Menu front door runs the full show.
/// </summary>
public enum MenuBackdropIntensity
{
    /// <summary>The full front-door scene: bright glows, all particles,
    /// string shimmer and the audio-reactive EQ bars.</summary>
    Full = 0,

    /// <summary>Dimmed treatment behind content screens: softer glows with a
    /// smaller drift, fewer + slower particles, fainter strings, NO EQ bars
    /// (they belong to the front door only) and a readability dim over the
    /// whole scene.</summary>
    Ambient = 1
}

/// <summary>
///     The resolved per-intensity tuning set. All factors are multipliers on
///     the Full-scene values; <see cref="ParticleCount" /> must never exceed
///     the control's pooled particle count (guarded by
///     <c>MenuBackdropMathTests</c>).
/// </summary>
public readonly record struct MenuBackdropProfile(
    double GlowOpacityFactor,
    int ParticleCount,
    double ParticleSpeedFactor,
    double StringOpacityFactor,
    bool ShowEqBars,
    double GlowDriftAmplitude,
    double SceneDimOpacity);

/// <summary>
///     Pure math behind <see cref="MenuBackdropControl" /> (the Play Mode
///     main-menu backdrop): idle EQ-bar motion, glow parallax/breathing,
///     string shimmer travel and the particle field. No Avalonia types, all
///     time injected — unit-tested in <c>MenuBackdropMathTests</c> without an
///     Avalonia instance (same pattern as <c>KnobMath</c>/<c>PedalDragMath</c>).
/// </summary>
public static class MenuBackdropMath
{
    // ---- intensity profiles -------------------------------------------------

    /// <summary>Pooled particle count of the control — the profile ceiling.</summary>
    public const int MaxParticleCount = 40;

    /// <summary>
    ///     Resolves the tuning set for an intensity. Full is the front-door
    ///     scene (all factors 1, no dim); Ambient halves the motion and lays a
    ///     subtle background-coloured dim over the scene so list content on top
    ///     stays readable.
    ///     <para>
    ///         <b>The EQ bars are off in BOTH profiles.</b> They were a row of
    ///         twenty audio-reactive columns along the bottom of the launch
    ///         screen, and they landed directly behind the LAST SESSION strip —
    ///         so the one place the scene had text to read was the one place it
    ///         put moving bars. The owner asked for them gone and the strip
    ///         kept. The field stays on the profile rather than being deleted
    ///         because the drawing code, its smoothing and
    ///         <c>MenuBackdropMathTests</c> are all still here and correct; this
    ///         is a decision about the scene, and one line is where it should be
    ///         reversible.
    ///     </para>
    /// </summary>
    public static MenuBackdropProfile ProfileFor(MenuBackdropIntensity intensity)
        => intensity == MenuBackdropIntensity.Ambient
            ? new MenuBackdropProfile(
                GlowOpacityFactor: 0.45,
                ParticleCount: 16,
                ParticleSpeedFactor: 0.5,
                StringOpacityFactor: 0.4,
                ShowEqBars: false,
                GlowDriftAmplitude: 0.03,
                SceneDimOpacity: 0.35)
            : new MenuBackdropProfile(
                GlowOpacityFactor: 1.0,
                ParticleCount: MaxParticleCount,
                ParticleSpeedFactor: 1.0,
                StringOpacityFactor: 1.0,
                ShowEqBars: false,
                GlowDriftAmplitude: 0.05,
                SceneDimOpacity: 0.0);
    // ---- EQ bars -----------------------------------------------------------

    /// <summary>Idle bar travel matches the old style-animation ranges
    /// (eqA 12–72, eqB 18–54, eqC 24–96, eqD 30–80) cycled across the bars.</summary>
    private static readonly double[] IdleRangeLo = [12, 18, 24, 30];
    private static readonly double[] IdleRangeHi = [72, 54, 96, 80];

    /// <summary>Full sine periods (seconds) echoing the old 0.9/1.3/1.1/1.6 s
    /// Alternate clocks (an Alternate keyframe pair is half a sine cycle).</summary>
    private static readonly double[] IdlePeriods = [1.8, 2.6, 2.2, 3.2];

    public const double BarMinHeight = 12.0;
    public const double BarMaxHeight = 96.0;

    /// <summary>Idle (no audio) height of bar <paramref name="barIndex" /> at
    /// <paramref name="tSeconds" /> — a staggered sine per bar, always within
    /// [<see cref="BarMinHeight" />, <see cref="BarMaxHeight" />].</summary>
    public static double IdleBarHeight(int barIndex, double tSeconds)
    {
        var slot = ((barIndex % 4) + 4) % 4;
        var period = IdlePeriods[slot];
        // Golden-ratio phase stagger so neighbouring bars never move in step.
        var phase = barIndex * 0.61803398875;
        var s = 0.5 * (1 + Math.Sin(2 * Math.PI * (tSeconds / period + phase)));
        return IdleRangeLo[slot] + s * (IdleRangeHi[slot] - IdleRangeLo[slot]);
    }

    /// <summary>Asymmetric smoothing toward <paramref name="target" />: fast
    /// attack (bars jump up with the signal) and a slower release (they fall
    /// smoothly). Tuned for a ~30 Hz tick.</summary>
    public static double SmoothBarHeight(double current, double target)
    {
        var k = target > current ? 0.55 : 0.14;
        return current + (target - current) * k;
    }

    // ---- Audio-reactive EQ bars (0.16 5.3) ----------------------------------
    // Fed by the SAME non-destructive input-capture window the pre-play
    // signal meter polls (LivePlayViewModel.ReadInputPeak) — no new native
    // calls. The control polls at its own ~30 Hz tick; these functions turn
    // the raw peak into a smoothed envelope and per-bar targets, and blend
    // back to the idle pattern when the signal dies.

    /// <summary>Envelope level above which the backdrop counts the input as
    /// live (switches the bars from idle to audio-driven).</summary>
    public const double AudioLiveThreshold = 0.02;

    /// <summary>Maps a raw capture-window peak onto a readable 0..1 level —
    /// the SAME sqrt(peak×4) curve as the pre-play signal meter, so the bars
    /// and the meter agree on what "loud" means.</summary>
    public static double MapPeak(float rawPeak)
        => Math.Sqrt(Math.Clamp(rawPeak * 4f, 0f, 1f));

    /// <summary>Peak-follower: instant attack (a pluck registers on the very
    /// next frame), exponential release (~1 s to silence at a 30 Hz tick).</summary>
    public static double AudioEnvelope(double current, double mappedPeak)
        => mappedPeak >= current ? mappedPeak : current * 0.90;

    /// <summary>Whether the envelope still counts as a live signal.</summary>
    public static bool IsAudioLive(double envelope) => envelope > AudioLiveThreshold;

    /// <summary>Eases the idle↔audio crossfade (0 = idle pattern, 1 = fully
    /// audio-driven): ramps in faster than it falls back so a pause between
    /// notes doesn't visibly snap the bars home.</summary>
    public static double NextAudioBlend(double blend, bool audioLive)
        => Math.Clamp(blend + (audioLive ? 0.08 : -0.03), 0.0, 1.0);

    /// <summary>Audio-driven target height for one bar: the envelope scales a
    /// center-weighted arch with a traveling ripple so the row reads as a
    /// spectrum, not a flat wall. Always within the bar range.</summary>
    public static double AudioBarTarget(int barIndex, int barCount, double envelope, double tSeconds)
    {
        var x = (barIndex + 0.5) / Math.Max(1, barCount); // 0..1 across the row
        var arch = 1.0 - Math.Abs(2 * x - 1) * 0.55;      // 1 center → 0.45 edges
        var ripple = 0.75 + 0.25 * Math.Sin(2 * Math.PI * (x * 3 - tSeconds * 1.7) + barIndex * 1.3);
        var level = Math.Clamp(envelope, 0.0, 1.0);
        var target = BarMinHeight + level * (BarMaxHeight - BarMinHeight) * arch * ripple;
        return Math.Clamp(target, BarMinHeight, BarMaxHeight);
    }

    /// <summary>Linear idle↔audio crossfade of a bar target.</summary>
    public static double BlendBarTarget(double idle, double audio, double blend)
        => idle + (audio - idle) * Math.Clamp(blend, 0.0, 1.0);

    // ---- Glows (parallax drift + opacity breathing) ------------------------

    /// <summary>Slow parallax offset (normalized units, ±<paramref name="amplitude" />)
    /// for glow <paramref name="glowIndex" />: an elliptical drift with
    /// per-glow incommensurate periods so the three glows never sync up.</summary>
    public static (double Dx, double Dy) GlowDrift(int glowIndex, double tSeconds, double amplitude)
    {
        // 27–45 s loops: slow enough to read as ambient light, not motion.
        var px = 27.0 + glowIndex * 9.0;
        var py = 33.0 + glowIndex * 6.0;
        var phase = glowIndex * 2.1;
        return (Math.Sin(2 * Math.PI * tSeconds / px + phase) * amplitude,
                Math.Cos(2 * Math.PI * tSeconds / py + phase * 0.7) * amplitude);
    }

    private static readonly double[] GlowOpacityLo = [0.50, 0.45, 0.40];
    private static readonly double[] GlowOpacityHi = [0.95, 0.85, 0.70];
    private static readonly double[] GlowOpacityPeriods = [18.0, 26.0, 34.0];

    /// <summary>Breathing opacity for glow <paramref name="glowIndex" /> —
    /// ranges/periods mirror the old drift1/2/3 opacity keyframes (9/13/17 s
    /// Alternate = 18/26/34 s full cycles).</summary>
    public static double GlowOpacity(int glowIndex, double tSeconds)
    {
        var i = ((glowIndex % 3) + 3) % 3;
        var s = 0.5 * (1 + Math.Sin(2 * Math.PI * tSeconds / GlowOpacityPeriods[i] + i * 1.7));
        return GlowOpacityLo[i] + s * (GlowOpacityHi[i] - GlowOpacityLo[i]);
    }

    // ---- Guitar-string shimmer ---------------------------------------------

    /// <summary>Half-width (px) of the traveling shimmer highlight.</summary>
    public const double ShimmerHalfWidth = 70.0;

    /// <summary>Center X of the traveling shimmer on string
    /// <paramref name="stringIndex" /> — sweeps left→right forever, staggered
    /// per string; always within [-<see cref="ShimmerHalfWidth" /> * 2,
    /// width + <see cref="ShimmerHalfWidth" /> * 2] so it enters and leaves
    /// the surface fully.</summary>
    public static double ShimmerCenterX(int stringIndex, double tSeconds, double width)
    {
        var period = 7.0 + stringIndex * 0.9; // per-string speed stagger
        var phase = (tSeconds / period + stringIndex * 0.35) % 1.0;
        if (phase < 0) phase += 1.0;
        var margin = ShimmerHalfWidth * 2;
        return phase * (width + 2 * margin) - margin;
    }

    /// <summary>Slow base-opacity breathing of a string line. Softened in Wave V4
    /// (0.04..0.11, was 0.06..0.16) so the faint neck lines recede into the scene
    /// instead of reading as flat scanlines.</summary>
    public static double StringOpacity(int stringIndex, double tSeconds)
    {
        var s = 0.5 * (1 + Math.Sin(2 * Math.PI * tSeconds / 12.0 + stringIndex * 1.1));
        return 0.04 + s * 0.07;
    }

    /// <summary>Gentle downward-right tilt (rise-over-run) of string
    /// <paramref name="stringIndex" /> of <paramref name="count" />, so the neck
    /// lines fan in a subtle perspective (Wave V4 "diagonal-integrated") instead
    /// of stacking as horizontal bands. A small base tilt plus a per-string spread
    /// around the middle — always a shallow, bounded slope.</summary>
    public static double StringSlope(int stringIndex, int count)
    {
        var center = (count - 1) / 2.0;
        return StringSlopeBase + (stringIndex - center) * StringSlopeSpread;
    }

    /// <summary>Base string tilt (vertical fraction of the surface width). ~2.8°.</summary>
    public const double StringSlopeBase = 0.05;

    /// <summary>Per-string fan added around the middle string.</summary>
    public const double StringSlopeSpread = 0.015;

    // ---- Particle field ----------------------------------------------------

    /// <summary>Advances a particle's normalized Y by its upward drift and
    /// wraps it below the bottom edge once it leaves the top.</summary>
    public static double AdvanceParticleY(double y, double speed, double dtSeconds)
    {
        var next = y - speed * dtSeconds;
        return next < -0.05 ? next + 1.10 : next;
    }

    /// <summary>Horizontal sway around the particle's base column.</summary>
    public static double ParticleX(double baseX, double swayPhase, double tSeconds)
        => baseX + Math.Sin(tSeconds * 0.35 + swayPhase) * 0.018;

    /// <summary>Twinkle alpha in [0.1, 1.0].</summary>
    public static double ParticleAlpha(double twinklePhase, double tSeconds)
    {
        var s = 0.5 * (1 + Math.Sin(tSeconds * 0.9 + twinklePhase));
        return 0.1 + s * 0.9;
    }
}
