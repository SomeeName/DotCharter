using System.Windows.Input;
using DotCharter.Presentation.Dialogs;

namespace DotCharter.Presentation.Contracts.Dialogs;

/// <summary>
///     Tempo ▸ Set BPM for all measures: one tempo applied to the whole beat
///     grid as one undo step. <see cref="IDialogScreen{TResult}.Result" /> is true
///     when the dialog closed through Save.
/// </summary>
public interface ISetBpmScreen : IDialogScreen<bool>
{
    /// <summary>Seeded from beat 0 of the grid, 1–999.</summary>
    double Bpm { get; set; }

    ICommand SaveCommand { get; }

    ICommand CancelCommand { get; }
}
