using DotCharter.Tone.Audio.Tuner;

namespace DotCharter.Tests.Services.Audio.Tuner;

public sealed class TuningDriftWatchdogTests
{
    private const double NoteMs = 500; // one note every half second

    private static TuningDriftWatchdog Watchdog() => new();

    /// <summary>Feeds <paramref name="count"/> evidences of
    /// <paramref name="cents"/> spaced <see cref="NoteMs"/> apart from
    /// <paramref name="startMs"/>; returns the time after the last one.</summary>
    private static double Feed(TuningDriftWatchdog watchdog, double cents, int count, double startMs = 0)
    {
        var t = startMs;
        for (var i = 0; i < count; i++, t += NoteMs)
            watchdog.AddEvidence(cents, t);
        return t;
    }

    [Fact]
    public void StartsInsufficient_UntilTheEvidenceFloorIsMet()
    {
        var watchdog = Watchdog();
        Assert.Equal(TuningDriftVerdict.Insufficient, watchdog.Verdict);

        // Decay between notes means the DECAYED weight crosses 8 at the 9th
        // note (weight after n notes = (1−d^n)/(1−d), d = 2^(−500/30000)).
        Feed(watchdog, 0, 8);
        Assert.Equal(TuningDriftVerdict.Insufficient, watchdog.Verdict);

        watchdog.AddEvidence(0, 8 * NoteMs);
        Assert.Equal(TuningDriftVerdict.Stable, watchdog.Verdict);
    }

    [Theory]
    [InlineData(-12.0, TuningDriftVerdict.DriftFlat)]
    [InlineData(12.0, TuningDriftVerdict.DriftSharp)]
    public void ConsistentOffset_RaisesTheDirectionalVerdict(double cents, TuningDriftVerdict expected)
    {
        var watchdog = Watchdog();
        Feed(watchdog, cents, 10);

        Assert.Equal(expected, watchdog.Verdict);
        Assert.InRange(watchdog.MeanCents, cents - 0.5, cents + 0.5);
    }

    [Fact]
    public void InTunePlaying_StaysStable()
    {
        var watchdog = Watchdog();
        // Realistic wobble around zero, well inside the threshold.
        var t = 0.0;
        for (var i = 0; i < 40; i++, t += NoteMs)
            watchdog.AddEvidence(i % 2 == 0 ? 3.0 : -3.0, t);

        Assert.Equal(TuningDriftVerdict.Stable, watchdog.Verdict);
    }

    [Fact]
    public void Hysteresis_StaysRaisedBetweenClearAndDriftThresholds()
    {
        var watchdog = Watchdog();
        var t = Feed(watchdog, -12, 10);
        Assert.Equal(TuningDriftVerdict.DriftFlat, watchdog.Verdict);

        // Pull the mean into the 4..8 dead band: still raised.
        while (Math.Abs(watchdog.MeanCents) > 6.0)
            t = Feed(watchdog, 0, 1, t);
        Assert.True(Math.Abs(watchdog.MeanCents) > 4.0);
        Assert.Equal(TuningDriftVerdict.DriftFlat, watchdog.Verdict);

        // Only below the clear threshold does it stand down.
        while (Math.Abs(watchdog.MeanCents) > 4.0)
            t = Feed(watchdog, 0, 1, t);
        Assert.Equal(TuningDriftVerdict.Stable, watchdog.Verdict);

        // And re-raising needs the full drift threshold again, not 4¢.
        while (Math.Abs(watchdog.MeanCents) < 6.0)
            t = Feed(watchdog, -12, 1, t);
        if (Math.Abs(watchdog.MeanCents) < 8.0)
            Assert.Equal(TuningDriftVerdict.Stable, watchdog.Verdict);
    }

    [Fact]
    public void OneOctaveErrorOutlier_CannotFakeADrift()
    {
        var watchdog = Watchdog();
        var t = Feed(watchdog, 0, 12);

        // A wild +1200¢ reading (octave error in the source) is clamped.
        watchdog.AddEvidence(1200, t);

        Assert.Equal(TuningDriftVerdict.Stable, watchdog.Verdict);
        // Clamped to 50 over ~12 decayed weights — far from the 8¢ threshold.
        Assert.InRange(watchdog.MeanCents, 0, 4.5);
    }

    [Fact]
    public void OldEvidence_DecaysAcrossTheHalfLife()
    {
        var watchdog = Watchdog();
        Feed(watchdog, -12, 10); // raised
        Assert.Equal(TuningDriftVerdict.DriftFlat, watchdog.Verdict);

        // Two half-lives later the player has retuned and plays clean:
        // the stale flat evidence must fade and the verdict clear.
        Feed(watchdog, 0, 10, startMs: 65_000);
        Assert.Equal(TuningDriftVerdict.Stable, watchdog.Verdict);
        Assert.InRange(Math.Abs(watchdog.MeanCents), 0, 4.0);
    }

    [Fact]
    public void HeavierEvidence_CountsMore()
    {
        var watchdog = Watchdog();

        // One high-confidence note carrying weight 8 meets the floor alone.
        watchdog.AddEvidence(-15, 0, weight: 8.0);
        Assert.Equal(TuningDriftVerdict.DriftFlat, watchdog.Verdict);
    }

    [Fact]
    public void NonPositiveWeightAndNaN_AreIgnored()
    {
        var watchdog = Watchdog();
        watchdog.AddEvidence(-20, 0, weight: 0);
        watchdog.AddEvidence(-20, 100, weight: -1);
        watchdog.AddEvidence(double.NaN, 200);

        Assert.Equal(TuningDriftVerdict.Insufficient, watchdog.Verdict);
        Assert.Equal(0, watchdog.EvidenceWeight, 6);
    }

    [Fact]
    public void TimeGoingBackwards_DoesNotAmplifyEvidence()
    {
        var watchdog = Watchdog();
        Feed(watchdog, -10, 10);
        var weightBefore = watchdog.EvidenceWeight;

        watchdog.AddEvidence(-10, 0); // clock hiccup: earlier than the last note
        Assert.True(watchdog.EvidenceWeight <= weightBefore + 1.0 + 1e-9);
    }

    [Fact]
    public void VerdictChanged_FiresOncePerTransition()
    {
        var watchdog = Watchdog();
        var transitions = new List<TuningDriftVerdict>();
        watchdog.VerdictChanged += transitions.Add;

        var t = Feed(watchdog, 0, 8);           // Insufficient -> Stable
        t = Feed(watchdog, -14, 30, t);          // Stable -> DriftFlat
        while (Math.Abs(watchdog.MeanCents) > 4.0)
            t = Feed(watchdog, 0, 1, t);         // DriftFlat -> Stable

        Assert.Equal(new[]
        {
            TuningDriftVerdict.Stable,
            TuningDriftVerdict.DriftFlat,
            TuningDriftVerdict.Stable
        }, transitions);
    }

    [Fact]
    public void Reset_ReturnsToInsufficient_AndRaisesTheTransition()
    {
        var watchdog = Watchdog();
        Feed(watchdog, -12, 10);

        var raised = TuningDriftVerdict.Stable;
        watchdog.VerdictChanged += v => raised = v;
        watchdog.Reset();

        Assert.Equal(TuningDriftVerdict.Insufficient, watchdog.Verdict);
        Assert.Equal(TuningDriftVerdict.Insufficient, raised);
        Assert.Equal(0, watchdog.EvidenceWeight, 6);
        Assert.Equal(0, watchdog.MeanCents, 6);
    }

    [Fact]
    public void AddEvidence_IsAllocationFree_AfterWarmup()
    {
        var watchdog = Watchdog();
        var count = 0;
        watchdog.VerdictChanged += _ => count++; // subscribed before measuring

        watchdog.AddEvidence(-3, 0);
        watchdog.AddEvidence(3, 100);

        // The clock keeps advancing across batches — the watchdog weighs
        // evidence by age, so replaying the same timestamps would measure a
        // different path than the one that runs live.
        var nowMs = 200.0;
        AllocationProbe.AssertAllocationFree("TuningDriftWatchdog.AddEvidence", () =>
        {
            for (var i = 0; i < 100; i++)
            {
                watchdog.AddEvidence(i % 2 == 0 ? -12 : 12, nowMs);
                nowMs += 50;
            }
        });

        _ = count;
    }
}
