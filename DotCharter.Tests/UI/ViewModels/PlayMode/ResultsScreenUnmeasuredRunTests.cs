using System.Text.RegularExpressions;
using DotCharter.Services.Audio.LivePlay;
using DotCharter.Services.Gameplay.Leaderboards;
using DotCharter.Services.Gameplay.Scoring;
using DotCharter.UI.ViewModels.PlayMode;

namespace DotCharter.Tests.UI.ViewModels.PlayMode;

/// <summary>
///     A run that judged NOTHING must not be graded.
///     <para>
///         <c>AccuracyMath.FromCounts(0, 0)</c> is 0 — deliberately, so that no
///         readout can report a confident 100% for a player who has done nothing
///         — and <c>GradeCalculator.FromAccuracy(0)</c> is a confident <b>F</b>.
///         The results screen printed both: "0.0%" and a giant F, staged reveal
///         and all, for a player who ended a run from the pause menu during the
///         intro. That state is ordinary, not a corner: nothing is judged until
///         the first hit window closes, and <c>EndRunFromPause</c> forces the
///         screen back to <c>Playing</c> precisely so the results are built.
///     </para>
///     <para>
///         The verdict belongs on the REPORT, beside every other one the results
///         screen reads, exactly as <c>SongPlayStats.GradeLabel</c> carries
///         its own — so it is
///         decided once instead of re-derived per readout.
///     </para>
/// </summary>
public sealed class ResultsScreenUnmeasuredRunTests
{
    [Fact]
    public void ARunThatJudgedNothing_IsNotMeasuredAndIsNotGraded()
    {
        var report = Report(hits: 0, misses: 0);

        Assert.False(report.IsMeasured);
        Assert.Equal(AccuracyMath.UnmeasuredLabel, report.GradeLabel);
        // …and the raw enum still says F, which is exactly why the label exists.
        Assert.Equal(LetterGrade.F, GradeCalculator.FromAccuracy(report.Result.Accuracy));
    }

    [Fact]
    public void ARunWithJudgedSounds_StillPrintsItsLetter()
    {
        var clean = Report(hits: 100, misses: 0, accuracy: 1.0);
        Assert.True(clean.IsMeasured);
        Assert.Equal(GradeCalculator.Label(LetterGrade.S), clean.GradeLabel);

        // A genuinely failed run is measured and still grades F — the fix may
        // not swallow a real F.
        var flubbed = Report(hits: 1, misses: 99, accuracy: 0.01);
        Assert.True(flubbed.IsMeasured);
        Assert.Equal(GradeCalculator.Label(LetterGrade.F), flubbed.GradeLabel);
    }

    /// <summary>
    ///     The wiring half. The two readouts are written into observable
    ///     properties by <c>OnSessionCompleted</c>, which needs a live judging
    ///     session and a dispatcher to reach — so this asserts, over the whole
    ///     partial set with comments stripped, that the screen binds the report's
    ///     verdict and does not re-derive a grade of its own.
    /// </summary>
    [Fact]
    public void TheResultsScreenBindsTheReportsVerdict_AndDoesNotRederiveAGrade()
    {
        var source = SourceText.StripCommentLines(PlayModeViewModelSource());

        Assert.Contains("ResultGrade = report.GradeLabel;", source, StringComparison.Ordinal);
        Assert.Contains("report.IsMeasured", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ResultGrade = GradeCalculator", source, StringComparison.Ordinal);

        // The accuracy readout must be gated too — an em dash grade beside a
        // confident "0.0%" would be worse than either alone.
        var accuracy = Regex.Match(source, @"ResultAccuracy\s*=(?<rhs>[^;]*);",
            RegexOptions.Singleline);
        Assert.True(accuracy.Success, "ResultAccuracy is no longer assigned — this lint was renamed out of its job");
        Assert.Contains("report.IsMeasured", accuracy.Groups["rhs"].Value, StringComparison.Ordinal);
        Assert.Contains("UnmeasuredLabel", accuracy.Groups["rhs"].Value, StringComparison.Ordinal);
    }

    /// <summary>
    ///     The same verdict, driven: the results board is its own view model, so a
    ///     report can be shown without a judging session. An unmeasured run prints
    ///     the em dash for both readouts; a measured one prints its percent and letter.
    /// </summary>
    [Fact]
    public void TheResultsBoard_ShowsTheReportsVerdict()
    {
        var board = new RunResultsViewModel(FakeLocalization.English(), new NoLeaderboard());

        board.Show(Report(hits: 0, misses: 0));
        Assert.Equal(AccuracyMath.UnmeasuredLabel, board.ResultAccuracy);
        Assert.Equal(AccuracyMath.UnmeasuredLabel, board.ResultGrade);

        board.Show(Report(hits: 100, misses: 0, accuracy: 1.0));
        Assert.NotEqual(AccuracyMath.UnmeasuredLabel, board.ResultAccuracy);
        Assert.Equal(GradeCalculator.Label(LetterGrade.S), board.ResultGrade);
        Assert.Equal("100", board.ResultHits);
    }

    // ---- fixture -------------------------------------------------------------

    private sealed class NoLeaderboard : ILeaderboardService
    {
        public IReadOnlyList<LeaderboardEntry> GetSongLeaderboard(string songKey, string arrangementName,
            LiveDetectorMode detector) => [];
    }

    private static LivePlaySessionReport Report(int hits, int misses, double accuracy = 0.0)
    {
        var result = new LivePlaySessionResult(
            Score: 0, Accuracy: accuracy, Hits: hits, Misses: misses,
            TotalSounds: Math.Max(1, hits + misses), BestStreak: hits,
            DurationSeconds: 12, SpeedPercent: 100, SongProgress: 0.1);
        var bests = new PracticeBests(0, 0, 0, 0);
        return new LivePlaySessionReport(result, bests, bests, WasRecorded: false,
            "lib:Song", "Lead:Lead", Array.Empty<SectionResult>());
    }

    /// <summary>The WHOLE partial set plus the results board it fills
    /// (<c>RunResultsViewModel.cs</c>): a guard that reads one file goes silently
    /// blind the day a member moves to a sibling.</summary>
    private static string PlayModeViewModelSource()
    {
        var folder = UiSourceTree.Resolve(Path.Combine(TestPaths.RepoRoot(), "DotCharter.UI", "ViewModels", "PlayMode"));
        var board = Path.Combine(folder, "RunResultsViewModel.cs");
        Assert.True(File.Exists(board), "RunResultsViewModel.cs moved — repoint this guard");
        var files = UiSourceTree.GetFiles(folder, "PlayModeViewModel*.cs")
            .Append(board)
            .OrderBy(f => f, StringComparer.Ordinal).ToArray();
        Assert.NotEmpty(files);
        return string.Join("\n", files.Select(UiSourceTree.ReadAllText));
    }
}
