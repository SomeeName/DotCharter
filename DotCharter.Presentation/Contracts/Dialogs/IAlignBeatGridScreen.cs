using System.Windows.Input;
using DotCharter.Presentation.Dialogs;

namespace DotCharter.Presentation.Contracts.Dialogs;

/// <summary>
/// Tempo map ▸ Align beat grid to notes: one offset in ms moves every
///     beat while every timed element keeps its time, as one undo step. The offset
///     is pre-filled from the notes, and the card shows how far they sit from the
///     grid now and after the move. <see cref="IDialogScreen{TResult}.Result" /> is
///     true once the grid moved.
/// </summary>
public interface IAlignBeatGridScreen : IDialogScreen<bool>
{
    /// <summary>Positive moves the beats later; −1000 … 1000.</summary>
    double OffsetMs { get; set; }

    /// <summary>Median distance of the notes to the nearest 1/4- or 1/3-beat line today, formatted.</summary>
    string OffNowText { get; }

    /// <summary>The same median once the grid has moved by <see cref="OffsetMs" />; follows the input.</summary>
    string OffAfterText { get; }

    /// <summary>Where the suggestion came from, or why there is none.</summary>
    string EstimateText { get; }

    /// <summary>False when the chart has too few notes to measure; the offset is then typed by hand.</summary>
    bool HasEstimate { get; }

    ICommand ApplyCommand { get; }

    ICommand CancelCommand { get; }
}
