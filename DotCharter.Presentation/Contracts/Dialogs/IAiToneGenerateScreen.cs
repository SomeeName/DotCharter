using System.Windows.Input;
using DotCharter.Presentation.Dialogs;

namespace DotCharter.Presentation.Contracts.Dialogs;

/// <summary>
/// Tools ▸ Generate tones with AI…: the charter's own words for the tones before the
///     run starts. <see cref="IDialogScreen{TResult}.Result" /> is the cleaned, capped text
///     (empty when the box was left empty) when the dialog closed through Generate, and
///     <c>null</c> when it was cancelled or closed from outside.
/// </summary>
public interface IAiToneGenerateScreen : IDialogScreen<string?>
{
    /// <summary>What the charter typed; remembered for this project between opens.</summary>
    string Notes { get; set; }

    /// <summary>The longest text the run keeps (<c>AiToneContextText.MaxLength</c>).</summary>
    int MaxLength { get; }

    /// <summary>"12 / 500": how much of the cap is used.</summary>
    string LengthText { get; }

    ICommand GenerateCommand { get; }

    ICommand CancelCommand { get; }
}
