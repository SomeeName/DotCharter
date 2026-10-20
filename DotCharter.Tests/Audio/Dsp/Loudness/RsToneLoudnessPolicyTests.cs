using DotCharter.Tone.Audio.Dsp.Loudness;

namespace DotCharter.Tests.Audio.Dsp.Loudness;

/// <summary>Where a measured tone lands: the reference, the peak ceiling, the gain
/// module's range, and the song-relative intent of Rocksmith's tone Volume.</summary>
public sealed class RsToneLoudnessPolicyTests
{
    [Fact]
    public void AMeasuredTone_LandsOnTheReference_PlusItsIntent()
    {
        // A driven tone: crest 12 dB, so the ceiling is far away.
        var body = new ToneLoudness(-30, -18);
        Assert.Equal(RsToneLoudnessPolicy.TargetLufs - -30, RsToneLoudnessPolicy.TrailingGainDb(body, 0f), 4);
        Assert.Equal(RsToneLoudnessPolicy.TargetLufs - -30 + 1.5, RsToneLoudnessPolicy.TrailingGainDb(body, 1.5f), 4);
    }

    [Fact]
    public void ACleanTonesPeaks_StopItUnderTheCeiling()
    {
        // Crest 24 dB: at the reference its peak would be 0 dBFS.
        var clean = new ToneLoudness(-40, -16);
        var gain = RsToneLoudnessPolicy.TrailingGainDb(clean, 0f);
        Assert.Equal(RsToneLoudnessPolicy.PeakCeilingDbfs - -16, gain, 4);
        Assert.True(-40 + gain < RsToneLoudnessPolicy.TargetLufs);
    }

    [Fact]
    public void TheGain_StaysInsideTheGainModulesRange()
    {
        Assert.Equal(RsToneLoudnessPolicy.MaxGainDb, RsToneLoudnessPolicy.TrailingGainDb(new ToneLoudness(-80, -70), 0f));
        Assert.Equal(-RsToneLoudnessPolicy.MaxGainDb, RsToneLoudnessPolicy.TrailingGainDb(new ToneLoudness(10, 20), 0f));
    }

    [Fact]
    public void Intent_IsTheVolumeAgainstTheSongsMedian_Clamped()
    {
        // The stock pair plus a hot outlier: median -18.6.
        var intents = RsToneLoudnessPolicy.IntentOffsetsDb([-19.5f, -18.6f, -10f]);
        Assert.Equal(-0.9f, intents[0], 4);
        Assert.Equal(0f, intents[1], 4);
        Assert.Equal(RsToneLoudnessPolicy.MaxIntentDb, intents[2], 4);

        // Even count: the median is the mean of the middle two.
        var even = RsToneLoudnessPolicy.IntentOffsetsDb([-20f, -18f, -16f, -30f]);
        Assert.Equal([-1f, 1f, 3f, -3f], even);
    }

    [Fact]
    public void ALoneTone_EqualVolumes_OrAMissingVolume_AskForNothing()
    {
        Assert.Equal([0f], RsToneLoudnessPolicy.IntentOffsetsDb([-14f]));
        Assert.Equal([0f, 0f], RsToneLoudnessPolicy.IntentOffsetsDb([-14f, -14f]));
        var withMissing = RsToneLoudnessPolicy.IntentOffsetsDb([float.NaN, -14f, -16f]);
        Assert.Equal(0f, withMissing[0]);
        Assert.Equal(1f, withMissing[1], 4);
    }
}
