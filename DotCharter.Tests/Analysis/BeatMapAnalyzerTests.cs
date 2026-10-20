using DotCharter.Analysis.BeatMap;

namespace DotCharter.Tests.Analysis;

public sealed class BeatMapAnalyzerTests
{
    private const int Rate = AnalysisTestSignals.SampleRate;

    /// <summary>Constant 120 BPM click track, clicks from 0.5 s, every 4th
    /// click (the downbeat) accented.</summary>
    private static float[] ConstantClickTrack(double seconds = 30.0, double bpm = 120.0)
    {
        var buffer = new float[(int)(seconds * Rate)];
        var interval = 60.0 / bpm;
        var index = 0;
        for (var t = 0.5; t < seconds - interval; t += interval, index++)
            AnalysisTestSignals.AddClick(buffer, t, index % 4 == 0 ? 0.9f : 0.45f);
        return buffer;
    }

    /// <summary>Click times for a linear tempo ramp from
    /// <paramref name="fromBpm"/> to <paramref name="toBpm"/>.</summary>
    private static (float[] Samples, List<double> ClickTimes) RampClickTrack(
        double seconds, double fromBpm, double toBpm)
    {
        var buffer = new float[(int)(seconds * Rate)];
        var clicks = new List<double>();
        var t = 0.5;
        while (t < seconds - 0.6)
        {
            clicks.Add(t);
            AnalysisTestSignals.AddClick(buffer, t, clicks.Count % 4 == 1 ? 0.9f : 0.45f);
            var bpm = fromBpm + (toBpm - fromBpm) * (t / seconds);
            t += 60.0 / bpm;
        }

        return (buffer, clicks);
    }

    private static double DistanceToNearestClick(double time, IReadOnlyList<double> clickTimes)
    {
        var best = double.MaxValue;
        foreach (var click in clickTimes)
            best = Math.Min(best, Math.Abs(time - click));
        return best;
    }

    [Fact]
    public void Analyze_ConstantClickTrack_FindsTempoBeatsAndDownbeats()
    {
        var result = new BeatMapAnalyzer().Analyze(ConstantClickTrack(), Rate);

        Assert.InRange(result.AverageBpm, 118.0, 122.0);
        Assert.InRange(result.Beats.Count, 55, 62);

        // Every beat sits on the 0.5 s click grid (constant analysis latency
        // of the flux envelope allowed for).
        foreach (var beat in result.Beats)
        {
            var offset = (beat.TimeSeconds - 0.5) % 0.5;
            var distance = Math.Min(offset, 0.5 - offset);
            Assert.True(distance < 0.035, $"beat at {beat.TimeSeconds:F3}s is {distance * 1000:F1}ms off-grid");
            Assert.InRange(beat.Confidence, 0.05f, 1f);
        }

        // Median inter-beat interval nails the 0.5 s period.
        var intervals = new List<double>();
        for (var i = 1; i < result.Beats.Count; i++)
            intervals.Add(result.Beats[i].TimeSeconds - result.Beats[i - 1].TimeSeconds);
        intervals.Sort();
        Assert.InRange(intervals[intervals.Count / 2], 0.49, 0.51);

        // Downbeats: every 4th beat, aligned with the accented clicks (2 s bar).
        var downbeats = result.Beats.Where(b => b.IsDownbeat).ToList();
        Assert.InRange(downbeats.Count, result.Beats.Count / 4 - 1, result.Beats.Count / 4 + 1);
        foreach (var downbeat in downbeats)
        {
            var offset = (downbeat.TimeSeconds - 0.5) % 2.0;
            var distance = Math.Min(offset, 2.0 - offset);
            Assert.True(distance < 0.035, $"downbeat at {downbeat.TimeSeconds:F3}s misses the accent grid");
        }
    }

    [Fact]
    public void Analyze_GradualTempoRamp_TracksDrift()
    {
        var (samples, clicks) = RampClickTrack(60.0, 100.0, 140.0);
        var result = new BeatMapAnalyzer().Analyze(samples, Rate);

        Assert.InRange(result.AverageBpm, 105.0, 135.0);
        Assert.True(result.Beats.Count > 80, $"only {result.Beats.Count} beats tracked");

        // Beats stay locked to the drifting clicks.
        foreach (var beat in result.Beats)
        {
            Assert.True(DistanceToNearestClick(beat.TimeSeconds, clicks) < 0.045,
                $"beat at {beat.TimeSeconds:F3}s lost the click grid");
        }

        // Local tempo rises across the ramp.
        var early = result.Beats.Take(8).Average(b => b.Bpm);
        var late = result.Beats.Skip(result.Beats.Count - 8).Average(b => b.Bpm);
        Assert.True(late > early + 20.0, $"tempo drift not tracked: early {early:F1}, late {late:F1}");
    }

    [Fact]
    public void Analyze_IsDeterministic()
    {
        var (samples, _) = RampClickTrack(45.0, 100.0, 140.0);
        var analyzer = new BeatMapAnalyzer();

        var first = analyzer.Analyze(samples, Rate);
        var second = analyzer.Analyze(samples, Rate);

        Assert.Equal(first.AverageBpm, second.AverageBpm);
        Assert.Equal(first.Beats.Count, second.Beats.Count);
        for (var i = 0; i < first.Beats.Count; i++)
            Assert.Equal(first.Beats[i], second.Beats[i]);
    }

    /// <summary>
    ///     <see cref="BeatMapAnalyzerOptions.MinBpm" />/<c>MaxBpm</c> are a
    ///     CONTRACT — nothing downstream re-checks the range, so a beat map
    ///     outside it is worse than no beat map at all — and the analyzer broke
    ///     it at the edges. A lag is an integer number of hops and the two bounds
    ///     rounded OUTWARD: <c>floor</c> on the fast end admitted a period faster
    ///     than <c>MaxBpm</c>, <c>ceil</c> on the slow end one slower than
    ///     <c>MinBpm</c>, and the parabolic refinement then moved the winner up to
    ///     a whole hop further out again.
    ///     <para>The last case is the measured one: at 44.1 kHz / hop 256 the
    ///     envelope runs at 172.27 fps, so <c>MinBpm = 75</c> is 137.8 hops and
    ///     <c>ceil</c> made 138 reachable — 74.898 BPM, outside the very range the
    ///     caller asked for. The first three cover the DEFAULT range at both ends
    ///     and in the middle; they held before the fix too, and are here because
    ///     the two bounds are separate expressions and a fix to one says nothing
    ///     about the other.</para>
    /// </summary>
    [Theory]
    [InlineData(60.0, 200.0, 200.0)]  // source exactly at MaxBpm
    [InlineData(60.0, 200.0, 60.0)]   // source exactly at MinBpm
    [InlineData(60.0, 200.0, 120.0)]  // comfortably inside
    [InlineData(75.0, 78.0, 100.0)]   // narrow band under a 100 BPM track: 74.898
    public void EveryDetectedTempo_StaysInsideTheRequestedBpmRange(double minBpm, double maxBpm, double sourceBpm)
    {
        var options = new BeatMapAnalyzerOptions { MinBpm = minBpm, MaxBpm = maxBpm };
        var result = new BeatMapAnalyzer().Analyze(ConstantClickTrack(25.0, sourceBpm), Rate, options);

        Assert.NotEmpty(result.Beats);
        Assert.InRange(result.AverageBpm, minBpm, maxBpm);
        foreach (var beat in result.Beats)
            Assert.InRange(beat.Bpm, minBpm, maxBpm);
    }

    [Fact]
    public void Analyze_SilenceOrTooShort_ReturnsEmpty()
    {
        var analyzer = new BeatMapAnalyzer();
        Assert.Empty(analyzer.Analyze(new float[Rate * 10], Rate).Beats);
        Assert.Empty(analyzer.Analyze(new float[Rate / 2], Rate).Beats);
    }

    /// <summary>
    ///     A decoder glitch or a denormal blow-up must cost the four frames that
    ///     overlap it and NOTHING after them. The FFT smears one bad sample across
    ///     every bin and its NaN log-magnitude becomes the PREVIOUS frame's
    ///     spectrum, so the poison has an obvious route to the whole rest of the
    ///     clip: NaN flux ⇒ a NaN running sum in the whitening filter ⇒ no tempo
    ///     window ever scores ⇒ an empty beat map for a song that is 99% clean.
    ///     <para>It did not, in fact, get there — <c>d &gt; 0</c> is false for NaN,
    ///     so the sum stayed finite by accident. This pins the property so it is
    ///     no longer an accident: rewriting that line as
    ///     <c>sum += MathF.Max(0f, d)</c> (Max(0, NaN) IS NaN) fails this test.
    ///     </para>
    /// </summary>
    [Theory]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(float.NegativeInfinity)]
    public void ASingleNonFiniteSample_DoesNotDestroyTheRestOfTheTrack(float poison)
    {
        var samples = ConstantClickTrack();
        samples[Rate * 10] = poison;

        var result = new BeatMapAnalyzer().Analyze(samples, Rate);

        Assert.InRange(result.AverageBpm, 118.0, 122.0);
        Assert.InRange(result.Beats.Count, 55, 62);
        foreach (var beat in result.Beats)
        {
            Assert.True(double.IsFinite(beat.Bpm), $"beat at {beat.TimeSeconds:F3}s has BPM {beat.Bpm}");
            Assert.InRange(beat.Confidence, 0f, 1f); // NaN fails InRange
        }
    }
}
