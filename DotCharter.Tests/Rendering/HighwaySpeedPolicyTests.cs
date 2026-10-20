using DotCharter.Core.Graphics;

namespace DotCharter.Tests.Rendering;

/// <summary>
///     The highway's viewing geometry, which used to be compile-time constants
///     folded into ~25 call sites (and so could not be changed by anyone).
///
///     The direction of scroll speed is the thing most likely to be "fixed"
///     backwards by a future reader: in Rocksmith units, LOWER IS FASTER. That
///     is not our choice — it is what a CDLC's manifest stores and what DLC
///     Builder's tooltip says — and getting it wrong would make every imported
///     chart scroll the opposite of how its charter authored it.
/// </summary>
public sealed class HighwaySpeedPolicyTests
{
    // The values the constants used to hold, hardcoded here on purpose: if the
    // policy ever stops reproducing them at the defaults, the highway silently
    // changed for every existing user.
    private const float LegacyTimeToZScale = 0.02f;
    private const float LegacyFadeEndZ = 35f;
    private const float LegacyFadeStartZ = 20f;
    private const float LegacyHorizonMs = 1750f; // = 35 / 0.02, never actually named before

    [Fact]
    public void AtTheHistoricalDefaults_TheGeometryIsUnchanged()
    {
        Assert.Equal(LegacyTimeToZScale, HighwaySpeedPolicy.TimeToZScale(1f), 5);
        Assert.Equal(LegacyFadeEndZ, HighwaySpeedPolicy.ClampDrawDistanceZ(HighwaySpeedPolicy.DefaultDrawDistanceZ), 3);
        Assert.Equal(LegacyFadeStartZ, HighwaySpeedPolicy.FadeStartZ(HighwaySpeedPolicy.DefaultDrawDistanceZ), 3);
        Assert.Equal(LegacyHorizonMs, HighwaySpeedPolicy.HorizonMs(1f, LegacyFadeEndZ), 1);
    }

    /// <summary>LOWER scroll speed = FASTER highway = MORE space between notes.
    /// Inverting this would silently reverse every imported CDLC.</summary>
    [Fact]
    public void LowerScrollSpeed_SpreadsNotesOutAndShortensTheHorizon()
    {
        var fast = HighwaySpeedPolicy.TimeToZScale(0.5f);
        var slow = HighwaySpeedPolicy.TimeToZScale(3.0f);

        // More world units per ms == the same two notes sit further apart.
        Assert.True(fast > slow, "a LOWER scroll speed must spread notes further apart");

        // ...and therefore less music fits on a board of fixed length.
        Assert.True(
            HighwaySpeedPolicy.HorizonMs(0.5f, 35f) < HighwaySpeedPolicy.HorizonMs(3.0f, 35f),
            "a LOWER scroll speed must show FEWER ms of chart");
    }

    [Fact]
    public void ScrollSpeedAndDrawDistance_MultiplyIntoTheHorizon()
    {
        // horizon = drawDistance * scrollSpeed / 0.02 == drawDistance * scrollSpeed * 50
        Assert.Equal(35f * 1.3f * 50f, HighwaySpeedPolicy.HorizonMs(1.3f, 35f), 1);
        Assert.Equal(80f * 3.0f * 50f, HighwaySpeedPolicy.HorizonMs(3.0f, 80f), 1);
    }

    /// <summary>Whatever feeds the renderer must cover this, or notes will not
    /// exist when they should already be on screen.</summary>
    [Fact]
    public void MaxHorizon_IsTheWorstCaseAFeedMustCover()
    {
        Assert.Equal(12_000f, HighwaySpeedPolicy.MaxHorizonMs, 1);

        // It must dominate every legal combination of the two knobs.
        foreach (var speed in new[] { 0.5f, 1f, 1.3f, 2f, 3f })
        {
            foreach (var dist in new[] { 20f, 35f, 60f, 80f })
            {
                Assert.True(HighwaySpeedPolicy.MaxHorizonMs >= HighwaySpeedPolicy.HorizonMs(speed, dist),
                $"MaxHorizonMs must cover speed {speed} / distance {dist}");
            }
        }
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(-5f)]
    [InlineData(999f)]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    public void ScrollSpeed_IsClampedSoAConfigCannotProduceAnInfiniteHorizon(float corrupt)
    {
        var clamped = HighwaySpeedPolicy.ClampScrollSpeed(corrupt);

        Assert.InRange(clamped, HighwaySpeedPolicy.MinScrollSpeed, HighwaySpeedPolicy.MaxScrollSpeed);
        // A zero scroll speed would divide by zero into an infinite scale.
        Assert.True(float.IsFinite(HighwaySpeedPolicy.TimeToZScale(corrupt)));
        Assert.True(HighwaySpeedPolicy.TimeToZScale(corrupt) > 0f);
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(-10f)]
    [InlineData(5000f)]
    [InlineData(float.NaN)]
    public void DrawDistance_IsClamped(float corrupt)
    {
        var clamped = HighwaySpeedPolicy.ClampDrawDistanceZ(corrupt);
        Assert.InRange(clamped, HighwaySpeedPolicy.MinDrawDistanceZ, HighwaySpeedPolicy.MaxDrawDistanceZ);
        Assert.True(float.IsFinite(HighwaySpeedPolicy.HorizonMs(1f, corrupt)));
    }

    /// <summary>The fog/fade-in ramp is a FRACTION of the board, not a fixed 20
    /// units: on an 80-unit board a fixed ramp would leave notes popping into
    /// existence far out instead of fading in.</summary>
    [Fact]
    public void TheFadeRamp_ScalesWithTheBoard()
    {
        foreach (var d in new[] { 20f, 35f, 80f })
        {
            var start = HighwaySpeedPolicy.FadeStartZ(d);
            Assert.True(start < d, "the fade must begin before the board ends");
            Assert.Equal(HighwaySpeedPolicy.FadeRampRatio, start / d, 4);
        }
    }

    /// <summary>Rocksmith's own default, so an RS import scrolls the way its
    /// charter authored it.</summary>
    [Fact]
    public void TheDefaultScrollSpeed_IsRocksmiths()
        => Assert.Equal(1.3f, HighwaySpeedPolicy.DefaultScrollSpeed, 3);
}
