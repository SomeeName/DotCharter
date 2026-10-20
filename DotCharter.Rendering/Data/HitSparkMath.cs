namespace DotCharter.Rendering.Data;

/// <summary>
///     Pure math for the STATELESS string-colored hit sparks (0.15.6 / plan
///     5.7). Unlike the yellow-red particle explosions (which keep a spawn
///     list), sparks re-derive every frame from (note time, string, fret) —
///     seeks and re-renders need no bookkeeping, and a given hit always
///     produces the identical burst. Rendered by
///     <c>HitSparkRenderComponent</c> as radial-glow (mode 8) quads.
///     Unit-tested in <c>HitSparkMathTests</c>.
/// </summary>
public static class HitSparkMath
{
    public const double LifetimeMs = 420.0;
    public const int SparksPerHit = 12;

    /// <summary>One spark's offset from the hit origin + visual state at
    /// normalized age <c>t</c> (0 = hit, 1 = end of life).</summary>
    public readonly record struct Spark(float Dx, float Dy, float Dz, float HalfSize, float Alpha);

    /// <summary>Deterministic per-hit seed (mirrors the explosion seeding).</summary>
    public static int Seed(double noteTimeMs, int stringIndex, int fret) => unchecked(((int)(noteTimeMs * 1000) * 397) ^ (stringIndex * 31) ^ (fret * 17));

    /// <summary>
    ///     Evaluates spark <paramref name="index" /> of a burst at age
    ///     <paramref name="t" /> (clamped 0..1). Sparks fan mostly upward and
    ///     outward, decelerate, fall back under gravity, shrink and fade out
    ///     completely by t = 1.
    /// </summary>
    public static Spark Evaluate(int seed, int index, float t)
    {
        t = Math.Clamp(t, 0f, 1f);

        // xorshift32 off a per-spark state — allocation-free, deterministic.
        var state = unchecked((uint)(seed ^ (index + 1) * 0x9E3779B9) | 1u);
        float Next()
        {
            state ^= state << 13;
            state ^= state >> 17;
            state ^= state << 5;
            return (state & 0xFFFFFFu) / (float)0x1000000;
        }

        var r0 = Next();
        var r1 = Next();
        var r2 = Next();
        var r3 = Next();

        // Fan: full horizontal spread, biased upward.
        var angle = (r0 - 0.5f) * 2.4f; // radians around straight-up
        var speed = 0.9f + 1.1f * r1;
        var vx = MathF.Sin(angle) * speed;
        var vy = MathF.Cos(angle) * speed * (0.55f + 0.45f * r2);
        var vz = (r3 - 0.5f) * 0.5f;

        // Decelerating travel + gravity pull-back.
        var travel = 1f - (1f - t) * (1f - t); // ease-out
        const float gravity = 1.6f;
        var dx = vx * travel;
        var dy = vy * travel - gravity * t * t;
        var dz = vz * travel;

        var alpha = 1f - t;
        var halfSize = 0.055f * (1f - 0.55f * t);

        return new Spark(dx, dy, dz, halfSize, alpha);
    }
}
