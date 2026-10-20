using System.Windows.Input;
using DotCharter.Presentation.Dialogs;

namespace DotCharter.Presentation.Contracts.Dialogs;

/// <summary>The single-number tempo-map verbs this one dialog serves.</summary>
public enum TempoMapOperation
{
    DefaultStartSilence,
    AddSilenceAtStart,
    AddSilenceAtEnd,
    AddMeasuresAtStart,
    AddMeasuresAtEnd
}

/// <summary>Opens <see cref="ITempoMapOperationScreen" /> for one <see cref="TempoMapOperation" />.</summary>
public sealed record TempoMapOperationRequest(TempoMapOperation Operation)
    : IDialogRequest<TempoMapOperationRequest, ITempoMapOperationScreen, bool>;

/// <summary>
///     One card for every single-number tempo-map operation: the title,
///     subtitle, label, range and format all come from the operation.
///     <see cref="IDialogScreen{TResult}.Result" /> is true once the operation ran.
/// </summary>
public interface ITempoMapOperationScreen : IDialogScreen<bool>
{
    string Title { get; }

    string Subtitle { get; }

    /// <summary>The row label, which is also the input's accessible name.</summary>
    string Label { get; }

    double Value { get; set; }

    double Minimum { get; }

    double Maximum { get; }

    /// <summary>The input's number format: <c>0.###</c> for seconds, <c>0</c> for a measure count.</summary>
    string FormatString { get; }

    ICommand ApplyCommand { get; }

    ICommand CancelCommand { get; }
}
