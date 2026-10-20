using System.ComponentModel;
using System.Windows.Input;

namespace DotCharter.Presentation.Contracts.PlayMode;

/// <summary>
///     The redesigned results screen: a grade hero, a compact stats grid, and
///     the things you can do next.
///     <para>
///         Everything under the hero is a SECOND read: the section breakdown,
///         the timing histogram and the local leaderboard. Each one has its own
///         <c>Has*</c> gate because a chart without sections, a run without
///         timing samples and a machine with one profile are all ordinary, and
///         an empty panel that keeps its slot and says why is the house rule.
///     </para>
/// </summary>
public interface IResultsScreen : INotifyPropertyChanged
{
    string SongTitle { get; }

    string ArrangementName { get; }

    string TuningLabel { get; }

    /// <summary>The letter grade — the hero.</summary>
    string GradeLabel { get; }

    string ScoreLabel { get; }

    string AccuracyLabel { get; }

    string StreakLabel { get; }

    string HitsLabel { get; }

    string MissesLabel { get; }

    bool IsFullCombo { get; }

    /// <summary>True when this run beat the stored best.</summary>
    bool IsNewRecord { get; }

    /// <summary>The new-best or vs.-best line, already formatted.</summary>
    string ComparisonLabel { get; }

    /// <summary>
    /// The technique line: the notes hit WITHOUT their charted technique, already
    ///     formatted — "Played without the technique: bends 3/12 · palm mutes 1/40".
    /// </summary>
    string TechniqueLabel { get; }

    /// <summary>False when every technique was played (or none was judged): the line is hidden.</summary>
    bool HasTechniqueLine { get; }

    /// <summary>One row per chart section.</summary>
    IReadOnlyList<IResultSection> Sections { get; }

    /// <summary>False on a chart with no sections — the panel then says so.</summary>
    bool HasSections { get; }

    /// <summary>False when the run produced no timing samples.</summary>
    bool HasTiming { get; }

    /// <summary>"Rushing" / "Dragging" / "On time" — the one-word verdict.</summary>
    string TimingHeadline { get; }

    string TimingMeanLabel { get; }

    string TimingSpreadLabel { get; }

    string TimingEarlyLabel { get; }

    string TimingLateLabel { get; }

    string TimingOnTimeLabel { get; }

    /// <summary>The histogram, left (early) to right (late).</summary>
    IReadOnlyList<ITimingBar> TimingBars { get; }

    /// <summary>This machine's other profiles on this song.</summary>
    IReadOnlyList<ILeaderboardRow> Leaderboard { get; }

    /// <summary>False with one profile on the machine — the panel says so.</summary>
    bool HasLeaderboard { get; }

    ICommand RetryCommand { get; }

    /// <summary>Into the riff repeater on the weakest section.</summary>
    ICommand PracticeCommand { get; }

    /// <summary>False when there is no weak section to practise.</summary>
    bool CanPractice { get; }

    ICommand SongSelectCommand { get; }

    ICommand OpenInEditorCommand { get; }

    /// <summary>Plays the recorded take back.</summary>
    ICommand ListenBackCommand { get; }

    bool CanListenBack { get; }

    bool IsTakePlaying { get; }

    /// <summary>The running setlist: "Next: &lt;song&gt;" while one follows, done on its last song.</summary>
    IResultsSetlist Setlist { get; }
}

/// <summary>One section of the chart, as the breakdown draws it.</summary>
public interface IResultSection : INotifyPropertyChanged
{
    string Name { get; }

    /// <summary>"1:12", where it starts.</summary>
    string TimeLabel { get; }

    string AccuracyLabel { get; }

    /// <summary>The bar, 0..1 — a <c>ProgressBar</c>'s value, never a pixel width.</summary>
    double Accuracy { get; }

    /// <summary>Under 75 %.</summary>
    bool IsWeak { get; }

    /// <summary>75 to 89 %.</summary>
    bool IsFair { get; }

    /// <summary>90 % and over.</summary>
    bool IsStrong { get; }
}

/// <summary>One column of the timing histogram.</summary>
public interface ITimingBar
{
    /// <summary>
    ///     The column's drawn height in DIPs inside the 96 px track the results
    ///     screen gives it. A histogram cannot be a <c>ProgressBar</c> and a
    ///     star-sized Grid row cannot be bound, so this is a measurement the
    ///     screen and the fake agree on rather than a fraction.
    /// </summary>
    double BarHeight { get; }

    /// <summary>True for the on-time column — the only tinted one.</summary>
    bool IsCentre { get; }
}

/// <summary>One profile's best on this song, on this machine.</summary>
public interface ILeaderboardRow : INotifyPropertyChanged
{
    /// <summary>"1", "2", … already formatted.</summary>
    string RankLabel { get; }

    string ProfileName { get; }

    string ScoreLabel { get; }

    string AccuracyLabel { get; }

    /// <summary>True for the row that is you.</summary>
    bool IsActiveProfile { get; }
}
