using System.Windows.Input;
using DotCharter.Presentation.Dialogs;

namespace DotCharter.Presentation.Contracts.Dialogs;

/// <summary>
/// Tools ▸ Chart quality report: every existing check in one list — the arrangement
///     rules, the export gate and, on request, the audio-verified lint — over the verdict model
/// (<c>ChartQualityReporter</c>), run off the UI thread (<see cref="IsChecking" />).
///     "Ready to ship" means nothing would stop the export;
///     the findings that remain are grouped by severity, and each can jump to its place in the
///     chart and run its Fix where one exists.
///     <see cref="IDialogScreen{TResult}.Result" /> is true when a fix or a preparation changed the chart.
/// </summary>
public interface IChartQualityReportScreen : IDialogScreen<bool>
{
    /// <summary>"Ready to ship", or what stops it: "2 things stop the export".</summary>
    string Verdict { get; }

    bool IsReady { get; }

    /// <summary>
    /// The checks are running, off the UI thread: the dialog opens at once and a busy row
    ///     shows while they run. On a recheck the previous report stays on screen, but its verbs
    ///     wait for the new one.
    /// </summary>
    bool IsChecking { get; }

    /// <summary>A check has finished, or there is no chart to check: the verdict and what it found show.
    /// False only while the first check runs.</summary>
    bool HasVerdict { get; }

    /// <summary>"2 errors · 4 important · 11 warnings".</summary>
    string Counts { get; }

    IReadOnlyList<IQualityArrangementSummary> Arrangements { get; }

    /// <summary>The findings by severity, most severe first; an empty severity has no group.</summary>
    IReadOnlyList<IQualityFindingGroup> Groups { get; }

    bool HasFindings { get; }

    /// <summary>
    ///     Also list the findings on easier difficulty levels. Off by default: on a chart with a
    ///     difficulty ladder (every Rocksmith import) they are most of the list and repeat the
    ///     hardest level's. A finding that stops the export always shows.
    /// </summary>
    bool ShowEasierLevels { get; set; }

    /// <summary>There are non-blocking findings on easier levels, so the toggle has something to do.</summary>
    bool HasEasierLevelFindings { get; }

    /// <summary>"94 on easier levels"; how many the toggle hides or shows.</summary>
    string EasierLevelsNote { get; }

    /// <summary>The audio-verified lint is running (it decodes the song, so it is on request).</summary>
    bool IsBusy { get; }

    /// <summary>"Not checked against the audio yet", "312 of 318 sounds confirmed", or why it could not run.</summary>
    string AudioStatus { get; }

    bool CanVerifyAudio { get; }

    ICommand VerifyAudioCommand { get; }

    ICommand RecheckCommand { get; }


    /// <summary>
    /// What an Editor on Fire export of this chart would approximate or drop, grouped
    ///     by what it is. It never counts toward the verdict. It opens closed, and is not computed
    /// while closed, unless the user exports this chart to Editor on Fire.
    /// </summary>
    IEofRoundTripSection EofRoundTrip { get; }

    /// <summary>
    ///     Opened from a lossy export's "Show" on the status rail: the round trip expands and the
    ///     view brings it into view once it has laid out.
    /// </summary>
    bool FocusEofRoundTrip { get; set; }

    ICommand CloseCommand { get; }
}

/// <summary>
/// The Editor on Fire round trip: one group per kind of loss, each naming its places;
///     the first few of each are listed, and Show all lists every one.
/// </summary>
public interface IEofRoundTripSection : System.ComponentModel.INotifyPropertyChanged
{
    /// <summary>
    ///     Open. The round trip is computed — off the UI thread — the first time the section opens,
    ///     and again after each recheck while it is open; nothing is computed while it is closed.
    /// </summary>
    bool IsExpanded { get; set; }

    /// <summary>The round trip is being computed; the groups below are empty while it is.</summary>
    bool IsBusy { get; }

    /// <summary>"Nothing is lost", "2 dropped · 9 approximated", or why it could not be checked.</summary>
    string Summary { get; }

    bool HasGroups { get; }

    IReadOnlyList<IEofLossGroup> Groups { get; }

    /// <summary>List every place instead of the first few of each kind.</summary>
    bool ShowAll { get; set; }

    /// <summary>A group lists fewer places than it has, so Show all has something to do.</summary>
    bool CanShowAll { get; }

    /// <summary>Opens every place, of every kind, as plain text in the text-document dialog, which copies it.</summary>
    ICommand OpenListCommand { get; }
}

/// <summary>Every place one kind of loss happened.</summary>
public interface IEofLossGroup
{
    /// <summary>What was lost: "Tone never used by a tone change".</summary>
    string Title { get; }

    /// <summary>"Dropped (2)" or "Approximated (9)".</summary>
    string Count { get; }

    /// <summary>Left out of the file, not merely approximated.</summary>
    bool IsDropped { get; }

    IReadOnlyList<IEofLossPlace> Items { get; }

    /// <summary>"4 more"; empty when every place is listed.</summary>
    string MoreNote { get; }

    bool HasMore { get; }
}

/// <summary>One place: where it happened, and the value that names the item.</summary>
public interface IEofLossPlace
{
    /// <summary>The arrangement, or "Beat map" / "Vocals"; it trims before <see cref="Where" /> does.</summary>
    string Subject { get; }

    /// <summary>"M12.3 · 00:41.25"; empty for a loss that is about the whole arrangement.</summary>
    string Where { get; }

    /// <summary>The phrase, tone, lyric or value: "clean", "1.3 → 1.25"; may be empty.</summary>
    string Detail { get; }

    bool HasTime { get; }

    /// <summary>Seeks the editor behind the dialog, switching arrangement first.</summary>
    ICommand JumpCommand { get; }
}

/// <summary>One arrangement's line: its estimated difficulty and what the report found in it.</summary>
public interface IQualityArrangementSummary
{
    string Name { get; }

    /// <summary>"Difficulty 6.4".</summary>
    string Difficulty { get; }

    /// <summary>"1 error · 3 warnings", or "Clean".</summary>
    string Counts { get; }

    bool IsClean { get; }

    bool HasErrors { get; }
}

/// <summary>The findings of one severity.</summary>
public interface IQualityFindingGroup
{
    /// <summary>"Stops the export (2)".</summary>
    string Title { get; }

    /// <summary>The export refuses the chart while any of these remain.</summary>
    bool IsBlocking { get; }

    bool IsImportant { get; }

    IReadOnlyList<IQualityFinding> Items { get; }
}

/// <summary>One finding, in the wording of the check that reported it.</summary>
public interface IQualityFinding
{
    /// <summary>"Lead · level 12 · 1:23.456".</summary>
    string Location { get; }

    string Message { get; }

    /// <summary>The check(s) that reported it: "Rules", "Export", "Rules · Export", "Audio".</summary>
    string Source { get; }

    bool HasTime { get; }

    /// <summary>Seeks the editor behind the dialog, switching arrangement and level first.</summary>
    ICommand JumpCommand { get; }

    bool HasFix { get; }

    /// <summary>What Fix will do, for its tooltip.</summary>
    string FixDescription { get; }

    /// <summary>Runs the fix as ONE undo step, then the report runs again.</summary>
    ICommand FixCommand { get; }
}
