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
    AddMeasuresAtEnd,

    /// <summary>Not a Start &amp; end verb: it works at the playhead, so the dialog opens on it alone.</summary>
    InsertMeasuresAtPlayhead
}

/// <summary>One entry of the dialog's operation list.</summary>
public sealed record TempoMapOperationChoice(TempoMapOperation Operation, string Label)
{
    /// <summary>The list item's accessible name.</summary>
    public override string ToString() => Label;
}

/// <summary>Opens <see cref="ITempoMapOperationScreen" /> for one <see cref="TempoMapOperation" />.</summary>
public sealed record TempoMapOperationRequest(TempoMapOperation Operation)
    : IDialogRequest<TempoMapOperationRequest, ITempoMapOperationScreen, bool>;

/// <summary>
///     One card for the five Start &amp; end operations: the choice picks the
///     subtitle, label, range and format; the title is fixed. Opened for
///     <see cref="TempoMapOperation.InsertMeasuresAtPlayhead" /> it serves that verb alone,
///     under its own title, with the choice hidden.
///     <see cref="IDialogScreen{TResult}.Result" /> is true once the operation ran.
/// </summary>
public interface ITempoMapOperationScreen : IDialogScreen<bool>
{
    string Title { get; }

    /// <summary>The caption of the operation choice, also its accessible name.</summary>
    string OperationLabel { get; }

    IReadOnlyList<TempoMapOperationChoice> Choices { get; }

    /// <summary>False when the dialog serves one verb only, so there is nothing to choose.</summary>
    bool ShowsChoices { get; }

    /// <summary>Picking another entry re-fills <see cref="Subtitle" />, <see cref="Label" />, the range and <see cref="Value" />.</summary>
    TempoMapOperationChoice? SelectedChoice { get; set; }

    string Subtitle { get; }

    /// <summary>The row label, which is also the input's accessible name.</summary>
    string Label { get; }

    double Value { get; set; }

    double Minimum { get; }

    double Maximum { get; }

    /// <summary>The input's number format: <c>0.###</c> for seconds, <c>0</c> for a measure count.</summary>
    string FormatString { get; }

    /// <summary>True for Insert measures at playhead: the time signature and tempo rows show.</summary>
    bool ShowsMeasureTempo { get; }

    /// <summary>The inserted bars' beats per measure (the top of the time signature).</summary>
    double BeatsPerMeasure { get; set; }

    /// <summary>The beat units to pick from (the bottom of the time signature).</summary>
    IReadOnlyList<int> BeatUnits { get; }

    int BeatUnit { get; set; }

    /// <summary>The inserted bars' tempo in quarter notes a minute.</summary>
    double Bpm { get; set; }

    ICommand ApplyCommand { get; }

    ICommand CancelCommand { get; }
}
