using DotCharter.UI.Controls;

namespace DotCharter.Tests.UI;

/// <summary>
///     Pure-math contract of the Play Mode menu backdrop
///     (<c>MenuBackdropControl</c>): bar travel stays in range, smoothing is
///     asymmetric + convergent, glow/shimmer/particle motion stays bounded.
///     No Avalonia instance required (same pattern as <c>KnobMath</c>).
/// </summary>
public sealed class MenuBackdropMathTests
{
    // ---- intensity profiles (0.16 visual pass) -------------------------------

    [Fact]
    public void ProfileFor_Full_IsTheUnscaledFrontDoorScene()
    {
        var p = MenuBackdropMath.ProfileFor(MenuBackdropIntensity.Full);
        Assert.Equal(1.0, p.GlowOpacityFactor);
        Assert.Equal(MenuBackdropMath.MaxParticleCount, p.ParticleCount);
        Assert.Equal(1.0, p.ParticleSpeedFactor);
        Assert.Equal(1.0, p.StringOpacityFactor);
        // The EQ bars are OFF in both profiles now. They were twenty
        // audio-reactive columns along the bottom of the launch screen and they
        // landed directly behind the LAST SESSION strip — the one place the
        // scene has text to read was the one place it put moving bars. The owner
        // asked for them gone and the strip kept.
        Assert.False(p.ShowEqBars, "the launch screen's EQ bars were removed on purpose");
        Assert.Equal(0.0, p.SceneDimOpacity); // no readability dim on the menu
    }

    [Fact]
    public void ProfileFor_Ambient_IsStrictlyCalmerThanFull_AndDropsTheEqBars()
    {
        var full = MenuBackdropMath.ProfileFor(MenuBackdropIntensity.Full);
        var ambient = MenuBackdropMath.ProfileFor(MenuBackdropIntensity.Ambient);

        Assert.True(ambient.GlowOpacityFactor < full.GlowOpacityFactor);
        Assert.True(ambient.ParticleCount < full.ParticleCount);
        Assert.True(ambient.ParticleSpeedFactor < full.ParticleSpeedFactor);
        Assert.True(ambient.StringOpacityFactor < full.StringOpacityFactor);
        Assert.True(ambient.GlowDriftAmplitude < full.GlowDriftAmplitude);
        Assert.False(ambient.ShowEqBars, "no EQ bars behind content screens");
        Assert.True(ambient.SceneDimOpacity > 0, "content screens need the readability dim");
    }

    [Fact]
    public void ProfileFor_AllIntensities_StayWithinRenderableRanges()
    {
        foreach (var intensity in new[] { MenuBackdropIntensity.Full, MenuBackdropIntensity.Ambient })
        {
            var p = MenuBackdropMath.ProfileFor(intensity);
            Assert.InRange(p.GlowOpacityFactor, 0.0, 1.0);
            Assert.InRange(p.ParticleCount, 0, MenuBackdropMath.MaxParticleCount); // never exceeds the pool
            Assert.InRange(p.ParticleSpeedFactor, 0.0, 1.0);
            Assert.InRange(p.StringOpacityFactor, 0.0, 1.0);
            Assert.InRange(p.GlowDriftAmplitude, 0.0, 0.05);
            Assert.InRange(p.SceneDimOpacity, 0.0, 1.0);
        }
    }

    // ---- idle EQ bars -------------------------------------------------------

    [Fact]
    public void IdleBarHeight_StaysWithinGlobalRange_ForAllBarsAndTimes()
    {
        for (var bar = 0; bar < 20; bar++)
        {
            for (var t = 0.0; t < 12.0; t += 0.05)
            {
                var h = MenuBackdropMath.IdleBarHeight(bar, t);
                Assert.InRange(h, MenuBackdropMath.BarMinHeight, MenuBackdropMath.BarMaxHeight);
            }
        }
    }

    [Fact]
    public void IdleBarHeight_NeighbouringBars_AreNotInLockstep()
    {
        // The phase stagger must desynchronize neighbours — a flat visualizer
        // (all bars equal) would mean the stagger regressed.
        var h0 = MenuBackdropMath.IdleBarHeight(0, 1.0);
        var h1 = MenuBackdropMath.IdleBarHeight(1, 1.0);
        var h2 = MenuBackdropMath.IdleBarHeight(2, 1.0);
        Assert.False(Math.Abs(h0 - h1) < 0.001 && Math.Abs(h1 - h2) < 0.001);
    }

    [Fact]
    public void SmoothBarHeight_AttackIsFasterThanRelease()
    {
        var up = MenuBackdropMath.SmoothBarHeight(20, 80) - 20;     // rising step
        var down = 80 - MenuBackdropMath.SmoothBarHeight(80, 20);   // falling step
        Assert.True(up > down, $"attack step {up} must exceed release step {down}");
    }

    [Fact]
    public void SmoothBarHeight_ConvergesToTarget()
    {
        var h = 12.0;
        for (var i = 0; i < 200; i++) h = MenuBackdropMath.SmoothBarHeight(h, 90);
        Assert.Equal(90, h, 3);
    }

    // ---- audio-reactive EQ bars (5.3) ----------------------------------------

    [Fact]
    public void MapPeak_SilenceIsZero_LoudSaturates_AndIsMonotonic()
    {
        Assert.Equal(0.0, MenuBackdropMath.MapPeak(0f));
        Assert.Equal(1.0, MenuBackdropMath.MapPeak(0.25f)); // meter curve saturates at peak*4 = 1
        Assert.Equal(1.0, MenuBackdropMath.MapPeak(1f));

        var previous = 0.0;
        for (var p = 0f; p <= 0.3f; p += 0.01f)
        {
            var mapped = MenuBackdropMath.MapPeak(p);
            Assert.True(mapped >= previous, "MapPeak must be monotonic");
            Assert.InRange(mapped, 0.0, 1.0);
            previous = mapped;
        }
    }

    [Fact]
    public void AudioEnvelope_AttacksInstantly_ReleasesGradually()
    {
        // Attack: a pluck registers on the very next frame.
        Assert.Equal(0.8, MenuBackdropMath.AudioEnvelope(0.1, 0.8));

        // Release: silence decays smoothly, never jumps to zero.
        var env = 1.0;
        var next = MenuBackdropMath.AudioEnvelope(env, 0.0);
        Assert.True(next is > 0.5 and < 1.0, $"one-tick release fell to {next}");

        // ...but does die out (idle fallback becomes reachable).
        for (var i = 0; i < 300; i++) env = MenuBackdropMath.AudioEnvelope(env, 0.0);
        Assert.False(MenuBackdropMath.IsAudioLive(env));
    }

    [Fact]
    public void NextAudioBlend_RampsInFasterThanOut_AndStaysClamped()
    {
        var up = MenuBackdropMath.NextAudioBlend(0.5, audioLive: true) - 0.5;
        var down = 0.5 - MenuBackdropMath.NextAudioBlend(0.5, audioLive: false);
        Assert.True(up > down, "blend must ramp in faster than it falls back");

        var blend = 0.0;
        for (var i = 0; i < 100; i++) blend = MenuBackdropMath.NextAudioBlend(blend, true);
        Assert.Equal(1.0, blend);
        for (var i = 0; i < 200; i++) blend = MenuBackdropMath.NextAudioBlend(blend, false);
        Assert.Equal(0.0, blend);
    }

    [Fact]
    public void AudioBarTarget_StaysWithinBarRange_ForAnyEnvelope()
    {
        foreach (var envelope in new[] { 0.0, 0.3, 1.0, 5.0 /* clamped */ })
        {
            for (var bar = 0; bar < 20; bar++)
            {
                for (var t = 0.0; t < 5.0; t += 0.07)
                {
                    var h = MenuBackdropMath.AudioBarTarget(bar, 20, envelope, t);
                    Assert.InRange(h, MenuBackdropMath.BarMinHeight, MenuBackdropMath.BarMaxHeight);
                }
            }
        }
    }

    [Fact]
    public void AudioBarTarget_LouderSignal_RaisesTheCenterBars()
    {
        var quiet = MenuBackdropMath.AudioBarTarget(10, 20, 0.1, 1.0);
        var loud = MenuBackdropMath.AudioBarTarget(10, 20, 0.9, 1.0);
        Assert.True(loud > quiet, $"loud target {loud} must exceed quiet target {quiet}");
    }

    [Fact]
    public void BlendBarTarget_IsIdleAtZero_AudioAtOne_AndClamps()
    {
        Assert.Equal(40.0, MenuBackdropMath.BlendBarTarget(40, 90, 0.0));
        Assert.Equal(90.0, MenuBackdropMath.BlendBarTarget(40, 90, 1.0));
        Assert.Equal(65.0, MenuBackdropMath.BlendBarTarget(40, 90, 0.5));
        Assert.Equal(90.0, MenuBackdropMath.BlendBarTarget(40, 90, 3.0)); // clamped
    }

    // ---- glows --------------------------------------------------------------

    [Fact]
    public void GlowDrift_StaysWithinAmplitude()
    {
        for (var glow = 0; glow < 3; glow++)
        {
            for (var t = 0.0; t < 100.0; t += 0.5)
            {
                var (dx, dy) = MenuBackdropMath.GlowDrift(glow, t, 0.05);
                Assert.InRange(dx, -0.05, 0.05);
                Assert.InRange(dy, -0.05, 0.05);
            }
        }
    }

    [Fact]
    public void GlowOpacity_StaysWithinItsBreathingRange()
    {
        for (var glow = 0; glow < 3; glow++)
        {
            for (var t = 0.0; t < 80.0; t += 0.25)
            {
                var o = MenuBackdropMath.GlowOpacity(glow, t);
                Assert.InRange(o, 0.40, 0.95);
            }
        }
    }

    // ---- strings ------------------------------------------------------------

    [Fact]
    public void ShimmerCenterX_StaysWithinTheOffscreenMargins()
    {
        const double width = 1920;
        var margin = MenuBackdropMath.ShimmerHalfWidth * 2;
        for (var s = 0; s < 6; s++)
        {
            for (var t = 0.0; t < 60.0; t += 0.1)
            {
                var x = MenuBackdropMath.ShimmerCenterX(s, t, width);
                Assert.InRange(x, -margin, width + margin);
            }
        }
    }

    [Fact]
    public void StringOpacity_StaysWithinTheSoftenedRange()
    {
        // Wave V4 softened the neck lines to 0.04..0.11 (was 0.06..0.16) so they
        // recede into the scene instead of reading as scanlines.
        for (var s = 0; s < 6; s++)
        {
            for (var t = 0.0; t < 30.0; t += 0.1)
                Assert.InRange(MenuBackdropMath.StringOpacity(s, t), 0.04, 0.111);
        }
    }

    [Fact]
    public void StringSlope_IsAShallowBoundedTilt_ThatFansAroundTheMiddle()
    {
        const int count = 4;
        var slopes = new double[count];
        for (var i = 0; i < count; i++)
        {
            slopes[i] = MenuBackdropMath.StringSlope(i, count);
            // Shallow, downward, never steep enough to read as a wedge.
            Assert.InRange(slopes[i], 0.0, 0.12);
        }

        // Fan: strictly increasing across strings (they diverge, not stack flat).
        for (var i = 1; i < count; i++)
            Assert.True(slopes[i] > slopes[i - 1], "string slopes must fan apart");
    }

    // ---- particles ----------------------------------------------------------

    [Fact]
    public void AdvanceParticleY_DriftsUpAndWrapsBelowTheBottom()
    {
        var y = 0.5;
        var next = MenuBackdropMath.AdvanceParticleY(y, speed: 0.02, dtSeconds: 0.033);
        Assert.True(next < y, "particles drift upward (y decreases)");

        // Long simulation never escapes the wrap band.
        y = 0.02;
        for (var i = 0; i < 10_000; i++)
        {
            y = MenuBackdropMath.AdvanceParticleY(y, 0.03, 0.033);
            Assert.InRange(y, -0.05, 1.05);
        }
    }

    [Fact]
    public void ParticleX_SwayStaysNearTheBaseColumn()
    {
        for (var t = 0.0; t < 40.0; t += 0.1)
            Assert.InRange(MenuBackdropMath.ParticleX(0.5, 1.3, t), 0.48, 0.52);
    }

    [Fact]
    public void ParticleAlpha_IsAValidOpacity()
    {
        for (var t = 0.0; t < 20.0; t += 0.05)
            Assert.InRange(MenuBackdropMath.ParticleAlpha(2.0, t), 0.0, 1.0);
    }
}
