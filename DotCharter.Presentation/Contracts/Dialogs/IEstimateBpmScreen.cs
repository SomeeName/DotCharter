using System.Windows.Input;
using DotCharter.Presentation.Dialogs;

namespace DotCharter.Presentation.Contracts.Dialogs;

/// <summary>
/// Tempo map ▸ Estimate BPM…: listens to the song audio (the whole song, or the part between the
///     start and end markers), shows the tempo with its half and double as alternatives, and on Apply sets
///     the chosen one from the first beat — or the beat the marked part starts on — up to the next anchor,
///     as one undo step. <see cref="IDialogScreen{TResult}.Result" /> is true once the tempo was applied.
/// </summary>
public interface IEstimateBpmScreen : IDialogScreen<bool>
{
    string SourceText { get; }

    bool IsWholeSong { get; set; }

    /// <summary>The part between the start and end markers; needs <see cref="HasMarkedPart" />.</summary>
    bool IsMarkedPart { get; set; }

    bool HasMarkedPart { get; }

    string MarkedPartText { get; }

    bool IsBusy { get; }

    string StatusText { get; }

    bool StatusIsProblem { get; }

    bool HasResult { get; }

    /// <summary>The estimate, formatted ("128 BPM").</summary>
    string BpmText { get; }

    string HalfText { get; }

    string DoubleText { get; }

    bool HasHalf { get; }

    bool HasDouble { get; }

    /// <summary>Which of the three the Apply button uses; exactly one is true.</summary>
    bool UseEstimate { get; set; }

    bool UseHalf { get; set; }

    bool UseDouble { get; set; }

    /// <summary>Where the tempo will go, e.g. "From beat 1 up to the next anchor."</summary>
    string TargetText { get; }

    ICommand EstimateCommand { get; }

    ICommand ApplyCommand { get; }

    /// <summary>Cancels a running estimate; otherwise closes the dialog unchanged.</summary>
    ICommand CancelCommand { get; }
}
