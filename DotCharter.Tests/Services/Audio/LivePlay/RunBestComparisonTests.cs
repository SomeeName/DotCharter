using DotCharter.Services.Audio.LivePlay;

namespace DotCharter.Tests.Services.Audio.LivePlay;

/// <summary>
///     Pins the pure "is this a new best / by how much" decision. Best is ranked
///     by SCORE alone (matching the LAN scoreboard merge + local leaderboard
///     ordering), so a lower score is never a new best even when more accurate,
///     and — because the store keeps the max — a worse run never lowers the best.
/// </summary>
public sealed class RunBestComparisonTests
{
    private static PracticeBests Best(long score, double accuracy = 0.80, int streak = 10, double mastery = 50)
        => new(score, accuracy, streak, mastery);

    [Fact]
    public void FirstEverRun_IsFirstRun_NoDeltaNoStandingBest()
    {
        var c = RunBestComparison.Evaluate(runScore: 4000, runAccuracy: 0.90, runCoverage: 1.0, Best(0, 0), hadPriorRun: false);

        Assert.Equal(RunBestOutcome.FirstRun, c.Outcome);
        Assert.True(c.IsFirstRun);
        Assert.False(c.IsNewBest);
        Assert.False(c.StoredBestStands);   // nothing to stand on yet
        Assert.False(c.ImprovedAccuracy);   // no prior accuracy to beat
        Assert.Equal(0, c.ScoreDelta);
        Assert.Equal(4000, c.RunScore);
    }

    [Fact]
    public void HigherScore_IsNewBest_WithPositiveDelta()
    {
        var c = RunBestComparison.Evaluate(runScore: 5200, runAccuracy: 0.92, runCoverage: 1.0, Best(4000, 0.88), hadPriorRun: true);

        Assert.Equal(RunBestOutcome.NewBest, c.Outcome);
        Assert.True(c.IsNewBest);
        Assert.False(c.StoredBestStands);
        Assert.True(c.ImprovedAccuracy);
        Assert.Equal(1200, c.ScoreDelta);
        Assert.Equal(4000, c.PreviousBestScore);
    }

    [Fact]
    public void LowerScore_IsBelowBest_StoredBestStands_NegativeDelta()
    {
        var c = RunBestComparison.Evaluate(runScore: 2000, runAccuracy: 0.70, runCoverage: 1.0, Best(5000, 0.90), hadPriorRun: true);

        Assert.Equal(RunBestOutcome.BelowBest, c.Outcome);
        Assert.False(c.IsNewBest);
        Assert.True(c.StoredBestStands);
        Assert.False(c.ImprovedAccuracy);
        Assert.Equal(-3000, c.ScoreDelta);
        Assert.Equal(5000, c.PreviousBestScore);
    }

    [Fact]
    public void EqualScore_IsMatchedBest_StoredBestStands_ZeroDelta()
    {
        var c = RunBestComparison.Evaluate(runScore: 4000, runAccuracy: 0.90, runCoverage: 1.0, Best(4000, 0.90), hadPriorRun: true);

        Assert.Equal(RunBestOutcome.MatchedBest, c.Outcome);
        Assert.False(c.IsNewBest);
        Assert.True(c.StoredBestStands);
        Assert.Equal(0, c.ScoreDelta);
    }

    [Fact]
    public void HigherAccuracyButLowerScore_IsNotANewBest_ScoreDecides()
    {
        // The whole point of the score-ranked rule: a cleaner but lower-scoring
        // run does not dethrone the best, though the accuracy gain is still flagged.
        var c = RunBestComparison.Evaluate(runScore: 4000, runAccuracy: 0.99, runCoverage: 1.0, Best(5000, 0.80), hadPriorRun: true);

        Assert.Equal(RunBestOutcome.BelowBest, c.Outcome);
        Assert.False(c.IsNewBest);
        Assert.True(c.StoredBestStands);
        Assert.True(c.ImprovedAccuracy);      // accuracy tracked independently
        Assert.Equal(-1000, c.ScoreDelta);
    }

    [Fact]
    public void AShortRunsAccuracyGain_IsNotFlaggedAsAnImprovement()
    {
        // The store refuses to raise BestAccuracy below the coverage floor, so
        // claiming the improvement here would tell the player something that was
        // not kept.
        var c = RunBestComparison.Evaluate(runScore: 200, runAccuracy: 1.0, runCoverage: 0.05,
            Best(5000, 0.90), hadPriorRun: true);

        Assert.False(c.ImprovedAccuracy);
    }

    [Fact]
    public void Report_IsNewBestAccuracy_RequiresTheCoverageFloor()
    {
        // 3 of 60 sounds judged, all clean.
        var tiny = new LivePlaySessionResult(200, 1.0, 3, 0, 60, 3, 10, 100, 0.05);
        var report = new LivePlaySessionReport(tiny, Best(5000, 0.90), Best(5000, 0.90),
            WasRecorded: true, SongKey: "lib:abc", ArrangementKey: "Lead", Sections: [], HadPriorRun: true);

        Assert.False(report.IsNewBestAccuracy);

        // The same accuracy over the whole chart is a real record.
        var full = new LivePlaySessionResult(9000, 1.0, 60, 0, 60, 60, 200, 100, 1.0);
        var fullReport = new LivePlaySessionReport(full, Best(5000, 0.90), Best(5000, 1.0),
            WasRecorded: true, SongKey: "lib:abc", ArrangementKey: "Lead", Sections: [], HadPriorRun: true);

        Assert.True(fullReport.IsNewBestAccuracy);
    }

    [Fact]
    public void Report_Comparison_ReflectsBelowBest_ForRecordedWorseRun()
    {
        var result = new LivePlaySessionResult(2000, 0.70, 20, 10, 30, 5, 60, 100, 1.0);
        var report = new LivePlaySessionReport(
            result,
            Before: Best(5000, 0.90),
            After: Best(5000, 0.90),
            WasRecorded: true,
            SongKey: "lib:abc",
            ArrangementKey: "Lead",
            Sections: [],
            HadPriorRun: true);

        Assert.Equal(RunBestOutcome.BelowBest, report.Comparison.Outcome);
        Assert.True(report.Comparison.StoredBestStands);
        Assert.Equal(-3000, report.Comparison.ScoreDelta);
        Assert.False(report.IsNewBestScore);
    }
}
