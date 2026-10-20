using DotCharter.Services.Gameplay.Scoring;

namespace DotCharter.Services.Audio.LivePlay;

/// <summary>Per-section outcome inside a <see cref="LivePlaySessionReport"/>.</summary>
/// <summary>
///     One section's result inside a finished run.
///     <para>
///         <paramref name="StartBar" />/<paramref name="EndBar" /> are the
///         1-based chart bars the section spans, captured while the chart was
///         still open (<see cref="LiveBarNumbers" />) because the practice store
///         persists no beat map and a stored run can never be re-barred. They
///         default to null — "not recorded", never bar 0 — which is what a chart
///         with no measure markers and every run recorded by an older build both
///         report.
///     </para>
/// </summary>
public sealed record SectionResult(string Name, double StartMs, double EndMs, int SoundCount, int Hits, int Misses,
    int? StartBar = null, int? EndBar = null)
{
    /// <summary>Zero judged is 0 — see <see cref="AccuracyMath" />. Read it
    /// behind <see cref="WasPlayed" />: an unplayed section has no accuracy,
    /// and both of the numbers a display could show there are wrong.</summary>
    public double Accuracy => AccuracyMath.FromCounts(Hits, Misses);

    public bool WasPlayed => AccuracyMath.IsMeasured(Hits, Misses);

    /// <summary>No-miss clear: every sound of the section was judged and none
    /// missed. Empty sections (no sounds) don't count as cleared.</summary>
    public bool IsFullCombo => SoundCount > 0 && Misses == 0 && Hits == SoundCount;
}

/// <summary>Value snapshot of pre-session bests (records/improvement diffing).</summary>
public sealed record PracticeBests(long BestScore, double BestAccuracy, int BestStreak, double MasteryPercent)
{
    public static PracticeBests From(PracticeArrangementProfile profile) => new(
        profile.BestScore, profile.BestAccuracy, profile.BestStreak, profile.MasteryPercent);
}

/// <summary>Where a finished run's SCORE lands against the player's stored best
/// for that (song, arrangement). "Best" is ranked by score alone — the same rule
/// the local leaderboard ordering uses — so a run that scores
/// lower is NOT a new best even when its accuracy is higher. Best accuracy is
/// still tracked separately (see <see cref="RunBestComparison.ImprovedAccuracy"/>).</summary>
public enum RunBestOutcome
{
    /// <summary>The first recorded run for this song+arrangement.</summary>
    FirstRun,

    /// <summary>Beat the previous best score (strictly higher).</summary>
    NewBest,

    /// <summary>Exactly equalled the previous best score.</summary>
    MatchedBest,

    /// <summary>Scored below the previous best — the stored best stands unchanged.</summary>
    BelowBest
}

/// <summary>
///     Pure verdict on how one finished run compares to the prior stored best.
///     The single source of truth for the "is this a new best / by how much"
///     decision, so the report and the results UI agree and the rule is
///     unit-tested with no UI. Ranked by score (see <see cref="RunBestOutcome"/>);
///     a worse run therefore never counts as a new best and — because the store
///     keeps the max — never lowers what is saved.
/// </summary>
public readonly record struct RunBestComparison(
    RunBestOutcome Outcome,
    long RunScore,
    long PreviousBestScore,
    bool ImprovedAccuracy)
{
    /// <summary>Run score minus the previous best; negative below the best, zero
    /// on a tie or a first run.</summary>
    public long ScoreDelta => Outcome == RunBestOutcome.FirstRun ? 0 : RunScore - PreviousBestScore;

    public bool IsFirstRun => Outcome == RunBestOutcome.FirstRun;
    public bool IsNewBest => Outcome == RunBestOutcome.NewBest;

    /// <summary>The run did NOT raise the stored best, so a prior best stands: a
    /// strictly lower score OR an equalled score. The results screen surfaces this
    /// so a worse run reads as "your best stands", never as an overwrite.</summary>
    public bool StoredBestStands => Outcome is RunBestOutcome.BelowBest or RunBestOutcome.MatchedBest;

    /// <summary>Evaluates the verdict from raw run numbers and the pre-run best.
    /// <paramref name="hadPriorRun"/> distinguishes a genuine first run from a
    /// prior run that merely happened to score zero.
    /// <para>
    /// <paramref name="runCoverage"/> is <see cref="LivePlaySessionResult.Coverage"/>
    /// and is REQUIRED, not defaulted: <see cref="ImprovedAccuracy"/> must agree
    /// with what <c>PracticeProfileService.Record</c> actually stored, and that is
    /// gated on <c>MinCoverageForBestAccuracy</c> — a coverage-blind comparison
    /// would tell the player a 3-note run set a new best accuracy that the store
    /// then refused to keep.
    /// </para></summary>
    public static RunBestComparison Evaluate(long runScore, double runAccuracy, double runCoverage,
        PracticeBests previousBest, bool hadPriorRun)
    {
        if (!hadPriorRun)
            return new RunBestComparison(RunBestOutcome.FirstRun, runScore, 0, ImprovedAccuracy: false);

        var outcome = runScore > previousBest.BestScore ? RunBestOutcome.NewBest
            : runScore == previousBest.BestScore ? RunBestOutcome.MatchedBest
            : RunBestOutcome.BelowBest;
        return new RunBestComparison(outcome, runScore, previousBest.BestScore,
            runAccuracy > previousBest.BestAccuracy
            && runCoverage >= PracticeProfileService.MinCoverageForBestAccuracy);
    }
}

/// <summary>
///     Everything a results screen needs about one finished Live Play
///     session: the raw result, bests before and after recording (new-record
///     detection), the store identity, and the per-section breakdown.
/// </summary>
public sealed record LivePlaySessionReport(
    LivePlaySessionResult Result,
    PracticeBests Before,
    PracticeBests After,
    bool WasRecorded,
    string SongKey,
    string ArrangementKey,
    IReadOnlyList<SectionResult> Sections,
    bool HadPriorRun = false,
    LiveTimingReport? Timing = null,
    IReadOnlyList<TechniqueResult>? Techniques = null)
{
    public bool IsNewBestScore => WasRecorded && Result.Score > Before.BestScore;

    /// <summary>Gated on the same coverage floor the store applies, so the results
    /// screen never celebrates an accuracy record that was not kept.</summary>
    public bool IsNewBestAccuracy => WasRecorded && Result.Accuracy > Before.BestAccuracy
                                                 && PracticeProfileService.QualifiesForBestAccuracy(Result);

    public bool IsNewBestStreak => WasRecorded && Result.BestStreak > Before.BestStreak;

    /// <summary>
    ///     This run judged at least one sound, so <see cref="LivePlaySessionResult.Accuracy" />
    ///     means something. False is a real, reachable state — ending a run from
    ///     the pause menu during the intro, or a level with no sounds at the
    ///     played range — and <c>AccuracyMath.FromCounts(0, 0)</c> is 0, which
    ///     <c>GradeCalculator.FromAccuracy</c> maps to a confident <b>F</b>. A
    ///     readout gates on this; see <see cref="GradeLabel" />.
    /// </summary>
    public bool IsMeasured => AccuracyMath.IsMeasured(Result.Hits, Result.Misses);

    /// <summary>The grade to PRINT: the letter once something was judged, the
    /// unmeasured em dash until then. Same shape (and same reason) as
    /// <c>SongPlayStats.GradeLabel</c> and <c>SessionHistoryGroup.BestGradeLabel</c>
    /// — bind this, never a grade re-derived from <see cref="LivePlaySessionResult.Accuracy" />.
    /// </summary>
    public string GradeLabel => IsMeasured
        ? GradeCalculator.Label(GradeCalculator.FromAccuracy(Result.Accuracy))
        : AccuracyMath.UnmeasuredLabel;

    /// <summary>Timing-tier breakdown of the run (results-screen chips).
    /// May not sum to <c>Result.Hits</c> — untimed CQT hits (no attack onset)
    /// carry no tier.</summary>
    public int PerfectCount => Result.PerfectCount;
    public int GreatCount => Result.GreatCount;
    public int GoodCount => Result.GoodCount;

    /// <summary>Full combo over the whole chart (see
    /// <see cref="LivePlaySessionResult.IsFullCombo"/>) — the FC celebration.</summary>
    public bool IsFullCombo => Result.IsFullCombo;

    /// <summary>Sections cleared without a miss this run (badge chips /
    /// heatmap greens). Empty sections don't count.</summary>
    public int FullComboSectionCount
    {
        get
        {
            var count = 0;
            foreach (var s in Sections)
            {
                if (s.IsFullCombo)
                    count++;
            }

            return count;
        }
    }

    /// <summary>Score-ranked verdict of this run against the pre-run best — the
    /// pure decision the results screen renders ("new best" vs. "your best of X
    /// stands"). Uses <see cref="HadPriorRun"/> so a genuine first run is not
    /// confused with a prior run that scored zero.</summary>
    public RunBestComparison Comparison =>
        RunBestComparison.Evaluate(Result.Score, Result.Accuracy, Result.Coverage, Before, HadPriorRun);

    /// <summary>First section with the worst accuracy that was actually
    /// played — where the editor bridge drops the user.</summary>
    public SectionResult? WorstSection
    {
        get
        {
            SectionResult? worst = null;
            foreach (var s in Sections)
            {
                if (!s.WasPlayed || s.Misses == 0) continue;
                if (worst == null || s.Accuracy < worst.Accuracy) worst = s;
            }

            return worst;
        }
    }
}
