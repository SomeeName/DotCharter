using static DotCharter.Tests.Ai.AudioEval.MatchEvalReport;

namespace DotCharter.Tests.Ai.AudioEval;

/// <summary>
///     The default-gate half of the match eval: the tally and the table. The eval
///     that produces the rows is opt-in and takes tens of minutes, so without these
///     the arithmetic that decides "the matcher beats the authored rig on X of 21"
///     would only ever be exercised by a run nobody repeats.
/// </summary>
public sealed class MatchEvalReportTests
{
    private static MatchRow Row(double truth, double top1, double bestN = double.NaN, string origin = "retrieval") =>
        new("A — B", "Clean", "0–30s", "charted:x.xml", truth, top1,
            double.IsNaN(bestN) ? top1 : bestN, 0.2, 12, 30.0, origin);

    [Fact]
    public void Tally_CountsAWinOnlyWhenTheMatcherIsStrictlyCloser()
    {
        var tally = Tally([Row(0.80, 0.70), Row(0.80, 0.90), Row(0.80, 0.80)]);

        Assert.Equal(1, tally.Wins);
        // The tie is a LOSS: two independent renders never tie, so an exact tie is a
        // broken comparison and must not read as a good result.
        Assert.Equal(2, tally.Losses);
        Assert.Equal(0, tally.Unscored);
        Assert.Equal(3, tally.Scored);
    }

    [Fact]
    public void Tally_CountsARowWithNoFiniteDistanceAsUnscored_NeverAsALoss()
    {
        var tally = Tally([Row(double.NaN, 0.70), Row(0.80, double.NaN), Row(0.80, 0.70)]);

        Assert.Equal(1, tally.Wins);
        Assert.Equal(0, tally.Losses);
        Assert.Equal(2, tally.Unscored);
        // The headline must not say "1 of 3" when only one row was comparable.
        Assert.Contains("1 of 1", tally.ToString(), StringComparison.Ordinal);
        Assert.Contains("2 unscored", tally.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void RenderTable_MarksTheVerdictAndKeepsUnscoredRowsVisible()
    {
        var table = RenderTable([Row(0.80, 0.70, origin: "retrieval+cmaes"), Row(0.80, double.NaN)]);

        Assert.Contains("**win**", table, StringComparison.Ordinal);
        Assert.Contains("retrieval+cmaes", table, StringComparison.Ordinal);
        // A row the matcher could not score is printed, not dropped — dropping it
        // would quietly improve every aggregate it belonged to.
        Assert.Contains("| — |", table, StringComparison.Ordinal);
        Assert.Equal(4, table.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
    }

    [Fact]
    public void RenderTable_FormatsInvariantly()
    {
        var table = RenderTable([Row(0.8067, 1.2401)]);

        Assert.Contains("0.8067", table, StringComparison.Ordinal);
        Assert.Contains("1.2401", table, StringComparison.Ordinal);
        Assert.DoesNotContain("0,8067", table, StringComparison.Ordinal);
    }

    [Fact]
    public void Worst_RanksByTheMarginTheMatcherLostBy_AndSkipsUnscoredRows()
    {
        var worst = Worst(
        [
            Row(0.80, 0.85), // −0.05
            Row(0.80, 1.60), // −0.80, the worst
            Row(0.80, 0.60), // a win
            Row(0.80, double.NaN)
        ], 2);

        Assert.Equal(2, worst.Count);
        Assert.Equal(1.60, worst[0].MatcherTop1);
        Assert.Equal(0.85, worst[1].MatcherTop1);
    }
}
