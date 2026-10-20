using DotCharter.Services.Audio.Calibration;

namespace DotCharter.Tests.Services.Audio.Calibration;

public sealed class LatencyCalibrationMathTests
{
    // A player with a constant reaction/anticipation bias must land on the SAME
    // A-V offset as a player with none: the bias is common to both taps and the
    // visual−audio subtraction cancels it. This is the whole point of the two-tap
    // method, so it is the first thing that must hold.
    [Fact]
    public void ConstantPlayerBias_CancelsOut()
    {
        var visual = new[] { 18.0, 20, 22, 20, 19, 21, 20, 20, 21, 19 }; // mean 20
        var audio = new[] { -1.0, 1, 0, 2, -2, 0, 1, -1, 0, 0 };         // mean 0

        var unbiased = LatencyCalibrationMath.ComputeAvOffset(visual, audio);

        // Same player, but now anticipating by a constant 40 ms in BOTH steps.
        var visualBiased = visual.Select(v => v + 40).ToArray();
        var audioBiased = audio.Select(v => v + 40).ToArray();
        var biased = LatencyCalibrationMath.ComputeAvOffset(visualBiased, audioBiased);

        Assert.True(unbiased.Trusted);
        Assert.True(biased.Trusted);
        Assert.Equal(20.0, unbiased.AvOffsetMs, 6);
        Assert.Equal(unbiased.AvOffsetMs, biased.AvOffsetMs, 6); // bias made no difference
    }

    [Fact]
    public void GrossOutlier_IsRejected_AndDoesNotMoveTheMean()
    {
        // Eight tight taps around 10 ms plus one wild 500 ms mis-tap.
        var taps = new[] { 10.0, 11, 9, 10, 10, 11, 9, 10, 500 };

        var stats = TapAsyncStats.From(taps);

        Assert.Equal(9, stats.Count);
        Assert.Equal(8, stats.UsedCount);
        Assert.Equal(1, stats.RejectedCount);
        Assert.Equal(10.0, stats.Mean, 3);              // outlier did not drag it
        Assert.DoesNotContain(500.0, stats.Accepted);
    }

    [Fact]
    public void TinySet_KeepsEverySample()
    {
        // Below MinForOutlierRejection: no rejection even if one looks off.
        var taps = new[] { 5.0, 6, 40 };

        var stats = TapAsyncStats.From(taps);

        Assert.Equal(3, stats.UsedCount);
        Assert.Equal(0, stats.RejectedCount);
    }

    [Fact]
    public void EmptySet_IsEmpty()
    {
        var stats = TapAsyncStats.From(Array.Empty<double>());
        Assert.Equal(0, stats.Count);
        Assert.Equal(0, stats.UsedCount);
        Assert.Equal(0.0, stats.Mean);
    }

    [Fact]
    public void BelowMinTaps_IsRefused()
    {
        var fewVisual = new[] { 20.0, 19, 21, 20, 20 };                      // 5 taps
        var enoughAudio = Enumerable.Repeat(0.0, 12).ToArray();

        var result = LatencyCalibrationMath.ComputeAvOffset(fewVisual, enoughAudio);

        Assert.False(result.Trusted);
        Assert.Equal(AvOffsetUntrusted.NotEnoughTaps, result.Reason);
    }

    [Fact]
    public void OutlierRejection_CanPushAStepBelowTheMinimum()
    {
        // Exactly the minimum taps, but two are outliers → inliers fall under it.
        // (The six inliers carry realistic spread so the MAD is non-degenerate and
        // the filter actually engages — an all-identical inlier set has MAD 0 and,
        // correctly, rejects nothing.)
        var visual = new[] { 18.0, 19, 20, 21, 20, 19, 500, 500 };
        var audio = Enumerable.Repeat(0.0, 12).ToArray();

        var result = LatencyCalibrationMath.ComputeAvOffset(visual, audio, minTaps: 8);

        Assert.False(result.Trusted);
        Assert.Equal(AvOffsetUntrusted.NotEnoughTaps, result.Reason);
        Assert.Equal(6, result.Visual.UsedCount);
    }

    [Fact]
    public void PositiveOffset_MapsToVisualLead_NotAudioDelay()
    {
        var app = LatencyCalibrationMath.MapAvOffsetToConfig(30.0);

        Assert.Equal(30.0, app.VisualLeadMs, 6);
        Assert.False(app.AudioLeadsUncompensated);
        Assert.Equal(0.0, app.UncompensatedAudioLeadMs, 6);
    }

    [Fact]
    public void NegativeOffset_ClampsVisualLeadToZero_AndFlagsAudioLead()
    {
        // Picture leads sound: we refuse to auto-delay audio (it would bias judging).
        var app = LatencyCalibrationMath.MapAvOffsetToConfig(-15.0);

        Assert.Equal(0.0, app.VisualLeadMs, 6);
        Assert.True(app.AudioLeadsUncompensated);
        Assert.Equal(15.0, app.UncompensatedAudioLeadMs, 6);
    }

    [Fact]
    public void ImplausibleOffset_IsClampedToCeiling()
    {
        var app = LatencyCalibrationMath.MapAvOffsetToConfig(5000.0);
        Assert.Equal(LatencyCalibrationMath.MaxVisualLeadMs, app.VisualLeadMs, 6);
    }
}
