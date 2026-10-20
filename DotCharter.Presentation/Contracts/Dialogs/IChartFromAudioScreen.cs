using System.Windows.Input;
using DotCharter.Presentation.Dialogs;

namespace DotCharter.Presentation.Contracts.Dialogs;

/// <summary>
///     File ▸ Chart from audio: pick a WAV (ideally the isolated guitar stem),
///     tune the sensitivity, analyse it against the current beat grid, then
///     accept the result as a NEW draft arrangement — nothing existing is
///     touched, so rejecting a draft is deleting (or undoing) it — or hold it
///     for a merge into the current arrangement, decided phrase by phrase on
/// the merge card over the timeline.
///     <see cref="IDialogScreen{TResult}.Result" /> is true once a draft was applied or held.
/// </summary>
public interface IChartFromAudioScreen : IDialogScreen<bool>
{
    /// <summary>The WAV to analyse. It belongs to the project it was chosen for and is forgotten on another.</summary>
    string AudioPath { get; set; }

    /// <summary>0–100; higher keeps more, lower-confidence, notes.</summary>
    double Sensitivity { get; set; }

    /// <summary>True while an analysis runs. Cancel still closes at once: the analysis writes nothing.</summary>
    bool IsBusy { get; }

    /// <summary>Why nothing is drafted, or that the analysis is running; empty otherwise.</summary>
    string Status { get; }

    /// <summary>A draft with at least one note is in hand, so Apply is enabled.</summary>
    bool HasDraft { get; }

    /// <summary>"N notes, M % average confidence"; empty without a draft.</summary>
    string DraftSummary { get; }

    /// <summary>
    ///     The greyed Apply's own reason: true exactly when there is no draft and
    ///     neither the busy panel nor the status strip is already speaking.
    /// </summary>
    bool NothingToApply { get; }

    /// <summary>
    ///     True when the analysis would hear the full mix because no separated stem of
    /// the arrangement's instrument exists for the chosen file, or while a
    ///     separation of it runs. The strip carries <see cref="StemHint" /> and
    ///     the Separate stems button; it goes once a stem is found.
    /// </summary>
    bool HasStemHint { get; }

    /// <summary>The strip's line: that the full mix is analysed, or how the last separation ended.</summary>
    string StemHint { get; }

    /// <summary>Separate stems can start: the chosen file is the song's audio and a model is there or offered.</summary>
    bool CanSeparateStems { get; }

    /// <summary>Why Separate stems is greyed; empty when it is live or a run is in flight.</summary>
    string SeparateStemsBlockedReason { get; }

    /// <summary>
    ///     True while a separation of the chosen file runs — the one started here, or one
    ///     already running when the dialog opened, which it follows instead of starting a
    /// second. Cancel separation replaces the button.
    /// </summary>
    bool IsSeparating { get; }

    /// <summary>0–1, the running separation's progress.</summary>
    double SeparationProgress { get; }

    /// <summary>
    ///     While <see cref="IsSeparating" />: closing the dialog does not stop the
    ///     separation, which runs on in the status bar and saves its stems to the cache.
    ///     Empty otherwise.
    /// </summary>
    string SeparationKeepsRunningNote { get; }

    /// <summary>The current arrangement has a level to merge into, so the merge target is offered.</summary>
    bool CanMerge { get; }

    /// <summary>The merge target's label, naming the current arrangement ("Merge into “Lead”").</summary>
    string MergeTargetText { get; }

    /// <summary>
    ///     Apply holds the draft for a phrase-by-phrase merge into the current arrangement
    ///     instead of adding a new one. Always false while <see cref="CanMerge" /> is.
    /// </summary>
    bool MergeIntoCurrent { get; set; }

    /// <summary>The other target, for the radio pair: the negation of <see cref="MergeIntoCurrent" />.</summary>
    bool AddAsNewArrangement { get; set; }

    /// <summary>
    ///     The merge also offers phrases that already have notes. Off by default: the
    ///     draft's measured note F1 is about 12 %, so by default it only fills empty phrases.
    /// </summary>
    bool ReplaceExisting { get; set; }

    /// <summary>The primary button's label for the chosen target.</summary>
    string ApplyText { get; }

    ICommand BrowseAudioCommand { get; }

    ICommand AnalyzeCommand { get; }

    ICommand ApplyCommand { get; }

    ICommand CancelCommand { get; }

    /// <summary>Runs the app's own stem separation (Tools ▸ Separate Stems) on the song's audio, then looks for the stem again.</summary>
    ICommand SeparateStemsCommand { get; }

    /// <summary>Cancels the separation shown here. The dialog's own Cancel closes it and leaves a run going.</summary>
    ICommand CancelSeparationCommand { get; }
}
