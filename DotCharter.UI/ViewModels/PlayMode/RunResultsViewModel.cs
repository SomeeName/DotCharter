using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using DotCharter.Presentation.Shared;
using DotCharter.Services.Audio.LivePlay;
using DotCharter.Services.Gameplay.Leaderboards;
using DotCharter.Services.Gameplay.Scoring;

namespace DotCharter.UI.ViewModels.PlayMode;

/// <summary>
///     The Results screen's board for one finished run: score, grade and bests,
///     the section breakdown, the technique line, the timing dashboard (rushing
///     vs dragging) and the song's leaderboard. <see cref="PlayModeViewModel" />
///     owns one and exposes it as <c>RunResults</c>, so <c>ResultsView</c> binds
///     <c>RunResults.…</c>.
///     <para>
///         Everything here is a projection of the <see cref="LivePlaySessionReport" />
///         handed to <see cref="Show" />; it reads no run state of Play Mode's.
///         The verbs that LEAVE the run (Retry, back to Song Select, the editor
///         bridges) stay on Play Mode, because they unwind the run chain it owns.
///     </para>
/// </summary>
public sealed partial class RunResultsViewModel(ILocalizationService localization, ILeaderboardService leaderboards)
    : ObservableObject
{
    [ObservableProperty] private string _resultScore = "";

    /// <summary>Raw numeric score for the results screen's count-up reveal
    /// (the view animates 0 → this; <see cref="ResultScore" /> stays the
    /// formatted source of truth).</summary>
    [ObservableProperty] private long _resultScoreValue;

    [ObservableProperty] private string _resultAccuracy = "";
    [ObservableProperty] private string _resultStreak = "";
    [ObservableProperty] private string _resultHits = "";
    [ObservableProperty] private string _resultMisses = "";
    [ObservableProperty] private string _resultGrade = "";

    /// <summary>The run was a full combo (<c>LivePlaySessionResult.IsFullCombo</c>):
    /// drives the Results FC badge (revealed after the grade stamp). Never shown
    /// for a non-FC run — no empty placeholder.</summary>
    [ObservableProperty] private bool _resultIsFullCombo;

    [ObservableProperty] private bool _isNewBestScore;
    [ObservableProperty] private bool _isNewBestAccuracy;
    [ObservableProperty] private bool _isNewBestStreak;
    [ObservableProperty] private string _resultImprovement = "";

    /// <summary>Plain-language verdict of this run against the stored best — the
    /// results screen shows it so a worse run reads as "your best of X stands",
    /// never as an overwrite. Empty when nothing was recorded.</summary>
    [ObservableProperty] private string _resultBestSummary = "";

    /// <summary>The techniques played without their technique (<see cref="ResultsTechniqueLine" />); empty hides it.</summary>
    [ObservableProperty] private string _resultTechniqueLine = "";
    [ObservableProperty] private bool _hasWorstSection;

    public ObservableCollection<SectionRow> ResultSections { get; } = [];
    public ObservableCollection<LeaderboardEntry> ResultLeaderboard { get; } = [];

    /// <summary>Which note detector's runs <see cref="ResultLeaderboard" /> ranks
    /// (<see cref="ResultsBoardCaption" />).</summary>
    [ObservableProperty] private string _resultLeaderboardCaption = "";

    // ---- Timing dashboard (rushing vs dragging) -------------------------------

    /// <summary>False when the run produced no timed hits — e.g. every hit was
    /// CQT pull-verified, which is deliberately untimed. The panel hides rather
    /// than showing a flat, meaningless histogram.</summary>
    [ObservableProperty] private bool _hasTiming;

    [ObservableProperty] private string _timingHeadline = "";
    [ObservableProperty] private string _timingMean = "";
    [ObservableProperty] private string _timingSpread = "";
    [ObservableProperty] private string _timingEarly = "";
    [ObservableProperty] private string _timingOnTime = "";
    [ObservableProperty] private string _timingLate = "";

    public ObservableCollection<TimingBarRow> TimingBars { get; } = [];
    public ObservableCollection<TimingSectionRow> TimingSections { get; } = [];

    /// <summary>Fills the board from a finished run's report.</summary>
    public void Show(LivePlaySessionReport report)
    {
        var result = report.Result;
        ResultScore = result.Score.ToString("N0");
        ResultScoreValue = result.Score;
        // Nothing judged is an em dash, not "0.0%" and an F. It is reachable —
        // ending a run from the pause menu during the intro judges nothing — and
        // AccuracyMath.FromCounts(0, 0) is 0, which grades F with total
        // confidence. The verdict lives on the report (report.IsMeasured /
        // report.GradeLabel), the same way SongPlayStats and SessionHistoryGroup
        // carry theirs, so no readout re-derives it.
        ResultAccuracy = report.IsMeasured
            ? PercentText.Format(result.Accuracy, 1)
            : AccuracyMath.UnmeasuredLabel;
        ResultStreak = result.BestStreak.ToString();
        ResultHits = result.Hits.ToString();
        ResultMisses = result.Misses.ToString();
        ResultGrade = report.GradeLabel;
        ResultIsFullCombo = report.IsFullCombo;
        IsNewBestScore = report.IsNewBestScore;
        IsNewBestAccuracy = report.IsNewBestAccuracy;
        IsNewBestStreak = report.IsNewBestStreak;
        ResultImprovement = report.Before.BestScore > 0
            ? localization.Format("PlayMode_Results_VsBest",
                (result.Score - report.Before.BestScore).ToString("+#,0;-#,0;±0"))
            : "";
        ResultBestSummary = BuildBestSummary(report);
        ResultTechniqueLine = ResultsTechniqueLine.For(report.Techniques, localization);
        HasWorstSection = report.WorstSection != null;
        BuildTimingDashboard(report.Timing);

        ResultSections.Clear();
        foreach (var s in report.Sections)
        {
            ResultSections.Add(new SectionRow(
                s.Name,
                TimeSpan.FromMilliseconds(s.StartMs).ToString(@"m\:ss"),
                s.WasPlayed ? PercentText.Format(s.Accuracy, 0) : "—",
                s.Misses, s.WasPlayed, s.StartMs,
                s.StartBar is { } from
                    ? localization.Format("PlayMode_Progress_BarRange", from, s.EndBar ?? from)
                    : "")
            { Accuracy = s.Accuracy, Localization = localization });
        }

        ResultLeaderboardCaption = ResultsBoardCaption.For(result.Detector, localization);
        ResultLeaderboard.Clear();
        foreach (var row in leaderboards.GetSongLeaderboard(report.SongKey, report.ArrangementKey, result.Detector))
            ResultLeaderboard.Add(row);
    }

    /// <summary>
    ///     Renders the run's timing analysis. Sign convention throughout is the
    ///     report's: NEGATIVE is early (rushing), POSITIVE is late (dragging).
    /// </summary>
    private void BuildTimingDashboard(LiveTimingReport? timing)
    {
        TimingBars.Clear();
        TimingSections.Clear();

        if (timing is null || timing.HitCount == 0)
        {
            HasTiming = false;
            return;
        }

        HasTiming = true;
        var loc = localization;

        // Lead with the verdict, not the number — "you are rushing" is the thing a
        // player acts on; the milliseconds are supporting evidence.
        TimingHeadline = timing.LeansEarly
            ? loc.Format("PlayMode_Results_Timing_Rushing", Math.Abs(timing.MeanMs).ToString("0"))
            : timing.LeansLate
                ? loc.Format("PlayMode_Results_Timing_Dragging", timing.MeanMs.ToString("0"))
                : loc["PlayMode_Results_Timing_OnTime"];

        TimingMean = $"{timing.MeanMs:+0;-0;0} ms";
        TimingSpread = $"±{timing.StdDevMs:0} ms";
        TimingEarly = timing.EarlyCount.ToString();
        TimingOnTime = timing.OnTimeCount.ToString();
        TimingLate = timing.LateCount.ToString();

        var heights = TimingHistogramMath.BarHeights(timing.Histogram);
        for (var i = 0; i < heights.Count; i++)
        {
            TimingBars.Add(new TimingBarRow(
                heights[i],
                TimingHistogramMath.IsCentreBin(i, heights.Count),
                TimingHistogramMath.IsEarlyBin(i, heights.Count)));
        }

        // Worst-biased sections first — that is the practice target.
        foreach (var section in timing.Sections
                     .Where(s => s.HitCount > 0)
                     .OrderByDescending(s => Math.Abs(s.MeanMs))
                     .Take(6))
        {
            TimingSections.Add(new TimingSectionRow(
                section.Name,
                $"{section.MeanMs:+0;-0;0} ms",
                loc.Plural("PlayMode_Results_Timing_SectionHitsOne", "PlayMode_Results_Timing_SectionHits",
                    section.HitCount),
                section.MeanMs < 0));
        }
    }

    /// <summary>Localizes the pure <see cref="RunBestComparison"/> verdict for the
    /// results screen. The DECISION lives in the comparison (unit-tested); this
    /// only formats it. Empty for a session that recorded nothing.</summary>
    private string BuildBestSummary(LivePlaySessionReport report)
    {
        if (!report.WasRecorded) return "";

        var loc = localization;
        var c = report.Comparison;
        return c.Outcome switch
        {
            RunBestOutcome.FirstRun =>
                loc.Format("PlayMode_Results_FirstRun", c.RunScore.ToString("N0")),
            RunBestOutcome.NewBest =>
                loc.Format("PlayMode_Results_NewBest",
                    c.ScoreDelta.ToString("+#,0;-#,0;±0"), c.PreviousBestScore.ToString("N0")),
            RunBestOutcome.MatchedBest =>
                loc.Format("PlayMode_Results_MatchedBest", c.PreviousBestScore.ToString("N0")),
            _ =>
                loc.Format("PlayMode_Results_BelowBest",
                    c.PreviousBestScore.ToString("N0"), c.RunScore.ToString("N0"),
                    c.ScoreDelta.ToString("+#,0;-#,0;±0"))
        };
    }
}
