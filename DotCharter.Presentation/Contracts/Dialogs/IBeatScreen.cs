using System.Windows.Input;
using DotCharter.Core.Domain.Events;
using DotCharter.Presentation.Dialogs;

namespace DotCharter.Presentation.Contracts.Dialogs;

/// <summary>Opens <see cref="IBeatScreen" /> on <paramref name="Beat" />, the grid's beat at <paramref name="Index" />.</summary>
public sealed record BeatRequest(BeatMarker Beat, int Index) : IDialogRequest<BeatRequest, IBeatScreen, bool>;

/// <summary>
///     One beat of the grid: its tempo, its time signature (which runs on to
///     the next signature change) and whether it is a tempo anchor.
///     <see cref="IDialogScreen{TResult}.Result" /> is true once saved.
/// </summary>
public interface IBeatScreen : IDialogScreen<bool>
{
    /// <summary>1–999.</summary>
    double Bpm { get; set; }

    int BeatsInMeasure { get; set; }

    int NoteDenominator { get; set; }

    bool IsAnchor { get; set; }

    ICommand SaveCommand { get; }

    ICommand CancelCommand { get; }
}
