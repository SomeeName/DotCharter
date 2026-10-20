using System.ComponentModel;
using System.Windows.Input;
using DotCharter.Presentation.Dialogs;

namespace DotCharter.Presentation.Contracts.Dialogs;

/// <summary>
/// Tempo map ▸ Align to recording: warps the chart's beat grid onto its recording. Bars,
///     time signatures and every note stay as written; only the beat times move, so every
///     arrangement and the vocals follow. The charter picks the range and whether hand-placed
///     (anchored) beats stay, runs the analysis (off the UI thread, cancellable), then sees a
///     confidence per section and unticks any section to leave it stretched between its aligned
///     neighbours. Apply is ONE undo step. <see cref="IDialogScreen{TResult}.Result" /> is true once applied.
/// </summary>
public interface IAlignToRecordingScreen : IDialogScreen<bool>
{
    /// <summary>The recording the run listens to, or why there is none.</summary>
    string SourceText { get; }

    /// <summary>The three range choices are one radio group: setting one true clears the other two.</summary>
    bool IsWholeSong { get; set; }

    bool IsFromBar { get; set; }

    bool IsLoopRegion { get; set; }

    /// <summary>False without a loop region; the choice is then shown disabled.</summary>
    bool HasLoopRegion { get; }

    /// <summary>The first bar a "from bar" run may move; 1 … <see cref="BarCount" />.</summary>
    double FromBar { get; set; }

    double BarCount { get; }

    /// <summary>The loop region as bars, for the choice's label.</summary>
    string LoopRegionText { get; }

    /// <summary>Keep beats placed by hand where they are; the rest aligns around them.</summary>
    bool KeepAnchors { get; set; }

    /// <summary>How many anchored beats the chart has, formatted.</summary>
    string AnchorsText { get; }

    bool IsBusy { get; }

    /// <summary>What the run is doing, why it cannot run, or what it found.</summary>
    string StatusText { get; }

    /// <summary>The status is a problem (no audio, locked map, failed decode), not progress.</summary>
    bool StatusIsProblem { get; }

    bool HasResult { get; }

    IReadOnlyList<IAlignSectionRow> Sections { get; }

    /// <summary>How many sections will move and what was listened to.</summary>
    string SummaryText { get; }

    ICommand AnalyzeCommand { get; }

    /// <summary>Writes the ticked sections; enabled once there is a result with a section ticked.</summary>
    ICommand ApplyCommand { get; }

    /// <summary>Cancels a run in flight; otherwise closes without changing anything.</summary>
    ICommand CancelCommand { get; }
}

/// <summary>One section of the chart after the analysis.</summary>
public interface IAlignSectionRow : INotifyPropertyChanged
{
    /// <summary>The section's name and bars, e.g. "Chorus · bars 9–16".</summary>
    string Title { get; }

    /// <summary>0..1, for the meter; 0 when the section has no notes.</summary>
    double Confidence { get; }

    /// <summary>The confidence as a percentage, or "no notes".</summary>
    string ConfidenceText { get; }

    /// <summary>Below the threshold: offered unticked.</summary>
    bool IsLow { get; }

    /// <summary>How far its beats move on average, formatted.</summary>
    string ShiftText { get; }

    /// <summary>Whether this section's alignment is applied; a skipped one is stretched between its neighbours.</summary>
    bool IsApplied { get; set; }

    /// <summary>False when the section has no beat in the range (nothing to apply).</summary>
    bool CanApply { get; }
}
