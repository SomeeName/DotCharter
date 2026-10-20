using System.Numerics;

namespace DotCharter.Rendering.Data;

/// <summary>
///     The stage lights' shape, in FRAME FRACTIONS (u = x / width, v = y / height,
///     y down) so it is the same picture at any size. Six lamp heads sit on the
///     top edge, three each side, leaving the middle clear above the highway; each
///     throws a soft cone down to a blurred pool on the floor below the horizon.
///     Two laser fans rise from the left and right edges at horizon height and a
///     coloured haze band lies along the horizon, dimmed behind the far end of
///     the highway. Pure functions of the song time in seconds — the motion stops
///     when the transport does.
///     <para>
///         The numbers are the approved mockup's (look A, a 1600×900 frame with a
///         340 px horizon), less its lighting truss.
///     </para>
/// </summary>
public static class StageLightsShape
{
    public const int Lamps = 6;
    public const int FanBeams = 6;

    /// <summary>Where a lamp head's bright dot sits: on the frame's top edge.</summary>
    public const float HeadV = 0.007f;

    /// <summary>Cone half-width (in u) at the head and at the floor pool.</summary>
    public const float ConeTopHalfWidth = 0.007f;

    public const float ConeFloorHalfWidth = 0.055f;

    /// <summary>How far each pool swings sideways (in u) as its cone sways.</summary>
    public const float Sway = 0.03f;

    /// <summary>The haze band's centre below the horizon and its vertical σ above
    /// the centre; below it the haze falls off faster, so little of it lies behind
    /// the near lanes.</summary>
    public const float HazeOffset = 0.031f;

    public const float HazeSigma = 0.045f;

    public const float HazeBelowSigma = 0.026f;

    /// <summary>How much haze is left over the highway, and how wide (in u) the
    /// soft shoulder back to full haze is either side of it.</summary>
    public const float NotchFloor = 0.12f;

    public const float NotchShoulder = 0.16f;

    /// <summary>The laser fans fade out this far (in u) from their edge.</summary>
    public const float LaserReach = 0.42f;

    /// <summary>The fans start this far (fraction of height) below the horizon.</summary>
    public const float LaserDrop = 0.009f;

    /// <summary>The lowest a laser may point, in degrees above flat.</summary>
    public const float MinLaserDegrees = 3f;

    /// <summary>Where the horizon sits when it is off-screen (the mockup's 340 / 900).</summary>
    public const float DefaultHorizonV = 0.378f;

    private static readonly float[] HeadUs = [0.09f, 0.20f, 0.32f, 0.68f, 0.80f, 0.91f];
    private static readonly float[] TargetUs = [0.075f, 0.175f, 0.30f, 0.72f, 0.825f, 0.925f];
    private static readonly float[] SwayRates = [0.43f, 0.37f, 0.51f, 0.47f, 0.33f, 0.41f];
    private static readonly float[] SwayPhases = [0f, 2.1f, 4.0f, 1.3f, 3.4f, 5.2f];

    public static float HeadU(int lamp) => HeadUs[lamp];

    /// <summary>The floor pools' row: well below the horizon, short of the near board.</summary>
    public static float PoolV(float horizonV) => Math.Min(horizonV + 0.36f * (1f - horizonV), 0.64f);

    /// <summary>Lamp <paramref name="lamp" />'s pool centre (u): its target swayed
    /// slowly, each lamp at its own rate and phase.</summary>
    public static float PoolU(int lamp, double tSec) =>
        TargetUs[lamp] + Sway * MathF.Sin((float)(tSec * SwayRates[lamp]) + SwayPhases[lamp]);

    /// <summary>Gentle intensity breathing, 0.8–1, each lamp out of step with the next.</summary>
    public static float Breathing(int lamp, double tSec) =>
        0.9f + 0.1f * MathF.Sin((float)(tSec * 1.3) + lamp * 1.9f);

    /// <summary>The cone's alpha from the head (s = 0) to the pool (s = 1).</summary>
    public static float ConeAlong(float s) => 0.36f - 0.22f * Math.Clamp(s, 0f, 1f);

    /// <summary>The cone's soft cross-section at <paramref name="x" /> half-widths
    /// from its axis: a Gaussian that is gone by |x| = 1.3.</summary>
    public static float ConeAcross(float x)
    {
        if (MathF.Abs(x) >= 1.3f) return 0f;
        return MathF.Exp(-x * x / (2f * 0.45f * 0.45f));
    }

    /// <summary>The haze's σ on the side of its centre that row <paramref name="row" />
    /// (in σ, positive = below) lies on.</summary>
    public static float HazeSigmaFor(float row) => row > 0 ? HazeBelowSigma : HazeSigma;

    /// <summary>The haze band's vertical profile at <paramref name="v" />.</summary>
    public static float HazeProfile(float v, float horizonV)
    {
        var dv = v - (horizonV + HazeOffset);
        var d = dv / (dv > 0 ? HazeBelowSigma : HazeSigma);
        return MathF.Exp(-0.5f * d * d);
    }

    /// <summary>The haze's clumping across u: smooth value noise, 0.5–1.5, drifting
    /// slowly sideways with song time.</summary>
    public static float HazeNoise(float u, double tSec)
    {
        var x = u * 9f + (float)(tSec * 0.12);
        var i = MathF.Floor(x);
        var f = x - i;
        f = f * f * (3f - 2f * f);
        var a = Hash((int)i, 7);
        var b = Hash((int)i + 1, 7);
        return 0.5f + (a + (b - a) * f);
    }

    /// <summary>The haze over the highway: <see cref="NotchFloor" /> between its
    /// edges at that row (<paramref name="left" />, <paramref name="right" />),
    /// smoothstepped back to 1 over <see cref="NotchShoulder" /> outside them, so
    /// it never shows through the translucent lanes however wide the anchor.</summary>
    public static float HighwayNotch(float u, float left, float right)
    {
        var outside = Math.Max(Math.Max(left - u, u - right), 0f);
        var t = Math.Clamp(outside / NotchShoulder, 0f, 1f);
        t = t * t * (3f - 2f * t);
        return NotchFloor + (1f - NotchFloor) * t;
    }

    /// <summary>
    ///     Fan beam <paramref name="beam" /> of side <paramref name="side" /> (0 left,
    ///     1 right), in degrees above flat. The whole fan sweeps up and down, and
    ///     opens and closes; the two sides run half a cycle apart, so the fans
    ///     scissor across each other.
    /// </summary>
    public static float LaserDegrees(int side, int beam, double tSec)
    {
        var t = (float)tSec;
        var ph = side * MathF.PI;
        var sweep = 5f * MathF.Sin(t * 0.55f + ph);
        var spread = 1f + 0.3f * MathF.Sin(t * 0.8f + ph + 1.1f);
        return Math.Max(MinLaserDegrees, 7f + beam * 8f * spread + sweep);
    }

    /// <summary>
    ///     A fan beam's origin (on its edge, just below the horizon) and the point
    ///     where it has faded out, <see cref="LaserReach" /> across the frame.
    ///     <paramref name="aspect" /> is width / height: the fan is shaped in pixels.
    /// </summary>
    public static (Vector2 From, Vector2 To) LaserLine(int side, int beam, double tSec, float horizonV,
        float aspect)
    {
        var a = LaserDegrees(side, beam, tSec) * MathF.PI / 180f;
        var from = new Vector2(side == 0 ? 0f : 1f, horizonV + LaserDrop);
        var rise = 0.6f * MathF.Tan(a) * LaserReach * aspect;
        var to = new Vector2(side == 0 ? LaserReach : 1f - LaserReach, from.Y - rise);
        return (from, to);
    }

    /// <summary>A laser's alpha at <paramref name="s" /> (0 at its edge, 1 where it
    /// has faded out).</summary>
    public static float LaserFade(float s) => MathF.Pow(1f - Math.Clamp(s, 0f, 1f), 0.5f);

    /// <summary>A stable 0..1 hash of (i, salt).</summary>
    public static float Hash(int i, int salt)
    {
        unchecked
        {
            var h = (uint)(i * 374761393 + salt * 668265263);
            h = (h ^ (h >> 13)) * 1274126177u;
            h ^= h >> 16;
            return (h & 0xFFFFFF) / 16777215f;
        }
    }
}
