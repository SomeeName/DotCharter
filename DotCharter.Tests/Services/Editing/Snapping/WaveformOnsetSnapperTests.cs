using DotCharter.Tone.Audio.Abstractions;
using DotCharter.Services.Editing.Snapping;

namespace DotCharter.Tests.Services.Editing.Snapping;

public sealed class WaveformOnsetSnapperTests
{
    // Builds a level-0 envelope (blockMs per block) with sharp rising edges
    // (silence -> full) at the given block indices.
    private static WaveformSamples EnvelopeWithOnsetsAt(double blockMs, int length, params int[] onsetBlocks)
    {
        var env = new float[length];
        var onsetSet = new HashSet<int>(onsetBlocks);
        var level = 0f;
        for (var i = 0; i < length; i++)
        {
            if (onsetSet.Contains(i)) level = 1f;      // attack
            else if (level > 0f) level = Math.Max(0f, level - 0.05f); // slow decay
            env[i] = level;
        }
        return new WaveformSamples([env], blockMs);
    }

    [Fact]
    public void FindNearestOnset_ReturnsClosestOnsetWithinRadius()
    {
        // Onsets at block 40 (400ms) and block 80 (800ms), blockMs=10.
        var wave = EnvelopeWithOnsetsAt(10, 200, 40, 80);
        var snapper = new WaveformOnsetSnapper();

        var onset = snapper.FindNearestOnsetMs(wave, targetMs: 420, radiusMs: 60);
        Assert.NotNull(onset);
        Assert.Equal(400, onset!.Value, 0);
    }

    [Fact]
    public void FindNearestOnset_PicksNearerOfTwoOnsets()
    {
        var wave = EnvelopeWithOnsetsAt(10, 200, 40, 60); // 400ms and 600ms
        var snapper = new WaveformOnsetSnapper();

        Assert.Equal(600, snapper.FindNearestOnsetMs(wave, 560, 120)!.Value, 0);
        Assert.Equal(400, snapper.FindNearestOnsetMs(wave, 440, 120)!.Value, 0);
    }

    [Fact]
    public void FindNearestOnset_ReturnsNull_WhenNoOnsetInRadius()
    {
        var wave = EnvelopeWithOnsetsAt(10, 200, 40); // only 400ms
        var snapper = new WaveformOnsetSnapper();
        Assert.Null(snapper.FindNearestOnsetMs(wave, targetMs: 800, radiusMs: 50));
    }

    [Fact]
    public void FindNearestOnset_ReturnsNull_ForFlatSignal()
    {
        var env = new float[200];
        Array.Fill(env, 0.5f);
        var wave = new WaveformSamples([env], 10);
        Assert.Null(new WaveformOnsetSnapper().FindNearestOnsetMs(wave, 500, 100));
    }

    [Fact]
    public void FindNearestOnset_HandlesOnsetNearStartOfBuffer()
    {
        var wave = EnvelopeWithOnsetsAt(10, 200, 1); // onset at block 1 (10ms)
        var onset = new WaveformOnsetSnapper().FindNearestOnsetMs(wave, targetMs: 10, radiusMs: 50);
        Assert.Equal(10, onset!.Value, 0);
    }

    [Theory]
    [InlineData(0)]    // zero radius
    [InlineData(-10)]  // negative radius
    public void FindNearestOnset_ReturnsNull_ForNonPositiveRadius(double radius)
    {
        var wave = EnvelopeWithOnsetsAt(10, 200, 40);
        Assert.Null(new WaveformOnsetSnapper().FindNearestOnsetMs(wave, 400, radius));
    }

    [Fact]
    public void FindNearestOnset_ReturnsNull_ForNullOrEmptyWaveform()
    {
        var snapper = new WaveformOnsetSnapper();
        Assert.Null(snapper.FindNearestOnsetMs(null, 100, 50));
        Assert.Null(snapper.FindNearestOnsetMs(WaveformSamples.Empty, 100, 50));
    }
}
