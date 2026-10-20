using DotCharter.Rendering.Data;

namespace DotCharter.Tests.Rendering;

public class HitSparkMathTests
{
    [Fact]
    public void Evaluate_IsDeterministic()
    {
        var seed = HitSparkMath.Seed(12345.5, 2, 7);
        for (var i = 0; i < HitSparkMath.SparksPerHit; i++)
        {
            var a = HitSparkMath.Evaluate(seed, i, 0.4f);
            var b = HitSparkMath.Evaluate(seed, i, 0.4f);
            Assert.Equal(a, b);
        }
    }

    [Fact]
    public void Seed_DiffersAcrossStringsAndFrets()
    {
        var s1 = HitSparkMath.Seed(1000.0, 1, 5);
        var s2 = HitSparkMath.Seed(1000.0, 2, 5);
        var s3 = HitSparkMath.Seed(1000.0, 1, 6);
        Assert.NotEqual(s1, s2);
        Assert.NotEqual(s1, s3);
    }

    [Fact]
    public void Alpha_FadesOutCompletelyByEndOfLife()
    {
        var seed = HitSparkMath.Seed(500.0, 0, 3);
        for (var i = 0; i < HitSparkMath.SparksPerHit; i++)
        {
            Assert.Equal(1f, HitSparkMath.Evaluate(seed, i, 0f).Alpha);
            Assert.Equal(0f, HitSparkMath.Evaluate(seed, i, 1f).Alpha);
        }
    }

    [Fact]
    public void Sparks_StartAtTheHitOrigin()
    {
        var seed = HitSparkMath.Seed(2500.0, 4, 12);
        for (var i = 0; i < HitSparkMath.SparksPerHit; i++)
        {
            var s = HitSparkMath.Evaluate(seed, i, 0f);
            Assert.Equal(0f, s.Dx, 3);
            Assert.Equal(0f, s.Dy, 3);
            Assert.Equal(0f, s.Dz, 3);
        }
    }

    [Fact]
    public void Sparks_DisperseOverTime()
    {
        var seed = HitSparkMath.Seed(999.0, 3, 8);
        var positions = new HashSet<(float, float)>();
        for (var i = 0; i < HitSparkMath.SparksPerHit; i++)
        {
            var s = HitSparkMath.Evaluate(seed, i, 0.5f);
            positions.Add((MathF.Round(s.Dx, 3), MathF.Round(s.Dy, 3)));
            // Every spark must actually have left the origin.
            Assert.True(MathF.Abs(s.Dx) + MathF.Abs(s.Dy) > 0.01f);
        }

        // No two sparks share a position — the burst reads as a spray.
        Assert.Equal(HitSparkMath.SparksPerHit, positions.Count);
    }

    [Fact]
    public void Gravity_PullsSparksDownTowardEndOfLife()
    {
        var seed = HitSparkMath.Seed(4242.0, 1, 1);
        for (var i = 0; i < HitSparkMath.SparksPerHit; i++)
        {
            // The gravity term (-1.6 t²) dominates late: dy at t=1 is lower
            // than the pure travel apex would be.
            var late = HitSparkMath.Evaluate(seed, i, 1f);
            var mid = HitSparkMath.Evaluate(seed, i, 0.55f);
            Assert.True(late.Dy < mid.Dy + 0.4f);
        }
    }

    [Fact]
    public void Size_ShrinksButNeverInverts()
    {
        var seed = HitSparkMath.Seed(77.0, 5, 0);
        for (var i = 0; i < HitSparkMath.SparksPerHit; i++)
        {
            var start = HitSparkMath.Evaluate(seed, i, 0f);
            var end = HitSparkMath.Evaluate(seed, i, 1f);
            Assert.True(end.HalfSize < start.HalfSize);
            Assert.True(end.HalfSize > 0f);
        }
    }

    [Fact]
    public void Evaluate_ClampsAgeOutsideLifetime()
    {
        var seed = HitSparkMath.Seed(1.0, 0, 0);
        Assert.Equal(HitSparkMath.Evaluate(seed, 0, 0f), HitSparkMath.Evaluate(seed, 0, -0.5f));
        Assert.Equal(HitSparkMath.Evaluate(seed, 0, 1f), HitSparkMath.Evaluate(seed, 0, 1.5f));
    }
}
