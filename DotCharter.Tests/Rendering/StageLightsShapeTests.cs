using System.Numerics;
using DotCharter.Rendering.Data;

namespace DotCharter.Tests.Rendering;

/// <summary>
///     The stage lights' geometry and motion: heads on the top edge clear of the
///     middle, pools on the floor short of the near board, lasers rising from the
///     edges, haze dipping behind the far end of the highway, and every motion a
///     function of song time that actually moves.
/// </summary>
public class StageLightsShapeTests
{
    private const float Horizon = 0.378f;

    [Fact]
    public void The_heads_sit_on_the_top_edge_three_per_side_with_the_middle_clear()
    {
        Assert.InRange(StageLightsShape.HeadV, 0f, 0.02f);
        var left = 0;
        var right = 0;
        for (var i = 0; i < StageLightsShape.Lamps; i++)
        {
            var u = StageLightsShape.HeadU(i);
            Assert.False(u is > 0.38f and < 0.62f, $"lamp {i} at u={u} hangs over the highway");
            if (u < 0.5f) left++;
            else right++;
        }

        Assert.Equal(3, left);
        Assert.Equal(3, right);
    }

    [Fact]
    public void The_pools_land_on_the_floor_below_the_horizon_and_above_the_near_board()
    {
        foreach (var hz in new[] { 0.25f, Horizon, 0.5f, 0.7f })
        {
            var v = StageLightsShape.PoolV(hz);
            Assert.True(v > hz || v >= 0.64f, $"pool v={v} is above the horizon {hz}");
            Assert.True(v <= 0.64f, $"pool v={v} reaches the near board");
        }

        for (var t = 0.0; t < 120; t += 1.3)
        {
            for (var i = 0; i < StageLightsShape.Lamps; i++)
            {
                var u = StageLightsShape.PoolU(i, t);
                var halfWidth = StageLightsShape.ConeFloorHalfWidth * 1.5f;
                Assert.False(u + halfWidth > 0.42f && u - halfWidth < 0.58f,
                    $"pool {i} at u={u} swings in behind the highway at {t} s");
            }
        }
    }

    [Fact]
    public void The_cones_sway_each_at_its_own_phase()
    {
        for (var i = 0; i < StageLightsShape.Lamps; i++)
        {
            var min = float.MaxValue;
            var max = float.MinValue;
            for (var t = 0.0; t < 30; t += 0.1)
            {
                var u = StageLightsShape.PoolU(i, t);
                min = Math.Min(min, u);
                max = Math.Max(max, u);
            }

            Assert.True(max - min > StageLightsShape.Sway, $"lamp {i} swings only {max - min}");
            Assert.True(max - min <= 2 * StageLightsShape.Sway + 1e-4f);
        }

        // Never in lockstep: at one moment the pools sit at different points of their swing.
        var offsets = Enumerable.Range(0, StageLightsShape.Lamps)
            .Select(i => StageLightsShape.PoolU(i, 4.2) - StageLightsShape.PoolU(i, 0)).ToArray();
        Assert.True(offsets.Max() - offsets.Min() > 0.01f, "the cones sway in lockstep");
    }

    [Fact]
    public void The_lights_breathe_gently()
    {
        var min = float.MaxValue;
        var max = float.MinValue;
        for (var t = 0.0; t < 20; t += 0.05)
        {
            var b = StageLightsShape.Breathing(2, t);
            min = Math.Min(min, b);
            max = Math.Max(max, b);
        }

        Assert.InRange(min, 0.75f, 0.85f);
        Assert.InRange(max, 0.95f, 1f);
        Assert.NotEqual(StageLightsShape.Breathing(0, 3), StageLightsShape.Breathing(1, 3));
    }

    [Fact]
    public void The_cones_are_soft_at_the_edges_and_fade_toward_the_floor()
    {
        Assert.Equal(1f, StageLightsShape.ConeAcross(0f));
        Assert.Equal(0f, StageLightsShape.ConeAcross(1.3f));
        Assert.True(StageLightsShape.ConeAcross(0.9f) < 0.2f);
        Assert.True(StageLightsShape.ConeAlong(1f) < StageLightsShape.ConeAlong(0f) * 0.5f);
        Assert.True(StageLightsShape.ConeAlong(0f) <= 0.4f, "the cones stay transparent");
    }

    [Fact]
    public void The_laser_fans_rise_from_the_edges_and_fade_before_the_far_side()
    {
        for (var t = 0.0; t < 40; t += 0.37)
        {
            for (var side = 0; side < 2; side++)
            {
                for (var b = 0; b < StageLightsShape.FanBeams; b++)
                {
                    var (from, to) = StageLightsShape.LaserLine(side, b, t, Horizon, 16f / 9f);
                    Assert.Equal(side == 0 ? 0f : 1f, from.X);
                    Assert.InRange(from.Y, Horizon, Horizon + 0.02f);
                    Assert.True(to.Y < from.Y, "a laser rises");
                    Assert.True(StageLightsShape.LaserDegrees(side, b, t) >= StageLightsShape.MinLaserDegrees);
                    Assert.Equal(StageLightsShape.LaserReach, MathF.Abs(to.X - from.X), 4);
                }
            }
        }

        Assert.Equal(1f, StageLightsShape.LaserFade(0f));
        Assert.Equal(0f, StageLightsShape.LaserFade(1f));
    }

    [Fact]
    public void The_laser_fans_sweep_and_scissor()
    {
        // Sweep: a beam's angle travels over time.
        var angles = Enumerable.Range(0, 200).Select(i => StageLightsShape.LaserDegrees(0, 0, i * 0.1)).ToArray();
        Assert.True(angles.Max() - angles.Min() > 6f, $"the fan barely sweeps ({angles.Min()}° → {angles.Max()}°)");

        // Scissor: the two fans move against each other.
        var left = StageLightsShape.LaserDegrees(0, 2, 1.0) - StageLightsShape.LaserDegrees(0, 2, 0.5);
        var right = StageLightsShape.LaserDegrees(1, 2, 1.0) - StageLightsShape.LaserDegrees(1, 2, 0.5);
        Assert.True(left * right < 0, $"the fans move together ({left}, {right})");

        // The fan opens and closes: the spread between its outer beams changes.
        static float Spread(double t) => StageLightsShape.LaserDegrees(0, StageLightsShape.FanBeams - 1, t) -
                                         StageLightsShape.LaserDegrees(0, 0, t);
        var spreads = Enumerable.Range(0, 80).Select(i => Spread(i * 0.1)).ToArray();
        Assert.True(spreads.Max() - spreads.Min() > 10f, "the fan never opens or closes");
    }

    [Fact]
    public void The_haze_hugs_the_horizon_and_dips_behind_the_highway()
    {
        Assert.Equal(1f, StageLightsShape.HazeProfile(Horizon + StageLightsShape.HazeOffset, Horizon), 4);
        Assert.True(StageLightsShape.HazeProfile(Horizon - 0.15f, Horizon) < 0.01f);
        Assert.True(StageLightsShape.HazeProfile(Horizon + StageLightsShape.HazeOffset + 0.05f, Horizon) <
                    StageLightsShape.HazeProfile(Horizon + StageLightsShape.HazeOffset - 0.05f, Horizon) * 0.5f,
            "below the horizon the haze falls off faster than above it");
        for (var u = 0f; u <= 1f; u += 1f / 64)
            Assert.InRange(StageLightsShape.HazeNoise(u, 7.7), 0.5f, 1.5f);
        Assert.NotEqual(StageLightsShape.HazeNoise(0.3f, 0), StageLightsShape.HazeNoise(0.3f, 4));
    }

    [Theory]
    [InlineData(0.46f, 0.54f)]
    [InlineData(0.30f, 0.72f)]
    [InlineData(0.05f, 0.95f)]
    public void The_haze_notches_out_over_the_whole_highway_however_wide(float left, float right)
    {
        for (var u = left; u <= right; u += 0.005f)
            Assert.InRange(StageLightsShape.HighwayNotch(u, left, right), 0f, 0.2f);
        Assert.Equal(1f, StageLightsShape.HighwayNotch(left - StageLightsShape.NotchShoulder, left, right), 4);
        Assert.Equal(1f, StageLightsShape.HighwayNotch(right + StageLightsShape.NotchShoulder, left, right), 4);
        var mid = StageLightsShape.HighwayNotch(right + StageLightsShape.NotchShoulder / 2, left, right);
        Assert.InRange(mid, 0.3f, 0.9f);
    }

    [Fact]
    public void The_haze_fades_into_the_notch_gradually_not_in_a_step()
    {
        const float left = 0.4f, right = 0.6f, step = 0.01f;
        for (var u = 0f; u < 1f; u += step)
        {
            var jump = MathF.Abs(StageLightsShape.HighwayNotch(u + step, left, right) -
                StageLightsShape.HighwayNotch(u, left, right));
            Assert.True(jump <= 0.1f, $"the haze jumps by {jump} at u = {u}");
        }
    }

    [Fact]
    public void The_highway_span_follows_its_edges_down_the_screen()
    {
        var span = new HighwaySpan(new Vector2(0.45f, 0.5f), new Vector2(0.35f, 0.7f),
            new Vector2(0.55f, 0.5f), new Vector2(0.70f, 0.7f));
        var (lf, rf) = span.At(0.3f); // above the far end: the far width, widened to the minimum
        Assert.Equal(0.5f - HighwaySpan.MinHalfWidth, lf, 4);
        Assert.Equal(0.5f + HighwaySpan.MinHalfWidth, rf, 4);
        var (l, r) = span.At(0.6f);
        Assert.Equal(0.40f, l, 4);
        Assert.Equal(0.625f, r, 4);
        var (l2, r2) = span.At(0.9f); // past the near point: the edges keep widening
        Assert.True(l2 < 0.35f && r2 > 0.70f);
    }

    [Fact]
    public void Same_song_time_same_picture()
    {
        for (var i = 0; i < StageLightsShape.Lamps; i++)
        {
            Assert.Equal(StageLightsShape.PoolU(i, 12.34), StageLightsShape.PoolU(i, 12.34));
            Assert.Equal(StageLightsShape.LaserLine(i % 2, i, 12.34, Horizon, 1.6f),
                StageLightsShape.LaserLine(i % 2, i, 12.34, Horizon, 1.6f));
        }
    }
}
