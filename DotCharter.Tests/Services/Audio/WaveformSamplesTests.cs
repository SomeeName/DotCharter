using DotCharter.Tone.Audio.Abstractions;

namespace DotCharter.Tests.Services.Audio;

/// <summary>
///     The waveform pyramid's two derivations. <c>Mipmaps</c> (max-of-pair)
///     carries the transients; <c>Body</c> (mean-of-pair over the same base
///     array) carries the loudness a max-only pyramid saturates away. Both are
///     derived from the ONE persisted base level, so these are the rules the
///     disk cache and the live generator share by construction.
/// </summary>
public sealed class WaveformSamplesTests
{
    private static float[] Ramp(int n, int seed = 7)
    {
        var rng = new Random(seed);
        var peaks = new float[n];
        for (var i = 0; i < n; i++) peaks[i] = (float)rng.NextDouble();
        return peaks;
    }

    [Fact]
    public void BodyPyramid_IsTheMeanOverTheSpanThePeakPyramidMaxes()
    {
        // Power-of-two length so every level halves exactly and each cell
        // covers a COMPLETE 2^level span (the odd-tail rule is pinned
        // separately below).
        var peaks = Ramp(1024);
        var samples = WaveformSamples.FromBasePeaks(peaks, 2.0);

        Assert.Equal(samples.Mipmaps.Length, samples.Body.Length);
        // Level 0 is the base array itself, shared by both pyramids (max and
        // mean of a single block are the same number).
        Assert.Same(peaks, samples.Body[0]);

        for (var level = 1; level < samples.Body.Length; level++)
        {
            var span = 1 << level;
            var body = samples.Body[level];
            Assert.Equal(samples.Mipmaps[level].Length, body.Length);

            for (var i = 0; i < body.Length; i++)
            {
                double sum = 0;
                for (var k = 0; k < span; k++) sum += peaks[i * span + k];
                Assert.Equal(sum / span, body[i], 4);

                // …and the peak pyramid maxes over exactly the same span.
                var max = 0f;
                for (var k = 0; k < span; k++) max = Math.Max(max, peaks[i * span + k]);
                Assert.Equal(max, samples.Mipmaps[level][i], 4);
            }
        }
    }

    [Fact]
    public void BodyPyramid_HalvesItsOwnLevelAndCarriesTheOddTail()
    {
        // Same shape as the peak pyramid's halving rule
        // (WaveformDiskCacheTests.FromBasePeaks_MatchesTheHistoricInlinePyramid)
        // so the two stay the same length level for level.
        var peaks = Ramp(1001, seed: 3);
        var samples = WaveformSamples.FromBasePeaks(peaks, 2.0);

        for (var level = 1; level < samples.Body.Length; level++)
        {
            var prev = samples.Body[level - 1];
            var cur = samples.Body[level];
            Assert.Equal((prev.Length + 1) / 2, cur.Length);
            for (var i = 0; i < cur.Length; i++)
            {
                var a = prev[2 * i];
                var b = 2 * i + 1 < prev.Length ? prev[2 * i + 1] : a;
                Assert.Equal(0.5f * (a + b), cur[i], 4);
            }
        }
    }

    [Fact]
    public void TheBodyNeverExceedsThePeak_AtEveryLevel()
    {
        var samples = WaveformSamples.FromBasePeaks(Ramp(777, seed: 11), 2.0);

        for (var level = 0; level < samples.Mipmaps.Length; level++)
        {
            var peak = samples.Mipmaps[level];
            var body = samples.Body[level];
            for (var i = 0; i < peak.Length; i++)
            {
                Assert.True(body[i] <= peak[i] + 1e-6f,
                    $"level {level} cell {i}: body {body[i]} exceeded peak {peak[i]}.");
            }
        }
    }

    [Fact]
    public void OnALoudPassageWithOneTransient_TheBodyStaysLowWhileThePeakSaturates()
    {
        // The whole point of the second pyramid: a quiet passage containing a
        // single loud attack reads as FULL SCALE in a max-only pyramid the
        // moment one cell covers the attack. The body still reports the quiet.
        var peaks = new float[512];
        for (var i = 0; i < peaks.Length; i++) peaks[i] = 0.1f;
        peaks[100] = 1.0f;

        var samples = WaveformSamples.FromBasePeaks(peaks, 2.0);
        var level = samples.Mipmaps.Length - 1; // coarsest — one cell spans many blocks
        var cell = 100 >> level;

        Assert.Equal(1.0f, samples.Mipmaps[level][cell], 4);
        Assert.True(samples.Body[level][cell] < 0.25f,
            $"body {samples.Body[level][cell]} should stay near the 0.1 floor, not follow the transient.");
    }

    [Fact]
    public void AHandBuiltSamples_WithNoBodyPyramid_FallsBackToThePeaks()
    {
        // The record's third parameter is optional so existing two-argument
        // construction (tests, fakes) keeps compiling — it must degrade to the
        // peak envelope, never to a null deref or a short array.
        var peaks = Ramp(64);
        var samples = new WaveformSamples([peaks], 20.0);

        Assert.Same(samples.Mipmaps, samples.Body);
        Assert.Same(peaks, samples.Body[0]);
    }

    [Theory]
    // A 20 ms-base pyramid answers level 0 for a 20 ms analysis window …
    [InlineData(20.0, 20.0, 0)]
    // … and a 2 ms-base one answers the level nearest that width (16 ms, the
    // closer octave — 32 ms is the same absolute distance but a worse match).
    [InlineData(2.0, 20.0, 3)]
    [InlineData(2.0, 2.0, 0)]
    [InlineData(2.0, 1000.0, 8)]
    public void PickLevelNearest_PinsAnAnalysisWidthIndependentlyOfTheBaseBlock(
        double baseBlockMs, double targetMs, int expectedLevel)
    {
        var samples = WaveformSamples.FromBasePeaks(Ramp(8192), baseBlockMs);
        Assert.Equal(expectedLevel, samples.PickLevelNearest(targetMs));
    }

    [Fact]
    public void PickLevelNearest_OnASingleLevelPyramid_IsAlwaysLevelZero()
    {
        var samples = new WaveformSamples([Ramp(40)], 10.0);
        Assert.Equal(0, samples.PickLevelNearest(20.0));
        Assert.Equal(0, samples.PickLevelNearest(0.001));
    }
}
