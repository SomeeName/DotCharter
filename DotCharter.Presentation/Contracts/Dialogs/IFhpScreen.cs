using System.Windows.Input;
using DotCharter.Core.Domain.Events;
using DotCharter.Presentation.Dialogs;

namespace DotCharter.Presentation.Contracts.Dialogs;

/// <summary>
///     Opens <see cref="IFhpScreen" /> at <paramref name="TimeMs" /> (the playhead
///     when negative), editing <paramref name="Anchor" /> or the anchor already on
///     that grid position, else creating one.
/// </summary>
public sealed record FhpRequest(double TimeMs = -1, FretAnchor? Anchor = null)
    : IDialogRequest<FhpRequest, IFhpScreen, bool>;

/// <summary>
///     The fret-hand position (anchor) at one grid position: its fret and its
///     width. <see cref="IDialogScreen{TResult}.Result" /> is true once saved.
/// </summary>
public interface IFhpScreen : IDialogScreen<bool>
{
    /// <summary>1–24.</summary>
    int Fret { get; set; }

    /// <summary>1–24 frets.</summary>
    int Width { get; set; }

    ICommand SaveCommand { get; }

    ICommand CancelCommand { get; }
}
