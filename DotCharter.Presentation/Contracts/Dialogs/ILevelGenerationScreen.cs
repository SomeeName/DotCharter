using System.Windows.Input;
using DotCharter.Presentation.Dialogs;

namespace DotCharter.Presentation.Contracts.Dialogs;

/// <summary>
///     Tools ▸ Generate difficulty levels: expands the active arrangement's
///     hardest level into N thinner ones. The dialog stays open for the run and
///     closes when the work is done; <see cref="IDialogScreen{TResult}.Result" />
///     is true when a run finished.
/// </summary>
public interface ILevelGenerationScreen : IDialogScreen<bool>
{
    /// <summary>Names the arrangement and how many levels it has now, which a run replaces.</summary>
    string Subtitle { get; }

    double LevelCount { get; set; }

    double Minimum { get; }

    double Maximum { get; }

    /// <summary>True for the whole run. There is no cancel: generation is one transaction.</summary>
    bool IsBusy { get; }

    /// <summary>0–1 over the requested levels.</summary>
    double Progress { get; }

    /// <summary>"Level 3 of 8"; empty when idle.</summary>
    string ProgressLabel { get; }

    ICommand ApplyCommand { get; }

    ICommand CancelCommand { get; }
}
