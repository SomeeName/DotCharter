using DotCharter.Services.Audio.LivePlay;

namespace DotCharter.Tests.Services.Audio.LivePlay;

public sealed class LiveTimingAnalyticsTests
{
    /// <summary>A judged event with a REAL attack timestamp behind it. The tier
    /// defaults to <see cref="LiveTimingTier.Good" /> rather than to the record's
    /// own <see cref="LiveTimingTier.None" />, because None means "no honest
    /// attack time exists" and the collector now declines those — see
    /// <see cref="LiveTimingCollector.Add(in LivePlayEvent)" />.</summary>
    private static LivePlayEvent Judged(LivePlayVerdict verdict, double soundTimeMs, double detectedAtMs,
        LiveTimingTier tier = LiveTimingTier.Good)
        => new(verdict, new ExpectedSound { TimeMs = soundTimeMs }, detectedAtMs, 1f, tier);

    [Fact]
    public void EmptyRun_YieldsEmptyReport()
    {
        var report = new LiveTimingCollector().BuildReport();
        Assert.Equal(0, report.HitCount);
        Assert.Equal(LiveTimingReport.HistogramBinCount, report.Histogram.Count);
        Assert.False(report.LeansEarly);
        Assert.False(report.LeansLate);
    }

    [Fact]
    public void Misses_AreExcluded_TheirDetectedAtIsWindowExpiryNotADetection()
    {
        var collector = new LiveTimingCollector();
        collector.Add(Judged(LivePlayVerdict.Miss, 1000, 1250));
        collector.Add(Judged(LivePlayVerdict.Hit, 2000, 2010));

        var report = collector.BuildReport();
        Assert.Equal(1, report.HitCount);
        Assert.Equal(10.0, report.MeanMs, 6);
    }

    [Fact]
    public void WrongTechniqueHits_AreRealDetections_AndCount()
    {
        var collector = new LiveTimingCollector();
        collector.Add(Judged(LivePlayVerdict.HitWrongTechnique, 500, 480));

        var report = collector.BuildReport();
        Assert.Equal(1, report.HitCount);
        Assert.Equal(-20.0, report.MeanMs, 6);
    }

    [Fact]
    public void KnownDistribution_ProducesExactStatistics()
    {
        var collector = new LiveTimingCollector();
        // Signed deltas: -40, -20, 0, +20, +40 (mean 0, symmetric).
        foreach (var delta in new[] { -40.0, -20.0, 0.0, 20.0, 40.0 })
            collector.Add(soundTimeMs: 1000, deltaMs: delta);

        var report = collector.BuildReport();

        Assert.Equal(5, report.HitCount);
        Assert.Equal(0.0, report.MeanMs, 6);
        Assert.Equal(0.0, report.MedianMs, 6);
        Assert.Equal(-40.0, report.MinMs, 6);
        Assert.Equal(40.0, report.MaxMs, 6);
        // Population std dev of {-40,-20,0,20,40} = sqrt(4000/5) = ~28.28.
        Assert.Equal(Math.Sqrt(800.0), report.StdDevMs, 6);
        // Linear-interpolated percentiles over 5 points (rank = p * 4).
        Assert.Equal(-32.0, report.P10Ms, 6);
        Assert.Equal(-20.0, report.P25Ms, 6);
        Assert.Equal(20.0, report.P75Ms, 6);
        Assert.Equal(32.0, report.P90Ms, 6);
        // ±15 ms on-time window: only the exact 0 qualifies.
        Assert.Equal(2, report.EarlyCount);
        Assert.Equal(1, report.OnTimeCount);
        Assert.Equal(2, report.LateCount);
    }

    [Fact]
    public void LeaningVerdicts_FollowTheMean()
    {
        var early = new LiveTimingCollector();
        early.Add(soundTimeMs: 0, deltaMs: -30);
        Assert.True(early.BuildReport().LeansEarly);

        var late = new LiveTimingCollector();
        late.Add(soundTimeMs: 0, deltaMs: 30);
        Assert.True(late.BuildReport().LeansLate);

        var pocket = new LiveTimingCollector();
        pocket.Add(soundTimeMs: 0, deltaMs: 5);
        var report = pocket.BuildReport();
        Assert.False(report.LeansEarly);
        Assert.False(report.LeansLate);
    }

    [Fact]
    public void Histogram_BinsSignedDeltas_AndClampsOutliersIntoEdgeBins()
    {
        var collector = new LiveTimingCollector();
        collector.Add(soundTimeMs: 0, deltaMs: 0);      // center: bin 15 ([0, 10))
        collector.Add(soundTimeMs: 0, deltaMs: -1);     // bin 14 ([-10, 0))
        collector.Add(soundTimeMs: 0, deltaMs: -999);   // clamps into bin 0
        collector.Add(soundTimeMs: 0, deltaMs: 999);    // clamps into the last bin

        var histogram = collector.BuildReport().Histogram;

        Assert.Equal(30, histogram.Count);
        Assert.Equal(1, histogram[15]);
        Assert.Equal(1, histogram[14]);
        Assert.Equal(1, histogram[0]);
        Assert.Equal(1, histogram[^1]);
        Assert.Equal(4, histogram.Sum());
    }

    [Fact]
    public void SectionBias_AssignsHitsByChartTime_AndSkipsUnplayedSections()
    {
        var sections = new[]
        {
            new SectionResult("Verse", 0, 10_000, 10, 2, 0),
            new SectionResult("Chorus", 10_000, 20_000, 10, 2, 0),
            new SectionResult("Bridge", 20_000, 30_000, 10, 0, 0),
        };

        var collector = new LiveTimingCollector();
        collector.Add(Judged(LivePlayVerdict.Hit, 1_000, 980));    // Verse, -20
        collector.Add(Judged(LivePlayVerdict.Hit, 9_000, 8_990)); // Verse, -10
        collector.Add(Judged(LivePlayVerdict.Hit, 12_000, 12_030)); // Chorus, +30
        collector.Add(Judged(LivePlayVerdict.Hit, 15_000, 15_010)); // Chorus, +10

        var report = collector.BuildReport(sections);

        Assert.Equal(2, report.Sections.Count); // Bridge unplayed → omitted
        var verse = report.Sections[0];
        Assert.Equal("Verse", verse.Name);
        Assert.Equal(2, verse.HitCount);
        Assert.Equal(-15.0, verse.MeanMs, 6);
        Assert.Equal(-15.0, verse.MedianMs, 6);

        var chorus = report.Sections[1];
        Assert.Equal("Chorus", chorus.Name);
        Assert.Equal(20.0, chorus.MeanMs, 6);
    }

    [Fact]
    public void HitsOutsideEverySection_StillCountInOverallNumbers()
    {
        var sections = new[] { new SectionResult("Verse", 0, 1_000, 5, 1, 0) };
        var collector = new LiveTimingCollector();
        collector.Add(Judged(LivePlayVerdict.Hit, 5_000, 5_020)); // beyond all sections

        var report = collector.BuildReport(sections);
        Assert.Equal(1, report.HitCount);
        Assert.Empty(report.Sections);
    }

    [Fact]
    public void Reset_ClearsCollectedHits()
    {
        var collector = new LiveTimingCollector();
        collector.Add(soundTimeMs: 0, deltaMs: 10);
        Assert.Equal(1, collector.Count);

        collector.Reset();
        Assert.Equal(0, collector.Count);
        Assert.Equal(0, collector.BuildReport().HitCount);
    }

    [Fact]
    public void SingleHit_DegenerateStatistics_AreStable()
    {
        var collector = new LiveTimingCollector();
        collector.Add(soundTimeMs: 100, deltaMs: 25);

        var report = collector.BuildReport();
        Assert.Equal(25.0, report.MeanMs, 6);
        Assert.Equal(25.0, report.MedianMs, 6);
        Assert.Equal(25.0, report.P10Ms, 6);
        Assert.Equal(25.0, report.P90Ms, 6);
        Assert.Equal(0.0, report.StdDevMs, 6);
    }

    /// <summary>
    ///     A CQT pull-verified hit carries the PLAYHEAD at window close, not an
    ///     attack time, so its delta is a constant ≈ +HitWindowLateMs. Counting
    ///     it told a perfect player they were 230 ms late.
    /// </summary>
    [Fact]
    public void AnUntimedHit_ContributesNoTiming()
    {
        var collector = new LiveTimingCollector();
        collector.Add(Judged(LivePlayVerdict.Hit, 1_000, 1_230, LiveTimingTier.None));
        collector.Add(Judged(LivePlayVerdict.HitWrongTechnique, 2_000, 2_230, LiveTimingTier.None));

        Assert.Equal(0, collector.Count);
        Assert.Equal(0, collector.BuildReport().HitCount);
    }

    /// <summary>
    ///     The consequence that outlived the run. A whole CQT run's fake deltas
    ///     are near-identical, so the spread gate — which exists to reject
    ///     non-latency signals — passed them, and the calibrator moved the
    ///     device's PERSISTED offset by 0.4 × 230 ms, leashed only by the
    ///     baseline drift cap. Now there is nothing to learn from.
    /// </summary>
    [Fact]
    public void AnAllUntimedRun_TeachesTheLatencyCalibratorNothing()
    {
        var collector = new LiveTimingCollector();
        for (var i = 0; i < 60; i++)
            collector.Add(Judged(LivePlayVerdict.Hit, i * 500, i * 500 + 230, LiveTimingTier.None));

        var decision = LatencyBiasCalibrator.Evaluate(
            currentOffsetMs: 20, baselineOffsetMs: 20, timing: collector.BuildReport());

        Assert.False(decision.Adjusted);
        Assert.Equal(20, decision.NewOffsetMs);
        Assert.Equal("no timed hits", decision.Reason);
    }

    /// <summary>The guard is one-sided: a genuinely timed hit still counts.</summary>
    [Fact]
    public void ATimedHit_StillContributes()
    {
        var collector = new LiveTimingCollector();
        collector.Add(Judged(LivePlayVerdict.Hit, 1_000, 1_030, LiveTimingTier.Great));

        Assert.Equal(1, collector.Count);
        Assert.Equal(30.0, collector.BuildReport().MedianMs, 6);
    }
}

